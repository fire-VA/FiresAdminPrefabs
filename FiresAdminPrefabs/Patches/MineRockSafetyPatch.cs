using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // Guards MineRock.UpdateVisability against a null ZNetView or ZDO so the
    // InvokeRepeating loop vanilla's Start schedules can't NRE-spam on ghosts,
    // mid-destroy ticks, or half-initialized clones.
    [HarmonyPatch(typeof(MineRock), "UpdateVisability")]
    public static class MineRock_UpdateVisability_VAGuard_Patch
    {
        private static readonly AccessTools.FieldRef<MineRock, ZNetView> _netViewRef =
            AccessTools.FieldRefAccess<MineRock, ZNetView>("m_nview");

        [HarmonyPrefix]
        public static bool Prefix(MineRock __instance)
        {
            var netView = _netViewRef(__instance);
            if (netView == null || !netView.IsValid()) return false;
            return netView.GetZDO() != null;
        }
    }

    // Strips MineRock from player-placed clones so the visibility loop never
    // schedules. World-generated spawns have creator 0, so dungeon Leviathans and
    // the like still mine normally.
    [HarmonyPatch(typeof(MineRock), "Start")]
    public static class MineRock_Start_VAStrip_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(MineRock __instance)
        {
            if (__instance == null) return true;
            var piece = __instance.GetComponent<Piece>();
            if (piece == null) return true;
            if (!piece.IsPlacedByPlayer()) return true;

            UnityEngine.Object.Destroy(__instance);
            if (ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: Stripped MineRock from player-placed '{piece.gameObject.name}' (creator={piece.GetCreator()})");
            return false;
        }
    }

    // True through the body of Player.PlacePiece, so Awake-time patches firing
    // inside Instantiate can tell a fresh local placement from a reload — on a new
    // placement SetCreator has not written m_creator to the ZDO yet.
    internal static class VAPlayerPlacementContext
    {
        public static bool IsPlacing;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class Player_PlacePiece_VAPlacementContext_Patch
    {
        // Never flags an Infinity Hammer placement, so the strip below can't gut an
        // IH-relocated fractured prefab.
        // 1.1.206 (Fire 10-09: "make the mods work together properly"): only an IH COPY (a selection clone) is IH's to protect;
        // a FAP piece picked from IH's Builds page is the registered prefab itself and gets FAP's normal placement.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Piece piece)
        {
            VerdantsAscent.Utilities.VAInfinityHammerCompat.IsIhCopyPlacing =
                VerdantsAscent.Utilities.VAInfinityHammerCompat.IsInfinityHammerActive() && !VerdantsAscent.Utilities.VAInfinityHammerCompat.IsRegisteredPrefab(piece);
            VAPlayerPlacementContext.IsPlacing = !VerdantsAscent.Utilities.VAInfinityHammerCompat.IsIhCopyPlacing;
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            VAPlayerPlacementContext.IsPlacing = false;
            VerdantsAscent.Utilities.VAInfinityHammerCompat.IsIhCopyPlacing = false;
        }
    }

    // Strips MineRock5 from player-placed fractured prefabs (Ashlands walls,
    // MineRock_Meteorite) so vanilla's UpdateMesh → CombineMeshes never runs over
    // their non-readable cell meshes and spams warnings on every placement.
    //
    // The placement flag is needed because m_creator is still 0 during Awake on a
    // fresh placement; on reload the flag is false but the ZDO has the creator.
    [HarmonyPatch(typeof(MineRock5), "Awake")]
    public static class MineRock5_Awake_VAStrip_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(MineRock5 __instance)
        {
            if (__instance == null) return true;
            var piece = __instance.GetComponent<Piece>();
            if (piece == null) return true;
            if (!VAPlayerPlacementContext.IsPlacing && !piece.IsPlacedByPlayer())
                return true;

            UnityEngine.Object.Destroy(__instance);
            if (ConfigManager.Instance.configVerboseLogging.Value)
            {
                string reason = VAPlayerPlacementContext.IsPlacing
                    ? "placement-context"
                    : $"creator={piece.GetCreator()}";
                Debug.Log($"{VaPieces.PluginName}: Stripped MineRock5 from player-placed '{piece.gameObject.name}' ({reason}) — silences CombineMeshes spam on fractured prefabs");
            }
            return false;
        }
    }

    // Native Unity warnings bypass Debug.unityLogger.logHandler entirely, so they
    // have to be caught at the BepInEx source that forwards them. Resolved by
    // reflection to avoid a hard reference on BepInEx internals.
    //
    // Navmesh/combine spam is per-mesh per-bake — hundreds of lines on a populated
    // world — and every mesh shipping Read/Write off produces it.
    [HarmonyPatch]
    public static class VABepInExUnityLogPatch
    {
        public static bool Prepare()
        {
            try { return ResolveMethod() != null; }
            catch { return false; }
        }

        public static MethodBase TargetMethod() => ResolveMethod();

        private static MethodInfo ResolveMethod()
        {
            var logSourceType = AccessTools.TypeByName("BepInEx.Logging.UnityLogSource");
            if (logSourceType == null) return null;
            return AccessTools.Method(logSourceType, "OnUnityLogMessageReceived",
                new Type[] { typeof(string), typeof(string), typeof(LogType) });
        }

        public static bool Prefix(string message, LogType type)
        {
            if (string.IsNullOrEmpty(message)) return true;
            if (type != LogType.Warning && type != LogType.Error) return true;

            if (Contains(message, "Failed to find expected binary shader data"))
                return false;
            if (Contains(message, "RuntimeNavMeshBuilder: Source mesh") && Contains(message, "does not allow read access"))
                return false;
            if (Contains(message, "Cannot combine mesh that does not allow access"))
                return false;

            return true;
        }

        private static bool Contains(string message, string fragment) =>
            message.IndexOf(fragment, StringComparison.Ordinal) >= 0;
    }
}
