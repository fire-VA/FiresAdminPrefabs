using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Diagnostic-only. Logs a stack trace whenever a watched prefab is
    // destroyed via any of the destruction paths so we can identify the
    // caller of "pieces that just disappear" complaints. WatchPrefabs is
    // intentionally small — bundle prefabs only, to avoid drowning the
    // log under normal vanilla zone streaming.
    public static class VAFapDestroyWatchPatch
    {
        private static readonly HashSet<string> WatchPrefabs =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "AngelOakNorger",
                "NorgersAngelOak",
            };

        // Names registered at runtime — e.g. a creature just placed via the
        // monster hammer, so we catch whatever destroys it right after spawn.
        private static readonly HashSet<string> DynamicWatch =
            new HashSet<string>(StringComparer.Ordinal);

        public static void WatchName(string prefabName)
        {
            if (!string.IsNullOrEmpty(prefabName)) DynamicWatch.Add(prefabName);
        }

        private static bool IsWatched(GameObject go)
        {
            if (go == null) return false;
            string n = go.name;
            if (string.IsNullOrEmpty(n)) return false;
            int cloneIdx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIdx > 0) n = n.Substring(0, cloneIdx).TrimEnd();
            if (WatchPrefabs.Contains(n) || DynamicWatch.Contains(n)) return true;
            // Monster stand-in placement ghosts — diagnosing the "monster ghost
            // flashes for a frame then vanishes before it can be placed" report.
            // Logs the stack trace of whatever destroys the ghost.
            if (n.StartsWith(VerdantsAscent.Utilities.VAMonsterStandinFactory.NamePrefix, StringComparison.Ordinal)) return true;
            return false;
        }

        [HarmonyPatch(typeof(ZNetView), "OnDestroy")]
        public static class ZNetView_OnDestroy_Watch_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(ZNetView __instance)
            {
                if (__instance == null) return;
                if (!IsWatched(__instance.gameObject)) return;
                Vector3 pos = __instance.transform != null ? __instance.transform.position : Vector3.zero;
                VaPieces.Log?.LogDebug($"[DestroyWatch] ZNetView.OnDestroy on '{__instance.gameObject.name}' at {pos}");
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Destroy")]
        public static class ZNetScene_Destroy_Watch_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(GameObject go)
            {
                if (go == null) return;
                if (!IsWatched(go)) return;
                Vector3 pos = go.transform != null ? go.transform.position : Vector3.zero;
                VaPieces.Log?.LogDebug($"[DestroyWatch] ZNetScene.Destroy('{go.name}') at {pos}");
            }
        }

        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        public static class WearNTear_Destroy_Watch_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(WearNTear __instance)
            {
                if (__instance == null) return;
                if (!IsWatched(__instance.gameObject)) return;
                Vector3 pos = __instance.transform != null ? __instance.transform.position : Vector3.zero;
                VaPieces.Log?.LogDebug($"[DestroyWatch] WearNTear.Destroy on '{__instance.gameObject.name}' at {pos} (health field={__instance.m_health})");
            }
        }

        [HarmonyPatch(typeof(TreeBase), "RPC_Damage")]
        public static class TreeBase_RPC_Damage_Watch_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(TreeBase __instance, HitData hit)
            {
                if (__instance == null) return;
                if (!IsWatched(__instance.gameObject)) return;
                Vector3 pos = __instance.transform != null ? __instance.transform.position : Vector3.zero;
                float dmg = hit != null ? hit.GetTotalDamage() : 0f;
                VaPieces.Log?.LogDebug($"[DestroyWatch] TreeBase.RPC_Damage on '{__instance.gameObject.name}' at {pos} (total dmg={dmg})");
            }
        }

        [HarmonyPatch(typeof(ZNetView), "Destroy")]
        public static class ZNetView_Destroy_Watch_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(ZNetView __instance)
            {
                if (__instance == null) return;
                if (!IsWatched(__instance.gameObject)) return;
                Vector3 pos = __instance.transform != null ? __instance.transform.position : Vector3.zero;
                VaPieces.Log?.LogDebug($"[DestroyWatch] ZNetView.Destroy(self) on '{__instance.gameObject.name}' at {pos}");
            }
        }
    }
}
