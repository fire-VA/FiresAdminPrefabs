using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Patches;
using VerdantsAscent.Utilities;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Managers
{
    // Bridges VABlueprintLoader into the hammer's tab system.
    //
    // Scans HammerTabs/Blueprints/ for .blueprint and .vbuild files, and for each one
    // parses it, re-centres it on its bounding box, and registers a synthetic
    // "BP_<filename>" stub — an empty GameObject carrying Piece + ZNetView — into
    // ZNetScene and the hammer's Blueprints category. Icons render from a proxy stage
    // built out of the blueprint's own pieces and cache to PNG.
    //
    // The stub exists because the whole hammer pipeline (piece table, placement ghost,
    // snapping, recipe gating) requires a real Piece on a real registered GameObject.
    // It is never actually placed: VABlueprintPlacementPatch intercepts PlacePiece and
    // runs SpawnBlueprintAt instead. Its ZNetView is non-persistent, so even if some
    // path did slip past the patch, the stub couldn't survive a zone reload.
    public static class VABlueprintHammerIntegration
    {
        public const string BlueprintsTabName = "Blueprints";
        public const string BlueprintPrefabPrefix = "BP_";

        public static string BlueprintsDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "HammerTabs", BlueprintsTabName);

        // Synthetic prefabName → loaded Blueprint. Used by the placement
        // patch to look up "what should I actually spawn for this stub?"
        private static readonly Dictionary<string, VABlueprintLoader.Blueprint> _blueprintsByPrefabName =
            new Dictionary<string, VABlueprintLoader.Blueprint>(StringComparer.Ordinal);

        // Synthetic prefabName → stub GameObject. Tracked so we can
        // detect "is this prefab one we created?" cheaply at placement
        // patch time.
        private static readonly Dictionary<string, GameObject> _stubPrefabsByName =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        // Per-prefab snapshot of the blueprint's source-file path so
        // the placement patch can log the source filename for debugging.
        private static readonly Dictionary<string, string> _sourcePathByName =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly HashSet<string> _ghostVisualsBuilt = new HashSet<string>(StringComparer.Ordinal);

        // Matches VAVanillaPieceManager's InitFrameBudgetMs so the scan and that loop yield at the same cadence.
        private const long ScanFrameBudgetMs = 8;

        private const int BadgeRefreshAttempts = 15;
        private const float BadgeRefreshIntervalSeconds = 2f;

        // Padding (per axis, metres) added around each piece center when
        // building the BP-local overlap AABB in PlayerOverlapsBlueprintBounds.
        private static readonly Vector3 OverlapBoundsPad = new Vector3(1f, 1f, 1f);

        private static bool _scanCompleted;
        private static Coroutine _ghostCacheBakeCoroutine;

        public static bool IsBlueprintStub(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            return _stubPrefabsByName.ContainsKey(prefabName);
        }

        // The one naming rule for stubs. Registration and orphan detection both go
        // through it, so the two can never disagree about what a stub is called.
        public static string MakeStubName(string blueprintPath) =>
            BlueprintPrefabPrefix + Path.GetFileNameWithoutExtension(blueprintPath).Replace(' ', '_');

        private static Dictionary<int, string> _stubNamesByHash;

        // Resolves a prefab hash to a stub name using the blueprint files on disk rather
        // than the registry. The registry is empty for a non-admin and before the
        // deferred scan runs — which is exactly when an orphan's zone streams in.
        public static bool TryGetStubNameForHash(int prefabHash, out string stubName)
        {
            if (_stubNamesByHash == null) _stubNamesByHash = BuildStubNameIndex();
            return _stubNamesByHash.TryGetValue(prefabHash, out stubName);
        }

        private static Dictionary<int, string> BuildStubNameIndex()
        {
            var index = new Dictionary<int, string>();
            try
            {
                foreach (var path in VABlueprintLoader.EnumerateAllBlueprintFiles())
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    string stubName = MakeStubName(path);
                    index[stubName.GetStableHashCode()] = stubName;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration — stub name index build failed: {ex.Message}");
            }
            return index;
        }

        public static bool TryGetBlueprint(string prefabName, out VABlueprintLoader.Blueprint bp)
        {
            bp = null;
            if (string.IsNullOrEmpty(prefabName)) return false;
            return _blueprintsByPrefabName.TryGetValue(prefabName, out bp);
        }

        // Stubs are added to ZNetScene.m_prefabs but not to its named index, so callers that need a stub
        // by name must come through here instead of walking m_prefabs.
        public static bool TryGetStubPrefab(string prefabName, out GameObject stub)
        {
            stub = null;
            if (string.IsNullOrEmpty(prefabName)) return false;
            return _stubPrefabsByName.TryGetValue(prefabName, out stub);
        }

        // The bake cache key for a blueprint: its source file name ("Phalanx.blueprint"), or its
        // name when it has no source file. The bake cache, bundles and bake-parent ZDOs all use it.
        public static string GetCacheKey(VABlueprintLoader.Blueprint blueprint) =>
            !string.IsNullOrEmpty(blueprint.SourcePath) ? Path.GetFileName(blueprint.SourcePath) : blueprint.Name;

        public static bool TryGetBlueprintBySourceFileName(string cacheKey, out VABlueprintLoader.Blueprint bp)
        {
            bp = null;
            if (string.IsNullOrEmpty(cacheKey)) return false;
            foreach (var candidate in _blueprintsByPrefabName.Values)
            {
                if (candidate == null || !string.Equals(GetCacheKey(candidate), cacheKey, StringComparison.Ordinal)) continue;
                bp = candidate;
                return true;
            }
            return false;
        }

        public static IEnumerable<string> RegisteredBlueprintPrefabNames => _stubPrefabsByName.Keys;
        public static int RegisteredCount => _stubPrefabsByName.Count;

        // Phase-1 counts stashed so phase-2 can emit a unified
        // 📐 BLUEPRINTS summary banner with parsed/failed/baked all
        // in one visual unit. See the EmitBlueprints call at the end
        // of the phase-2 coroutine.
        private static int _phase1Parsed;
        private static int _phase1Failed;

        // ──────────────────────────────────────────────────────────────
        //  Scan + register
        // ──────────────────────────────────────────────────────────────

        public static void EnsureBlueprintsFolder()
        {
            try
            {
                if (!Directory.Exists(BlueprintsDir)) Directory.CreateDirectory(BlueprintsDir);

                string readme = Path.Combine(BlueprintsDir, "README.txt");
                if (File.Exists(readme)) return;
                File.WriteAllText(readme,
                    "FiresAdminPrefabs — Blueprints folder\n" +
                    "======================================\n\n" +
                    "Drop your PlanBuild .blueprint (or BuildShare .vbuild)\n" +
                    "files into THIS folder. On the next world load, each one\n" +
                    "appears as a placeable piece in the hammer's 'Blueprints'\n" +
                    "tab. Clicking to place it spawns every piece the blueprint\n" +
                    "contains, oriented to the placement direction.\n\n" +
                    "Icons are auto-generated the first time the blueprint is\n" +
                    "scanned and cached as PNGs (same cache the rest of FAP's\n" +
                    "hammer icons use). Use `fap_reload_icons` to force a\n" +
                    "re-render.\n\n" +
                    "Compatible formats:\n" +
                    "  *.blueprint  (PlanBuild)\n" +
                    "  *.vbuild     (BuildShare / older InfinityHammer)\n\n" +
                    "Per-client: this folder lives in your local config. Other\n" +
                    "players on the server organize their own blueprint sets.\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration.EnsureBlueprintsFolder failed: {ex.Message}");
            }
        }

        private sealed class ParsedBlueprintFile
        {
            public string Path;
            public VABlueprintLoader.Blueprint Blueprint;
            public string Error;
        }

        // Worker-thread half of the scan: file reads, text parsing and centering only — plain data, no
        // Unity objects. Files go out in enumeration order so the consumer's first-wins duplicate rule
        // holds. EnumerateAllBlueprintFiles reads the TOP LEVEL of FAP's own Blueprints folder only;
        // PlanBuild dirs are searched by name later (EnumerateCandidatePaths), never scanned for the tab.
        private static void ParseAllBlueprintFiles(ConcurrentQueue<ParsedBlueprintFile> into)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in VABlueprintLoader.EnumerateAllBlueprintFiles())
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!seen.Add(Path.GetFullPath(path))) continue;

                var file = new ParsedBlueprintFile { Path = path };
                try
                {
                    var blueprint = VABlueprintLoader.ParseFile(path);
                    if (blueprint != null && blueprint.Objects.Count > 0)
                    {
                        blueprint.Center("");
                        file.Blueprint = blueprint;
                    }
                }
                catch (Exception ex)
                {
                    file.Error = ex.Message;
                }
                into.Enqueue(file);
            }
        }

        // Runs from VAVanillaPieceManager's heavy init, after the Blueprints category
        // exists.
        //
        // Phase 1 is this coroutine. A worker reads, parses and centers every blueprint file
        // (the folder can hold well over 100 MB of text; single files reach tens of MB), and
        // this coroutine takes the results in file order to build each bare stub, register it
        // into ZNetScene and the hammer table, and queue its icon — the GameObject half, which
        // has to stay on the main thread.
        //
        // Phase 2 starts at the end as its own coroutine: it checks every blueprint's ghost cache in the
        // background and bakes the missing or stale ones. A ghost's meshes are only attached when the player
        // picks that blueprint, by EnsureGhostVisuals.
        public static IEnumerator ScanAndRegisterCoroutine(
            ZNetScene scene, PieceTable pieceTable, Piece.PieceCategory bpCategory)
        {
            if (_scanCompleted) yield break;
            _scanCompleted = true;
            _stubNamesByHash = null;

            if (scene == null || pieceTable == null) yield break;

            EnsureBlueprintsFolder();

            var parsedFiles = new ConcurrentQueue<ParsedBlueprintFile>();
            var parsing = Task.Run(() => ParseAllBlueprintFiles(parsedFiles));
            int parsed = 0, failed = 0, registered = 0;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                // Checked first so the many early `continue`s below can't carry the frame past its budget.
                if (sw.ElapsedMilliseconds >= ScanFrameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }

                if (!parsedFiles.TryDequeue(out var file))
                {
                    if (parsing.IsCompleted && parsedFiles.IsEmpty) break;
                    yield return null;
                    sw.Restart();
                    continue;
                }

                string path = file.Path;
                if (file.Error != null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration parse failed for '{path}': {file.Error}");
                    failed++;
                    continue;
                }
                var bp = file.Blueprint;
                if (bp == null) { failed++; continue; }
                parsed++;

                string stubName = MakeStubName(path);
                if (_stubPrefabsByName.ContainsKey(stubName))
                {
                    // Two files with same base name — keep the first,
                    // skip the duplicate. User can rename to resolve.
                    Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration — duplicate stub name '{stubName}' (second source: {path}). Skipping.");
                    continue;
                }

                var stub = BuildBlueprintStubBare(stubName, bp, bpCategory);
                if (stub == null) { failed++; continue; }

                // Register into ZNetScene so the hammer's eligibility
                // path can find it. The stub is NON-PERSISTENT — never
                // becomes a real ZDO at placement (the placement patch
                // intercepts). BuildBlueprintStubBare just made this
                // GameObject, so neither list can already hold it and a
                // Contains() would only be a scan of every prefab.
                scene.m_prefabs.Add(stub);
                pieceTable.m_pieces.Add(stub);

                _stubPrefabsByName[stubName] = stub;
                _blueprintsByPrefabName[stubName] = bp;
                _sourcePathByName[stubName] = path;
                registered++;

                // Register the stub for LAZY icon rendering — NOT enqueued
                // now. Most sessions never open the Blueprints tab, so
                // rendering (or even cache-loading) every blueprint icon at
                // world-load is wasted work. The BuildUi.OnSelectTag postfix
                // flushes these into the cache-first render path the first
                // time the Blueprints tab is actually shown (OnBlueprintsTabOpened).
                // Adopts FiresDungeonMaster's "render only what's viewed"
                // principle while keeping our disk cache + proxy renderer.
                var piece = stub.GetComponent<Piece>();
                if (piece != null) VAPieceIconRenderer.RegisterDeferredBlueprint(piece);
            }

            if (parsing.IsFaulted)
                Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration — blueprint scan stopped early: {parsing.Exception?.GetBaseException().Message}");

            // Stash phase-1 counts so phase-2 can fold them into the
            // unified 📐 BLUEPRINTS summary banner. Phase-1 itself
            // doesn't emit a banner — single combined banner reads
            // better than two split ones.
            _phase1Parsed = parsed;
            _phase1Failed = failed;

            // Phase-1 plain log is verbose-only. The Blueprints/ folder
            // path is useful for diagnostics but not for normal loads.
            if (VerdantsAscent.Utilities.VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: VABlueprintHammerIntegration phase-1 registered {registered} blueprint(s) ({parsed} parsed, {failed} failed). Blueprints/ folder: {BlueprintsDir}");

            // The scan is frame-budgeted, so with a large Blueprints folder it finishes long after the build
            // menu last rebuilt its available pieces; rebuild once so the new stubs show in the tab.
            if (Player.m_localPlayer != null)
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VABlueprintHammerIntegration");

            if ((UnityEngine.Object)VaPieces.Instance != (UnityEngine.Object)null)
            {
                VaPieces.Instance.StartCoroutine(RefreshBakedBadgesCoroutine());
                if (_ghostCacheBakeCoroutine != null)
                    VaPieces.Instance.StopCoroutine(_ghostCacheBakeCoroutine);
                _ghostCacheBakeCoroutine = VaPieces.Instance.StartCoroutine(BakeMissingGhostCachesCoroutine());
            }
        }

        // Bakes a ghost cache for each blueprint without a current one, so picking it later only reads from
        // disk. Each bake's file finishes writing before the next bake starts, which keeps at most one
        // blueprint's mesh data in memory.
        private static IEnumerator BakeMissingGhostCachesCoroutine()
        {
            var blueprints = new List<VABlueprintLoader.Blueprint>(_blueprintsByPrefabName.Values);
            var cacheCheck = VAFapBlueprintGhostCache.CheckCachesInBackground(blueprints);
            while (!cacheCheck.IsCompleted) yield return null;

            int alreadyCached = 0, baked = 0;
            foreach (var blueprint in blueprints)
            {
                if (VAFapBlueprintGhostCache.HasCachedGhost(blueprint)) { alreadyCached++; continue; }
                var scene = ZNetScene.instance;
                if (scene == null) yield break;

                yield return VaPieces.Instance.StartCoroutine(VAFapBlueprintGhostCache.BakeToCacheCoroutine(blueprint, scene));
                if (VAFapBlueprintGhostCache.HasCachedGhost(blueprint)) baked++;
                while (VAFapBlueprintGhostCache.HasPendingWrites) yield return null;
            }

            VAFapLoadSummary.EmitBlueprints(_phase1Parsed, _phase1Failed, alreadyCached + baked);
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: VABlueprintHammerIntegration — ghost caches ready for {alreadyCached + baked} of {blueprints.Count} blueprint(s): {alreadyCached} already current, {baked} baked.");
        }

        // Paints the baked badge on blueprint stubs whose bundles are registered and copies the badged icons to
        // those bundle prefabs. Icons render lazily, so it repeats until a pass paints nothing after an earlier
        // pass painted something, or it runs out of attempts.
        private static IEnumerator RefreshBakedBadgesCoroutine()
        {
            int paintedTotal = 0;
            for (int attempt = 0; attempt < BadgeRefreshAttempts; attempt++)
            {
                int paintedThisPass = VABakeCacheIconBadge.RefreshAllStubs();
                VABakeBundle.CopyBadgedIconsToBundlePrefabs();
                paintedTotal += paintedThisPass;
                if (paintedTotal > 0 && paintedThisPass == 0) yield break;
                yield return new WaitForSeconds(BadgeRefreshIntervalSeconds);
            }
        }

        // Called when the Blueprints tab is shown. Releases the deferred blueprint icons to the renderer and,
        // when any were released, repaints the baked badges as those icons arrive.
        public static void OnBlueprintsTabOpened()
        {
            int flushed = VAPieceIconRenderer.FlushDeferredBlueprints();
            if (flushed > 0 && (UnityEngine.Object)VaPieces.Instance != (UnityEngine.Object)null)
                VaPieces.Instance.StartCoroutine(RefreshBakedBadgesCoroutine());
        }

        // Gives a blueprint stub its ghost meshes the first time the player picks it; later picks reuse them.
        public static void EnsureGhostVisuals(string stubName)
        {
            if (string.IsNullOrEmpty(stubName) || _ghostVisualsBuilt.Contains(stubName)) return;
            if (!_stubPrefabsByName.TryGetValue(stubName, out var stub) || stub == null) return;
            if (!_blueprintsByPrefabName.TryGetValue(stubName, out var blueprint)) return;
            var scene = ZNetScene.instance;
            if (scene == null) return;

            VAFapBlueprintGhostCache.AttachGhost(stub, blueprint, scene);
            _ghostVisualsBuilt.Add(stubName);
        }

        // A bare stub is Piece + ZNetView only, which is all the hammer needs to list it. Its ghost meshes
        // are attached by EnsureGhostVisuals when the player picks the blueprint.
        private static GameObject BuildBlueprintStubBare(string stubName, VABlueprintLoader.Blueprint bp, Piece.PieceCategory bpCategory)
        {
            try
            {
                var go = new GameObject(stubName);
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.SetActive(false);   // stays inactive so Awake doesn't fire until placement

                var nview = go.AddComponent<ZNetView>();
                nview.m_persistent = false;

                var piece = go.AddComponent<Piece>();
                piece.m_name = string.IsNullOrEmpty(bp.Name) ? stubName : bp.Name;
                piece.m_description = BuildPieceDescription(bp);
                piece.m_enabled = true;
                piece.m_category = bpCategory;
                piece.m_clipGround = true;
                piece.m_clipEverything = true;
                piece.m_allowRotatedOverlap = true;
                piece.m_canBeRemoved = true;
                piece.m_resources = Array.Empty<Piece.Requirement>();

                return go;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration.BuildBlueprintStubBare failed for '{stubName}': {ex.Message}");
                return null;
            }
        }

        private static string BuildPieceDescription(VABlueprintLoader.Blueprint bp)
        {
            string desc = string.IsNullOrEmpty(bp.Description)
                ? $"Blueprint: {bp.Objects.Count} piece(s)"
                : bp.Description;
            return $"<color=#ffaa00>{desc}</color>\nSource: {Path.GetFileName(bp.SourcePath)}";
        }

        // ──────────────────────────────────────────────────────────────
        //  Placement — called by the Harmony patch on Player.PlacePiece
        // ──────────────────────────────────────────────────────────────

        // Makes a non-default piece scale persist. Setting transform.localScale alone never
        // reached the ZDO, so pieces snapped back to scale 1 on zone reload and for remote
        // clients, breaking snap alignment. This forces m_syncInitialScale and goes
        // through SetLocalScale, which writes the scale ZDO vars the way Structure Tweaks'
        // AllScalable does; VABlueprintGroupPatches re-applies it on load even without ST.
        public static void ApplyPersistentScale(GameObject go, Vector3 scale)
        {
            if (go == null) return;
            var znv = go.GetComponent<ZNetView>();
            if (znv != null && znv.IsValid())
            {
                znv.m_syncInitialScale = true;
                znv.SetLocalScale(scale);
            }
            else
            {
                // Non-networked spawn (ghost/preview) — local transform only.
                go.transform.localScale = scale;
            }
        }

        // Spawns every piece of the blueprint at the placement transform, rotating each
        // piece's position and rotation by the player's yaw so the blueprint faces the way
        // they do. Returns how many pieces spawned.
        public static int SpawnBlueprintAt(VABlueprintLoader.Blueprint bp, Vector3 worldPos, Quaternion worldRot, Player ownerForPlace)
        {
            if (bp == null || bp.Objects.Count == 0) return 0;

            int spawned = 0;
            int missing = 0;
            var scene = ZNetScene.instance;
            if (scene == null) return 0;

            // Refuses the placement when the blueprint's bounds would overlap the local
            // player. Spawning hundreds of MeshColliders on top of the player capsule
            // sends PhysX into a depenetration spiral — placing a 113K-piece blueprint on
            // the character crashed the game — and it's never the result anyone wanted.
            //
            // The AABB comes from the piece positions in blueprint space, padded for piece
            // extent, then moved into world space and tested against the player's capsule.
            if (PlayerOverlapsBlueprintBounds(bp, worldPos, worldRot, ownerForPlace))
            {
                if (ownerForPlace != null)
                {
                    ownerForPlace.Message(MessageHud.MessageType.Center,
                        "$msg_blueprint_blocked_by_player", 0, null);
                }
                // Fallback default message if the localization key
                // isn't bound — Valheim shows raw key text, which is
                // ugly. Direct-text follow-up via Center type so user
                // always sees something readable.
                if (ownerForPlace != null)
                {
                    ownerForPlace.Message(MessageHud.MessageType.Center,
                        "Can't place blueprint — move out of the way first.", 0, null);
                }
                Debug.Log($"{VaPieces.PluginName}: VABlueprintHammerIntegration — placement of '{bp.Name}' blocked: player inside blueprint bounds at {worldPos}.");
                return 0;
            }

            // Generate a fresh group ID for this placement. Every piece
            // spawned below gets this ID stamped into its ZDO under
            // `va_bp_group`. ZNetView.Awake postfix
            // (VABlueprintGroupPatches) registers each piece into the
            // group registry, so destroying ANY one piece (hammer
            // remove, WearNTear destruction, tree chop on a tree we
            // placed as part of the blueprint, etc.) propagates to
            // wipe every other piece + the bake-parent visual via
            // ZNetScene.Destroy prefix. See VABlueprintGroupRegistry
            // for the full destroy-cascade model.
            string groupId = Utilities.VABlueprintGroupRegistry.NewGroupId();

            // The Blueprints tab always spawns the full live blueprint: real prefabs,
            // vanilla physics, IH-compatible health. Baked content is its own placeable
            // on the Baked Meshes tab, so there's no bake-attach path here and the bake
            // cache is never used to skip pieces. It only drives the "baked" badge on
            // this tab's icons.
            float? healthOverride = Utilities.VAInfinityHammerCompat.GetEffectiveHealth();

            // Collect spawned GameObjects — historically used by
            // the post-loop bake-attach pass which no longer runs.
            // Kept for the banner counter + future per-piece passes
            // (e.g., the EWP `va_bp_group` cascade test); allocation
            // is trivial.
            var spawnedGOs = new List<GameObject>(bp.Objects.Count);

            // Diagnostic counters (banner-friendly).
            int veggies = 0;       // pieces classified as vegetation

            // Cache-key resolution stays — the placement banner
            // names the blueprint by its source filename, and
            // downstream consumers (badge tracker, debug logs) read
            // the same value.
            string srcLabelEarly = GetCacheKey(bp);

            foreach (var piece in bp.Objects)
            {
                if (piece == null) continue;
                if (piece.Chance < 1f && UnityEngine.Random.value > piece.Chance) continue;

                var prefab = scene.GetPrefab(piece.Prefab);
                if (prefab == null) { missing++; continue; }

                Vector3 spawnPos = worldPos + (worldRot * piece.Pos);
                Quaternion spawnRot = worldRot * piece.Rot;

                try
                {
                    // ZDO-first spawn: parse the piece's saved data blob (IH /
                    // WEC / EWD DataEntry base64) and write it — plus group id
                    // and health override — into the init-ZDO BEFORE Awake.
                    // Direct Instantiate dropped piece.Data entirely, so
                    // infinite-health / Structure-Tweaks-overridden floating
                    // pieces spawned as bare vanilla pieces and collapsed on
                    // the first support update. Legacy PlanBuild ExtraInfo
                    // (sign text, item stands) rides the same path.
                    Utilities.VAZdoDataBlob blob = null;
                    if (!string.IsNullOrEmpty(piece.Data))
                        Utilities.VAZdoDataBlob.TryParse(piece.Data, out blob);

                    var go = Utilities.VAZdoFirstSpawner.Instantiate(
                        prefab, spawnPos, spawnRot, blob, piece.ExtraInfo,
                        zdo =>
                        {
                            zdo.Set(VABlueprintGroupRegistry.ZdoKey, groupId);
                            if (healthOverride.HasValue)
                                Utilities.VAInfinityHammerCompat.ApplyHealthOverride(zdo, prefab.GetComponent<WearNTear>() != null);
                        }, out _);
                    if (go == null) { missing++; continue; }
                    if (piece.Scale != Vector3.one && piece.Scale != Vector3.zero)
                        ApplyPersistentScale(go, piece.Scale);

                    // Stamp VABlueprintPieceMarker on every spawned
                    // piece. The WearNTear_SetHealthVisual_
                    // VABlueprintSkip_Patch checks for this marker
                    // and skips vanilla's SetHealthVisual on baked
                    // pieces whose wear-state children were destroyed
                    // by the baker. Interactive (non-baked) pieces
                    // also get the marker — harmless (the marker
                    // patch only matters when wear-state children
                    // are missing, which happens only on baked
                    // pieces).
                    try
                    {
                        go.AddComponent<Utilities.VABlueprintPieceMarker>();
                    }
                    catch (Exception markerEx)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration — marker add on '{piece.Prefab}' threw: {markerEx.Message}");
                    }

                    // Neither WearNTear neutralize nor wear-state stripping happens
                    // here. Placements behave like any other player build — vanilla
                    // stability, support and weather decay all apply — and the baker
                    // prunes wear-state children itself, after its IsInteractive check
                    // has already excluded Smelters, Doors, Fireplaces and Containers.
                    //
                    // Pruning here unconditionally, as an earlier version did, ran
                    // before that gate and destroyed those children on interactive
                    // pieces. Smelter caches references into its child hierarchy at
                    // Awake, so the next tick called SetActive on a destroyed object —
                    // 1260 interactive pieces on a Phalanx placement meant 1260 NREs a
                    // second, indefinitely.

                    // Group id + health override landed pre-Awake via the
                    // spawner's mutate hook, so the group-register Awake
                    // patch already saw the real id; Register here is a
                    // harmless de-dup that also covers prefabs whose Awake
                    // patch ordering skipped it.
                    //
                    // Vegetation detection runs AFTER Instantiate so
                    // we can inspect the live components on the GO
                    // (more reliable than the prefab template alone).
                    bool isVeggie = false;
                    var znv = go.GetComponent<ZNetView>();
                    if (znv != null && znv.IsValid())
                    {
                        VABlueprintGroupRegistry.Register(groupId, znv);
                    }
                    isVeggie = Utilities.VABlueprintVegetationFilter.IsVegetation(prefab);
                    if (isVeggie) veggies++;

                    // Add to the bake list ONLY if non-vegetation.
                    // Vegetation pieces (trees / bushes / pickables)
                    // stay as live independent GameObjects so they
                    // keep wind-sway shaders + pick interactions
                    // working. They're still in the destroy group
                    // (per user spec: chopping a tree wipes the
                    // blueprint).
                    if (!isVeggie)
                        spawnedGOs.Add(go);

                    spawned++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintHammerIntegration spawn failed for piece '{piece.Prefab}': {ex.Message}");
                }
            }

            // srcLabel was pre-computed early (srcLabelEarly) for the
            // banner. Re-declare locally with the same value.
            string srcLabel = srcLabelEarly;

            // Banner-format placement log. The bakeStatus / bake
            // banner pair no longer fires from this method — baked
            // prefab placements are now a different code path
            // entirely (Player.PlacePiece → VABundlePlacementSpawn
            // postfix on the piece_<srcLabel> bundle prefab from
            // the Baked Meshes tab). This banner just records the
            // live blueprint placement.
            string healthLabel = healthOverride.HasValue
                ? (healthOverride.Value < 0f ? "hp: invuln (IH)" : $"hp: {healthOverride.Value:F0} (IH)")
                : (Utilities.VAInfinityHammerCompat.IsInfinityHammerLoaded ? "hp: vanilla (IH off)" : "hp: vanilla");
            Utilities.VAFapLoadSummary.EmitPlacing(
                blueprintName: srcLabel,
                pieces: spawned,
                veggies: veggies,
                missingPrefabs: missing,
                healthLabel: healthLabel,
                bakeStatus: "live",
                groupId: groupId);

            return spawned;
        }

        // True when the local player would be inside the blueprint's bounds at this
        // placement, so SpawnBlueprintAt can refuse before instantiating anything.
        //
        // The blueprint-space AABB of the piece positions is padded 1m per axis for
        // piece extent, its eight corners are rotated and translated into world space,
        // and the player's collider centre is tested against that box inflated by the
        // player's radius. Rotating a box and re-boxing it overestimates, which is the
        // safe direction: a false block on an edge case beats a false allow and a crash.
        private static bool PlayerOverlapsBlueprintBounds(VABlueprintLoader.Blueprint bp, Vector3 worldPos, Quaternion worldRot, Player player)
        {
            try
            {
                if (player == null) return false;
                if (bp == null || bp.Objects == null || bp.Objects.Count == 0) return false;

                // Build BP-local AABB from piece positions.
                Vector3 lmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 lmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                bool any = false;
                foreach (var p in bp.Objects)
                {
                    if (p == null) continue;
                    var pos = p.Pos;
                    if (pos.x < lmin.x) lmin.x = pos.x;
                    if (pos.y < lmin.y) lmin.y = pos.y;
                    if (pos.z < lmin.z) lmin.z = pos.z;
                    if (pos.x > lmax.x) lmax.x = pos.x;
                    if (pos.y > lmax.y) lmax.y = pos.y;
                    if (pos.z > lmax.z) lmax.z = pos.z;
                    any = true;
                }
                if (!any) return false;

                lmin -= OverlapBoundsPad; lmax += OverlapBoundsPad;

                // 8 corners of the local AABB.
                var corners = new Vector3[8]
                {
                    new Vector3(lmin.x, lmin.y, lmin.z),
                    new Vector3(lmax.x, lmin.y, lmin.z),
                    new Vector3(lmin.x, lmax.y, lmin.z),
                    new Vector3(lmin.x, lmin.y, lmax.z),
                    new Vector3(lmax.x, lmax.y, lmin.z),
                    new Vector3(lmax.x, lmin.y, lmax.z),
                    new Vector3(lmin.x, lmax.y, lmax.z),
                    new Vector3(lmax.x, lmax.y, lmax.z),
                };

                // Rotate + translate each corner into world space; take
                // world AABB.
                Vector3 wmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 wmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector3 wc = worldPos + (worldRot * corners[i]);
                    if (wc.x < wmin.x) wmin.x = wc.x;
                    if (wc.y < wmin.y) wmin.y = wc.y;
                    if (wc.z < wmin.z) wmin.z = wc.z;
                    if (wc.x > wmax.x) wmax.x = wc.x;
                    if (wc.y > wmax.y) wmax.y = wc.y;
                    if (wc.z > wmax.z) wmax.z = wc.z;
                }

                // Inflate world AABB by player radius (~0.5m) + a
                // safety margin for fast travel + animation step.
                const float playerRadiusM = 1.0f;
                wmin -= new Vector3(playerRadiusM, playerRadiusM, playerRadiusM);
                wmax += new Vector3(playerRadiusM, playerRadiusM, playerRadiusM);

                // Test player center against inflated AABB.
                Vector3 pc = player.transform.position;
                bool inX = pc.x >= wmin.x && pc.x <= wmax.x;
                bool inY = pc.y >= wmin.y && pc.y <= wmax.y;
                bool inZ = pc.z >= wmin.z && pc.z <= wmax.z;
                return inX && inY && inZ;
            }
            catch (Exception ex)
            {
                // Never block placement due to a check error —
                // user-reported crash is the real concern; the
                // overlap check is an additional safety net.
                Debug.LogWarning($"{VaPieces.PluginName}: PlayerOverlapsBlueprintBounds threw: {ex.Message}");
                return false;
            }
        }
    }
}
