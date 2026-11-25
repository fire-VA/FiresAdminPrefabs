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
        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        public static class ZNetView_Awake_VABlueprintGroupRegister_Patch
        {
            private static readonly int s_groupKey = VABlueprintGroupRegistry.ZdoKey.GetStableHashCode();   // 1.1.402: hashed once, not per ZNetView.Awake
            public static void Run(ZNetView __instance)
            {
                try
                {
                    if (__instance == null || !__instance.IsValid()) return;
                    var zdo = __instance.GetZDO();
                    if (zdo == null) return;

                    string groupId = zdo.GetString(s_groupKey, string.Empty);
                    if (string.IsNullOrEmpty(groupId)) return;

                    // Infinity Hammer's ZDO snapshot copies the group id verbatim when
                    // area-selecting over FAP pieces, so an IH copy would self-register
                    // and later be swept by the cascade.
                    if (VAInfinityHammerCompat.IsIhCopyPlacing) return;   // 1.1.206: only an IH copy, not an IH-held hammer

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
        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        public static class ZNetView_Awake_VAScaleReadback_Patch
        {
            public static void Run(ZNetView __instance)
            {
                try
                {
                    if (__instance == null || __instance.m_syncInitialScale) return;
                    if (!__instance.IsValid()) return;
                    var zdo = __instance.GetZDO();
                    if (zdo == null) return;

                    // 1.1.261 (Fire 10-15, Arbby/Con on bri's world): with VanillaFriendly on, show what a vanilla client sees.
                    // Vanilla never reads a stored scale on these prefabs (m_syncInitialScale off), so neither do we. Display only:
                    // the ZDO keeps its scale, and turning VanillaFriendly off brings the look back on the next load.
                    if (VAVanillaFriendlyScale.Skip(__instance, zdo)) return;

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
                    if (VABlueprintPieceMarker.LiveCount == 0) return true;   // 1.1.404: no blueprint pieces alive, no GetComponent per call
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
                    if (VABakeBatchMarker.LiveCount == 0) return true;   // 1.1.404: no baked batches alive, no GetComponent per call
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

            // 1.1.414 ([saddles] 11-10, [worldgen]'s trace of Fire's 30 s "not ready" spawn wait): while this hold returns false, vanilla's
            // FindSpawnPoint never runs, and FindSpawnPoint is what points ZNet's reference position at the spawn (Game.cs:610 / :639 / :661 / :664,
            // 10-28 decompile). The reference stayed at 0,0,0, so the zones around the world origin loaded and the real spawn area loaded only after
            // the hold ended. Each held frame now sets the reference to the point FindSpawnPoint would choose, in its order (:607-664): the logout
            // point if there is one and this is not a respawn after death, else the custom spawn point (bed), else the start location's icon + 2 m up,
            // else 0,0,0. Only SetReferencePosition: no respawn timer, no spawn, no profile change; vanilla's FindSpawnPoint takes over unchanged
            // after the hold, and sets the same point again. Gated on m_requestRespawn exactly as vanilla gates FindSpawnPoint (:808).
            private static readonly AccessTools.FieldRef<Game, PlayerProfile> s_profile = AccessTools.FieldRefAccess<Game, PlayerProfile>("m_playerProfile");
            private static readonly AccessTools.FieldRef<Game, bool> s_requestRespawn = AccessTools.FieldRefAccess<Game, bool>("m_requestRespawn");
            private static readonly AccessTools.FieldRef<Game, bool> s_afterDeath = AccessTools.FieldRefAccess<Game, bool>("m_respawnAfterDeath");
            private static bool _saidReference;

            private static void PointReferenceAtSpawn(Game game)
            {
                if (game == null || ZNet.instance == null || ZoneSystem.instance == null || !s_requestRespawn(game)) return;
                PlayerProfile profile = s_profile(game);
                if (profile == null) return;
                Vector3 point;
                if (!s_afterDeath(game) && profile.HaveLogoutPoint()) point = profile.GetLogoutPoint();                  // :607-610
                else if (profile.HaveCustomSpawnPoint()) point = profile.GetCustomSpawnPoint();                          // :636-639
                else if (ZoneSystem.instance.GetLocationIcon(game.m_StartLocation, out Vector3 pos)) point = pos + Vector3.up * 2f;   // :658-661
                else point = Vector3.zero;                                                                                // :664
                ZNet.instance.SetReferencePosition(point);
                if (!_saidReference)
                {
                    _saidReference = true;
                    VaPieces.Log?.LogDebug($"reference position pointed at spawn during bake hold ({point.x:0}, {point.z:0})");
                }
            }

            [HarmonyPrefix]
            public static bool Prefix(Game __instance)
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
                try { PointReferenceAtSpawn(__instance); }
                catch (Exception ex) { Debug.LogWarning($"[{VaPieces.PluginName}] could not point the reference position at the spawn during the bake hold: {ex.Message}"); }
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
