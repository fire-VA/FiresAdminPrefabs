using FiresCore.Help;

namespace VerdantsAscent.Utilities
{
    // FAP's sections for the shared FiresCore help panel. FAP only REGISTERS
    // content — the panel itself lives in FUC and is created by whichever mod
    // calls HelpPanel.Initialize (FiresRPGmaker today). Registration is just a
    // static list append + Changed event, so it is safe to call from Awake
    // regardless of which mod owns the panel or what order plugins load in.
    internal static class VAFapHelpContent
    {
        private const string ModId = "Fires Admin Prefabs";

        public static void Register()
        {
            HelpRegistry.RegisterSection(ModId, "", "What Fires Admin Prefabs Does", 0, BuildOverview);

            HelpRegistry.RegisterSection(ModId, "Build Menu", "Build Menu Overview", 10, BuildMenuOverview);
            HelpRegistry.RegisterSection(ModId, "Build Menu", "Resizing & Moving the Menu", 11, BuildMenuResize);
            HelpRegistry.RegisterSection(ModId, "Build Menu", "Search & Navigation", 12, BuildMenuSearch);
            HelpRegistry.RegisterSection(ModId, "Build Menu", "Tabs & Organization", 13, BuildMenuTabs);
            HelpRegistry.RegisterSection(ModId, "Build Menu", "Favorites", 14, BuildFavorites);

            HelpRegistry.RegisterSection(ModId, "Pieces", "Right-Click Piece Menu", 20, BuildRightClickMenu);
            HelpRegistry.RegisterSection(ModId, "Pieces", "Hiding & Restoring Pieces", 21, BuildHideRestore);
            HelpRegistry.RegisterSection(ModId, "Pieces", "Per-Piece Overrides", 22, BuildPieceOverrides);

            HelpRegistry.RegisterSection(ModId, "Content", "Monsters", 30, BuildMonsters);
            HelpRegistry.RegisterSection(ModId, "Content", "Containers & Chests", 31, BuildContainers);
            HelpRegistry.RegisterSection(ModId, "Content", "Clutter", 32, BuildClutter);
            HelpRegistry.RegisterSection(ModId, "Content", "Item Stands", 33, BuildItemStands);
            HelpRegistry.RegisterSection(ModId, "Content", "FX & VFX", 34, BuildFx);
            HelpRegistry.RegisterSection(ModId, "Content", "Blueprints", 35, BuildBlueprints);
            HelpRegistry.RegisterSection(ModId, "Content", "Baked Meshes", 36, BuildBakedMeshes);

            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "Hammer Toggles", 50, BuildHammerToggles, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "Persistence & EWP Respawns", 51, BuildPersistence, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "Asset Bundles & Sync", 52, BuildAssetSync, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "Config Reference", 53, BuildConfigReference, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "File & Folder Layout", 54, BuildFileLayout, adminOnly: true);

            HelpRegistry.RegisterSection(ModId, "Commands (Admin)", "Admin Commands", 60, BuildCommands, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Commands (Admin)", "Config Push / Pull", 61, BuildConfigPush, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Commands (Admin)", "Compatibility Notes", 62, BuildCompatibility, adminOnly: true);
        }

        // ── Overview ────────────────────────────────────────────────────

        private static void BuildOverview(HelpContentWriter w)
        {
            w.Header("What Fires Admin Prefabs Does");
            w.Paragraph("Fires Admin Prefabs (FAP) turns the vanilla hammer into a full admin build tool. It scans " +
                "every prefab the game knows about — vanilla props, nature objects, clutter, effects, creatures, " +
                "items, and anything loaded from an asset bundle — and injects them into the hammer as placeable " +
                "pieces, organized into tabs you control.");
            w.Divider();

            w.SubHeader("The Short Version");
            w.Bullet("Equip the hammer. Every tab beyond the vanilla ones is FAP.");
            w.Bullet("Type in the search bar at the top to filter across ALL tabs at once.");
            w.Bullet("Drag the bottom-right corner ( // ) to resize the menu. Your size is saved.");
            w.Bullet("Right-click any piece icon for per-piece actions (favorite, hide, move, configure).");
            w.Bullet("Right-click any tab to rename or hide it.");
            w.Divider();

            w.SubHeader("Who Can Use It");
            w.Paragraph("Most of FAP is admin-facing by nature — you are placing raw world content. The " +
                "[General] AdminOnlyMode config gates placement to admins on a server. The build-menu " +
                "conveniences (search, resize, favorites, scrolling) work for anyone with the mod.");
            w.Divider();

            w.SubHeader("Vanilla-Friendly Mode");
            w.Paragraph("If your players do NOT all have FAP installed, turn on [General] VanillaFriendly. " +
                "It disables every feature that only renders correctly on a modded client — custom scale sync, " +
                "looping particle/audio overrides, and TimedDestruction cancellation — so what you build looks " +
                "the same to everyone. Placement, tabs, and search all still work.");
        }

        // ── Build Menu ──────────────────────────────────────────────────

