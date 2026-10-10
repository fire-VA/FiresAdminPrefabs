using System.Collections;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Whether the server this client joined runs FiresAdminPrefabs.
    //
    // AdminOnlyMode is a server's setting: installed there, FAP enforces it through its synced config and the
    // server's admin list. On a server without FAP nothing enforces it, so FAP runs client-only, for that player's
    // own use, with the admin gate open (Fire, 2026-09-28: "Admin only mode was meant to allow it to be installed on
    // the server so that it can enforce").
    //
    // A server with FAP sends FAP's config package the moment it accepts the join (Core's ConfigSync, from the
    // RPC_PeerInfo postfix), well before the world around the player has loaded. So a client whose FAP ConfigSync
    // is still its own source of truth when the local player first spawns is on a server without FAP. Nothing is
    // decided before that spawn and the gate stays closed as it always was, so a FAP server's AdminOnlyMode never
    // shows the admin hammer to a player whose package is still on its way. Decided once per connection.
    public static class VAFapServerPresence
    {
        private enum State { Unknown, ServerRunsFap, ClientOnly }

        private static State _state;

        public static bool IsClientOnly
        {
            get
            {
                if (_state == State.Unknown && Player.m_localPlayer != null) Decide();
                return _state == State.ClientOnly;
            }
        }

        public static bool ServerRunsFap => _state == State.ServerRunsFap;

        internal static void Hook(ConfigSync configSync)
        {
            if (configSync != null) configSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        }

        // False means the server's FAP config arrived; true is the disconnect restoring the local config.
        private static void OnSourceOfTruthChanged(bool localIsSourceOfTruth)
        {
            if (localIsSourceOfTruth) return;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            if (_state == State.ClientOnly)
                Debug.LogWarning($"{VaPieces.PluginName}: the server's FiresAdminPrefabs config arrived after this client had "
                    + "decided it was client-only. The admin hammer stays as it is until the next join, which applies the server's AdminOnlyMode.");
            _state = State.ServerRunsFap;
        }

        private static void Decide()
        {
            var znet = ZNet.instance;
            if (znet == null || znet.IsServer()) return;    // host or single player: FAP is on the server
            var configSync = VaPieces.Instance != null ? VaPieces.Instance.configSync : null;
            if (configSync == null) return;

            if (!configSync.IsSourceOfTruth)
            {
                _state = State.ServerRunsFap;
                return;
            }

            _state = State.ClientOnly;
            Debug.Log($"{VaPieces.PluginName}: client-only: the server doesn't run FiresAdminPrefabs, so the admin gate is open "
                + "(AdminOnlyMode only applies on servers that run it). Pieces other players could not see (bundle prefabs, "
                + "stand-ins, bakes, blueprints) stay off the build menu, and asset sync and pushes to the server are off.");

            // Deferred a frame: this can be reached from inside the admin check InitializeOnce itself makes.
            if (VaPieces.Instance != null) VaPieces.Instance.StartCoroutine(RunDeferredInit());
        }

        // A registration-only pass for a non-admin may still be running; the full build follows it, as it does after
        // an admin promotion. The menu is rebuilt either way, since client-only also changes what it may show.
        private static IEnumerator RunDeferredInit()
        {
            yield return null;
            while (VAVanillaPieceManager.IsInitializing) yield return null;
            if (ZNet.instance == null) yield break;

            if (!VAVanillaPieceManager.IsInitialized && VAVanillaPieceManager.IsAdminGateOpen())
            {
                Debug.Log($"{VaPieces.PluginName}: client-only - running the deferred InitializeOnce.");
                VAVanillaPieceManager.InitializeOnce();
            }
            if (Player.m_localPlayer != null) VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAFapServerPresence");
            Patches.VABuildUiCategoryTags.RequestBuildMenuRefresh();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned_VAServerPresence_Patch
        {
            private static void Postfix(Player __instance)
            {
                if (_state == State.Unknown && __instance != null && __instance == Player.m_localPlayer) Decide();
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy_VAServerPresence_Patch
        {
            private static void Postfix() => _state = State.Unknown;
        }
    }
}
