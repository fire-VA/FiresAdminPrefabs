using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using FiresCore.Bridge;

namespace VerdantsAscent.Patches
{
    // Adds hammer categories beyond vanilla's fixed enum. Custom values are allocated
    // between PieceCategory.Max and PieceCategory.All so they collide with neither; the
    // build menu shows them as tags through VABuildUiCategoryTags.

    public static class VACategoryRegistry
    {
        public const string CatNature = "Nature";
        public const string CatCreatorShop = "CreatorShop";
        public const string CatBlueprints = "Blueprints";
        public const string CatClutter = "Clutter";
        public const string CatFX  = "FX";
        public const string CatVFX = "VFX";
        public const string CatItemStandsV = "Item Stands (V)";
        public const string CatItemStandsH = "Item Stands (H)";
        public const string CatMonsters = "Monsters";
        public const string CatBakedMeshes = "Baked Meshes";

        private static readonly Dictionary<string, Piece.PieceCategory> _byName =
            new Dictionary<string, Piece.PieceCategory>(StringComparer.Ordinal);

        // Max itself is vanilla's "no selection" sentinel, so allocation starts above it.
        private static int _nextCategoryValue = VerdantsAscent.Utilities.VAPieceCategoryRuntime.Max + 1;

        public static bool TryGet(string name, out Piece.PieceCategory category)
            => _byName.TryGetValue(name, out category);

        public static List<string> AllNames()
            => new List<string>(_byName.Keys);

        // Categories whose tab the user hid. The enum value is still allocated for them, so a
        // piece can carry one; the build menu just never shows it.
        public static void CollectHiddenCategories(HashSet<Piece.PieceCategory> into)
        {
            if (into == null) return;
            foreach (var entry in _byName)
            {
                if (VAHammerTabOrganizer.IsTabHidden(entry.Key)) into.Add(entry.Value);
            }
        }

        public static bool TryGetName(Piece.PieceCategory category, out string name)
        {
            foreach (var entry in _byName)
            {
                if (entry.Value == category)
                {
                    name = entry.Key;
                    return true;
                }
            }
            name = null;
            return false;
        }

        public static Piece.PieceCategory GetOrAdd(string name)
        {
            if (_byName.TryGetValue(name, out var existing)) return existing;
            var value = (Piece.PieceCategory)(_nextCategoryValue++);
            _byName[name] = value;
            return value;
        }

        // FAP's own categories, in tab order. Every one is ALLOCATED regardless of
        // config, because a category's enum value must never depend on the toggle state
        // at boot: booting vanilla-friendly used to skip Clutter's GetOrAdd entirely,
        // which shifted every later category down one and then handed Clutter a value
        // after all the bundle tabs the moment someone toggled the flag back off.
        private static readonly string[] OwnCategories =
        {
            CatNature, CatClutter, CatCreatorShop, CatBlueprints, CatFX, CatVFX,
            CatItemStandsV, CatItemStandsH, CatMonsters, CatBakedMeshes,
        };

        // FAP's own tabs, as opposed to the categories other mods allocate here by name (Dungeon Sections, the
        // Mausoleum's, Fires NPCs'): the fixed set above and one per loaded bundle.
        public static bool IsFapTab(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (Array.IndexOf(OwnCategories, name) >= 0) return true;
            foreach (var bundleName in VAExternalBundleLoader.LoadedBundleNames)
                if (string.Equals(bundleName, name, StringComparison.Ordinal)) return true;
            return false;
        }

        // Clutter and the per-bundle tabs are the modded-content tabs vanilla-friendly
        // mode keeps off the table; the prefabs themselves still load either way.
        private static bool IsModdedContentTab(string name)
            => string.Equals(name, CatClutter, StringComparison.Ordinal);

        public static void RegisterAll(PieceTable pieceTable) => SyncCategories(pieceTable);

