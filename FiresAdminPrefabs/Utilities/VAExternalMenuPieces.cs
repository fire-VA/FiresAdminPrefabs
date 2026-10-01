using System;
using System.Collections.Generic;
using FiresCore.Bridge;

namespace VerdantsAscent.Utilities
{
    // Menu entries another mod builds at runtime and places into the piece table itself.
    //
    // Infinity Hammer is the one that matters: CustomMenu.BuildObject/Build create a
    // "_IH_Menu_*" GameObject carrying a BuildMenuTool (a Piece subclass), then IH chooses
    // the slots - tools at the front of their own category, and the Back button at index 0
    // of EVERY category list (UpdateAvailable postfix, verified in IH 1.79 and 1.80.5).
    // Those slots are IH's, so FAP holds them in place instead of sorting them.
    //
    // 1.0 also needs the second half of that intent restored. ByUsagePieceList exempts only
    // repair/remove pieces from its usage-flag filter, and a runtime menu piece has no usage
    // flags, so it survives on Show All alone - and there it renders in m_availablePieces
    // (a HashSet) order, which puts it last. Anything its owner placed in every category is
    // a tool action by that mod's own reckoning, so it gets the repair/remove treatment.
    public static class VAExternalMenuPieces
    {
        private const string IhMenuPrefix = "_IH_Menu_";
        private const string MenuToolTypeName = "BuildMenuTool";

        // IH puts its entries at the head of a list; this caps how far in we look for them.
        private const int LeadingScanCap = 32;

        public static bool IsMenuPiece(Piece piece)
        {
            if (piece == null) return false;
            if (piece.GetType().Name == MenuToolTypeName) return true;
            var go = piece.gameObject;
            return go != null && go.name.StartsWith(IhMenuPrefix, StringComparison.Ordinal);
        }

        // The menu pieces at the head of EVERY non-empty category list, in the order the
        // first such list holds them. Null when there are none, which is the no-IH case.
        public static List<Piece> CollectUniversal(PieceTable table)
        {
            var byCategory = table != null ? PieceTableAccess.ByCategory(table) : null;
            if (byCategory == null) return null;

            List<Piece> universal = null;
            for (int c = 0; c < byCategory.Count; c++)
            {
                var list = byCategory[c];
                if (list == null || list.Count == 0) continue;

                var leading = new List<Piece>();
                for (int i = 0; i < list.Count && leading.Count < LeadingScanCap; i++)
                {
                    var piece = list[i];
                    if (piece == null) continue;
                    if (piece.m_repairPiece || piece.m_removePiece) continue;  // FAP pins these first
                    if (!IsMenuPiece(piece)) break;
                    leading.Add(piece);
                }
                if (leading.Count == 0) return null;                           // missing from a tab = not universal

                if (universal == null) universal = leading;
                else universal.RemoveAll(p => !leading.Contains(p));
                if (universal.Count == 0) return null;
            }

            return universal != null && universal.Count > 0 ? universal : null;
        }

        // Front of the tab, in the owner's order, adding back any the usage filter dropped.
        public static void PinToFront(PieceTable table, IList<Piece> resultOut)
        {
            if (resultOut == null) return;
            var universal = CollectUniversal(table);
            if (universal == null) return;

            for (int i = universal.Count - 1; i >= 0; i--)
            {
                var piece = universal[i];
                int at = resultOut.IndexOf(piece);
                if (at == 0) continue;
                if (at > 0) resultOut.RemoveAt(at);
                resultOut.Insert(0, piece);
            }
        }
    }
}
