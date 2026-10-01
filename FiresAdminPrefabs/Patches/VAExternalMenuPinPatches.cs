using System.Collections.Generic;
using HarmonyLib;

namespace VerdantsAscent.Patches
{
    // Another mod's menu entries, on every list, where that mod put them. On the Categories and
    // Materials lists FAP's clear brushes follow right behind them; Recent and Favorites keep
    // their own order.
    //
    // 1.0's four piece lists - Categories, Materials, Recent, Favorites - each decide which
    // pieces a tab shows, and every one of them exempts only m_repairPiece/m_removePiece from
    // its filter. Infinity Hammer's Back button is a runtime piece with no usage flags, no
    // recipe and no recent/favorite entry, so all four drop it; the one row that did show it
    // (Show All) walks the m_availablePieces HashSet, which left it last.
    //
    // IH does the same job for its own pipettes through its PinnedPieceList wrapper. These
    // postfixes give the pieces IH placed in EVERY category that same standing. Postfix order
    // matters: IH's wrapper calls the vanilla list first, so it still gets the final say on
    // where its pipettes sit, and the Back button lands right behind them.
    public static class VAExternalMenuPinPatches
    {
        internal static void Pin(PieceTable pieceTable, IList<Piece> resultOut)
            => VerdantsAscent.Utilities.VAExternalMenuPieces.PinToFront(pieceTable, resultOut);

        internal static void PinWithBrushes(PieceTable pieceTable, IList<Piece> resultOut)
        {
            Pin(pieceTable, resultOut);
            VerdantsAscent.Utilities.VAAreaClearBrush.PlaceBrushesAfterMenuIcons(resultOut);
        }

        [HarmonyPatch(typeof(ByUsagePieceList), nameof(ByUsagePieceList.GetAvailablePiecesWithTag))]
        public static class VAPin_ByUsage
        {
            [HarmonyPostfix]
            public static void Postfix(PieceTable pieceTable, IList<Piece> resultOut) => PinWithBrushes(pieceTable, resultOut);
        }

        [HarmonyPatch(typeof(ByMaterialPieceList), nameof(ByMaterialPieceList.GetAvailablePiecesWithTag))]
        public static class VAPin_ByMaterial
        {
            [HarmonyPostfix]
            public static void Postfix(PieceTable pieceTable, IList<Piece> resultOut) => PinWithBrushes(pieceTable, resultOut);
        }

        [HarmonyPatch(typeof(RecentPieceList), nameof(RecentPieceList.GetAvailablePiecesWithTag))]
        public static class VAPin_Recent
        {
            [HarmonyPostfix]
            public static void Postfix(PieceTable pieceTable, IList<Piece> resultOut) => Pin(pieceTable, resultOut);
        }

        [HarmonyPatch(typeof(FavoritePieceList), nameof(FavoritePieceList.GetAvailablePiecesWithTag))]
        public static class VAPin_Favorites
        {
            [HarmonyPostfix]
            public static void Postfix(PieceTable pieceTable, IList<Piece> resultOut) => Pin(pieceTable, resultOut);
        }
    }
}