        // Reconciles m_categories against the CURRENT config. Idempotent. Unlike the
        // add-only pass this replaced, it also REMOVES a tab that should no longer be
        // visible, which is what makes a live VanillaFriendly flip work in both
        // directions: add-only could never make the Clutter tab appear when the flag
        // was turned off mid-session, since nothing re-ran registration and nothing
        // could drop a tab either. A tab restored mid-session is appended, so it sits
        // at the end of the row until the next boot puts it back in declared order.
        public static void SyncCategories(PieceTable pieceTable)
        {
            if (pieceTable == null) return;

            var config = ConfigManager.Instance;
            bool vanillaFriendly = config != null
                && config.configVanillaFriendly != null
                && config.configVanillaFriendly.Value;

            foreach (var name in OwnCategories) Allocate(pieceTable, name);

            var bundleTabs = new List<string>();
            foreach (var bundleName in VAExternalBundleLoader.LoadedBundleNames)
            {
                if (string.IsNullOrEmpty(bundleName)) continue;
                if (VAHammerTabOrganizer.TryGetBundleAlias(bundleName, out _)) continue;
                Allocate(pieceTable, bundleName);
                bundleTabs.Add(bundleName);
            }

            foreach (var name in OwnCategories)
                SetVisible(pieceTable, name,
                    !VAHammerTabOrganizer.IsTabHidden(name)
                    && !(vanillaFriendly && IsModdedContentTab(name)));

            foreach (var name in bundleTabs)
                SetVisible(pieceTable, name,
                    !VAHammerTabOrganizer.IsTabHidden(name) && !vanillaFriendly);

            ReseedSelectedCategory(pieceTable);
        }

        // The enum value is always allocated and m_availablePieces always grown, even
        // for a tab the user hid, so a piece pinned to that category can't index past
        // the end. Hidden tabs simply stay out of m_categories.
        private static void Allocate(PieceTable table, string name)
        {
            var category = GetOrAdd(name);
            int needed = (int)category + 1;
            while (PieceTableAccess.ByCategory(table).Count < needed)
                PieceTableAccess.ByCategory(table).Add(new List<Piece>());
        }

        private static void SetVisible(PieceTable table, string name, bool visible)
        {
            var category = GetOrAdd(name);

            if (!visible) { RemoveTab(table, category); return; }

            if (table.m_categories.Contains(category)) return;
            table.m_categories.Add(category);
            table.m_categoryLabels.Add(
                VAHammerTabOrganizer.TryGetDisplayName(name, out var displayName) ? displayName : name);
        }

        // Restores a tab by category alone, resolving its label the way SetVisible does.
        // Needed because not every FAP category comes from SyncCategories: Favorites, the
        // user's own tab folders and other mods' categories (Mausoleum, Dungeon Sections,
        // Fires NPCs) are registered elsewhere, so a pass that removed one has to be able
        // to put it back itself rather than waiting for a registration it never gets.
        public static bool AddTab(PieceTable table, Piece.PieceCategory category)
        {
            if (table == null || table.m_categories == null) return false;
            if (table.m_categories.Contains(category)) return true;
            if (!TryGetName(category, out var name)) return false;

            table.m_categories.Add(category);
            table.m_categoryLabels.Add(
                VAHammerTabOrganizer.TryGetDisplayName(name, out var displayName) ? displayName : name);
            return true;
        }

        // True for a category FAP allocated. Vanilla's own tabs are vanilla's business:
        // it already leaves Feasts/Food/Meads off the hammer table itself.
        public static bool IsOwnCategory(Piece.PieceCategory category)
        {
            foreach (var entry in _byName)
                if (entry.Value == category) return true;
            return false;
        }

        // m_categoryLabels runs parallel to m_categories, so a removal has to drop the
        // label at the same index or every later tab renders under the wrong name.
        public static void RemoveTab(PieceTable table, Piece.PieceCategory category)
        {
            if (table == null || table.m_categories == null) return;
            int index = table.m_categories.IndexOf(category);
            if (index < 0) return;
            table.m_categories.RemoveAt(index);
            if (table.m_categoryLabels != null && index < table.m_categoryLabels.Count)
                table.m_categoryLabels.RemoveAt(index);
        }

        // m_selectedCategory holds a value, not an index, and vanilla re-seeds it only when
        // it reads Max or All (PieceTable.cs:148) - so a tab removed while the player was
        // standing on it leaves the menu pointed at a category that has no tab.
        public static void ReseedSelectedCategory(PieceTable table)
        {
            if (table == null || table.m_categories == null || table.m_categories.Count == 0) return;
            try
            {
                if (!table.m_categories.Contains(table.GetSelectedCategory()))
                    table.SetCategory(table.m_categories[0]);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: could not re-seed the selected build category: {ex.Message}");
            }
        }
    }

