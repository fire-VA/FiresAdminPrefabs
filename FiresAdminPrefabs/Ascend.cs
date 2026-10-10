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
    public class VaPieces : BaseUnityPlugin
    {
        public static VaPieces Instance;
        public const string PluginGUID = "com.Fire.adminprefabs";
        public const string PluginName = "FiresAdminPrefabs";
        public const string PluginVersion = "1.1.209";
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

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            VAFapServerPresence.Hook(configSync);
            ConfigManager.Instance.Initialize(Config);
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

            VAFapPlayerPieces.AddToConfigSync(configSync);
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
                StartCoroutine(PollForAdminPromotion());
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

        [HarmonyPatch(typeof(Piece), "Awake")]
        public static class Piece_Awake_Patch
        {
            public static void Postfix(Piece __instance)
            {
                if (__instance == null) return;
                DistantPieceRenderer.RegisterPiece(__instance);
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
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        public static class ZNet_OnDestroy_Patch
        {
            public static void Prefix() => DistantPieceRenderer.ClearAll();
        }
    }
}