        private static void BuildMenuOverview(HelpContentWriter w)
        {
            w.Header("Build Menu Overview");
            w.Paragraph("FAP replaces the hammer's fixed 15x6 icon grid with a scrolling, resizable grid that can " +
                "show far more than vanilla's 90 icons. The grid, its scrolling, and its click/arrow navigation " +
                "apply to EVERY build tool — the hammer, the hoe/cultivator, and Infinity Hammer's own menus.");
            w.Divider();

            w.SubHeader("What You Get");
            w.Bullet("Scrollable piece grid — mouse wheel, or drag the scrollbar on the right.");
            w.Bullet("A search bar that filters pieces across every tab.");
            w.Bullet("Resizable panel with a drag grip in the bottom-right corner.");
            w.Bullet("Drag the panel's empty wood-grain area to reposition it.");
            w.Bullet("Custom tabs, renameable and hideable, driven by folders on disk.");
            w.Bullet("SNAP POINTS on the vanilla props that shipped without any — dvergr poles, " +
                "beams and walls, goblin walls and roofs, blackmarble posts, columns and creep " +
                "pieces, doors, gates, chests — hand-tuned tables, so they lock to neighbors and " +
                "to each other end-to-end.");
            w.Divider();

            w.SubHeader("If SearsCatalog Is Installed");
            w.Paragraph("FAP stands down entirely and lets SearsCatalog drive the panel — the two do the same job, " +
                "and Sears wins to avoid two mods fighting over one grid. Everything else in FAP (tabs, " +
                "right-click menus, placement) works normally.");
        }

        private static void BuildMenuResize(HelpContentWriter w)
        {
            w.Header("Resizing & Moving the Menu");
            w.SubHeader("Resize");
            w.Bullet("Grab the ' // ' grip in the bottom-right corner of the build panel and drag.");
            w.Bullet("Dragging right/left changes COLUMNS. Dragging down/up changes visible ROWS.");
            w.Bullet("Release to apply. The panel snaps to whole icon cells — you can't end up half a column wide.");
            w.Bullet("Limits: 6-30 columns, 4-20 rows. Vanilla is 15x6.");
            w.Divider();

            w.SubHeader("It Remembers");
            w.Paragraph("Your chosen size is written to [BuildMenu] Columns and [BuildMenu] Rows in FAP's config " +
                "and restored on every login. These are per-player local settings — the server never overwrites " +
                "them, and each player picks their own.");
            w.Divider();

            w.SubHeader("Move");
            w.Bullet("Click-drag any empty wood-grain part of the panel to reposition it on screen.");
            w.Bullet("Position resets on world reload — it's a per-session convenience, not a saved layout.");
        }

        private static void BuildMenuSearch(HelpContentWriter w)
        {
            w.Header("Search & Navigation");
            w.SubHeader("Search Bar");
            w.Paragraph("The bar sits just above the tab strip. Typing filters the piece list ACROSS ALL TABS — " +
                "the tab counts update to match, so you can see at a glance which tab holds what you want.");
            w.Bullet("Matches prefab name, display name, and description — raw and localized.");
            w.Bullet("Underscores and spaces are interchangeable: 'stone floor' finds 'stone_floor'.");
            w.Bullet("Clear the box to restore the full list.");
            w.Bullet("While the box is focused, your keystrokes do NOT leak into the game as movement or hotkeys.");
            w.Divider();

            w.SubHeader("The Falling Search Bar");
            w.Paragraph("The bar physically stands on top of the build menu. Squeeze the menu against the top of " +
                "the screen, or drag it out from under the bar, and the bar loses its footing and tumbles off — " +
                "you can catch it by sliding the menu back under it as it falls. It returns whenever you reopen " +
                "the menu. If you'd rather it just behave, set [BuildMenu] SearchBarLockTopLeft = true and it " +
                "bolts itself to the menu's top-left corner instead. Flipping that on mid-fall rescues it instantly.");
            w.Divider();

            w.SubHeader("Navigation");
            w.Bullet("Mouse wheel scrolls the piece grid — it never flips tabs.");
            w.Bullet("Q / E (or clicking a tab) switches tabs, exactly like vanilla.");
            w.Bullet("Arrow keys / gamepad navigate the grid using your ACTUAL column count, not vanilla's 15 — " +
                "and the grid auto-scrolls to keep the selection visible.");
            w.Bullet("Opening the menu scrolls to your currently-selected piece rather than dumping you at the top.");
            w.Bullet("Switching tabs resets the scroll to the top of the new tab.");
        }

