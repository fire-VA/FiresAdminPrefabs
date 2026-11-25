using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using FiresCore.Sync;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

#nullable disable

using FiresCore.Net;

namespace VerdantsAscent
{
    // FiresAdminPrefabs entry point. Loads external asset bundles, injects vanilla
    // and modded prefabs onto the hammer, and owns the mod's config + Harmony setup.
    //
    // FiresUnifiedCore is a hard dependency (NetworkObjectHelper, AdminSyncing,
    // ConfigSync, DistantRenderer). So is ValheimModding's shared YamlDotNet
    // (Thunderstore ValheimModding-YamlDotNet): FAP links YamlDotNet 16 and does not
    // ship it, and the preset stores load it in WaitForZNetReady, so without it that
    // coroutine dies and the hammer never initializes. BepInEx names the missing
    // package instead. Expand World Data is soft: without it the blueprint per-piece
    // data restore silently no-ops.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, MinimumCoreVersion)]
    [BepInDependency(YamlDotNetDetectorGUID)]
    [BepInDependency("expand_world_data", BepInDependency.DependencyFlags.SoftDependency)]
    // 1.1.269 (Fire's tester, 289x Jotunn RefreshCategories IndexOutOfRange): FAP loaded BEFORE Jotunn, so VAJotunnTabGuard.Prepare
    // found no Jotunn assembly and never patched. Soft-depend so Jotunn (when installed) always loads first.
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.SoftDependency)]
    public class VaPieces : BaseUnityPlugin
    {
        public static VaPieces Instance;
        public const string PluginGUID = "com.Fire.adminprefabs";
        public const string PluginName = "FiresAdminPrefabs";
        public const string PluginVersion = "1.1.445";   // 1.1.445 ([worldgen] 11-25, store release for Valheim 1.0.30; Fire: push the FAP update): = 1.1.439.1 with a 3-part version (stores); next free number, the unreleased 1.1.440-444 line renumbers above it. Code unchanged.
        // was 1.1.439.1: 1.1.439.1 ([worldgen] 11-25, Valheim 1.0.30; built on live 1.1.439, so the unreleased 1.1.440-444 work is NOT in it): ZDOExtraData.GetData now hands back FastIterableKeyValues (VARadiusSwapTool loops by index); InstanceRenderer.m_frustumCull is gone (dropped from the [PaintDiag] log line). No other change.
        // was 1.1.439: 1.1.439 ([ghost], [lead] 11-20 fire_bf1 capture: ProcessQueue GC.Alloc 68k calls / 3 MB per frame; Fire: "Only the garbage fix"): IsRequiredBySibling reuses one component list instead of a new GetComponents array per call, same result; icon rendering unchanged. | was 1.1.438 ([ghost], lead 11-19 perf audit): VATextureLibrary no longer LoadAllAssets<Texture2D>() every loaded bundle at ZNetScene.Awake (HandTest: 3609 textures / 82 bundles pulled into RAM, lazy bundles undone); it indexes asset NAMES only and loads a texture when a material asks for it (same first-bundle-wins priority, same in-memory fallback). The item-stand refill watcher goes only on PRESET stands (was every ItemStand, 4 ZDO reads a second each) and its keys are hashed once. // 1.1.437 ([ghost], lead 11-18, Fire VADN-FAT-1118 dedi log): no icon queue walk or "Queued N pieces for icon render" line without a graphics device (the renderer already refused every piece there).   // 1.1.436 ([ghost], lead 11-18, a rig client log line 2416): FapCoreFar removed (archived). Core has had no far systems since EBM 1.2.210 and FAT 0.2.1482 removed its Far Forest, so it had nothing to do in any mod combination; its "FAT is installed ..." Debug line was false.   //1.1.435 ([ghost], lead 11-18): FapCoreFar runs in Start (after every plugin loaded), so "FAT absent" is logged only when FAT really is absent; with FAT installed, one Debug line.   //1.1.434 ([ghost], lead 11-16 via [generator], VA DN dedi log): Pickable_Mushroom_Random_Farm_bal maps to Moss_bal like its non-farm sibling (was "1 Pickable(s) still have no item").   // 1.1.433 ([ghost], lead 11-16; Fire: FTS goes, placed windows must not vanish, clean in any mod combination): the 34 FiresGlass windows move into FAP (Managers\VAFiresGlass.cs): [visual]'s linear firesglass / firesglass_linux bundle in FapBundles (one per platform: PickPlatformTwins), same prefab names = same hashes, registered by the existing injection (skips a name another mod has), each set up as FTS did (collider, Piece, square icon, 2 Crystal + 1 Fine wood at the workbench), a normal player piece on the Hammer (never admin-gated, never lazy); with FTS installed its duplicate Hammer entries are removed. Panes only (no light projector). One Info line per world load: "[FiresGlass] N/34 windows registered ...".   // was 1.1.432:   // 1.1.432 ([generator], lead 11-16, Fire's client log: 117 x "No mesh data available for mesh Sphere on particle system ..." at the main menu): ClientNonReadableRenderMeshes keeps readable every mesh a particle system uses (ParticleSystemRenderer mesh / GetMeshes, shape mesh / meshRenderer / skinnedMeshRenderer) and never drops the CPU copy of Unity's shared primitive meshes (Sphere, Cube, Capsule, Cylinder, Plane, Quad), so particle systems in this or a later bundle can read them (VAExternalBundleLoader.MakeRenderMeshesNonReadable). 1.1.431 ([ghost], Fire 11-16, Arbby's Discord report): (1) Infinity Hammer freeze-select no longer jumps on the first nudge: the offset-centre shift is measured from the rotation the piece had when it froze (zero at freeze time, so IH's nudge step is exact), scaled by the ghost's scale; (2) FAP-made snap points are corners + centres only: no points along the edges (up to 231 on 10-20 m pieces), the [Hammer] Snap edge point spacing key is removed.   // was 1.1.430 ([generator], lead 11-16, Fire: "something WERE DOING is overriding the render distance and shortening it"): a bundle prefab on the cull-only LODGroup (no impostor: under 4 m) no longer vanishes inside the loaded area; its cull distance is max(BundleCullDistance, the loaded radius / lodBias), re-applied live when the graphics settings change.   // 1.1.429 ([ghost], lead 11-16 RigPerf-Full arrival: all 22 post-load FAP [Hitch] rows were the icon ProcessQueue on a cold cache): the icon flush + render drain start while the world loads and run at FapInitBudget 40 ms/frame behind the loading screen; in play 1 render per frame, only on frames under 16.7 ms; one Info line says where the drain ran. | was 1.1.428 ([ghost], lead 11-13 overnight): the lazy-bundle look-ahead walks only zones that entered the ring (and zones that were empty when walked), not all 49 zones on every zone change and every 10 s; one Debug line says why it scanned or skipped. // 1.1.427 ([ghost], Fire 11-12 "all ashwood or black marble variants should have an icon"): MissingRenderAssets no longer refuses vanilla's own empty MeshFilters (LOD low slots, destruction chunks: ashwood_wall_beam_26_alt has m_Mesh 0 on 5 of 8 in the 1.0 rip); the proxy leaves empty renderers out. // 1.1.426 ([ghost], [visual] G2): VAFarBakeSource hands EBM the baked-blueprint meshes (coarsest LOD per zone) by cache name through EbmApi.RegisterFarBakeSource / RaiseFarBakeReady (reflection, EBM 1.2.341+); VA_BakeParent leaves RegisterFarSkip when EBM takes them. Also: build-menu rows holding only repair/remove = Debug (was a player-visible Warning); icon renders refused for a prefab's own missing meshes/materials counted as "refused", not "UNEXPECTED other-null". // 1.1.425 ([ghost], [generator] Core 0.2.1234): DistantRenderer.Recheck (reflection) after each registration sync and when a lazy prefab becomes real; the registration gate reads ZNetScene's named-prefab table instead of GetPrefab (which realized lazy stubs). // 1.1.424 ([ghost], lead 11-12, Fire run_3: FAP init coroutines = 57 of 79 post-arrival hitch frames): hammer scan / stand-ins no longer wait for spawn, run under the loading screen at 40 ms/frame and at 2 ms/frame after (FapInitBudget, one Info line each), HashSet instead of List.Contains per stand-in add. // 1.1.423 ([ghost], lead 11-12): [Diagnostics] BuildMenuNativeTrace (default off) now gates the trace hooks by Prepare() (off = never patched); snap-default Player.Update prefix reads only with a piece in hand. Distant registration (Fire 8:4x PM): a prefab registers only when it resolves in this game's ZNetScene, synced at world load + first spawn. (The OnGUI-off-while-closed item was already done in 1.1.325.) // 1.1.422 ([ghost], lead 11-12: PatchAll 618 ms; Fire 11-12 "proven-better defaults ship ON" + "FAP as clean as possible on frame time"): 3 diagnostic probes (Destroy Watch, Light Load Probe, Asset Load Window) behind [Diagnostics] keys OFF = never patched; ItemStand hide returns before its renderer walk when idle and only re-enables what FAP disabled (it was switching EBM-covered stand renderers back on every 4 s); [Startup] Settings File Written Once At Startup default ON (1,498 -> 645 ms); one Info "[Startup] patch census" line (FAP patch methods vs distinct targets, targets with 2+ FAP patches, targets other mods also patch, 10 slowest classes); measure only.   // was 1.1.421: 1.1.421 ([ghost], lead 11-12: FAP ~1.7 s outside Awake): process-clock stamps at class ready, Awake start and end on the "FAP Awake took" line (measure only).   // was 1.1.420: 1.1.420 ([ghost], lead 11-12 sp8 A/B): the [Startup] key Patch Only Classes With Harmony Attributes is removed (no gain: PatchAll 590 vs 596 ms); Settings File Written Once At Startup stays (FAP Awake 1,498 -> 645 ms measured), default OFF until Fire OKs a default change.   // was 1.1.419: 1.1.419 ([ghost], lead 11-12): finer startup timing inside config + sync + bridges; two Startup keys, both OFF until the load-in A/B: Settings File Written Once At Startup, Patch Only Classes With Harmony Attributes.   // was 1.1.418: 1.1.418 ([ghost], lead 11-12: full stack +11 s in the chainloader): one Info line at FinishInit, "FAP Awake took N ms (PatchAll M ms over T types: C patch classes, X methods; bundles K ms). Parts: ..." (measure only, nothing reordered).   // was 1.1.417: 1.1.417 ([ghost], lead 11-12, profiler-proven ~7 ms of the town frame): no SoundWatch MonoBehaviour per piece with a sound (23,942 Update calls a frame); only pieces with a fap_sound override are watched, on ONE flat list at 2 Hz, joined at load, on Save and by a routed change notice.   // was 1.1.416: 1.1.416 ([saddles] 11-11, [generator] MATERIALS_FAP): bundle materials the same every session: FAP_RepairCopy names from template + descriptor (not creation order); same-named game materials picked by a stable rank (not first-wins); a prefab read before the paced repair is repaired on demand (GetPrefab); late-queued impostors baked too. 1.1.415 ([saddles] 11-11, Fire: "only meant to add them to new pieces"): the Distant tag (DistantPieceRenderer.RegisterPiece) runs on placement (Piece.SetCreator) only, no longer on every existing piece at load. 1.1.414 ([saddles] 11-10): local LZ4 bundle cache (VABundleLz4Cache, [Bundles] Local LZ4 Cache default on; VAPieces 1.0.1 ships LZMA); the bake hold points the reference position at the spawn; shared Hammer icons redrawn from their own model ([Hammer] Redraw shared icons). 1.1.413 (supersedes withdrawn 1.1.412, contains it; [ghost]'s EBM 1.2.210): EbmApi.RegisterPrefabSourceLookup(FapExternalPrefabs.SourceBundleOf) pushed at world load. 1.1.412: Horizon/Shadow Caster Cull + Vegetation LOD removed (in EBM 1.2.210), VA distant prefabs via EbmApi.RegisterFarSkip.
        // ConfigSync's minimum for connecting clients: a FIXED constant, raised only when a synced key's name, type or meaning changes
        // (1.1.218 moved synced keys to new sections). Not PluginVersion, which forced every update in lockstep.
        private const string MinimumSyncVersion = "1.1.218";
        private const string YamlDotNetDetectorGUID = "com.ValheimModding.YamlDotNetDetector";
        private const string MinimumCoreVersion = "0.2.126";

        public static BepInEx.Logging.ManualLogSource Log;
        public static readonly Harmony harmony = new Harmony(PluginGUID);

        // The dedicated server executable (Core's FiresMod.IsDedicatedServer: ZNet.IsDedicated, or the process name
        // before ZNet exists). A client started with -batchmode -nographics (the headless test bot) is NOT one: it
        // runs FAP's whole client path and skips only GPU work, through IsHeadless. Both used to be "no graphics
        // device", so the headless bot never ran FAP's client code at all ([perf], 2026-09-28).
        public static bool IsDedicatedServer { get; private set; }

        // No graphics device: the dedicated server, and the headless test bot. Gates GPU work only (icon renders).
        public static bool IsHeadless => FiresCore.Lifecycle.FiresMod.IsHeadless;

        // Patch classes whose method bodies reference client-only types (placement-ghost
        // state). Harmony's IL rewriter can crash the Mono runtime importing those
        // references on a headless server, so the dedicated build skips them and
        // patches everything else.
        private static readonly HashSet<string> _clientOnlyPatchTypes = new HashSet<string>
        {
            "VerdantsAscent.Patches.Player_UpdatePlacementGhost_HammerToggle_Patch",
            "VerdantsAscent.Patches.VAOffsetCentre",
        };

        private const float AdminPollFastIntervalSec = 2f;
        private const float AdminPollSlowIntervalSec = 10f;
        private const float AdminPollFastPhaseSec = 60f;

        private static bool _adminStatusHooked;

        public ConfigSync configSync;

        // 1.1.418 ([ghost], lead 11-12: the full stack loads +11 s slower in the chainloader): FAP's startup timed by part, one Info line at FinishInit.
        // Measure only; nothing is reordered or skipped.
        // 1.1.421 ([ghost], lead 11-12: "FAP ~1.7 s outside Awake (assembly load / JIT)"): the Awake stopwatch starts inside Awake, so what came
        // before it was never timed. Process-relative stamps (ms since the process started, the same base as Core's "started at process +N ms"):
        // class ready = this type's static constructor (the assembly and type are loaded), Awake start (after AddComponent + Awake's JIT), Awake end.
        // Core's line gives its end (start + Awake), so "Core end -> FAP class ready" is the YamlDotNet Detector + FAP assembly load. Measure only.
        private static readonly double s_classReadyMs = ProcessMs();
        private static double s_awakeStartMs;
        private static double ProcessMs()
        {
            try { return (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; } catch { return -1; }
        }
        static VaPieces() { }   // explicit: the stamp above runs when the type is first touched (BepInEx's AddComponent), not earlier

        private static System.Diagnostics.Stopwatch s_awakeSw;
        private static readonly System.Text.StringBuilder s_laps = new System.Text.StringBuilder();
        private static long s_lapAt, s_patchMs, s_bundlesMs;
        private static int s_typesSeen, s_patchClasses, s_patchMethods;
        private static void Lap(string what)
        {
            if (s_awakeSw == null) return;
            long now = s_awakeSw.ElapsedMilliseconds, d = now - s_lapAt;
            s_lapAt = now;
            if (d >= 5) s_laps.Append(what).Append(' ').Append(d).Append(" ms, ");   // parts under 5 ms are left out of the line
        }

        // 1.1.419 ([ghost], lead 11-12: 1.1.418's line read "config + sync + bridges 822 ms, PatchAll 591 ms"). Two startup savings, each its own key,
        // both OFF until the load-in A/B (Fire's rule: new keys ship off):
        //  - SaveOnce: BepInEx's ConfigFile rewrites the whole settings file on every NEW Bind while SaveOnConfigSet is on (FiresFixes measured the same,
        //    StartupConfigSaves.cs "once per setting"). On = SaveOnConfigSet is off from here to the end of Awake's config section, then ONE Save().
        //    The file written equals what the last of those saves wrote (same entries, values and orphans); explicit Config.Save() calls still write.
        //    SaveOnConfigSet is put back right after, so any later Bind or Set behaves as before.
        //  - 1.1.420 (lead 11-12, sp8 A/B): the second key, "Patch Only Classes With Harmony Attributes", is REMOVED. Measured no gain (PatchAll 590 vs
        //    596 ms, identity held at 155 patch classes / 208 methods): the cost is the real patches, not the no-op types. Its cfg entry is left as an
        //    orphan in old files (BepInEx keeps it, nothing reads it).
        internal static BepInEx.Configuration.ConfigEntry<bool> StartupSaveOnce;
        private static bool s_settingsHeld, s_settingsWasSaveOnSet;

        private void BeginSettingsHold()
        {
            StartupSaveOnce = Config.Bind("Startup", "Settings File Written Once At Startup", /* 1.1.422: ON (Fire 11-12: proven-better defaults ship on; measured FAP Awake 1,498 -> 645 ms, sp8) */ true,
                "FAP writes its settings file once at startup instead of once per setting. Same file; about 0.9 s faster load (measured).");
            if (!StartupSaveOnce.Value) return;
            s_settingsWasSaveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            s_settingsHeld = true;
        }

        private void EndSettingsHold()
        {
            if (!s_settingsHeld) return;
            s_settingsHeld = false;
            Config.SaveOnConfigSet = s_settingsWasSaveOnSet;
            try { Config.Save(); } catch (Exception ex) { Logger.LogWarning("settings file write at startup failed: " + ex.Message); }
        }


        private void Awake()
        {
            s_awakeStartMs = ProcessMs();   // 1.1.421
            s_awakeSw = System.Diagnostics.Stopwatch.StartNew(); s_lapAt = 0; s_laps.Length = 0;
            Instance = this;
            Log = Logger;
            VerdantsAscent.Utilities.VAImguiGuard.RegisterWithCore();   // 1.1.306: FAP windows count as an open screen for Core's input gate
            PreBindConfigMoves();   // 1.1.218: moved settings re-keyed before any bind
            BeginSettingsHold();   // 1.1.419: right after the moves; binds its two Startup keys, holds the per-bind file writes only when its key is on
            FiresCore.Input.FiresInputBlock.LogTag = "FAP";

            TryRun("VAFapBanner.PrintBig", VAFapBanner.PrintBig);

            // ZNet does not exist yet; Core answers from the process name this early.
            IsDedicatedServer = FiresCore.Lifecycle.FiresMod.IsDedicatedServer;
            if (IsDedicatedServer)
                Debug.Log($"[{PluginName}] Detected DEDICATED SERVER context - UI patches will be skipped.");
            else if (IsHeadless)
                Debug.Log($"[{PluginName}] Headless client (no graphics device): the client path runs; piece icons are not rendered.");

            // Folds the ghost and icon caches out of BepInEx/config, which r2modman and
            // Thunderstore sweep into profile and modpack exports. Runs before anything
            // reads either cache; a same-volume move is a rename whatever it weighs.
            TryRun("VAFapCachePaths.MigrateFromConfig", VAFapCachePaths.MigrateFromConfig);
            // 1.1.276 (Fire 10-19): "Make hopper" / "Remove hopper" on any container in the hold-Alt right-click menu (Core's hopper)
            if (!IsDedicatedServer) TryRun("VAHopperContextProvider", () => FiresCore.UI.ContextMenu.FiresContextMenu.Register(new Patches.VAHopperContextProvider()));
            if (!IsDedicatedServer) TryRun("VALootModeContextProvider", () => FiresCore.UI.ContextMenu.FiresContextMenu.Register(new Patches.VALootModeContextProvider()));   // 1.1.394 loot chests + debris: Vanilla function / Decor
            if (!IsDedicatedServer) TryRun("VALootModeBuildMenu", () => Api.FapPieceMenu.RegisterProvider("fap_loot_mode", Patches.VALootPieceMode.BuildMenuRows));   // 1.1.395: Place as storage / vanilla on the build-menu right-click
            if (!IsDedicatedServer) TryRun("VASoundContextProvider", () => FiresCore.UI.ContextMenu.FiresContextMenu.Register(new Utilities.VASoundContextProvider()));   // 1.1.297 (Sound: per placed piece)
            Lap("setup + context menus");

            // Stand-in MonoBehaviour types must exist before bundle prefabs try to
            // resolve their captured script attachments.
            VARuntimeMonoStub.LoadAll();
            Lap("mono stubs");

            // Bundles load across frames and are force-completed by the barrier in
            // the ZNetScene.Awake prefix; loading synchronously here froze startup
            // for minutes on large asset folders.
            long bundles0 = s_awakeSw.ElapsedMilliseconds;
            VAExternalBundleLoader.BeginLoadAllBundlesAsync();
            s_bundlesMs = s_awakeSw.ElapsedMilliseconds - bundles0;
            Lap("bundles (begin)");

            // Overrides must be on disk-loaded before the renderer builds its first
            // scan list, or previously configured pieces are missed.
            VAFapPieceOverrides.Load();
            Lap("piece overrides");
            VAFapPieceShaderOverrider.Initialize();
            Lap("shader overrider");
            DistantPieceRenderer.Initialize();
            Lap("distant renderer");

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = MinimumSyncVersion   // 1.1.218: a fixed constant, raised only when a synced key changes
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            Lap("ConfigSync create");
            VAFapServerPresence.Hook(configSync);
            Lap("server presence hook");
            ConfigManager.Instance.Initialize(Config);
            Lap("ConfigManager binds");
            Managers.FapCoreSettingsOwned.Bind(Config);   // 1.1.345: Core's "New piece faces away from you", owned here
            VerdantsAscent.Utilities.FapDiagnosticKeys.Bind(Config);   // 1.1.422: [Diagnostics] probe keys (OFF = their patches are never applied); inside the settings hold
            Lap("core settings bind");
            try { global::VerdantsAscent.Utilities.VALightPresetStore.EnsureLoaded(); } catch (System.Exception ex) { Logger.LogWarning("light presets at load: " + ex.Message); }   // 1.1.264: built-ins seeded at load
            Lap("light presets load");
            CleanUpConfigFile();
            Lap("config cleanup + layout binds");
            BindConfigsToSync();
            Lap("ConfigSync entries");

            TryRun("VAFapAssetSync.Init", VerdantsAscent.Sync.VAFapAssetSync.Init);
            Lap("asset sync init");
            TryRun("ServerPushBridge register", RegisterServerPushBridge);
            TryRun("MonsterPresetBridge register", VerdantsAscent.Utilities.VAMonsterPresetApi.Register);
            Lap("bridges");
            EndSettingsHold();   // 1.1.419: "Settings File Written Once At Startup" writes the file once here (off = nothing held)
            Lap("settings file write (held)");

            if ((bool)(UnityEngine.Object)ZNet.instance && ZNet.instance.IsDedicated())
                StartCoroutine(DelayedDedicatedServerInit());
            else
                FinishInit();
        }

        private void Update() => VAFapThreadLog.Flush();

        private static void RegisterServerPushBridge() =>
            FiresCore.Bridge.ServerPushBridge.RegisterPusher(
                log => VerdantsAscent.Sync.VAFapAssetSync.PushPresetsNow(log));

        private IEnumerator DelayedDedicatedServerInit()
        {
            while ((UnityEngine.Object)ZNetScene.instance == (UnityEngine.Object)null
                   || (UnityEngine.Object)ObjectDB.instance == (UnityEngine.Object)null)
                yield return null;

            yield return new WaitForEndOfFrame();
            FinishInit();
        }

        private void FinishInit()
        {
            EndSettingsHold();   // 1.1.419 fallback (no-op when Awake already ended the hold)
            // 1.1.185 (FAP 1.1.184 didn't load: one patch class aimed at a method vanilla doesn't have, and the client's single
            // PatchAll threw on it, so nothing after it initialised): every client and server patches type by type, a failing
            // class logged and skipped, the rest applied.
            Lap("wait before patching");   // a dedicated server's FinishInit runs after ZNetScene/ObjectDB exist (DelayedDedicatedServerInit)
            long patch0 = s_awakeSw != null ? s_awakeSw.ElapsedMilliseconds : 0;
            PatchAllExceptClientOnlyTypes(skipClientOnly: IsDedicatedServer);
            if (s_awakeSw != null) s_patchMs = s_awakeSw.ElapsedMilliseconds - patch0;
            Lap("PatchAll");

            if (!IsDedicatedServer)
            {
                TryRun("Help content registration", VAFapHelpContent.Register);
                TryRun("Build menu tabs", Patches.VABuildUiCategoryTags.Register);
                TryRun("Infinity Tools tab", Patches.VAInfinityToolsTab.Register);   // 1.1.317: after FAP's source, so FAP keeps its tables
                Lap("help + build tabs");
            }

            Debug.Log($"{PluginName}: Init complete (dedicated: {IsDedicatedServer}, headless: {IsHeadless})");
            // 1.1.418: one Info line. PatchAll = CreateClassProcessor over every type in the assembly; "patch classes" = types that patched at least
            // one method, "methods" = methods patched. Bundles = the async BEGIN only (the finish is the ZNetScene.Awake barrier).
            if (s_awakeSw != null)
                Logger.LogInfo($"FAP Awake took {s_awakeSw.ElapsedMilliseconds} ms (PatchAll {s_patchMs} ms over {s_typesSeen} types: {s_patchClasses} patch classes, {s_patchMethods} methods; bundles {s_bundlesMs} ms; settings written once {(StartupSaveOnce != null && StartupSaveOnce.Value ? "on" : "off")}). Parts: {s_laps.ToString().TrimEnd(' ', ',')}. Process clock: class ready at +{s_classReadyMs:0} ms, Awake started at +{s_awakeStartMs:0} ms ({s_awakeStartMs - s_classReadyMs:0} ms between), ended at +{ProcessMs():0} ms");
            StartCoroutine(WaitForZNetReady());
        }

        private static void PatchAllExceptClientOnlyTypes(bool skipClientOnly)
        {
            int patched = 0, skipped = 0, failed = 0;
            foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                if (skipClientOnly && IsClientOnlyPatch(type)) { skipped++; continue; }
                if (DeferredPatchTypes.Contains(type)) continue;
                try
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 1.1.422 census
                    var done = harmony.CreateClassProcessor(type).Patch();
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    patched++;
                    s_typesSeen++;   // 1.1.418: real counts for the timing line (the log line below keeps its old wording)
                    if (done != null && done.Count > 0)
                    {
                        s_patchClasses++; s_patchMethods += done.Count;
                        NotePatchClass(type, done, dt);
                    }
                }
                catch (Exception ex)
                {
                    // Non-patch types make CreateClassProcessor a no-op, so only a
                    // type that actually carries the attribute failed for real.
                    var attributes = type.GetCustomAttributes(typeof(HarmonyPatch), inherit: true);
                    if (attributes != null && attributes.Length > 0)
                    {
                        failed++;
                        Debug.LogError($"[{PluginName}] Failed to apply Harmony patch {type.FullName} (skipped; the rest of FAP still loads): {ex.Message}");
                    }
                }
            }
            Debug.Log($"[{PluginName}] {(skipClientOnly ? "Dedicated server" : "Client")}: patched {patched} types, skipped {skipped} client-only types, {failed} failed.");
            SayPatchCensus();
        }

        // 1.1.422 ([ghost], lead 11-12: FAP PatchAll 618 ms over 155 classes / 208 methods): measure only. Harmony rebuilds a target's whole replacement
        // method on EVERY Patch call that touches it (all earlier patches on it included, ours and other mods'). So the census says whether the 618 ms
        // is per-target build cost (distinct targets ~ applications: nothing to batch) or repeated rebuilds of targets several FAP classes share
        // (batching them into one Patch per target would save the repeats). One Info line at FinishInit, plus the 10 slowest patch classes.
        private static readonly List<KeyValuePair<string, long>> s_classTicks = new List<KeyValuePair<string, long>>();
        private static void NotePatchClass(Type type, List<MethodInfo> done, long ticks)
        {
            s_classTicks.Add(new KeyValuePair<string, long>(type.Name + " (" + done.Count + ")", ticks));   // targets are read once, at the end (SayPatchCensus)
        }
        private static void SayPatchCensus()
        {
            try
            {
                int distinct = 0, ours = 0, shared = 0, othersToo = 0;
                foreach (var orig in harmony.GetPatchedMethods())
                {
                    distinct++;
                    var info = Harmony.GetPatchInfo(orig);
                    if (info == null) continue;
                    int mine = 0, all = 0;
                    foreach (var p in info.Prefixes) { all++; if (p.owner == PluginGUID) mine++; }
                    foreach (var p in info.Postfixes) { all++; if (p.owner == PluginGUID) mine++; }
                    foreach (var p in info.Transpilers) { all++; if (p.owner == PluginGUID) mine++; }
                    foreach (var p in info.Finalizers) { all++; if (p.owner == PluginGUID) mine++; }
                    ours += mine;
                    if (mine > 1) shared++;
                    if (all > mine) othersToo++;
                }
                s_classTicks.Sort((a, b) => b.Value.CompareTo(a.Value));
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < s_classTicks.Count && i < 10; i++) sb.Append(s_classTicks[i].Key).Append(' ').Append((s_classTicks[i].Value * ms).ToString("0.0")).Append(" ms, ");
                Log.LogInfo($"[Startup] patch census: {ours} FAP patch methods on {distinct} distinct target(s); {shared} target(s) carry 2+ FAP patches " +
                            $"(each extra one rebuilt that target again); {othersToo} target(s) also carry other mods' patches (each FAP rebuild re-emits theirs). " +
                            $"Slowest patch classes (methods): {sb.ToString().TrimEnd(' ', ',')}");
            }
            catch (Exception e) { Log.LogInfo("[Startup] patch census failed: " + e.Message); }
        }

        // 1.1.212 (Fire's server: EWP's LoadDefaultValueGroups NRE'd on FAP's missing-script prefabs, so EWP never
        // initialised): these patch another mod's code, and that mod may load after FAP, so at Awake its assembly isn't
        // there yet and Prepare() silently skipped the patch. They are applied from Start, once every plugin has loaded.
        private static readonly HashSet<Type> DeferredPatchTypes = new HashSet<Type>
        {
            typeof(Patches.VAEWPLoadDefaultValueGroupsPatch),
            typeof(Patches.VAEWDLoadDefaultValueGroupsPatch),
        };

        private void Start()
        {
            EndSettingsHold();   // 1.1.419 fallback: if Awake stopped early, the held settings file is still written and saving is back on
            // 1.1.436: FapCoreFar removed (Core has no far systems since EBM 1.2.210; FAT 0.2.1482 removed its Far Forest), nothing to start here
            foreach (var type in DeferredPatchTypes)
            {
                try
                {
                    var patched = harmony.CreateClassProcessor(type).Patch();
                    Debug.Log($"[{PluginName}] Deferred patch {type.Name}: {(patched != null && patched.Count > 0 ? "applied" : "not needed (target mod or method absent)")}.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{PluginName}] Failed to apply deferred Harmony patch {type.FullName}: {ex.Message}");
                }
            }
        }

        // Nested Harmony patch classes inherit their outer class's gating.
        private static bool IsClientOnlyPatch(Type type)
        {
            for (var current = type; current != null; current = current.DeclaringType)
                if (_clientOnlyPatchTypes.Contains(current.FullName))
                    return true;
            return false;
        }

        // configServerEnforced is the one locking entry a ConfigSync accepts; it
        // gates every other entry below, which sync server->client under that lock.
        // 1.1.217 (Fire 10-13, Tools\CONFIG_AUDIT_20261013.md): the .cfg reads as it works.
        //   - the preset editors' window layout (opacity / size / position) is bound now, not on first open, so it has descriptions;
        //   - [Item stands] UseEwpForRefill moved into [ItemStands]: the old value is copied over once (logged), then the old entry goes;
        //   - [Performance] PrewarmScriptTypes is bound by nothing any more: removed.
        // 1.1.218 (the lead: no first-load defaults): every moved entry is re-keyed in the ConfigFile's orphan table at the TOP of Awake,
        // before any bind, so the bind reads the moved value. Logged by name only; CleanUpConfigFile saves the file once afterwards.
        private static bool s_preMoved;
        private void PreBindConfigMoves()
        {
            try
            {
                var orphans = AccessTools.Property(typeof(BepInEx.Configuration.ConfigFile), "OrphanedEntries")?.GetValue(Config, null)
                    as Dictionary<BepInEx.Configuration.ConfigDefinition, string>;
                if (orphans == null || orphans.Count == 0) return;
                var moved = new List<string>();
                foreach (var (os, ok, ns, nk) in new[]
                {
                    ("Item stands", "UseEwpForRefill", "ItemStands", "UseEwpForRefill"),
                    ("HammerTabs", "TabDisplayNames", "BuildMenu", "TabDisplayNames"),
                    ("UI", "ConfigWindowScale", "General", "ConfigWindowScale"),
                    ("Sync", "AutoPushOnPlace", "General", "AutoPushOnPlace"),
                    ("InfinityHammer", "KeepAreaGrabScale", "Compatibility", "InfinityHammerKeepAreaGrabScale"),
                })
                {
                    var oldDef = new BepInEx.Configuration.ConfigDefinition(os, ok);
                    var newDef = new BepInEx.Configuration.ConfigDefinition(ns, nk);
                    if (!orphans.TryGetValue(oldDef, out string raw)) continue;
                    if (!orphans.ContainsKey(newDef)) orphans[newDef] = raw;   // a value already under the new key wins
                    orphans.Remove(oldDef);
                    moved.Add($"[{os}] {ok} -> [{ns}] {nk}");
                }
                if (moved.Count == 0) return;
                s_preMoved = true;
                Debug.Log($"{PluginName}: config: moved {moved.Count} setting(s) before binding, values kept: {string.Join("; ", moved.ToArray())}");
            }
            catch (Exception ex) { Debug.LogWarning($"{PluginName}: config moves skipped: {ex.Message}"); }
        }

        private void CleanUpConfigFile()
        {
            TryRun("config layout binds", () =>
            {
                VAChestConfigUI.BindLayout(); VAClutterConfigUI.BindLayout(); VAExtraDataUI.BindLayout();
                VAItemStandConfigUI.BindLayout(); VAMonsterConfigUI.BindLayout(); VALightConfigUI.BindLayout();
            });
            try
            {
                var orphans = AccessTools.Property(typeof(BepInEx.Configuration.ConfigFile), "OrphanedEntries")?.GetValue(Config, null)
                    as Dictionary<BepInEx.Configuration.ConfigDefinition, string>;
                if (orphans == null) return;
                bool changed = false;
                var oldEwp = new BepInEx.Configuration.ConfigDefinition("Item stands", "UseEwpForRefill");
                if (orphans.TryGetValue(oldEwp, out string raw))
                {
                    if (bool.TryParse((raw ?? "").Trim(), out bool v)) ConfigManager.Instance.configItemStandUseEwp.Value = v;
                    orphans.Remove(oldEwp); changed = true;
                    Debug.Log($"{PluginName}: config: [Item stands] UseEwpForRefill = {raw} moved to [ItemStands] UseEwpForRefill");
                }
                // 1.1.218 (Fire 10-13: merge the one-entry sections): the value saved under the old section / key is copied once into
                // the new one (logged by name), then the old entry goes. [Internal] AppliedMigrations stays where it is (migration state).
                foreach (var (os, ok, ns, nk) in new[]
                {
                    ("HammerTabs", "TabDisplayNames", "BuildMenu", "TabDisplayNames"),
                    ("UI", "ConfigWindowScale", "General", "ConfigWindowScale"),
                    ("Sync", "AutoPushOnPlace", "General", "AutoPushOnPlace"),
                    ("InfinityHammer", "KeepAreaGrabScale", "Compatibility", "InfinityHammerKeepAreaGrabScale"),
                })
                {
                    var oldDef = new BepInEx.Configuration.ConfigDefinition(os, ok);
                    var newDef = new BepInEx.Configuration.ConfigDefinition(ns, nk);
                    if (!orphans.TryGetValue(oldDef, out string was) || !Config.ContainsKey(newDef)) continue;
                    Config[newDef].SetSerializedValue(was);
                    orphans.Remove(oldDef); changed = true;
                    Debug.Log($"{PluginName}: config: [{os}] {ok} moved to [{ns}] {nk} (value kept)");
                }
                var dead = new BepInEx.Configuration.ConfigDefinition("Performance", "PrewarmScriptTypes");
                if (orphans.Remove(dead)) { changed = true; Debug.Log($"{PluginName}: config: removed the retired [Performance] PrewarmScriptTypes"); }
                if (changed || s_preMoved) Config.Save();
                s_preMoved = false;
            }
            catch (Exception ex) { Debug.LogWarning($"{PluginName}: config cleanup skipped: {ex.Message}"); }
        }

        private void BindConfigsToSync()
        {
            ConfigManager config = ConfigManager.Instance;

            configSync.AddLockingConfigEntry(config.configServerEnforced);

            configSync.AddConfigEntry(config.configVerboseLogging);

            configSync.AddConfigEntry(config.configHammerForceClipGround);
            configSync.AddConfigEntry(config.configHammerForceClipEverything);
            configSync.AddConfigEntry(config.configHammerDisableStaticPhysics);
            configSync.AddConfigEntry(config.configHammerForcePersistence);
            configSync.AddConfigEntry(config.configHammerSuppressRandomRotation);
            configSync.AddConfigEntry(config.configHammerSwapFracOnPlace);
            configSync.AddConfigEntry(config.configHammerEffectsOverrideTTL);
            configSync.AddConfigEntry(config.configItemStandHideStandVisual);
            configSync.AddConfigEntry(config.configIHKeepAreaGrabScale);

            configSync.AddConfigEntry(config.configAdminOnlyMode);
            configSync.AddConfigEntry(config.configRegisterPiecesForNonAdmins);
            configSync.AddConfigEntry(config.configWackyDatabaseCompat);
            configSync.AddConfigEntry(config.configVanillaFriendlyEffectAllowlist);

            configSync.AddConfigEntry(config.configVanillaFriendly);
            configSync.AddConfigEntry(config.configUseEwpForPersistence);
            // 1.1.281 (lead, FAP how-to audit): the whole Persistence panel follows the server, like its two master switches
            configSync.AddConfigEntry(config.configEwpEffects);
            configSync.AddConfigEntry(config.configEwpChests);
            configSync.AddConfigEntry(config.configEwpMonsters);
            configSync.AddConfigEntry(config.configItemStandUseEwp);
            configSync.AddConfigEntry(config.configFapRespawner);

            VAFapPlayerPieces.AddToConfigSync(configSync);
            VASoundPresets.AddToConfigSync(configSync);   // 1.1.295 (Sound Presets: the server's set for everyone)
            VerdantsAscent.Managers.FapConfigUi.Apply(Config);   // 1.1.279: settings UI tags (after ConfigSync added its lock tags)
        }

        private void OnDestroy()
        {
            DistantPieceRenderer.ClearAll();
            ConfigManager.Instance.Dispose();

            if (configSync != null)
            {
                configSync.lockedConfigChanged -= ConfigManager.Instance.OnConfigLockChanged;
                configSync = null;
            }

            Instance = null;
        }

        // Reflectively invoked BY NAME from FiresCore AdminSyncing when the server
        // pushes an admin-status update. Renaming it silently breaks late admin
        // promotion - the caller's GetMethod returns null and no-ops.
        private void OnConfigLockChanged()
        {
            ConfigManager.Instance?.OnConfigLockChanged();
        }

        // Every disk-backed store loads here, once ZNetScene exists. Rule yaml is
        // only ensured to exist; per-preset rules are written on placement.
        private IEnumerator WaitForZNetReady()
        {
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{PluginName}: Waiting for ZNetScene...");

            while ((UnityEngine.Object)ZNetScene.instance == (UnityEngine.Object)null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
                yield return null;

            yield return new WaitForEndOfFrame();

            VAVanillaPieceManager.InitializeOnce();

            // Not behind the admin gate InitializeOnce uses: these pieces already
            // exist in players' worlds, so their settings have to be restored for
            // everyone, not just admins.
            VAExternalPieceConfig.ApplyAll();

            VAFapEwpYamlWriter.EnsureSharedEntriesOnLoad();

            VAMonsterPresetStore.LoadAll();
            VAMonsterEwpRuleWriter.EnsureFileExists();
            VAMonsterPresetStore.EnableHotReload();

            VAChestPresetStore.LoadAll();
            VAChestEwpRuleWriter.EnsureFileExists();
            VAChestPresetStore.EnableHotReload();

            VAFapPersistentOptIn.LoadFromDisk();

            // ClutterSystem doesn't exist yet; VAClutterAutoApplyPatch applies
            // these to the live singleton once it awakes.
            VAClutterPresetStore.LoadAll();
            VAClutterPresetStore.EnableHotReload();

            VerdantsAscent.Network.VAFapConsoleCommands.Register();
            VAClutterBrushIndicator.EnsureCreated();
            VASwapFlow.EnsureCreated();   // 1.1.291 (Radius Swapper picking flow + kit popups)

            HookAdminPromotionWatchers();

            // The vanilla-noise banner otherwise sits on its 30s cooldown holding the
            // last batch; load is the natural boundary to flush it.
            TryRun("VanillaNoise flush",
                VerdantsAscent.Patches.ZLog_FilterFiresAdminPrefabs.ForceFlushIfDirty);

            TryRun("VAFapBanner.Print", VAFapBanner.Print);
        }

        // Admin status can arrive after load, or flap mid-session. Core's event is
        // the fast path; the poll is the fallback for when vanilla's own admin-list
        // sync grants admin without Core ever firing.
        private void HookAdminPromotionWatchers()
        {
            if (IsDedicatedServer) return;

            if (!_adminStatusHooked)
            {
                try
                {
                    FiresCore.Sync.AdminSyncing.AdminStatusChanged += OnCoreAdminStatusChanged;
                    _adminStatusHooked = true;
                }
                catch { /* Core absent - the poll below still covers us */ }
            }

            if (ConfigManager.Instance.configAdminOnlyMode.Value && !VAVanillaPieceManager.IsAdminGateOpen())
                StartAdminWatcher("plugin start");
        }

        // 1.1.350 (Fire 10-31: "the admin only toggle ... blocks pieces from showing when the mod is installed on just the client even
        // if the client is in debug mode"): the watcher started above runs at the main menu, where ZNet is null, so its first tick
        // ended it before any world loaded and it never came back. Started again on every local spawn (a world join) instead.
        private static bool s_adminWatcherRunning;

        private void StartAdminWatcher(string why)
        {
            if (s_adminWatcherRunning) return;
            s_adminWatcherRunning = true;
            Log?.LogDebug($"[FAP] admin watcher started ({why})");
            StartCoroutine(PollForAdminPromotion());
        }

        internal static void OnLocalPlayerSpawned()
        {
            if (Instance == null || IsDedicatedServer || ConfigManager.Instance == null) return;
            if (!ConfigManager.Instance.configAdminOnlyMode.Value || VAVanillaPieceManager.IsInitialized) return;
            Instance.StartAdminWatcher("world join");
        }

        // 1.1.350: the vanilla `debugmode` command only flips Player.m_debugMode (no event), and on a server without FAP that is the
        // admin answer. Opening the build menu is the moment it has to count, so the gate is asked there (an event, not a poll).
        internal static void OnBuildMenuOpening()
        {
            if (Instance == null || IsDedicatedServer || ConfigManager.Instance == null) return;
            if (VAVanillaPieceManager.IsInitialized || VAVanillaPieceManager.IsInitializing) return;
            if (!VAVanillaPieceManager.IsAdminGateOpen()) return;
            Debug.Log($"{PluginName}: admin gate opened at build-menu open ({VAVanillaPieceManager.DescribeOpenGate()}) - building the hammer.");
            VAVanillaPieceManager.InitializeOnce();
            RefreshHammerTabsAndPieces();
        }

        private static void OnCoreAdminStatusChanged(bool isAdmin)
        {
            if (!isAdmin) return;
            if (VAVanillaPieceManager.IsInitialized) return;
            if (!VAVanillaPieceManager.IsAdminGateOpen()) return;

            Debug.Log($"{PluginName}: Core admin-status push granted admin ({VAVanillaPieceManager.DescribeOpenGate()}) - running deferred InitializeOnce.");
            VAVanillaPieceManager.InitializeOnce();
            RefreshHammerTabsAndPieces();
        }

        // Never times out: a promotion that lands an hour in still has to apply.
        private IEnumerator PollForAdminPromotion()
        {
            try { foreach (var step in PollForAdminPromotionBody()) yield return step; }
            finally { s_adminWatcherRunning = false; }
        }

        private IEnumerable PollForAdminPromotionBody()
        {
            float elapsed = 0f;
            bool loggedStillClosed = false;

            while (true)
            {
                float interval = elapsed < AdminPollFastPhaseSec
                    ? AdminPollFastIntervalSec
                    : AdminPollSlowIntervalSec;
                yield return new WaitForSeconds(interval);
                elapsed += interval;

                // Disconnected. The next world load starts a fresh watcher.
                if ((UnityEngine.Object)ZNet.instance == (UnityEngine.Object)null)
                    yield break;

                // Built already, e.g. by the client-only decision at spawn (VAFapServerPresence), or still building.
                if (VAVanillaPieceManager.IsInitialized) yield break;
                if (VAVanillaPieceManager.IsInitializing) continue;

                if (VAVanillaPieceManager.IsAdminGateOpen())
                {
                    Debug.Log($"{PluginName}: Admin gate opened after {elapsed:F0}s of polling ({VAVanillaPieceManager.DescribeOpenGate()}) - running deferred InitializeOnce.");
                    VAVanillaPieceManager.InitializeOnce();
                    RefreshHammerTabsAndPieces();

                    // InitializeOnce clears its flag on every failure path, so a
                    // half-done promotion has to keep the watcher alive.
                    if (VAVanillaPieceManager.IsInitialized)
                        yield break;
                    continue;
                }

                if (!loggedStillClosed && elapsed >= AdminPollFastPhaseSec)
                {
                    loggedStillClosed = true;
                    if (ConfigManager.Instance.configVerboseLogging.Value)
                        Debug.Log($"{PluginName}: Admin gate still closed after {AdminPollFastPhaseSec:F0}s - local player is not admin yet. Vanilla hammer remains active; still watching for a late promotion.");
                }
            }
        }

        private static void RefreshHammerTabsAndPieces()
        {
            if (Player.m_localPlayer != null)
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "Ascend");
        }

        private static void TryRun(string description, Action action)
        {
            try { action(); }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] {description} failed: {ex.Message}"); }
        }

        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        public static class Piece_Awake_Patch
        {
            public static void Run(Piece __instance)
            {
                if (__instance == null) return;
                // 1.1.415 (Fire 11-11): DistantPieceRenderer.RegisterPiece moved to the Piece.SetCreator postfix (placement only). Here it ran on every
                // existing piece as zones loaded: SetDistant + ForceSendZDO on each registered piece at every arrival.
                VATowerNearChunks.OnPieceAwake(__instance); VAPrefabChildren.OnPieceAwake(__instance);
            }
        }

        // WearNTear.Destroy is the private method that runs on actual demolition.
        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        public static class WearNTear_Destroy_Patch
        {
            public static void Prefix(WearNTear __instance)
            {
                if (__instance == null) return;

                var netView = __instance.GetComponent<ZNetView>();
                if (netView == null || !netView.IsValid()) return;

                DistantPieceRenderer.UnregisterPiece(netView);
            }
        }

        // Applies user shader overrides to instances already in the scene. Instances
        // spawned later are handled per-clone by VAFapPieceShaderOverridePatch, so
        // the prefab template itself is never mutated.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VAFapPieceShaderOverrider_Patch
        {
            [HarmonyPriority(HarmonyLib.Priority.Last)]
            [HarmonyPostfix]
            public static void Postfix()
            {
                TryRun("VAFapPieceShaderOverrider.ApplyAllAtInit", VAFapPieceShaderOverrider.ApplyAllAtInit);
                if (!IsDedicatedServer) TryRun("VATowerNearChunks.WarmNearIndex", VATowerNearChunks.WarmNearIndex);   // 1.1.402: on the loading screen, not inside a create
                TryRun("DistantPieceRenderer.SyncForWorld", () => DistantPieceRenderer.SyncForWorld("world loading"));   // 1.1.423: register only prefabs this game has (ZNetScene is up now); pushes to EBM too (1.1.411)
                TryRun("EbmPrefabSource.Push", EbmPrefabSource.Push);   // 1.1.413 ([ghost]'s EBM 1.2.210): EBM no longer reads FAP by name
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        public static class ZNet_OnDestroy_Patch
        {
            public static void Prefix() => DistantPieceRenderer.ClearAll();
        }
    }
}
