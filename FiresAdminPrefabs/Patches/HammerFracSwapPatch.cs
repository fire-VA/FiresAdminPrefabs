using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // Substitutes the whole-mesh "<name>" prefab for a selected "<name>_frac" piece.
    // Both SetupPlacementGhost and PlacePiece read GetSelectedPrefab, so hooking here
    // keeps the ghost preview and the placed result the same object.
    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.GetSelectedPrefab))]
    public static class PieceTable_GetSelectedPrefab_FracSwap_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref GameObject __result)
        {
            if (__result == null) return;

            // Infinity Hammer's SelectedPrefab carries the real name; swapping it would
            // freeze one object and place a different one.
            if (VerdantsAscent.Utilities.VAInfinityHammerCompat.IsInfinityHammerActive()) return;
            if (!ConfigManager.Instance.configHammerSwapFracOnPlace.Value) return;

            if (VAVanillaPieceManager.TryGetNonFracSwap(__result.name, out var basePrefab) && basePrefab != null)
                __result = basePrefab;
        }
    }
}