        private static void BuildMenuTabs(HelpContentWriter w)
        {
            w.Header("Tabs & Organization");
            w.Paragraph("FAP's tabs are driven by folders on disk, so you can reorganize the entire hammer without " +
                "touching code. Everything here is PER-PLAYER — your tab layout is yours; it is not synced from " +
                "the server and does not affect anyone else.");
            w.Divider();

            w.SubHeader("Built-In Tabs");
            w.Bullet("Nature — trees, rocks, plants, and other natural scenery.");
            w.Bullet("CreatorShop — the general catch-all for injected vanilla prefabs.");
            w.Bullet("Clutter — prefabs recognized as clutter (grass, ground detail, instanced scatter).");
            w.Bullet("Monsters — one placeable stand-in per creature in the game.");
            w.Bullet("Item Stands (V) / (H) — one tile per item, wall-mount and shelf-mount orientations.");
            w.Bullet("FX / VFX — effect prefabs, split by prefix.");
            w.Bullet("Blueprints — one tile per .blueprint / .vbuild file you've added.");
            w.Bullet("Baked Meshes — pre-combined single-prefab versions of baked blueprints.");
            w.Bullet("Favorites — your bookmarked pieces (see the Favorites page).");
            w.Bullet("The VANILLA tabs too — Misc, Crafting, Building, Furniture are folder-managed " +
                "just like the custom tabs, so their contents and order are yours to change.");
            w.Divider();

            w.SubHeader("Reorganizing");
            w.Bullet("Right-click a piece → 'Move to tab...' to send it somewhere else, or 'New tab...' to " +
                "create one. Vanilla tabs are valid destinations, and ANY piece can move — vanilla " +
                "builtins and other mods' pieces included, not just FAP's.");
            w.Bullet("Right-click a tab → 'Rename' to give it a friendly label (the folder keeps its real name).");
            w.Bullet("Right-click a tab → 'Hide' to remove it, and 'Show hidden tabs...' to bring it back.");
            w.Divider();

            w.SubHeader("Editing On Disk");
            w.Paragraph("Everything lives under config/FiresAdminPrefabs/HammerTabs/. Each tab is a folder " +
                "containing a prefabs.txt (one prefab name per line, # for comments). Creating a folder creates " +
                "a tab; moving a line between files moves a piece — cut/paste whole blocks for bulk moves. " +
                "LINE ORDER = IN-GAME ORDER: reorder the lines to reorder the tab. Disk always wins over FAP's " +
                "automatic sorting. Changes apply on world reload.");
            w.CodeBlock(
                "HammerTabs/\n" +
                "  Misc/prefabs.txt               <- VANILLA tabs are folders too\n" +
                "  Crafting/ BuildingWorkbench/   <- reorganize + reorder them\n" +
                "  BuildingStonecutter/ Furniture/\n" +
                "  CreatorShop/prefabs.txt\n" +
                "  Nature/prefabs.txt\n" +
                "  My Custom Tab/prefabs.txt      <- new folder = new tab\n" +
                "  Ignore/prefabs.txt             <- hidden pieces (not a tab)\n" +
                "  Hidden_OldTab/                 <- hidden tab\n" +
                "  Favorites/prefabs.txt          <- bookmarks (order = tab order)");
            w.Paragraph("Vanilla, modded, and FAP-injected pieces can ALL move between ANY tabs this way. " +
                "If a name ends up in two files, the alphabetically-first folder wins — after a cut/paste, " +
                "make sure the line really left the source file.");
            w.Divider();

            w.SubHeader("Sorting");
            w.Bullet("Explicit file order comes first — pieces listed in a tab's prefabs.txt show in line order.");
            w.Bullet("[BuildMenu] SortVanillaFirst — unlisted pieces rank vanilla before modded/bundle content.");
            w.Bullet("[General] SortHammerByMod — gives each asset bundle its own tab instead of dumping everything " +
                "into CreatorShop.");
        }

        private static void BuildFavorites(HelpContentWriter w)
        {
            w.Header("Favorites");
            w.Paragraph("The Favorites tab collects the pieces you use most, without disturbing where they live.");
            w.Divider();

            w.SubHeader("How To Use It");
            w.Bullet("Right-click any piece icon → '★ Add to Favorites'.");
            w.Bullet("The piece appears in the Favorites tab immediately — no reload needed.");
            w.Bullet("Right-click it again (from either tab) to remove it.");
            w.Divider();

            w.SubHeader("A Bookmark, Not A Move");
            w.Paragraph("Favoriting does NOT relocate the piece. It still lives in its normal tab and also shows " +
                "up in Favorites. That's deliberate: if favoriting moved pieces, un-favoriting one later would " +
                "leave it with no home tab at all. This is also why 'Favorites' appears at the top of the " +
                "'Move to tab...' list as a bookmark action rather than a move destination.");
            w.Divider();

            w.SubHeader("Where It's Stored");
            w.Paragraph("config/FiresAdminPrefabs/HammerTabs/Favorites/prefabs.txt — one prefab name per " +
                "line, safe to hand-edit, and LINE ORDER is the tab's display order. It's per-player and " +
                "never synced, so every player keeps their own list. (Older builds used a root " +
                "favorites.txt — it migrates automatically.)");
        }

        // ── Pieces ──────────────────────────────────────────────────────

        private static void BuildRightClickMenu(HelpContentWriter w)
        {
            w.Header("Right-Click Piece Menu");
            w.Paragraph("Right-clicking any piece icon in the hammer opens a context menu. Which entries appear " +
                "depends on what the piece is — a monster stand-in gets creature options, a chest gets container " +
                "options, and so on.");
            w.Divider();

            w.SubHeader("Always Available");
            w.Bullet("★ Add / Remove Favorites — bookmark the piece into the Favorites tab.");
            w.Bullet("Hide (<piece>) — remove it from the hammer entirely.");
            w.Bullet("Move to tab... — send it to another tab (vanilla tabs included) or create a new " +
                "one. Works on every piece: vanilla, modded, or FAP-injected.");
            w.Bullet("Distance render override... — force this piece to render further than vanilla streaming.");
            w.Bullet("Shader override... — swap the piece's shader.");
            w.Bullet("Restore hidden piece... — un-hide something you hid earlier.");
            w.Divider();

            w.SubHeader("Contextual");
            w.Bullet("Configure monster / Presets — on Monsters-tab stand-ins.");
            w.Bullet("Configure container / Presets — on anything with a Container (chests, drawers).");
            w.Bullet("Configure clutter / Presets — on clutter prefabs and clutter stand-ins.");
            w.Bullet("Place with Persistence — on FX/VFX pieces, opts that one effect into persisting.");
            w.Bullet("Bake to prefab / Re-bake — on Blueprints-tab tiles.");
            w.Bullet("Re-render icon / Remove saved NPC — on saved-NPC tiles from FiresRPGmaker.");
            w.Divider();

            w.SubHeader("Tab Right-Click");
            w.Bullet("Rename '<tab>' — set a friendly display label; the folder name stays as the internal key.");
            w.Bullet("Hide '<tab>' — hides the tab (renames its folder with a Hidden_ prefix).");
            w.Bullet("Show hidden tabs... — list and restore hidden tabs. Needs a world reload to apply.");
        }

