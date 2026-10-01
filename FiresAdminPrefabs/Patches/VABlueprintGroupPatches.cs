using System;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Wires the blueprint-group "destroy the root, wipe the placement" behaviour into
    // vanilla. The group id rides each piece's ZDO and is rebuilt into in-memory
    // state on ZNetView.Awake.
    //
    // ZNetScene.RemoveObjects and Pickable.SetPicked are deliberately not hooked:
    // zone unload preserves ZDOs, and picking a berry shouldn't take the building.
    public static class VABlueprintGroupPatches
    {
        [HarmonyPatch(typeof(ZNetView), "Awake")]
        public static class ZNetView_Awake_VABlueprintGroupRegister_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ZNetView __instance)
            {
                try
                {
                    if (__instance == null || !__instance.IsValid()) return;
                    var zdo = __instance.GetZDO();
                    if (zdo == null) return;

                    string groupId = zdo.GetString(VABlueprintGroupRegistry.ZdoKey, string.Empty);
                    if (string.IsNullOrEmpty(groupId)) return;

                    // Infinity Hammer's ZDO snapshot copies the group id verbatim when
                    // area-selecting over FAP pieces, so an IH copy would self-register
                    // and later be swept by the cascade.
                    if (VAInfinityHammerCompat.IsInfinityHammerActive()) return;

                    VABlueprintGroupRegistry.Register(groupId, __instance);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegister postfix threw on '{__instance?.name}': {ex.Message}");
                }
            }
        }

        // FAP writes scale through vanilla's own ZDO keys, but vanilla only reads them
        // back when the prefab sets m_syncInitialScale — which most building pieces
        // don't — so scaled pieces snapped to 1.0 on zone reload. Mirrors vanilla's
        // own scale block for the prefabs it skips.
        [HarmonyPatch(typeof(ZNetView), "Awake")]
        public static class ZNetView_Awake_VAScaleReadback_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ZNetView __instance)
            {
                try
                {
                    if (__instance == null || __instance.m_syncInitialScale) return;
                    if (!__instance.IsValid()) return;
                    var zdo = __instance.GetZDO();
                    if (zdo == null) return;

                    Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                    if (scale != Vector3.zero)
                    {
                        __instance.transform.localScale = scale;
                        return;
                    }

                    float uniformScale = zdo.GetFloat(ZDOVars.s_scaleScalarHash, __instance.transform.localScale.x);
                    if (!__instance.transform.localScale.x.Equals(uniformScale))
                        __instance.transform.localScale = new Vector3(uniformScale, uniformScale, uniformScale);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VAScaleReadback postfix threw on '{__instance?.name}': {ex.Message}");
                }
            }
        }

        // Only the bundle ROOT triggers the sibling wipe; without that gate a wilting
        // carrot or a chopped torch would take the rest of the placement with it.
        // Vanilla's parameter name `go` is bound by Harmony and can't be renamed.
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
        public static class ZNetScene_Destroy_VABlueprintGroupCascade_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(ZNetScene __instance, GameObject go)
            {
                try
                {
                    if (__instance == null || go == null) return;

                    var netView = go.GetComponent<ZNetView>();
                    if (netView == null || !netView.IsValid()) return;

                    var zdo = netView.GetZDO();
                    if (zdo == null) return;

                    string groupId = zdo.GetString(VABlueprintGroupRegistry.ZdoKey, string.Empty);
                    if (string.IsNullOrEmpty(groupId)) return;

                    if (go.GetComponent<VABakeParentLoader>() == null) return;

                    int siblingsDestroyed = VABlueprintGroupRegistry.HandleMemberDestroyed(groupId, netView);
                    if (siblingsDestroyed > 0)
                        Debug.Log($"[{VaPieces.PluginName}] Blueprint group '{groupId}' destroyed: trigger='{go.name}' (bundle root), +{siblingsDestroyed} sibling(s) wiped.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupCascade prefix threw on '{go?.name}': {ex.Message}");
                }
            }
        }

        // SpawnBlueprintAt prunes the worn/broken wear-state subtrees, so vanilla's
        // SetHealthVisual would dereference destroyed GameObjects. Skipping it on
        // marked pieces is safe: those visuals can't transition to targets that
        // no longer exist.
        [HarmonyPatch(typeof(WearNTear), "SetHealthVisual")]
        public static class WearNTear_SetHealthVisual_VABlueprintSkip_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(WearNTear __instance)
            {
                try
                {
                    if (__instance == null) return true;
                    return __instance.GetComponent<VABlueprintPieceMarker>() == null;
                }
                catch { return true; }
            }
        }

        // Vanilla's build-mode support highlight would paint an entire baked combined
        // mesh cyan or red. Non-baked pieces keep normal highlighting.
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Highlight))]
        public static class WearNTear_Highlight_VABakedSkip_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(WearNTear __instance)
            {
                try
                {
                    if (__instance == null) return true;
                    return __instance.GetComponent<VABakeBatchMarker>() == null;
                }
                catch { return true; }
            }
        }

        // Registers the synthetic bundle parent and every baked prefab from disk before
        // any ZDO tries to spawn one. The bundle pass is a coroutine hosted on the
        // scene so it yields between bundles instead of locking the main thread for
        // the whole (~100s on big sets) build.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VABakeParentRegister_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ZNetScene __instance)
            {
                try { VABakeParentPrefab.EnsureRegistered(__instance); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABakeParentPrefab.EnsureRegistered threw on ZNetScene.Awake: {ex.Message}");
                }

                try { __instance.StartCoroutine(VABakeBundle.RegisterAllAsync(__instance)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABakeBundle.RegisterAllAsync StartCoroutine threw on ZNetScene.Awake: {ex.Message}");
                }
            }
        }

        // The materialized bake meshes are deliberately KEPT across world unload: a
        // Mesh is a plain asset, not a MonoBehaviour, so holding it causes no main-menu
        // NRE and the next load skips a multi-second re-materialize. The readback
        // copies are not kept — they're keyed by the dead scene's Mesh references.
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
        public static class ZNetScene_Shutdown_VABlueprintGroupClear_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                try { VABlueprintGroupRegistry.ClearAll(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegistry.ClearAll threw on shutdown: {ex.Message}");
                }

                try { VAFapReadableMeshProvider.Clear(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VAFapReadableMeshProvider.Clear threw on shutdown: {ex.Message}");
                }

                try { VABakeTemplateRegistry.ClearAll(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABakeTemplateRegistry.ClearAll threw on shutdown: {ex.Message}");
                }
            }
        }

        // Holds a host's spawn at the loading screen until VABakeBundle has registered every
        // bundle prefab. UpdateRespawn only does spawn logic, so skipping it just delays the
        // spawn and keeps the loading screen up.
        //
        // HOST ONLY: a client that stalls here trips the server's join timeout and gets
        // disconnected. The cap keeps a registration that never finishes from trapping the player.
        [HarmonyPatch(typeof(Game), "UpdateRespawn")]
        public static class Game_UpdateRespawn_VAHoldForBake_Patch
        {
            private const float MaxHoldSeconds = 300f;

            private static float _holdStartTime = -1f;
            private static bool _wasHolding;

            [HarmonyPrefix]
            public static bool Prefix()
            {
                bool isHost;
                try { isHost = ZNet.instance != null && ZNet.instance.IsServer(); }
                catch { isHost = false; }
                if (!isHost)
                {
                    ReleaseHold();
                    return true;
                }

                if (VABakeBundle.BakeComplete)
                {
                    if (_wasHolding)
                    {
                        float held = _holdStartTime >= 0f ? Time.realtimeSinceStartup - _holdStartTime : 0f;
                        Debug.Log($"[{VaPieces.PluginName}] Baked-mesh bundles registered after {held:F1}s held at the loading screen — releasing spawn.");
                    }
                    ReleaseHold();
                    return true;
                }

                float now = Time.realtimeSinceStartup;
                if (_holdStartTime < 0f)
                {
                    _holdStartTime = now;
                    Debug.Log($"[{VaPieces.PluginName}] Holding spawn at the loading screen while baked-mesh bundles register.");
                }
                _wasHolding = true;

                if (now - _holdStartTime >= MaxHoldSeconds)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] Bundle registration hold cap ({MaxHoldSeconds:F0}s) reached — releasing spawn before registration finished.");
                    ReleaseHold();
                    return true;
                }
                return false;
            }

            private static void ReleaseHold()
            {
                _holdStartTime = -1f;
                _wasHolding = false;
            }
        }
    }
}
