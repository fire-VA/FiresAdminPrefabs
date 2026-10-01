using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Swallows NREs out of ZNetView.LoadFields caused by Unity "missing
    // script" component slots — bundle prefabs sometimes reference C#
    // types whose source mod isn't installed, leaving null-type slots that
    // LoadFields dereferences. Standalone Unity has no public API for
    // removing those slots, so we catch the NRE at the only call site that
    // actually trips on them.
    //
    // The missing component can't function regardless (no C# behavior
    // exists for it), so silent success is the correct outcome — the rest
    // of the prefab loads + syncs normally. One warning per unique prefab
    // name per session; full counts available via SuppressedCounts.
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.LoadFields))]
    public static class ZNetView_LoadFields_VAGuard_Patch
    {
        private static readonly HashSet<string> _reportedNames =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _suppressedCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public static IReadOnlyDictionary<string, int> SuppressedCounts => _suppressedCounts;

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ZNetView __instance)
        {
            if (__exception == null) return null;

            string name = "<unknown>";
            try
            {
                if (__instance != null && __instance.gameObject != null)
                    name = __instance.gameObject.name;
            }
            catch { }

            int cloneIdx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIdx > 0) name = name.Substring(0, cloneIdx).TrimEnd();

            if (_suppressedCounts.TryGetValue(name, out var prev))
                _suppressedCounts[name] = prev + 1;
            else
                _suppressedCounts[name] = 1;

            if (_reportedNames.Add(name))
            {
                try
                {
                    Debug.LogWarning(
                        $"[VAZNetViewLoadFieldsGuard] Suppressed NRE in ZNetView.LoadFields on prefab '{name}' " +
                        $"(typically caused by a 'missing script' component baked into a bundle prefab whose source mod isn't loaded). " +
                        $"The missing component can't function regardless; the rest of the prefab loads normally. " +
                        $"Further sightings of this prefab are silently suppressed (see SuppressedCounts for totals).");
                }
                catch { }
            }

            return null;
        }
    }
}
