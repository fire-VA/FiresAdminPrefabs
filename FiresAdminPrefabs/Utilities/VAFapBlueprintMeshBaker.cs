using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using VerdantsAscent.Managers;
using Debug = UnityEngine.Debug;

using FiresCore.Net;
using FiresCore.Pieces;

namespace VerdantsAscent.Utilities
{
    // Combines the renderers of an entire blueprint into a small number
    // of batched draw calls, preserving colliders + interactive pieces.
    // Bake-eligible static pieces get update-heavy components stripped and
    // inactive wear-state children pruned; an interactive deny-list keeps
    // doors/chests/signs/etc. functional with their original renderer.
    public static class VAFapBlueprintMeshBaker
    {
        // Type names that, if PRESENT anywhere in the piece subtree, mark
        // it "interactive — DO NOT bake." Resolved by name so a renamed /
        // removed type silently no-ops. Conservative on purpose: a bake
        // that breaks a door costs more than one that misses geometry.
        private static readonly string[] InteractiveComponentNames =
        {
            "Container", "Door", "Pickable", "ItemDrop", "Fireplace",
            "Smelter", "CookingStation", "Beehive", "MapTable", "Sign",
            "PrivateArea", "TeleportWorld", "Bed", "Chair", "CraftingStation",
            "Plant", "Aoe", "ItemStand", "Bell", "Bonfire",
            "Trap", "Tameable", "MusicLocation", "ZSFX", "EffectArea",
            "OfferingBowl", "RandomFlyingBird", "RandomSpeak",
            "TerrainModifier",   // bombs / artillery shells
            // Workbench-like
            "RuneStone", "PrivateArea", "Vagon",
            // Rigidbody drives its own physics (also covers joint chains,
            // which depend on a Rigidbody) — place as-is rather than bake.
            "Rigidbody",
            // Cloth banners/curtains/tapestries drape via Unity Cloth;
            // baking flattens them. Keep live so the simulation runs.
            "Cloth",
        };

        // Bake coroutines yield whenever a chunk of work exceeds this budget. 33ms is about 30fps:
        // lower is smoother but slower overall, higher bakes faster with visible hitches.
        private const long BakeFrameBudgetMs = 33L;

        // Chunked yield interval for the per-piece phases. Even with
        // a time budget, checking the stopwatch on every single piece
        // adds overhead. We check every N pieces instead.
        private const int BakePerPieceYieldCheckEvery = 64;

        // Vertex thresholds for switching a combined mesh to a UInt32
        // index buffer (UInt16 caps at 65535 verts). PerMesh trips on
        // any single contribution; Summed trips on the running total.
        private const int UInt32IndexVertexThresholdPerMesh = 30000;
        private const int UInt32IndexVertexThresholdSummed = 60000;

        // ──────────────────────────────────────────────────────────────
        //  PREFAB-ONLY BAKE — no spawning, no ZNetView, no zone race
        // ──────────────────────────────────────────────────────────────
        //
        // Bakes straight off ZNetScene prefab templates (readable meshes,
        // stable materials) instead of spawning throwaway pieces — the old
        // spawn-then-bake lost most pieces to ZNetScene.RemoveObjects. Only
        // populates the cache and publishes the bundle.
        public static IEnumerator BakeFromBlueprintPrefabsCoroutine(
            VABlueprintLoader.Blueprint bp,
            ZNetScene scene,
            string cacheKey,
            Action<string> progressCallback = null)
        {
            if (bp == null || scene == null || bp.Objects == null || bp.Objects.Count == 0)
                yield break;

            VAFapBakeGeometryDiagnostic.BeginBake();

            var bakeStopwatch = Stopwatch.StartNew();
            var yieldStopwatch = Stopwatch.StartNew();
            void Report(string msg)
            {
                if (progressCallback == null) return;
                try { progressCallback(msg); } catch { }
            }

            Report($"Prefab bake phase 1 (scan): 0/{bp.Objects.Count} pieces");

            int totalPieces = bp.Objects.Count;
            int skippedNoPrefab = 0, skippedInteractive = 0, skippedVeggie = 0, skippedRug = 0, skippedTransparent = 0, skippedExtraInfo = 0;
            int candidatePieces = 0;
            var byMaterial = new Dictionary<Material, List<Contribution>>();
            int reportEvery = Math.Max(64, totalPieces / 10);

            // v4 — live children. Pieces skipped from the combine
            // (interactive, sign, vegetation, rug) get recorded here
            // with their BP-local transform so the template builder
            // can instantiate them as children of the bundle prefab.
            var liveChildren = new List<VAFapBlueprintBakeCache.LiveChildEntry>();

            // Rebuild readable copies of any R/W-disabled meshes up front
            // (CombineMeshes can't read them), so the contribution scan below
            // recovers them via VAFapReadableMeshProvider.GetReadable.
            {
                var nonReadableMeshes = new HashSet<Mesh>();
                var seenPrefabNames = new HashSet<string>(StringComparer.Ordinal);
                for (int pieceIndex = 0; pieceIndex < totalPieces; pieceIndex++)
                {
                    var pieceData = bp.Objects[pieceIndex];
                    if (pieceData == null || string.IsNullOrEmpty(pieceData.Prefab)) continue;
                    if (!seenPrefabNames.Add(pieceData.Prefab)) continue;
                    GameObject prefab;
                    try { prefab = scene.GetPrefab(pieceData.Prefab); }
                    catch { prefab = null; }
                    if (prefab == null) continue;
                    var renderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                    for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                    {
                        var renderer = renderers[rendererIndex];
                        if (renderer == null || !renderer.enabled) continue;
                        // Every wear state, since this prefab may appear at several of them.
                        if (!ShouldCaptureRenderer(renderer, prefab, null)) continue;
                        var meshFilter = renderer.GetComponent<MeshFilter>();
                        var sharedMesh = meshFilter != null ? meshFilter.sharedMesh : null;
                        if (sharedMesh != null && !sharedMesh.isReadable) nonReadableMeshes.Add(sharedMesh);
                    }
                }
                if (nonReadableMeshes.Count > 0)
                {
                    Report($"Prefab bake pre-pass: recovering {nonReadableMeshes.Count} R/W-disabled mesh(es) via GPU readback…");
                    yield return VAFapReadableMeshProvider.WarmCoroutine(nonReadableMeshes);
                }
            }

            for (int pi = 0; pi < totalPieces; pi++)
            {
                var pieceData = bp.Objects[pi];
                if (pieceData == null || string.IsNullOrEmpty(pieceData.Prefab)) continue;

                GameObject prefab;
                try { prefab = scene.GetPrefab(pieceData.Prefab); }
                catch { prefab = null; }
                if (prefab == null) { skippedNoPrefab++; continue; }

                // Live-child capture: pieces kept out of the combined mesh
                // but instantiated as live prefabs at placement (per-instance
                // state — sign text, container contents, rug collider, etc.).
                bool isInteractive = IsInteractive(prefab) || IsSignPrefab(prefab);
                bool isVeggie = VABlueprintVegetationFilter.IsVegetation(prefab);
                bool isRug = IsRugPrefab(prefab);
                bool isBanner = IsBannerPrefab(prefab);
                // Transparent-queue pieces (stained glass etc.) can't join the
                // opaque combine — merged transparency sorts once per bake
                // instead of per pane. Live-child them like interactives.
                bool isTransparent = IsTransparentPiece(prefab);
                // Pieces whose saved look hides every model (the invisible-piece trick) spawn for real: all
                // that is left of them is their colliders. Every other look bakes with the model it shows.
                var look = ResolveWearLook(prefab, pieceData.Data);
                bool isInvisible = ShowsNoWearModel(prefab, look);
                // ExtraInfo is the piece doing something the combine cannot carry — a custom
                // m_name, an EWP script hook, an edited field. Combining turns it into geometry
                // and the value is gone, so it spawns for real instead, the way Infinity Hammer
                // keeps it.
                bool hasExtraInfo = !string.IsNullOrEmpty(pieceData.ExtraInfo);
                if (isInteractive || isVeggie || isRug || isBanner || isInvisible || isTransparent || hasExtraInfo)
                {
                    Vector3 lcScale = pieceData.Scale != Vector3.zero ? pieceData.Scale : Vector3.one;
                    // Capture all 7 InfinityHammer BlueprintObject fields for
                    // full parity with IH's spawn pipeline at placement time.
                    liveChildren.Add(new VAFapBlueprintBakeCache.LiveChildEntry
                    {
                        PrefabName = pieceData.Prefab,
                        BpLocalPos = pieceData.Pos,
                        BpLocalRot = pieceData.Rot,
                        BpLocalScale = lcScale,
                        Data = pieceData.Data ?? string.Empty,
                        Chance = pieceData.Chance > 0f ? pieceData.Chance : 1.0f,
                        ExtraInfo = pieceData.ExtraInfo ?? string.Empty,
                    });
                    if (isInteractive) skippedInteractive++;
                    else if (isVeggie) skippedVeggie++;
                    else if (isTransparent) skippedTransparent++;
                    else if (hasExtraInfo) skippedExtraInfo++;
                    else skippedRug++;
                    continue;
                }

                // Compose piece-to-bp-local transform.
                Vector3 pieceScale = pieceData.Scale != Vector3.zero ? pieceData.Scale : Vector3.one;
                Matrix4x4 pieceLocal = Matrix4x4.TRS(pieceData.Pos, pieceData.Rot, pieceScale);

                if (CollectContributionsFromPrefab(prefab, pieceLocal, byMaterial, look))
                    candidatePieces++;

                if (pi > 0 && pi % reportEvery == 0)
                    Report($"Prefab bake phase 1: {pi}/{totalPieces} pieces ({candidatePieces} contributed, {liveChildren.Count} live)");

                if ((pi & (BakePerPieceYieldCheckEvery - 1)) == 0 && yieldStopwatch.ElapsedMilliseconds >= BakeFrameBudgetMs)
                {
                    yield return null;
                    yieldStopwatch.Restart();
                }
            }
            string phaseOneSummary = $"{candidatePieces} contributing pieces, {byMaterial.Count} materials, {liveChildren.Count} live children. "
                + $"Skipped: {skippedInteractive} interactive, {skippedVeggie} veg, {skippedRug} rug, {skippedTransparent} transparent, "
                + $"{skippedExtraInfo} extra-info, {skippedNoPrefab} no-prefab";
            Report($"Prefab bake phase 1 done: {phaseOneSummary}");
            // Report() only reaches the on-screen toast, so the split between combined and
            // live-childed pieces was invisible after the fact — which is the number that says
            // whether a rule like extra-info is quietly costing a blueprint its bake.
            VaPieces.Log?.LogInfo($"[PrefabBake] '{cacheKey}': {phaseOneSummary}");

            VAFapBakeGeometryDiagnostic.EmitReport(cacheKey);

            if (byMaterial.Count == 0)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [PrefabBake] '{cacheKey}': zero contributions, nothing to cache.");
                yield break;
            }