        private static void BuildHideRestore(HelpContentWriter w)
        {
            w.Header("Hiding & Restoring Pieces");
            w.Paragraph("FAP injects a LOT of prefabs. Hiding is how you cut the noise down to the things you " +
                "actually build with.");
            w.Divider();

            w.SubHeader("Hiding");
            w.Bullet("Right-click a piece → 'Hide'. It disappears from the hammer immediately where possible.");
            w.Bullet("The prefab is still fully registered — spawn commands, existing placements, and ZDO sync " +
                "all keep working. It's only removed from the build menu.");
            w.Bullet("The name is appended to HammerTabs/Ignore/prefabs.txt.");
            w.Divider();

            w.SubHeader("Restoring");
            w.Bullet("Right-click any piece → 'Restore hidden piece...' and pick from the list.");
            w.Bullet("Restored pieces reappear after a world reload.");
            w.Bullet("You can also delete the line from Ignore/prefabs.txt by hand.");
        }

        private static void BuildPieceOverrides(HelpContentWriter w)
        {
            w.Header("Per-Piece Overrides");
            w.SubHeader("Distance Render Override");
            w.Paragraph("Vanilla unloads distant objects aggressively. This forces a specific piece to keep " +
                "rendering out to a chosen distance — useful for landmarks, towers, and skyline props that " +
                "shouldn't pop out of existence.");
            w.Bullet("Right-click the piece → 'Distance render override...' and pick a preset.");
            w.Bullet("Options: Off (vanilla streaming), 250m, 500m, 750m, 1000m, 1500m.");
            w.Bullet("Applies to the prefab, so every instance of it is affected.");
            w.Bullet("Set it back to 'Off' to clear the override.");
            w.Divider();

            w.SubHeader("Shader Override");
            w.Paragraph("Swaps the shader a piece renders with, applied immediately to the prefab and every live " +
                "instance. Useful when a bundle prefab ships with a shader that doesn't fit Valheim's lighting.");
            w.Bullet("Right-click → 'Shader override...' and pick from the available shader list.");
            w.Bullet("'(no override — use prefab default)' clears it.");
            w.Divider();

            w.Paragraph("Both overrides are stored per-piece and persist across sessions. The current value is " +
                "shown in the menu label so you can see at a glance what's set.");
        }

        // ── Content ─────────────────────────────────────────────────────

        private static void BuildMonsters(HelpContentWriter w)
        {
            w.Header("Monsters");
            w.Paragraph("The Monsters tab holds one placeable stand-in for every creature in the game. Placing one " +
                "spawns the real creature at that spot.");
            w.Divider();

            w.SubHeader("Configuring");
            w.Bullet("Right-click a monster tile → 'Configure monster (<creature>)...' to open the editor.");
            w.Bullet("Set level/stars, boss flag, respawn timer, and other per-creature values.");
            w.Bullet("Save it as a named preset to reuse.");
            w.Divider();

            w.SubHeader("Presets");
            w.Bullet("Right-click → 'Presets for <creature>...' lists every preset saved for that creature.");
            w.Bullet("Picking one QUEUES it for your next placement of that creature — place the stand-in and the " +
                "spawned creature uses those settings.");
            w.Bullet("Presets are stored in config/data/data_FAP_presets.yaml.");
            w.Divider();

            w.SubHeader("Respawning");
            w.Paragraph("Respawn timers are handled by ExpandWorldPrefabs (EWP) server-side. See the " +
                "'Persistence & EWP Respawns' page — on a dedicated server the rules must be pushed to the " +
                "server or respawns will never fire.");
        }

        private static void BuildContainers(HelpContentWriter w)
        {
            w.Header("Containers & Chests");
            w.Paragraph("Any piece with a Container component — chests, drawers, barrels, and modded storage — " +
                "gets container options on right-click.");
            w.Divider();

            w.Bullet("'Configure container (<prefab>)...' opens the loot/content editor.");
            w.Bullet("'Presets for <prefab>...' lists saved presets; picking one queues it for your next placement.");
            w.Bullet("Presets are stored in config/data/data_FAP_chests.yaml.");
            w.Divider();

            w.Paragraph("This is how you build pre-filled loot containers: configure the contents once, save it as " +
                "a preset, then place as many as you like with that preset queued.");
        }

        private static void BuildClutter(HelpContentWriter w)
        {
            w.Header("Clutter");
            w.Paragraph("Clutter is Valheim's instanced ground detail — grass, small rocks, ground cover. It renders " +
                "through a separate high-performance system rather than as normal pieces, so FAP handles it " +
                "specially.");
            w.Divider();

            w.SubHeader("Painting");
            w.Bullet("Clutter pieces paint in an area rather than placing one object.");
            w.Bullet("[Clutter] PaintRadius and [Clutter] PaintDensity control the brush.");
            w.Divider();

            w.SubHeader("Configuring");
            w.Bullet("Right-click a clutter piece → 'Configure clutter (<prefab>)...' to edit its appearance/density.");
            w.Bullet("'Presets for <prefab>...' applies a saved preset globally for that clutter type — clutter is " +
                "configured world-wide, not per-placement.");
            w.Bullet("The active preset is shown in the menu label; 'Clear active' reverts to vanilla.");
            w.Divider();

            w.SubHeader("Maintenance");
            w.Bullet("fap_recategorize_clutter — finds prefabs that are really clutter but were parked in the wrong tab.");
            w.Bullet("fap_clean_stale_clutter — removes stale persistent ZDOs left by clutter effect prefabs.");
        }

