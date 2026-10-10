using System.Collections.Generic;
using FiresCore.Bridge;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // The one place that decides whether a piece reaches the build menu.
    //
    // Valheim 1.0 added the flat PieceTable.m_availablePieces set and rebuilt the menu on it:
    // all four piece lists read that set, and the search box reads it through Show All. Hiding
    // a piece by giving it a category no tab pointed at stopped working there — pre-1.0 such a
    // piece was unreachable, in 1.0 it is listed regardless of category. So every hide reason is
    // applied here, to the flat sets and the per-category lists together, right after vanilla
    // rebuilds them.
    //
    // Hidden means hidden from the MENU only. The piece stays on the table and in ZNetScene, so
    // it still spawns, syncs, and counts as a build piece for other mods.
    internal static class VABuildMenuVisibility
    {
        // True while the local player is a non-admin on a server that registers FAP's pieces for
        // everyone (ConfigManager.configRegisterPiecesForNonAdmins).
        internal static bool HideInjectedPiecesFromMenu { get; set; }

        private static readonly HashSet<Piece.PieceCategory> _hiddenTabCategories =
            new HashSet<Piece.PieceCategory>();

        private static bool _hideAdminGatedPieces;
        private static bool _hideInjectedPieces;
        private static bool _vanillaFriendly;
        private static bool? _lastAdminPreviewState;
        private static bool? _viewAppliedToLists;

        // Toggling debugmode changes nothing on its own: the piece lists are rebuilt by
        // UpdateAvailable, which a console command never triggers. Opening the menu is the
        // moment the answer has to be current.
        internal static void RefreshIfViewChanged()
        {
            if (_viewAppliedToLists == ViewingAsPlayer()) return;
            var player = Player.m_localPlayer;
            // 1.1.203: _viewAppliedToLists is only set by Apply on the Hammer's table, so with another tool's table (Infinity
            // Hammer's) it never matched and EVERY menu open forced a full rebuild + repaint. Not the Hammer: nothing to refresh.
            if (player == null || !HammerCategoryUI.IsOrganizable(player.m_buildPieces)) return;
            if (player != null) VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(player, "VABuildMenuVisibility");
            VABuildUiCategoryTags.RequestBuildMenuRefresh();
        }

        private static readonly System.Collections.Generic.HashSet<int> _skippedTables = new System.Collections.Generic.HashSet<int>();

        internal static void Apply(PieceTable table)
        {
            if (table == null) return;
            // Fire (09-29 21:3x): picking a Chaos Hammer (FAT) paint swatch placed a random FAP clutter piece. This pass ran
            // on EVERY PieceTable: it pruned another tool's lists, added and removed FAP tabs in its m_categories and re-seeded
            // its selected category, so the swatch's grid index was read from another category. FAP's menu rules are for the
            // vanilla Hammer only, like the scroll patch.
            if (!HammerCategoryUI.IsOrganizable(table))
            {
                if (_skippedTables.Add(table.GetInstanceID()))
                    UnityEngine.Debug.Log($"[FAP] build-menu visibility: {table.name} skipped (not the Hammer)");
                return;
            }
            var byCategory = PieceTableAccess.ByCategory(table);
            if (byCategory == null) return;

            RefreshViewRules();
            _viewAppliedToLists = ViewingAsPlayer();   // 1.1.209: set by the pass that pruned the lists (1.1.208 set it from the row check too, so a menu open under IH's tool skipped the rebuild that runs the tab organizer: raw per-bundle tabs)

            table.m_availablePieces.RemoveWhere(IsHiddenFromMenu);
            table.m_enabledPieces.RemoveWhere(IsHiddenFromMenu);
            for (int category = 0; category < byCategory.Count; category++)
                byCategory[category]?.RemoveAll(IsHiddenFromMenu);

            // A tab whose list ends up empty is a DEAD CLICK, not just an eyesore:
            // PieceTable.GetPiece returns null for an empty category (REF1.0
            // PieceTable.cs:116), GetSelectedPrefab then returns null, SetupPlacementGhost
            // builds no ghost, and Player's build-click block is skipped whole - so the
            // hammer does nothing and says nothing. Tabs are reconciled against config
            // first so one that refilled comes back, then the still-empty FAP tabs go.
            // Only FAP's own tabs: vanilla manages its own (it already keeps Feasts, Food
            // and Meads off the hammer table).
            VACategoryRegistry.SyncCategories(table);
            PruneEmptyOwnTabs(table, byCategory);
            VACategoryRegistry.ReseedSelectedCategory(table);
        }

        private static readonly List<Piece.PieceCategory> _emptyTabs = new List<Piece.PieceCategory>();

        // Every tab this pass removed, so the next pass can put it back the moment it has
        // pieces again. SyncCategories cannot be relied on for that: Favorites, the user's
        // own tab folders and other mods' categories (Mausoleum, Dungeon Sections, Fires
        // NPCs) are registered outside it, so a tab pruned here would never return.
        private static readonly HashSet<Piece.PieceCategory> _prunedEmptyTabs =
            new HashSet<Piece.PieceCategory>();

        private static void PruneEmptyOwnTabs(PieceTable table, List<List<Piece>> byCategory)
        {
            if (table.m_categories == null) return;

            // Stand-ins are built across frames by the init coroutine, and vanilla has not
            // distributed anything at all on a table this early - reading either state as
            // "no content" would hide the very tabs this is meant to keep clickable.
            if (VAVanillaPieceManager.IsInitializing) return;
            if (table.m_availablePieces == null || table.m_availablePieces.Count == 0) return;

            // Restore first: anything that refilled since the last pass earns its tab back.
            if (_prunedEmptyTabs.Count > 0)
            {
                _emptyTabs.Clear();
                foreach (var category in _prunedEmptyTabs)
                {
                    int index = (int)category;
                    if (index < 0 || index >= byCategory.Count) continue;
                    var refilled = byCategory[index];
                    if (refilled == null || refilled.Count == 0) continue;
                    if (VACategoryRegistry.AddTab(table, category)) _emptyTabs.Add(category);
                }
                foreach (var category in _emptyTabs) _prunedEmptyTabs.Remove(category);
            }

            _emptyTabs.Clear();
            foreach (var category in table.m_categories)
            {
                if (!VACategoryRegistry.IsOwnCategory(category)) continue;
                int index = (int)category;
                if (index < 0 || index >= byCategory.Count) continue;
                var pieces = byCategory[index];
                if (pieces != null && pieces.Count > 0) continue;
                _emptyTabs.Add(category);
            }

            foreach (var category in _emptyTabs)
            {
                VACategoryRegistry.RemoveTab(table, category);
                _prunedEmptyTabs.Add(category);
            }
        }

        // The menu rules for the current view: which tabs are hidden (config, dev-only outside debug mode, VanillaFriendly's) and
        // which pieces. Apply prunes the piece lists with them; 1.1.208: the tab ROWS check them too (RowVisible), so a tab
        // Fire hid never shows as a row whatever state the lists are in - under Infinity Hammer's tool (which hands over the
        // Hammer's table after IH's own list handling) the rows were built from lists this pass had not pruned yet.
        private static void RefreshViewRules()
        {
            _hiddenTabCategories.Clear();
            VACategoryRegistry.CollectHiddenCategories(_hiddenTabCategories);

            bool viewingAsPlayer = ViewingAsPlayer();
            _hideInjectedPieces = HideInjectedPiecesFromMenu || viewingAsPlayer;
            _hideAdminGatedPieces = viewingAsPlayer && AdminGatedHidingEnabled();
            // 1.1.209: _viewAppliedToLists is NOT set here - only Apply (which really pruned the lists) may say "applied".

            // Client-only, every other player is a client without FAP, which is exactly who vanilla-friendly builds
            // for: a bundle prefab, stand-in or bake placed here would be invisible to them.
            _vanillaFriendly = (ConfigManager.Instance?.configVanillaFriendly?.Value ?? false)
                || VAFapServerPresence.IsClientOnly;

            // Baked meshes are a combined mesh only FAP can build, and a blueprint stub is a
            // menu placeholder no vanilla client has. Neither belongs in a build a vanilla
            // client has to render. Hidden rather than unregistered, so turning the setting
            // back off brings them straight back.
            // 1.1.211 (Fire 10-10: under Infinity Hammer's tool both Item Stands tabs showed, the real hammer one): the
            // orientation ItemStandOrientation leaves off is no row, whatever lists the tool's table still holds.
            if (!VAVanillaPieceManager.ShowStandOrientation(VAItemStandStandinFactory.Orientation.Vertical))
                HideCategory(VACategoryRegistry.CatItemStandsV);
            if (!VAVanillaPieceManager.ShowStandOrientation(VAItemStandStandinFactory.Orientation.Horizontal))
                HideCategory(VACategoryRegistry.CatItemStandsH);

            if (_vanillaFriendly)
            {
                HideCategory(VACategoryRegistry.CatBakedMeshes);
                HideCategory(VACategoryRegistry.CatBlueprints);
            }
        }

        // 1.1.208: is this FAP tab a row at all - not a hidden tab, and holding at least one piece the current view shows.
        internal static bool RowVisible(Piece.PieceCategory category, List<Piece> pieces)
        {
            RefreshViewRules();
            if (_hiddenTabCategories.Contains(category)) return false;
            if (pieces == null) return false;
            foreach (var p in pieces) if (p != null && !IsHiddenFromMenu(p)) return true;
            return false;
        }

        // 1.1.208: one piece against the current view's rules (the rules are refreshed by RowVisible, called per row first).
        internal static bool PieceVisible(Piece piece) => piece != null && !IsHiddenFromMenu(piece);

        private static void HideCategory(string categoryName)
        {
            if (VACategoryRegistry.TryGet(categoryName, out var category))
                _hiddenTabCategories.Add(category);
        }

        private static bool IsHiddenFromMenu(Piece piece)
        {
            if (piece == null) return false;

            // Max is FAP's "registered but off the menu" marker. Vanilla never assigns it:
            // UpdateAvailable indexes its per-category lists by category, and Max is one past
            // the last, so a vanilla piece carrying it would throw.
            if (VerdantsAscent.Utilities.VAPieceCategoryRuntime.IsHidden(piece.m_category)) return true;
            if (_hiddenTabCategories.Contains(piece.m_category)) return true;

            // A WackysDatabase clone carries the user's own name, so the rules below would read it
            // as foreign content and leave it on the menu after its source was hidden. Ask them
            // about the prefab it was cloned from instead.
            var identity = Compat.VAWackyDatabaseCompat.ResolveCloneSource(piece.gameObject);

            // Listing a bake in PlayerPieces.yaml is an admin opting it in for players, so the
            // FAP-content gate stands aside for it. Vanilla-friendly and the admin recipe gate
            // still apply.
            bool publishedForPlayers = identity != null && Utilities.VAFapPlayerPieces.IsPublished(identity.name);
            if (_hideInjectedPieces && !publishedForPlayers && VAVanillaPieceManager.IsFapOwnedPiece(identity)) return true;

            if (_hideAdminGatedPieces && FiresCore.Items.AdminRecipeGate.IsGated(piece.m_resources)) return true;

            if (_vanillaFriendly && IsModdedOnlyContent(identity)) return true;

            return false;
        }

        // Vanilla-friendly is for builders whose blueprints travel to players without the mod, so
        // the menu only offers what a vanilla client renders and persists on its own. Registration
        // already skips most of this; the checks here are what make a mid-session flip apply.
        private static bool IsModdedOnlyContent(GameObject pieceObject)
        {
            if (pieceObject == null) return false;

            string prefabName = pieceObject.name;
            if (VAClutterStandinFactory.IsStandinName(prefabName)) return true;
            if (VAClutterClearTool.IsStandinName(prefabName)) return true;

            // A baked piece is one combined mesh only FiresAdminPrefabs can build. Asked by name
            // rather than by the Baked Meshes tab, which a piece published for players leaves.
            if (Utilities.VABakeBundle.TryGetCacheKeyForPrefab(prefabName) != null) return true;

            // Asked per piece rather than read off m_category: the disk tab layout re-stamps
            // categories on every UpdateAvailable, so an effect filed under Max at categorise
            // time is back in the FX tab by the time the menu reads it.
            if (VAVanillaPieceManager.IsVanillaUnsafeEffect(pieceObject)) return true;

            // A bundle prefab exists only where its bundle is installed, so a build carrying one
            // reads as missing prefabs to everyone else.
            if (VAExternalBundleLoader.TryGetSourceBundle(prefabName, out _)) return true;

            if (VAItemStandStandinFactory.TryResolve(pieceObject, out string itemName, out var orientation))
            {
                var item = VAItemStandRegistry.Resolve(itemName);
                if (item == null) return true;
                return orientation == VAItemStandStandinFactory.Orientation.Vertical
                    ? !item.VanillaAcceptsVertical
                    : !item.VanillaAcceptsHorizontal;
            }

            return false;
        }

        // A piece whose recipe costs a cheat item is admin content whoever added it. Vanilla
        // would still list it greyed out, advertising content the player can never build.
        private static bool AdminGatedHidingEnabled()
        {
            var config = ConfigManager.Instance;
            return config != null
                && config.configHideAdminGatedPieces != null
                && config.configHideAdminGatedPieces.Value;
        }

        // Who the menu is being built for. A player always gets the player's menu; an admin gets
        // it too while debug mode is off, which is the A/B switch — debugmode is an admin-only
        // command, so turning it off is how an admin asks to see what their players see.
        internal static bool ViewingAsPlayer()
        {
            // Client-only there is no server AdminOnlyMode for a player view to preview.
            if (VAFapServerPresence.IsClientOnly) return false;

            bool isAdmin;
            try { isAdmin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); }
            catch { return true; }
            if (!isAdmin) return true;

            var config = ConfigManager.Instance;
            if (config?.configAdminOnlyMode == null || !config.configAdminOnlyMode.Value) return false;

            bool asPlayer = !IsDebugModeActive();
            if (_lastAdminPreviewState != asPlayer)
            {
                _lastAdminPreviewState = asPlayer;
                Debug.Log($"{VaPieces.PluginName}: build menu is showing the "
                    + (asPlayer ? "PLAYER view (debugmode off)" : "ADMIN view (debugmode on)")
                    + " — AdminOnlyMode couples the two so an admin can compare them.");
            }
            return asPlayer;
        }

        // Vanilla's own pairing: debug mode only counts while cheats are enabled.
        internal static bool IsDebugModeActive()
        {
            if (!Player.m_debugMode) return false;
            var console = Console.instance;
            return console == null || console.IsCheatsEnabled();
        }
    }

    [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
    public static class BuildUi_OpenBuildMenu_VAViewRefresh_Patch
    {
        [HarmonyPrefix]
        public static void Prefix() => VABuildMenuVisibility.RefreshIfViewChanged();
    }
}
