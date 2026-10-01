using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using VerdantsAscent.Managers;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Utilities
{
    // Binary format and mesh rebuild for baked blueprints. A bake's Meshes live on the GPU
    // and die with the process, so VABakeBundle stores their raw vertex and index arrays
    // in this format as a .bakebundle and Materialize turns them back into Meshes.
    //
    // Materials can't be serialized, so only their names are stored. Materialize resolves
    // them against the blueprint's prefabs in ZNetScene; a name that no longer resolves
    // (its mod was removed) falls back to the part's dominant material, or leaves the part
    // invisible when nothing in it resolves.
    public static class VAFapBakeCacheDisk
    {
        // v8 splits a bake's zone key into 64m cubes and drops the per-part transition height
        // now that VABakeLodPolicy derives switch distances. Only v8 loads; anything older is
        // refused and its blueprint re-bakes.
        //
        // The header is an int version followed by a one-byte compression flag.
        private const int FileVersion = 8;
        private const byte CompressedPayloadFlag = 1;

        // Vertex colours are never written: source meshes carry mod-painted and
        // support-stability tints that would be frozen into the bake.
        private const int FileBufferSizeBytes = 1024 * 1024;
        private const int Vector3SizeBytes = 12;
        private const int Vector2SizeBytes = 8;
        private const long SlowPartWarnThresholdMs = 1000;
        private const float DefaultLiveChildChance = 1f;

        // One combined-mesh part: per-vertex streams flattened to XYZ and XY floats, plus a
        // material name and triangle index list per submesh.
        public sealed class SerializedPart
        {
            public int SourceLayer;
            public int TriCount;
            public bool UInt32Index;
            public int VertexCount;
            public bool HasNormals;
            public float[] VerticesXYZ;
            public float[] NormalsXYZ;
            public float[] UvsXY;
            public string[] MaterialNames;
            public int[][] SubmeshTriangles;
            public int ZoneX;
            public int ZoneY;
            public int ZoneZ;
            public int LodLevel;
            public bool ExcludeFromCollision;
        }

        public sealed class SerializedLiveChild
        {
            public string PrefabName;
            public float PosX, PosY, PosZ;
            public float RotX, RotY, RotZ, RotW;
            public float ScaleX, ScaleY, ScaleZ;
            public string Data = string.Empty;
            public float Chance = DefaultLiveChildChance;
            public string ExtraInfo = string.Empty;
        }

        public sealed class SerializedBlueprintBake
        {
            public string SourceSha1;
            public string CacheKey;
            public int InteractivePieces;
            public int CandidatePieces;
            public int NoReadableMeshCount;
            public int TotalTris;
            public List<SerializedPart> Parts = new List<SerializedPart>();
            public List<SerializedLiveChild> LiveChildren = new List<SerializedLiveChild>();
        }

        private struct MaterialRecoveryCounts
        {
            public int PartsSubstituted;
            public int SlotsPatched;
            public int PartsInvisible;

            public bool Any => PartsSubstituted > 0 || PartsInvisible > 0;
        }

        // ─────────────────────────────────────────────────────────
        //  Convert in-memory CachedBake → serialized form
        // ─────────────────────────────────────────────────────────

        // Captures a bake's mesh arrays and material names, or returns null when the source
        // blueprint can't be hashed. Material names are normalized first so HD-Textures
        // instance numbering and " (Instance)" suffixes can't break the lookup on load.
        public static SerializedBlueprintBake SerializeFromInMemory(
            VAFapBlueprintBakeCache.CachedBake cached,
            string cacheKey,
            string sourcePath)
        {
            if (cached == null) return null;
            string sourceSha1 = VABlueprintLoader.TryComputeSourceHash(sourcePath);
            if (string.IsNullOrEmpty(sourceSha1)) return null;

            var serialized = new SerializedBlueprintBake
            {
                SourceSha1 = sourceSha1,
                CacheKey = cacheKey,
                InteractivePieces = cached.InteractivePieces,
                CandidatePieces = cached.CandidatePieces,
                NoReadableMeshCount = cached.NoReadableMeshCount,
                TotalTris = cached.TotalTris,
            };

            foreach (var part in cached.Parts)
            {
                if (part == null || part.CombinedMesh == null) continue;
                serialized.Parts.Add(SerializePart(part));
            }

            if (cached.LiveChildren != null)
            {
                foreach (var liveChild in cached.LiveChildren)
                {
                    if (liveChild == null || string.IsNullOrEmpty(liveChild.PrefabName)) continue;
                    serialized.LiveChildren.Add(new SerializedLiveChild
                    {
                        PrefabName = liveChild.PrefabName,
                        PosX = liveChild.BpLocalPos.x,
                        PosY = liveChild.BpLocalPos.y,
                        PosZ = liveChild.BpLocalPos.z,
                        RotX = liveChild.BpLocalRot.x,
                        RotY = liveChild.BpLocalRot.y,
                        RotZ = liveChild.BpLocalRot.z,
                        RotW = liveChild.BpLocalRot.w,
                        ScaleX = liveChild.BpLocalScale.x,
                        ScaleY = liveChild.BpLocalScale.y,
                        ScaleZ = liveChild.BpLocalScale.z,
                        Data = liveChild.Data ?? string.Empty,
                        Chance = liveChild.Chance > 0f ? liveChild.Chance : DefaultLiveChildChance,
                        ExtraInfo = liveChild.ExtraInfo ?? string.Empty,
                    });
                }
            }

            return serialized;
        }

        private static SerializedPart SerializePart(VAFapBlueprintBakeCache.CachedPart part)
        {
            var mesh = part.CombinedMesh;
            var vertices = mesh.vertices;
            var serializedPart = new SerializedPart
            {
                SourceLayer = part.SourceLayer,
                TriCount = part.TriCount,
                UInt32Index = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32,
                VertexCount = vertices.Length,
                VerticesXYZ = FlattenVector3s(vertices),
                ZoneX = part.ZoneKey.X,
                ZoneY = part.ZoneKey.Y,
                ZoneZ = part.ZoneKey.Z,
                LodLevel = part.LodLevel,
                ExcludeFromCollision = part.ExcludeFromCollision,
            };

            var normals = mesh.normals;
            if (normals != null && normals.Length == vertices.Length)
            {
                serializedPart.HasNormals = true;
                serializedPart.NormalsXYZ = FlattenVector3s(normals);
            }

            var uvs = mesh.uv;
            serializedPart.UvsXY = uvs != null && uvs.Length == vertices.Length
                ? FlattenVector2s(uvs)
                : new float[vertices.Length * 2];

            int submeshCount = mesh.subMeshCount;
            serializedPart.MaterialNames = new string[submeshCount];
            serializedPart.SubmeshTriangles = new int[submeshCount][];
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                var material = part.Materials != null && submesh < part.Materials.Length ? part.Materials[submesh] : null;
                serializedPart.MaterialNames[submesh] = VAFapBlueprintMaterialLookup.NormalizeInstanceName(material != null ? material.name : string.Empty);
                serializedPart.SubmeshTriangles[submesh] = mesh.GetTriangles(submesh);
            }
            return serializedPart;
        }

        private static float[] FlattenVector3s(Vector3[] vectors)
        {
            var flat = new float[vectors.Length * 3];
            for (int i = 0; i < vectors.Length; i++)
            {
                flat[i * 3] = vectors[i].x;
                flat[i * 3 + 1] = vectors[i].y;
                flat[i * 3 + 2] = vectors[i].z;
            }
            return flat;
        }

        private static float[] FlattenVector2s(Vector2[] vectors)
        {
            var flat = new float[vectors.Length * 2];
            for (int i = 0; i < vectors.Length; i++)
            {
                flat[i * 2] = vectors[i].x;
                flat[i * 2 + 1] = vectors[i].y;
            }
            return flat;
        }

        // ─────────────────────────────────────────────────────────
        //  Convert serialized form → in-memory CachedBake
        // ─────────────────────────────────────────────────────────

        // Rebuilds GPU Meshes from a serialized bake and resolves each submesh material by
        // name through materialLookup (see VAFapBlueprintMaterialLookup). Parts are built
        // one at a time, so only one part's typed vertex arrays are alive at once.
        public static VAFapBlueprintBakeCache.CachedBake Materialize(
            SerializedBlueprintBake serialized,
            Dictionary<string, Material> materialLookup)
        {
            if (serialized == null) return null;

            var cached = new VAFapBlueprintBakeCache.CachedBake
            {
                BlueprintName = serialized.CacheKey,
                InteractivePieces = serialized.InteractivePieces,
                CandidatePieces = serialized.CandidatePieces,
                NoReadableMeshCount = serialized.NoReadableMeshCount,
                TotalTris = serialized.TotalTris,
            };

            var recovery = new MaterialRecoveryCounts();
            foreach (var part in serialized.Parts)
            {
                if (part == null || part.VertexCount == 0) continue;
                int partIndex = cached.Parts.Count;
                cached.Parts.Add(new VAFapBlueprintBakeCache.CachedPart
                {
                    CombinedMesh = BuildPartMesh(serialized.CacheKey, partIndex, part),
                    Materials = ResolvePartMaterials(serialized.CacheKey, part, materialLookup, partIndex, ref recovery),
                    SourceLayer = part.SourceLayer,
                    TriCount = part.TriCount,
                    ZoneKey = new VABakeZoneKey { X = part.ZoneX, Y = part.ZoneY, Z = part.ZoneZ },
                    LodLevel = part.LodLevel,
                    ExcludeFromCollision = part.ExcludeFromCollision,
                });
            }

            if (recovery.Any)
                VAFapLoadSummary.EmitMaterialRecovery(serialized.CacheKey, recovery.PartsSubstituted, recovery.SlotsPatched, recovery.PartsInvisible);

            foreach (var liveChild in serialized.LiveChildren)
            {
                if (liveChild == null || string.IsNullOrEmpty(liveChild.PrefabName)) continue;
                cached.LiveChildren.Add(new VAFapBlueprintBakeCache.LiveChildEntry
                {
                    PrefabName = liveChild.PrefabName,
                    BpLocalPos = new Vector3(liveChild.PosX, liveChild.PosY, liveChild.PosZ),
                    BpLocalRot = new Quaternion(liveChild.RotX, liveChild.RotY, liveChild.RotZ, liveChild.RotW),
                    BpLocalScale = new Vector3(liveChild.ScaleX, liveChild.ScaleY, liveChild.ScaleZ),
                    Data = liveChild.Data ?? string.Empty,
                    Chance = liveChild.Chance > 0f ? liveChild.Chance : DefaultLiveChildChance,
                    ExtraInfo = liveChild.ExtraInfo ?? string.Empty,
                });
            }

            return cached;
        }

        // Uploads one part to a new Mesh. The GPU upload happens now rather than on first
        // render, and the mesh stays CPU-readable for collider cooking. A part stored without
        // usable normals gets them recalculated.
        private static Mesh BuildPartMesh(string cacheKey, int partIndex, SerializedPart part)
        {
            var buildStopwatch = Stopwatch.StartNew();
            int vertexCount = part.VertexCount;

            var mesh = new Mesh { name = $"FAPBakeCacheDisk_{cacheKey}_p{partIndex}" };
            mesh.indexFormat = part.UInt32Index
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            if (part.VerticesXYZ != null && part.VerticesXYZ.Length >= vertexCount * 3)
                mesh.SetVertices(UnflattenVector3s(part.VerticesXYZ, vertexCount));

            bool hasNormals = part.HasNormals && part.NormalsXYZ != null && part.NormalsXYZ.Length >= vertexCount * 3;
            if (hasNormals)
                mesh.SetNormals(UnflattenVector3s(part.NormalsXYZ, vertexCount));

            if (part.UvsXY != null && part.UvsXY.Length >= vertexCount * 2)
                mesh.SetUVs(0, UnflattenVector2s(part.UvsXY, vertexCount));

            int submeshCount = part.SubmeshTriangles?.Length ?? 0;
            mesh.subMeshCount = submeshCount;
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                var triangles = part.SubmeshTriangles[submesh];
                if (triangles != null) mesh.SetTriangles(triangles, submesh, calculateBounds: false);
            }
            if (!hasNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: false);

            buildStopwatch.Stop();
            if (buildStopwatch.ElapsedMilliseconds > SlowPartWarnThresholdMs)
                Debug.LogWarning($"{VaPieces.PluginName}: [BAKE-SLOW-PART] '{cacheKey}' part {partIndex} = {buildStopwatch.ElapsedMilliseconds}ms (verts={vertexCount}, tris~{part.TriCount}, recalcNormals={!hasNormals}, uint32Idx={part.UInt32Index}).");
            return mesh;
        }

        // Vector3 and Vector2 have the same memory layout as their flat float source, so each
        // stream converts with a single block copy.
        private static unsafe Vector3[] UnflattenVector3s(float[] flat, int count)
        {
            var vectors = new Vector3[count];
            long byteCount = (long)count * Vector3SizeBytes;
            fixed (float* source = flat)
            fixed (Vector3* destination = vectors)
            {
                Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            }
            return vectors;
        }

        private static unsafe Vector2[] UnflattenVector2s(float[] flat, int count)
        {
            var vectors = new Vector2[count];
            long byteCount = (long)count * Vector2SizeBytes;
            fixed (float* source = flat)
            fixed (Vector2* destination = vectors)
            {
                Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            }
            return vectors;
        }

        // Resolves a part's submesh materials by name. Slots that stay unresolved reuse the
        // part's highest-triangle material rather than the first one, so a small trim
        // material can't repaint the structure and ghost material cleanup never sees a null.
        private static Material[] ResolvePartMaterials(
            string cacheKey,
            SerializedPart part,
            Dictionary<string, Material> materialLookup,
            int partIndex,
            ref MaterialRecoveryCounts recovery)
        {
            bool verbose = VAFapLoadSummary.VerboseEnabled;
            int submeshCount = part.SubmeshTriangles?.Length ?? 0;
            var materials = new Material[submeshCount];
            int resolved = 0;
            int resolvedByBaseName = 0;
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                if (!VAFapBlueprintMaterialLookup.TryResolve(materialLookup, GetMaterialName(part, submesh), out var material, out bool matchedBaseName))
                    continue;
                materials[submesh] = material;
                resolved++;
                if (matchedBaseName) resolvedByBaseName++;
            }

            if (resolvedByBaseName > 0 && verbose)
                Debug.Log($"{VaPieces.PluginName}: VAFapBakeCacheDisk.Materialize — part {partIndex} of '{cacheKey}' resolved {resolvedByBaseName} material(s) via ' (Instance)' suffix strip (shader-swap mod artifact).");

            if (resolved == 0)
            {
                if (submeshCount == 0) return materials;
                recovery.PartsInvisible++;
                if (verbose)
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapBakeCacheDisk.Materialize — part {partIndex} of '{cacheKey}' had ALL {submeshCount} materials missing from lookup. Visual will be invisible until re-bake.");
                return materials;
            }

            Material dominant = FindHighestTriangleMaterial(part, materials);
            var unresolvedNames = new List<string>();
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                if (materials[submesh] != null) continue;
                materials[submesh] = dominant;
                unresolvedNames.Add(GetMaterialName(part, submesh) ?? "(null)");
            }

            if (unresolvedNames.Count > 0)
            {
                recovery.PartsSubstituted++;
                recovery.SlotsPatched += unresolvedNames.Count;
                if (verbose)
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapBakeCacheDisk.Materialize — part {partIndex} of '{cacheKey}' substituted {unresolvedNames.Count} unresolved slot(s) [{string.Join(", ", unresolvedNames)}] with dominant material '{dominant.name}'.");
            }
            return materials;
        }

        private static string GetMaterialName(SerializedPart part, int submesh) =>
            part.MaterialNames != null && submesh < part.MaterialNames.Length ? part.MaterialNames[submesh] : null;

        private static Material FindHighestTriangleMaterial(SerializedPart part, Material[] materials)
        {
            Material dominant = null;
            int dominantIndexCount = -1;
            for (int submesh = 0; submesh < materials.Length; submesh++)
            {
                if (materials[submesh] == null) continue;
                int indexCount = part.SubmeshTriangles[submesh]?.Length ?? 0;
                if (indexCount <= dominantIndexCount) continue;
                dominantIndexCount = indexCount;
                dominant = materials[submesh];
            }
            return dominant;
        }

        // ─────────────────────────────────────────────────────────
        //  Binary read/write
        // ─────────────────────────────────────────────────────────

        public static void WriteToFile(SerializedBlueprintBake bake, string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSizeBytes))
            {
                using (var headerWriter = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: true))
                {
                    headerWriter.Write(FileVersion);
                    headerWriter.Write(CompressedPayloadFlag);
                }
                using (var deflateStream = new DeflateStream(fileStream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                using (var payloadWriter = new BinaryWriter(deflateStream))
                {
                    WritePayload(payloadWriter, bake);
                }
            }
        }

        // Returns null for anything but the current format, which is how a bundle from an
        // older bake gets re-baked instead of loading with the wrong layout.
        public static SerializedBlueprintBake ReadFromFile(string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                   FileBufferSizeBytes, FileOptions.SequentialScan))
            {
                bool compressed;
                using (var headerReader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true))
                {
                    if (headerReader.ReadInt32() != FileVersion) return null;
                    compressed = headerReader.ReadByte() == CompressedPayloadFlag;
                }

                using (var deflateStream = compressed ? new DeflateStream(fileStream, CompressionMode.Decompress, leaveOpen: true) : null)
                using (var payloadReader = new BinaryReader((Stream)deflateStream ?? fileStream, Encoding.UTF8, leaveOpen: true))
                {
                    return ReadPayload(payloadReader);
                }
            }
        }

        private static void WritePayload(BinaryWriter writer, SerializedBlueprintBake bake)
        {
            WriteString(writer, bake.SourceSha1);
            WriteString(writer, bake.CacheKey);
            writer.Write(bake.InteractivePieces);
            writer.Write(bake.CandidatePieces);
            writer.Write(bake.NoReadableMeshCount);
            writer.Write(bake.TotalTris);

            writer.Write(bake.Parts.Count);
            foreach (var part in bake.Parts)
            {
                writer.Write(part.SourceLayer);
                writer.Write(part.TriCount);
                writer.Write(part.HasNormals);
                writer.Write(part.UInt32Index);
                writer.Write(part.VertexCount);
                writer.Write(part.ZoneX);
                writer.Write(part.ZoneY);
                writer.Write(part.ZoneZ);
                writer.Write(part.LodLevel);
                writer.Write(part.ExcludeFromCollision);
                WriteFloatArray(writer, part.VerticesXYZ);
                if (part.HasNormals) WriteFloatArray(writer, part.NormalsXYZ);
                WriteFloatArray(writer, part.UvsXY);

                int submeshCount = part.SubmeshTriangles?.Length ?? 0;
                writer.Write(submeshCount);
                for (int submesh = 0; submesh < submeshCount; submesh++)
                {
                    WriteString(writer, GetMaterialName(part, submesh));
                    var triangles = part.SubmeshTriangles[submesh] ?? Array.Empty<int>();
                    writer.Write(triangles.Length);
                    VAFapArrayBlockIO.Write(writer, triangles, triangles.Length);
                }
            }

            writer.Write(bake.LiveChildren.Count);
            foreach (var liveChild in bake.LiveChildren)
            {
                WriteString(writer, liveChild.PrefabName);
                writer.Write(liveChild.PosX);
                writer.Write(liveChild.PosY);
                writer.Write(liveChild.PosZ);
                writer.Write(liveChild.RotX);
                writer.Write(liveChild.RotY);
                writer.Write(liveChild.RotZ);
                writer.Write(liveChild.RotW);
                writer.Write(liveChild.ScaleX);
                writer.Write(liveChild.ScaleY);
                writer.Write(liveChild.ScaleZ);
                WriteString(writer, liveChild.Data);
                writer.Write(liveChild.Chance);
                WriteString(writer, liveChild.ExtraInfo);
            }
        }

        private static SerializedBlueprintBake ReadPayload(BinaryReader reader)
        {
            var bake = new SerializedBlueprintBake();
            bake.SourceSha1 = ReadString(reader);
            bake.CacheKey = ReadString(reader);
            bake.InteractivePieces = reader.ReadInt32();
            bake.CandidatePieces = reader.ReadInt32();
            bake.NoReadableMeshCount = reader.ReadInt32();
            bake.TotalTris = reader.ReadInt32();

            int partCount = reader.ReadInt32();
            for (int i = 0; i < partCount; i++)
                bake.Parts.Add(ReadPart(reader));

            int liveChildCount = reader.ReadInt32();
            for (int i = 0; i < liveChildCount; i++)
            {
                var liveChild = ReadLiveChild(reader);
                if (!string.IsNullOrEmpty(liveChild.PrefabName)) bake.LiveChildren.Add(liveChild);
            }
            return bake;
        }

        private static SerializedPart ReadPart(BinaryReader reader)
        {
            var part = new SerializedPart();
            part.SourceLayer = reader.ReadInt32();
            part.TriCount = reader.ReadInt32();
            part.HasNormals = reader.ReadBoolean();
            part.UInt32Index = reader.ReadBoolean();
            part.VertexCount = reader.ReadInt32();
            part.ZoneX = reader.ReadInt32();
            part.ZoneY = reader.ReadInt32();
            part.ZoneZ = reader.ReadInt32();
            part.LodLevel = reader.ReadInt32();
            part.ExcludeFromCollision = reader.ReadBoolean();

            part.VerticesXYZ = ReadFloatArray(reader);
            if (part.HasNormals) part.NormalsXYZ = ReadFloatArray(reader);
            part.UvsXY = ReadFloatArray(reader);

            int submeshCount = reader.ReadInt32();
            part.MaterialNames = new string[submeshCount];
            part.SubmeshTriangles = new int[submeshCount][];
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                part.MaterialNames[submesh] = ReadString(reader);
                var triangles = new int[reader.ReadInt32()];
                VAFapArrayBlockIO.Read(reader, triangles, triangles.Length);
                part.SubmeshTriangles[submesh] = triangles;
            }
            return part;
        }

        private static SerializedLiveChild ReadLiveChild(BinaryReader reader)
        {
            var liveChild = new SerializedLiveChild();
            liveChild.PrefabName = ReadString(reader);
            liveChild.PosX = reader.ReadSingle();
            liveChild.PosY = reader.ReadSingle();
            liveChild.PosZ = reader.ReadSingle();
            liveChild.RotX = reader.ReadSingle();
            liveChild.RotY = reader.ReadSingle();
            liveChild.RotZ = reader.ReadSingle();
            liveChild.RotW = reader.ReadSingle();
            liveChild.ScaleX = reader.ReadSingle();
            liveChild.ScaleY = reader.ReadSingle();
            liveChild.ScaleZ = reader.ReadSingle();
            liveChild.Data = ReadString(reader);
            liveChild.Chance = reader.ReadSingle();
            liveChild.ExtraInfo = ReadString(reader);
            return liveChild;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (string.IsNullOrEmpty(value)) { writer.Write(0); return; }
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            return length <= 0 ? string.Empty : Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static void WriteFloatArray(BinaryWriter writer, float[] values)
        {
            if (values == null) { writer.Write(0); return; }
            writer.Write(values.Length);
            VAFapArrayBlockIO.Write(writer, values, values.Length);
        }

        private static float[] ReadFloatArray(BinaryReader reader)
        {
            var values = new float[reader.ReadInt32()];
            VAFapArrayBlockIO.Read(reader, values, values.Length);
            return values;
        }
    }
}
