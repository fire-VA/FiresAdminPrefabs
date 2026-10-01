using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Player.CleanupGhostMaterials registers per-ghost Material COPIES in
    // m_ghostRippleDistance and UpdatePlacement writes to every one of them each
    // frame without a liveness check, so a copy destroyed while the ghost is up
    // throws until the player selects another piece. Drop dead keys first.
    [HarmonyPatch(typeof(Player), "UpdatePlacement")]
    public static class Player_UpdatePlacement_VAGhostRippleGuard_Patch
    {
        private static AccessTools.FieldRef<Player, Dictionary<Material, float>> _ghostRippleDistanceRef;
        private static bool _fieldResolveAttempted;
        private static int _destroyedMaterialsDroppedThisSession;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Player __instance)
        {
            if (__instance == null) return;

            if (!_fieldResolveAttempted)
            {
                _fieldResolveAttempted = true;
                try
                {
                    _ghostRippleDistanceRef =
                        AccessTools.FieldRefAccess<Player, Dictionary<Material, float>>("m_ghostRippleDistance");
                }
                catch (Exception ex)
                {
                    _ghostRippleDistanceRef = null;
                    Debug.LogWarning($"{VaPieces.PluginName}: Player.m_ghostRippleDistance did not resolve " +
                                     $"({ex.GetType().Name}) — ghost ripple guard inactive.");
                }
            }
            if (_ghostRippleDistanceRef == null) return;

            Dictionary<Material, float> ghostRippleDistance;
            try { ghostRippleDistance = _ghostRippleDistanceRef(__instance); }
            catch { return; }
            if (ghostRippleDistance == null || ghostRippleDistance.Count == 0) return;

            List<Material> destroyed = null;
            foreach (var entry in ghostRippleDistance)
            {
                if (entry.Key != null) continue;
                if (destroyed == null) destroyed = new List<Material>();
                destroyed.Add(entry.Key);
            }
            if (destroyed == null) return;

            for (int i = 0; i < destroyed.Count; i++) ghostRippleDistance.Remove(destroyed[i]);
            _destroyedMaterialsDroppedThisSession += destroyed.Count;
            Debug.Log($"{VaPieces.PluginName}: dropped {destroyed.Count} destroyed ghost material(s) from the vanilla " +
                      $"ripple list ({_destroyedMaterialsDroppedThisSession} this session).");
        }
    }
}