        private static void BuildItemStands(HelpContentWriter w)
        {
            w.Header("Item Stands");
            w.Paragraph("Two tabs — Item Stands (V) and (H) — give you one pre-populated tile per item in the game, " +
                "so you can place a displayed item directly instead of placing a stand and then filling it.");
            w.Divider();

            w.Bullet("(V) is wall-mounted / vertical; (H) is shelf-mounted / horizontal. Pick the orientation you " +
                "want rather than rotating after placement.");
            w.Bullet("[HammerToggles] ItemStandHideStandVisual hides the stand itself, leaving just the floating item.");
            w.Bullet("With [General] VanillaFriendly ON, these tabs are limited to items vanilla clients also have.");
        }

        private static void BuildFx(HelpContentWriter w)
        {
            w.Header("FX & VFX");
            w.Paragraph("The FX and VFX tabs expose Valheim's effect prefabs — fire, smoke, sparks, mist, magic " +
                "effects — as placeable pieces for set dressing.");
            w.Divider();

            w.SubHeader("Persistence");
            w.Paragraph("Most effects are designed to play once and destroy themselves. To make one stay:");
            w.Bullet("Right-click the FX piece → 'Place with Persistence' to opt that specific effect in.");
            w.Bullet("[HammerToggles] EffectsOverrideTTL overrides the self-destruct timer more broadly.");
            w.Bullet("With VanillaFriendly ON, looping overrides are disabled — players without FAP wouldn't see " +
                "them loop anyway.");
            w.Divider();

            w.SubHeader("Cleanup");
            w.Bullet("fap_clear_orphan_fx — sweeps orphaned status-effect FX (wet, frost, etc.) left behind when " +
                "their character unloaded. [FxClear] Radius and the watchdog settings tune it.");
            w.Bullet("fap_fx_dump — lists every renderer-bearing object near you with its lineage, for diagnosing " +
                "which effect an artifact actually came from.");
            w.Bullet("fap_clear_legacy_persistence — strips FAP_persist from every ZDO in the world, to recover " +
                "from older FAP versions that over-applied persistence.");
        }

        private static void BuildBlueprints(HelpContentWriter w)
        {
            w.Header("Blueprints");
            w.Paragraph("Drop .blueprint or .vbuild files into config/FiresAdminPrefabs/HammerTabs/Blueprints/ and " +
                "each becomes a placeable tile in the Blueprints tab. Placing one spawns every piece the blueprint " +
                "describes at that location.");
            w.Divider();

            w.SubHeader("Baking");
            w.Paragraph("A large blueprint placed as hundreds of individual pieces is expensive to render. Baking " +
                "combines it into a single optimized mesh ahead of time, so placement is near-instant and the " +
                "result is far cheaper to draw.");
            w.Bullet("Right-click a blueprint tile → 'Bake to prefab...' to bake it.");
            w.Bullet("Already baked? The entry reads 'Re-bake to prefab...' — use it after changing the source file.");
            w.Bullet("Baking runs in the background; the game stays responsive. Large blueprints take minutes.");
            w.Bullet("Requires [Performance] EnableBlueprintMeshBake.");
            w.Bullet("After a bake completes, relaunch to use the new prefab from the Baked Meshes tab.");
            w.Divider();

            w.SubHeader("Bulk & Diagnostics");
            w.Bullet("fap_bake_all — bakes every registered blueprint and writes a full report to " +
                "config/FiresAdminPrefabs/Diagnostics/bake_all_report.txt.");
            w.Bullet("fap_rebake_all — bakes every registered blueprint into a fresh bundle, replacing existing bakes " +
                "(use after a bake-pipeline change).");
            w.Bullet("fap_bake_diagnose <name> — explains why pieces do or don't land in the combined mesh, " +
                "without baking.");
            w.Divider();

            w.SubHeader("Transparent Pieces");
            w.Paragraph("[Performance] BakeExcludeTransparentPieces keeps glass and other transparent pieces out of " +
                "the combined mesh. Merged transparent geometry sorts as one object, which makes glass draw " +
                "through walls — excluded pieces stay as live children and render correctly.");
            w.Divider();

            w.SubHeader("Detail & Shadows");
            w.Paragraph("A bake is cut into 64m cubes, each carrying full-detail geometry and a low-detail level " +
                "built from the source pieces' own lowest LOD. A cube past [Performance] BakedLodLowDetailDistance " +
                "(90m) draws the cheap level, so standing inside a megabuild costs the few cubes around you " +
                "instead of the whole thing.");
            w.Bullet("BakedLodShadowProxy — full-detail geometry casts no shadows; the low-detail level casts them " +
                "for it. Shadows are re-rendered once per cascade, so this is the single biggest saving on a " +
                "megabuild. Thin details stop casting.");
            w.Bullet("BakedLodCullDistance — 0 means a bake never stops drawing, which is what keeps a castle on " +
                "the horizon.");
            w.Bullet("Bundles baked before FAP 1.1.63 are refused on load — run fap_rebake_all once.");
        }

