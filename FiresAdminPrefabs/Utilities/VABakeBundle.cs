using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using FiresCore.Net;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using Debug = UnityEngine.Debug;
using UnityEngine.Rendering;
using PartsByZoneAndLod = System.Collections.Generic.Dictionary<VerdantsAscent.Utilities.VABakeZoneKey, System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<VerdantsAscent.Utilities.VAFapBlueprintBakeCache.CachedPart>>>;

namespace VerdantsAscent.Utilities
{
    // Persists baked blueprints as deployable prefabs: a .bakebundle binary in Assets/ with a
    // .bakemanifest sidecar in Manifests/. Both folders are synced by VAFapSyncManifest, so a
    // server's bakes reach clients on connect.
    //
    // Each world load registers a mesh-less piece_* prefab per bundle from its manifest. The
    // mesh is built the first time the bundle enters the world, through a ZDO instantiating it
    // or a placement ghost, so bundles nobody encounters cost nothing. The binary format lives
    // in VAFapBakeCacheDisk; this isn't a Unity AssetBundle because BuildPipeline is editor-only.
    public static class VABakeBundle
    {
        public const string BundleExt = ".bakebundle";
        public const string ManifestExt = ".bakemanifest";

        private const int ManifestVersion = 1;
        private const int ManifestBakeFormatVersion = 3;
        private const string PrefabNamePrefix = "piece_";
        private const string BlueprintExt = ".blueprint";
        private const string TempFileExt = ".tmp";
        private const string UnnamedAssetBaseName = "_unnamed";
        private const string BakeDescriptionColor = "#ffaa00";
        private static readonly Vector3 TemplateParkPosition = new Vector3(100000f, 100000f, 100000f);
        // Registration runs during world load, where it competes with vanilla's own asynchronous
        // bundle and texture reads. Those reads are on a deadline nobody rechecks: a location's
        // soft-reference preload is released after a fixed number of zone iterations whether or
        // not its read finished, and a starved read dies with the archive. Manifest I/O now runs
        // on a worker, so what is left on this thread is pure CPU and drains in a few frames.
        private const long RegistrationFrameBudgetMs = 2;
        private const long SlowColliderCookWarnThresholdMs = 1000;
        private const double SlowLazyBuildLogThresholdMs = 250;
        private const int LiveChildrenPerFrame = 200;

        // The one cook policy for every collider FAP bakes, here and in VAFapBlueprintMeshBaker. The
        // parallel Physics.BakeMesh pre-cook must pass these same options or its cooked BVH isn't
        // reused. UseFastMidphase is left out because PhysX's fast midphase misses collisions past
        // 2^21 indices — about 700k triangles — which baked parts routinely exceed.
        internal const MeshColliderCookingOptions ColliderCookOptions =
            MeshColliderCookingOptions.EnableMeshCleaning |
            MeshColliderCookingOptions.WeldColocatedVertices |
            MeshColliderCookingOptions.CookForFasterSimulation;

