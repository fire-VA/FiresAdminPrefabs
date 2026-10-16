using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Bridge;
using FiresCore.Pieces;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Puts FAP's tabs on the 1.0 build menu's left-hand tag list, as rows of Core's BuildMenuTabs.
    //
    // 1.0 dropped PieceCategory tabs, and FAP's pieces default to m_usage 0, so all ~40 FAP tabs would collapse into
    // Show All. Core appends named rows after vanilla's usage tags; this source names FAP's rows for the vanilla hammer's
    // table and fills each from its category. The row key is the PieceCategory value, so a tag id is
    // BuildMenuTabs.TagIdBase + category, exactly as before the move to Core (2026-09-27).
    internal sealed class VABuildUiCategoryTags : BuildMenuTabs.ISource
    {
        private static readonly VABuildUiCategoryTags s_source = new VABuildUiCategoryTags();

        internal static void Register()
        {
            BuildMenuTabs.Register(s_source);
            SharedPieceGroups.Publish(SharedPieceGroups.Clutter, ClutterTabPieces);
            SharedPieceGroups.PublishBiomes(SharedPieceGroups.Clutter, ClutterBiomes);
        }

        // Where a clutter stand-in's clutter grows, so the Chaos Hammer's Biomes tab can sort it. The clear brush and
        // anything not in the clutter registry belong to no biome.
        private static Heightmap.Biome ClutterBiomes(GameObject piece)
        {
            string clutterName = VAClutterStandinFactory.ResolveRealNameFromStandin(piece);
            return clutterName != null && VAClutterRegistry.TryGet(clutterName, out var clutter) && clutter != null
                ? clutter.m_biome
                : Heightmap.Biome.None;
        }

        // Everything on the vanilla hammer's Clutter tab, lent to FAT's Chaos Hammer through Core. The place patch
        // (HammerClutterPaintPatch) recognises these by name, so they work from any table.
        private static IEnumerable<GameObject> ClutterTabPieces()
        {
            var table = HammerPieces.HammerTable();
            if (table == null || !VACategoryRegistry.TryGet(VACategoryRegistry.CatClutter, out var clutter)) yield break;
            foreach (var prefab in table.m_pieces)
            {
                var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece != null && piece.m_category == clutter) yield return prefab;
            }
        }

        internal static int TagIdFor(int category) => BuildMenuTabs.TagIdFor(category);

        internal static bool TryGetTabName(int tagId, out string tabName)
        {
            tabName = null;
            return tagId >= BuildMenuTabs.TagIdBase
                && VACategoryRegistry.TryGetName((Piece.PieceCategory)(tagId - BuildMenuTabs.TagIdBase), out tabName);
        }

        internal static void RequestBuildMenuRefresh() => BuildMenuTabs.RequestRefresh();

        public bool Owns(PieceTable table) => HammerCategoryUI.IsOrganizable(table);

        public void CollectTabs(PieceTable table, List<BuildMenuTabs.Tab> into)
        {
            var byCategory = PieceTableAccess.ByCategory(table);
            if (byCategory == null) return;

            // "The missing ones" literally: a FAP tab whose name already reads the same as a vanilla usage tag on the
            // list would just be a duplicate row.
            var alreadyListed = new HashSet<string>(BuildMenuTabs.ListedVanillaTags, StringComparer.OrdinalIgnoreCase);
            var rows = new List<BuildMenuTabs.Tab>();
            foreach (var name in VACategoryRegistry.AllNames())
            {
                // Favorites has its own top-level tab in 1.0 and FAP's right-click favorites already mirror into it,
                // so a second Favorites row here would be the same set twice.
                if (string.Equals(name, VAHammerTabOrganizer.FavoritesTabName, StringComparison.OrdinalIgnoreCase)) continue;
                if (VAHammerTabOrganizer.IsTabHidden(name)) continue;
                if (!VACategoryRegistry.TryGet(name, out var category)) continue;
                // 1.1.210 (Fire 10-10: devcommands ON under Infinity Hammer's tool listed every raw bundle tab, "clutter"
                // beside "Clutter"): a per-bundle tab is a row only when SortHammerByMod files pieces into it, in any mode.
                if (IsRawBundleTab(name)) continue;

                int index = (int)category;
                if (index < 0 || index >= byCategory.Count) continue;
                var pieces = byCategory[index];
                if (pieces == null || pieces.Count == 0) continue;          // empty tab = no row, same as vanilla
                // 1.1.208 (Fire 10-10: under Infinity Hammer's tool the hidden / dev-only tabs and both item-stand halves showed):
                // the same view rules the menu pass prunes with, applied to the row itself.
                if (!VABuildMenuVisibility.RowVisible(category, pieces)) continue;

                // A row whose list holds only tool actions (Core lifts repair/remove into the menu's own buttons) or
                // destroyed pieces would open empty, so it is no row either: listed exactly when Fill has a piece.
                if (!HasShowablePiece(pieces))
                {
                    ReportOnce(name, "left out: nothing in it a player can pick", pieces);
                    continue;
                }
                if (!VAVanillaPieceManager.HasBuiltHammerMenu && VACategoryRegistry.IsFapTab(name) && HoldsUnpublishedPiece(pieces))
                    ReportOnce(name, "listed although FAP's hammer init never ran (admin gate closed)", pieces);

                string label = VAHammerTabOrganizer.TryGetDisplayName(name, out var displayName) ? displayName : name;
                if (alreadyListed.Contains(BuildMenuTabs.Localize(label))) continue;
                rows.Add(new BuildMenuTabs.Tab(label, index));
                rowNames[index] = name;
            }
            rows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));
            // 1.1.234: a category's sub-filters (VACategoryRegistry.AddSubFilter) follow its row, indented, each only when
            // it matches a showable piece.
            s_subKeys.Clear();
            foreach (var row in rows)
            {
                into.Add(row);
                if (!rowNames.TryGetValue(row.Key, out var rowName)) continue;
                var filters = VACategoryRegistry.SubFilters(rowName);
                // 1.1.271 (Fire 10-15: "the left list shows only Dungeons; the top-right sort tab brings up the sub-sections"): sub-filter
                // rows are never drawn on the left list; the rig cfg had the old [Diagnostics] BuildMenuSubFilterRows key on (15 rows).
                if (filters == null || !LeftListSubRows) continue;
                for (int i = 0; i < filters.Count && s_subKeys.Count < MaxSubRows; i++)
                {
                    if (!AnyMatch(byCategory[row.Key], filters[i].Value)) continue;
                    s_subKeys.Add(new KeyValuePair<int, int>(row.Key, i));
                    into.Add(new BuildMenuTabs.Tab("    » " + filters[i].Key, SubKeyBase + s_subKeys.Count - 1));
                }
            }
            rowNames.Clear();
            // 1.1.237 (the lead, build-menu crash): what FAP hands the menu at each tag rebuild.
            try { VaPieces.Log?.LogDebug($"[FAP] tag rows for '{table?.name}': {into.Count} row(s), {s_subKeys.Count} sub-filter row(s), tag ids " + string.Join(",", into.Select(t => BuildMenuTabs.TagIdFor(t.Key).ToString()).ToArray())); } catch { }
        }

        // 1.1.237 (the build-menu crash bisect): sub-filter rows get small dense keys right after the category keys (tag ids
        // 101000+, not 150000+), at most MaxSubRows, and only with [Diagnostics] BuildMenuSubFilterRows on (default off until proven).
        private const int SubKeyBase = 1000, MaxSubRows = 64;
        private const bool LeftListSubRows = false;   // 1.1.271: retired with the BuildMenuSubFilterRows key
        private static readonly List<KeyValuePair<int, int>> s_subKeys = new List<KeyValuePair<int, int>>();
        private static readonly Dictionary<int, string> rowNames = new Dictionary<int, string>();

        private static bool Matches(Func<Piece, bool> match, Piece piece)
        {
            try { return match(piece); } catch { return false; }
        }

        private static bool AnyMatch(List<Piece> pieces, Func<Piece, bool> match)
        {
            if (pieces == null) return false;
            foreach (var p in pieces) if (IsShowable(p) && VABuildMenuVisibility.PieceVisible(p) && Matches(match, p)) return true;
            return false;
        }

        public void Fill(PieceTable table, int key, IList<Piece> into)
        {
            var byCategory = PieceTableAccess.ByCategory(table);
            if (byCategory == null) return;
            Func<Piece, bool> filter = null;
            if (key >= SubKeyBase)
            {
                int n = key - SubKeyBase;
                if (n < 0 || n >= s_subKeys.Count) return;
                int category = s_subKeys[n].Key, f = s_subKeys[n].Value;
                if (!VACategoryRegistry.TryGetName((Piece.PieceCategory)category, out var catName)) return;
                var filters = VACategoryRegistry.SubFilters(catName);
                if (filters == null || f >= filters.Count) return;
                filter = filters[f].Value;
                key = category;
            }
            if (key < 0 || key >= byCategory.Count) return;
            var pieces = byCategory[key];
            if (pieces == null) return;
            if (filter != null)
            {
                foreach (var piece in pieces)
                    if (IsShowable(piece) && VABuildMenuVisibility.PieceVisible(piece) && Matches(filter, piece)) into.Add(piece);
                return;
            }
            // 1.1.208: the same view rules per piece, so a visible tab never shows a hidden piece (Infinity Hammer's tool).
            foreach (var piece in pieces)
                if (IsShowable(piece) && VABuildMenuVisibility.PieceVisible(piece)) into.Add(piece);
        }

        private static bool IsRawBundleTab(string name)
        {
            if (ConfigManager.Instance != null && ConfigManager.Instance.configSortHammerByMod.Value) return false;
            foreach (var bundleName in VAExternalBundleLoader.LoadedBundleNames)
                if (string.Equals(bundleName, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsShowable(Piece piece) => piece != null && !piece.m_repairPiece && !piece.m_removePiece;

        private static bool HasShowablePiece(List<Piece> pieces)
        {
            foreach (var piece in pieces)
                if (IsShowable(piece)) return true;
            return false;
        }

        // Pieces an admin published for players (PlayerPieces.yaml) are the one FAP content a closed gate lets onto a
        // player's rows.
        private static bool HoldsUnpublishedPiece(List<Piece> pieces)
        {
            foreach (var piece in pieces)
                if (IsShowable(piece) && !VAFapPlayerPieces.IsPublished(piece.gameObject.name)) return true;
            return false;
        }

        // A player on a server where FAP's admin gate stayed closed reported an empty CreatorShop row (FAP 1.1.134,
        // 2026-09-28). Apart from published pieces, nothing FAP runs with the gate closed puts a piece in its own
        // categories, so whatever filled that list is named here, once per row per session, for the next log to show.
        private const int ReportedPieceCount = 5;
        private static readonly HashSet<string> s_reportedRows = new HashSet<string>(StringComparer.Ordinal);

        private static void ReportOnce(string name, string what, List<Piece> pieces)
        {
            if (!s_reportedRows.Add(name + "|" + what)) return;
            var described = new List<string>();
            foreach (var piece in pieces)
            {
                if (described.Count == ReportedPieceCount) break;
                described.Add(piece == null
                    ? "(destroyed)"
                    : $"{piece.gameObject.name} (category {(int)piece.m_category}{(piece.m_repairPiece ? ", repair" : "")}{(piece.m_removePiece ? ", remove" : "")})");
            }
            Debug.LogWarning($"{VaPieces.PluginName}: build menu row '{name}' {what}. Its list holds {pieces.Count} entr{(pieces.Count == 1 ? "y" : "ies")}: "
                + $"{string.Join(", ", described)}{(pieces.Count > described.Count ? ", ..." : "")}.");
        }

    }

    // Showing the Blueprints tag releases the blueprint stub icons that world load leaves unrendered.
    // Reads the tag BuildUi settled on, because SetCurrentTag falls back to Show All when no button has the id.
    [HarmonyPatch(typeof(BuildUi), "OnSelectTag")]
    public static class BuildUi_OnSelectTag_VABlueprintDefer_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(int ___m_currentTagId)
        {
            if (VACategoryRegistry.TryGet(VACategoryRegistry.CatBlueprints, out var blueprintCategory)
                && ___m_currentTagId == VABuildUiCategoryTags.TagIdFor((int)blueprintCategory))
            {
                VABlueprintHammerIntegration.OnBlueprintsTabOpened();
            }
        }
    }
}
