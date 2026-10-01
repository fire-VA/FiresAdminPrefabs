using BepInEx.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using FiresCore.Sync;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Managers
{
    // Shader installed on nature-detected bundle prefabs. Bundle materials usually
    // ship shaders built against a different Unity version, which renders foliage as
    // the broken alpha-cutout look; Standard is the fallback for bundles whose
    // texture channel layout the vegetation shader misreads.
    public enum BundleNatureShaderMode
    {
        Vegetation,
        Standard,
    }

    // Every entry's player-facing documentation is its Bind() description — that is
    // what shows in the config UI, so it is not repeated here. Comments below cover
    // only what the description can't say.
    public class ConfigManager
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ?? (_instance = new ConfigManager());

        private const double SelfWriteSuppressSeconds = 3.0;
        private const float ConfigReloadDebounceSeconds = 2f;
        private const double ConfigReloadMinIntervalMs = 2000;

        private FileSystemWatcher _configWatcher;
        private DateTime _lastSelfWriteUtc = DateTime.MinValue;
        private DateTime _lastConfigUpdate = DateTime.MinValue;
        private Coroutine _reloadCoroutine;

        // ────── General ──────
        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerEnforced;
        public ConfigEntry<bool> configAdminOnlyMode;
        public ConfigEntry<bool> configRegisterPiecesForNonAdmins;
        public ConfigEntry<bool> configWackyDatabaseCompat;
        public ConfigEntry<string> configVanillaFriendlyEffectAllowlist;
        public ConfigEntry<bool> configVanillaFriendly;
        public ConfigEntry<bool> configUseEwpForPersistence;
        public ConfigEntry<bool> configSortHammerByMod;
        public ConfigEntry<BundleNatureShaderMode> configBundleNatureShader;
        public ConfigEntry<string> configBundleNatureShaderAllOf;
        public ConfigEntry<string> configBundleClutterAllOf;
        public ConfigEntry<string> configCustomPersistentPrefabs;

        // ────── Performance ──────
        public ConfigEntry<bool> configEnableBlueprintBake;
        public ConfigEntry<bool> configBakeExcludeTransparent;
        public ConfigEntry<float> configBakedLodLowDetailDistance;
        public ConfigEntry<float> configBakedLodCullDistance;
        public ConfigEntry<bool> configBakedLodShadowProxy;
        public ConfigEntry<int> configGhostCacheBudgetMB;
        public ConfigEntry<int> configGhostCacheWarnPerBlueprintMB;
        public ConfigEntry<UnityEngine.ThreadPriority> configBundleLoadingBudget;
        public ConfigEntry<bool> configInjectionSingleWalk;
        public ConfigEntry<string> configDediSkipRenderMeshes;
        public ConfigEntry<string> configClientLazyRenderMeshes;
        public ConfigEntry<string> configRemovedPrefabPlaceholders;

        // ────── Hammer tabs ──────
        public ConfigEntry<string> configTabDisplayNames;

        // ────── Clutter / FX brushes ──────
        public ConfigEntry<float> configClutterPaintRadius;
        public ConfigEntry<float> configClutterPaintDensity;
        public ConfigEntry<float> configFxClearRadius;
        public ConfigEntry<float> configFxClearDurationSec;
        public ConfigEntry<float> configFxClearTickIntervalSec;

        // ────── UI ──────
        public ConfigEntry<float> configConfigWindowScale;
        public ConfigEntry<float> configBuildMenuWidth;
        public ConfigEntry<float> configBuildMenuHeight;
        public ConfigEntry<float> configBuildMenuScale;
        public ConfigEntry<float> configBuildMenuPosX;
        public ConfigEntry<float> configBuildMenuPosY;
        public ConfigEntry<bool> configSortVanillaFirst;
        public ConfigEntry<bool> configHideAdminGatedPieces;
        public FiresCore.Input.KeyBinding configBuildMenuContextMenuKey;

        // ────── Sync ──────
        public ConfigEntry<bool> configAutoPushOnPlace;

        // ────── Hammer toggles ──────
        // Applied at placement time to every piece FAP injected; vanilla pieces keep
        // their author-set flags. Read live, so a flip lands on the next placement.
        public ConfigEntry<bool> configHammerForceClipGround;
        public ConfigEntry<bool> configHammerForceClipEverything;
        public ConfigEntry<bool> configHammerDisableStaticPhysics;
        public ConfigEntry<bool> configHammerForcePersistence;
        public ConfigEntry<bool> configHammerSuppressRandomRotation;
        public ConfigEntry<bool> configHammerSwapFracOnPlace;
        public ConfigEntry<bool> configHammerEffectsOverrideTTL;
        public ConfigEntry<bool> configItemStandHideStandVisual;

        private HashSet<string> _bundleNatureShaderAllOfSet;
        private string _bundleNatureShaderAllOfRaw;
        private HashSet<string> _bundleClutterAllOfSet;
        private string _bundleClutterAllOfRaw;

        public bool IsBundleInNatureShaderAllOf(string bundleName) =>
            IsBundleListed(bundleName, configBundleNatureShaderAllOf,
                ref _bundleNatureShaderAllOfSet, ref _bundleNatureShaderAllOfRaw);

        public bool IsBundleInClutterAllOf(string bundleName) =>
            IsBundleListed(bundleName, configBundleClutterAllOf,
                ref _bundleClutterAllOfSet, ref _bundleClutterAllOfRaw);

        // Re-parsed only when the raw string changes, so the init scan doesn't split
        // and lowercase the list once per prefab.
        private static bool IsBundleListed(string bundleName, ConfigEntry<string> entry,
            ref HashSet<string> cachedSet, ref string cachedRaw)
        {
            if (string.IsNullOrEmpty(bundleName)) return false;

            string raw = entry?.Value ?? string.Empty;
            if (cachedSet == null || cachedRaw != raw)
            {
                cachedRaw = raw;
                cachedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in raw.Split(','))
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length > 0) cachedSet.Add(trimmed);
                }
            }
            return cachedSet.Contains(bundleName);
        }

        public void Initialize(ConfigFile config)
        {
            configVerboseLogging = config.Bind("General", "VerboseLogging", false,
                "Enable detailed debug logs.");
            configServerEnforced = config.Bind("General", "ServerEnforced", true,
                "Master ConfigSync lock. When true on the server, every synced config below (AdminOnlyMode, hammer toggles, etc.) is forced to the server's value on all clients — players can't override locally. When false, each client uses their own local values. Server-authoritative: only the server's setting matters.");

            configAdminOnlyMode = config.Bind("General", "AdminOnlyMode", true,
                "When true, only server-recognized admins get the expanded hammer (vanilla prefabs as buildable, custom categories, scale/TTL/static-physics toggles). Non-admins still get the search bar across vanilla pieces and server-enforced placement configs. Server-synced — flipping this server-side re-gates every connected client.");

            configRegisterPiecesForNonAdmins = config.Bind("General", "RegisterPiecesForNonAdmins", false,
                "Only matters while AdminOnlyMode is true. When true, a non-admin's hammer still gets every prefab REGISTERED as a build piece — they just never appear in that player's build menu, search or Show All. Mods that only recognise registered build pieces (WackysDatabase clones, for example) then work for everyone, and pieces cloned from them render for players instead of being invisible. When false (default), a non-admin's hammer is left completely vanilla. Server-synced. Registration happens during world load, so a flip applies on the client's next world entry; the hiding itself applies at the next menu refresh.");

            configWackyDatabaseCompat = config.Bind("General", "WackysDatabaseCompat", true,
                "Only matters with WackysDatabase installed. Lets a WDB clone resolve a FAP piece as its source even though FAP's hammer scan runs later than WDB's lookup, applies FAP's own vanilla-friendly and admin rules to the clone as if it were its source, and refuses blueprint stubs, stand-ins, bake parents and bundle prefabs as clone sources, since a clone of one saves a piece nothing can resolve. Turn it off to leave WackysDatabase entirely alone. Server-synced.");

            configVanillaFriendlyEffectAllowlist = config.Bind("General", "VanillaFriendlyEffectAllowlist", "",
                "Effects VanillaFriendly should still offer, beyond the ones FAP detects as safe on its own. Comma-separated prefab names (e.g. 'vfx_Wet, fx_Lightning'); a name written with a leading '-' drops one of FAP's built-ins instead, for an effect that turns out not to survive on a vanilla client. Only add effects you have confirmed still play for someone without this mod — a wrong entry ships broken FX in every blueprint shared from this server. Server-synced, applies live.");

            configVanillaFriendly = config.Bind("General", "VanillaFriendly", false,
                "When true, disables features that need this mod loaded on every client to display correctly (PS/Audio looping, scale sync, TimedDestruction cancel). The expanded hammer + placement configs still work; placed effects just won't loop or sync scale to vanilla clients. Pair with UseEwpForPersistence to get respawn-on-destroy behavior via ExpandWorldPrefabs instead.");

            configUseEwpForPersistence = config.Bind("General", "UseEwpForPersistence", false,
                "When true (requires ExpandWorldPrefabs on the server), effect-eligible placements get tagged with FAP_persist=1 so EWP respawns them on destroy. The single universal rule in expand_prefabs_FAP.yaml + the shared FAP_Respawn data entry in data_FAP.yaml are written once at mod load by every side that runs FAP (client, host, or dedicated server); no client→server push needed. Independent of VanillaFriendly — VanillaFriendly only controls which tabs surface modded content; EWP persistence is its own thing and works in either mode.");

            configSortHammerByMod = config.Bind("General", "SortHammerByMod", false,
                "When true, prefabs loaded from external bundles in BepInEx/config/FiresAdminPrefabs/Assets/ get one hammer category per source bundle (e.g. a 'biomeflora' tab for prefabs from the biomeflora bundle) — like MoreVanillaBuildPrefabs / Hammer Time's source-mod grouping. When false, bundle prefabs land in the generic CreatorShop category alongside vanilla prefabs. Client-only preference; flips re-bucket pieces live without a world reload.");

            configBundleNatureShader = config.Bind("General", "BundleNatureShader", BundleNatureShaderMode.Vegetation,
                "Shader installed on nature-detected bundle prefabs (trees, bushes, plants, pickables, mine-rocks) to fix the broken-alpha-cutout look bundle materials usually ship with. Vegetation (default) = Valheim's Custom/Vegetation; Standard = Unity Standard in cutout mode (revert option if Vegetation produces wrong textures on a particular bundle). Flips re-apply live, no world reload needed.");

            configEnableBlueprintBake = config.Bind("Performance", "EnableBlueprintMeshBake", false,
                "Shows the 'Bake to prefab...' action when you right-click a blueprint in the hammer. Baking is explicit and per-blueprint — nothing is baked just by placing one, and the fap_bake_diagnose / fap_bake_all commands work whether this is on or off. Baking combines the blueprint's static pieces into roughly one mesh per material and writes a .bakebundle into config/FiresAdminPrefabs/Assets with a .bakemanifest sidecar; the result appears on the Baked Meshes tab after a RELAUNCH, because bundles are registered at world load. Interactive pieces (doors, chests, fireplaces, signs, pickables, plants, item stands, wards) are kept aside and spawned as real objects when the baked piece is placed, so they still work; the combined geometry keeps its colliders but is invulnerable — no per-piece damage or salvage, and hammering any part removes the whole placement. Multiplayer: the server and every other client need that .bakebundle too (fap_push_bundle sends it), or the placement is a missing prefab to them. Local setting, not server-synced.");

            configBakeExcludeTransparent = config.Bind("Performance", "BakeExcludeTransparentPieces", true,
                "When ON, blueprint pieces with transparent-queue materials (renderQueue >= 2500) — and FiresGlass windows by name — are kept OUT of the combined bake mesh and spawn as live pieces instead. Combining transparent geometry breaks per-piece depth sorting (glass panes draw through walls / in the wrong order once merged). Costs a few live objects per blueprint; visual correctness for stained glass. Applies to NEW bakes; re-bake existing blueprints to apply.");

            configBakedLodLowDetailDistance = config.Bind("Performance", "BakedLodLowDetailDistance", 90f,
                "Distance in metres at which a baked blueprint's geometry switches to its low-detail level. A bake is split into 64m cubes and each cube switches on its own, measured from its middle, so standing inside a megabuild keeps full detail around you and drops the rest. Below about 80 the cubes near you start switching while you are still standing in them. Higher is closer to the old always-full-detail behaviour. Measured against Valheim's default 65 field of view, and your graphics quality's LOD Bias scales it the same way it scales every other LOD in the game. Applies on the next world load.");

            configBakedLodCullDistance = config.Bind("Performance", "BakedLodCullDistance", 0f,
                "Distance in metres past which a baked blueprint stops drawing entirely. 0 (default) means never — baked pieces are distant-rendered on purpose, so a castle stays on the horizon. Set a value only if you want very large bakes to vanish beyond a range. Applies on the next world load.");

            configBakedLodShadowProxy = config.Bind("Performance", "BakedLodShadowProxy", true,
                "When ON, a baked blueprint's full-detail geometry casts no shadows; its low-detail geometry casts them instead. Shadows are re-rendered once per cascade, so a megabuild that draws 18 million triangles was drawing well over a hundred million a frame. Shadow shapes get coarser near the build; thin details like railings stop casting. Turn OFF to go back to shadows from the full-detail mesh. Applies on the next world load.");

            configGhostCacheBudgetMB = config.Bind("Performance", "GhostCacheBudgetMB", 12288,
                "Total size in megabytes the blueprint ghost cache is allowed to occupy. Picking a blueprint in the hammer shows a placement preview, and that preview is baked once and kept on disk so the next pick is instant; a large library can run to several gigabytes, and before this setting existed nothing ever deleted an entry. When a fresh bake pushes the folder over this number, the least-recently-picked entries are deleted until it fits. Every entry belongs to a blueprint you still have - there is no dead weight to collect - so an eviction costs a rebake the next time that blueprint is picked. Treat this as a runaway guard rather than a tight cap: the default is comfortably above a large library so it rarely fires. 0 disables eviction entirely. The cache lives outside BepInEx/config so it is never swept into a profile or modpack export. Read live, no reload needed.");

            configGhostCacheWarnPerBlueprintMB = config.Bind("Performance", "GhostCacheWarnPerBlueprintMB", 512,
                "Logs a warning when a single blueprint's ghost cache is written at or above this many megabytes. Purely a report - the file is always written and the placement preview you see never changes. It exists because a handful of megabuilds dominate the cache: on one 516-entry library, 24 files held 77% of the disk and the largest single blueprint was 1.8 GB. The warning names them so you can decide what to keep. 0 turns the warning off. Read live, no reload needed.");

            configBundleLoadingBudget = config.Bind("Performance", "BundleLoadingBudget", UnityEngine.ThreadPriority.High,
                "How much time per frame Unity may spend loading this mod's asset bundles in the background, while it is loading them. Valheim pins background loading to its lowest setting unless something asks for more, which means a large Assets folder barely loads at all before the world does - and then the whole remainder is paid in one frozen frame as you join, measured at over ten seconds on a 233-bundle folder. Raising it lets that work finish during the menu and the load screen instead. Low is the game's own default and effectively restores the old behaviour; High is the fastest. The request goes through Valheim's own loading-budget controller, which always applies the highest outstanding request, so this never takes time away from the game's own loading - and it is released the moment the bundles are in, so nothing else in the session is affected. If the main menu feels less smooth while a very large Assets folder loads, step this down. Read when loading starts.");

            configInjectionSingleWalk = config.Bind("Performance", "InjectionSingleWalk", true,
                "How bundle prefabs are checked as they join the world: missing scripts, vanilla singletons, EffectArea and Pickable heals, the network view. On: one walk of each prefab's hierarchy feeds every check. Off: the older way, a separate walk per check. Both make exactly the same repairs; the load log prints the time of each step either way, so the two can be compared, and Off is the fallback. Takes effect at the next world load.");

            configDediSkipRenderMeshes = config.Bind("Performance", "DediSkipRenderMeshes", "ConsMassiveTower,CastleTowerMassive",
                "Dedicated server only. Comma-separated bundle prefab names whose render meshes the server drops right after loading their bundle. A server draws nothing, but a mesh loaded from a bundle still sits in its memory: the castle tower these two names share is 12.2 million vertices, about 800 MB on the server. Collision is separate (boxes and a small mesh) and is always kept, as is any mesh a collider uses, so players, building and support work exactly as before. Clients are never affected. Empty turns it off. Takes effect at the next server start.");
configRemovedPrefabPlaceholders = config.Bind("Compatibility", "RemovedPrefabPlaceholders", "ConsMassiveTower,CastleTowerMassive",
                "Comma-separated names of pieces that were REMOVED from the bundles. Worlds that already have one placed keep its save data, and without a prefab by that name the game would log 'Missing prefab hash' every frame near it. FAP registers an invisible, empty placeholder under each name that no loaded bundle provides, so those saves load quietly and the piece comes back if its bundle ever carries it again. Empty turns it off.");
            configClientLazyRenderMeshes = config.Bind("Performance", "ClientLazyRenderMeshes", "ConsMassiveTower,CastleTowerMassive",                "Clients only. Comma-separated bundle prefab names whose render-mesh DATA is unloaded right after their bundle loads and comes back from the bundle the first time one of them is built or loaded near you. The castle tower these two names share is 12.2 million vertices, about 1.6 GB per client, and most sessions never see one. Its look and its collision are unchanged (colliders are never touched); only the memory waits until a tower is there. Empty turns it off. Takes effect at the next game start.");

            configBundleNatureShaderAllOf = config.Bind("General", "BundleNatureShaderAllOf", "biomeflora",
                "Comma-separated bundle filenames whose prefabs should ALL get the BundleNatureShader treatment, bypassing the per-prefab nature heuristic. Use for entirely-foliage bundles whose prefab names don't contain tree/bush/flower keywords (cacti, succulents, ferns named 'XYZ_BF'). Case-insensitive. Adding entries requires a world reload — flips of the BundleNatureShader mode itself are live, but this list controls which prefabs are eligible.");

            configBundleClutterAllOf = config.Bind("General", "BundleClutterAllOf", "newcluttermarch2022",
                "Comma-separated bundle filenames whose prefabs should ALL be routed to the dedicated 'Clutter' hammer tab — regardless of whether the prefab has an InstanceRenderer or appears in ClutterSystem.m_clutter. Use for entirely-clutter bundles (grass tufts, small decorations). Vanilla clutter and prefabs with InstanceRenderer are auto-detected and routed even when not on this list. Case-insensitive. Live toggle: flips re-categorize without a world reload.");

            configCustomPersistentPrefabs = config.Bind("General", "CustomPersistentPrefabs", "",
                "Comma-separated prefab names that should receive EWP-persistence treatment in addition to the auto-detected effect prefabs (vfx_/fx_/sfx_ + TimedDestruction). Use for vanilla prefabs that auto-destroy themselves but you want to come back via EWP respawn — e.g. 'lavabomb_rock1'. Names are exact (no wildcards). The mod also maintains a hidden curated list of these built-in; this config adds yours on top.");

            configTabDisplayNames = config.Bind("HammerTabs", "TabDisplayNames", "",
                "Custom display names for hammer tabs (per-client; the on-disk folder names are unchanged). Comma-separated 'FolderName=Display Name' pairs. Set in-game via right-click a user tab → Rename — you normally don't edit this by hand.");

            configClutterPaintRadius = config.Bind("Clutter", "PaintRadius", 4f,
                "Radius (in meters) of the clutter-paint brush. When a clutter piece is selected in the hammer, each click paints clutter in a circle of this radius around the cursor instead of placing a single instance. Matches RPGmaker's paint_clutter default. Live: flips take effect on next click.");

            configClutterPaintDensity = config.Bind("Clutter", "PaintDensity", 1f,
                "Density multiplier for the clutter-paint brush. 1.0 = default spacing, 2.0 = twice as dense, 0.5 = sparser. Higher values look lusher but spawn more GameObjects per click. Live: flips take effect on next click.");

            configFxClearRadius = config.Bind("FxClear", "Radius", 4f,
                "Radius (meters) of the FX-clear brush. When the FAP_FxClearTool stand-in piece (in the Effect tab) is selected and clicked, every ZNetView in the brush radius that's tagged with both fap_piece=1 and FAP_persist=1 has FAP_persist cleared to 0 and is destroyed. Shift+Scroll resizes live. Surgical scope: vanilla world objects and non-FAP placements are never touched.");

            configFxClearDurationSec = config.Bind("FxClear", "WatchdogDurationSec", 10f,
                "How long after a clear click the watchdog keeps re-scanning the same area for FX that EWP respawned during the destroy/sync race window. Server sometimes processes our destroy before our FAP_persist clear-sync arrives, so EWP fires the respawn rule before the clear lands. Watchdog catches those late respawns. 10s is comfortable for typical LAN; bump to 20-30s if your server is under load.");

            configFxClearTickIntervalSec = config.Bind("FxClear", "WatchdogTickIntervalSec", 0.5f,
                "Interval between watchdog re-scans during the duration window. 0.5s catches respawns within half a second; lower = quicker catch but more CPU per click. Clamped to >= 0.1s.");

            configConfigWindowScale = config.Bind("UI", "ConfigWindowScale", 1.0f,
                "Scale of the right-click configure windows (monster/container/clutter). Drag any corner of a config window to resize it; the chosen scale persists here. Range 0.7-2.5.");

            configBuildMenuWidth = config.Bind("BuildMenu", "WindowWidth", float.NaN,
                "Build menu window width. Set by SHIFT+dragging the bottom-right grip, which grows the grid; NaN = vanilla default.");

            configBuildMenuHeight = config.Bind("BuildMenu", "WindowHeight", float.NaN,
                "Build menu window height. Set by SHIFT+dragging the bottom-right grip, which grows the grid; NaN = vanilla default.");

            configBuildMenuScale = config.Bind("BuildMenu", "WindowScale", 1f,
                new ConfigDescription(
                    "Build menu zoom. Set by dragging the bottom-right grip without Shift. Range 0.7-2.5.",
                    new AcceptableValueRange<float>(0.7f, 2.5f)));

            // Screen position, not anchoredPosition: the window re-anchors to centre on
            // first attach, so a saved position means the same thing at any resolution.
            configBuildMenuPosX = config.Bind("BuildMenu", "WindowPosX", float.NaN,
                "Build menu window X position in pixels. Drag the window background to move it; NaN = vanilla default position.");

            configBuildMenuPosY = config.Bind("BuildMenu", "WindowPosY", float.NaN,
                "Build menu window Y position in pixels. Drag the window background to move it; NaN = vanilla default position.");

            configSortVanillaFirst = config.Bind("BuildMenu", "SortVanillaFirst", true,
                "When true, every hammer tab lists vanilla hammer pieces first, then FAP-injected vanilla prefabs, then bundle/modded prefabs — so vanilla nature objects (bushes, trees, stumps) stop drowning among modded content. Order within each group is unchanged. Live: takes effect on the next menu refresh.");

            configHideAdminGatedPieces = config.Bind("BuildMenu", "HideAdminGatedPieces", true,
                "When true, any piece whose recipe requires a cheat-sword (SwordCheat) is hidden from the hammer menu for non-admins. The recipe itself is the marker — any mod that gates a piece this way gets admin-only visibility automatically, no per-mod registration. Vanilla would otherwise still SHOW those pieces greyed out with the requirement listed, advertising content a player can never build. Admins are unaffected. Live: takes effect on the next menu refresh.");

            configBuildMenuContextMenuKey = new FiresCore.Input.KeyBinding(config, "BuildMenu", "ContextMenuKey", KeyCode.Mouse1,
                defaultShift: true,
                description: "Opens FiresAdminPrefabs' menu for the build menu piece or tab under the cursor (favorites, hide, move to tab, rename, and the rest). Default Shift + right click; Mouse1 is the right mouse button. ContextMenuKey_Ctrl / _Shift / _Alt pick the modifiers to hold, and a modifier left off must not be held. A plain right click stays vanilla's: it closes the build menu. Live.");

            configAutoPushOnPlace = config.Bind("Sync", "AutoPushOnPlace", true,
                "When ON, placing a respawning chest/monster (or saving/deleting a preset) automatically pushes FAP's preset yaml (config/data/data_FAP*.yaml) + EWP rule yaml (config/expand_world/expand_prefabs_FAP*.yaml) up to the connected server, debounced ~2s so bulk building doesn't spam. Only fires for admins connected to a REMOTE server (on a listen-host the files are already local). When OFF, push only via the 'fap_push_presets' console command or FiresRPGmaker's 'pushcfgs'. The server always admin-gates the inbound write, so this toggle only controls the client-side auto-trigger.");

            configHammerForceClipGround = config.Bind("HammerToggles", "ForceClipGround", true,
                "When true, every piece our mod injected into the Hammer is forced to clip into the\n" +
                "ground during placement. Routes the placement ghost through the raycast-clip path\n" +
                "vanilla uses for terrain modifiers, which is the only path that works reliably on\n" +
                "sparse-collider modded vegetation. Disable if you want vanilla placement collision\n" +
                "for our pieces.");
            configHammerForceClipEverything = config.Bind("HammerToggles", "ForceClipEverything", true,
                "When true, every piece our mod injected into the Hammer is forced to clip through\n" +
                "other pieces during placement. Lets you place vegetation inside structures, half-bury\n" +
                "rocks for shaping, etc. Disable if you want vanilla overlap rejection.");
            configHammerDisableStaticPhysics = config.Bind("HammerToggles", "DisableStaticPhysics", true,
                "When true, StaticPhysics components on pieces our mod injected are disabled at\n" +
                "placement time and a ZDO flag is written so the suppression survives zone reloads.\n" +
                "StaticPhysics is the SlowUpdate driver that re-snaps a placed prefab to ground\n" +
                "height ~20s after placement, which stomps the y-position you placed it at. Disable\n" +
                "this toggle to let vanilla ground-snapping run.");
            configHammerForcePersistence = config.Bind("HammerToggles", "ForcePersistence", true,
                "When true, ZNetView.m_persistent is forced to true on pieces our mod injected.\n" +
                "Required for effect-prefabs (vfx_/fx_) to survive zone unload — vanilla configures\n" +
                "their ZNetView for transient use only. Disable only if you want effects to evaporate\n" +
                "the way they would in a non-modded game.");
            configHammerSwapFracOnPlace = config.Bind("HammerToggles", "SwapFracOnPlace", true,
                "When true, placing a '<name>_frac' prefab via the hammer is automatically swapped\n" +
                "to its whole-mesh counterpart '<name>' if that prefab exists. Fractured rocks/mudpiles\n" +
                "are pre-shattered geometry (each chunk is its own collider + renderer) used by the\n" +
                "engine for destruction physics — they're enormous compared to the whole-mesh source.\n" +
                "When this is true you still see all the _frac entries in the hammer (placement\n" +
                "preview matches the non-frac mesh), and clicking place drops the cheap whole-rock.\n" +
                "Disable to place actual _frac prefabs verbatim (useful for the rare case where you\n" +
                "want a pre-shattered look as scenery).");
            configHammerSuppressRandomRotation = config.Bind("HammerToggles", "SuppressRandomRotation", true,
                "When true, RandomPieceRotation.Awake is suppressed during hammer placement so the\n" +
                "player's chosen rotation isn't stomped by a world-seeded random angle. Pieces like\n" +
                "webs and ground decals normally use RandomPieceRotation for natural-spawn variety;\n" +
                "this toggle preserves that variety in zone generators but bypasses it when YOU\n" +
                "place the piece via hammer. Disable to let vanilla random-rotation run for hammer\n" +
                "placements too.");
            configItemStandHideStandVisual = config.Bind("HammerToggles", "ItemStandHideStandVisual", false,
                "When true, every ItemStand in the world with an item attached hides its own mesh\n" +
                "visual (wall plate / shelf base) so only the attached item shows — clean floating-\n" +
                "item display for builders. Uses the same 'itemstand_hide' ZDO key as the\n" +
                "ItemStandsAllItems mod so the two are cross-compatible. Per-stand overrides via that\n" +
                "key (-1 force-visible, 0 inherit this config, 1 force-hidden) win over the global\n" +
                "toggle. Live: flips re-apply to every loaded stand within the next refresh tick.");
            configHammerEffectsOverrideTTL = config.Bind("HammerToggles", "EffectsOverrideTTL", true,
                "When true, effect prefabs (vfx_/fx_) placed via the hammer get their auto-destroy\n" +
                "timer cancelled AND any timed animation/sound is forced to loop indefinitely, so the\n" +
                "placed effect behaves like a permanent decorative prop instead of evaporating after\n" +
                "its built-in TTL. Specifically: TimedDestruction.CancelInvoke('DestroyNow') + the\n" +
                "component disabled; every ParticleSystem.main.loop set true; every AudioSource.loop\n" +
                "set true. A ZDO flag is written so the override survives zone reload (re-applied on\n" +
                "each Piece.Awake). Disable to let vanilla TTL/non-loop behavior run.");

            RegisterLiveReapplyHandlers();
            SetupConfigWatcher(config);
        }

        // Toggles that change which tab a piece belongs to all re-run the same
        // recategorize pass, so a flip lands without a world reload.
        private void RegisterLiveReapplyHandlers()
        {
            EventHandler recategorize = (_, __) => VAVanillaPieceManager.RecategorizeEligiblePieces();
            configVanillaFriendly.SettingChanged += recategorize;
            configUseEwpForPersistence.SettingChanged += recategorize;
            configSortHammerByMod.SettingChanged += recategorize;
            configBundleClutterAllOf.SettingChanged += recategorize;

            configVanillaFriendlyEffectAllowlist.SettingChanged += (_, __) =>
            {
                VAVanillaPieceManager.InvalidateVanillaFriendlyEffects();
                VAVanillaPieceManager.RecategorizeEligiblePieces();
            };

            configBundleNatureShader.SettingChanged += (_, __) =>
                ShaderReplacement.ReapplyBundleNatureShader();

            // Without this the toggle would wait for each stand's next refresh tick.
            configItemStandHideStandVisual.SettingChanged += (_, __) =>
            {
                try
                {
                    int touched = VerdantsAscent.Patches.VAItemStandHidePatch.ReapplyAll();
                    if (configVerboseLogging.Value)
                        Debug.Log($"{VaPieces.PluginName}: ItemStandHideStandVisual toggle — re-applied to {touched} loaded stand(s).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: ItemStandHideStandVisual SettingChanged handler threw: {ex.Message}");
                }
            };
        }

        // Picks up admin edits to the .cfg while the game runs. SettingChanged fires on
        // every write FAP itself makes (SaveOnConfigSet rewrites the file on the same
        // set), so the timestamp is what tells our own writes from an external edit.
        private void SetupConfigWatcher(ConfigFile config)
        {
            if (_configWatcher != null) return;

            string configPath = config.ConfigFilePath;
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;

            config.SettingChanged -= StampSelfWrite;
            config.SettingChanged += StampSelfWrite;

            _configWatcher = new FileSystemWatcher
            {
                Path = Path.GetDirectoryName(configPath),
                Filter = Path.GetFileName(configPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };

            _configWatcher.Changed += (_, __) => RequestReload();
            _configWatcher.Renamed += (_, __) => RequestReload();
        }

        private void StampSelfWrite(object sender, SettingChangedEventArgs e)
            => _lastSelfWriteUtc = DateTime.UtcNow;

        private bool IsRecentSelfWrite()
            => (DateTime.UtcNow - _lastSelfWriteUtc).TotalSeconds < SelfWriteSuppressSeconds;

        private void RequestReload()
        {
            if (IsRecentSelfWrite()) return;
            if (_reloadCoroutine != null) return;
            _reloadCoroutine = VaPieces.Instance.StartCoroutine(ReloadConfigAfterDelay());
        }

        private IEnumerator ReloadConfigAfterDelay()
        {
            yield return new WaitForSeconds(ConfigReloadDebounceSeconds);

            if ((DateTime.Now - _lastConfigUpdate).TotalMilliseconds < ConfigReloadMinIntervalMs
                || IsRecentSelfWrite())
            {
                _reloadCoroutine = null;
                yield break;
            }
            _lastConfigUpdate = DateTime.Now;

            Debug.Log($"{VaPieces.PluginName}: Config file changed, reloading...");

            var configFile = VaPieces.Instance.Config;
            bool saveOnSet = configFile.SaveOnConfigSet;
            configFile.SaveOnConfigSet = false;

            configFile.Reload();

            configFile.SaveOnConfigSet = saveOnSet;

            if (configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: Config reloaded and synced due to file change.");

            _reloadCoroutine = null;
        }

        // Also the admin-status hook: AdminSyncing invokes this by reflection when the
        // lock flips, which is FAP's chance to retry an init that was skipped because
        // the player wasn't recognized as admin yet (the admin RPC lands after plugin
        // init). InitializeOnce is idempotent.
        public void OnConfigLockChanged()
        {
            if (configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: Config lock changed.");

            try
            {
                if (ZNetScene.instance == null) return;

                VAVanillaPieceManager.InitializeOnce();

                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.UpdateAvailablePiecesList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: OnConfigLockChanged retry-init failed: {ex.Message}");
            }
        }

        // Called from VaPieces.OnDestroy. The watcher raises events on a thread-pool
        // thread, so leaving it live past plugin teardown means a callback can reach
        // a torn-down VaPieces.Instance.
        public void Dispose()
        {
            _configWatcher?.Dispose();
            _configWatcher = null;
        }
    }
}