        public static string AssetsDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "Assets");
        public static string ManifestsDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "Manifests");

        // False while RegisterAllAsync runs. On a host, the respawn hold patch keeps the player
        // on the loading screen until registration finishes.
        public static bool BakeComplete { get; private set; }

        private static int _pendingBundleWrites;

        // Bundle files still being compressed and written by Publish. Bake sweeps wait for this
        // to reach zero, because a restart mid-write leaves a .tmp that never loads.
        public static int PendingBundleWrites => Volatile.Read(ref _pendingBundleWrites);

        // Descriptive sidecar written beside each bundle. Registration reads the source file
        // name, part count and triangle count; the other fields document the bake.
        [Serializable]
        public class Manifest
        {
            public int bakeManifestVersion = ManifestVersion;
            public string sourceBlueprintFile;
            public string sourceSha1;
            public string registerAsPrefab;
            public int pieceCount;
            public int partCount;
            public int totalTris;
            public List<string> materialNames = new List<string>();
            public int bakeFormatVersion = ManifestBakeFormatVersion;
            public bool lodEnabled;
            public int zoneCount = 1;
            public int maxLodLevels = 1;
            public List<int> trisPerLodLevel = new List<int>();
        }

        private sealed class LazyBundle
        {
            public string CacheKey;
            public string BundlePath;
            public string PrefabName;
            public GameObject Root;
        }

        private static readonly Dictionary<int, LazyBundle> _lazyBundles = new Dictionary<int, LazyBundle>();

        private static readonly Dictionary<string, string> _prefabNameToCacheKey =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // ─────────────────────────────────────────────────────────
        //  Naming
        // ─────────────────────────────────────────────────────────

        // "Phalanx.blueprint" → "Phalanx_blueprint", the base name for the bundle and manifest
        // files. Keeping the extension as a suffix stops sources that differ only by extension
        // from colliding.
        private static string MakeAssetBaseName(string sourceFileName)
        {
            if (string.IsNullOrEmpty(sourceFileName)) return UnnamedAssetBaseName;
            var builder = new StringBuilder(sourceFileName.Length);
            foreach (char ch in sourceFileName)
                builder.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return builder.ToString();
        }

        // "Phalanx.blueprint" → "piece_Phalanx", matching vanilla piece naming so tools that
        // filter on piece_* pick bundles up.
        public static string MakePrefabName(string cacheKey) =>
            PrefabNamePrefix + MakeAssetBaseName(StripBlueprintExtension(cacheKey));

        private static string StripBlueprintExtension(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return name.EndsWith(BlueprintExt, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - BlueprintExt.Length)
                : name;
        }

        private static string DescribeBake(int partCount, int triangleCount, string sourceFileName) =>
            $"<color={BakeDescriptionColor}>Baked blueprint: {partCount} part(s), {triangleCount:N0} tris</color>\nSource: {sourceFileName}";

        // Source blueprint file name for a registered bundle prefab, or null. Prefab names are
        // sanitized, so they can't be turned back into a file name directly.
        public static string TryGetCacheKeyForPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            return _prefabNameToCacheKey.TryGetValue(prefabName, out var cacheKey) ? cacheKey : null;
        }

        public static IEnumerable<string> RegisteredBundlePrefabNames => _prefabNameToCacheKey.Keys;

        public static string BundlePathFor(string cacheKey) =>
            Path.Combine(AssetsDir, MakeAssetBaseName(cacheKey) + BundleExt);

        public static string ManifestPathFor(string cacheKey) =>
            Path.Combine(ManifestsDir, MakeAssetBaseName(cacheKey) + ManifestExt);

        // ─────────────────────────────────────────────────────────
        //  Disk state
        // ─────────────────────────────────────────────────────────

        // A bake has only happened once its bundle is on disk. Publish returns as soon as the
        // write is queued, so a sweep that trusts its return value reports a clean run straight
        // through a disk-full failure.
        public static bool BundleLandedOnDisk(string cacheKey)
        {
            try { return File.Exists(BundlePathFor(cacheKey)); }
            catch { return false; }
        }

        // Negative when the drive can't be read, which callers treat as "unknown, carry on".
        public static long FreeBytesForBundles()
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AssetsDir))).AvailableFreeSpace; }
            catch { return -1; }
        }

        // An interrupted or failed write leaves a .tmp that nothing loads and nothing else
        // removes. Only safe while no write is in flight.
        public static int SweepStaleTempFiles()
        {
            if (PendingBundleWrites > 0) return 0;
            int removed = 0;
            foreach (string dir in new[] { AssetsDir, ManifestsDir })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string path in Directory.GetFiles(dir, "*" + TempFileExt, SearchOption.TopDirectoryOnly))
                        if (TryDelete(path)) removed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle temp sweep failed in '{dir}': {ex.Message}");
                }
            }
            return removed;
        }

        // A manifest whose bundle never landed registers nothing; it only makes a lost bake look
        // published.
        public static int RemoveManifestsWithoutBundles()
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(ManifestsDir)) return 0;
                foreach (string manifestPath in Directory.GetFiles(ManifestsDir, "*" + ManifestExt, SearchOption.TopDirectoryOnly))
                {
                    string bundlePath = Path.Combine(AssetsDir, Path.GetFileNameWithoutExtension(manifestPath) + BundleExt);
                    if (File.Exists(bundlePath)) continue;
                    if (TryDelete(manifestPath)) removed++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle orphan-manifest sweep failed: {ex.Message}");
            }
            return removed;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        // ─────────────────────────────────────────────────────────
        //  Publishing
        // ─────────────────────────────────────────────────────────

        // Writes the bundle and manifest for a fresh bake. The manifest is written immediately;
        // the bundle is compressed and written on a worker thread, and counted in
        // PendingBundleWrites until it lands.
        public static bool Publish(VAFapBlueprintBakeCache.CachedBake cached, string cacheKey, string sourcePath)
        {
            if (cached == null || string.IsNullOrEmpty(cacheKey)) return false;
            try
            {
                Directory.CreateDirectory(AssetsDir);
                Directory.CreateDirectory(ManifestsDir);

                var serialized = VAFapBakeCacheDisk.SerializeFromInMemory(cached, cacheKey, sourcePath);
                if (serialized == null) return false;

                string baseName = MakeAssetBaseName(cacheKey);
                string bundlePath = BundlePathFor(cacheKey);
                string manifestPath = ManifestPathFor(cacheKey);

                Interlocked.Increment(ref _pendingBundleWrites);
                if (!ThreadPool.QueueUserWorkItem(_ => WriteBundleFile(serialized, bundlePath, manifestPath)))
                    Interlocked.Decrement(ref _pendingBundleWrites);

                var manifest = BuildManifest(serialized, cacheKey);
                string manifestTempPath = manifestPath + TempFileExt;
                File.WriteAllText(manifestTempPath, JsonUtility.ToJson(manifest, prettyPrint: true));
                ReplaceFile(manifestTempPath, manifestPath);

                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"{VaPieces.PluginName}: VABakeBundle published '{baseName}' → {Path.GetFileName(bundlePath)} + {Path.GetFileName(manifestPath)} (prefab='{manifest.registerAsPrefab}', {manifest.partCount} parts, {manifest.materialNames.Count} mats, {manifest.zoneCount} zone(s), maxLOD={manifest.maxLodLevels}, lodEnabled={manifest.lodEnabled}).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.Publish failed for '{cacheKey}': {ex.Message}");
                return false;
            }
        }

        private static void WriteBundleFile(VAFapBakeCacheDisk.SerializedBlueprintBake serialized, string bundlePath, string manifestPath)
        {
            string tempPath = bundlePath + TempFileExt;
            try
            {
                var writeStopwatch = Stopwatch.StartNew();
                VAFapBakeCacheDisk.WriteToFile(serialized, tempPath);
                ReplaceFile(tempPath, bundlePath);
                VAFapThreadLog.Info($"{VaPieces.PluginName}: VABakeBundle background-write of '{Path.GetFileName(bundlePath)}' finished in {writeStopwatch.Elapsed.TotalSeconds:F2}s.");
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VABakeBundle background-write failed for '{Path.GetFileName(bundlePath)}': {ex.Message}");
                // The bundle and its sidecar go together. Leaving the manifest behind is what
                // turns a failed write into a bake that looks published and registers nothing.
                TryDelete(tempPath);
                TryDelete(manifestPath);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingBundleWrites);
            }
        }

        private static void ReplaceFile(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(sourcePath, destinationPath);
        }

        private static Manifest BuildManifest(VAFapBakeCacheDisk.SerializedBlueprintBake serialized, string cacheKey)
        {
            var manifest = new Manifest
            {
                sourceBlueprintFile = cacheKey,
                sourceSha1 = serialized.SourceSha1,
                registerAsPrefab = MakePrefabName(cacheKey),
                pieceCount = serialized.CandidatePieces + serialized.InteractivePieces,
                partCount = serialized.Parts.Count,
                totalTris = serialized.TotalTris,
            };

            var zoneKeys = new HashSet<VABakeZoneKey>();
            var materialNames = new HashSet<string>(StringComparer.Ordinal);
            var trisByLodLevel = new Dictionary<int, int>();
            foreach (var part in serialized.Parts)
            {
                zoneKeys.Add(new VABakeZoneKey { X = part.ZoneX, Y = part.ZoneY, Z = part.ZoneZ });
                manifest.maxLodLevels = Math.Max(manifest.maxLodLevels, part.LodLevel + 1);
                trisByLodLevel.TryGetValue(part.LodLevel, out int levelTris);
                trisByLodLevel[part.LodLevel] = levelTris + part.TriCount;
                if (part.MaterialNames == null) continue;
                foreach (var materialName in part.MaterialNames)
                    if (!string.IsNullOrEmpty(materialName)) materialNames.Add(materialName);
            }
            manifest.lodEnabled = manifest.maxLodLevels > 1;
            if (zoneKeys.Count > 0) manifest.zoneCount = zoneKeys.Count;
            manifest.materialNames.AddRange(materialNames);
            for (int lodLevel = 0; lodLevel < manifest.maxLodLevels; lodLevel++)
            {
                trisByLodLevel.TryGetValue(lodLevel, out int levelTris);
                manifest.trisPerLodLevel.Add(levelTris);
            }
            return manifest;
        }

        // Deletes a blueprint's bundle and manifest before a re-bake, and drops its cached icon
        // since the mesh changes under the same prefab name.
        public static void Unpublish(string cacheKey)
        {
            try
            {
                TryDelete(BundlePathFor(cacheKey));
                TryDelete(ManifestPathFor(cacheKey));
                VAPieceIconRenderer.InvalidateIcon(MakePrefabName(cacheKey));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.Unpublish threw for '{cacheKey}': {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Registration
        // ─────────────────────────────────────────────────────────

        // Registers a mesh-less prefab for every bundle in Assets/ and pushes them onto the
        // hammer. Runs on every ZNetScene.Awake: templates die with ZNetScene, because keeping
        // them alive across the main menu leaked their live-child components into vanilla's
        // per-type instance lists and NRE'd there.
        public static IEnumerator RegisterAllAsync(ZNetScene scene)
        {
            if (scene == null) yield break;

            _prefabNameToCacheKey.Clear();
            _lazyBundles.Clear();
            BakeComplete = false;
            VAFapPlayerPieces.BeginSession();
            try
            {
                if (!Directory.Exists(AssetsDir))
                {
                    Debug.Log($"{VaPieces.PluginName}: VABakeBundle.RegisterAll — Assets/ does not exist, no .bakebundle files to load.");
                    yield break;
                }

                float scanStartTime = Time.realtimeSinceStartup;
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"{VaPieces.PluginName}: [t={scanStartTime:F2}s] VABakeBundle.RegisterAll — START. Scanning {AssetsDir} for *{BundleExt} files.");

                var manifestQueue = new ConcurrentQueue<PendingManifest>();
                var readState = new ManifestReadState();
                if (!ThreadPool.QueueUserWorkItem(_ => ReadManifestsInBackground(manifestQueue, readState)))
                    ReadManifestsInBackground(manifestQueue, readState);

                int succeeded = 0, failed = 0;
                var mainThreadStopwatch = new Stopwatch();
                var frameStopwatch = Stopwatch.StartNew();
                while (true)
                {
                    if (!manifestQueue.TryDequeue(out var pending))
                    {
                        // Done is set only after the last Enqueue, so an empty queue behind it is
                        // an empty queue for good. Waiting on the reader costs no frame time.
                        if (readState.Done && manifestQueue.IsEmpty) break;
                        yield return null;
                        frameStopwatch.Restart();
                        continue;
                    }

                    mainThreadStopwatch.Start();
                    if (RegisterLazyBundle(scene, pending)) succeeded++; else failed++;
                    mainThreadStopwatch.Stop();

                    if (frameStopwatch.ElapsedMilliseconds < RegistrationFrameBudgetMs) continue;
                    yield return null;
                    frameStopwatch.Restart();
                }

                float scanSeconds = Time.realtimeSinceStartup - scanStartTime;
                try { VAFapLoadSummary.EmitBundlesBaked(succeeded, failed, scanSeconds, (float) mainThreadStopwatch.Elapsed.TotalMilliseconds); }
                catch (Exception ex)
                {
                    Debug.Log($"{VaPieces.PluginName}: VABakeBundle.RegisterAll done — {succeeded} per-blueprint prefab(s) registered, {failed} failed in {scanSeconds:F2}s (summary banner threw: {ex.Message}).");
                }

                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"{VaPieces.PluginName}: VABakeBundle.RegisterAll — manifest reader finished in {readState.ReadSeconds:F2}s off-thread.");

                PushBundlePiecesOntoHammer(scene);
            }
            finally
            {
                BakeComplete = true;
                VAFapPlayerPieces.OnBakedPiecesRegistered();
            }
        }

        // Runs on the manifest reader thread, so it logs through the queue rather than Unity's.
        private static string[] FindBundleFiles()
        {
            try { return Directory.GetFiles(AssetsDir, "*" + BundleExt, SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VABakeBundle.RegisterAll scan threw: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private sealed class PendingManifest
        {
            public string BundlePath;
            public string ManifestText; // null when the sidecar is missing or unreadable
        }

        private sealed class ManifestReadState
        {
            private volatile bool _done;

            // Written last by the reader; the main thread only reads it once Done is observed.
            public double ReadSeconds;

            public bool Done { get { return _done; } set { _done = value; } }
        }

        // Fetches every bundle's manifest sidecar off the main thread. One file read per bundle,
        // taken one at a time on the main thread, is what stretched registration across seconds
        // of world load. Only the text is read here: JsonUtility and every GameObject call the
        // registration makes have to stay on the main thread.
        private static void ReadManifestsInBackground(ConcurrentQueue<PendingManifest> queue, ManifestReadState state)
        {
            var readStopwatch = Stopwatch.StartNew();
            try
            {
                foreach (var bundlePath in FindBundleFiles())
                {
                    string manifestText = null;
                    try
                    {
                        string manifestPath = Path.Combine(ManifestsDir, Path.GetFileNameWithoutExtension(bundlePath) + ManifestExt);
                        if (File.Exists(manifestPath)) manifestText = File.ReadAllText(manifestPath);
                    }
                    catch (Exception ex)
                    {
                        VAFapThreadLog.Warning($"{VaPieces.PluginName}: VABakeBundle manifest read failed for '{Path.GetFileName(bundlePath)}': {ex.Message}");
                    }
                    queue.Enqueue(new PendingManifest { BundlePath = bundlePath, ManifestText = manifestText });
                }
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VABakeBundle manifest reader threw: {ex.Message}");
            }
            finally
            {
                state.ReadSeconds = readStopwatch.Elapsed.TotalSeconds;
                state.Done = true;
            }
        }

        // The hammer snapshots ZNetScene's prefabs before registration finishes, so bundle
        // pieces are added to it afterwards.
        private static void PushBundlePiecesOntoHammer(ZNetScene scene)
        {
            try
            {
                var bundlePrefabs = new List<GameObject>(_prefabNameToCacheKey.Count);
                foreach (var prefabName in _prefabNameToCacheKey.Keys)
                {
                    var bundlePrefab = scene.GetPrefab(prefabName);
                    if (bundlePrefab != null) bundlePrefabs.Add(bundlePrefab);
                }
                int added = VAVanillaPieceManager.RegisterLateBundlePieces(bundlePrefabs);
                if (added > 0)
                    Debug.Log($"{VaPieces.PluginName}: VABakeBundle.RegisterAll — pushed {added} late bundle piece(s) onto the hammer.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.RegisterAll — late hammer registration threw: {ex.Message}");
            }
        }

        // Registers one bundle's mesh-less root and records it for a later build. Only the small
        // manifest is read, never the bundle itself.
        private static bool RegisterLazyBundle(ZNetScene scene, PendingManifest pending)
        {
            string bundlePath = pending.BundlePath;
            try
            {
                var manifest = ParseManifest(pending.ManifestText);
                if (manifest == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle — no manifest for '{Path.GetFileName(bundlePath)}'; can't resolve prefab name. Skipped.");
                    return false;
                }

                if (manifest.bakeFormatVersion != ManifestBakeFormatVersion)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle — '{Path.GetFileName(bundlePath)}' is bake format v{manifest.bakeFormatVersion}, this build reads v{ManifestBakeFormatVersion}. Re-bake it (fap_rebake_all) — it is not registered.");
                    return false;
                }

                string cacheKey = manifest.sourceBlueprintFile;
                string prefabName = MakePrefabName(cacheKey);
                int prefabHash = prefabName.GetStableHashCode();
                if (scene.m_namedPrefabs.ContainsKey(prefabHash))
                {
                    _prefabNameToCacheKey[prefabName] = cacheKey;
                    return true;
                }

                var root = BuildTemplateRoot(prefabName, manifest);
                if (root == null) return false;

                scene.m_prefabs.Add(root);
                scene.m_namedPrefabs[prefabHash] = root;
                _prefabNameToCacheKey[prefabName] = cacheKey;
                _lazyBundles[prefabHash] = new LazyBundle { CacheKey = cacheKey, BundlePath = bundlePath, PrefabName = prefabName, Root = root };
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.RegisterLazyBundle threw for '{Path.GetFileName(bundlePath)}': {ex.Message}");
                return false;
            }
        }

        private static Manifest ParseManifest(string manifestText)
        {
            if (string.IsNullOrEmpty(manifestText)) return null;
            try
            {
                var manifest = JsonUtility.FromJson<Manifest>(manifestText);
                return manifest == null || string.IsNullOrEmpty(manifest.sourceBlueprintFile) ? null : manifest;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Builds a registered bundle's mesh into its root, once. Synchronous, so the first
        // appearance of a large bundle stutters. The entry is claimed before building so a
        // re-entrant fetch can't build it twice, and meshes still alive in the bake cache are
        // reused without reading the bundle file.
        public static void EnsureLazyBundleBuilt(int prefabHash)
        {
            if (!_lazyBundles.TryGetValue(prefabHash, out var entry)) return;
            _lazyBundles.Remove(prefabHash);

            var scene = ZNetScene.instance;
            if (scene == null || entry.Root == null) return;

            try
            {
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"Pre-warming {StripBlueprintExtension(entry.CacheKey)}…", 0, null);

                var buildTimer = Stopwatch.StartNew();
                double readSeconds = 0, meshSeconds = 0;
                if (!VAFapBlueprintBakeCache.TryGetMaterializedAlive(entry.CacheKey, out var cached))
                {
                    var serialized = VAFapBakeCacheDisk.ReadFromFile(entry.BundlePath);
                    readSeconds = buildTimer.Elapsed.TotalSeconds;
                    if (serialized == null)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.EnsureLazyBundleBuilt — unreadable bundle for '{entry.PrefabName}'.");
                        return;
                    }
                    cached = VAFapBakeCacheDisk.Materialize(serialized, BuildMaterialLookupForBundle(scene, serialized));
                    meshSeconds = buildTimer.Elapsed.TotalSeconds - readSeconds;
                }
                if (cached == null || cached.Parts.Count == 0)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.EnsureLazyBundleBuilt — no parts for '{entry.PrefabName}'.");
                    return;
                }

                BuildZoneBucketsForTemplate(entry.Root, cached.Parts, entry.PrefabName);

                var piece = entry.Root.GetComponent<Piece>();
                if (piece != null) piece.m_description = DescribeBake(cached.Parts.Count, cached.TotalTris, entry.CacheKey);

                VAPieceManager.DistantRenderPrefabs.Add(entry.PrefabName);
                VAFapBlueprintBakeCache.SetMaterialized(entry.CacheKey, cached);

                double totalSeconds = buildTimer.Elapsed.TotalSeconds;
                if (VAFapLoadSummary.VerboseEnabled || totalSeconds * 1000 >= SlowLazyBuildLogThresholdMs)
                    Debug.Log($"{VaPieces.PluginName}: VABakeBundle — built '{entry.PrefabName}' on first use in {totalSeconds:F2}s (file read {readSeconds:F2}s, meshes {meshSeconds:F2}s, hierarchy + colliders {totalSeconds - readSeconds - meshSeconds:F2}s, {cached.Parts.Count} parts, {DescribeLodSplit(cached.Parts)}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.EnsureLazyBundleBuilt threw for '{entry.PrefabName}': {ex.Message}");
            }
        }

        // A lazy bundle's mesh is built only where one actually enters the world: a ZDO being
        // instantiated, a player placement, or a placement ghost.
        //
        // This used to hang off ZNetScene.GetPrefab(int), which is a lookup, not an
        // instantiation — and GetPrefab(string) routes through it. So every lookup built a
        // mesh, including RegisterAllAsync's own loop that fetches each bundle to push it onto
        // the hammer. With 190 bundles that built all 1.7 GB synchronously in a single frame
        // and froze the client for 152s before it could join. Placement is covered by
        // VABlueprintPlacementPatch.
        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        private static class ZNetScene_CreateObject_LazyBuild_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(ZDO zdo)
            {
                if (_lazyBundles.Count == 0 || zdo == null) return;
                EnsureLazyBundleBuilt(zdo.GetPrefab());
            }
        }

        // The ghost is instantiated from the piece table's prefab directly, never via
        // ZNetScene, so it needs its own trigger or a baked mesh previews as nothing.
        // Builds exactly the one bundle the player just selected.
        [HarmonyPatch(typeof(Player), nameof(Player.SetupPlacementGhost))]
        private static class Player_SetupPlacementGhost_LazyBuild_Patch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.High)]
            public static void Prefix(Player __instance)
            {
                if (_lazyBundles.Count == 0 || __instance == null) return;
                var selected = __instance.m_buildPieces != null ? __instance.m_buildPieces.GetSelectedPrefab() : null;
                if (selected == null) return;
                EnsureLazyBundleBuilt(selected.name.GetStableHashCode());
            }
        }

        // Prefers the source blueprint's own prefab materials and falls back to every prefab in
        // ZNetScene when the blueprint isn't loaded.
        private static Dictionary<string, Material> BuildMaterialLookupForBundle(
            ZNetScene scene, VAFapBakeCacheDisk.SerializedBlueprintBake serialized)
        {
            Dictionary<string, Material> materialLookup = null;
            if (VABlueprintHammerIntegration.TryGetBlueprintBySourceFileName(serialized.CacheKey, out var blueprint) && blueprint != null)
                materialLookup = VAFapBlueprintMaterialLookup.Build(blueprint, scene);
            if (materialLookup == null || materialLookup.Count == 0)
                materialLookup = BuildGlobalMaterialLookup(scene);
            return materialLookup;
        }

        private static Dictionary<string, Material> BuildGlobalMaterialLookup(ZNetScene scene)
        {
            var materialsByName = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material != null && !materialsByName.ContainsKey(material.name))
                            materialsByName[material.name] = material;
                    }
                }
            }
            return materialsByName;
        }

        // Copies each blueprint stub's badged icon onto its piece_* bundle prefab, so the bundle
        // shows the same picture on the Baked Meshes tab and anywhere else that reads piece
        // icons from ZNetScene. The Sprite is shared, so re-badging the stub updates both.
        public static int CopyBadgedIconsToBundlePrefabs()
        {
            try
            {
                var scene = ZNetScene.instance;
                if (scene == null || scene.m_prefabs == null) return 0;

                var stubsByBundlePrefabName = BuildStubIndexByBundlePrefabName();
                int copied = 0;
                foreach (var prefab in scene.m_prefabs)
                {
                    if (prefab == null || !prefab.name.StartsWith(PrefabNamePrefix, StringComparison.Ordinal)) continue;
                    var bundlePiece = prefab.GetComponent<Piece>();
                    if (bundlePiece == null || bundlePiece.m_icon != null) continue;

                    if (!stubsByBundlePrefabName.TryGetValue(prefab.name, out var stub)) continue;
                    if (!VABlueprintHammerIntegration.TryGetStubPrefab(stub.StubName, out var stubPrefab) || stubPrefab == null) continue;
                    var stubPiece = stubPrefab.GetComponent<Piece>();
                    if (stubPiece == null || stubPiece.m_icon == null) continue;

                    VABakeCacheIconBadge.ApplyBadgeIfCached(stubPiece, stub.CacheKey);
                    bundlePiece.m_icon = stubPiece.m_icon;
                    copied++;
                }
                return copied;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.CopyBadgedIconsToBundlePrefabs threw: {ex.Message}");
                return 0;
            }
        }

        private struct BlueprintStubRef
        {
            public string StubName;
            public string CacheKey;
        }

        // One pass over the registered blueprints, keyed by the bundle prefab name each one bakes to.
        // The caller then answers "which blueprint is this prefab?" with a lookup instead of walking
        // every blueprint (and, inside that, every prefab) for each of them.
        private static Dictionary<string, BlueprintStubRef> BuildStubIndexByBundlePrefabName()
        {
            var index = new Dictionary<string, BlueprintStubRef>(StringComparer.Ordinal);
            foreach (var stubName in VABlueprintHammerIntegration.RegisteredBlueprintPrefabNames)
            {
                if (!VABlueprintHammerIntegration.TryGetBlueprint(stubName, out var blueprint) || blueprint == null) continue;
                string cacheKey = !string.IsNullOrEmpty(blueprint.SourcePath)
                    ? Path.GetFileName(blueprint.SourcePath)
                    : blueprint.Name;
                string bundlePrefabName = MakePrefabName(cacheKey);
                if (index.ContainsKey(bundlePrefabName)) continue;
                index[bundlePrefabName] = new BlueprintStubRef { StubName = stubName, CacheKey = cacheKey };
            }
            return index;
        }

        // ─────────────────────────────────────────────────────────
        //  Template construction
        // ─────────────────────────────────────────────────────────

        // Builds a bundle template's root: ZNetView, Piece, WearNTear and the FAP markers, with no
        // mesh yet. The root is registered with VABakeTemplateRegistry before the first
        // AddComponent so its own ZNetView.Awake is bypassed, and stays active so placed clones
        // run Awake normally and claim their ZDOs. It is parked far off-world so its Piece and
        // colliders never answer position queries.
        //
        // WearNTear's m_noSupportWear and m_noRoofWear read backwards: true means the piece does
        // take that wear and computes support, which drives the hover tint. The baked root is an
        // invulnerable decoration, so both are false.
        private static GameObject BuildTemplateRoot(string prefabName, Manifest manifest)
        {
            try
            {
                // Park the template under Core's permanently-inactive holder BEFORE anything adds a
                // ZNetView. AddComponent on an ACTIVE GameObject runs Awake immediately, and
                // ZNetView.Awake registers the object into ZNetScene.m_instances with a prefab hash
                // that does not resolve yet — every later CreateDestroyObjects tick then NREs on it,
                // once per frame, for the rest of the session. Same reason the live children below
                // must be instantiated into this root and not into an active one.
                //
                // The holder is inactive, not the template: activeInHierarchy is false so no Awake
                // fires, while the template's own activeSelf stays TRUE — and Instantiate copies
                // activeSelf, so placed copies still come out active. SetActive(false) on the root
                // itself would spawn every placed bundle inert and invisible.
                var root = new GameObject(prefabName);
                root.transform.SetParent(NetworkObjectHelper.PrefabHolder, worldPositionStays: false);
                VABakeTemplateRegistry.RegisterTemplate(root);
                root.transform.position = TemplateParkPosition;

                var netView = root.AddComponent<ZNetView>();
                netView.m_persistent = true;
                netView.m_type = ZDO.ObjectType.Default;
                netView.m_distant = true;

                var piece = root.AddComponent<Piece>();
                piece.m_name = StripBlueprintExtension(manifest.sourceBlueprintFile);
                piece.m_description = DescribeBake(manifest.partCount, manifest.totalTris, manifest.sourceBlueprintFile);
                piece.m_enabled = true;
                piece.m_clipGround = true;
                piece.m_clipEverything = true;
                piece.m_allowRotatedOverlap = true;
                piece.m_canBeRemoved = true;
                piece.m_resources = Array.Empty<Piece.Requirement>();
                piece.m_category = VerdantsAscent.Patches.VACategoryRegistry.GetOrAdd(
                    VerdantsAscent.Patches.VACategoryRegistry.CatBakedMeshes);

                var wearNTear = root.AddComponent<WearNTear>();
                wearNTear.m_health = -1f;
                wearNTear.m_noSupportWear = false;
                wearNTear.m_noRoofWear = false;
                wearNTear.m_supports = false;

                root.AddComponent<VABakeBatchMarker>();
                root.AddComponent<VABakeParentLoader>();
                return root;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.BuildTemplateRoot failed for '{prefabName}': {ex.Message}");
                return null;
            }
        }

        // Builds the zone and LOD hierarchy for a bake under root:
        //
        //   root
        //   └── ZoneBucket_X_Y_Z                VABakeColliderToggle, LODGroup when >1 level
        //       ├── BakedPart_lod0_0            MeshFilter, MeshRenderer, MeshCollider
        //       ├── ShadowProxy_lod0_0          the lod1 mesh, ShadowsOnly, sits in LOD level 0
        //       └── BakedPart_lod1_0
        //
        // Only LOD0 parts that aren't rugs get a collider. groupId is stamped on each part's
        // VABakeBatchMarker when building onto a placement rather than a template.
        public static void BuildZoneBucketsForTemplate(
            GameObject root,
            List<VAFapBlueprintBakeCache.CachedPart> parts,
            string prefabName,
            string groupId = null)
        {
            var partsByZone = GroupPartsByZoneAndLod(parts);
            PrecookCollisionMeshes(partsByZone, prefabName);

            foreach (var zoneEntry in partsByZone)
            {
                var zoneKey = zoneEntry.Key;
                var bucket = CreateStaticChild(root.transform, $"ZoneBucket_{zoneKey.X}_{zoneKey.Y}_{zoneKey.Z}");
                bucket.AddComponent<VABakeColliderToggle>();

                var lodLevels = new List<int>(zoneEntry.Value.Keys);
                lodLevels.Sort();
                var renderersByLevel = new List<Renderer>[lodLevels.Count];

                for (int levelIndex = 0; levelIndex < lodLevels.Count; levelIndex++)
                {
                    int lodLevel = lodLevels[levelIndex];
                    var levelParts = zoneEntry.Value[lodLevel];
                    var levelRenderers = new List<Renderer>(levelParts.Count);
                    for (int partIndex = 0; partIndex < levelParts.Count; partIndex++)
                        levelRenderers.Add(CreatePartObject(bucket.transform, levelParts[partIndex], lodLevel, levelIndex, lodLevels.Count, partIndex, zoneKey, prefabName, groupId));
                    renderersByLevel[levelIndex] = levelRenderers;
                }

                for (int levelIndex = 0; levelIndex + 1 < lodLevels.Count; levelIndex++)
                {
                    if (!VABakeLodPolicy.LevelNeedsShadowProxy(levelIndex, lodLevels.Count)) continue;
                    var shadowSourceParts = zoneEntry.Value[lodLevels[levelIndex + 1]];
                    for (int partIndex = 0; partIndex < shadowSourceParts.Count; partIndex++)
                        renderersByLevel[levelIndex].Add(CreateShadowProxyObject(bucket.transform, shadowSourceParts[partIndex], levelIndex, partIndex));
                }

                if (lodLevels.Count > 1) AttachCrossFadingLodGroup(bucket, renderersByLevel, zoneKey, prefabName);
            }
        }

        // "3 buckets, lod0 36,062,932 tris / lod1 3,873,104 tris" — the line that says whether a
        // bake actually has a cheap level to fall back to.
        private static string DescribeLodSplit(List<VAFapBlueprintBakeCache.CachedPart> parts)
        {
            var trisByLodLevel = new SortedDictionary<int, int>();
            var zoneKeys = new HashSet<VABakeZoneKey>();
            foreach (var part in parts)
            {
                if (part == null) continue;
                zoneKeys.Add(part.ZoneKey);
                trisByLodLevel.TryGetValue(part.LodLevel, out int levelTris);
                trisByLodLevel[part.LodLevel] = levelTris + part.TriCount;
            }

            var levels = new List<string>(trisByLodLevel.Count);
            foreach (var levelEntry in trisByLodLevel)
                levels.Add($"lod{levelEntry.Key} {levelEntry.Value:N0} tris");
            return $"{zoneKeys.Count} buckets, {string.Join(" / ", levels.ToArray())}";
        }

        private static PartsByZoneAndLod GroupPartsByZoneAndLod(List<VAFapBlueprintBakeCache.CachedPart> parts)
        {
            var partsByZone = new PartsByZoneAndLod();
            foreach (var part in parts)
            {
                if (part == null || part.CombinedMesh == null || part.Materials == null || part.Materials.Length == 0) continue;
                if (!partsByZone.TryGetValue(part.ZoneKey, out var partsByLod))
                {
                    partsByLod = new Dictionary<int, List<VAFapBlueprintBakeCache.CachedPart>>();
                    partsByZone[part.ZoneKey] = partsByLod;
                }
                if (!partsByLod.TryGetValue(part.LodLevel, out var levelParts))
                {
                    levelParts = new List<VAFapBlueprintBakeCache.CachedPart>();
                    partsByLod[part.LodLevel] = levelParts;
                }
                levelParts.Add(part);
            }
            return partsByZone;
        }

        // Cooks every collider mesh's BVH in parallel before the colliders are attached, so each
        // MeshCollider assignment reuses a cooked BVH instead of cooking serially on the main
        // thread. Blocks until all cooks finish; a mesh that fails to cook here is cooked by its
        // assignment instead.
        private static void PrecookCollisionMeshes(PartsByZoneAndLod partsByZone, string prefabName)
        {
            var collisionMeshIds = new List<int>();
            foreach (var partsByLod in partsByZone.Values)
            {
                if (!partsByLod.TryGetValue(0, out var lod0Parts)) continue;
                foreach (var part in lod0Parts)
                    if (!part.ExcludeFromCollision) collisionMeshIds.Add(part.CombinedMesh.GetInstanceID());
            }
            if (collisionMeshIds.Count == 0) return;

            var cookStopwatch = Stopwatch.StartNew();
            try
            {
                Parallel.ForEach(collisionMeshIds, meshId =>
                {
                    try { Physics.BakeMesh(meshId, convex: false, cookingOptions: ColliderCookOptions); }
                    catch (Exception ex)
                    {
                        VAFapThreadLog.Warning($"{VaPieces.PluginName}: Physics.BakeMesh failed on mesh ID {meshId} for '{prefabName}': {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: Parallel BakeMesh dispatch failed for '{prefabName}': {ex.Message}. Colliders will cook as they attach.");
            }
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}:     parallel-BakeMesh prewarmed {collisionMeshIds.Count} collision mesh(es) for '{prefabName}' in {cookStopwatch.Elapsed.TotalSeconds:F2}s.");
        }

        private static GameObject CreateStaticChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, worldPositionStays: false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            child.isStatic = true;
            return child;
        }

        private static Renderer CreatePartObject(
            Transform bucket,
            VAFapBlueprintBakeCache.CachedPart part,
            int lodLevel,
            int levelIndex,
            int levelCount,
            int partIndex,
            VABakeZoneKey zoneKey,
            string prefabName,
            string groupId)
        {
            var partObject = CreateStaticChild(bucket, $"BakedPart_lod{lodLevel}_{partIndex}");
            partObject.layer = part.SourceLayer;
            partObject.AddComponent<MeshFilter>().sharedMesh = part.CombinedMesh;
            var meshRenderer = partObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = part.Materials;
            meshRenderer.shadowCastingMode = VABakeLodPolicy.ShadowCastingModeForLevel(levelIndex, levelCount);

            if (lodLevel == 0 && !part.ExcludeFromCollision)
                AttachMeshCollider(partObject, part, zoneKey, partIndex, prefabName);

            var batchMarker = partObject.AddComponent<VABakeBatchMarker>();
            if (!string.IsNullOrEmpty(groupId)) batchMarker.GroupId = groupId;
            return meshRenderer;
        }

        // Shadow stand-in for one LOD level, drawn from the next level's mesh. It shares that
        // mesh and its materials, so it costs no memory and only ever renders in shadow passes.
        private static Renderer CreateShadowProxyObject(
            Transform bucket,
            VAFapBlueprintBakeCache.CachedPart shadowSourcePart,
            int levelIndex,
            int partIndex)
        {
            var proxyObject = CreateStaticChild(bucket, $"ShadowProxy_lod{levelIndex}_{partIndex}");
            proxyObject.layer = shadowSourcePart.SourceLayer;
            proxyObject.AddComponent<MeshFilter>().sharedMesh = shadowSourcePart.CombinedMesh;
            var meshRenderer = proxyObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = shadowSourcePart.Materials;
            meshRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            return meshRenderer;
        }

        private static void AttachMeshCollider(
            GameObject partObject,
            VAFapBlueprintBakeCache.CachedPart part,
            VABakeZoneKey zoneKey,
            int partIndex,
            string prefabName)
        {
            try
            {
                var meshCollider = partObject.AddComponent<MeshCollider>();
                meshCollider.cookingOptions = ColliderCookOptions;
                var cookStopwatch = Stopwatch.StartNew();
                meshCollider.sharedMesh = part.CombinedMesh;
                if (cookStopwatch.ElapsedMilliseconds > SlowColliderCookWarnThresholdMs)
                    Debug.LogWarning($"{VaPieces.PluginName}: [BAKE-SLOW-COLLIDER] '{prefabName}' zone ({zoneKey}) part {partIndex} MeshCollider = {cookStopwatch.ElapsedMilliseconds}ms INLINE cook (parallel pre-cook missed this mesh, tris~{part.TriCount}).");
                meshCollider.convex = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle — MeshCollider attach failed on lod0 part {partIndex} of zone ({zoneKey}) '{prefabName}': {ex.Message}");
            }
        }

        // Levels blend with a timed dithered cross-fade instead of popping; shaders without the
        // LOD_FADE_CROSSFADE keyword fall back to a hard switch. RecalculateBounds is what puts
        // the group's reference point at the middle of the bucket, and the size it measures is
        // then replaced by the one VABakeLodPolicy solved the switch distance for.
        private static void AttachCrossFadingLodGroup(
            GameObject bucket, List<Renderer>[] renderersByLevel, VABakeZoneKey zoneKey, string prefabName)
        {
            try
            {
                var transitionHeights = VABakeLodPolicy.TransitionHeights(renderersByLevel.Length);
                var lods = new LOD[renderersByLevel.Length];
                for (int levelIndex = 0; levelIndex < lods.Length; levelIndex++)
                {
                    lods[levelIndex] = new LOD(transitionHeights[levelIndex], renderersByLevel[levelIndex].ToArray())
                    {
                        fadeTransitionWidth = VABakeLodPolicy.LodFadeTransitionWidth,
                    };
                }

                var lodGroup = bucket.AddComponent<LODGroup>();
                lodGroup.SetLODs(lods);
                lodGroup.RecalculateBounds();
                lodGroup.size = VABakeLodPolicy.LodGroupSize;
                lodGroup.fadeMode = LODFadeMode.CrossFade;
                lodGroup.animateCrossFading = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle — LODGroup wire-up failed on zone ({zoneKey}) of '{prefabName}': {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Live children
        // ─────────────────────────────────────────────────────────

        // Instantiates each live child under a runtime bake parent. The root is registered in
        // VABakeTemplateRegistry, so the patched ZNetView.Awake skips these template-side
        // children; components that need a ZDO early-return here and set up on the placement
        // clone, whose children get real ZDOs.
        public static void BuildLiveChildrenForTemplate(
            GameObject root,
            List<VAFapBlueprintBakeCache.LiveChildEntry> liveChildren,
            string prefabName)
        {
            int spawned = 0, missing = 0;
            var scene = ZNetScene.instance;
            if (scene != null && liveChildren != null)
            {
                foreach (var liveChild in liveChildren)
                {
                    if (liveChild == null || string.IsNullOrEmpty(liveChild.PrefabName)) continue;
                    var sourcePrefab = scene.GetPrefab(liveChild.PrefabName);
                    if (sourcePrefab == null) { missing++; continue; }
                    if (TryAttachTemplateLiveChild(root, sourcePrefab, liveChild, prefabName)) spawned++;
                }
            }

            if (missing > 0)
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle — '{prefabName}' has {missing} live child(ren) with unresolved source prefabs (mod missing?). Bundle will be placed with those pieces absent.");
            if (spawned > 0 && VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: VABakeBundle — '{prefabName}' attached {spawned} live child(ren) to template (sync).");

            VALiveChildStrip.PruneRootWearNTearRenderers(root, prefabName);
        }

        // A non-unit scale is flagged for sync so the placed clone's ZNetView writes it into its
        // new ZDO; otherwise the child snaps back to unit scale on reload and on remote clients.
        private static bool TryAttachTemplateLiveChild(
            GameObject root,
            GameObject sourcePrefab,
            VAFapBlueprintBakeCache.LiveChildEntry liveChild,
            string prefabName)
        {
            try
            {
                var child = UnityEngine.Object.Instantiate(sourcePrefab, root.transform, worldPositionStays: false);
                child.transform.localPosition = liveChild.BpLocalPos;
                child.transform.localRotation = liveChild.BpLocalRot;
                var childScale = liveChild.BpLocalScale != Vector3.zero ? liveChild.BpLocalScale : Vector3.one;
                child.transform.localScale = childScale;
                if (childScale != Vector3.one)
                {
                    var childNetView = child.GetComponent<ZNetView>();
                    if (childNetView != null) childNetView.m_syncInitialScale = true;
                }

                VALiveChildStrip.Apply(child);
                child.AddComponent<VALiveChildMarker>().Data = liveChild.Data ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle — live child Instantiate failed for '{liveChild.PrefabName}' on '{prefabName}': {ex.Message}");
                return false;
            }
        }

        private enum PlacedLiveChildResult
        {
            Spawned,
            SkippedByChance,
            PrefabMissing,
            Failed,
        }

        // Spawns a placed bundle's live children as top-level world objects, the same shape
        // InfinityHammer produces: each has its own ZNetView and ZDO, carries the bundle's group
        // id so hammering the root removes the group, rolls its Chance, and gets its blueprint
        // Data written into the ZDO before Awake. Spread across frames because a megabase can
        // have thousands of children.
        public static IEnumerator SpawnLiveChildrenForPlacedBundleCoroutine(
            GameObject placedBundle,
            VAFapBlueprintBakeCache.CachedBake cached,
            string prefabName)
        {
            if (placedBundle == null || cached?.LiveChildren == null || cached.LiveChildren.Count == 0)
                yield break;

            var scene = ZNetScene.instance;
            if (scene == null) yield break;

            string groupId = VABlueprintGroupRegistry.NewGroupId();
            var bundleNetView = placedBundle.GetComponent<ZNetView>();
            if (bundleNetView != null && bundleNetView.IsValid())
            {
                bundleNetView.GetZDO()?.Set(VABlueprintGroupRegistry.ZdoKey, groupId);
                VABlueprintGroupRegistry.Register(groupId, bundleNetView);
            }
            VABlueprintGroupRegistry.AttachBakeParent(groupId, placedBundle);

            int total = cached.LiveChildren.Count;
            int spawned = 0, skipped = 0, missing = 0;
            for (int i = 0; i < total; i++)
            {
                switch (SpawnPlacedLiveChild(scene, placedBundle.transform, cached.LiveChildren[i], groupId, prefabName))
                {
                    case PlacedLiveChildResult.Spawned: spawned++; break;
                    case PlacedLiveChildResult.SkippedByChance: skipped++; break;
                    case PlacedLiveChildResult.PrefabMissing: missing++; break;
                }

                bool batchFinished = (i + 1) % LiveChildrenPerFrame == 0;
                if (batchFinished && i + 1 < total) yield return null;
            }

            Debug.Log($"{VaPieces.PluginName}: VABakeBundle.SpawnLiveChildrenForPlacedBundle — placed '{prefabName}' bundle: spawned {spawned} live child(ren), skipped {skipped} on chance roll, {missing} prefab(s) missing. GroupId='{groupId}'.");
        }

        // The EWD data bridge is only a fallback, for Data this mod's own parser can't read.
        private static PlacedLiveChildResult SpawnPlacedLiveChild(
            ZNetScene scene,
            Transform bundleTransform,
            VAFapBlueprintBakeCache.LiveChildEntry liveChild,
            string groupId,
            string prefabName)
        {
            if (liveChild == null || string.IsNullOrEmpty(liveChild.PrefabName)) return PlacedLiveChildResult.Failed;
            if (liveChild.Chance < 1f && UnityEngine.Random.value > liveChild.Chance) return PlacedLiveChildResult.SkippedByChance;

            var sourcePrefab = scene.GetPrefab(liveChild.PrefabName);
            if (sourcePrefab == null) return PlacedLiveChildResult.PrefabMissing;

            try
            {
                Vector3 worldPosition = bundleTransform.TransformPoint(liveChild.BpLocalPos);
                Quaternion worldRotation = bundleTransform.rotation * liveChild.BpLocalRot;

                VAZdoDataBlob dataBlob = null;
                if (!string.IsNullOrEmpty(liveChild.Data))
                    VAZdoDataBlob.TryParse(liveChild.Data, out dataBlob);

                var child = VAZdoFirstSpawner.Instantiate(
                    sourcePrefab, worldPosition, worldRotation, dataBlob, liveChild.ExtraInfo,
                    zdo => zdo.Set(VABlueprintGroupRegistry.ZdoKey, groupId),
                    out bool dataApplied);
                if (child == null) return PlacedLiveChildResult.PrefabMissing;

                if (liveChild.BpLocalScale != Vector3.zero)
                    VABlueprintHammerIntegration.ApplyPersistentScale(child, liveChild.BpLocalScale);
                VALiveChildStrip.Apply(child);

                var netView = child.GetComponent<ZNetView>();
                if (netView != null && netView.IsValid())
                {
                    VABlueprintGroupRegistry.Register(groupId, netView);
                    var zdo = netView.GetZDO();
                    if (!dataApplied && !string.IsNullOrEmpty(liveChild.Data) && zdo != null)
                        Patches.VABundleDataApplier.TryApply(liveChild.Data, liveChild.PrefabName, zdo);
                }
                return PlacedLiveChildResult.Spawned;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeBundle.SpawnLiveChildrenForPlacedBundle — Instantiate failed for '{liveChild.PrefabName}' on placed '{prefabName}': {ex.Message}");
                return PlacedLiveChildResult.Failed;
            }
        }
    }

    // Carries a template live child's blueprint Data through Instantiate and applies it once the
    // clone has a ZDO. Start runs after every sibling Awake, including the ZNetView that creates
    // the ZDO; on the template itself ZNetView.Awake is bypassed, the ZDO is null, and Start
    // waits for a clone.
    public sealed class VALiveChildMarker : MonoBehaviour
    {
        private const string CloneSuffix = "(Clone)";

        public string Data;

        private bool _applied;

        private void Start()
        {
            if (_applied) return;
            if (string.IsNullOrEmpty(Data)) { _applied = true; return; }

            try
            {
                var netView = GetComponent<ZNetView>();
                if (netView == null) { _applied = true; return; }
                var zdo = netView.GetZDO();
                if (zdo == null) return;

                string prefabName = gameObject.name;
                if (prefabName.EndsWith(CloneSuffix, StringComparison.Ordinal))
                    prefabName = prefabName.Substring(0, prefabName.Length - CloneSuffix.Length).TrimEnd();

                _applied = Patches.VABundleDataApplier.TryApply(Data, prefabName, zdo);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VALiveChildMarker.Start data-apply threw on '{name}': {ex.Message}");
                _applied = true;
            }
        }
    }

    // Per-piece component strip for live-child clones, plus a renderer-cache prune on the
    // bundle root once its children have attached.
    //
    // At megabase scale (7339 children in one zone) two stock components drop the framerate to
    // single digits: WearNTear, whose updater raycasts for sun exposure and walks renderer
    // bounds per instance, and ZSyncTransform, which dirty-checks and sends every frame for
    // objects that never move. Both are removed; interactable state lives in the ZDO. Removing
    // WearNTear also makes live children invulnerable, which is intended — the bundle root keeps
    // its own for whole-bundle removal.
    //
    // Rigidbodies are locked rather than destroyed, since some interactions still query them
    // (door hinges), and Sign's recurring UpdateText poll is cancelled after Awake has filled the
    // text. LODGroup stays: its distance check is nearly free and cheaper meshes at range more
    // than pay for it.
    //
    // The root's WearNTear caches renderers from a descendant scan, which on a placement clone
    // includes every live child. PruneRootWearNTearRenderers clears that cache. If the field is
    // renamed the prune logs and does nothing; the bundle still works, only slower.
    internal static class VALiveChildStrip
    {
        private const string SignUpdateTextMethod = "UpdateText";
        private const string WearNTearRenderersField = "m_renderers";

        private static System.Reflection.FieldInfo _wearNTearRenderersField;
        private static bool _wearNTearRenderersFieldResolved;

        public static void Apply(GameObject child)
        {
            if (child == null) return;
            try
            {
                DestroyIfPresent<WearNTear>(child);
                DestroyIfPresent<ZSyncTransform>(child);

                foreach (var rigidbody in child.GetComponentsInChildren<Rigidbody>(includeInactive: true))
                {
                    rigidbody.isKinematic = true;
                    rigidbody.useGravity = false;
                }

                foreach (var sign in child.GetComponentsInChildren<Sign>(includeInactive: true))
                    sign.CancelInvoke(SignUpdateTextMethod);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VALiveChildStrip.Apply failed on '{child.name}': {ex.Message}");
            }
        }

        private static void DestroyIfPresent<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            if (component != null) UnityEngine.Object.DestroyImmediate(component);
        }

        public static void PruneRootWearNTearRenderers(GameObject root, string prefabName)
        {
            if (root == null) return;
            try
            {
                var wearNTear = root.GetComponent<WearNTear>();
                if (wearNTear == null) return;

                if (!_wearNTearRenderersFieldResolved)
                {
                    _wearNTearRenderersField = AccessTools.Field(typeof(WearNTear), WearNTearRenderersField);
                    _wearNTearRenderersFieldResolved = true;
                    if (_wearNTearRenderersField == null)
                        Debug.LogWarning($"{VaPieces.PluginName}: VALiveChildStrip — could not locate WearNTear.m_renderers field via reflection. Root WearNTear renderer cache will iterate all descendant renderers per tick (perf cost on megabase bundles).");
                }
                if (_wearNTearRenderersField == null) return;

                _wearNTearRenderersField.SetValue(wearNTear, Activator.CreateInstance(_wearNTearRenderersField.FieldType));

                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"{VaPieces.PluginName}: VALiveChildStrip — pruned root WearNTear m_renderers on '{prefabName}'. Per-tick sun-exposure iteration now bounded to zero descendant renderers (was: all live-child renderers).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VALiveChildStrip.PruneRootWearNTearRenderers failed on '{prefabName}': {ex.Message}");
            }
        }
    }
}
