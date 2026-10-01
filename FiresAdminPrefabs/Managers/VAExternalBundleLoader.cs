using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Managers
{
    // Loads every Unity asset bundle dropped into config/FiresAdminPrefabs/Assets/ and injects its
    // prefabs into ZNetScene, so a ripped bundle only has to be copied into that folder. Loading
    // starts asynchronously at plugin Awake; the ZNetScene.Awake prefix finishes it and adds the
    // prefabs to m_prefabs before vanilla builds m_namedPrefabs from that list.
    //
    // Server and clients both load: the server needs the prefabs for placement and ZDO spawning,
    // clients to instantiate what syncs. There is no admin gate here, because non-admins still
    // have to render what admins place; VAVanillaPieceManager decides what reaches the hammer.
    // Bundles stay loaded for the whole session so their prefabs, meshes and textures stay valid.
    public static class VAExternalBundleLoader
    {
        private const string SubfolderName = "FiresAdminPrefabs";
        private const string AssetsFolderName = "Assets";
        private const string ManifestsFolderName = "Manifests";
        private const string ManifestFileSuffix = ".manifest.json";
        private const string ReadmeFileName = "README.txt";
        private const string ClutterCategory = "clutter";
        private const string InstancedNameMarker = "instanced";
        private const string UnknownBundleName = "<unknown>";
        private const string CharacterTriggerLayerName = "character_trigger";
        private const string UnityFsSignature = "UnityFS";
        private const int UnityFsHeaderBytes = 8;

        // Highest SteamyDumps manifest schema this build understands: v1 prefab inventory, v2 script
        // attachments, v3 bundle shaders, v4 renderer materials, v5 missing-script slot records.
        // v5 is additive over v4 and adds nothing this reader consumes - FAP counts dead script slots
        // off the live prefab itself (CountMissingScripts) rather than off the manifest. Newer
        // manifests than this still load best-effort, since unknown fields are ignored.
        private const int SupportedManifestSchemaVersion = 5;

        // Clutter from bundles is registered disabled and biome-less, so ExpandWorld can resolve
        // the names while ClutterSystem never spawns them; the paint tool places them directly.
        private const int ClutterEntryAmount = 80;
        private const float ClutterEntryScaleMin = 0.8f;
        private const float ClutterEntryScaleMax = 1.2f;
        private const float ClutterEntryMaxTilt = 30f;
        private const float ClutterEntryMinAlt = -1000f;
        private const float ClutterEntryMaxAlt = 1000f;

        private const float HealedBaseAreaRadius = 20f;
        private const float HealedOtherAreaRadius = 2f;

        private static readonly HashSet<string> BundleExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bundle", ".assetbundle", ".unity3d" };

        // Files that share the folder but are never bundles: docs, configs, Unity build and import
        // artifacts, and FAP's own baked blueprints, which VABakeBundle loads separately.
        private static readonly HashSet<string> ExcludedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".txt", ".md", ".yaml", ".yml", ".json", ".cfg", ".ini", ".log", ".old", ".bak",
                ".manifest", ".meta", VABakeBundle.BundleExt, VABakeBundle.ManifestExt,
            };

        // Vanilla singletons whose OnDestroy unconditionally nulls their static instance. A bundle
        // prefab carrying one is fatal once cloned: the singleton's duplicate check destroys the
        // clone, its OnDestroy nulls the real instance, and every later EnvMan.instance or
        // Minimap.instance read NREs for the rest of the session. No piece, decoration or clutter
        // has a legitimate reason to carry one, so they are stripped at injection.
        private static readonly Type[] VanillaSingletonComponentTypes =
        {
            typeof(EnvMan),
            typeof(Minimap),
            typeof(GlobalWind),
            typeof(ZoneSystem),
            typeof(ClutterSystem),
            typeof(ZNetScene),
            typeof(ZNet),
            typeof(ZDOMan),
            typeof(Game),
            typeof(Hud),
            typeof(ObjectDB),
            typeof(DungeonDB),
            typeof(MusicMan),
            typeof(Player),
            typeof(Chat),
        };

        public static readonly List<GameObject> ExternalPrefabs = new List<GameObject>();

        private static readonly List<AssetBundle> _loadedBundles = new List<AssetBundle>();
        private static readonly Dictionary<string, string> _sourceBundleByPrefab =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<int> _bundlePrefabInstanceIds = new HashSet<int>();
        private static readonly List<string> _loadedBundleNames = new List<string>();
        private static readonly HashSet<string> _loadedBundleNamesSet = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, List<string>> _bundleClutterManifests =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<ScriptAttachmentDto>> _prefabScriptAttachments =
            new Dictionary<string, List<ScriptAttachmentDto>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<RendererDescriptorDto>> _prefabRendererDescriptors =
            new Dictionary<string, List<RendererDescriptorDto>>(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> _singletonStripsByBundle =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly SortedSet<string> _singletonStripTypes = new SortedSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _pickableHealsByBundle =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _requirementHealsByBundle =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _effectAreaHealsByBundle =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MissingScriptTally> _missingScriptRejectsByBundle =
            new Dictionary<string, MissingScriptTally>(StringComparer.Ordinal);
        private static readonly List<string> _locationPrefabsLeftOut = new List<string>();

        // Every clutter prefab this loader has seen in ClutterSystem.m_clutter, kept so entries that
        // ExpandWorld (or any mod rebuilding the list) wipes can be put back by EnsureRegistered.
        private static readonly Dictionary<string, GameObject> _registeredClutterPrefabs =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        private static Dictionary<string, GameObject> _externalPrefabsByName;
        private static int _externalPrefabsByNameCount = -1;

        private sealed class MissingScriptTally
        {
            public int Prefabs;
            public int Slots;
        }

        private sealed class PendingBundle
        {
            public string Path;
            public AssetBundleCreateRequest CreateRequest;
            public AssetBundleRequest AssetRequest;
            public AssetBundle Bundle;
            public bool Harvested;
            // The bundle's manifest, read and parsed on the ThreadPool while Unity streams the bundle; null when no
            // manifest existed at the start (HarvestPending then reads it the old way).
            public System.Threading.Tasks.Task<ManifestRead> ManifestTask;
        }

        // A manifest parse's outcome; the worker never throws, so the main thread reports the error as before.
        private sealed class ManifestRead
        {
            public BundleManifestDto Manifest;
            public Exception Error;
        }

        private static readonly List<PendingBundle> _pendingBundles = new List<PendingBundle>();
        private static bool _asyncLoadStarted;
        private static bool _bundlesLoaded;
        private static int _acceptedFileCount, _rejectedFileCount, _loadedBundleCount;

        public static string AssetsDir =>
            Path.Combine(Paths.ConfigPath, SubfolderName, AssetsFolderName);

        public static string ManifestsDir =>
            Path.Combine(Paths.ConfigPath, SubfolderName, ManifestsFolderName);

        public static IReadOnlyCollection<string> LoadedBundleNames => _loadedBundleNames;

        public static bool TryGetSourceBundle(string prefabName, out string bundleName)
            => _sourceBundleByPrefab.TryGetValue(prefabName ?? string.Empty, out bundleName);

        // Identity check rather than a name match: vanilla prefabs can share a name with something
        // a bundle ships (instanced_meadows_grass does), and callers that must never touch vanilla
        // assets, like VAClutterShaderRepair, need the distinction.
        public static bool IsBundleOwnedPrefab(GameObject prefab)
            => prefab != null && _bundlePrefabInstanceIds.Contains(prefab.GetInstanceID());

        // Renderer and material descriptors from the prefab's v4+ manifest entry, or null.
        // Every renderer descriptor from every loaded manifest, so the repair can build a
        // material-name -> texture-name index across bundles. A manifest records the same
        // material in several places and only some of those entries carry its texture.
        public static IEnumerable<List<RendererDescriptorDto>> AllRendererDescriptors()
            => _prefabRendererDescriptors.Values;

        public static List<RendererDescriptorDto> GetRendererDescriptorsForPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            _prefabRendererDescriptors.TryGetValue(prefabName, out var descriptors);
            return descriptors;
        }

        // ─────────────────────────────────────────────────────────
        //  Bundle loading
        // ─────────────────────────────────────────────────────────

        // Hands every bundle in the assets folder to Unity's async loader and returns at once. The
        // folder can hold hundreds of MB, and the blocking load APIs froze plugin Awake for minutes
        // before the main menu appeared. A coroutine pumps the loads through the menu, and
        // FinishLoadingBundles force-completes whatever is left before any prefab is injected, so
        // prefab availability doesn't depend on the head start.
        public static void BeginLoadAllBundlesAsync()
        {
            if (_asyncLoadStarted || _bundlesLoaded) return;
            _asyncLoadStarted = true;

            EnsureFoldersExist();

            string[] files;
            try { files = Directory.GetFiles(AssetsDir); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader scan FAILED on '{AssetsDir}': {ex.Message}");
                return;
            }

            Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader scanning '{AssetsDir}' — {files.Length} file(s) present (async).");

            foreach (var path in files)
            {
                bool isBundle = IsBundleFile(path, out string reason);
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"{VaPieces.PluginName}:   {(isBundle ? "LOAD" : "SKIP")} '{Path.GetFileName(path)}' — {reason}");
                if (!isBundle)
                {
                    _rejectedFileCount++;
                    continue;
                }
                _acceptedFileCount++;

                AssetBundleCreateRequest createRequest = null;
                try { createRequest = AssetBundle.LoadFromFileAsync(path); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: LoadFromFileAsync threw on '{Path.GetFileName(path)}': {ex.Message}");
                }
                if (createRequest != null)
                    _pendingBundles.Add(new PendingBundle { Path = path, CreateRequest = createRequest, ManifestTask = StartManifestRead(BundleLabel(path)) });
            }

            if (VaPieces.Instance != null) VaPieces.Instance.StartCoroutine(DriveAsyncLoad());
        }

        // How many bundle loads may be outstanding at once. The old loop awaited one bundle before touching the
        // next, so 233 bundles cost the SUM of their load times and one slow bundle blocked every bundle behind
        // it. A cap keeps the per-frame cost and the peak resident set bounded, which is what the strict
        // one-at-a-time walk was really buying.
        private const int MaxBundleLoadsInFlight = 8;

        // Main-thread time the harvest loop may spend per frame. It used to harvest every bundle that had finished in the
        // same frame (their assets and manifests), 166-267 ms frames during the world load ([ghost],
        // Tools\FAP_CLIENT_LOAD_COST.md #4); what doesn't fit waits a frame, and the ZNetScene.Awake barrier still
        // force-completes anything left.
        private const long HarvestFrameBudgetMs = 8;

        // Opens and harvests bundles as each becomes ready instead of strictly in order.
        private static IEnumerator DriveAsyncLoad()
        {
            // One frame before touching the config. A coroutine's first segment runs inside StartCoroutine,
            // and BeginLoadAllBundlesAsync is called from Awake BEFORE ConfigManager.Initialize binds anything
            // - so reading the budget here would always see a null entry and silently use the default, making
            // the setting a lie. The bundles' create-requests are already in flight, so the frame costs nothing.
            yield return null;

            AcquireLoadingBudget();
            try
            {
                int harvested = 0;
                var frame = System.Diagnostics.Stopwatch.StartNew();
                while (harvested < _pendingBundles.Count)
                {
                    // The ZNetScene.Awake barrier harvested the rest itself; those were never counted here, so without
                    // this the loop would scan every bundle each frame for the rest of the session.
                    if (_bundlesLoaded) break;

                    int inFlight = 0;
                    frame.Restart();
                    for (int i = 0; i < _pendingBundles.Count; i++)
                    {
                        if (frame.ElapsedMilliseconds >= HarvestFrameBudgetMs) break;
                        var pending = _pendingBundles[i];
                        if (pending.Harvested) continue;

                        if (pending.CreateRequest != null && !pending.CreateRequest.isDone) { inFlight++; continue; }

                        if (pending.AssetRequest == null)
                        {
                            if (inFlight >= MaxBundleLoadsInFlight) continue;
                            if (!OpenPendingBundle(pending)) { HarvestPending(pending); harvested++; continue; }
                            try { pending.AssetRequest = pending.Bundle.LoadAllAssetsAsync<GameObject>(); }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"{VaPieces.PluginName}: LoadAllAssetsAsync threw on '{Path.GetFileName(pending.Path)}': {ex.Message}");
                            }
                            // A throw leaves AssetRequest null and LoadBundlePrefabs falls back to the
                            // synchronous read, exactly as before.
                            if (pending.AssetRequest == null) { HarvestPending(pending); harvested++; continue; }
                            inFlight++;
                            continue;
                        }

                        if (!pending.AssetRequest.isDone) { inFlight++; continue; }
                        // Its manifest is still parsing on a worker: next frame (the barrier waits for it instead).
                        if (pending.ManifestTask != null && !pending.ManifestTask.IsCompleted) { inFlight++; continue; }
                        HarvestPending(pending);
                        harvested++;
                    }
                    yield return null;
                }
            }
            finally
            {
                ReleaseLoadingBudget();
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Script attachments: cost accounting
        // ─────────────────────────────────────────────────────────

        // The attachments step was most of the injection prefix in R20 (280-412 ms for 28 attachments of 9 types), while
        // a rejoin in the same process paid almost nothing. R21 (1.1.144) split it: resolve 294 ms, first adds ~0, json 3.5.
        // The type lookup was the cost (VAScriptTypeResolver.TryDirect, [ghost], Tools\FAP_CLIENT_LOAD_COST.md #7);
        // these counters stay so the timing line keeps showing where the step goes.
        private static long s_attachResolveTicks, s_attachFirstAddTicks, s_attachRepeatAddTicks, s_attachJsonTicks, s_attachSlowestFirstAddTicks;
        private static int s_attachAdded, s_attachFirstAdds;
        private static string s_attachSlowestType;

        // Script types added once in this process.
        private static readonly HashSet<Type> s_scriptTypesAddedOnce = new HashSet<Type>();

        // Prefabs whose attachments are already on them. The bundle prefabs stay loaded for the whole process, so a
        // rejoin's injection finds them attached: real types were skipped already, but a stub was added again each time.
        private static readonly HashSet<int> s_prefabsWithAttachmentsApplied = new HashSet<int>();

        private static void ResetAttachmentCounters()
        {
            s_attachResolveTicks = s_attachFirstAddTicks = s_attachRepeatAddTicks = s_attachJsonTicks = s_attachSlowestFirstAddTicks = 0;
            s_attachAdded = s_attachFirstAdds = 0;
            s_attachSlowestType = null;
        }

        // Vanilla pins Unity's async-loading time slice at its floor. BackgroundLoadingBudgetController's
        // c_defaultBudget is ThreadPriority.Low and ApplyBudget() never goes below it, so unless something asks
        // for more, Unity spends only the smallest slice per frame on background loading. FAP was issuing 233
        // bundle loads against that floor: almost nothing finished before ZNetScene.Awake, and the barrier
        // there force-completed the remainder on the main thread - the 10.2 s join stall.
        //
        // Asking through vanilla's own controller rather than setting Application.backgroundLoadingPriority
        // keeps it cooperative. ApplyBudget takes the HIGHEST outstanding request, so vanilla's own requests
        // still win while they are higher, and releasing ours restores whatever everyone else asked for
        // instead of stamping a value nobody else agreed to.
        private static bool _loadingBudgetHeld;
        private static UnityEngine.ThreadPriority _loadingBudget;

        private static void AcquireLoadingBudget()
        {
            if (_loadingBudgetHeld) return;
            _loadingBudget = Managers.ConfigManager.Instance?.configBundleLoadingBudget?.Value
                             ?? UnityEngine.ThreadPriority.High;
            if (_loadingBudget <= UnityEngine.ThreadPriority.Low) return;
            try
            {
                BackgroundLoadingBudgetController.RequestLoadingBudget(_loadingBudget);
                _loadingBudgetHeld = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader couldn't raise the background loading budget: {ex.Message}");
            }
        }

        private static void ReleaseLoadingBudget()
        {
            if (!_loadingBudgetHeld) return;
            _loadingBudgetHeld = false;
            try { BackgroundLoadingBudgetController.ReleaseLoadingBudgetRequest(_loadingBudget); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader couldn't release the background loading budget: {ex.Message}");
            }
        }

        // Force-completes every async load still in flight; touching a request's result blocks
        // until it is done. Idempotent, and must run before ExternalPrefabs is consumed, or pieces
        // go unregistered and their placed ZDOs are orphaned.
        private static void FinishLoadingBundles()
        {
            if (_bundlesLoaded) return;
            if (!_asyncLoadStarted) BeginLoadAllBundlesAsync();
            _bundlesLoaded = true;

            for (int i = 0; i < _pendingBundles.Count; i++) HarvestPending(_pendingBundles[i]);

            // The coroutine's finally normally does this, but the barrier can beat it here, and a coroutine
            // killed by a scene change never runs one at all. Holding a raised budget for the rest of the
            // session would quietly change how the game streams everything else.
            ReleaseLoadingBudget();

            VAFapLoadSummary.EmitBundles(_acceptedFileCount, _rejectedFileCount, ExternalPrefabs.Count);

            int manifestEntries = 0;
            foreach (var names in _bundleClutterManifests.Values) manifestEntries += names.Count;
            if (VAFapLoadSummary.VerboseEnabled && _bundleClutterManifests.Count > 0)
                Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader manifests — {_bundleClutterManifests.Count} bundle(s) declared {manifestEntries} clutter prefab name(s).");

            if (_acceptedFileCount > 0 && _loadedBundleCount == 0)
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader — {_acceptedFileCount} file(s) passed the extension/header filter but NONE loaded successfully. Common causes: bundles built for a different Unity version than Valheim's (currently 2022.3 LTS), already loaded by a sibling mod, or corrupted. Check the per-file log lines above for the specific reason.");
        }

        private static bool OpenPendingBundle(PendingBundle pending)
        {
            if (pending.Bundle != null) return true;
            try
            {
                pending.Bundle = pending.CreateRequest != null
                    ? pending.CreateRequest.assetBundle
                    : AssetBundle.LoadFromFile(pending.Path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: Failed to load bundle '{Path.GetFileName(pending.Path)}': {ex.Message}");
                return false;
            }
            if (pending.Bundle == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: Bundle '{Path.GetFileName(pending.Path)}' returned null (corrupt, wrong Unity version, or already-loaded by a sibling mod).");
                return false;
            }
            _loadedBundles.Add(pending.Bundle);
            return true;
        }

        // Extracts one bundle's GameObjects into ExternalPrefabs and reads its manifest. The
        // coroutine and FinishLoadingBundles can both reach a bundle; whichever arrives first
        // does the work.
        private static void HarvestPending(PendingBundle pending)
        {
            if (pending == null || pending.Harvested) return;
            pending.Harvested = true;
            if (!OpenPendingBundle(pending)) return;

            GameObject[] prefabs;
            try { prefabs = LoadBundlePrefabs(pending); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: LoadAllAssets<GameObject> threw on '{Path.GetFileName(pending.Path)}': {ex.Message}");
                return;
            }
            if (prefabs.Length == 0)
            {
                Debug.LogWarning($"{VaPieces.PluginName}:     bundle '{Path.GetFileName(pending.Path)}' contains NO GameObject assets — nothing to register (bundle has only Textures/Meshes/Materials/etc. without a containing prefab).");
                return;
            }

            string bundleLabel = BundleLabel(pending.Path);

            int extracted = 0;
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                ExternalPrefabs.Add(prefab);
                _sourceBundleByPrefab[prefab.name] = bundleLabel;
                _bundlePrefabInstanceIds.Add(prefab.GetInstanceID());
                extracted++;
            }
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}:     bundle '{Path.GetFileName(pending.Path)}' → extracted {extracted}/{prefabs.Length} GameObject prefabs (queued for ZNetScene injection).");
            if (extracted == 0) return;
            if (VaPieces.IsDedicatedServer) DropDediRenderMeshes(prefabs, bundleLabel);
            else DeferClientRenderMeshes(prefabs, bundleLabel);

            if (_loadedBundleNamesSet.Add(bundleLabel)) _loadedBundleNames.Add(bundleLabel);
            _loadedBundleCount++;
            LoadBundleManifest(bundleLabel, pending.ManifestTask);
        }

        // The dedicated server draws nothing, yet a render mesh loaded from a bundle stays in its memory. The castle
        // tower's shared mesh (ConsMassiveTower, CastleTowerMassive) is 12.2M vertices, 801 MB on the dedi (FDT [Mem]
        // census, R40/R41). Its colliders are separate boxes and a small mesh, so the server loses nothing it uses
        // (Fire, 2026-09-28). A mesh any collider in the bundle still points at is kept.
        private static bool _dediSkipWarned;

        private static void DropDediRenderMeshes(GameObject[] prefabs, string bundleLabel)
        {
            var cfg = Managers.ConfigManager.Instance?.configDediSkipRenderMeshes;
            if (cfg == null)
            {
                if (!_dediSkipWarned) Debug.LogWarning($"{VaPieces.PluginName}: dedicated server: DediSkipRenderMeshes isn't bound yet at '{bundleLabel}'s harvest; render meshes kept");
                _dediSkipWarned = true;
                return;
            }
            string list = cfg.Value;
            if (string.IsNullOrWhiteSpace(list)) return;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in list.Split(',')) if (n.Trim().Length > 0) names.Add(n.Trim());

            var colliderMeshes = new HashSet<Mesh>();
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                foreach (var mc in prefab.GetComponentsInChildren<MeshCollider>(true))
                    if (mc.sharedMesh != null) colliderMeshes.Add(mc.sharedMesh);
            }

            var meshes = new HashSet<Mesh>();
            var matched = new List<string>();
            foreach (var prefab in prefabs)
            {
                if (prefab == null || !names.Contains(prefab.name)) continue;
                matched.Add(prefab.name);
                foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh != null && !colliderMeshes.Contains(mf.sharedMesh)) { meshes.Add(mf.sharedMesh); mf.sharedMesh = null; }
                }
                foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh != null && !colliderMeshes.Contains(smr.sharedMesh)) { meshes.Add(smr.sharedMesh); smr.sharedMesh = null; }
                }
                foreach (var r in prefab.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            }
            if (matched.Count == 0) return;

            long vertices = 0;
            foreach (var mesh in meshes)
            {
                vertices += mesh.vertexCount;
                Resources.UnloadAsset(mesh);
            }
            Debug.Log($"{VaPieces.PluginName}: dedicated server: dropped the render mesh of {string.Join(", ", matched)} from '{bundleLabel}' " +
                      $"({meshes.Count} mesh(es), {vertices:N0} vertices); colliders kept (Performance > DediSkipRenderMeshes)");
        }

        // Clients (Fire 2026-09-29, the interim before the decimated mesh): the castle tower's 12.2M-vertex render mesh cost
        // 1.6 GB on every client and the bot although most sessions never see a tower (MEMORY_LEADS #2). Its DATA is
        // unloaded right after the harvest while every reference to it stays: the bundle stays loaded, so Unity loads the
        // mesh back from it the first time a tower is actually built or loaded near the player. Piece icons come from the
        // PNG icon cache, so the build menu doesn't bring it back. Colliders are never touched.
        private static void DeferClientRenderMeshes(GameObject[] prefabs, string bundleLabel)
        {
            var cfg = Managers.ConfigManager.Instance?.configClientLazyRenderMeshes;
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.Value)) return;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in cfg.Value.Split(',')) if (n.Trim().Length > 0) names.Add(n.Trim());

            var colliderMeshes = new HashSet<Mesh>();
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                foreach (var mc in prefab.GetComponentsInChildren<MeshCollider>(true))
                    if (mc.sharedMesh != null) colliderMeshes.Add(mc.sharedMesh);
            }

            var meshes = new HashSet<Mesh>();
            var matched = new List<string>();
            foreach (var prefab in prefabs)
            {
                if (prefab == null || !names.Contains(prefab.name)) continue;
                matched.Add(prefab.name);
                foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null && !colliderMeshes.Contains(mf.sharedMesh)) meshes.Add(mf.sharedMesh);
                foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr.sharedMesh != null && !colliderMeshes.Contains(smr.sharedMesh)) meshes.Add(smr.sharedMesh);
            }
            if (matched.Count == 0) return;

            long vertices = 0;
            foreach (var mesh in meshes)
            {
                vertices += mesh.vertexCount;
                Resources.UnloadAsset(mesh);
            }
            Debug.Log($"{VaPieces.PluginName}: deferred the render mesh of {string.Join(", ", matched)} from '{bundleLabel}' " +
                      $"({meshes.Count} mesh(es), {vertices:N0} vertices): it loads back from the bundle when a tower is near " +
                      "(Performance > ClientLazyRenderMeshes)");
        }

        private static string BundleLabel(string path)
        {
            string label = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(label) ? Path.GetFileName(path) : label;
        }

        // Starts reading and parsing <bundle>.manifest.json on the ThreadPool. The 24 manifests on the rig are 26 MB
        // (one 8.8 MB) and were parsed by Newtonsoft on the main thread inside the world load's harvest frames ([ghost],
        // Tools\FAP_CLIENT_LOAD_COST.md #3). The worker only reads the file and fills DTOs; everything that registers
        // the result stays on the main thread in LoadBundleManifest.
        private static System.Threading.Tasks.Task<ManifestRead> StartManifestRead(string bundleName)
        {
            try
            {
                string manifestPath = Path.Combine(ManifestsDir, bundleName + ManifestFileSuffix);
                if (!File.Exists(manifestPath)) return null;
                return System.Threading.Tasks.Task.Run(() =>
                {
                    try { return new ManifestRead { Manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<BundleManifestDto>(File.ReadAllText(manifestPath)) }; }
                    catch (Exception ex) { return new ManifestRead { Error = ex }; }
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: couldn't start reading the manifest of '{bundleName}' in the background ({ex.Message}); it is read at harvest.");
                return null;
            }
        }

        private static GameObject[] LoadBundlePrefabs(PendingBundle pending)
        {
            if (pending.AssetRequest == null) return pending.Bundle.LoadAllAssets<GameObject>() ?? Array.Empty<GameObject>();

            var assets = pending.AssetRequest.allAssets;
            var prefabs = new List<GameObject>(assets.Length);
            foreach (var asset in assets)
                if (asset is GameObject gameObject) prefabs.Add(gameObject);
            return prefabs.ToArray();
        }

        // Reads the <bundle>.manifest.json that SteamyDumps' YoinkScanMe writes beside a bundle. It
        // feeds the asset router each prefab's category, lists the clutter names for
        // RegisterClutterFromManifests, and keeps script attachments and renderer descriptors for
        // injection and material repair. A missing manifest is normal.
        //
        // A bundle whose name contains "clutter", or that is listed in BundleClutterAllOf, has every
        // prefab treated as clutter, for packs whose per-prefab categories disagree.
        private static void LoadBundleManifest(string bundleName, System.Threading.Tasks.Task<ManifestRead> backgroundRead)
        {
            try
            {
                BundleManifestDto manifest;
                if (backgroundRead != null)
                {
                    // Done already on the coroutine path; the ZNetScene.Awake barrier waits here for a late one.
                    var read = backgroundRead.Result;
                    if (read.Error != null) throw read.Error;
                    manifest = read.Manifest;
                }
                else
                {
                    string manifestPath = Path.Combine(ManifestsDir, bundleName + ManifestFileSuffix);
                    if (!File.Exists(manifestPath)) return;
                    manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<BundleManifestDto>(File.ReadAllText(manifestPath));
                }
                if (manifest?.prefabs == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: Manifest '{bundleName}{ManifestFileSuffix}' parsed to null or empty — ignoring.");
                    return;
                }
                if (manifest.version > SupportedManifestSchemaVersion)
                    Debug.LogWarning($"{VaPieces.PluginName}: Manifest '{bundleName}{ManifestFileSuffix}' schema version {manifest.version} is newer than supported ({SupportedManifestSchemaVersion}) — reading best-effort.");

                bool treatAllAsClutter =
                    bundleName.IndexOf(ClutterCategory, StringComparison.OrdinalIgnoreCase) >= 0
                    || (ConfigManager.Instance?.IsBundleInClutterAllOf(bundleName) ?? false);

                var clutterNames = new List<string>();
                int prefabsWithAttachments = 0, totalAttachments = 0;
                foreach (var entry in manifest.prefabs)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.name)) continue;

                    VABundleAssetRouter.RegisterManifestCategory(entry.name, entry.category);
                    if (treatAllAsClutter || string.Equals(entry.category, ClutterCategory, StringComparison.OrdinalIgnoreCase))
                        clutterNames.Add(entry.name);

                    if (entry.scriptAttachments != null && entry.scriptAttachments.Count > 0)
                    {
                        _prefabScriptAttachments[entry.name] = entry.scriptAttachments;
                        prefabsWithAttachments++;
                        totalAttachments += entry.scriptAttachments.Count;
                    }
                    if (entry.renderers != null && entry.renderers.Count > 0)
                        _prefabRendererDescriptors[entry.name] = entry.renderers;
                }

                if (clutterNames.Count > 0) _bundleClutterManifests[bundleName] = clutterNames;
                Debug.Log($"{VaPieces.PluginName}:     manifest '{bundleName}{ManifestFileSuffix}' → {manifest.prefabCount} prefab(s) total, {clutterNames.Count} clutter, {prefabsWithAttachments} prefab(s) with script attachments ({totalAttachments} scripts).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: LoadBundleManifest('{bundleName}') failed: {ex.Message}");
            }
        }

        // Manifest JSON shapes, filled by Newtonsoft; only the fields FAP reads are declared.
