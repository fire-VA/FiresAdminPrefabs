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

    // 1.1.314 (Fire 10-23, Tool Swapper on copper: "make hammer-placed copper mineable"): player-placed MineRock clones are NO
    // LONGER stripped. The strip was there so vanilla's UpdateVisability loop (scheduled in Start) could not run on a placed
    // clone; the guard above already stops that loop from touching a missing ZNetView / ZDO, which was its only hazard. A placed
    // MineRock (copper, tin, ...) keeps its hit areas and RPC_Hit, so it takes pickaxe damage and drops its ore like a
    // world-generated one.

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

    // Player-placed fractured prefabs (Ashlands walls, MineRock_Meteorite) spammed warnings on every placement: vanilla's
    // UpdateMesh runs CombineMeshes over each hit area's cell mesh, and those cell meshes are not readable. 1.1.314 (Fire 10-23:
    // "strip only what caused it, from the cause"): MineRock5 is kept - so a placed ore rock is mineable - unless one of its
    // cell meshes really is non-readable, which is the one thing CombineMeshes cannot do. Only then is it stripped, as before,
    // with the mesh named in the log line. World-generated rocks are untouched either way.
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

            string unreadable = FirstUnreadableCellMesh(__instance);
            if (unreadable == null) return true;   // every cell mesh combines: keep MineRock5, the rock stays mineable

            UnityEngine.Object.Destroy(__instance);
            string reason = VAPlayerPlacementContext.IsPlacing ? "placement-context" : $"creator={piece.GetCreator()}";
            if (ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: Stripped MineRock5 from player-placed '{piece.gameObject.name}' ({reason}): cell mesh '{unreadable}' is not readable, so CombineMeshes cannot run on it");
            return false;
        }

        // The first hit-area cell mesh (a collider's MeshFilter, as MineRock5.Awake gathers them) that is not readable, or null.
        private static string FirstUnreadableCellMesh(MineRock5 rock)
        {
            foreach (var collider in rock.GetComponentsInChildren<Collider>())
            {
                var filter = collider.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh != null && !mesh.isReadable) return mesh.name;
            }
            return null;
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
            var logSourceType = global::VerdantsAscent.Utilities.QuietTypes.Find("BepInEx.Logging.UnityLogSource");
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