        private static void BuildBakedMeshes(HelpContentWriter w)
        {
            w.Header("Baked Meshes");
            w.Paragraph("The Baked Meshes tab holds the results of blueprint baking — one first-class placeable " +
                "prefab per baked blueprint. Clicking a tile and placing drops the whole combined structure as a " +
                "single object, with no per-piece spawn loop.");
            w.Divider();

            w.Bullet("Entries appear here after a bake completes AND you relaunch the game.");
            w.Bullet("Baked structures are dramatically cheaper to render than the equivalent loose pieces.");
            w.Bullet("The trade-off: a baked mesh is static. Individual pieces can't be edited or removed — " +
                "keep the original blueprint if you need to change it later.");
            w.Bullet("Each bake is saved as a .bakebundle in Assets/ with a .bakemanifest in Manifests/. Both folders " +
                "sync to clients, so you can bake once on the server and every player downloads the finished mesh " +
                "instead of baking it themselves. A large blueprint's bundle can run to hundreds of MB.");
        }

        // ── Server (Admin) ──────────────────────────────────────────────

        private static void BuildHammerToggles(HelpContentWriter w)
        {
            w.AdminHeader("Hammer Toggles");
            w.Paragraph("Global placement behaviors under [HammerToggles], applied to pieces FAP injected. Vanilla " +
                "pieces keep their author-set behavior. These are server-synced when ServerEnforced is on.");
            w.AdminDivider();

            w.Bullet("ForceClipGround — pieces may sink into terrain instead of being pushed above it.");
            w.Bullet("ForceClipEverything — disables placement collision checks entirely; place anything anywhere, " +
                "overlapping whatever you like.");
            w.Bullet("DisableStaticPhysics — stops placed props from ground-snapping ~20s after placement. Without " +
                "this, carefully positioned objects drift to terrain height. The suppression survives zone " +
                "unload/reload.");
            w.Bullet("ForcePersistence — marks placed pieces persistent so they survive world save/load.");
            w.Bullet("SuppressRandomRotation — stops prefabs that randomize their own rotation from doing so, " +
                "so what you preview is what you place.");
            w.Bullet("SwapFracOnPlace — swaps fractured/destroyed variants for their intact versions at placement.");
            w.Bullet("EffectsOverrideTTL — overrides the self-destruct timer on effect prefabs.");
            w.Bullet("ItemStandHideStandVisual — hides the stand mesh on item-stand pieces.");
        }

        private static void BuildPersistence(HelpContentWriter w)
        {
            w.AdminHeader("Persistence & EWP Respawns");
            w.Paragraph("Two different systems, and which one you want depends on whether your players have FAP.");
            w.AdminDivider();

            w.SubHeader("FAP Persistence");
            w.Paragraph("[HammerToggles] ForcePersistence marks placed pieces persistent directly. Simple, but " +
                "the behavior only exists on machines running FAP.");
            w.Divider();

            w.SubHeader("EWP-Backed Persistence (vanilla-safe)");
            w.Paragraph("With [General] UseEwpForPersistence, FAP instead writes rules that ExpandWorldPrefabs " +
                "enforces server-side. Because the SERVER does the work, vanilla clients see correct behavior too. " +
                "This is also what drives monster/chest respawn timers.");
            w.Bullet("Rules are written to config/expand_world/expand_prefabs_FAP*.yaml.");
            w.Bullet("Preset data is written to config/data/data_FAP*.yaml.");
            w.AdminDivider();

            w.SubHeader("Getting Rules Onto A Dedicated Server");
            w.Paragraph("This is the step people miss. As an admin you are a CLIENT — those yaml files are written " +
                "on YOUR machine, not the server's, so respawns never fire until they're pushed up.");
            w.Bullet("fap_push_presets — pushes your FAP preset + EWP rule yaml to the connected server.");
            w.Bullet("[Sync] AutoPushOnPlace — does it automatically when you place a respawning chest/monster " +
                "or save a preset. Debounced so bulk building doesn't flood the connection.");
            w.Bullet("The server applies them and reloads without a restart.");
            w.AdminDivider();

            w.SubHeader("Maintenance");
            w.Bullet("fap_ewpstatus — shows the FAP yaml paths and their current sizes.");
            w.Bullet("fap_cleanewp — drops stale per-prefab entries left by older FAP versions.");
            w.Bullet("fap_wipeewp confirm — deletes the FAP yaml files so they're rewritten fresh on next load.");
        }

        private static void BuildAssetSync(HelpContentWriter w)
        {
            w.AdminHeader("Asset Bundles & Sync");
            w.Paragraph("FAP loads extra prefabs from asset bundles and can distribute them to every connecting " +
                "player automatically, so you don't have to hand out files.");
            w.AdminDivider();

            w.SubHeader("Adding Bundles");
            w.Bullet("Drop bundles into config/FiresAdminPrefabs/Assets/ — the folder is created at startup.");
            w.Bullet("fap_assetsdir — prints the absolute path and lists what's currently there.");
            w.Bullet("Their prefabs are injected into the hammer on next world load.");
            w.Bullet("[General] SortHammerByMod gives each bundle its own tab.");
            w.Bullet("fap_import_yoink — pulls bundles/manifests from FiresSteamyDumps' scan output.");
            w.AdminDivider();

            w.SubHeader("Server → Client Sync");
            w.Paragraph("When a player connects, their client compares a SHA-256 manifest of its asset folders " +
                "against the server's and downloads only what differs. Synced folders: Assets, Manifests, Shaders " +
                "and Textures — baked blueprints travel with them as .bakebundle and .bakemanifest files.");
            w.Bullet("Transfers stream from disk to disk in small blocks, so neither side loads a whole bundle " +
                "into memory and the game stays responsive during a multi-hundred-megabyte sync.");
            w.Bullet("Files are verified by checksum and committed atomically — an interrupted transfer can't " +
                "leave a half-written bundle.");
            w.Bullet("Sync is additive: files you have that the server doesn't are never deleted.");
            w.Bullet("After new assets arrive you'll be prompted to reconnect — asset bundles can't be swapped " +
                "in mid-session, so the new content loads on your next connect.");
            w.AdminDivider();

            w.SubHeader("Version Matching");
            w.Paragraph("FAP enforces a matching version between client and server. If you update FAP on the " +
                "server, players on an older build are refused at join until they update — that's intentional, " +
                "since the sync protocol changes between versions.");
        }

