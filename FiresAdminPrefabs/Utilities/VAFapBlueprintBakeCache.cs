using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // In-memory cache of bake results, keyed by blueprint source file name. A blueprint
    // is combined once and every later placement or bundle prefab build reuses the
    // Meshes instead of re-running the combine.
    //
    // Meshes are in blueprint-local space: the bake parent sits at the placement
    // transform and its part objects sit at local zero. The cache owns the Meshes and
    // keeps them across world reloads; Remove destroys a blueprint's Meshes for a re-bake.
    public static class VAFapBlueprintBakeCache
    {
        // One combined mesh with a submesh per material. Small blueprints fit in a single
        // part; megablueprints split across several. The template builder groups parts by
        // bake zone into ZoneBucket children, wires LOD levels into a per-bucket LODGroup
        // whose switch distances come from VABakeLodPolicy, and skips the collider on rug
        // parts flagged ExcludeFromCollision.
        public sealed class CachedPart
        {
            public Mesh CombinedMesh;
            public Material[] Materials;
            public bool ExcludeFromCollision;
            public int SourceLayer;
            public int TriCount;
            public VABakeZoneKey ZoneKey;
            public int LodLevel;
        }

        // A blueprint piece kept as a real prefab beside the combined mesh: interactive
        // pieces, signs, vegetation and rugs. Mirrors InfinityHammer's BlueprintObject
        // field for field and spawns the same way — roll Chance, instantiate at the
        // blueprint-local transform, then apply Data (or ExtraInfo when Data is empty).
        public sealed class LiveChildEntry
        {
            public string PrefabName;
            public Vector3 BpLocalPos;
            public Quaternion BpLocalRot;
            public Vector3 BpLocalScale;
            public string Data;
            public float Chance;
            public string ExtraInfo;
        }

        public sealed class CachedBake
        {
            public string BlueprintName;
            public List<CachedPart> Parts = new List<CachedPart>();
            public List<LiveChildEntry> LiveChildren = new List<LiveChildEntry>();
            public int InteractivePieces;
            public int CandidatePieces;
            public int NoReadableMeshCount;
            public int TotalTris;
        }

        private static readonly Dictionary<string, CachedBake> _byName =
            new Dictionary<string, CachedBake>(StringComparer.Ordinal);

        public static bool TryGet(string blueprintName, out CachedBake cached)
        {
            cached = null;
            return !string.IsNullOrEmpty(blueprintName) && _byName.TryGetValue(blueprintName, out cached);
        }

        public static bool Has(string blueprintName) =>
            !string.IsNullOrEmpty(blueprintName) && _byName.ContainsKey(blueprintName);

        // Returns the cached bake only while every part's Mesh and Materials are still
        // alive. Unity can destroy them behind the cache (UnloadUnusedAssets, material
        // teardown on world unload), and a dead reference renders invisible.
        public static bool TryGetMaterializedAlive(string blueprintName, out CachedBake cached)
        {
            cached = null;
            if (!TryGet(blueprintName, out var candidate) || candidate?.Parts == null || candidate.Parts.Count == 0)
                return false;

            foreach (var part in candidate.Parts)
            {
                if (part == null || part.CombinedMesh == null) return false;
                if (part.Materials == null) continue;
                foreach (var material in part.Materials)
                    if (material == null) return false;
            }
            cached = candidate;
            return true;
        }

        // Stores a fresh bake, publishes it as a .bakebundle, and repaints the hammer's
        // "cached" icon badges. Replacing an entry leaves the old Meshes alive.
        public static void Set(string blueprintName, CachedBake cached)
        {
            if (string.IsNullOrEmpty(blueprintName) || cached == null) return;
            _byName[blueprintName] = cached;

            try
            {
                if (VerdantsAscent.Managers.VABlueprintHammerIntegration.TryGetBlueprintBySourceFileName(blueprintName, out var blueprint) && blueprint != null)
                    VABakeBundle.Publish(cached, blueprintName, blueprint.SourcePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapBlueprintBakeCache.Set — bundle publish threw for '{blueprintName}': {ex.Message}");
            }

            try { VABakeCacheIconBadge.RefreshAllStubs(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapBlueprintBakeCache.Set — icon badge refresh threw for '{blueprintName}': {ex.Message}");
            }
        }

        // Stores a bake rebuilt from a .bakebundle, without publishing it again.
        public static void SetMaterialized(string blueprintName, CachedBake cached)
        {
            if (string.IsNullOrEmpty(blueprintName) || cached == null) return;
            _byName[blueprintName] = cached;
        }

        // Drops one blueprint and destroys its Meshes so a re-bake starts clean. Any
        // placement still referencing those Meshes loses its geometry.
        public static bool Remove(string blueprintName)
        {
            if (!TryGet(blueprintName, out var cached)) return false;
            if (cached?.Parts != null)
            {
                foreach (var part in cached.Parts)
                    if (part?.CombinedMesh != null) UnityEngine.Object.Destroy(part.CombinedMesh);
            }
            _byName.Remove(blueprintName);
            return true;
        }
    }
}
