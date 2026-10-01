using System;
using HarmonyLib;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Opens one undo scope around vanilla's PlacePiece so every ZNetView spawned
    // inside it lands in a single World Edit Commands undo record. FAP's own spawn
    // paths open their own inner scopes, so this outer one just closes empty for them.
    //
    // Priority First/Last wraps every other mod's patches; a finalizer (not a postfix)
    // closes the scope even when a prefix skipped vanilla or vanilla threw.
    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class Player_PlacePiece_VAUndoBracket
    {
        // Infinity Hammer runs its own undo sub-action; a second outer scope would fight it.
        [HarmonyPriority(HarmonyLib.Priority.First)]
        [HarmonyPrefix]
        public static void Prefix(out IDisposable __state)
        {
            // The placer already has its copy before the ZDO is written, so queued values go onto the PREFAB for
            // the length of this call and are restored by the finalizer below.
            __state = VAPlacementScope.Combine(
                VAInfinityHammerCompat.IsInfinityHammerActive() ? null : VAUndoBridge.Scope(),
                VAExtraDataStamp.BeginPrefabSwap());
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        [HarmonyFinalizer]
        public static void Finalizer(IDisposable __state)
        {
            try { __state?.Dispose(); } catch { }
        }
    }
}