        private static void BuildConfigReference(HelpContentWriter w)
        {
            w.AdminHeader("Config Reference");
            w.Paragraph("config/com.Fire.adminprefabs.cfg. Most settings apply live; the ones that rebuild the " +
                "piece table need a world reload.");
            w.AdminDivider();

            w.SubHeader("[General]");
            w.Bullet("ServerEnforced — master lock. When true on the server, every synced setting below is forced " +
                "to the server's value on all clients.");
            w.Bullet("AdminOnlyMode — restricts FAP placement to admins.");
            w.Bullet("VanillaFriendly — disables features that need FAP on every client.");
            w.Bullet("UseEwpForPersistence — route persistence through ExpandWorldPrefabs instead of FAP.");
            w.Bullet("SortHammerByMod — one hammer tab per asset bundle.");
            w.Bullet("VerboseLogging — detailed diagnostics in the log.");
            w.Bullet("CustomPersistentPrefabs / BundleClutterAllOf / BundleNatureShader(AllOf) — per-bundle " +
                "classification overrides.");
            w.AdminDivider();

            w.SubHeader("[BuildMenu]");
            w.Bullet("WindowWidth / WindowHeight / WindowScale / WindowPosX / WindowPosY — build menu size, zoom " +
                "and position; written by dragging the window and its resize grip. Local, never server-forced.");
            w.Bullet("SortVanillaFirst — vanilla pieces listed before modded ones.");
            w.Bullet("HideAdminGatedPieces — hide pieces the player can't place.");
            w.AdminDivider();

            w.SubHeader("[Performance]");
            w.Bullet("EnableBlueprintMeshBake — enables the blueprint bake pipeline.");
            w.Bullet("BakeExcludeTransparentPieces — keep glass out of combined meshes (recommended).");
            w.Bullet("BakedLodLowDetailDistance — metres before a bake's 64m cube drops to its cheap geometry.");
            w.Bullet("BakedLodCullDistance — metres before a bake stops drawing; 0 (default) means never.");
            w.Bullet("BakedLodShadowProxy — shadows cast by the cheap geometry instead of the full-detail mesh.");
            w.AdminDivider();

            w.SubHeader("[Clutter] / [FxClear] / [UI] / [Sync]");
            w.Bullet("Clutter: PaintRadius, PaintDensity — the clutter brush.");
            w.Bullet("FxClear: Radius, WatchdogDurationSec, WatchdogTickIntervalSec — orphan-FX sweeper.");
            w.Bullet("UI: ConfigWindowScale — size of the right-click config windows (0.7-2.5); also set by " +
                "dragging a config window's corner.");
            w.Bullet("Sync: AutoPushOnPlace — auto-push preset/EWP yaml to the server on placement.");
        }

        private static void BuildFileLayout(HelpContentWriter w)
        {
            w.AdminHeader("File & Folder Layout");
            w.CodeBlock(
                "BepInEx/config/\n" +
                "  com.Fire.adminprefabs.cfg          FAP settings\n" +
                "  FiresAdminPrefabs/\n" +
                "    Assets/                          asset bundles + .bakebundle bakes (synced)\n" +
                "    Manifests/                       bundle metadata + .bakemanifest (synced)\n" +
                "    Shaders/                         shader registry (synced)\n" +
                "    Textures/                        companion textures (synced)\n" +
                "    HammerTabs/                      YOUR tab layout (never synced)\n" +
                "      <TabName>/prefabs.txt          one prefab per line; LINE ORDER = tab order\n" +
                "      Misc/ Crafting/ Furniture/     vanilla tabs, folder-managed too\n" +
                "      BuildingWorkbench/ BuildingStonecutter/\n" +
                "      Blueprints/                    .blueprint / .vbuild files\n" +
                "      Ignore/prefabs.txt             hidden pieces\n" +
                "      Hidden_<name>/                 hidden tab\n" +
                "      Favorites/prefabs.txt          your favorites (bookmarks)\n" +
                "    IconCache/                       rendered piece icons (local)\n" +
                "    GhostMeshCache/                  blueprint placement previews (local)\n" +
                "    Diagnostics/                     bake reports\n" +
                "  data/data_FAP_presets.yaml         monster presets\n" +
                "  data/data_FAP_chests.yaml          container presets\n" +
                "  expand_world/expand_prefabs_FAP*.yaml   EWP respawn rules");
            w.AdminDivider();
            w.Paragraph("Only the folders marked (synced) are server-authoritative and pushed to clients on connect. " +
                "HammerTabs, the caches and Diagnostics stay per-player — your tab layout and favorites are yours alone.");
        }