    // Grows the three category-indexed structures on a PieceTable before vanilla
    // indexes them. Vanilla's UpdateAvailable creates exactly Max lists and only
    // when the table has none; Valheim 1.0 raised Max from 8 to 9 (DeepNorth), and
    // a table pre-grown to the old 8 threw on every All-category piece.
    internal static class VAPieceTableGuard
    {
        private static int VanillaCategorySlotCount => VerdantsAscent.Utilities.VAPieceCategoryRuntime.Max;

        private static bool _hasLoggedFirstGrow;

        // scanPieces: also size for every piece's own category even when m_categories is already covered. Only the
        // UpdateAvailable prefix asks for it (the per-frame GetPiece prefixes would rescan the 5k-piece hammer table): FAT's
        // Chaos Hammer carries FAP's lent clutter pieces, whose Clutter TAB category its m_categories never lists, and vanilla
        // then indexed past the lists on every menu refresh (Fire 10-03: "the chaos hammer menu seems completely broken").
        public static void EnsureGrown(PieceTable table, bool scanPieces = false)
        {
            if (table == null || PieceTableAccess.ByCategory(table) == null) return;

            int needed = VanillaCategorySlotCount;
            var categories = table.m_categories;
            if (categories != null)
            {
                for (int i = 0; i < categories.Count; i++)
                {
                    int categoryValue = (int)categories[i];
                    if (categoryValue >= (int)Piece.PieceCategory.All) continue;
                    if (categoryValue + 1 > needed) needed = categoryValue + 1;
                }
            }

            if (!scanPieces && PieceTableAccess.ByCategory(table).Count >= needed
                && table.m_selectedPiece != null && table.m_selectedPiece.Length >= needed
                && table.m_lastSelectedPiece != null && table.m_lastSelectedPiece.Length >= needed)
                return;

            // A piece's own m_category can exceed anything m_categories declared.
            var pieces = table.m_pieces;
            if (pieces != null)
            {
                for (int i = 0; i < pieces.Count; i++)
                {
                    var pieceObject = pieces[i];
                    if (pieceObject == null) continue;
                    var piece = pieceObject.GetComponent<Piece>();
                    if (piece == null) continue;
                    int categoryValue = (int)piece.m_category;
                    if (categoryValue >= (int)Piece.PieceCategory.All) continue;
                    if (categoryValue + 1 > needed) needed = categoryValue + 1;
                }
            }

            int previousCount = PieceTableAccess.ByCategory(table).Count;
            while (PieceTableAccess.ByCategory(table).Count < needed)
                PieceTableAccess.ByCategory(table).Add(new List<Piece>());

            GrowVector2IntArray(ref table.m_selectedPiece, needed);
            GrowVector2IntArray(ref table.m_lastSelectedPiece, needed);

            if (!_hasLoggedFirstGrow && PieceTableAccess.ByCategory(table).Count > previousCount)
            {
                _hasLoggedFirstGrow = true;
                Debug.Log($"{VaPieces.PluginName}: PieceTableAccess.ByCategory(PieceTable) grown from {previousCount} to {PieceTableAccess.ByCategory(table).Count} (max category in use: {needed - 1}); m_selectedPiece/m_lastSelectedPiece sized to {needed}");
            }
        }

