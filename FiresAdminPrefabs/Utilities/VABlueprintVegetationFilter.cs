using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Detects "is this prefab vegetation?" so VAFapBlueprintMeshBaker can
    // exclude trees, bushes, pickables, and plants from the combined-mesh
    // bake. Combining them would fuse their wind-sway / pluck animations
    // into one rigid mesh — visually wrong. They still get the group ZDO
    // stamp so destroying one wipes the whole blueprint.
    //
    // Detection walks components first, then falls back to name fragments
    // for prefabs that don't use vanilla classes. Results are memoized
    // per prefab name and cleared on ZNetScene rebuild.
    public static class VABlueprintVegetationFilter
    {
        private static readonly HashSet<string> VegetationComponentNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "TreeBase",
                "TreeLog",
                "Pickable",
                "Plant",
                "PickableItem",
                "ZSyncTransformPlant",
            };

        private static readonly string[] VegetationNameFragments =
        {
            "_tree",
            "tree_",
            "_bush",
            "bush_",
            "_sapling",
            "_pickable_",
            "pickable_",
            "_berry",
            "berry_",
            "_mushroom",
            "mushroom_",
            "_flower",
            "flower_",
            "shroom",
            "fern",
            "ormbunke",
            "rasp_",
            "blueberry",
            "honey_",
        };

        private static readonly Dictionary<string, bool> _cache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public static bool IsVegetation(GameObject prefab)
        {
            if (prefab == null) return false;
            string name = prefab.name;
            if (string.IsNullOrEmpty(name)) return false;

            if (_cache.TryGetValue(name, out bool cached))
                return cached;

            bool result = ComputeIsVegetation(prefab, name);
            _cache[name] = result;
            return result;
        }

        public static void InvalidateCache() => _cache.Clear();

        private static bool ComputeIsVegetation(GameObject prefab, string name)
        {
            try
            {
                var components = prefab.GetComponentsInChildren<Component>(includeInactive: true);
                for (int i = 0; i < components.Length; i++)
                {
                    var c = components[i];
                    if (c == null) continue;
                    var typeName = c.GetType().Name;
                    if (VegetationComponentNames.Contains(typeName))
                        return true;
                }
            }
            catch { }

            string lname = name.ToLowerInvariant();
            for (int i = 0; i < VegetationNameFragments.Length; i++)
            {
                if (lname.IndexOf(VegetationNameFragments[i], StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }
    }
}
