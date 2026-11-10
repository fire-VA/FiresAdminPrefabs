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
        public const string PluginVersion = "1.1.411";   // 1.1.411 ([generator] M22 / [lead]): Core-far lookups quiet (GetType(name, false) in Core's assembly; Core's Far\ moves to EBM 1.2.210); VA distant prefabs pushed to EbmApi.SetDistantDrawn (EBM 1.2.210+); one Info line when the FgnApi auto-tune bind is used. 1.1.410: FgnApi.IsAutoTuneSettled.
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

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            VerdantsAscent.Utilities.VAImguiGuard.RegisterWithCore();   // 1.1.306: FAP windows count as an open screen for Core's input gate
            PreBindConfigMoves();   // 1.1.218: moved settings re-keyed before any bind
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

            // Stand-in MonoBehaviour types must exist before bundle prefabs try to
            // resolve their captured script attachments.
            VARuntimeMonoStub.LoadAll();

            // Bundles load across frames and are force-completed by the barrier in
            // the ZNetScene.Awake prefix; loading synchronously here froze startup
            // for minutes on large asset folders.
            VAExternalBundleLoader.BeginLoadAllBundlesAsync();

            // Overrides must be on disk-loaded before the renderer builds its first
            // scan list, or previously configured pieces are missed.
            VAFapPieceOverrides.Load();
            VAFapPieceShaderOverrider.Initialize();
            DistantPieceRenderer.Initialize();
            TryRun("FapCoreFar.InitIfFatAbsent", () => VerdantsAscent.Utilities.FapCoreFar.InitIfFatAbsent(Config));   // 1.1.405: Core far systems when FAT is absent
            TryRun("FapHorizonCull.Bind", () => VerdantsAscent.Utilities.FapHorizonCull.Bind(Config));                  // 1.1.405: terrain-horizon cull (default off)
            TryRun("FapVegLod.Bind", () => VerdantsAscent.Utilities.FapVegLod.Bind(Config));                            // 1.1.405: vegetation LOD switch sooner (1.0 = vanilla)

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = MinimumSyncVersion   // 1.1.218: a fixed constant, raised only when a synced key changes
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            VAFapServerPresence.Hook(configSync);
            ConfigManager.Instance.Initialize(Config);
            Managers.FapCoreSettingsOwned.Bind(Config);   // 1.1.345: Core's "New piece faces away from you", owned here
            try { global::VerdantsAscent.Utilities.VALightPresetStore.EnsureLoaded(); } catch (System.Exception ex) { Logger.LogWarning("light presets at load: " + ex.Message); }   // 1.1.264: built-ins seeded at load
            CleanUpConfigFile();
            BindConfigsToSync();

            TryRun("VAFapAssetSync.Init", VerdantsAscent.Sync.VAFapAssetSync.Init);
            TryRun("ServerPushBridge register", RegisterServerPushBridge);
            TryRun("MonsterPresetBridge register", VerdantsAscent.Utilities.VAMonsterPresetApi.Register);

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
            // 1.1.185 (FAP 1.1.184 didn't load: one patch class aimed at a method vanilla doesn't have, and the client's single
            // PatchAll threw on it, so nothing after it initialised): every client and server patches type by type, a failing
            // class logged and skipped, the rest applied.
            PatchAllExceptClientOnlyTypes(skipClientOnly: IsDedicatedServer);

            if (!IsDedicatedServer)
            {
                TryRun("Help content registration", VAFapHelpContent.Register);
                TryRun("Build menu tabs", Patches.VABuildUiCategoryTags.Register);
                TryRun("Infinity Tools tab", Patches.VAInfinityToolsTab.Register);   // 1.1.317: after FAP's source, so FAP keeps its tables
            }

            Debug.Log($"{PluginName}: Init complete (dedicated: {IsDedicatedServer}, headless: {IsHeadless})");
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
                    harmony.CreateClassProcessor(type).Patch();
                    patched++;
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
                DistantPieceRenderer.RegisterPiece(__instance);
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
                TryRun("DistantPieceRenderer.PushToEbm", () => DistantPieceRenderer.PushToEbm("world loading"));   // 1.1.411: EBM is loaded by now
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        public static class ZNet_OnDestroy_Patch
        {
            public static void Prefix() => DistantPieceRenderer.ClearAll();
        }
    }
}