            // Phase 2: same pipeline as BakeSpawnedBlueprintCoroutine.
            // Contributions are already BP-local, so worldToBp is identity.
            Matrix4x4 worldToBp = Matrix4x4.identity;

            int totalBatchFailures = 0;
            var cacheParts = new List<VAFapBlueprintBakeCache.CachedPart>();
            var successfullyBakedRenderers = new HashSet<MeshRenderer>();
            var bakedPieceRoots = new HashSet<GameObject>();
            // Same cap as live-bake — see its MAX_VERTS_PER_PART comment.
            const int MAX_VERTS_PER_PART = 500_000;

            // Bucket by (zone, lod) — see the live-bake's Phase 2 block.
            // Uses cacheKey + a transient parent instead of a placed one.
            var bucketed = RebucketByZoneAndLod(byMaterial);
            var zoneKeys = new HashSet<VABakeZoneKey>();
            foreach (var bkey in bucketed.Keys) zoneKeys.Add(bkey.Zone);
            Report($"Prefab bake phase 2 (bucketing): {bucketed.Count} (zone, lod) bucket(s) across {zoneKeys.Count} zone(s)");

            // Transient parent for BuildAndAttachPart's child GOs, destroyed
            // after Phase 2c. cacheParts hold Mesh refs, so they survive it.
            var transientParent = new GameObject($"[FAP/PrefabBake_transient_{SanitizeName(cacheKey)}]");
            transientParent.transform.position = Vector3.zero;
            transientParent.transform.rotation = Quaternion.identity;
            transientParent.SetActive(false);

            int totalParts = 0;
            int totalCombinedTris = 0;
            int globalPartIdx = 0;

            foreach (var bkv in bucketed)
            {
                var bucketKey = bkv.Key;
                var bucketByMaterial = bkv.Value;

                // Phase 2a: per-material first combine (per bucket)
                var perMatList = new List<PerMaterialMesh>(bucketByMaterial.Count);
                int matIdx = 0;
                foreach (var kv in bucketByMaterial)
                {
                    matIdx++;
                    if (kv.Value.Count == 0) continue;
                    if (yieldStopwatch.ElapsedMilliseconds >= BakeFrameBudgetMs)
                    {
                        yield return null;
                        yieldStopwatch.Restart();
                    }
                    var contribBatches = SplitContribsByVertCount(kv.Value, MAX_VERTS_PER_PART);
                    for (int batchIdx = 0; batchIdx < contribBatches.Count; batchIdx++)
                    {
                        var batch = contribBatches[batchIdx];
                        var outBox = new PerMaterialOutBox();
                        yield return CombineSingleMaterialChunkedCoroutine(
                            kv.Key, batch, worldToBp, cacheKey,
                            successfullyBakedRenderers, bakedPieceRoots, outBox, yieldStopwatch);
                        totalBatchFailures += outBox.Failures;
                        var pm = outBox.Result;
                        if (pm.Mesh != null)
                        {
                            perMatList.Add(pm);
                            string batchTag = contribBatches.Count > 1 ? $" batch {batchIdx + 1}/{contribBatches.Count}" : string.Empty;
                            Report($"Prefab bake phase 2a [zone=({bucketKey.Zone}) lod={bucketKey.Lod}]: material {matIdx}/{bucketByMaterial.Count} '{kv.Key.name}'{batchTag} — {pm.VertCount} verts, {pm.TriCount} tris");
                        }
                    }
                }

                if (perMatList.Count == 0)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [PrefabBake] '{cacheKey}' [zone=({bucketKey.Zone}) lod={bucketKey.Lod}]: zero per-material meshes in bucket — skipping.");
                    continue;
                }

                // Phase 2b: partition into parts (per bucket, vert cap inside)
                var partGroups = new List<List<int>>();
                {
                    var current = new List<int>();
                    int currentVerts = 0;
                    for (int i = 0; i < perMatList.Count; i++)
                    {
                        int v = perMatList[i].VertCount;
                        if (current.Count > 0 && currentVerts + v > MAX_VERTS_PER_PART)
                        {
                            partGroups.Add(current);
                            current = new List<int>();
                            currentVerts = 0;
                        }
                        current.Add(i);
                        currentVerts += v;
                    }
                    if (current.Count > 0) partGroups.Add(current);
                }
                Report($"Prefab bake phase 2b [zone=({bucketKey.Zone}) lod={bucketKey.Lod}]: packed {perMatList.Count} materials into {partGroups.Count} part(s)");

                // Phase 2c: per-part second combine + optimize (per bucket)
                for (int g = 0; g < partGroups.Count; g++)
                {
                    if (yieldStopwatch.ElapsedMilliseconds >= BakeFrameBudgetMs)
                    {
                        yield return null;
                        yieldStopwatch.Restart();
                    }
                    Report($"Prefab bake phase 2c [zone=({bucketKey.Zone}) lod={bucketKey.Lod}]: part {g + 1}/{partGroups.Count} — combining {partGroups[g].Count} material(s)");
                    int partTris = BuildAndAttachPart(
                        perMatList, partGroups[g],
                        transientParent, cacheKey, null, globalPartIdx,
                        cacheParts,
                        ref totalBatchFailures,
                        out int optimizedVerts, out int savedVerts, out int savedTris,
                        bucketKey.Zone, bucketKey.Lod,
                        excludeFromCollision: bucketKey.IsRug);
                    globalPartIdx++;
                    if (partTris > 0)
                    {
                        totalParts++;
                        totalCombinedTris += partTris;
                        Report($"Prefab bake phase 2c [zone=({bucketKey.Zone}) lod={bucketKey.Lod}]: part {g + 1} done — {optimizedVerts} verts ({FormatTriCountBrief(partTris)} tris)");
                    }
                }

                // Destroy intermediate per-material Meshes from THIS bucket.
                for (int i = 0; i < perMatList.Count; i++)
                {
                    if (perMatList[i].Mesh != null)
                        try { UnityEngine.Object.Destroy(perMatList[i].Mesh); } catch { }
                }
            }

