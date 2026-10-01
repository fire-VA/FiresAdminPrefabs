using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Targeted null-guard on ZNetScene.OnZDODestroyed.
    //
    // ZDOMan.HandleDestroyedZDO calls back into this method with the
    // destroyed ZDO; vanilla then looks the ZDO up in m_instances and
    // calls znetView.ResetZDO(). When the cached ZNetView is Unity
    // fake-null OR its m_zdo has already been torn down, ResetZDO NREs.
    //
    // This is NOT a sweep — we only intercept the exact call site, only
    // when the entry is provably stale, and we remove just that one
    // dictionary entry. Healthy entries pass through to vanilla. The
    // typical trigger is an orphan ZDO carried in from a save written
    // before EnsurePersistentZNetView was fixed to skip clutter.
    [HarmonyPatch(typeof(ZNetScene), "OnZDODestroyed")]
    public static class ZNetScene_OnZDODestroyed_VAGuard_Patch
    {
        private static readonly HashSet<string> _reportedNames =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _cleanedCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        [HarmonyPriority(Priority.First)]
        [HarmonyPrefix]
        public static bool Prefix(ZNetScene __instance, ZDO zdo,
                                   Dictionary<ZDO, ZNetView> ___m_instances)
        {
            if (__instance == null || zdo == null || ___m_instances == null)
                return true;

            try
            {
                if (!___m_instances.TryGetValue(zdo, out var view))
                    return true;

                if (!IsStale(view)) return true;

                string prefabName = ResolvePrefabName(__instance, zdo);

                if (_cleanedCounts.TryGetValue(prefabName, out var prev))
                    _cleanedCounts[prefabName] = prev + 1;
                else
                    _cleanedCounts[prefabName] = 1;

                if (_reportedNames.Add(prefabName))
                {
                    Debug.LogWarning(
                        $"[VAOnZDODestroyedGuard] Stale m_instances entry for prefab '{prefabName}' " +
                        $"would have NREd in vanilla's OnZDODestroyed → ResetZDO. Removed the dict " +
                        $"entry and skipped vanilla's body. (Typical cause: orphan ZDO carried in " +
                        $"from a prior session's world save before EnsurePersistentZNetView was " +
                        $"fixed to skip clutter. Save self-cleans as these ZDOs are processed.) " +
                        $"Further sightings of this prefab are silently counted.");
                }

                ___m_instances.Remove(zdo);
                return false;
            }
            catch (Exception ex)
            {
                try { Debug.LogError($"[VAOnZDODestroyedGuard] prefix error: {ex.Message}"); }
                catch { }
                return true;
            }
        }

        private static bool IsStale(ZNetView view)
        {
            if ((object)view == null) return true;
            if (view == null) return true;

            ZDO zdo;
            try { zdo = view.GetZDO(); }
            catch { return true; }
            return zdo == null;
        }

        private static string ResolvePrefabName(ZNetScene scene, ZDO zdo)
        {
            try
            {
                if (zdo == null) return "<null-zdo>";
                int hash = zdo.GetPrefab();
                if (hash == 0) return "<no-prefab-hash>";
                var prefab = scene != null ? scene.GetPrefab(hash) : null;
                if (prefab == null) return $"<unknown-prefab:{hash}>";
                return prefab.name;
            }
            catch { return "<resolve-failed>"; }
        }
    }
}
