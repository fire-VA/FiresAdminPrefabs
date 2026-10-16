using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using FiresCore.Bridge;

namespace VerdantsAscent.Patches
{
    // Applies FAP's disk tab layout, per-tab ordering and favorites injection to the
    // hammer's per-category piece lists, which BuildUi reads through FAP's own tags
    // (VABuildUiCategoryTags).
    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    public static class PieceTable_UpdateAvailable_VAFilter_Patch
    {
        // Before vanilla distributes pieces into categories, so the layout moves ANY
        // piece on the table — vanilla builtins and other mods' registrations too.
        //
        // Ahead of Infinity Hammer's own UpdateAvailable prefix, which sat at the same default
        // priority so which of the two stamped m_category last came down to registration order.
        // IH's HandleCustomMenuMode runs after this and wins: custom menu mode is something the
        // user turned on deliberately, and the disk tab layout re-applies the moment they leave it.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.HigherThanNormal)]
        public static void Prefix(PieceTable __instance)
        {
            if (__instance != null && HammerCategoryUI.IsOrganizable(__instance))
                VAHammerTabOrganizer.ApplyCategoryOverrides(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix(PieceTable __instance)
        {
            if (__instance == null || PieceTableAccess.ByCategory(__instance) == null) return;
            if (!HammerCategoryUI.IsOrganizable(__instance)) return;

            ApplyOrdering(__instance);
            InjectFavorites(__instance);
            PinToolActionsFirst(__instance);
        }

        private static void ApplyOrdering(PieceTable table)
        {
            bool vanillaFirst = ConfigManager.Instance?.configSortVanillaFirst?.Value ?? false;
            if (!vanillaFirst && !VAHammerTabOrganizer.HasAnyOrderData) return;

            var byCategory = PieceTableAccess.ByCategory(table);
            for (int categoryIndex = 0; categoryIndex < byCategory.Count; categoryIndex++)
                ApplyTabOrdering(byCategory[categoryIndex], vanillaFirst);
        }

        // A favorite is a bookmark, not a move: the piece appears here AND stays in its
        // home tab. UpdateAvailable just rebuilt every list, so this appends fresh each
        // pass, in the Favorites file's line order.
        private static void InjectFavorites(PieceTable table)
        {
            if (VAHammerTabOrganizer.FavoriteCount <= 0) return;
            if (!VACategoryRegistry.TryGet(VAHammerTabOrganizer.FavoritesTabName, out var favoritesCategory)) return;

            int favoritesIndex = (int)favoritesCategory;
            var byCategory = PieceTableAccess.ByCategory(table);
            if (favoritesIndex < 0 || favoritesIndex >= byCategory.Count) return;

            var favorites = byCategory[favoritesIndex];
            var tablePieces = table.m_pieces;
            if (favorites == null || tablePieces == null) return;

            var matched = new List<(Piece piece, int order)>();
            for (int i = 0; i < tablePieces.Count; i++)
            {
                var pieceObject = tablePieces[i];
                if (pieceObject == null) continue;
                if (!VAHammerTabOrganizer.IsFavorite(pieceObject.name)) continue;
                var piece = pieceObject.GetComponent<Piece>();
                if (piece == null || favorites.Contains(piece)) continue;
                VAHammerTabOrganizer.TryGetFavoriteOrder(pieceObject.name, out int order);
                matched.Add((piece, order));
            }
            matched.Sort((a, b) => a.order.CompareTo(b.order));
            for (int i = 0; i < matched.Count; i++) favorites.Add(matched[i].piece);
        }

        // "Equip the hammer and it's on repair" works because GetSelectedPiece resolves
        // to the first category's slot 0 until the player picks something. Injecting
        // thousands of pieces pushes piece_repair out of that slot, so a fresh hammer
        // comes up armed with a real piece. BuildUi lifts repair/remove into its own
        // container wherever they sit, so re-pinning them costs nothing visually.
        private static void PinToolActionsFirst(PieceTable table)
        {
            var byCategory = PieceTableAccess.ByCategory(table);
            for (int categoryIndex = 0; categoryIndex < byCategory.Count; categoryIndex++)
            {
                var pieces = byCategory[categoryIndex];
                if (pieces == null || pieces.Count < 2) continue;

                int writeIndex = 0;
                for (int i = 0; i < pieces.Count; i++)
                {
                    var piece = pieces[i];
                    if (piece == null || (!piece.m_repairPiece && !piece.m_removePiece)) continue;
                    if (i == writeIndex) { writeIndex++; continue; }
                    pieces.RemoveAt(i);
                    pieces.Insert(writeIndex, piece);
                    writeIndex++;
                }
            }
        }

        // Stable in-place ordering: pieces listed in a tab's prefabs.txt sort first by
        // line index, and the unlisted tail keeps its relative order, optionally ranked
        // vanilla-first.
        //
        // Menu entries another mod inserted (Infinity Hammer's Back button and tools)
        // keep their exact indices — sorting them dragged IH's Back button to the
        // bottom of every tab.
        private static void ApplyTabOrdering(List<Piece> pieceList, bool vanillaFirst)
        {
            if (pieceList == null || pieceList.Count < 2) return;

            bool[] pinned = null;
            int pinnedCount = 0;
            for (int i = 0; i < pieceList.Count; i++)
            {
                if (!VerdantsAscent.Utilities.VAExternalMenuPieces.IsMenuPiece(pieceList[i])) continue;
                if (pinned == null) pinned = new bool[pieceList.Count];
                pinned[i] = true;
                pinnedCount++;
            }
            if (pieceList.Count - pinnedCount < 2) return;

            var sortable = new (Piece piece, int listed, int order, int rank, int index)[pieceList.Count - pinnedCount];
            bool anyOutOfOrder = false;
            int count = 0;
            for (int i = 0; i < pieceList.Count; i++)
            {
                if (pinned != null && pinned[i]) continue;

                var piece = pieceList[i];
                string name = piece != null && piece.gameObject != null ? piece.gameObject.name : null;
                int orderIndex = 0;
                bool hasOrder = name != null && VAHammerTabOrganizer.TryGetOrderIndex(name, out orderIndex);
                if (!hasOrder) orderIndex = 0;
                int rank = vanillaFirst && !hasOrder ? SourceRank(piece) : 0;

                sortable[count] = (piece, hasOrder ? 0 : 1, orderIndex, rank, count);
                if (count > 0 && ComparePieces(sortable[count - 1], sortable[count]) > 0)
                    anyOutOfOrder = true;
                count++;
            }
            if (!anyOutOfOrder) return;

            Array.Sort(sortable, ComparePieces);

            for (int i = 0, sorted = 0; i < pieceList.Count; i++)
            {
                if (pinned != null && pinned[i]) continue;
                pieceList[i] = sortable[sorted++].piece;
            }
        }

        private static int ComparePieces(
            (Piece piece, int listed, int order, int rank, int index) a,
            (Piece piece, int listed, int order, int rank, int index) b)
        {
            int byListed = a.listed.CompareTo(b.listed);
            if (byListed != 0) return byListed;
            int byOrder = a.order.CompareTo(b.order);
            if (byOrder != 0) return byOrder;
            int byRank = a.rank.CompareTo(b.rank);
            return byRank != 0 ? byRank : a.index.CompareTo(b.index);
        }

        // 0 = vanilla table builtin or another mod's piece, 1 = FAP-injected vanilla
        // prefab, 2 = FAP-injected bundle/modded prefab.
        private static int SourceRank(Piece piece)
        {
            if (piece == null || piece.gameObject == null) return 0;
            string name = piece.gameObject.name;
            if (!VAVanillaPieceManager.WasInjectedByUs(name)) return 0;
            return VAExternalBundleLoader.TryGetSourceBundle(name, out _) ? 2 : 1;
        }
    }
}