            if (totalParts == 0)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [PrefabBake] '{cacheKey}': zero parts succeeded.");
                // SafeDestroy is defensive — the transient parent has no
                // ZNetView today, but this covers a later refactor adding one.
                NetworkObjectHelper.SafeDestroy(transientParent);
                yield break;
            }

            // Populate the cache. Set() also queues the disk write.
            var cachedBake = new VAFapBlueprintBakeCache.CachedBake
            {
                BlueprintName = cacheKey,
                Parts = cacheParts,
                LiveChildren = liveChildren,
                InteractivePieces = skippedInteractive,
                CandidatePieces = candidatePieces,
                NoReadableMeshCount = 0,
                TotalTris = totalCombinedTris,
            };

            VAFapBlueprintBakeCache.Set(cacheKey, cachedBake);

            // Tear down the transient parent + part children; the cache holds
            // the Mesh refs (sharedMesh is by-reference) so they survive.
            try { NetworkObjectHelper.SafeDestroy(transientParent); } catch { }

            bakeStopwatch.Stop();
            Report($"Prefab bake done: {totalParts} parts, {FormatTriCountBrief(totalCombinedTris)} tris in {bakeStopwatch.ElapsedMilliseconds / 1000}s");
            // Live-children count (combine-excluded pieces) lets the admin
            // sanity-check the exclusion total; a sudden spike means a
            // classifier change started skipping too much.
            int liveChildCount = cachedBake?.LiveChildren?.Count ?? 0;
            VAFapLoadSummary.EmitBake(
                blueprintName: cacheKey + " (prefab)",
                pieces: candidatePieces,
                materials: totalParts,
                tris: totalCombinedTris,
                bakeSeconds: (float)bakeStopwatch.Elapsed.TotalSeconds,
                liveChildren: liveChildCount);
        }

        // Builds contributions for ONE piece off a PREFAB template at a
        // synthetic pieceToBpLocal transform. Contributions store the final
        // BP-local transform, so Phase 2a combines them with worldToBp=identity.
        // Returns true if it contributed at least one submesh.
        //
        // Bake level 0 takes each renderer's LOD0 and bake level 1 its lowest
        // authored LOD, so both levels cover the whole piece. A piece whose
        // LODGroup has a single level, or a renderer wired outside one, is its
        // own low detail and goes into both.
        private static bool CollectContributionsFromPrefab(
            GameObject prefab,
            Matrix4x4 pieceToBpLocal,
            Dictionary<Material, List<Contribution>> byMaterial,
            WearLook look)
        {
            // Rug-style pieces stay in the visual mesh (player sees
            // the rug) but get excluded from the combined collision
            // mesh so trap doors / pitfalls hidden underneath stay
            // functional. See IsRugPrefab for the detection rule.
            bool includeInCollision = !IsRugPrefab(prefab);

            var sourceLodMap = BuildSourceLodMap(prefab);

            bool contributed = false;
            // Inactive included: a piece saved worn or broken carries the visual the prefab ships
            // switched off, and ShouldCaptureRenderer is what decides which one belongs here.
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (int ri = 0; ri < renderers.Length; ri++)
            {
                var renderer = renderers[ri];
                if (renderer == null || !renderer.enabled) continue;

                if (!ShouldCaptureRenderer(renderer, prefab, look)) continue;

                if (!sourceLodMap.TryGetValue(renderer, out var sourceLod))
                    sourceLod = new SourceLodPlacement { Level = 0, LevelCount = 1 };

                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                var mesh = meshFilter.sharedMesh;
                bool meshWasRebuiltForReading = false;
                if (!mesh.isReadable)
                {
                    // R/W-disabled vanilla mesh — substitute the readback copy
                    // warmed in the pre-pass, or skip if it couldn't recover.
                    var readable = VAFapReadableMeshProvider.GetReadable(mesh);
                    if (readable == null) continue;
                    mesh = readable;
                    meshWasRebuiltForReading = true;
                }
                if (mesh.vertexCount == 0) continue;

                Matrix4x4 mfLocal = prefab.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                Matrix4x4 contributionMatrix = pieceToBpLocal * mfLocal;
                VAFapBakeGeometryDiagnostic.InspectContribution(prefab, mesh, meshWasRebuiltForReading, contributionMatrix);
                Vector3 bpLocalPos = contributionMatrix.MultiplyPoint3x4(Vector3.zero);
                var zoneKey = VABakeZoneKey.FromBlueprintLocalPosition(bpLocalPos);

                var materials = renderer.sharedMaterials;
                int subCount = Mathf.Min(mesh.subMeshCount, materials.Length);
                for (int subMeshIndex = 0; subMeshIndex < subCount; subMeshIndex++)
                {
                    var material = materials[subMeshIndex];
                    if (material == null) continue;
                    if (!byMaterial.TryGetValue(material, out var list))
                    {
                        list = new List<Contribution>();
                        byMaterial[material] = list;
                    }
                    int triangleCount = mesh.GetTriangles(subMeshIndex).Length / 3;
                    for (int bakeLevel = 0; bakeLevel < VABakeLodPolicy.BakeLodLevels; bakeLevel++)
                    {
                        if (!FeedsBakeLevel(sourceLod, bakeLevel)) continue;
                        list.Add(new Contribution
                        {
                            Mesh = mesh,
                            SubMeshIndex = subMeshIndex,
                            LocalToWorld = contributionMatrix,
                            SourceRenderer = renderer,
                            PieceRoot = null,
                            VertexCount = mesh.vertexCount,
                            TriangleCount = triangleCount,
                            IncludeInCollision = includeInCollision,
                            ZoneKey = zoneKey,
                            LodLevel = bakeLevel,
                        });
                        contributed = true;
                    }
                }
            }
            return contributed;
        }

        private struct SourceLodPlacement
        {
            public int Level;
            public int LevelCount;
        }

        private static bool FeedsBakeLevel(SourceLodPlacement sourceLod, int bakeLevel) =>
            bakeLevel == 0 ? sourceLod.Level == 0 : sourceLod.Level == sourceLod.LevelCount - 1;

        private static Dictionary<Renderer, SourceLodPlacement> BuildSourceLodMap(GameObject prefab)
        {
            var sourceLodMap = new Dictionary<Renderer, SourceLodPlacement>();
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(includeInactive: true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lodGroup = lodGroups[g];
                if (lodGroup == null) continue;
                var lods = lodGroup.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                for (int level = 0; level < lods.Length; level++)
                {
                    var lodRenderers = lods[level].renderers;
                    if (lodRenderers == null) continue;
                    for (int r = 0; r < lodRenderers.Length; r++)
                    {
                        var lodRenderer = lodRenderers[r];
                        if (lodRenderer == null || sourceLodMap.ContainsKey(lodRenderer)) continue;
                        sourceLodMap[lodRenderer] = new SourceLodPlacement { Level = level, LevelCount = lods.Length };
                    }
                }
            }
            return sourceLodMap;
        }

        private static readonly HashSet<string> WearStateSubtreeNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "worn", "broken", "destroyed", "destruction",
                "destroyed_fx", "destruction_fx", "_destroyed",
            };

        // True when the transform sits under a worn, broken or destroyed state object, by name.
        // The fallback for pieces whose WearNTear doesn't name its state objects.
        public static bool IsUnderWearStateSubtree(Transform transform, Transform prefabRoot)
        {
            for (var current = transform; current != null && current != prefabRoot; current = current.parent)
                if (WearStateSubtreeNames.Contains(current.name)) return true;
            return false;
        }

        // ── Wear look ─────────────────────────────────────────────────
        //
        // Builders use damaged, worn and invisible pieces on purpose, so a bake captures the look each
        // piece was saved with rather than always its "new" one. WearNTear names the three models itself,
        // which beats matching child names: the names are only the fallback.

        // The look a blueprint piece's saved data gives it once placed, worked out the way the game does:
        // Infinity Hammer's infinite health, worn and damaged looks, and the invisible-piece trick.
        public static WearLook ResolveWearLook(GameObject prefab, string base64Data)
        {
            if (!string.IsNullOrEmpty(base64Data) && VAZdoDataBlob.TryParse(base64Data, out var blob))
                return WearLooks.Resolve(prefab, blob.Floats, blob.Ints, blob.Strings);
            return WearLooks.Healthy(prefab);
        }

        private static readonly Dictionary<long, bool> s_showsNoWearModel = new Dictionary<long, bool>();

        // A look that hides every wear model the prefab has leaves nothing to draw but its colliders, which only
        // a live piece carries - the invisible wall a builder made.
        public static bool ShowsNoWearModel(GameObject prefab, WearLook look)
        {
            var wearNTear = prefab.GetComponent<WearNTear>();
            if (wearNTear == null) return false;
            long key = ((long)prefab.GetInstanceID() << 8) | look.Bits;
            if (s_showsNoWearModel.TryGetValue(key, out bool cached)) return cached;

            bool hasModel = false, showsModel = false;
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!IsUnder(renderer.transform, wearNTear.m_new) && !IsUnder(renderer.transform, wearNTear.m_worn)
                    && !IsUnder(renderer.transform, wearNTear.m_broken)) continue;
                hasModel = true;
                if (WearLooks.IsShown(prefab, look, renderer.transform)) { showsModel = true; break; }
            }
            bool showsNone = hasModel && !showsModel;
            s_showsNoWearModel[key] = showsNone;
            return showsNone;
        }

        // Whether a renderer belongs in the bake. Pass null for look to take every model's
        // geometry, which is what the readable-mesh pre-pass warms: one prefab can appear in a
        // blueprint with several looks, and a mesh it skipped can't be baked later.
        public static bool ShouldCaptureRenderer(Renderer renderer, GameObject prefab, WearLook? look)
        {
            if (renderer == null || prefab == null) return false;

            var wearNTear = prefab.GetComponent<WearNTear>();
            var stateNew = wearNTear != null ? wearNTear.m_new : null;
            var stateWorn = wearNTear != null ? wearNTear.m_worn : null;
            var stateBroken = wearNTear != null ? wearNTear.m_broken : null;

            bool statesAuthored = stateNew != null && (stateWorn != null || stateBroken != null)
                                  && (stateWorn != stateNew || stateBroken != stateNew);
            if (statesAuthored)
            {
                bool underAnyState = IsUnder(renderer.transform, stateNew)
                                  || IsUnder(renderer.transform, stateWorn)
                                  || IsUnder(renderer.transform, stateBroken);
                if (underAnyState)
                {
                    if (!look.HasValue) return true;
                    return WearLooks.IsShown(prefab, look.Value, renderer.transform);
                }
            }

            // Everything outside the state objects keeps the original rule: whatever the prefab
            // ships switched on, minus anything a name marks as a damaged variant.
            return renderer.gameObject.activeInHierarchy
                && !IsUnderWearStateSubtree(renderer.transform, prefab.transform);
        }

        private static bool IsUnder(Transform transform, GameObject root)
        {
            if (root == null) return false;
            for (var current = transform; current != null; current = current.parent)
                if (current == root.transform) return true;
            return false;
        }

        // Compact tri-count formatter for progress messages.
        // 1234 → "1.2K", 1234567 → "1.23M". Banner-friendly inside
        // a one-line Player.Message.
        private static string FormatTriCountBrief(int tris)
        {
            if (tris < 1000) return tris.ToString();
            if (tris < 1_000_000) return (tris / 1000.0).ToString("F1") + "K";
            return (tris / 1_000_000.0).ToString("F2") + "M";
        }

        // Bucket key (ZoneKey, LodLevel, IsRug) — Phase 2 produces one set of
        // parts per bucket. IsRug splits rugs into their own collider-free parts.
        private struct BucketKey : IEquatable<BucketKey>
        {
            private const int HashCombineMultiplier = 397;

            public VABakeZoneKey Zone;
            public int Lod;
            public bool IsRug;

            public bool Equals(BucketKey other) => Zone.Equals(other.Zone) && Lod == other.Lod && IsRug == other.IsRug;
            public override bool Equals(object obj) => obj is BucketKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Zone.GetHashCode();
                    h = (h * HashCombineMultiplier) ^ Lod;
                    h = (h * HashCombineMultiplier) ^ (IsRug ? 1 : 0);
                    return h;
                }
            }
        }

        // Re-bucket a flat byMaterial dict by (zone, lod, isRug) so Phase 2
        // can run its pipeline per bucket. Keyed by BucketKey (not a tuple)
        // to avoid a System.ValueTuple dependency on older Unity setups.
        private static Dictionary<BucketKey, Dictionary<Material, List<Contribution>>>
            RebucketByZoneAndLod(Dictionary<Material, List<Contribution>> byMaterial)
        {
            var buckets = new Dictionary<BucketKey, Dictionary<Material, List<Contribution>>>();
            foreach (var matKv in byMaterial)
            {
                var material = matKv.Key;
                var contribs = matKv.Value;
                if (contribs == null) continue;
                for (int i = 0; i < contribs.Count; i++)
                {
                    var contribution = contribs[i];
                    // Rugs (!IncludeInCollision) get their own buckets so the parts can be
                    // uniformly collider-free.
                    var key = new BucketKey { Zone = contribution.ZoneKey, Lod = contribution.LodLevel, IsRug = !contribution.IncludeInCollision };
                    if (!buckets.TryGetValue(key, out var inner))
                    {
                        inner = new Dictionary<Material, List<Contribution>>();
                        buckets[key] = inner;
                    }
                    if (!inner.TryGetValue(material, out var innerList))
                    {
                        innerList = new List<Contribution>();
                        inner[material] = innerList;
                    }
                    innerList.Add(contribution);
                }
            }
            return buckets;
        }

        // Greedy first-fit split so no per-material output mesh exceeds vertCap
        // (a single over-cap contrib still ships as its own batch). Keeps a
        // dominant material from producing one un-splittable >2.1M-tri part.
        private static List<List<Contribution>> SplitContribsByVertCount(
            List<Contribution> contribs, int vertCap)
        {
            var batches = new List<List<Contribution>>();
            if (contribs == null || contribs.Count == 0) return batches;

            var current = new List<Contribution>();
            int currentVerts = 0;
            for (int i = 0; i < contribs.Count; i++)
            {
                var contribution = contribs[i];
                int vertexCount = contribution.VertexCount;
                if (current.Count > 0 && currentVerts + vertexCount > vertCap)
                {
                    batches.Add(current);
                    current = new List<Contribution>();
                    currentVerts = 0;
                }
                current.Add(contribution);
                currentVerts += vertexCount;
            }
            if (current.Count > 0) batches.Add(current);
            return batches;
        }

        // ──────────────────────────────────────────────────────────────
        //  Internals
        // ──────────────────────────────────────────────────────────────

        // Rug-style pieces (name contains "rug"/"carpet") bake into the visual
        // mesh but are excluded from collision, so trap doors hidden under them
        // keep working. Modded rugs following the convention get it for free.
        public static bool IsRugPrefab(GameObject prefab)
        {
            if (prefab == null) return false;
            string name = prefab.name;
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            if (lower.IndexOf("rug", System.StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("carpet", System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Banner/tapestry/curtain pieces drape via Cloth; baking flattens them.
        // Name match backs up the Cloth component check for modded banners that
        // use a custom (non-Unity-Cloth) simulation. Same pattern as IsSignPrefab.
        public static bool IsBannerPrefab(GameObject prefab)
        {
            if (prefab == null) return false;
            string name = prefab.name;
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            if (lower.IndexOf("banner", System.StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("tapestry", System.StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf("curtain", System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Sign pieces are kept out of the combine so the live Sign component
        // keeps its per-instance ZDO text. Name match backs up the Sign
        // component check for modded signs following the "sign_*"/"*_sign" naming.
        public static bool IsSignPrefab(GameObject prefab)
        {
            if (prefab == null) return false;
            string name = prefab.name;
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            // Anchor to word boundaries so "designed"/"ensign" don't match.
            if (lower == "sign") return true;
            if (lower.StartsWith("sign_", System.StringComparison.Ordinal)) return true;
            if (lower.IndexOf("_sign_", System.StringComparison.Ordinal) >= 0) return true;
            if (lower.EndsWith("_sign", System.StringComparison.Ordinal)) return true;
            return false;
        }

        private struct Contribution
        {
            public Mesh Mesh;
            public int SubMeshIndex;
            public Matrix4x4 LocalToWorld;
            public MeshRenderer SourceRenderer;
            public GameObject PieceRoot;   // the spawned piece's root, for disable + strip tracking
            public int VertexCount;
            public int TriangleCount;
            // false → rendered into the visual mesh but NOT the collision mesh
            // (set by CollectContributions for IsRugPrefab pieces).
            public bool IncludeInCollision;

            // Zone key from the BP-LOCAL position (placement-independent, so it
            // survives the off-site pre-bake). Phase 2 buckets by zone so each
            // part fits one 64m cube for per-bucket collider/LOD toggling.
            public VABakeZoneKey ZoneKey;

            // Bake LOD level, 0 = full detail. See CollectContributionsFromPrefab.
            public int LodLevel;
        }

        // Lazily-built type caches resolved via ResolveTypesQuietly — avoids
        // AccessTools.TypeByName's per-miss warning spam (thousands of lines
        // per placement). A name that doesn't resolve becomes a null entry.
        private static System.Type[] _interactiveTypeCache;

        private static System.Type[] ResolveTypesQuietly(string[] names)
        {
            var result = new System.Type[names.Length];
            // Walk loaded assemblies directly — same case-sensitive match as
            // AccessTools.TypeByName but silent on misses.
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (string.IsNullOrEmpty(name)) continue;
                System.Type found = null;
                for (int a = 0; a < assemblies.Length; a++)
                {
                    var asm = assemblies[a];
                    try
                    {
                        // Try the O(1) hash lookup, then scan exported types by simple name.
                        var t = asm.GetType(name, throwOnError: false, ignoreCase: false);
                        if (t != null) { found = t; break; }
                        var exports = asm.GetTypes();
                        for (int e = 0; e < exports.Length; e++)
                        {
                            if (exports[e].Name == name) { found = exports[e]; break; }
                        }
                        if (found != null) break;
                    }
                    catch { /* asm can't enumerate (ReflectionTypeLoadException) — skip */ }
                }
                result[i] = found;   // null if unresolved — intentional
            }
            return result;
        }

        public static bool IsInteractive(GameObject go)
        {
            if (_interactiveTypeCache == null)
                _interactiveTypeCache = ResolveTypesQuietly(InteractiveComponentNames);
            for (int i = 0; i < _interactiveTypeCache.Length; i++)
            {
                var t = _interactiveTypeCache[i];
                if (t == null) continue;
                if (go.GetComponentInChildren(t, includeInactive: true) != null) return true;
            }
            return false;
        }

        // Transparent-piece bake gate (config: BakeExcludeTransparentPieces).
        // Combining transparent geometry into the opaque bake breaks per-piece
        // depth sorting — each glass pane must sort against the world
        // individually, a merged mesh sorts once for the whole bake (panes
        // draw through walls / in the wrong order). Route these to the
        // live-child path instead. Gate: any renderer material in the
        // transparent queue (>= 2500), plus FiresGlass windows by name as
        // belt-and-braces against shader/queue changes. Verdict cached per
        // prefab name so the renderer walk runs once per prefab kind.
        private static readonly System.Collections.Generic.Dictionary<string, bool> _transparentByName
            = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.Ordinal);

        public static bool IsTransparentPiece(GameObject go)
        {
            if (go == null) return false;
            if (!(VerdantsAscent.Managers.ConfigManager.Instance?.configBakeExcludeTransparent?.Value ?? true))
                return false;

            string key = go.name;
            int paren = key.IndexOf('(');
            if (paren > 0) key = key.Substring(0, paren).Trim();
            if (_transparentByName.TryGetValue(key, out bool cached)) return cached;

            bool result = key.StartsWith("FiresGlass", StringComparison.Ordinal);
            if (!result)
            {
                var renderers = go.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                for (int i = 0; i < renderers.Length && !result; i++)
                {
                    var mats = renderers[i] != null ? renderers[i].sharedMaterials : null;
                    if (mats == null) continue;
                    for (int mi = 0; mi < mats.Length; mi++)
                    {
                        if (mats[mi] != null && mats[mi].renderQueue >= 2500) { result = true; break; }
                    }
                }
            }
            _transparentByName[key] = result;
            return result;
        }

        // ──────────────────────────────────────────────────────────────
        //  Read-only bake diagnostic (fap_bake_diagnose console command)
        // ──────────────────────────────────────────────────────────────

        // Cap on how many names a single diagnostic line lists before
        // collapsing the remainder to a "+N more" suffix.
        private const int DiagnoseReportListCap = 20;

        // Roll-up returned by DiagnoseBlueprintBake so fap_bake_all can
        // aggregate a master list across the whole blueprint library.
        public struct DiagnoseSummary
        {
            public string Name;
            public int Pieces;
            public int DistinctPrefabs;
            public int TotalInstances;
            public int RoofLike;
            public int NoPrefab;
            public int LookupSize;
            public List<string> ZeroGeometry;
            public List<string> MissingMaterials;
            public List<string> ChurnRiskMaterials;
        }

        // Runs the same classification + renderer walk as the real prefab
        // bake but contributes nothing — reports per prefab why each does or
        // doesn't land geometry. Each line is routed to emit (teed to the
        // terminal and LogOutput.log by the caller).
        public static DiagnoseSummary DiagnoseBlueprintBake(
            VABlueprintLoader.Blueprint bp, ZNetScene scene, string nameFilter, Action<string> emit)
        {
            var summary = new DiagnoseSummary
            {
                Name = bp?.Name ?? "(null)",
                ZeroGeometry = new List<string>(),
                MissingMaterials = new List<string>(),
                ChurnRiskMaterials = new List<string>(),
            };
            if (emit == null) emit = _ => { };
            if (bp == null || scene == null || bp.Objects == null)
            {
                emit("[FAP diagnose] null blueprint / scene / objects — nothing to analyze.");
                return summary;
            }

            // Distinct prefab names, instance counts, and one sample Data blob
            // per prefab so the report can show the classifier's Data verdict.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sampleData = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var piece in bp.Objects)
            {
                if (piece == null || string.IsNullOrEmpty(piece.Prefab)) continue;
                if (!counts.ContainsKey(piece.Prefab))
                {
                    counts[piece.Prefab] = 0;
                    sampleData[piece.Prefab] = piece.Data ?? string.Empty;
                    order.Add(piece.Prefab);
                }
                counts[piece.Prefab]++;
            }

            var matLookup = VAFapBlueprintMaterialLookup.Build(bp, scene);

            string filt = string.IsNullOrEmpty(nameFilter) ? null : nameFilter.ToLowerInvariant();
            var zeroContrib = new List<string>();
            var missingMats = new List<string>();
            var missingMatSet = new HashSet<string>(StringComparer.Ordinal);
            var churnMats = new List<string>();
            var churnSet = new HashSet<string>(StringComparer.Ordinal);
            int roofLike = 0, noPrefab = 0, totalInstances = 0;

            emit($"[FAP diagnose] '{bp.Name}' — {order.Count} distinct prefab(s), {bp.Objects.Count} piece(s). " +
                 $"Material lookup: {matLookup.Count} entry/entries. Filter='{(filt ?? "(roof + zero-contributors)")}'.");

            foreach (var name in order)
            {
                totalInstances += counts[name];
                bool isRoofName = name.ToLowerInvariant().IndexOf("roof", StringComparison.Ordinal) >= 0;
                if (isRoofName) roofLike++;

                GameObject prefab;
                try { prefab = scene.GetPrefab(name); }
                catch { prefab = null; }

                var analysis = AnalyzePrefabForDiagnose(prefab, sampleData[name], matLookup);

                if (prefab == null) noPrefab++;
                bool isZero = prefab != null && analysis.Classification == "NORMAL" && analysis.ContributingRenderers == 0;
                if (isZero) zeroContrib.Add(name);

                for (int materialIndex = 0; materialIndex < analysis.MissingMaterials.Count; materialIndex++)
                {
                    if (missingMatSet.Add(analysis.MissingMaterials[materialIndex]))
                        missingMats.Add(analysis.MissingMaterials[materialIndex]);
                }
                if (analysis.CapturedMaterials != null)
                {
                    for (int materialIndex = 0; materialIndex < analysis.CapturedMaterials.Count; materialIndex++)
                    {
                        var materialName = analysis.CapturedMaterials[materialIndex];
                        if (!string.IsNullOrEmpty(materialName)
                            && materialName.IndexOf("Instance", StringComparison.OrdinalIgnoreCase) >= 0
                            && churnSet.Add(materialName))
                            churnMats.Add(materialName);
                    }
                }

                // Always surface roofs and zero-contributors even off-filter —
                // a silent geometry drop is exactly what this report is for.
                bool matchesFilter = filt != null && name.ToLowerInvariant().IndexOf(filt, StringComparison.Ordinal) >= 0;
                bool show = matchesFilter || (filt == null && isRoofName) || isZero;
                if (!show) continue;

                emit($"── {name}  ×{counts[name]}  [{analysis.Classification}]");
                if (prefab == null)
                {
                    emit("     prefab NOT FOUND in ZNetScene → every instance silently dropped (skippedNoPrefab).");
                    continue;
                }
                emit($"     WNT: {analysis.WearDesc}");
                emit($"     LODGroups={analysis.LodGroupCount} (renderers mapped to a LOD level={analysis.LodMappedCount})  " +
                     $"renderers: active={analysis.ActiveRenderers} total={analysis.TotalRenderers}");
                emit($"     contributes: {analysis.ContributingRenderers} renderer(s), {analysis.Submeshes} submesh(es), ~{FormatTriCountBrief(analysis.Tris)} tris");
                emit($"     skipped: underWearState={analysis.SkUnderWear} notInLODGroup={analysis.SkNotInLod} disabled={analysis.SkDisabled} " +
                     $"noMesh={analysis.SkNoMesh} notReadable={analysis.SkNotReadable} zeroVert={analysis.SkZeroVert}");
                emit($"     mats: {analysis.MatDesc}");
                emit($"     => {analysis.Verdict}");
            }

            emit($"[FAP diagnose] DONE '{bp.Name}': {order.Count} prefab(s), {totalInstances} instance(s). " +
                 $"roof-like={roofLike}, no-prefab={noPrefab}, zero-geometry(NORMAL)={zeroContrib.Count}.");
            if (zeroContrib.Count > 0)
                emit("   zero-geometry prefabs: " + JoinFirst(zeroContrib, DiagnoseReportListCap));
            emit($"   materials referenced-but-MISSING from lookup: {missingMats.Count}" +
                 (missingMats.Count > 0 ? " → " + JoinFirst(missingMats, DiagnoseReportListCap) : ""));
            if (churnMats.Count > 0)
                emit($"   instance-suffixed materials (cross-session resolution risk): {churnMats.Count} → " + JoinFirst(churnMats, DiagnoseReportListCap));

            summary.Pieces = bp.Objects.Count;
            summary.DistinctPrefabs = order.Count;
            summary.TotalInstances = totalInstances;
            summary.RoofLike = roofLike;
            summary.NoPrefab = noPrefab;
            summary.LookupSize = matLookup.Count;
            summary.ZeroGeometry = zeroContrib;
            summary.MissingMaterials = missingMats;
            summary.ChurnRiskMaterials = churnMats;
            return summary;
        }

        // Per-prefab analysis result for the diagnostic. Mirror of the
        // outcome of CollectContributionsFromPrefab.
        private struct DiagnoseResult
        {
            public string Classification;   // NORMAL / interactive / sign / veg / rug / banner / invisible(Data) / NO-PREFAB
            public string WearDesc;
            public int LodGroupCount;
            public int LodMappedCount;
            public int ActiveRenderers;
            public int TotalRenderers;
            public int ContributingRenderers;
            public int Submeshes;
            public int Tris;
            public int SkUnderWear, SkNotInLod, SkDisabled, SkNoMesh, SkNotReadable, SkZeroVert;
            public string MatDesc;
            public List<string> MissingMaterials;
            public List<string> CapturedMaterials;
            public string Verdict;
        }

        private static DiagnoseResult AnalyzePrefabForDiagnose(
            GameObject prefab, string sampleData, Dictionary<string, Material> matLookup)
        {
            var result = new DiagnoseResult { MissingMaterials = new List<string>(), MatDesc = "(none)", WearDesc = "no WearNTear" };

            if (prefab == null) { result.Classification = "NO-PREFAB"; result.Verdict = "NO PREFAB — dropped"; return result; }

            // Same classification order + helpers as the bake's Phase-1 routing.
            var diagnoseLook = ResolveWearLook(prefab, sampleData);
            bool isInvisible = ShowsNoWearModel(prefab, diagnoseLook);
            if (IsInteractive(prefab)) result.Classification = "interactive";
            else if (IsSignPrefab(prefab)) result.Classification = "sign";
            else if (VABlueprintVegetationFilter.IsVegetation(prefab)) result.Classification = "veg";
            else if (IsRugPrefab(prefab)) result.Classification = "rug(no-collision)";
            else if (IsBannerPrefab(prefab)) result.Classification = "banner";
            else if (isInvisible) result.Classification = "invisible(Data)";
            else if (IsTransparentPiece(prefab)) result.Classification = "transparent(live)";
            else result.Classification = "NORMAL";

            var wnt = prefab.GetComponent<WearNTear>();
            if (wnt != null)
                result.WearDesc = $"m_new={DescribeWearGo(wnt.m_new)} m_worn={DescribeWearGo(wnt.m_worn)} m_broken={DescribeWearGo(wnt.m_broken)}";

            // Map the renderers of every LOD level.
            var lodMap = new HashSet<Renderer>();
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(includeInactive: true);
            result.LodGroupCount = lodGroups.Length;
            bool hasLod = lodGroups.Length > 0;
            foreach (var lodGroup in lodGroups)
            {
                if (lodGroup == null) continue;
                var lods = lodGroup.GetLODs();
                if (lods == null) continue;
                foreach (var lod in lods)
                {
                    if (lod.renderers == null) continue;
                    foreach (var lodRenderer in lod.renderers)
                        if (lodRenderer != null) lodMap.Add(lodRenderer);
                }
            }
            result.LodMappedCount = lodMap.Count;

            result.ActiveRenderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: false).Length;
            result.TotalRenderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true).Length;

            // Replicate CollectContributionsFromPrefab's eligibility loop.
            var capturedMats = new List<string>();
            var capturedSet = new HashSet<string>(StringComparer.Ordinal);
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) { result.SkDisabled++; continue; }
                if (!ShouldCaptureRenderer(renderer, prefab, diagnoseLook)) { result.SkUnderWear++; continue; }
                if (hasLod && !lodMap.Contains(renderer)) { result.SkNotInLod++; continue; }
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) { result.SkNoMesh++; continue; }
                var mesh = meshFilter.sharedMesh;
                if (!mesh.isReadable) { result.SkNotReadable++; continue; }
                if (mesh.vertexCount == 0) { result.SkZeroVert++; continue; }

                result.ContributingRenderers++;
                var materials = renderer.sharedMaterials;
                int subCount = Mathf.Min(mesh.subMeshCount, materials != null ? materials.Length : 0);
                result.Submeshes += subCount;
                for (int subMeshIndex = 0; subMeshIndex < subCount; subMeshIndex++)
                {
                    if (materials[subMeshIndex] == null) continue;
                    try { result.Tris += mesh.GetTriangles(subMeshIndex).Length / 3; } catch { }
                    string materialName = materials[subMeshIndex].name ?? "";
                    if (capturedSet.Add(materialName)) capturedMats.Add(materialName);
                }
            }

            // Resolve each captured material the way Materialize does
            // (direct hit, or hit after stripping a " (Instance)" suffix).
            if (capturedMats.Count > 0)
            {
                var description = new System.Text.StringBuilder();
                for (int i = 0; i < capturedMats.Count; i++)
                {
                    string materialName = capturedMats[i];
                    bool resolved = matLookup != null && (matLookup.ContainsKey(materialName)
                        || (materialName.EndsWith(" (Instance)", StringComparison.Ordinal)
                            && matLookup.ContainsKey(materialName.Substring(0, materialName.Length - " (Instance)".Length))));
                    if (!resolved) result.MissingMaterials.Add(materialName);
                    if (i > 0) description.Append(", ");
                    description.Append(materialName).Append(resolved ? "[OK]" : "[MISSING]");
                }
                result.MatDesc = description.ToString();
            }
            result.CapturedMaterials = capturedMats;

            // This read-only pass uses the raw filter and never warms the
            // readback cache, so the two now-fixed zero-contributor causes are
            // labelled RECOVERED (not MISSING) — a real bake handles them.
            if (result.Classification != "NORMAL")
                result.Verdict = $"routed to LIVE CHILD (class={result.Classification}) — NOT in combined mesh";
            else if (result.ContributingRenderers == 0)
            {
                if (result.SkNotReadable > 0)
                    result.Verdict = $"RECOVERED at bake: {result.SkNotReadable} R/W-disabled mesh(es) rebuilt via GPU readback (shows 0 here because the diagnostic doesn't warm the cache).";
                else if (hasLod && result.SkNotInLod == result.ActiveRenderers && result.ActiveRenderers > 0)
                    result.Verdict = "RECOVERED at bake: active renderer outside the LODGroup → baked at LOD0 as its own low detail (shows 0 here; diagnostic uses the raw filter).";
                else if (result.TotalRenderers == 0)
                    result.Verdict = "ZERO GEOMETRY → MISSING. Cause: prefab has no MeshRenderer at all (nothing to bake).";
                else if (result.ActiveRenderers == 0)
                    result.Verdict = "ZERO GEOMETRY → MISSING. Cause: pristine visual ships inactive (includeInactive:false misses it) — NOT yet auto-handled.";
                else if (result.SkUnderWear == result.ActiveRenderers)
                    result.Verdict = "ZERO GEOMETRY → MISSING. Cause: every active renderer is under a worn/broken/destroyed subtree (name-skip).";
                else
                    result.Verdict = "ZERO GEOMETRY → MISSING. See skip counts above.";
            }
            else if (result.MissingMaterials.Count > 0)
                result.Verdict = $"contributes geometry, but {result.MissingMaterials.Count} material(s) unresolved → those submeshes render invisible or get a substituted (wrong-color) material";
            else
                result.Verdict = "contributes clean geometry with all materials resolved";
            return result;
        }

        private static string DescribeWearGo(GameObject go)
        {
            if (go == null) return "null";
            return $"'{go.name}'({(go.activeSelf ? "ON" : "off")})";
        }

        // Comma-joins up to max items, collapsing any overflow to "+N more".
        private static string JoinFirst(List<string> items, int max)
        {
            if (items == null || items.Count == 0) return "(none)";
            var builder = new System.Text.StringBuilder();
            int shown = Math.Min(max, items.Count);
            for (int i = 0; i < shown; i++) { if (i > 0) builder.Append(", "); builder.Append(items[i]); }
            if (items.Count > shown) builder.Append($", …(+{items.Count - shown} more)");
            return builder.ToString();
        }

        // Intermediate per-material mesh from Phase 2a's first-pass combine,
        // in BP-local space, carrying the metadata Phase 2c needs.
        private struct PerMaterialMesh
        {
            public Material Material;
            public Mesh Mesh;              // visual — ALL contributions for this material
            // Collision-only counterpart: null if all contribs are rugs, same
            // ref as Mesh if none are, a distinct Mesh if some-but-not-all are.
            public Mesh CollisionMesh;
            public int SourceLayer;
            public int VertCount;
            public int TriCount;
            public MeshRenderer FirstSrcRenderer;
        }

        // First-pass combine for one material into a single PerMaterialMesh,
        // recording contributing renderers + piece roots for the Phase 3 strip.
        // Returns a PerMaterialMesh with Mesh==null on failure.
        private static PerMaterialMesh CombineSingleMaterial(
            Material material,
            List<Contribution> contribs,
            Matrix4x4 worldToBp,
            string blueprintName,
            HashSet<MeshRenderer> successfullyBakedRenderers,
            HashSet<GameObject> bakedPieceRoots,
            ref int totalBatchFailures)
        {
            var result = new PerMaterialMesh { Material = material };
            if (contribs == null || contribs.Count == 0) return result;

            var combineArr = new CombineInstance[contribs.Count];
            bool needsUInt32 = false;
            int triCount = 0;
            int summedVerts = 0;
            for (int i = 0; i < contribs.Count; i++)
            {
                var contribution = contribs[i];
                combineArr[i] = new CombineInstance
                {
                    mesh = contribution.Mesh,
                    subMeshIndex = contribution.SubMeshIndex,
                    transform = worldToBp * contribution.LocalToWorld,
                };
                if (contribution.VertexCount > UInt32IndexVertexThresholdPerMesh) needsUInt32 = true;
                summedVerts += contribution.VertexCount;
                triCount += contribution.TriangleCount;
            }
            if (summedVerts > UInt32IndexVertexThresholdSummed) needsUInt32 = true;

            var combinedMesh = new Mesh { name = $"FAPBlueprintBake_perMat_{SanitizeName(blueprintName)}_{material.name}" };
            if (needsUInt32) combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            try
            {
                combinedMesh.CombineMeshes(combineArr, mergeSubMeshes: true, useMatrices: true);
                combinedMesh.RecalculateBounds();
                // Strip vertex colors CombineMeshes copies — a leak surface for
                // paint/AO/stability mods that mutate sharedMesh.colors at runtime.
                combinedMesh.colors32 = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [Bake] CombineMeshes (per-material) FAILED for material '{material.name}' with {contribs.Count} contribution(s): {ex.Message}");
                UnityEngine.Object.Destroy(combinedMesh);
                totalBatchFailures++;
                return result;
            }

            // Prefer the piece ROOT's layer over the renderer's: roof visual
            // children sit on layer 0 while the root is "piece", and
            // Cover.GetCoverForPoint raycasts the piece mask — wrong layer
            // means baked roofs render but give no shelter.
            int sourceLayer = -1;
            var firstRenderer = contribs[0].SourceRenderer;
            var firstPieceRoot = contribs[0].PieceRoot;
            if (firstPieceRoot != null) sourceLayer = firstPieceRoot.layer;
            if (sourceLayer == 0 && firstRenderer != null) sourceLayer = firstRenderer.gameObject.layer;
            int pieceLayer = LayerMask.NameToLayer("piece");
            if (sourceLayer <= 0 && pieceLayer >= 0) sourceLayer = pieceLayer;
            if (sourceLayer < 0) sourceLayer = LayerMask.NameToLayer("static_solid");
            if (sourceLayer < 0) sourceLayer = 0;

            // Record contributing renderers + roots here (per-material is where
            // we know which original meshes contributed) for the Phase 3 strip.
            for (int i = 0; i < contribs.Count; i++)
            {
                var renderer = contribs[i].SourceRenderer;
                if (renderer == null) continue;
                successfullyBakedRenderers.Add(renderer);
                var pieceRoot = contribs[i].PieceRoot;
                if (pieceRoot != null) bakedPieceRoots.Add(pieceRoot);
            }

            result.Mesh = combinedMesh;
            result.SourceLayer = sourceLayer;
            result.VertCount = combinedMesh.vertexCount;
            result.TriCount = triCount;
            result.FirstSrcRenderer = firstRenderer;

            // Collision counterpart: all non-rug → reuse visual mesh; all rug →
            // null; mixed → combine only the non-rug subset.
            int nonRugCount = 0;
            for (int i = 0; i < contribs.Count; i++)
                if (contribs[i].IncludeInCollision) nonRugCount++;

            if (nonRugCount == contribs.Count)
            {
                result.CollisionMesh = combinedMesh;
            }
            else if (nonRugCount == 0)
            {
                result.CollisionMesh = null;
            }
            else
            {
                var collisionArr = new CombineInstance[nonRugCount];
                int collisionIndex = 0;
                int collisionVerts = 0;
                for (int i = 0; i < contribs.Count; i++)
                {
                    var contribution = contribs[i];
                    if (!contribution.IncludeInCollision) continue;
                    collisionArr[collisionIndex++] = new CombineInstance
                    {
                        mesh = contribution.Mesh,
                        subMeshIndex = contribution.SubMeshIndex,
                        transform = worldToBp * contribution.LocalToWorld,
                    };
                    collisionVerts += contribution.VertexCount;
                }
                var collisionMesh = new Mesh { name = $"FAPBlueprintBake_perMatCol_{SanitizeName(blueprintName)}_{material.name}" };
                if (collisionVerts > UInt32IndexVertexThresholdSummed) collisionMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                try
                {
                    collisionMesh.CombineMeshes(collisionArr, mergeSubMeshes: true, useMatrices: true);
                    collisionMesh.RecalculateBounds();
                    result.CollisionMesh = collisionMesh;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [Bake] CombineMeshes (collision) FAILED for material '{material.name}': {ex.Message}");
                    UnityEngine.Object.Destroy(collisionMesh);
                    result.CollisionMesh = null;   // skip collision for this material
                }
            }

            return result;
        }

        // Mutable box carrying the chunked coroutine's result, since
        // coroutines can't have ref/out params.
        private sealed class PerMaterialOutBox
        {
            public PerMaterialMesh Result;
            public int Failures;
        }

        // Contributions per chunk — one CombineMeshes call each, sized to
        // finish well inside a 60FPS frame so the bake stays responsive.
        private const int ChunkedCombineSize = 250;

        // Chunked variant of CombineSingleMaterial: for >ChunkedCombineSize
        // contribs, combines in yielding chunks then merges them. Same result
        // as the sync version. Output in outBox.Result; failures in outBox.Failures.
        private static IEnumerator CombineSingleMaterialChunkedCoroutine(
            Material material,
            List<Contribution> contribs,
            Matrix4x4 worldToBp,
            string blueprintName,
            HashSet<MeshRenderer> successfullyBakedRenderers,
            HashSet<GameObject> bakedPieceRoots,
            PerMaterialOutBox outBox,
            Stopwatch yieldStopwatch)
        {
            if (outBox == null) yield break;
            outBox.Result = new PerMaterialMesh { Material = material };
            if (contribs == null || contribs.Count == 0) yield break;

            // Tiny materials go through the sync path — no fragmentation overhead.
            if (contribs.Count <= ChunkedCombineSize)
            {
                int syncFailures = 0;
                outBox.Result = CombineSingleMaterial(material, contribs, worldToBp, blueprintName,
                    successfullyBakedRenderers, bakedPieceRoots, ref syncFailures);
                outBox.Failures += syncFailures;
                yield break;
            }

            // Each chunk produces one intermediate Mesh, merged in the final combine.
            var chunkMeshes = new List<Mesh>((contribs.Count / ChunkedCombineSize) + 1);
            int triCount = 0;
            for (int chunkStart = 0; chunkStart < contribs.Count; chunkStart += ChunkedCombineSize)
            {
                int chunkEnd = Mathf.Min(chunkStart + ChunkedCombineSize, contribs.Count);
                int chunkLen = chunkEnd - chunkStart;
                var combineArr = new CombineInstance[chunkLen];
                int chunkVerts = 0;
                for (int i = 0; i < chunkLen; i++)
                {
                    var contribution = contribs[chunkStart + i];
                    combineArr[i] = new CombineInstance
                    {
                        mesh = contribution.Mesh,
                        subMeshIndex = contribution.SubMeshIndex,
                        transform = worldToBp * contribution.LocalToWorld,
                    };
                    chunkVerts += contribution.VertexCount;
                    triCount += contribution.TriangleCount;
                }
                var chunkMesh = new Mesh { name = $"FAPBakeChunk_{SanitizeName(blueprintName)}_{material.name}_{chunkStart}" };
                if (chunkVerts > UInt32IndexVertexThresholdSummed) chunkMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                try
                {
                    chunkMesh.CombineMeshes(combineArr, mergeSubMeshes: true, useMatrices: true);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [Bake/chunked] CombineMeshes chunk FAILED for material '{material.name}' at [{chunkStart},{chunkEnd}): {ex.Message}");
                    UnityEngine.Object.Destroy(chunkMesh);
                    outBox.Failures++;
                    continue;
                }
                chunkMeshes.Add(chunkMesh);

                if (yieldStopwatch != null && yieldStopwatch.ElapsedMilliseconds >= BakeFrameBudgetMs)
                {
                    yield return null;
                    yieldStopwatch.Restart();
                }
            }

            if (chunkMeshes.Count == 0)
            {
                outBox.Failures++;
                yield break;
            }

            // Merge the chunk meshes into one per-material Mesh (fast — bounded
            // by chunk count, not contrib count).
            int summedVerts = 0;
            var finalArr = new CombineInstance[chunkMeshes.Count];
            for (int i = 0; i < chunkMeshes.Count; i++)
            {
                finalArr[i] = new CombineInstance
                {
                    mesh = chunkMeshes[i],
                    subMeshIndex = 0,
                    transform = Matrix4x4.identity,
                };
                summedVerts += chunkMeshes[i].vertexCount;
            }
            var combinedMesh = new Mesh { name = $"FAPBlueprintBake_perMat_{SanitizeName(blueprintName)}_{material.name}" };
            if (summedVerts > UInt32IndexVertexThresholdSummed) combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            try
            {
                combinedMesh.CombineMeshes(finalArr, mergeSubMeshes: true, useMatrices: false);
                combinedMesh.RecalculateBounds();
                // Strip vertex colors (see CombineSingleMaterial for
                // the rationale — same fix for the chunked path).
                combinedMesh.colors32 = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [Bake/chunked] Final per-material CombineMeshes FAILED for material '{material.name}': {ex.Message}");
                UnityEngine.Object.Destroy(combinedMesh);
                for (int i = 0; i < chunkMeshes.Count; i++)
                    try { UnityEngine.Object.Destroy(chunkMeshes[i]); } catch { }
                outBox.Failures++;
                yield break;
            }

            for (int i = 0; i < chunkMeshes.Count; i++)
                try { UnityEngine.Object.Destroy(chunkMeshes[i]); } catch { }

            // Prefer the piece ROOT's layer over the renderer's — see
            // CombineSingleMaterial for the roof-shelter rationale.
            int sourceLayer = -1;
            var firstRenderer = contribs[0].SourceRenderer;
            var firstPieceRoot = contribs[0].PieceRoot;
            if (firstPieceRoot != null) sourceLayer = firstPieceRoot.layer;
            if (sourceLayer == 0 && firstRenderer != null) sourceLayer = firstRenderer.gameObject.layer;
            int pieceLayer = LayerMask.NameToLayer("piece");
            if (sourceLayer <= 0 && pieceLayer >= 0) sourceLayer = pieceLayer;
            if (sourceLayer < 0) sourceLayer = LayerMask.NameToLayer("static_solid");
            if (sourceLayer < 0) sourceLayer = 0;
            for (int i = 0; i < contribs.Count; i++)
            {
                var renderer = contribs[i].SourceRenderer;
                if (renderer == null) continue;
                successfullyBakedRenderers.Add(renderer);
                var pieceRoot = contribs[i].PieceRoot;
                if (pieceRoot != null) bakedPieceRoots.Add(pieceRoot);
            }

            var pm = outBox.Result;
            pm.Mesh = combinedMesh;
            pm.SourceLayer = sourceLayer;
            pm.VertCount = combinedMesh.vertexCount;
            pm.TriCount = triCount;
            pm.FirstSrcRenderer = firstRenderer;

            // Collision counterpart — same three cases as CombineSingleMaterial,
            // chunked for the mixed case.
            int nonRugCount = 0;
            for (int i = 0; i < contribs.Count; i++)
                if (contribs[i].IncludeInCollision) nonRugCount++;

            if (nonRugCount == contribs.Count)
            {
                pm.CollisionMesh = combinedMesh;
            }
            else if (nonRugCount == 0)
            {
                pm.CollisionMesh = null;
            }
            else
            {
                var collisionContribs = new List<Contribution>(nonRugCount);
                for (int i = 0; i < contribs.Count; i++)
                    if (contribs[i].IncludeInCollision) collisionContribs.Add(contribs[i]);

                var colChunkMeshes = new List<Mesh>((collisionContribs.Count / ChunkedCombineSize) + 1);
                for (int chunkStart = 0; chunkStart < collisionContribs.Count; chunkStart += ChunkedCombineSize)
                {
                    int chunkEnd = Mathf.Min(chunkStart + ChunkedCombineSize, collisionContribs.Count);
                    int chunkLen = chunkEnd - chunkStart;
                    var arr = new CombineInstance[chunkLen];
                    int chunkVerts = 0;
                    for (int i = 0; i < chunkLen; i++)
                    {
                        var contribution = collisionContribs[chunkStart + i];
                        arr[i] = new CombineInstance
                        {
                            mesh = contribution.Mesh,
                            subMeshIndex = contribution.SubMeshIndex,
                            transform = worldToBp * contribution.LocalToWorld,
                        };
                        chunkVerts += contribution.VertexCount;
                    }
                    var cm = new Mesh { name = $"FAPBakeColChunk_{SanitizeName(blueprintName)}_{material.name}_{chunkStart}" };
                    if (chunkVerts > UInt32IndexVertexThresholdSummed) cm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    try { cm.CombineMeshes(arr, mergeSubMeshes: true, useMatrices: true); }
                    catch { UnityEngine.Object.Destroy(cm); continue; }
                    colChunkMeshes.Add(cm);
                    if (yieldStopwatch != null && yieldStopwatch.ElapsedMilliseconds >= BakeFrameBudgetMs)
                    {
                        yield return null;
                        yieldStopwatch.Restart();
                    }
                }
                if (colChunkMeshes.Count > 0)
                {
                    int colSummedVerts = 0;
                    var colFinalArr = new CombineInstance[colChunkMeshes.Count];
                    for (int i = 0; i < colChunkMeshes.Count; i++)
                    {
                        colFinalArr[i] = new CombineInstance
                        {
                            mesh = colChunkMeshes[i],
                            subMeshIndex = 0,
                            transform = Matrix4x4.identity,
                        };
                        colSummedVerts += colChunkMeshes[i].vertexCount;
                    }
                    var colFinal = new Mesh { name = $"FAPBlueprintBake_perMatCol_{SanitizeName(blueprintName)}_{material.name}" };
                    if (colSummedVerts > UInt32IndexVertexThresholdSummed) colFinal.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    try
                    {
                        colFinal.CombineMeshes(colFinalArr, mergeSubMeshes: true, useMatrices: false);
                        colFinal.RecalculateBounds();
                        pm.CollisionMesh = colFinal;
                    }
                    catch
                    {
                        UnityEngine.Object.Destroy(colFinal);
                        pm.CollisionMesh = null;
                    }
                    for (int i = 0; i < colChunkMeshes.Count; i++)
                        try { UnityEngine.Object.Destroy(colChunkMeshes[i]); } catch { }
                }
            }

            outBox.Result = pm;
        }

        // Second-pass combine for one PART: merges the chosen per-material meshes
        // into one Mesh (one submesh per material), runs VAFapMeshOptimizer, and
        // builds a single GO (MeshFilter + N-material MeshRenderer + MeshCollider +
        // VABakeBatchMarker). Returns combined tri count (0 on failure); out params
        // report the optimizer's post-counts and savings.
        private static int BuildAndAttachPart(
            List<PerMaterialMesh> perMatList,
            List<int> partIndices,
            GameObject parent,
            string blueprintName,
            string groupId,
            int partIdx,
            List<VAFapBlueprintBakeCache.CachedPart> cacheParts,
            ref int totalBatchFailures,
            out int optimizedVerts,
            out int savedVerts,
            out int savedTris,
            VABakeZoneKey zoneKey,
            int lodLevel,
            bool excludeFromCollision)
        {
            optimizedVerts = 0;
            savedVerts = 0;
            savedTris = 0;
            int partLen = partIndices.Count;
            if (partLen == 0) return 0;

            // The bake spans many frames; the parent can be unloaded mid-bake
            // (ZoneSystem cleanup of an off-site spawn). Touching a destroyed
            // parent.transform NREs in the engine bindings — bail cleanly.
            if (parent == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [Bake] BuildAndAttachPart called with null/destroyed parent for part {partIdx} of '{blueprintName}' — skipping. (Likely the bake parent was unloaded mid-bake by ZoneSystem.)");
                totalBatchFailures++;
                return 0;
            }

            // Second-pass input: one CombineInstance per per-material mesh at
            // subMeshIndex 0, identity transform (already in BP-local space).
            var combineArr = new CombineInstance[partLen];
            var materials = new Material[partLen];
            int sourceLayer = -1;
            int summedVerts = 0;
            int rawTriCount = 0;
            MeshRenderer firstSrc = null;

            for (int i = 0; i < partLen; i++)
            {
                var perMat = perMatList[partIndices[i]];
                combineArr[i] = new CombineInstance
                {
                    mesh = perMat.Mesh,
                    subMeshIndex = 0,
                    transform = Matrix4x4.identity,
                };
                materials[i] = perMat.Material;
                summedVerts += perMat.VertCount;
                rawTriCount += perMat.TriCount;
                if (firstSrc == null && perMat.FirstSrcRenderer != null) firstSrc = perMat.FirstSrcRenderer;
                if (sourceLayer < 0) sourceLayer = perMat.SourceLayer;
            }
            if (sourceLayer < 0) sourceLayer = LayerMask.NameToLayer("static_solid");
            if (sourceLayer < 0) sourceLayer = 0;

            string partSuffix = partIdx > 0 ? $"_part{partIdx}" : string.Empty;
            var finalMesh = new Mesh { name = $"FAPBlueprintBake_{SanitizeName(blueprintName)}{partSuffix}" };
            // The optimizer switches back to UInt16 if the welded vertex count fits.
            if (summedVerts > UInt32IndexVertexThresholdSummed) finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            try
            {
                // mergeSubMeshes=false → one submesh per material; useMatrices=false
                // since per-material meshes are already in BP-local space.
                finalMesh.CombineMeshes(combineArr, mergeSubMeshes: false, useMatrices: false);

                // Final choke point for vertex colours. Valheim's building pieces carry
                // them and Custom/Piece renders them as the structural support tint, so
                // anything that survives here is baked into the geometry as frozen
                // green/yellow. The per-material combines strip their own output, but
                // stripping again here means no upstream path can leak them.
                finalMesh.colors32 = null;

                finalMesh.RecalculateBounds();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [Bake] Final CombineMeshes FAILED for part {partIdx} with {partLen} material(s): {ex.Message}");
                UnityEngine.Object.Destroy(finalMesh);
                totalBatchFailures++;
                return 0;
            }

            // Optimizer pass (see VAFapMeshOptimizer for the per-step rationale).
            int preOptVerts = finalMesh.vertexCount;
            int preOptTris = 0;
            for (int s = 0; s < finalMesh.subMeshCount; s++) preOptTris += finalMesh.GetTriangles(s).Length / 3;

            var optResult = VAFapMeshOptimizer.Optimize(finalMesh);

            optimizedVerts = optResult.NewVertexCount;
            savedVerts = preOptVerts - optResult.NewVertexCount;
            savedTris = preOptTris - optResult.NewTriangleCount;

            if (optResult.KeptSubmeshIndices.Length != materials.Length)
            {
                var keptMaterials = new Material[optResult.KeptSubmeshIndices.Length];
                for (int i = 0; i < keptMaterials.Length; i++) keptMaterials[i] = materials[optResult.KeptSubmeshIndices[i]];
                materials = keptMaterials;
            }

            // Build the GO. ConsTower structure.
            var partGo = new GameObject($"BakedPart{partSuffix}_{SanitizeName(blueprintName)}");
            partGo.transform.SetParent(parent.transform, worldPositionStays: false);
            partGo.transform.localPosition = Vector3.zero;
            partGo.transform.localRotation = Quaternion.identity;
            partGo.transform.localScale = Vector3.one;
            partGo.isStatic = true;
            partGo.layer = sourceLayer;

            var meshFilter = partGo.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = finalMesh;

            var meshRenderer = partGo.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials;
            CopyRendererSettings(meshRenderer, firstSrc);

            // Collision mesh from the per-mat CollisionMesh refs (rug-only
            // materials contribute none). All-non-rug → reuse finalMesh; all-rug
            // → null (no collider); mixed → combine the non-null collision meshes.
            Mesh collisionFinal = null;
            bool noRugsInPart = true;
            int collisionInputCount = 0;
            for (int i = 0; i < partLen; i++)
            {
                var perMat = perMatList[partIndices[i]];
                if (perMat.CollisionMesh == null)
                {
                    noRugsInPart = false;   // material has rug content; this entry has no collision
                    continue;
                }
                collisionInputCount++;
                if (perMat.CollisionMesh != perMat.Mesh) noRugsInPart = false;
            }

            if (noRugsInPart)
            {
                collisionFinal = finalMesh;
            }
            else if (collisionInputCount > 0)
            {
                var collisionArr = new CombineInstance[collisionInputCount];
                int collisionIndex = 0;
                int colVerts = 0;
                for (int i = 0; i < partLen; i++)
                {
                    var perMat = perMatList[partIndices[i]];
                    if (perMat.CollisionMesh == null) continue;
                    collisionArr[collisionIndex++] = new CombineInstance
                    {
                        mesh = perMat.CollisionMesh,
                        subMeshIndex = 0,
                        transform = Matrix4x4.identity,
                    };
                    colVerts += perMat.CollisionMesh.vertexCount;
                }
                var colMesh = new Mesh { name = $"FAPBlueprintBakeCol_{SanitizeName(blueprintName)}{partSuffix}" };
                if (colVerts > UInt32IndexVertexThresholdSummed) colMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                try
                {
                    // mergeSubMeshes=true: MeshCollider ignores submeshes; one
                    // flat mesh cooks faster in PhysX.
                    colMesh.CombineMeshes(collisionArr, mergeSubMeshes: true, useMatrices: false);
                    colMesh.RecalculateBounds();
                    // Optimize the collision mesh too — the weld pass shrinks the PhysX BVH.
                    VAFapMeshOptimizer.Optimize(colMesh);
                    collisionFinal = colMesh;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [Bake] Final collision CombineMeshes FAILED for part {partIdx}: {ex.Message}");
                    UnityEngine.Object.Destroy(colMesh);
                    collisionFinal = null;
                }
            }
            // else collisionInputCount == 0 → rug-only part → no collider (intended).

            // null collisionFinal → no collider, correct for a rug-only part.
            if (collisionFinal != null)
            {
                try
                {
                    var meshCollider = partGo.AddComponent<MeshCollider>();
                    meshCollider.cookingOptions = VABakeBundle.ColliderCookOptions;
                    meshCollider.sharedMesh = collisionFinal;
                    meshCollider.convex = false;
                }
                catch (Exception colEx)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [Bake] MeshCollider attach failed for part {partIdx}: {colEx.Message}");
                }
            }

            if (!string.IsNullOrEmpty(groupId))
            {
                try
                {
                    var marker = partGo.AddComponent<VABakeBatchMarker>();
                    marker.GroupId = groupId;
                }
                catch (Exception markerEx)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [Bake] VABakeBatchMarker attach failed for part {partIdx}: {markerEx.Message}");
                }
            }

            cacheParts.Add(new VAFapBlueprintBakeCache.CachedPart
            {
                CombinedMesh = finalMesh,
                Materials = materials,
                SourceLayer = sourceLayer,
                TriCount = optResult.NewTriangleCount,
                ZoneKey = zoneKey,
                LodLevel = lodLevel,
                ExcludeFromCollision = excludeFromCollision,
            });

            return optResult.NewTriangleCount;
        }

        private static void CopyRendererSettings(MeshRenderer dest, MeshRenderer src)
        {
            if (src == null) return;
            dest.shadowCastingMode = src.shadowCastingMode;
            dest.receiveShadows = src.receiveShadows;
            dest.lightProbeUsage = src.lightProbeUsage;
            dest.reflectionProbeUsage = src.reflectionProbeUsage;
            dest.motionVectorGenerationMode = src.motionVectorGenerationMode;
            dest.allowOcclusionWhenDynamic = src.allowOcclusionWhenDynamic;
        }

        // Replaces non-alphanumeric characters so bundle-sourced names stay
        // readable in the Unity hierarchy.
        private static string SanitizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Unnamed";
            var builder = new System.Text.StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') builder.Append(ch);
                else builder.Append('_');
            }
            return builder.ToString();
        }
    }
}