        private static void GrowVector2IntArray(ref Vector2Int[] array, int needed)
        {
            if (array == null)
            {
                array = new Vector2Int[needed];
                return;
            }
            if (array.Length >= needed) return;
            var grown = new Vector2Int[needed];
            Array.Copy(array, grown, array.Length);
            array = grown;
        }
    }

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.GetPiecesInSelectedCategory))]
    public static class PieceTable_GetPiecesInSelectedCategory_VAGuard_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(PieceTable __instance) => VAPieceTableGuard.EnsureGrown(__instance);
    }

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.GetPiece), new Type[] { typeof(Piece.PieceCategory), typeof(Vector2Int) })]
    public static class PieceTable_GetPiece_VAGuard_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(PieceTable __instance) => VAPieceTableGuard.EnsureGrown(__instance);
    }

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    public static class PieceTable_UpdateAvailable_VAGrow_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(PieceTable __instance) => VAPieceTableGuard.EnsureGrown(__instance, scanPieces: true);

        // Last of FAP's UpdateAvailable postfixes, so it also prunes what the ordering and
        // favorites pass added, and still ahead of Infinity Hammer's (priority 200) so its menu
        // pieces are never touched.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.LowerThanNormal)]
        public static void Postfix(PieceTable __instance)
            => VABuildMenuVisibility.Apply(__instance);
    }

    internal static class HammerCategoryUI
    {
        // 1.1.203 (Fire 10-09, Arbby: Infinity Hammer's blueprint menu froze 5 s / 2 s on open with FAP): FAP asked the player
        // to rebuild its piece list from many places (config change, tab edits, the menu-open view refresh...). With Infinity
        // Hammer's own table equipped, every rebuild makes IH re-read EVERY PlanBuild blueprint from disk (Fire's: 218 files,
        // 76 MB). FAP's rules are for the vanilla Hammer only, so FAP never triggers a rebuild of anyone else's table.
        // 1.1.205 (Fire 10-09: "the same FAP menu under both hammers"): Infinity Hammer swaps in its own table. On its Builds page
        // with a tool picked (CurrentMode Builds, a filter set) IH lets vanilla fill that table with the tool's pieces - FAP's
        // pieces in FAP's categories - so FAP organizes it exactly as on the Hammer (the item-stand merge, hidden empty tabs, the
        // order). Every other IH page (its blueprint list, binds, objects, its own menu) is IH's: FAP stays out, and never triggers
        // a rebuild there (the blueprint page re-reads every PlanBuild file per rebuild).
        private const string IhTableName = "_InfinityHammerPieceTable";
        private static System.Reflection.PropertyInfo s_ihMode, s_ihFilter;
        private static bool s_ihLooked;
        internal static bool IsOrganizable(PieceTable table)
        {
            if (table == null) return false;
            if (IsHammerTable(table)) return true;
            if (table.name != IhTableName) return false;
            if (!s_ihLooked)
            {
                s_ihLooked = true;
                var t = AccessTools.TypeByName("InfinityHammer.HammerMenuCommand");
                s_ihMode = t?.GetProperty("CurrentMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                s_ihFilter = t?.GetProperty("CurrentFilter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                VaPieces.Log?.LogDebug($"[FAP] Infinity Hammer menu state: {(s_ihMode != null && s_ihFilter != null ? "found (HammerMenuCommand.CurrentMode / CurrentFilter)" : "NOT found - FAP leaves IH's table alone")}");
            }
            if (s_ihMode == null || s_ihFilter == null) return false;
            try
            {
                return s_ihMode.GetValue(null)?.ToString() == "Builds" && !string.IsNullOrEmpty(s_ihFilter.GetValue(null) as string);
            }
            catch { return false; }
        }

        // 1.1.207: IH's menu state as text, for the build-menu evidence line.
        internal static string IhStateText()
        {
            IsOrganizable(null);
            if (!s_ihLooked) { var t = AccessTools.TypeByName("InfinityHammer.HammerMenuCommand"); s_ihLooked = true;
                s_ihMode = t?.GetProperty("CurrentMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                s_ihFilter = t?.GetProperty("CurrentFilter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); }
            if (s_ihMode == null) return "ih=(not loaded)";
            try { return $"ih.mode={s_ihMode.GetValue(null)} ih.filter='{s_ihFilter?.GetValue(null)}'"; }
            catch (System.Exception ex) { return $"ih=(unreadable: {ex.Message})"; }
        }

        private static int _skippedRebuilds;
        internal static void RebuildPieceListIfHammer(Player player, string why)
        {
            if (player == null) return;
            if (!IsOrganizable(player.m_buildPieces))
            {
                if (_skippedRebuilds++ == 0)
                    VaPieces.Log?.LogDebug($"[FAP] piece-list rebuild skipped ({why}): the equipped table '{player.m_buildPieces?.name}' is not the Hammer (FAP leaves other tools' menus alone)");
                return;
            }
            player.UpdateAvailablePiecesList();
        }

        internal static bool IsHammerTable(PieceTable table)
        {
            if (table == null) return false;
            var hammer = ObjectDB.instance?.GetItemPrefab("Hammer");
            var hammerTable = hammer?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            return hammerTable != null && ReferenceEquals(table, hammerTable);
        }
    }
}