#pragma warning disable CS0649
        [Serializable]
        private class BundleManifestDto
        {
            public int version;
            public int prefabCount;
            public List<PrefabEntryDto> prefabs;
        }

        [Serializable]
        private class PrefabEntryDto
        {
            public string name;
            public string category;
            public List<ScriptAttachmentDto> scriptAttachments;
            public List<RendererDescriptorDto> renderers;
        }

        [Serializable]
        public class RendererDescriptorDto
        {
            public string path;
            public List<MaterialDescriptorDto> materials;
        }

        [Serializable]
        public class MaterialDescriptorDto
        {
            public string name;
            public string shader;
            public string mainTexture;
            public string color;
            public float cutoff;
            public float glossiness;
            public List<string> keywords;
        }

        [Serializable]
        private class ScriptAttachmentDto
        {
            public string typeFullName;
            public string assemblyName;
            public string jsonState;
        }
#pragma warning restore CS0649

        // ─────────────────────────────────────────────────────────
        //  Clutter registration
        // ─────────────────────────────────────────────────────────

        // Adds bundle clutter to ClutterSystem.m_clutter in three passes: names declared by
        // manifests, prefabs the asset router detected in manifest-less bundles, and any
        // "instanced"-named prefab in ZNetScene. It runs from both the ClutterSystem.Awake and the
        // ZNetScene.Awake postfixes, because the router queue only fills during ZNetScene.Awake,
        // which comes second. Each prefab is added once, so repeat runs only add the delta.
        public static int RegisterClutterFromManifests()
        {
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return 0;

            var externalPrefabsByName = ExternalPrefabsByName();
            int fromManifests = 0, missingPrefabs = 0;
            foreach (var manifest in _bundleClutterManifests)
            {
                foreach (var prefabName in manifest.Value)
                {
                    if (string.IsNullOrEmpty(prefabName) || _registeredClutterPrefabs.ContainsKey(prefabName)) continue;
                    if (!externalPrefabsByName.TryGetValue(prefabName, out var prefab) || prefab == null)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: Manifest '{manifest.Key}' lists prefab '{prefabName}' but no such GameObject was extracted from the bundle. Skipping.");
                        missingPrefabs++;
                        continue;
                    }
                    if (TryRegisterClutter(clutterSystem, prefabName, prefab)) fromManifests++;
                }
            }

            int fromRouter = 0;
            foreach (var queued in VABundleAssetRouter.ClutterPrefabsToRegister)
            {
                if (queued.Value == null || _registeredClutterPrefabs.ContainsKey(queued.Key)) continue;
                if (TryRegisterClutter(clutterSystem, queued.Key, queued.Value)) fromRouter++;
            }

            int fromInstancedNames = 0;
            var scene = ZNetScene.instance;
            if (scene != null && scene.m_prefabs != null)
            {
                foreach (var prefab in scene.m_prefabs)
                {
                    if (prefab == null || string.IsNullOrEmpty(prefab.name) || prefab.transform.parent != null) continue;
                    if (_registeredClutterPrefabs.ContainsKey(prefab.name)) continue;
                    if (prefab.name.IndexOf(InstancedNameMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (TryRegisterClutter(clutterSystem, prefab.name, prefab)) fromInstancedNames++;
                }
            }

            int total = fromManifests + fromRouter + fromInstancedNames;
            if (total > 0) VAVanillaPieceManager.InvalidateClutterCache();
            if (total > 0 || missingPrefabs > 0)
                Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader registered {fromManifests} clutter prefab(s) from {_bundleClutterManifests.Count} manifest(s) + {fromRouter} auto-detected from manifest-less bundles + {fromInstancedNames} 'instanced'-named prefab(s) ({missingPrefabs} manifest entries missing in bundles, skipped). NEW entries this pass: {total}. Total tracked: {_registeredClutterPrefabs.Count}.");
            return total;
        }

        // Puts back clutter entries another mod removed from ClutterSystem.m_clutter. Every entry
        // seen in the list is remembered first, so a rebuild back to the vanilla baseline restores
        // everything that was there before. Cheap and idempotent; VAClutterRegistry calls it on
        // every cache build.
        public static int EnsureRegistered()
        {
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return 0;

            var presentNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clutter in clutterSystem.m_clutter)
            {
                if (clutter?.m_prefab == null || string.IsNullOrEmpty(clutter.m_prefab.name)) continue;
                presentNames.Add(clutter.m_prefab.name);
                if (!_registeredClutterPrefabs.ContainsKey(clutter.m_prefab.name))
                    _registeredClutterPrefabs[clutter.m_prefab.name] = clutter.m_prefab;
            }

            int reinjected = 0;
            foreach (var registered in _registeredClutterPrefabs)
            {
                if (registered.Value == null || presentNames.Contains(registered.Key)) continue;
                if (TryAddClutterEntry(clutterSystem, registered.Value)) reinjected++;
            }
            if (reinjected > 0 && VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader.EnsureRegistered re-injected {reinjected} clutter entries that another mod (likely EWD) wiped from ClutterSystem.m_clutter. High-water mark = {_registeredClutterPrefabs.Count}.");
            return reinjected;
        }

        private static bool TryRegisterClutter(ClutterSystem clutterSystem, string prefabName, GameObject prefab)
        {
            if (!TryAddClutterEntry(clutterSystem, prefab)) return false;
            _registeredClutterPrefabs[prefabName] = prefab;
            return true;
        }

        // Returns false when the prefab, or one with its name, is already in the list.
        private static bool TryAddClutterEntry(ClutterSystem clutterSystem, GameObject prefab)
        {
            if (clutterSystem == null || clutterSystem.m_clutter == null || prefab == null) return false;
            foreach (var clutter in clutterSystem.m_clutter)
            {
                if (clutter?.m_prefab == null) continue;
                if (clutter.m_prefab == prefab || clutter.m_prefab.name == prefab.name) return false;
            }

            clutterSystem.m_clutter.Add(new ClutterSystem.Clutter
            {
                m_name = prefab.name,
                m_prefab = prefab,
                m_enabled = false,
                m_biome = Heightmap.Biome.None,
                m_instanced = prefab.GetComponentInChildren<InstanceRenderer>(includeInactive: true) != null,
                m_amount = ClutterEntryAmount,
                m_onUncleared = true,
                m_onCleared = false,
                m_scaleMin = ClutterEntryScaleMin,
                m_scaleMax = ClutterEntryScaleMax,
                m_maxTilt = ClutterEntryMaxTilt,
                m_minTilt = 0f,
                m_minAlt = ClutterEntryMinAlt,
                m_maxAlt = ClutterEntryMaxAlt,
            });
            return true;
        }

        // First element wins, matching the order prefabs were extracted in. Rebuilt only when the
        // extracted set grows.
        private static Dictionary<string, GameObject> ExternalPrefabsByName()
        {
            if (_externalPrefabsByName != null && _externalPrefabsByNameCount == ExternalPrefabs.Count)
                return _externalPrefabsByName;

            var byName = new Dictionary<string, GameObject>(ExternalPrefabs.Count);
            foreach (var prefab in ExternalPrefabs)
                if (prefab != null && !byName.ContainsKey(prefab.name)) byName[prefab.name] = prefab;
            _externalPrefabsByName = byName;
            _externalPrefabsByNameCount = ExternalPrefabs.Count;
            return byName;
        }

        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        public static class ClutterSystem_Awake_VAManifestRegister_Patch
        {
            [HarmonyPostfix]
            public static void Postfix() => RegisterClutterFromManifests();
        }

        // Low priority so it sees ZNetScene's prefab list after the other Awake postfixes settle.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VARegisterClutter_LatePass_Patch
        {
            [HarmonyPriority(Priority.Low)]
            [HarmonyPostfix]
            public static void Postfix() => RegisterClutterFromManifests();
        }

        // ─────────────────────────────────────────────────────────
        //  ZNetScene injection
        // ─────────────────────────────────────────────────────────

        // Runs before vanilla's ZNetScene.Awake body, so the prefabs added to m_prefabs are hashed
        // into m_namedPrefabs by vanilla itself.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VAExternalBundleLoader_Patch
        {
            public static void Prefix(ZNetScene __instance)
            {
                if (__instance == null) return;
                long barrierStart = System.Diagnostics.Stopwatch.GetTimestamp();
                FinishLoadingBundles();
                InjectPrefabsIntoZNetScene(__instance, System.Diagnostics.Stopwatch.GetTimestamp() - barrierStart);
            }
        }

        // Adds every extracted prefab to ZNetScene.m_prefabs, skipping names the scene already
        // has. A ZoneSystem location is left out: its children spawn as separate network objects,
        // so it is never a network prefab itself. A prefab with a missing-script slot is rejected
        // outright: ExpandWorld calls GetType() on every component without a null check, and
        // there is no runtime API to remove the slot. Accepted prefabs lose any vanilla singleton
        // they carry, get a trigger collider on bare EffectArea markers, are routed by category,
        // and get their manifest's script attachments. Material repair runs afterwards in a paced
        // coroutine so this prefix doesn't stall vanilla's world load.
        //
        // With InjectionSingleWalk on, WalkHierarchy lists each prefab's components once and every
        // hierarchy-wide check reads that list; off, each check walks the prefab itself, as before.
        // Both make the same repairs. The timing line splits the prefix by step for comparing them.
        // Fire (09-29 10:06): the castle tower is gone from vapieces. Worlds with one placed keep its ZDO, and vanilla's
        // CreateObject logs "Missing prefab hash" for it every frame it's in range. Each name in RemovedPrefabPlaceholders
        // that no loaded bundle or earlier mod provides gets an invisible template: ZNetView only (persistent, so the ZDO is
        // kept), registered with VABakeTemplateRegistry before the ZNetView like VA_BakeParent, so only its clones wake.
        private static readonly Dictionary<string, GameObject> _placeholders = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static readonly Vector3 PlaceholderPark = new Vector3(100000f, 100000f, 100000f);

        private static void AddRemovedPrefabPlaceholders(ZNetScene scene)
        {
            string list = Managers.ConfigManager.Instance?.configRemovedPrefabPlaceholders?.Value;
            if (string.IsNullOrWhiteSpace(list)) return;
            var provided = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in scene.m_prefabs) if (p != null) provided.Add(p.name);
            foreach (var p in ExternalPrefabs) if (p != null && !_placeholders.ContainsValue(p)) provided.Add(p.name);
            var added = new List<string>();
            foreach (var raw in list.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0 || provided.Contains(name)) continue;
                if (!_placeholders.TryGetValue(name, out var stub) || stub == null)
                {
                    stub = new GameObject(name);
                    VABakeTemplateRegistry.RegisterTemplate(stub);
                    stub.transform.position = PlaceholderPark;
                    UnityEngine.Object.DontDestroyOnLoad(stub);
                    var view = stub.AddComponent<ZNetView>();
                    view.m_persistent = true;
                    view.m_type = ZDO.ObjectType.Default;
                    _placeholders[name] = stub;
                }
                scene.m_prefabs.Add(stub);
                added.Add(name);
            }
            if (added.Count > 0)
                Debug.Log($"{VaPieces.PluginName}: placeholder for missing prefab(s) {string.Join(", ", added)} (removed from the bundles; placed ones keep their save data and load invisible, without 'Missing prefab hash' spam)");
        }

        private static void InjectPrefabsIntoZNetScene(ZNetScene scene, long barrierTicks)
        {
            long injectStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool singleWalk = Managers.ConfigManager.Instance?.configInjectionSingleWalk?.Value ?? true;
            long walkTicks = 0, singletonTicks = 0, healTicks = 0, routerTicks = 0, attachmentTicks = 0, wiringTicks = 0;
            ResetAttachmentCounters();

            _missingScriptRejectsByBundle.Clear();
            _singletonStripsByBundle.Clear();
            _singletonStripTypes.Clear();
            _effectAreaHealsByBundle.Clear();
            _pickableHealsByBundle.Clear();
            _requirementHealsByBundle.Clear();
            _locationPrefabsLeftOut.Clear();

            int purgedPrefabs = scene.m_prefabs.RemoveAll(prefab => prefab == null);
            int purgedNamedPrefabs = RemoveDestroyedNamedPrefabs(scene);
            if (purgedPrefabs > 0 || purgedNamedPrefabs > 0)
                Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader scrubbed {purgedPrefabs} fake-null entries from m_prefabs + {purgedNamedPrefabs} from m_namedPrefabs (EWD-compat).");

            AddRemovedPrefabPlaceholders(scene);
            if (ExternalPrefabs.Count == 0) return;

            var sceneNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var existing in scene.m_prefabs)
                if (existing != null) sceneNames.Add(existing.name);

            // A dead script slot is only fatal because nothing can remove it at
            // runtime and vanilla readers call GetType() on it. When the manifest
            // says which MonoBehaviours belonged on this prefab, we can put them
            // back, so the prefab is worth keeping rather than discarding whole.
            int added = 0, skipped = 0, repipedPastDeadSlots = 0;
            foreach (var prefab in ExternalPrefabs)
            {
                if (prefab == null) continue;
                if (sceneNames.Contains(prefab.name)) { skipped++; continue; }
                if (prefab.GetComponent<Location>() != null)
                {
                    _locationPrefabsLeftOut.Add($"{prefab.name} ({SourceBundleOf(prefab)})");
                    continue;
                }

                long mark = System.Diagnostics.Stopwatch.GetTimestamp();
                var hierarchy = singleWalk ? s_walkComponents : null;
                int missingScripts = singleWalk ? WalkHierarchy(prefab) : CountMissingScripts(prefab);
                walkTicks += Lap(ref mark);
                if (missingScripts > 0 && !_prefabScriptAttachments.ContainsKey(prefab.name))
                {
                    RecordMissingScriptReject(prefab, missingScripts);
                    skipped++;
                    continue;
                }
                if (missingScripts > 0) repipedPastDeadSlots++;

                StripDangerousSingletonComponents(prefab, hierarchy);
                singletonTicks += Lap(ref mark);
                HealColliderlessEffectAreas(prefab, hierarchy);
                HealPickablesWithMissingItem(prefab, hierarchy);
                HealPiecesWithMissingRequirementItem(prefab);
                healTicks += Lap(ref mark);
                VABundleAssetRouter.Apply(prefab, hierarchy);
                routerTicks += Lap(ref mark);
                scene.m_prefabs.Add(prefab);
                sceneNames.Add(prefab.name);
                ApplyScriptAttachments(prefab);
                attachmentTicks += Lap(ref mark);
                VerdantsAscent.Api.FapExternalPrefabs.Raise(prefab);
                wiringTicks += Lap(ref mark);
                added++;
            }
            s_walkComponents.Clear();
            s_walkEffectAreas.Clear();
            s_walkPickables.Clear();

            Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader injected {added} external prefab(s) into ZNetScene.m_prefabs (skipped {skipped} name-collisions, kept {repipedPastDeadSlots} with dead script slots because their manifest names the scripts to re-attach). Vanilla will populate m_namedPrefabs. Category breakdown via VABundleAssetRouter: {VABundleAssetRouter.FormatCategorySummary()}.");

            long repairStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (VaPieces.Instance != null)
                VaPieces.Instance.StartCoroutine(VAMaterialRepair.RepairManyAsync(new List<GameObject>(ExternalPrefabs), emitSummary: true));
            else if (VAMaterialRepair.TotalPrefabsScanned > 0)
                Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair — {VAMaterialRepair.FormatSummary()}.");
            long repairStartTicks = System.Diagnostics.Stopwatch.GetTimestamp() - repairStart;

            LogInjectionSummaries();

            long totalTicks = System.Diagnostics.Stopwatch.GetTimestamp() - injectStart;
            long restTicks = totalTicks - walkTicks - singletonTicks - healTicks - routerTicks - attachmentTicks - wiringTicks - repairStartTicks;
            Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader injection timing ({(singleWalk ? "single walk" : "separate walks")}): "
                + $"{added} added, prefix {Ms(barrierTicks + totalTicks)} ms: barrier {Ms(barrierTicks)}, walk {Ms(walkTicks)}, "
                + $"singletons {Ms(singletonTicks)}, heals {Ms(healTicks)}, router {Ms(routerTicks)}, attachments {Ms(attachmentTicks)}, "
                + $"wiring {Ms(wiringTicks)}, repair start {Ms(repairStartTicks)}, rest {Ms(restTicks)}.");
            if (s_attachAdded > 0)
                Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader attachments: {s_attachAdded} added, {s_attachFirstAdds} of them a type's first add "
                    + $"in this process: resolve {Ms(s_attachResolveTicks)} ms, first adds {Ms(s_attachFirstAddTicks)} ms (slowest {s_attachSlowestType ?? "-"} "
                    + $"{Ms(s_attachSlowestFirstAddTicks)} ms), repeat adds {Ms(s_attachRepeatAddTicks)} ms, json {Ms(s_attachJsonTicks)} ms.");
        }

        private static long Lap(ref long mark)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsed = now - mark;
            mark = now;
            return elapsed;
        }

        private static string Ms(long ticks) =>
            (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        private static void LogInjectionSummaries()
        {
            int resolverLookups = 0;
            var resolverMethods = new StringBuilder();
            foreach (var methodCount in VAScriptTypeResolver.MethodCounts)
            {
                if (methodCount.Value <= 0) continue;
                resolverLookups += methodCount.Value;
                if (resolverMethods.Length > 0) resolverMethods.Append(", ");
                resolverMethods.Append(methodCount.Key).Append('=').Append(methodCount.Value);
            }
            if (resolverLookups > 0)
                Debug.Log($"{VaPieces.PluginName}: VAScriptTypeResolver methods used during injection: {resolverMethods} (total {resolverLookups} attachment lookup(s)).");

            // Both repairs report through the end-of-load banner rather than a
            // paragraph each. The per-bundle breakdown and the rationale used to
            // be printed in full every launch — 590 columns against a 100-column
            // console, which folds mid-word and is unreadable.
            int totalStripped = 0, totalHealed = 0;
            if (_singletonStripsByBundle.Count > 0) FormatCountsByBundle(_singletonStripsByBundle, out totalStripped);
            if (_effectAreaHealsByBundle.Count > 0) FormatCountsByBundle(_effectAreaHealsByBundle, out totalHealed);

            var repairedBundles = new HashSet<string>(_singletonStripsByBundle.Keys);
            repairedBundles.UnionWith(_effectAreaHealsByBundle.Keys);
            VAFapLoadSummary.EmitBundleRepairs(totalStripped, totalHealed, repairedBundles.Count);

            if (VAFapLoadSummary.VerboseEnabled && repairedBundles.Count > 0)
            {
                Debug.Log($"{VaPieces.PluginName}: bundle repairs by bundle — "
                    + $"singleton strips [{string.Join(",", _singletonStripTypes)}]: "
                    + $"{FormatCountsByBundle(_singletonStripsByBundle, out int _)}; "
                    + $"EffectArea heals: {FormatCountsByBundle(_effectAreaHealsByBundle, out int _)}");
            }

            if (_pickableHealsByBundle.Count > 0)
            {
                string pickablesByBundle = FormatCountsByBundle(_pickableHealsByBundle, out int totalPickables);
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader gave {totalPickables} Pickable(s) an m_overrideName because " +
                                 $"their m_itemPrefab does not resolve ({pickablesByBundle}). Vanilla Pickable.GetHoverName dereferences that " +
                                 "field with no null check, so hovering one threw once per frame from Hud.UpdateCrosshair. They are safe to " +
                                 "look at now but still yield nothing when picked — the bundle's item reference is what needs repairing.");
            }

            if (_requirementHealsByBundle.Count > 0)
            {
                string piecesByBundle = FormatCountsByBundle(_requirementHealsByBundle, out int totalPieces);
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader dropped requirement entries with no item from {totalPieces} piece(s) " +
                                 $"({piecesByBundle}). Valheim 1.0's build menu and placement read every requirement's item with no null " +
                                 "check, so one such piece stopped the hammer menu from opening and threw when placed. Every vanilla cost " +
                                 "check already skipped those entries, so no piece's cost changed — the bundle's item references are what need repairing.");
            }

            if (_locationPrefabsLeftOut.Count > 0)
                Debug.Log($"{VaPieces.PluginName}: VAExternalBundleLoader left {_locationPrefabsLeftOut.Count} ZoneSystem location prefab(s) out of ZNetScene [{string.Join(", ", _locationPrefabsLeftOut)}]. A location's children spawn as separate network objects; registered whole, the one-ZNetView-per-prefab normalization strips every child's ZNetView and leaves its spawners throwing.");

            foreach (var reject in _missingScriptRejectsByBundle)
                VAFapLoadSummary.EmitMissingScripts(reject.Key, reject.Value.Prefabs, reject.Value.Slots);
        }

        private static string FormatCountsByBundle(Dictionary<string, int> countsByBundle, out int total)
        {
            total = 0;
            var formatted = new StringBuilder();
            foreach (var bundleCount in countsByBundle)
            {
                total += bundleCount.Value;
                if (formatted.Length > 0) formatted.Append(", ");
                formatted.Append(bundleCount.Key).Append('=').Append(bundleCount.Value);
            }
            return formatted.ToString();
        }

        private static string SourceBundleOf(GameObject prefab) =>
            _sourceBundleByPrefab.TryGetValue(prefab.name, out var bundleName) && !string.IsNullOrEmpty(bundleName)
                ? bundleName
                : UnknownBundleName;

        private static void AddToBundleCount(Dictionary<string, int> countsByBundle, string bundleName, int amount)
        {
            countsByBundle.TryGetValue(bundleName, out int previous);
            countsByBundle[bundleName] = previous + amount;
        }

        private static void RecordMissingScriptReject(GameObject prefab, int missingScripts)
        {
            string bundleName = SourceBundleOf(prefab);
            if (!_missingScriptRejectsByBundle.TryGetValue(bundleName, out var tally))
                _missingScriptRejectsByBundle[bundleName] = tally = new MissingScriptTally();
            tally.Prefabs++;
            tally.Slots += missingScripts;

            if (VAFapLoadSummary.VerboseEnabled)
                Debug.LogWarning($"{VaPieces.PluginName}: bundle prefab '{prefab.name}' from '{bundleName}' has {missingScripts} missing-script MonoBehaviour slot(s) — REJECTED from ZNetScene injection.");
        }

        // A missing-script slot is a component entry Unity reports as null because its serialized
        // script has no backing type. Inactive children are included.
        private static int CountMissingScripts(GameObject prefab)
        {
            int missing = 0;
            foreach (var transform in prefab.GetComponentsInChildren<Transform>(includeInactive: true))
                foreach (var component in transform.GetComponents<Component>())
                    if (component == null) missing++;
            return missing;
        }

        // The single walk ([ghost], Tools\FAP_CLIENT_LOAD_COST.md #6). The old way walked the whole
        // hierarchy once to count missing scripts, once per vanilla singleton type (15), once per
        // heal and up to five times in the router: about twenty native walks and arrays per prefab,
        // most of them empty. Main thread only; the lists are reused from prefab to prefab.
        private static readonly List<Transform> s_walkTransforms = new List<Transform>();
        private static readonly List<Component> s_walkComponents = new List<Component>();
        private static readonly List<EffectArea> s_walkEffectAreas = new List<EffectArea>();
        private static readonly List<Pickable> s_walkPickables = new List<Pickable>();
        private static readonly Dictionary<Type, bool> s_isVanillaSingletonByType = new Dictionary<Type, bool>();

        // Fills s_walkComponents with every live component of the prefab and its children, inactive
        // ones included, in the order GetComponentsInChildren returns them: depth first, each object's
        // components in their own order. Returns the missing-script count, counted exactly as
        // CountMissingScripts counts it, slot by slot on each transform.
        private static int WalkHierarchy(GameObject prefab)
        {
            s_walkComponents.Clear();
            prefab.GetComponentsInChildren(includeInactive: true, s_walkTransforms);
            int missing = 0;
            foreach (var transform in s_walkTransforms)
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) missing++;
                    else s_walkComponents.Add(component);
                }
            s_walkTransforms.Clear();
            return missing;
        }

        // The walk's T components, in walk order, into a reused list: what
        // GetComponentsInChildren<T>(includeInactive: true) would return now. A component destroyed
        // since the walk (a stripped singleton) is fake-null and left out, as a fresh search would.
        internal static List<T> CollectFromWalk<T>(List<Component> hierarchy, List<T> into) where T : Component
        {
            into.Clear();
            foreach (var component in hierarchy)
                if (component is T found && found != null) into.Add(found);
            return into;
        }

        private static bool IsVanillaSingleton(Type componentType)
        {
            if (s_isVanillaSingletonByType.TryGetValue(componentType, out bool isSingleton)) return isSingleton;
            foreach (var singletonType in VanillaSingletonComponentTypes)
                if (singletonType.IsAssignableFrom(componentType)) { isSingleton = true; break; }
            s_isVanillaSingletonByType[componentType] = isSingleton;
            return isSingleton;
        }

        // Destroyed with DestroyImmediate(allowDestroyingAssets) because the prefab is a bundle asset,
        // not a scene instance, and vanilla reads its components straight after this prefix returns.
        // A component whose OnDestroy throws is logged and the rest are still removed.
        //
        // With the walk, one pass over it (one type test per component class, cached) finds the
        // prefabs that carry a singleton at all, nearly none. Those are stripped in the old order,
        // type by type and in hierarchy order within a type, since removal order can matter when
        // one component requires another.
        private static void StripDangerousSingletonComponents(GameObject prefab, List<Component> hierarchy = null)
        {
            if (hierarchy != null)
            {
                bool carriesSingleton = false;
                foreach (var component in hierarchy)
                    if (IsVanillaSingleton(component.GetType())) { carriesSingleton = true; break; }
                if (!carriesSingleton) return;
            }

            var strippedTypeNames = new List<string>();
            foreach (var singletonType in VanillaSingletonComponentTypes)
            {
                if (hierarchy == null)
                {
                    foreach (var component in prefab.GetComponentsInChildren(singletonType, includeInactive: true))
                        StripSingleton(prefab, singletonType, component, strippedTypeNames);
                    continue;
                }
                foreach (var component in hierarchy)
                    if (singletonType.IsInstanceOfType(component))
                        StripSingleton(prefab, singletonType, component, strippedTypeNames);
            }
            if (strippedTypeNames.Count == 0) return;

            string bundleName = SourceBundleOf(prefab);
            AddToBundleCount(_singletonStripsByBundle, bundleName, strippedTypeNames.Count);
            _singletonStripTypes.UnionWith(strippedTypeNames);
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: StripDangerousSingletonComponents — stripped [{string.Join(",", strippedTypeNames)}] from bundle prefab '{prefab.name}' (source bundle: {bundleName}).");
        }

        private static void StripSingleton(GameObject prefab, Type singletonType, Component component, List<string> strippedTypeNames)
        {
            if (component == null) return;
            try
            {
                UnityEngine.Object.DestroyImmediate(component, allowDestroyingAssets: true);
                strippedTypeNames.Add(singletonType.Name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: StripDangerousSingletonComponents — DestroyImmediate({singletonType.Name}) on '{prefab.name}' threw: {ex.Message}");
            }
        }

        // Vanilla EffectArea.Awake sets GetComponent<Collider>().isTrigger with no null check, so a
        // marker shipped without its collider NREs on every instantiation (the furniture pack's
        // couchbed has such a PlayerBase child). The heal mirrors vanilla: a trigger SphereCollider
        // on the character_trigger layer. The layer matters twice — EffectArea queries that mask,
        // and placement and hover raycasts ignore it, so a 20m trigger on the default layer would
        // swallow every raycast nearby. Only bare markers are healed; re-layering an object that
        // renders would break its rendering and raycasts.
        // Pickable.GetHoverName dereferences m_itemPrefab with no null check:
        //
        //     return m_itemPrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
        //
        // A bundle prefab whose item reference did not survive therefore throws
        // from Hud.UpdateCrosshair — once per frame for as long as the crosshair
        // rests on it, not once. Vanilla's own early return is the cure: a
        // non-empty m_overrideName means that line is never reached. The prefab
        // name is used because it is the one string that identifies which asset
        // needs the real fix.
        private static void HealPickablesWithMissingItem(GameObject prefab, List<Component> hierarchy = null)
        {
            IList<Pickable> pickables = hierarchy == null
                ? (IList<Pickable>)prefab.GetComponentsInChildren<Pickable>(includeInactive: true)
                : CollectFromWalk(hierarchy, s_walkPickables);
            if (pickables.Count == 0) return;

            var healedNames = new List<string>();
            foreach (var pickable in pickables)
            {
                if (pickable == null) continue;
                if (!string.IsNullOrEmpty(pickable.m_overrideName)) continue;

                var itemPrefab = pickable.m_itemPrefab;
                if (itemPrefab != null && itemPrefab.GetComponent<ItemDrop>() != null) continue;

                pickable.m_overrideName = pickable.gameObject.name;
                healedNames.Add(pickable.gameObject.name);
            }
            if (healedNames.Count == 0) return;

            string bundleName = SourceBundleOf(prefab);
            AddToBundleCount(_pickableHealsByBundle, bundleName, healedNames.Count);
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.LogWarning($"{VaPieces.PluginName}: HealPickablesWithMissingItem — '{prefab.name}' had {healedNames.Count} Pickable(s) " +
                                 $"with no resolvable m_itemPrefab ({string.Join(", ", healedNames)}). Set m_overrideName so hovering them " +
                                 "cannot NRE every frame; they still will not yield their item until the reference is repaired.");
        }

        // Valheim 1.0 added three readers of a piece's requirements with no null check on the item:
        //
        //     resource.m_resItem.m_itemData.m_shared.m_name        (BuildUiPieceButton.RefreshSearchTerm)
        //     resource.m_resItem.gameObject.name                   (ByMaterialPieceList.UpdateAvailableTags)
        //     resource.m_resItem.m_itemData.m_shared.m_name        (Inventory.ItemCheated, from TryPlacePiece)
        //
        // RefreshSearchTerm runs from BuildUiPieceButton.Setup, so one listed piece whose item
        // reference did not survive throws out of BuildUi.OpenBuildMenu and the menu never opens;
        // placing such a piece throws too. HaveRequirements, ConsumeResources, Piece.DropResources and
        // the requirement panel all skip a null item already, so removing the entry changes no cost.
        // The item is gone from the bundle, so there is nothing to resolve it back to.
        private static void HealPiecesWithMissingRequirementItem(GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            if (piece == null) return;

            var kept = new List<Piece.Requirement>(piece.m_resources.Length);
            foreach (var requirement in piece.m_resources)
                if (requirement.m_resItem != null) kept.Add(requirement);
            int dropped = piece.m_resources.Length - kept.Count;
            if (dropped == 0) return;

            piece.m_resources = kept.ToArray();
            string bundleName = SourceBundleOf(prefab);
            AddToBundleCount(_requirementHealsByBundle, bundleName, 1);
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.LogWarning($"{VaPieces.PluginName}: HealPiecesWithMissingRequirementItem — '{prefab.name}' ({bundleName}) had {dropped} " +
                                 $"requirement(s) with no item; {kept.Count} left.");
        }

        private static void HealColliderlessEffectAreas(GameObject prefab, List<Component> hierarchy = null)
        {
            IList<EffectArea> areas = hierarchy == null
                ? (IList<EffectArea>)prefab.GetComponentsInChildren<EffectArea>(includeInactive: true)
                : CollectFromWalk(hierarchy, s_walkEffectAreas);
            if (areas.Count == 0) return;

            var healedDescriptions = new List<string>();
            foreach (var area in areas)
            {
                var marker = area.gameObject;
                if (marker.GetComponent<Collider>() != null) continue;
                if (marker.GetComponent<Renderer>() != null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: HealColliderlessEffectAreas — EffectArea on '{prefab.name}/{marker.name}' has no collider but sits on a RENDERED object; not healing (FiresAdminTerrain's Awake guard will skip it). Re-export the bundle with a dedicated trigger child.");
                    continue;
                }

                SphereCollider trigger;
                try { trigger = marker.AddComponent<SphereCollider>(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: HealColliderlessEffectAreas — AddComponent(SphereCollider) on '{prefab.name}/{marker.name}' threw: {ex.Message}");
                    continue;
                }
                bool baseLike = (area.m_type & (EffectArea.Type.PlayerBase | EffectArea.Type.NoMonsters)) != EffectArea.Type.None;
                trigger.isTrigger = true;
                trigger.radius = baseLike ? HealedBaseAreaRadius : HealedOtherAreaRadius;
                int triggerLayer = LayerMask.NameToLayer(CharacterTriggerLayerName);
                if (triggerLayer >= 0) marker.layer = triggerLayer;
                healedDescriptions.Add($"{marker.name} (type={area.m_type}, r={trigger.radius})");
            }
            if (healedDescriptions.Count == 0) return;

            string bundleName = SourceBundleOf(prefab);
            AddToBundleCount(_effectAreaHealsByBundle, bundleName, healedDescriptions.Count);
            Debug.LogWarning($"{VaPieces.PluginName}: HealColliderlessEffectAreas — added trigger SphereCollider to {healedDescriptions.Count} EffectArea marker(s) [{string.Join("; ", healedDescriptions)}] on bundle prefab '{prefab.name}' (source bundle: {bundleName}). Vanilla EffectArea.Awake NREs without a collider; the marker now behaves like a vanilla PlayerBase child.");
        }

        // Re-attaches the MonoBehaviours the source mod added at runtime, as recorded in the
        // manifest. VAScriptTypeResolver always returns a type: when nothing matches it returns
        // VAUnresolvedScriptStub, which keeps the slot occupied for ZDO compatibility and records
        // the original type and state so the user can see what is missing. A real type already on
        // the prefab is skipped, since the source mod may still be loaded.
        private static void ApplyScriptAttachments(GameObject prefab)
        {
            if (!_prefabScriptAttachments.TryGetValue(prefab.name, out var attachments) || attachments.Count == 0) return;
            if (!s_prefabsWithAttachmentsApplied.Add(prefab.GetInstanceID())) return;

            bool verbose = VAFapLoadSummary.VerboseEnabled;
            int added = 0, skippedExisting = 0, failed = 0, stubsAttached = 0;
            foreach (var attachment in attachments)
            {
                if (attachment == null || string.IsNullOrEmpty(attachment.typeFullName)) continue;

                long resolveStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var resolved = VAScriptTypeResolver.Resolve(attachment.typeFullName, attachment.assemblyName);
                long addStart = System.Diagnostics.Stopwatch.GetTimestamp();
                s_attachResolveTicks += addStart - resolveStart;
                if (resolved.Type == null) { failed++; continue; }
                if (!resolved.IsStub && prefab.GetComponent(resolved.Type) != null) { skippedExisting++; continue; }

                Component component;
                try { component = prefab.AddComponent(resolved.Type); }
                catch (Exception ex)
                {
                    failed++;
                    Debug.LogWarning($"{VaPieces.PluginName}: ApplyScriptAttachments — AddComponent({resolved.Type.FullName}) on '{prefab.name}' threw (resolution method: {resolved.Method}, original type: {attachment.typeFullName}): {ex.Message}");
                    continue;
                }
                long addTicks = System.Diagnostics.Stopwatch.GetTimestamp() - addStart;
                if (s_scriptTypesAddedOnce.Add(resolved.Type))
                {
                    s_attachFirstAdds++;
                    s_attachFirstAddTicks += addTicks;
                    if (addTicks > s_attachSlowestFirstAddTicks) { s_attachSlowestFirstAddTicks = addTicks; s_attachSlowestType = resolved.Type.Name; }
                }
                else s_attachRepeatAddTicks += addTicks;
                if (component == null) { failed++; continue; }

                if (resolved.IsStub)
                {
                    if (component is VAUnresolvedScriptStub stub)
                    {
                        stub.originalTypeFullName = attachment.typeFullName;
                        stub.originalAssemblyName = attachment.assemblyName;
                        stub.serializedJsonState = attachment.jsonState;
                    }
                    stubsAttached++;
                    if (verbose)
                        Debug.LogWarning($"{VaPieces.PluginName}: ApplyScriptAttachments — type '{attachment.typeFullName}' (from {attachment.assemblyName}) not found in any loaded assembly; attached VAUnresolvedScriptStub on '{prefab.name}' to preserve component slot. Add an entry to script_type_aliases.json to redirect.");
                }
                else if (!string.IsNullOrEmpty(attachment.jsonState) && attachment.jsonState != "{}")
                {
                    long jsonStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { JsonUtility.FromJsonOverwrite(attachment.jsonState, component); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: ApplyScriptAttachments — FromJsonOverwrite for {attachment.typeFullName} (resolved as {resolved.Type.FullName} via {resolved.Method}) on '{prefab.name}' threw: {ex.Message}. Component added but with default fields.");
                    }
                    s_attachJsonTicks += System.Diagnostics.Stopwatch.GetTimestamp() - jsonStart;
                }
                added++;
                s_attachAdded++;
            }

            if (verbose && (added > 0 || failed > 0))
                Debug.Log($"{VaPieces.PluginName}: ApplyScriptAttachments on '{prefab.name}': +{added} added (incl. {stubsAttached} stubs), {skippedExisting} already present, {failed} failed.");
        }

        // ─────────────────────────────────────────────────────────
        //  Fake-null scrubbing
        // ─────────────────────────────────────────────────────────

        // Runs after vanilla has built m_namedPrefabs, first among the postfixes.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VAEwdCompatScrub_Patch
        {
            [HarmonyPriority(Priority.First)]
            [HarmonyPostfix]
            public static void Postfix(ZNetScene __instance)
            {
                if (__instance != null) ScrubFakeNulls(__instance, "ZNetScene.Awake postfix");
            }
        }

        // The last scrub before ExpandWorld walks the collections from its own ZoneSystem.Start
        // postfix.
        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        public static class ZoneSystem_Start_VAEwdCompatScrub_Patch
        {
            [HarmonyPriority(Priority.First)]
            [HarmonyPrefix]
            public static void Prefix()
            {
                if (ZNetScene.instance != null) ScrubFakeNulls(ZNetScene.instance, "ZoneSystem.Start prefix");
            }
        }

        // Removes Unity fake-null entries (destroyed objects) from every vanilla collection
        // ExpandWorld Data walks to build its default value groups. EWD NREs on the first one,
        // which corrupts its prefab cache and cascades into later world-load failures such as
        // Better Continents' KeyNotFoundException. Vegetation and clutter entries are also removed
        // when only their m_prefab is destroyed. Locations are checked only for dead entries,
        // because ZoneLocation.m_prefab is a SoftReference this project doesn't reference.
        private static void ScrubFakeNulls(ZNetScene scene, string siteLabel)
        {
            int purgedPrefabs = scene.m_prefabs.RemoveAll(prefab => prefab == null);
            int purgedNamedPrefabs = RemoveDestroyedNamedPrefabs(scene);

            int purgedItems = 0, purgedStatusEffects = 0;
            var objectDb = ObjectDB.instance;
            if (objectDb != null)
            {
                if (objectDb.m_items != null) purgedItems = objectDb.m_items.RemoveAll(item => item == null);
                if (objectDb.m_StatusEffects != null) purgedStatusEffects = objectDb.m_StatusEffects.RemoveAll(statusEffect => statusEffect == null);
            }

            int purgedLocations = 0, purgedVegetation = 0;
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem != null)
            {
                if (zoneSystem.m_locations != null) purgedLocations = zoneSystem.m_locations.RemoveAll(location => location == null);
                if (zoneSystem.m_vegetation != null) purgedVegetation = zoneSystem.m_vegetation.RemoveAll(vegetation => vegetation == null || vegetation.m_prefab == null);
            }

            int purgedClutter = 0;
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem != null && clutterSystem.m_clutter != null)
                purgedClutter = clutterSystem.m_clutter.RemoveAll(clutter => clutter == null || clutter.m_prefab == null);

            int total = purgedPrefabs + purgedNamedPrefabs + purgedItems + purgedStatusEffects + purgedLocations + purgedVegetation + purgedClutter;
            if (total > 0)
                Debug.Log($"{VaPieces.PluginName}: ScrubFakeNulls ({siteLabel}) — purged "
                    + $"{purgedPrefabs} from ZNetScene.m_prefabs, {purgedNamedPrefabs} from m_namedPrefabs, "
                    + $"{purgedItems} from ObjectDB.m_items, {purgedStatusEffects} from m_StatusEffects, "
                    + $"{purgedLocations} from ZoneSystem.m_locations, {purgedVegetation} from m_vegetation, "
                    + $"{purgedClutter} from ClutterSystem.m_clutter (EWD/WEC/BC compat).");
        }

        private static int RemoveDestroyedNamedPrefabs(ZNetScene scene)
        {
            var destroyedHashes = new List<int>();
            foreach (var namedPrefab in scene.m_namedPrefabs)
                if (namedPrefab.Value == null) destroyedHashes.Add(namedPrefab.Key);
            foreach (int hash in destroyedHashes) scene.m_namedPrefabs.Remove(hash);
            return destroyedHashes.Count;
        }

        // ─────────────────────────────────────────────────────────
        //  Folder bootstrap
        // ─────────────────────────────────────────────────────────

        private static void EnsureFoldersExist()
        {
            try
            {
                Directory.CreateDirectory(AssetsDir);
                Directory.CreateDirectory(ManifestsDir);
                WriteReadmeIfMissing(ManifestsDir, ManifestsReadme);
                WriteReadmeIfMissing(AssetsDir, AssetsReadme);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAExternalBundleLoader could not create assets folder: {ex.Message}");
            }
        }

        private static void WriteReadmeIfMissing(string folder, string text)
        {
            string readmePath = Path.Combine(folder, ReadmeFileName);
            if (!File.Exists(readmePath)) File.WriteAllText(readmePath, text);
        }

        private static bool IsBundleFile(string path, out string reason)
        {
            string extension = Path.GetExtension(path) ?? string.Empty;
            if (ExcludedExtensions.Contains(extension))
            {
                reason = $"excluded extension '{extension}'";
                return false;
            }
            if (extension.Length == 0)
            {
                bool hasHeader = HasUnityFsHeader(path);
                reason = hasHeader ? "extensionless with UnityFS header" : "extensionless but no UnityFS header (likely not a bundle)";
                return hasHeader;
            }
            if (BundleExtensions.Contains(extension))
            {
                reason = $"recognized extension '{extension}'";
                return true;
            }
            reason = $"unrecognized extension '{extension}' (expected .bundle / .assetbundle / .unity3d / extensionless UnityFS)";
            return false;
        }

        // SteamyDumps saves raw bundles under their internal name with no extension, so those are
        // accepted only when the file starts with the "UnityFS" signature.
        private static bool HasUnityFsHeader(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < UnityFsHeaderBytes) return false;
                    var header = new byte[UnityFsSignature.Length];
                    if (stream.Read(header, 0, header.Length) < header.Length) return false;
                    return Encoding.ASCII.GetString(header) == UnityFsSignature;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private const string ManifestsReadme =
            "FiresAdminPrefabs — bundle manifests\n" +
            "======================================\n\n" +
            "This folder is parallel to Assets/. Each manifest is the full\n" +
            "inventory of its sibling bundle: every prefab, its category, and\n" +
            "(from newer SteamyDumps builds) its runtime scripts and materials.\n" +
            "FAP reads the manifest at world load to:\n" +
            "  * Route each prefab to the right hammer tab by its category.\n" +
            "  * Register clutter prefabs into ClutterSystem.m_clutter\n" +
            "    (disabled, so they never auto-spawn, but findable by name\n" +
            "    so ExpandWorldData / EWP can resolve them).\n" +
            "  * Re-attach scripts the source mod added at runtime.\n" +
            "  * Repair broken materials from the recorded shader and texture.\n\n" +
            "Baked blueprints also keep a .bakemanifest here beside their\n" +
            ".bakebundle in Assets/.\n\n" +
            "===== Generating manifests =====\n\n" +
            "Use FiresSteamyDumps' YoinkScanMe console command:\n" +
            "  1. Drop bundle / DLL into BepInEx/ScanMe/\n" +
            "  2. Run `YoinkScanMe` in the console (F5)\n" +
            "  3. Output appears at:\n" +
            "     config/FiresSteamyDumps/Yoink/ScanMe/Bundles/<bundleName>\n" +
            "     config/FiresSteamyDumps/Yoink/ScanMe/Manifests/<bundleName>.manifest.json\n" +
            "  4. Run fap_import_yoink, or copy:\n" +
            "       Bundles/* into BepInEx/config/FiresAdminPrefabs/Assets/\n" +
            "       Manifests/* into BepInEx/config/FiresAdminPrefabs/Manifests/\n" +
            "  5. Re-enter the world.\n\n" +
            "===== File naming =====\n\n" +
            "  One manifest per bundle:  <bundleName>.manifest.json\n" +
            "  Pairs with raw bundle:    Assets/<bundleName>      (no extension)\n\n" +
            "  Example:\n" +
            "    Assets/biomeflora\n" +
            "    Manifests/biomeflora.manifest.json\n\n" +
            "Categories: clutter | piece | pickable | container | plant | minerock | other\n";

        private const string AssetsReadme =
            "FiresAdminPrefabs — external asset bundle folder\n" +
            "==================================================\n\n" +
            "Drop Unity AssetBundle files into this folder. FiresAdminPrefabs\n" +
            "loads them on the next world load and registers every GameObject\n" +
            "prefab into ZNetScene + the Hammer PieceTable.\n\n" +
            "Accepted file types:\n" +
            "  - *.bundle, *.assetbundle, *.unity3d  (any of these extensions)\n" +
            "  - Extensionless files with a UnityFS header  (magic-byte sniffed)\n" +
            "  - The second is what FiresSteamyDumps' YoinkScanMe produces —\n" +
            "    raw bundle saves are written with their internal name, no\n" +
            "    extension. Just copy them straight here, no renaming needed.\n\n" +
            "Always SKIPPED (won't even try to load): .txt, .md, .yaml, .yml,\n" +
            "  .json, .cfg, .ini, .log, .manifest, .meta, .old, .bak\n" +
            "Baked blueprints (.bakebundle) also live here; FAP loads those\n" +
            "separately.\n\n" +
            "===== Getting bundles via SteamyDumps =====\n\n" +
            "1. In SteamyDumps' ScanMe folder (BepInEx/ScanMe/), drop:\n" +
            "     - The .dll of any installed mod that ships bundles as\n" +
            "       embedded resources, OR\n" +
            "     - Raw .bundle / .unity3d files extracted from anywhere.\n" +
            "2. Run the YoinkScanMe console command in-game.\n" +
            "3. Run fap_import_yoink, or copy the extensionless <bundleName>\n" +
            "   file from Config/FiresSteamyDumps/Yoink/ScanMe/Bundles/ here.\n" +
            "4. Restart Valheim or rejoin the world to load it.\n\n" +
            "===== Multiplayer note =====\n\n" +
            "Server and clients both need the same bundles. FiresAdminPrefabs\n" +
            "sends this folder from the server to each client when they connect,\n" +
            "and prompts them to reconnect once new bundles arrive.\n\n" +
            "===== Troubleshooting =====\n\n" +
            "  - A prefab name that vanilla or an earlier-loaded mod already\n" +
            "    registered is skipped; the injection summary counts these.\n" +
            "  - 'Bundle X returned null (corrupt, wrong Unity version, or\n" +
            "    already-loaded by a sibling mod)' → the bundle was built\n" +
            "    against a different Unity version than Valheim is running,\n" +
            "    or another mod's plugin already loaded it from its own\n" +
            "    embedded resources.\n";
    }
}
