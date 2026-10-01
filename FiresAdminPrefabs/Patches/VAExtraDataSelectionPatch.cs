using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // The armed field edits are sticky, so the one thing that must be watched is the hammer moving to another
    // piece. FAP has no other hook on SetSelectedPiece. Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    //
    // Player has TWO SetSelectedPiece overloads in 1.0 - (Piece) and (Vector2Int) - so the argument list MUST be
    // given or Harmony throws AmbiguousMatchException out of PatchAll and every patch class after this one in
    // reflection order is silently dropped. Only the Vector2Int overload is hooked: SetSelectedPiece(Piece)
    // resolves the piece to an index and calls it (Player.cs:1075), so this is the single funnel for both.
    [HarmonyPatch(typeof(Player), nameof(Player.SetSelectedPiece), new[] { typeof(Vector2Int) })]
    public static class Player_SetSelectedPiece_VAExtraData
    {
        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance == null || __instance != Player.m_localPlayer) return;
            VerdantsAscent.Utilities.VAExtraDataQueue.OnSelectedPieceChanged(__instance.GetSelectedPiece());
        }
    }
}
