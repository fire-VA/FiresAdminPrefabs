using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using FiresCore.Pieces;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Managers;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Utilities
{
    // Disk cache of blueprint placement ghosts. Built piece by piece a ghost is one GameObject per piece,
    // 100k for a large blueprint, so each blueprint is baked once into one combined mesh per material and
    // stored as a .bpghost file stamped with the blueprint's source hash and piece count.
    //
    // Nothing is loaded up front. When blueprints register, a background task hashes each one and reads
    // only its cache file's header; missing or stale caches are then baked. A ghost's meshes are read from
    // disk only when the player picks that blueprint.
    public static class VAFapBlueprintGhostCache
    {
        public const string CacheDirName = "GhostMeshCache";

        // Version 2 bakes skip wear-state subtrees, keep LOD0 only, cap source meshes and split large materials.
        // 3: ghosts follow each piece's saved wear state, so caches built before it are pristine
        // where the blueprint asked for damaged.
        private const int CacheFileVersion = 3;
        private const char WearCacheKeySeparator = '#';
        private const uint CacheFileMagic = 0x4D475042;
        private const string CacheFileExtension = ".bpghost";
        private const string TempFileSuffix = ".tmp";
        private const string SplitPartSuffix = "_part";
        private const string GhostObjectPrefix = "BPGhost_";
        private const int HeaderReadBufferSizeBytes = 4096;
        private const int FileBufferSizeBytes = 1024 * 1024;
        private const long BakeFrameBudgetMs = 10;
        private const double SlowGhostLoadLogThresholdMs = 250;

        // Caps added after a Phalanx bake produced a combined mesh over 4 GB and locked Unity on attach.
        private const int MaxSourceMeshVertices = 50_000;
        private const int MaxCombinedPartVertices = 1_000_000;

        // Out of BepInEx/config, which r2modman and Thunderstore sweep into exports. See VAFapCachePaths.
        public static string CacheDir => VAFapCachePaths.GhostMeshCacheDir;

        private readonly struct BlueprintSource
        {
            public readonly string Name;
            public readonly string SourcePath;
            public readonly int PieceCount;

            public BlueprintSource(VABlueprintLoader.Blueprint blueprint)
            {
                Name = blueprint.Name;
                SourcePath = blueprint.SourcePath;
                PieceCount = blueprint.Objects.Count;
            }
        }

        private sealed class CacheStatus
        {
            public readonly string SourceSha1;
            public readonly bool FileIsCurrent;

            public CacheStatus(string sourceSha1, bool fileIsCurrent)
            {
                SourceSha1 = sourceSha1;
                FileIsCurrent = fileIsCurrent;
            }
        }

        private struct CacheFileHeader
        {
            public string BlueprintName;
            public string SourceSha1;
            public int PieceCount;
            public int PartCount;

            public bool Matches(BlueprintSource source, string sourceSha1) =>
                BlueprintName == source.Name && SourceSha1 == sourceSha1 && PieceCount == source.PieceCount;
        }

        // One combined mesh's vertex streams. The counts are kept apart from the arrays so a loader can
        // reuse arrays larger than the part it currently holds.
        private sealed class GhostPart
        {
            public string Name;
            public bool HasNormals;
            public bool HasColors;
            public int VertexCount;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public int UvCount;
            public Vector2[] Uvs;
            public Color32[] Colors;
            public int TriangleIndexCount;
            public int[] Triangles;
        }

        private sealed class GhostBake
        {
            public string BlueprintName;
            public string SourceSha1;
            public int PieceCount;
            public readonly List<GhostPart> Parts = new List<GhostPart>();
        }

        private sealed class BakeableMesh
        {
            public Mesh Mesh;
            public int VertexCount;
            public Material[] SubmeshMaterials;
            public Matrix4x4 PrefabLocalMatrix;
        }

        // A material's combine instances, split into parts that each stay under the combined vertex cap.
        private sealed class MaterialParts
        {
            public readonly List<List<CombineInstance>> Parts = new List<List<CombineInstance>> { new List<CombineInstance>() };
            private int _verticesInLastPart;

            public void Add(CombineInstance instance, int vertexCount)
            {
                if (_verticesInLastPart > 0 && _verticesInLastPart + vertexCount > MaxCombinedPartVertices)
                {
                    Parts.Add(new List<CombineInstance>());
                    _verticesInLastPart = 0;
                }
                Parts[Parts.Count - 1].Add(instance);
                _verticesInLastPart += vertexCount;
            }
        }

        private sealed class BakeSkips
        {
            public int MissingPrefabPieces, Disabled, WearState, NotLod0, NoMesh, Unreadable, OverVertexCap, NoMaterial;

            public override string ToString() =>
                $"{MissingPrefabPieces} pieces with no prefab; renderers skipped per prefab: disabled {Disabled}, " +
                $"wear state {WearState}, not LOD0 {NotLod0}, no mesh {NoMesh}, unreadable {Unreadable}, " +
                $"over vertex cap {OverVertexCap}, no material {NoMaterial}";
        }

        private static readonly ConcurrentDictionary<string, CacheStatus> _statusByBlueprint =
            new ConcurrentDictionary<string, CacheStatus>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, GhostBake> _bakesBeingWritten =
            new ConcurrentDictionary<string, GhostBake>(StringComparer.Ordinal);

        public static bool HasPendingWrites => !_bakesBeingWritten.IsEmpty;

        // The budget asks before evicting, so a bake still being written is never deleted under itself.
        internal static bool IsBakeInFlight(string blueprintName) => _bakesBeingWritten.ContainsKey(blueprintName);

        // An evicted file must not leave a status behind saying its cache is current, or the next pick
        // opens a file that is gone. The load path recovers from that anyway, but silently rebaking
        // after a caught exception is a worse account of what happened than simply being stale.
        internal static void InvalidateCacheStatus(string blueprintName)
        {
            if (_statusByBlueprint.TryGetValue(blueprintName, out var status))
                _statusByBlueprint[blueprintName] = new CacheStatus(status.SourceSha1, fileIsCurrent: false);
        }

        // Hashes each blueprint and reads its cache file header on a worker thread. A blueprint's first
        // check stands until a bake writes a new file for it.
        public static Task CheckCachesInBackground(IEnumerable<VABlueprintLoader.Blueprint> blueprints)
        {
            var sources = new List<BlueprintSource>();
            foreach (var blueprint in blueprints) sources.Add(new BlueprintSource(blueprint));
            return Task.Run(() =>
            {
                foreach (var source in sources)
                    if (!_statusByBlueprint.ContainsKey(source.Name))
                        _statusByBlueprint.TryAdd(source.Name, CheckCache(source));
            });
        }

        public static bool HasCachedGhost(VABlueprintLoader.Blueprint blueprint) =>
            _bakesBeingWritten.ContainsKey(blueprint.Name) || GetStatus(blueprint).FileIsCurrent;

        // Gives a blueprint stub its ghost meshes: from a bake still being written, from the cache file, or
        // by baking now when neither is current.
        public static void AttachGhost(GameObject stub, VABlueprintLoader.Blueprint blueprint, ZNetScene scene)
        {
            var materials = VAFapBlueprintMaterialLookup.Build(blueprint, scene);
            if (_bakesBeingWritten.TryGetValue(blueprint.Name, out var bakeBeingWritten))
            {
                foreach (var part in bakeBeingWritten.Parts) AttachPart(stub, blueprint.Name, part, materials);
                return;
            }

            if (GetStatus(blueprint).FileIsCurrent && TryAttachFromFile(stub, blueprint, materials)) return;

            var bake = BakeCoroutine(blueprint, scene, stub);
            while (bake.MoveNext()) { }
        }

        public static IEnumerator BakeToCacheCoroutine(VABlueprintLoader.Blueprint blueprint, ZNetScene scene) =>
            BakeCoroutine(blueprint, scene, stubToAttach: null);

        private static CacheStatus GetStatus(VABlueprintLoader.Blueprint blueprint) =>
            _statusByBlueprint.GetOrAdd(blueprint.Name, _ => CheckCache(new BlueprintSource(blueprint)));

        private static CacheStatus CheckCache(BlueprintSource source)
        {
            string sourceSha1 = VABlueprintLoader.TryComputeSourceHash(source.SourcePath);
            if (sourceSha1 == null) return new CacheStatus(null, fileIsCurrent: false);
            try
            {
                return new CacheStatus(sourceSha1, IsCacheFileCurrent(source, sourceSha1));
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — couldn't read the ghost cache header for '{source.Name}', so it will be rebaked: {ex.Message}");
                return new CacheStatus(sourceSha1, fileIsCurrent: false);
            }
        }

        private static bool IsCacheFileCurrent(BlueprintSource source, string sourceSha1)
        {
            string path = GetCachePath(source.Name);
            if (!File.Exists(path)) return false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, HeaderReadBufferSizeBytes))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
                return TryReadHeader(reader, out var header) && header.Matches(source, sourceSha1);
        }

        // Streams the cache file one part at a time through reused arrays, so no more than the largest part
        // is in memory even for a multi-gigabyte ghost. On any failure the attached parts are removed and
        // the cache is marked stale, so the caller bakes instead.
        private static bool TryAttachFromFile(GameObject stub, VABlueprintLoader.Blueprint blueprint, Dictionary<string, Material> materials)
        {
            var status = GetStatus(blueprint);
            var attachedObjects = new List<GameObject>();
            var loadTimer = Stopwatch.StartNew();
            var readTimer = new Stopwatch();
            long vertexCount = 0;
            try
            {
                using (var stream = new FileStream(GetCachePath(blueprint.Name), FileMode.Open, FileAccess.Read, FileShare.Read,
                                                   FileBufferSizeBytes, FileOptions.SequentialScan))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (!TryReadHeader(reader, out var header) || !header.Matches(new BlueprintSource(blueprint), status.SourceSha1))
                    {
                        _statusByBlueprint[blueprint.Name] = new CacheStatus(status.SourceSha1, fileIsCurrent: false);
                        return false;
                    }

                    var part = new GhostPart();
                    for (int partIndex = 0; partIndex < header.PartCount; partIndex++)
                    {
                        readTimer.Start();
                        ReadPart(reader, part);
                        readTimer.Stop();
                        var attached = AttachPart(stub, blueprint.Name, part, materials);
                        if (attached == null) continue;
                        attachedObjects.Add(attached);
                        vertexCount += part.VertexCount;
                    }
                }
            }
            catch (Exception ex)
            {
                DestroyGhostObjects(attachedObjects);
                _statusByBlueprint[blueprint.Name] = new CacheStatus(status.SourceSha1, fileIsCurrent: false);
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — couldn't load the ghost for '{blueprint.Name}', rebaking it: {ex.Message}");
                return false;
            }

            if (attachedObjects.Count == 0) return false;

            VAFapGhostCacheBudget.Touch(blueprint.Name);

            if (VAFapLoadSummary.VerboseEnabled || loadTimer.Elapsed.TotalMilliseconds >= SlowGhostLoadLogThresholdMs)
                Debug.Log($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — loaded the ghost for '{blueprint.Name}' in {loadTimer.Elapsed.TotalSeconds:F2}s " +
                          $"(file read {readTimer.Elapsed.TotalSeconds:F2}s, meshes {(loadTimer.Elapsed - readTimer.Elapsed).TotalSeconds:F2}s, " +
                          $"{attachedObjects.Count} parts, {vertexCount:N0} vertices).");
            return true;
        }

        private static void DestroyGhostObjects(List<GameObject> ghostObjects)
        {
            foreach (var ghostObject in ghostObjects)
            {
                UnityEngine.Object.DestroyImmediate(ghostObject.GetComponent<MeshFilter>().sharedMesh);
                UnityEngine.Object.DestroyImmediate(ghostObject);
            }
        }

        private static GameObject AttachPart(GameObject stub, string blueprintName, GhostPart part, Dictionary<string, Material> materials)
        {
            var material = ResolvePartMaterial(materials, part.Name);
            return material == null ? null : AttachMesh(stub, part.Name, BuildMesh(blueprintName, part), material);
        }

        private static GameObject AttachMesh(GameObject stub, string partName, Mesh mesh, Material material)
        {
            var ghostObject = new GameObject(GhostObjectPrefix + partName);
            ghostObject.transform.SetParent(stub.transform, worldPositionStays: false);
            ghostObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = ghostObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return ghostObject;
        }

        private static Mesh BuildMesh(string blueprintName, GhostPart part)
        {
            var mesh = new Mesh
            {
                name = GetMeshName(blueprintName, part.Name),
                indexFormat = part.VertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(part.Vertices, 0, part.VertexCount);
            if (part.HasNormals) mesh.SetNormals(part.Normals, 0, part.VertexCount);
            if (part.UvCount == part.VertexCount) mesh.SetUVs(0, part.Uvs, 0, part.UvCount);
            if (part.HasColors) mesh.SetColors(part.Colors, 0, part.VertexCount);
            mesh.SetTriangles(part.Triangles, 0, part.TriangleIndexCount, 0, calculateBounds: true);
            if (!part.HasNormals) mesh.RecalculateNormals();
            return mesh;
        }

        private static string GetMeshName(string blueprintName, string partName) => $"{GhostObjectPrefix}{blueprintName}_{partName}";

        // A material split at the vertex cap is stored as parts named "{material}_part{N}".
        private static Material ResolvePartMaterial(Dictionary<string, Material> materials, string partName)
        {
            if (VAFapBlueprintMaterialLookup.TryResolve(materials, partName, out var material, out _)) return material;
            return TryGetSplitPartMaterialName(partName, out string materialName)
                && VAFapBlueprintMaterialLookup.TryResolve(materials, materialName, out material, out _)
                ? material
                : null;
        }

        private static bool TryGetSplitPartMaterialName(string partName, out string materialName)
        {
            materialName = null;
            int suffixStart = partName.LastIndexOf(SplitPartSuffix, StringComparison.Ordinal);
            int indexStart = suffixStart + SplitPartSuffix.Length;
            if (suffixStart <= 0 || indexStart == partName.Length) return false;
            for (int i = indexStart; i < partName.Length; i++)
                if (!char.IsDigit(partName[i])) return false;
            materialName = partName.Substring(0, suffixStart);
            return true;
        }

        // Combines the blueprint's pieces into one mesh per material, split into parts at the vertex cap, and
        // queues the result to be written to the cache. With a stub the meshes are attached to it; without
        // one they are discarded once their arrays are captured.
        private static IEnumerator BakeCoroutine(VABlueprintLoader.Blueprint blueprint, ZNetScene scene, GameObject stubToAttach)
        {
            var partsByMaterial = new Dictionary<Material, MaterialParts>();
            var meshesByPrefab = new Dictionary<string, List<BakeableMesh>>(StringComparer.Ordinal);
            var skips = new BakeSkips();
            var frameTimer = Stopwatch.StartNew();

            foreach (var blueprintPiece in blueprint.Objects)
            {
                AddPieceToMaterialParts(blueprintPiece, scene, meshesByPrefab, partsByMaterial, skips);
                if (frameTimer.ElapsedMilliseconds < BakeFrameBudgetMs) continue;
                yield return null;
                frameTimer.Restart();
            }

            if (partsByMaterial.Count == 0)
            {
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — '{blueprint.Name}' has nothing to bake, so its ghost is empty ({skips}).");
                yield break;
            }

            var bake = new GhostBake
            {
                BlueprintName = blueprint.Name,
                SourceSha1 = GetStatus(blueprint).SourceSha1,
                PieceCount = blueprint.Objects.Count,
            };
            foreach (var materialParts in partsByMaterial)
            {
                var material = materialParts.Key;
                string materialName = VAFapBlueprintMaterialLookup.NormalizeInstanceName(material.name);
                var parts = materialParts.Value.Parts;
                for (int partIndex = 0; partIndex < parts.Count; partIndex++)
                {
                    string partName = parts.Count > 1 ? materialName + SplitPartSuffix + partIndex : materialName;
                    var mesh = CombineInstances(parts[partIndex], GetMeshName(blueprint.Name, partName));
                    if (mesh == null) continue;
                    bake.Parts.Add(CapturePart(partName, mesh));
                    if (stubToAttach != null) AttachMesh(stubToAttach, partName, mesh, material);
                    else UnityEngine.Object.Destroy(mesh);
                    yield return null;
                }
            }

            if (bake.SourceSha1 != null) QueueWrite(bake);
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — baked '{blueprint.Name}' into {bake.Parts.Count} part(s) from {blueprint.Objects.Count} pieces ({skips}).");
        }

        private static void AddPieceToMaterialParts(
            VABlueprintLoader.Piece blueprintPiece,
            ZNetScene scene,
            Dictionary<string, List<BakeableMesh>> meshesByPrefab,
            Dictionary<Material, MaterialParts> partsByMaterial,
            BakeSkips skips)
        {
            // Keyed by look as well as prefab: the same piece can appear in one blueprint intact,
            // damaged and invisible, and the ghost has to show what the bake will build.
            var prefab = string.IsNullOrEmpty(blueprintPiece.Prefab) ? null : scene.GetPrefab(blueprintPiece.Prefab);
            var look = VAFapBlueprintMeshBaker.ResolveWearLook(prefab, blueprintPiece.Data);
            string meshCacheKey = blueprintPiece.Prefab + WearCacheKeySeparator + look.Bits;

            if (!meshesByPrefab.TryGetValue(meshCacheKey, out var bakeableMeshes))
            {
                bakeableMeshes = prefab != null ? CollectBakeableMeshes(prefab, look, skips) : null;
                meshesByPrefab[meshCacheKey] = bakeableMeshes;
            }
            if (bakeableMeshes == null)
            {
                skips.MissingPrefabPieces++;
                return;
            }

            var pieceScale = blueprintPiece.Scale != Vector3.zero ? blueprintPiece.Scale : Vector3.one;
            var pieceMatrix = Matrix4x4.TRS(blueprintPiece.Pos, blueprintPiece.Rot, pieceScale);
            foreach (var bakeableMesh in bakeableMeshes)
            {
                var instanceMatrix = pieceMatrix * bakeableMesh.PrefabLocalMatrix;
                for (int submesh = 0; submesh < bakeableMesh.SubmeshMaterials.Length; submesh++)
                {
                    var material = bakeableMesh.SubmeshMaterials[submesh];
                    if (material == null) continue;
                    if (!partsByMaterial.TryGetValue(material, out var materialParts))
                        partsByMaterial[material] = materialParts = new MaterialParts();
                    materialParts.Add(new CombineInstance { mesh = bakeableMesh.Mesh, subMeshIndex = submesh, transform = instanceMatrix }, bakeableMesh.VertexCount);
                }
            }
        }

        // The meshes a prefab adds to a ghost: enabled LOD0 renderers for the look the piece
        // was saved with, whose meshes are readable and under the source vertex cap.
        private static List<BakeableMesh> CollectBakeableMeshes(GameObject prefab, WearLook look, BakeSkips skips)
        {
            var bakeableMeshes = new List<BakeableMesh>();
            var lod0Renderers = CollectLod0Renderers(prefab, out bool hasLodGroups);
            foreach (var meshRenderer in prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
            {
                if (!meshRenderer.enabled) { skips.Disabled++; continue; }
                if (!VAFapBlueprintMeshBaker.ShouldCaptureRenderer(meshRenderer, prefab, look)) { skips.WearState++; continue; }
                if (hasLodGroups && !lod0Renderers.Contains(meshRenderer)) { skips.NotLod0++; continue; }

                var meshFilter = meshRenderer.GetComponent<MeshFilter>();
                var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                if (mesh == null || mesh.vertexCount == 0) { skips.NoMesh++; continue; }
                if (!mesh.isReadable) { skips.Unreadable++; continue; }
                if (mesh.vertexCount > MaxSourceMeshVertices) { skips.OverVertexCap++; continue; }

                var rendererMaterials = meshRenderer.sharedMaterials;
                var submeshMaterials = new Material[Mathf.Min(mesh.subMeshCount, rendererMaterials.Length)];
                for (int submesh = 0; submesh < submeshMaterials.Length; submesh++)
                {
                    submeshMaterials[submesh] = rendererMaterials[submesh];
                    if (submeshMaterials[submesh] == null) skips.NoMaterial++;
                }

                bakeableMeshes.Add(new BakeableMesh
                {
                    Mesh = mesh,
                    VertexCount = mesh.vertexCount,
                    SubmeshMaterials = submeshMaterials,
                    PrefabLocalMatrix = prefab.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix,
                });
            }
            return bakeableMeshes;
        }

        private static HashSet<Renderer> CollectLod0Renderers(GameObject prefab, out bool hasLodGroups)
        {
            var lod0Renderers = new HashSet<Renderer>();
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(includeInactive: true);
            hasLodGroups = lodGroups.Length > 0;
            foreach (var lodGroup in lodGroups)
            {
                var lods = lodGroup.GetLODs();
                if (lods.Length == 0) continue;
                foreach (var renderer in lods[0].renderers)
                    if (renderer != null) lod0Renderers.Add(renderer);
            }
            return lod0Renderers;
        }

        private static Mesh CombineInstances(List<CombineInstance> instances, string meshName)
        {
            var mesh = new Mesh { name = meshName, indexFormat = IndexFormat.UInt32 };
            try
            {
                mesh.CombineMeshes(instances.ToArray(), mergeSubMeshes: true, useMatrices: true);
                mesh.RecalculateBounds();
                return mesh;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — couldn't combine '{meshName}': {ex.Message}");
                UnityEngine.Object.Destroy(mesh);
                return null;
            }
        }

        private static GhostPart CapturePart(string partName, Mesh mesh)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            var colors = mesh.colors32;
            var triangles = mesh.triangles;
            return new GhostPart
            {
                Name = partName,
                HasNormals = normals.Length == vertices.Length,
                HasColors = colors.Length == vertices.Length,
                VertexCount = vertices.Length,
                Vertices = vertices,
                Normals = normals,
                UvCount = uvs.Length,
                Uvs = uvs,
                Colors = colors,
                TriangleIndexCount = triangles.Length,
                Triangles = triangles,
            };
        }

        private static void QueueWrite(GhostBake bake)
        {
            _bakesBeingWritten[bake.BlueprintName] = bake;
            Task.Run(() => WriteBake(bake));
        }

        private static void WriteBake(GhostBake bake)
        {
            bool written = false;
            long fileBytes = 0;
            try
            {
                fileBytes = WriteCacheFile(bake);
                written = true;
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapBlueprintGhostCache — couldn't write the ghost cache for '{bake.BlueprintName}': {ex.Message}");
            }
            finally
            {
                _statusByBlueprint[bake.BlueprintName] = new CacheStatus(bake.SourceSha1, written);
                _bakesBeingWritten.TryRemove(bake.BlueprintName, out _);
            }

            if (written) VAFapGhostCacheBudget.NoteWritten(bake.BlueprintName, fileBytes);
        }

        private static string GetCachePath(string blueprintName) =>
            Path.Combine(CacheDir, VAFapFileNames.ToSafeFileName(blueprintName) + CacheFileExtension);

        // The file is written under a temporary name and renamed into place, so a crash mid-write never
        // leaves a truncated cache file behind.
        private static long WriteCacheFile(GhostBake bake)
        {
            Directory.CreateDirectory(CacheDir);
            string path = GetCachePath(bake.BlueprintName);
            string tempPath = path + TempFileSuffix;
            long fileBytes;
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSizeBytes))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(CacheFileMagic);
                writer.Write(CacheFileVersion);
                writer.Write(bake.BlueprintName);
                writer.Write(bake.SourceSha1);
                writer.Write(bake.PieceCount);
                writer.Write(bake.Parts.Count);
                foreach (var part in bake.Parts) WritePart(writer, part);
                writer.Flush();
                fileBytes = stream.Length;
            }
            File.Delete(path);
            File.Move(tempPath, path);
            return fileBytes;
        }

        private static bool TryReadHeader(BinaryReader reader, out CacheFileHeader header)
        {
            header = default;
            if (reader.ReadUInt32() != CacheFileMagic || reader.ReadInt32() != CacheFileVersion) return false;
            header.BlueprintName = reader.ReadString();
            header.SourceSha1 = reader.ReadString();
            header.PieceCount = reader.ReadInt32();
            header.PartCount = reader.ReadInt32();
            return true;
        }

        private static void WritePart(BinaryWriter writer, GhostPart part)
        {
            writer.Write(part.Name);
            writer.Write(part.HasNormals);
            writer.Write(part.HasColors);
            writer.Write(part.VertexCount);
            VAFapArrayBlockIO.Write(writer, part.Vertices, part.VertexCount);
            if (part.HasNormals) VAFapArrayBlockIO.Write(writer, part.Normals, part.VertexCount);
            writer.Write(part.UvCount);
            VAFapArrayBlockIO.Write(writer, part.Uvs, part.UvCount);
            if (part.HasColors) VAFapArrayBlockIO.Write(writer, part.Colors, part.VertexCount);
            writer.Write(part.TriangleIndexCount);
            VAFapArrayBlockIO.Write(writer, part.Triangles, part.TriangleIndexCount);
        }

        private static void ReadPart(BinaryReader reader, GhostPart part)
        {
            part.Name = reader.ReadString();
            part.HasNormals = reader.ReadBoolean();
            part.HasColors = reader.ReadBoolean();
            part.VertexCount = reader.ReadInt32();
            part.Vertices = ReadIntoArray(reader, part.Vertices, part.VertexCount);
            if (part.HasNormals) part.Normals = ReadIntoArray(reader, part.Normals, part.VertexCount);
            part.UvCount = reader.ReadInt32();
            part.Uvs = ReadIntoArray(reader, part.Uvs, part.UvCount);
            if (part.HasColors) part.Colors = ReadIntoArray(reader, part.Colors, part.VertexCount);
            part.TriangleIndexCount = reader.ReadInt32();
            part.Triangles = ReadIntoArray(reader, part.Triangles, part.TriangleIndexCount);
        }

        private static T[] ReadIntoArray<T>(BinaryReader reader, T[] reusableArray, int count) where T : unmanaged
        {
            var array = reusableArray != null && reusableArray.Length >= count ? reusableArray : new T[count];
            VAFapArrayBlockIO.Read(reader, array, count);
            return array;
        }
    }
}
