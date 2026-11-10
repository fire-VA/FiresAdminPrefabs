using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.1.401 (lead 11-07, snow_proof3 client: "ArgumentException: The Object you want to instantiate is null" from vanilla
    // GuidePoint.Start): vanilla GuidePoint.Start instantiates m_ravenPrefab when no raven exists yet, then registers its raven
    // text. A bundle prefab whose GuidePoint lost its m_ravenPrefab reference throws there, and its text is never registered.
    // The raven prefab comes from vanilla's own data (a vanilla GuidePoint that still holds it, else the loaded Raven asset),
    // never a guessed name. Prefabs are filled at ZNetScene.Awake; location GuidePoints (loaded later) are filled in Start's prefix.
    [HarmonyPatch]
    internal static class VAGuidePointRepair
    {
        private static GameObject s_raven;
        private static string s_ravenFrom;
        private static bool s_noDonorWarned;
        private static readonly HashSet<string> s_saidRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
            if (__instance == null) return;
            try { RepairPrefabs(__instance); }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: VAGuidePointRepair failed: {e.Message}"); }
        }

        private static GameObject Donor(ZNetScene scene)
        {
            if (s_raven != null) return s_raven;
            try
            {
                if (scene != null && scene.m_prefabs != null)
                    foreach (var p in scene.m_prefabs)
                    {
                        if (p == null) continue;
                        foreach (var gp in p.GetComponentsInChildren<GuidePoint>(true))
                            if (gp != null && gp.m_ravenPrefab != null) { s_raven = gp.m_ravenPrefab; s_ravenFrom = $"GuidePoint on prefab '{p.name}'"; return s_raven; }
                    }
                foreach (var gp in Resources.FindObjectsOfTypeAll<GuidePoint>())
                    if (gp != null && gp.m_ravenPrefab != null) { s_raven = gp.m_ravenPrefab; s_ravenFrom = $"loaded GuidePoint on '{gp.transform.root.name}'"; return s_raven; }
                foreach (var r in Resources.FindObjectsOfTypeAll<Raven>())
                    if (r != null && !r.gameObject.scene.IsValid()) { s_raven = r.transform.root.gameObject; s_ravenFrom = "the loaded Raven asset"; return s_raven; }
            }
            catch (Exception e) { VaPieces.Log?.LogDebug($"[GuidePoint] donor search threw: {e.Message}"); }
            return null;
        }

        private static string RootName(Component c) => c.transform.root.name.Replace("(Clone)", "").Trim();

        private static void RepairPrefabs(ZNetScene scene)
        {
            var missing = new List<(GuidePoint gp, string root)>();
            if (scene.m_prefabs != null)
                foreach (var p in scene.m_prefabs)
                {
                    if (p == null) continue;
                    foreach (var gp in p.GetComponentsInChildren<GuidePoint>(true))
                        if (gp != null && gp.m_ravenPrefab == null) missing.Add((gp, p.name));
                }
            if (missing.Count == 0) { VaPieces.Log?.LogDebug("[GuidePoint] every prefab GuidePoint has its raven prefab (1.1.401)"); return; }
            var donor = Donor(scene);
            if (donor == null) { WarnNoDonor(missing.Count + " prefab GuidePoint(s)"); return; }
            foreach (var (gp, _) in missing) gp.m_ravenPrefab = donor;
            var roots = missing.Select(m => m.root).Distinct().ToList();
            VaPieces.Log?.LogInfo($"[GuidePoint] {missing.Count} prefab GuidePoint(s) had no raven prefab -> vanilla '{donor.name}' (from {s_ravenFrom}) (1.1.401)");
            VaPieces.Log?.LogDebug($"[GuidePoint] repaired prefabs: {string.Join(", ", roots.Select(r => r + " (bundle " + Bundle(r) + ")"))}");
        }

        private static string Bundle(string prefab)
        {
            try { return VAExternalBundleLoader.TryGetSourceBundle(prefab, out string b) ? b : "?"; } catch { return "?"; }
        }

        private static void WarnNoDonor(string what)
        {
            if (s_noDonorWarned) return;
            s_noDonorWarned = true;
            Debug.LogWarning($"{VaPieces.PluginName}: VAGuidePointRepair: {what} have no raven prefab and no vanilla raven prefab was found to give them; vanilla GuidePoint.Start will throw for them as before.");
        }

        // Proof + the late fill (location GuidePoints load after ZNetScene.Awake).
        [HarmonyPatch(typeof(GuidePoint), "Start"), HarmonyPrefix]
        private static void GuidePoint_Start_Prefix(GuidePoint __instance)
        {
            try
            {
                if (__instance == null || __instance.m_ravenPrefab != null) return;
                string root = RootName(__instance);
                bool ravenUp = Raven.IsInstantiated();
                var donor = Donor(ZNetScene.instance);
                if (donor != null) __instance.m_ravenPrefab = donor;
                else WarnNoDonor($"GuidePoint '{__instance.gameObject.name}' on '{root}'");
                if (s_saidRoots.Add(root))
                    VaPieces.Log?.LogDebug($"[GuidePoint] Start: '{__instance.gameObject.name}' on '{root}' (bundle {Bundle(root)}) had no raven prefab; raven already up {ravenUp}; "
                        + (donor != null ? $"given vanilla '{donor.name}' (from {s_ravenFrom})" : "no donor - vanilla throws") + " (1.1.401)");
            }
            catch (Exception e) { VaPieces.Log?.LogDebug($"[GuidePoint] Start prefix threw: {e.Message}"); }
        }
    }
}
