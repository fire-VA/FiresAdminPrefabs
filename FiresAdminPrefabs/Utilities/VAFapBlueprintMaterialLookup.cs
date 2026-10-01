using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Bake files can only store material names. This maps them back to the live materials on the
    // blueprint's source prefabs, matching a name exactly or by its base form, because Unity appends
    // " (Instance)" to cloned materials and HD-Textures renumbers its "{base}_HDVInstance{N}" clones
    // every session.
    public static class VAFapBlueprintMaterialLookup
    {
        private const string UnityInstanceSuffix = " (Instance)";
        private const string HdInstanceMarker = "HDVInstance";
        private const char HdInstanceSeparator = '_';

        // Maps every material name used by the blueprint's source prefabs, and each name's base form,
        // to the live Material.
        public static Dictionary<string, Material> Build(VABlueprintLoader.Blueprint blueprint, ZNetScene scene)
        {
            var lookup = new Dictionary<string, Material>(StringComparer.Ordinal);
            if (blueprint == null || scene == null) return lookup;

            var seenPrefabs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var blueprintObject in blueprint.Objects)
            {
                if (blueprintObject == null || string.IsNullOrEmpty(blueprintObject.Prefab)) continue;
                if (!seenPrefabs.Add(blueprintObject.Prefab)) continue;
                var prefab = scene.GetPrefab(blueprintObject.Prefab);
                if (prefab == null) continue;

                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null) continue;
                        AddIfAbsent(lookup, material.name, material);
                        AddIfAbsent(lookup, NormalizeInstanceName(material.name), material);
                    }
                }
            }
            return lookup;
        }

        // Tries the exact name, then the name without Unity's " (Instance)" suffix, then the
        // HD-Textures base name. matchedBaseName reports a hit on either stripped form.
        public static bool TryResolve(
            Dictionary<string, Material> lookup,
            string materialName,
            out Material material,
            out bool matchedBaseName)
        {
            material = null;
            matchedBaseName = false;
            if (string.IsNullOrEmpty(materialName) || lookup == null) return false;
            if (lookup.TryGetValue(materialName, out material)) return true;

            if (materialName.EndsWith(UnityInstanceSuffix, StringComparison.Ordinal)
                && lookup.TryGetValue(materialName.Substring(0, materialName.Length - UnityInstanceSuffix.Length), out material))
            {
                matchedBaseName = true;
                return true;
            }

            string baseName = NormalizeInstanceName(materialName);
            if (!string.IsNullOrEmpty(baseName) && baseName.Length != materialName.Length
                && lookup.TryGetValue(baseName, out material))
            {
                matchedBaseName = true;
                return true;
            }
            return false;
        }

        // Collapses an HD-Textures instanced material name ("{base}_HDVInstance{N}", optionally with
        // " (Instance)") to its base. Every instance is a clone of the base material.
        public static string NormalizeInstanceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string result = name;
            while (result.EndsWith(UnityInstanceSuffix, StringComparison.Ordinal))
                result = result.Substring(0, result.Length - UnityInstanceSuffix.Length);

            int markerIndex = result.LastIndexOf(HdInstanceMarker, StringComparison.Ordinal);
            if (markerIndex > 0)
            {
                bool indexIsDigits = markerIndex + HdInstanceMarker.Length < result.Length;
                for (int i = markerIndex + HdInstanceMarker.Length; i < result.Length; i++)
                    if (!char.IsDigit(result[i])) { indexIsDigits = false; break; }
                if (indexIsDigits)
                {
                    int cut = markerIndex;
                    if (result[cut - 1] == HdInstanceSeparator) cut--;
                    if (cut > 0) result = result.Substring(0, cut);
                }
            }
            return result;
        }

        private static void AddIfAbsent(Dictionary<string, Material> lookup, string name, Material material)
        {
            if (!string.IsNullOrEmpty(name) && !lookup.ContainsKey(name)) lookup[name] = material;
        }
    }
}