        // ── Commands (Admin) ────────────────────────────────────────────

        private static void BuildCommands(HelpContentWriter w)
        {
            w.AdminHeader("Admin Commands");
            w.Paragraph("All FAP commands are prefixed fap_. Type them in the console (F5).");
            w.AdminDivider();

            w.SubHeader("Assets & Icons");
            w.Bullet("fap_assetsdir — print the bundle-assets folder path and list its contents.");
            w.Bullet("fap_import_yoink [force] — import bundles/manifests from FiresSteamyDumps' scan output.");
            w.Bullet("fap_reload_icons — clear the icon cache and re-render every piece icon.");
            w.AdminDivider();

            w.SubHeader("Blueprint Baking");
            w.Bullet("fap_bake_all — bake every registered blueprint + write a diagnostic report.");
            w.Bullet("fap_rebake_all — re-bake every registered blueprint, replacing existing bundles.");
            w.Bullet("fap_bake_diagnose <name> [filter] — explain bake inclusion without baking.");
            w.AdminDivider();

            w.SubHeader("Persistence / EWP");
            w.Bullet("fap_push_presets — push preset + EWP rule yaml to the server (admin, client-side).");
            w.Bullet("fap_ewpstatus — show FAP yaml paths and sizes.");
            w.Bullet("fap_cleanewp — drop stale per-prefab entries from older versions.");
            w.Bullet("fap_wipeewp confirm — delete the FAP yaml files and regenerate.");
            w.Bullet("fap_clear_legacy_persistence — strip FAP_persist from every ZDO in the world.");
            w.AdminDivider();

            w.SubHeader("Cleanup & Diagnostics");
            w.Bullet("fap_clear_orphan_fx — sweep orphaned status-effect FX left in the world.");
            w.Bullet("fap_fx_dump — list every renderer-bearing object near you with its lineage.");
            w.Bullet("fap_recategorize_clutter — fix clutter prefabs parked in the wrong tab.");
            w.Bullet("fap_clean_stale_clutter — remove stale persistent clutter-effect ZDOs.");
        }

        private static void BuildConfigPush(HelpContentWriter w)
        {
            w.AdminHeader("Config Push / Pull");
            w.Paragraph("Provided by FiresUnifiedCore, not FAP itself, but it's the tool you'll reach for whenever " +
                "you need to edit server-side config without FTP access. Works for ANY mod's config files.");
            w.AdminDivider();

            w.Bullet("pushconfigs <pattern> — send config file(s) from your machine UP to the server.");
            w.Bullet("Exact file: pushconfigs expand_world_spawns.yaml");
            w.Bullet("Whole folder: pushconfigs expand_world");
            w.Bullet("Wildcard: pushconfigs expand_world*");
            w.Bullet("Press Tab to autocomplete against your config folder.");
            w.AdminDivider();

            w.Paragraph("Admin-only, and the push goes only to the server — it is never echoed back to clients, so " +
                "it can't trigger a reload loop. The receiving mod picks the file up with its own file watcher. " +
                "Use it for ExpandWorld spawns/locations/events yaml, EWP rules, or any other server config.");
            w.Paragraph("Do NOT use it for FiresRPGmaker's quest/dialogue data — that has its own pushcfgs command " +
                "with merge handling, and a plain overwrite there can wipe the server's quest database.");
        }

        private static void BuildCompatibility(HelpContentWriter w)
        {
            w.AdminHeader("Compatibility Notes");
            w.SubHeader("Infinity Hammer");
            w.Paragraph("FAP and Infinity Hammer are made to coexist. The grid geometry is patched " +
                "SearsCatalog-style — the vanilla methods themselves answer with the live column/row " +
                "count — so IH's selection, copy, and zoop always agree with what you clicked, at any " +
                "grid size.");
            w.Bullet("ZOOP works on FAP-injected and bundle pieces: FAP feeds IH the piece dimensions " +
                "it needs at load, so zooping steps correctly without running hammer_measure.");
            w.Bullet("hammer_measure is made SAFE: FAP replaces it with a batched version that can't " +
                "crash on a large modded prefab set (a few prefabs per frame, heavy prefabs skipped).");
            w.Bullet("Wheel-scrolling the grid never disturbs IH's placement ghost — the scroll input " +
                "is muted from vanilla's tab-cycling entirely.");
            w.Bullet("FAP defers to IH for pieces IH places — FAP's placement toggles don't fight it.");
            w.Bullet("IH's own tabs are laid out by IH; FAP only supplies the grid.");
            w.AdminDivider();

            w.SubHeader("SearsCatalog");
            w.Paragraph("If SearsCatalog is installed it takes over the build panel entirely and FAP's grid stands " +
                "down. Both do the same job; running both would mean two mods fighting over one layout.");
            w.AdminDivider();

            w.SubHeader("ExpandWorld");
            w.Paragraph("EWP handles FAP's respawn rules server-side. EWD/EWS supply world data. FAP writes rules " +
                "into their config files rather than duplicating their functionality.");
            w.AdminDivider();

            w.SubHeader("Dedicated Servers");
            w.Bullet("FAP must be installed on BOTH the server and clients, at MATCHING versions.");
            w.Bullet("Client-only UI features are skipped automatically on a headless server.");
            w.Bullet("Remember that anything you author as an admin is written on YOUR machine — push it to the " +
                "server (fap_push_presets, or pushconfigs for other mods' files).");
        }
    }
}
