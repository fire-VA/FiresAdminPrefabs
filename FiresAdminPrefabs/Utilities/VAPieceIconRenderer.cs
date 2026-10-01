using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;
using FiresCore.Lifecycle;
using VerdantsAscent.Managers;

using FiresCore.Net;

namespace VerdantsAscent.Utilities
{
    // Generates the m_icon Sprite for every Piece FAP injects: instantiate the prefab
    // off-screen, frame an isometric camera on its renderer bounds, render one frame
    // into a RenderTexture, read it back as a Texture2D, wrap it as a Sprite, destroy
    // the clone.
    //
    // The clone is made with ZNetView.m_forceDisableInit so it never creates a ZDO —
    // only the mesh matters here, and this keeps the bandwidth cost at zero. Pieces
    // that already ship an authored m_icon are skipped, and the queue renders one icon
    // per frame so kicking off hundreds at world load doesn't hitch.
    public class VAPieceIconRenderer : MonoBehaviour
    {
        private static VAPieceIconRenderer _instance;
        public static VAPieceIconRenderer Instance => _instance;

        private const int IconSize = 128;

        // Spawn-finalization slack: frames to idle after a readiness gate
        // opens before doing heavy work, so we don't contend with the
        // spawn-finalization frame.
        private const int SpawnSlackFrames = 30;

        // Timeout passed to PlayerSpawnGate.RunWhenLocalReady; generous so the
        // world load + zone stream can finish on heavy modsets without dropping icons.
        private const float GateTimeoutSeconds = 600f;

        // Placeholder Texture2D dimensions before LoadImage resizes to the PNG's
        // real size.
        private const int PlaceholderTextureSize = 2;

        // Sprite.Create shared params: centered pivot + pixels-per-unit.
        private static readonly Vector2 SpritePivotCenter = new Vector2(0.5f, 0.5f);
        private const float SpritePixelsPerUnit = 100f;

        // Flat ambient-lighting overrides applied (and restored) around each icon
        // capture so hammer thumbnails share one consistent, bright look.
        private static readonly Color IconAmbientLight = new Color(0.95f, 0.95f, 1.0f, 1f);
        private const float IconAmbientIntensity = 1.6f;
        private static readonly Color IconAmbientSky = new Color(1.0f, 1.0f, 1.0f, 1f);
        private static readonly Color IconAmbientEquator = new Color(0.9f, 0.9f, 0.95f, 1f);
        private static readonly Color IconAmbientGround = new Color(0.6f, 0.6f, 0.65f, 1f);

        // Below this squared-extent the render bounds are treated as degenerate
        // and replaced with a unit cube so framing has something to fit.
        private const float DegenerateBoundsSqrThreshold = 0.001f;

        // Icon camera framing: distance as a multiple of bounds size, the
        // three-quarter view direction, and near/far clip as multiples of distance.
        private const float CameraDistanceFactor = 1.2f;
        private static readonly Vector3 CameraViewDirection = new Vector3(0.4f, 0.3f, -0.8f);
        private const float NearClipDistanceFactor = 0.01f;
        private const float FarClipDistanceFactor = 10f;

        // Icon camera setup: transparent dark background, FOV, default clip planes, depth.
        private static readonly Color IconCameraBackground = new Color(0.04f, 0.04f, 0.06f, 0f);
        private const float IconCameraFieldOfView = 35f;
        private const float IconCameraNearClip = 0.01f;
        private const float IconCameraFarClip = 500f;
        private const int IconCameraDepth = -20;

        // Icon RenderTexture depth-buffer bits + MSAA sample count.
        private const int IconRenderTextureDepthBits = 16;
        private const int IconRenderTextureAntiAliasing = 2;

        // Rendered icons persist as 128x128 PNGs under VAFapCachePaths.IconCacheDir, one
        // per sanitized prefab name, and the icon flush checks there first — so only a
        // genuinely new prefab pays for Camera.Render. There's no manifest: each PNG is its
        // own evidence, and a stale one just costs disk space. fap_reload_icons clears and
        // re-renders. Per-client and never synced, since icons are purely local.
        public static string IconCacheDir => VAFapCachePaths.IconCacheDir;

        // Counters for the per-session summary log. Reset on first
        // Enqueue of the session (lazy).
        private static int _cacheHits;
        private static int _cacheMisses;
        private static int _cacheSaves;
        private static int _cacheSaveFailures;

        // Reflection handles for the Texture2D image-conversion extension
        // methods. These live in UnityEngine.ImageConversionModule.dll
        // which targets netstandard 2.1 — incompatible with this csproj's
        // netstandard 2.0. Loading the module at runtime via reflection
        // sidesteps the compile-time reference mismatch. The Unity
        // runtime itself ships ImageConversionModule, so the methods
        // resolve at first use.
        private static System.Reflection.MethodInfo _miEncodeToPNG;
        private static System.Reflection.MethodInfo _miLoadImage;

        private static System.Reflection.MethodInfo EncodeToPNG_Method
        {
            get
            {
                if (_miEncodeToPNG != null) return _miEncodeToPNG;
                try
                {
                    var imageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                    if (imageConversionType == null) return null;
                    _miEncodeToPNG = imageConversionType.GetMethod("EncodeToPNG",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(Texture2D) },
                        modifiers: null);
                }
                catch { _miEncodeToPNG = null; }
                return _miEncodeToPNG;
            }
        }

        private static System.Reflection.MethodInfo LoadImage_Method
        {
            get
            {
                if (_miLoadImage != null) return _miLoadImage;
                try
                {
                    var imageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                    if (imageConversionType == null) return null;
                    _miLoadImage = imageConversionType.GetMethod("LoadImage",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(Texture2D), typeof(byte[]) },
                        modifiers: null);
                }
                catch { _miLoadImage = null; }
                return _miLoadImage;
            }
        }

        // Reflection-wrapped calls. Both return null/false on failure
        // (missing method, runtime exception) so callers can fall
        // through to non-cache paths gracefully.
        private static byte[] EncodeTextureToPNG(Texture2D tex)
        {
            var method = EncodeToPNG_Method;
            if (method == null || tex == null) return null;
            try { return method.Invoke(null, new object[] { tex }) as byte[]; }
            catch { return null; }
        }

        public static bool LoadImageIntoTexture(Texture2D tex, byte[] data)
        {
            var method = LoadImage_Method;
            if (method == null || tex == null || data == null) return false;
            try
            {
                var result = method.Invoke(null, new object[] { tex, data });
                return result is bool ok && ok;
            }
            catch { return false; }
        }

        // Dedicated layer for the icon proxy hierarchy. The icon camera's
        // cullingMask is restricted to this layer so it never picks up
        // world geometry, and the icon directional light's cullingMask is
        // restricted to the same layer so the light NEVER illuminates the
        // player's real world. Without this isolation we couldn't add a
        // light at all — a normal directional light affects every renderer
        // in the scene, which would brighten the game world during every
        // icon render.
        //
        // Layer 31 is conventionally unused in Unity projects; if a sibling
        // mod ever claims it the only consequence is icon-render lighting
        // also affecting their layer-31 renderers — harmless visually.
        private const int IconLayer = 31;

        // Ceiling on proxy objects built for one blueprint thumbnail. Past this
        // the icon gains nothing at 128x128 and the stage build starts costing
        // whole seconds of main thread.
        private const int MaxBlueprintProxyPieces = 1500;

        // Remote stage position. Far enough from any active zone that
        // ambient lighting / fog from the player's current biome doesn't
        // bleed into the icon render. Y-up by 3km keeps us above any
        // terrain even on map seeds with extreme mountain heights.
        private static readonly Vector3 StagePosition = new Vector3(-8000f, 3000f, -8000f);

        private Camera _iconCamera;
        private RenderTexture _renderTexture;
        private readonly Queue<Piece> _queue = new Queue<Piece>();
        private readonly HashSet<int> _enqueued = new HashSet<int>();
        private bool _processing;

        // Accumulates each drain wave's outcome buckets so per-wave detail folds
        // into one debounced 🖼 ICONS DONE banner after the waves settle.
        private int _aggRendered, _aggParticleOnly, _aggEmpty, _aggOtherNull, _aggThrown, _aggWaves;
        private float _aggFirstStart = -1f;
        private Coroutine _drainBannerDebounce;
        private const float DrainBannerDebounceSeconds = 4f;

        // PNG encoding and the disk write are decoupled from rendering. Doing both inline
        // cost 5-50ms of EncodeToPNG plus the write on every render frame, and with 2700+
        // icons queued on a first launch the loading screen looked frozen. RenderIcon now
        // assigns the sprite immediately and queues a Texture2D COPY, which ProcessSaveQueue
        // drains under a per-frame millisecond budget. It has to be a copy: the sprite's own
        // texture can be collected or reused before the encode runs.
        //
        // Each queued copy holds a 128x128 RGBA32 texture, so a queue that cannot keep up
        // costs memory as well as lost saves.
        private struct PendingSave
        {
            public string prefabName;
            public Texture2D tex;
        }
        private readonly Queue<PendingSave> _saveQueue = new Queue<PendingSave>();

        // Saving is budgeted in milliseconds, not counted in frames. Three bare yields between saves was
        // one PNG every four frames, and an icon drain is exactly what drags the frame rate down: at the
        // 9-12 fps a 4,502-icon reload ran at, that came to ~3 saves/s against ~9 renders/s, so 1,716
        // icons reached disk and the other 2,786 were re-rendered next session while their texture copies
        // sat in this queue. A budget cannot make a frame worse than the one encode it already allowed --
        // it is checked before each save, never mid-encode -- but it lets throughput follow the work.
        private const float SaveQueueFrameBudgetMs = 3f;
        private bool _saveProcessing;

        // Dedup set for the "blueprint produced zero renderers" warning.
        // The same blueprint name (e.g. "LocationProxy") can appear
        // dozens of times in the queue if multiple .blueprint files
        // parse to the same internal Name — without dedup we used to
        // log hundreds of identical warnings. Static so it persists
        // across coroutine pauses + a re-renderer Awake (it survives a
        // GameObject re-creation, which the queue does not).
        private static readonly HashSet<string> _zeroRendererBlueprintWarned =
            new HashSet<string>(StringComparer.Ordinal);

        // Counter for the 🖼 ICONS summary banner. Increments alongside
        // _zeroRendererBlueprintWarned.Add() so a blueprint that's
        // retried multiple times still only counts once. Read by
        // VABlueprintHammerIntegration when it emits the load summary.
        public static int ZeroRendererBlueprintCount { get; private set; }

        // Why we use a render-proxy instead of Object.Instantiate(prefab):
        // Setting MonoBehaviour.enabled=false on the source does NOT prevent
        // the clone's Awake from firing — `enabled` only gates Update /
        // OnEnable / OnDisable. Awake runs unconditionally on Instantiate.
        // Dangerous Awakes (MineRock5 building runtime mesh, etc.) still
        // throw even with source pre-disabled, spamming Unity's log.
        //
        // RenderIcon builds a render-proxy instead — a fresh GameObject
        // hierarchy that copies only transform + visual components
        // (MeshFilter / MeshRenderer / SkinnedMeshRenderer). No
        // MonoBehaviours, no Awakes, no crashes.

        public static VAPieceIconRenderer EnsureInstance()
        {
            if (_instance != null) return _instance;
            var go = new GameObject("VA_PieceIconRenderer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<VAPieceIconRenderer>();
            return _instance;
        }

        private void Awake()
        {
            _instance = this;
            CreateCamera();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            ReleaseResources();
        }

        // ──────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────

        // Enqueues arriving before the local player has fully spawned are buffered here and
        // flushed by PlayerSpawnGate once the player is ready for custom data writes. During
        // load, Enqueue is then just a buffer push. Previously it decoded each cached PNG on
        // the spot — 1-2ms apiece across 2700+ icons, 3-5s of loading-screen stall — and the
        // render coroutine started immediately too.
        private static readonly List<Piece> _pendingEnqueue = new List<Piece>(4096);
        private static bool _gateRegistered;

        // True once the spawn gate has fired. The original flush was a ONE-SHOT:
        // pieces the prefab scan registers AFTER the player spawns (the common
        // case on a heavy world, where spawn beats the scan) landed in
        // _pendingEnqueue with nothing left to flush them, so their cached icon
        // never loaded — the whole "icons sometimes populate, sometimes don't"
        // inconsistency. Once _playerReady, late enqueues re-kick the flush.
        private static bool _playerReady;
        private static bool _flushRunning;

        public static void Enqueue(Piece piece)
        {
            if (piece == null) return;
            if (VaPieces.IsHeadless) return;
            if (piece.m_icon != null) return;

            // BUFFER ONLY — no I/O, no decode, no allocation beyond
            // the List.Add. Anything heavier waits for the gate.
            _pendingEnqueue.Add(piece);

            // Register the gate callback ONCE. PlayerSpawnGate.RunWhenLocalReady
            // fires on the first Update tick where the local player satisfies the
            // readiness predicate; the generous timeout tolerates heavy modsets.
            if (!_gateRegistered)
            {
                _gateRegistered = true;
                EnsureInstance();
                PlayerSpawnGate.RunWhenLocalReady(GateTimeoutSeconds, OnLocalPlayerReady);
            }

            // Spawn gate already fired → the one-shot flush is gone. Re-kick a
            // paced flush so this late enqueue actually loads. Deduped via
            // _flushRunning, so a scan registering hundreds of pieces post-spawn
            // starts ONE flush, not one per piece.
            else if (_playerReady)
            {
                KickFlush();
            }
        }

        // Fired by PlayerSpawnGate.RunWhenLocalReady once the local
        // player has fully spawned in. Starts the flush coroutine
        // which paces the cache-lookup + render-queue-push work
        // across frames.
        private static void OnLocalPlayerReady(Player p)
        {
            Debug.Log($"{VaPieces.PluginName}: VAPieceIconRenderer — local player ready ('{p.GetPlayerName()}'); starting flush of {_pendingEnqueue.Count} pending icon enqueue(s).");
            _playerReady = true;
            KickFlush();
        }

        // Starts the paced flush unless one is already running. _flushRunning is
        // set SYNCHRONOUSLY here (not inside the coroutine, which doesn't run
        // until next frame) so two kicks in the same frame can't both start it.
        private static void KickFlush()
        {
            if (_flushRunning) return;
            var instance = EnsureInstance();
            if (instance == null) return;
            _flushRunning = true;
            instance.StartCoroutine(instance.FlushPendingPaced());
        }

        // Flushes _pendingEnqueue at a paced rate. Cache hits decode a PNG into a sprite; cache misses
        // push into the active render queue. The PNG reads run on a worker one batch ahead of the
        // decode, so a slow or contended disk never lands on the main thread (one read took 92 ms
        // there while FAT's voxel cache was loading); only the decode is paced by the frame budget.
        private IEnumerator FlushPendingPaced()
        {
            // Extra half-second slack — the spawn-finalization frame
            // shouldn't be competing with our first batch of work.
            for (int i = 0; i < SpawnSlackFrames; i++) yield return null;

            const float FlushBudgetMs = 6f;
            int hits = 0, misses = 0;
            float frameStartMs = Time.realtimeSinceStartup * 1000f;

            // Drain FULLY. The prefab scan calls Enqueue across many frames (and
            // often keeps registering pieces after the player has spawned), so
            // process a snapshot, remove it, and loop while the buffer keeps
            // refilling. Items that arrive mid-drain are picked up on the next
            // pass rather than stranded.
            while (_pendingEnqueue.Count > 0)
            {
                int n = _pendingEnqueue.Count;
                var batch = StartCachedIconReads(0, Math.Min(n, CachedIconReadBatchSize));
                for (int batchStart = 0; batchStart < n; batchStart += CachedIconReadBatchSize)
                {
                    if (!batch.Reading.IsCompleted)
                    {
                        do yield return null; while (!batch.Reading.IsCompleted);
                        frameStartMs = Time.realtimeSinceStartup * 1000f;
                    }
                    if (batch.Reading.IsFaulted)
                        Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer — cached icon reads stopped early " +
                                         $"({batch.Reading.Exception?.GetBaseException().Message}); the unread icons render instead.");

                    var reads = batch.Reads;
                    int nextStart = batchStart + CachedIconReadBatchSize;
                    if (nextStart < n) batch = StartCachedIconReads(nextStart, Math.Min(n, nextStart + CachedIconReadBatchSize));

                    foreach (var read in reads)
                    {
                        var piece = read.Piece;
                        if (piece == null || piece.m_icon != null) continue;

                        if (ApplyCachedIcon(read))
                        {
                            _cacheHits++;
                            hits++;
                        }
                        else
                        {
                            _cacheMisses++;
                            misses++;
                            int instanceId = piece.GetInstanceID();
                            if (_enqueued.Add(instanceId))
                            {
                                _queue.Enqueue(piece);
                                if (!_processing) StartCoroutine(ProcessQueue());
                            }
                        }

                        if (Time.realtimeSinceStartup * 1000f - frameStartMs >= FlushBudgetMs)
                        {
                            yield return null;
                            frameStartMs = Time.realtimeSinceStartup * 1000f;
                        }
                    }
                }
                // Only the snapshot we processed — late enqueues (index >= n)
                // survive and the while-loop picks them up next pass.
                _pendingEnqueue.RemoveRange(0, n);
            }

            _flushRunning = false;
            // A piece enqueued in the tiny window between the loop's final
            // Count check and here would wait forever otherwise — re-kick.
            if (_pendingEnqueue.Count > 0) KickFlush();

            Debug.Log($"{VaPieces.PluginName}: VAPieceIconRenderer — flush complete: {hits} cache hit(s), {misses} cache miss(es) queued for render.");
        }

        // ──────────────────────────────────────────────────────────────
        //  Icon cache — load
        // ──────────────────────────────────────────────────────────────

        private const int CachedIconReadBatchSize = 128;

        // One cached-icon lookup. The piece, its name and the PNG path are taken on the main thread;
        // the worker only fills Png (or Error) from the path.
        private sealed class CachedIconRead
        {
            public Piece Piece;
            public string Name;
            public string Path;
            public byte[] Png;
            public string Error;
        }

        private struct CachedIconReadBatch
        {
            public List<CachedIconRead> Reads;
            public Task Reading;
        }

        private static string CachedIconPath(string prefabName) =>
            Path.Combine(IconCacheDir, VAFapFileNames.ToSafeFileName(prefabName) + ".png");

        private static CachedIconReadBatch StartCachedIconReads(int start, int end)
        {
            var reads = new List<CachedIconRead>(end - start);
            for (int i = start; i < end; i++)
            {
                var piece = _pendingEnqueue[i];
                if (piece == null || piece.m_icon != null) continue;
                string name = piece.name;
                reads.Add(new CachedIconRead
                {
                    Piece = piece,
                    Name = name,
                    Path = string.IsNullOrEmpty(name) ? null : CachedIconPath(name),
                });
            }
            return new CachedIconReadBatch { Reads = reads, Reading = Task.Run(() => ReadCachedIconFiles(reads)) };
        }

        private static void ReadCachedIconFiles(List<CachedIconRead> reads)
        {
            foreach (var read in reads)
            {
                if (read.Path == null) continue;
                try
                {
                    if (File.Exists(read.Path)) read.Png = File.ReadAllBytes(read.Path);
                }
                catch (Exception ex)
                {
                    read.Error = ex.Message;
                }
            }
        }

        // Decodes a PNG the worker read and assigns it as the piece's icon. False on a missing file, an
        // I/O error or a failed decode, and the caller falls through to the render path.
        private static bool ApplyCachedIcon(CachedIconRead read)
        {
            if (read.Error != null)
                Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer — cached icon read failed for '{read.Name}': {read.Error}");
            if (read.Png == null || read.Png.Length == 0) return false;

            try
            {
                var tex = new Texture2D(PlaceholderTextureSize, PlaceholderTextureSize, TextureFormat.RGBA32, mipChain: false);
                if (!LoadImageIntoTexture(tex, read.Png))
                {
                    UnityEngine.Object.Destroy(tex);
                    return false;
                }
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = $"FAP_CachedIcon_{read.Name}";

                var sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    SpritePivotCenter,
                    SpritePixelsPerUnit);
                sprite.name = read.Name + "_icon";
                read.Piece.m_icon = sprite;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer — cached icon decode failed for '{read.Name}': {ex.Message}");
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Icon cache — save
        // ──────────────────────────────────────────────────────────────

        // Persists a rendered Texture2D as a PNG so the next session
        // can load it in FlushPendingPaced. Called from RenderIcon
        // immediately after a successful render. Failures are
        // non-fatal — the in-memory icon still gets assigned to
        // piece.m_icon either way.
        private static void SaveIconToCache(string prefabName, Texture2D tex)
        {
            if (string.IsNullOrEmpty(prefabName) || tex == null) return;
            if (VaPieces.IsHeadless) return;

            try
            {
                if (!Directory.Exists(IconCacheDir)) Directory.CreateDirectory(IconCacheDir);
                byte[] png = EncodeTextureToPNG(tex);
                if (png == null || png.Length == 0)
                {
                    _cacheSaveFailures++;
                    return;
                }
                string path = CachedIconPath(prefabName);
                File.WriteAllBytes(path, png);
                _cacheSaves++;
            }
            catch (Exception ex)
            {
                _cacheSaveFailures++;
                if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.SaveIconToCache failed for '{prefabName}': {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Icon cache — clear + reload-all
        // ──────────────────────────────────────────────────────────────

        // Deletes every PNG in IconCacheDir. Returns the count of
        // files removed. Console command fap_reload_icons wraps this
        // together with a re-enqueue of every eligible Piece.
        public static int ClearCache()
        {
            if (!Directory.Exists(IconCacheDir)) return 0;
            int count = 0;
            try
            {
                foreach (var file in Directory.GetFiles(IconCacheDir, "*.png"))
                {
                    try { File.Delete(file); count++; }
                    catch { /* skip locked files; not fatal */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.ClearCache enumerate failed: {ex.Message}");
            }
            return count;
        }

        // Drops the cached PNG for a SINGLE prefab so its icon re-renders next
        // time. The disk cache is keyed by prefab name, so a re-bake that changes
        // the mesh but keeps the same name (piece_Phalanx) would otherwise keep
        // the stale icon — VABakeBundle.Unpublish calls this on re-bake to close
        // that gap. Returns true if a file was removed.
        public static bool InvalidateIcon(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            try
            {
                string path = CachedIconPath(prefabName);
                if (File.Exists(path)) { File.Delete(path); return true; }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.InvalidateIcon failed for '{prefabName}': {ex.Message}");
            }
            return false;
        }

        // Console-command target. Clears the cache, nulls out m_icon on every
        // FAP-managed Piece, and re-queues each for fresh render. Bypasses
        // Enqueue's gate-one-shot — runs after the gate already fired in-world,
        // so it pushes straight into _queue + kicks ProcessQueue instead.
        public static (int filesDeleted, int piecesReQueued) ReloadAll()
        {
            int deleted = ClearCache();

            var instance = EnsureInstance();
            if (instance == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.ReloadAll — no renderer instance, cannot reload.");
                return (deleted, 0);
            }

            int requeued = 0;

            // 1) Main scan loop pieces (FAP-injected vanilla prefabs).
            foreach (var entry in VAVanillaPieceManager.EligiblePieces)
            {
                if (instance.PushForReRenderInternal(entry.Value)) requeued++;
            }

            // 2) Clutter stand-ins (one per ClutterSystem entry,
            //    FAP-owned GameObjects).
            foreach (var entry in VAClutterStandinFactory.AllStandins)
            {
                if (instance.PushForReRenderInternal(entry.Value)) requeued++;
            }

            // 3) Item-stand stand-ins (vertical + horizontal — many
            //    hundreds; this is the bulk of the missing-icon
            //    population in heavy-mod setups).
            foreach (var entry in VAItemStandStandinFactory.AllStandins)
            {
                if (instance.PushForReRenderInternal(entry.Value)) requeued++;
            }

            // 4) FX clear stand-ins (one per FX category — four total).
            foreach (var entry in VAFapEwpClearTool.AllStandins)
            {
                if (instance.PushForReRenderInternal(entry.Value)) requeued++;
            }

            // 5) Kick the queue. ProcessQueue is a coroutine on the
            //    renderer's MonoBehaviour. Without this kick, the
            //    enqueued pieces would just stack forever — the
            //    process coroutine only auto-fires from Enqueue's
            //    pending-list flush (PlayerSpawnGate one-shot) which
            //    already fired earlier in the session.
            if (requeued > 0 && !instance._processing)
            {
                instance.StartCoroutine(instance.ProcessQueue());
            }

            // Reset counters so the next session's log reflects fresh
            // cache activity only.
            _cacheHits = 0;
            _cacheMisses = 0;
            _cacheSaves = 0;
            _cacheSaveFailures = 0;

            Debug.Log($"{VaPieces.PluginName}: VAPieceIconRenderer.ReloadAll — deleted {deleted} cached PNG(s), pushed {requeued} piece(s) into the render queue.");
            return (deleted, requeued);
        }

        // Instance helper: nulls m_icon + pushes the piece's id+ref
        // into the (instance) render queue. Bypasses _pendingEnqueue
        // so we don't depend on PlayerSpawnGate's one-shot flush —
        // ReloadAll runs after the gate already fired.
        //
        // Returns true if the piece was queued (false if null or
        // already in the active session's queue).
        private bool PushForReRenderInternal(GameObject prefab)
        {
            if (prefab == null) return false;
            var piece = prefab.GetComponent<Piece>();
            if (piece == null) return false;
            piece.m_icon = null;
            int instanceId = piece.GetInstanceID();
            if (!_enqueued.Add(instanceId))
            {
                // Already in the active queue (e.g. user ran reload
                // twice in a row). Don't double-enqueue.
                return false;
            }
            _queue.Enqueue(piece);
            return true;
        }

        // Diagnostic — drained by the per-session summary log + by the
        // fap_reload_icons console command after re-queue.
        public static (int hits, int misses, int saves, int saveFailures) GetCacheStats()
            => (_cacheHits, _cacheMisses, _cacheSaves, _cacheSaveFailures);

        // ── Deferred blueprint icons ───────────────────────────────────
        // Blueprint stub icons are NOT touched at world-load — most sessions
        // never open the Blueprints tab, so rendering (or even cache-loading)
        // every blueprint up front is wasted work. Stubs land here at load and
        // are flushed into the normal cache-first Enqueue path the first time
        // the Blueprints tab is shown (FlushDeferredBlueprints, driven by a
        // BuildUi.OnSelectTag postfix). Warm profiles then just load PNGs;
        // cold profiles render paced. Keeps our disk cache + layer-isolated
        // proxy renderer — only the TRIGGER goes lazy.
        private static readonly List<Piece> _deferredBlueprints = new List<Piece>(512);

        // Called at world-load per blueprint stub. Zero-cost buffer push — no
        // I/O, no enqueue, no render until the tab is opened.
        public static void RegisterDeferredBlueprint(Piece piece)
        {
            if (piece == null) return;
            if (VaPieces.IsHeadless) return;
            _deferredBlueprints.Add(piece);
        }

        // Moves every buffered blueprint stub into the cache-first Enqueue path
        // and clears the buffer. Returns how many were flushed (0 when nothing
        // is pending, so it's cheap to call on every Blueprints-tab refresh).
        public static int FlushDeferredBlueprints()
        {
            if (VaPieces.IsHeadless) return 0;
            int n = _deferredBlueprints.Count;
            if (n == 0) return 0;

            // Snapshot + clear FIRST so a re-entrant Enqueue can't double-add.
            var batch = _deferredBlueprints.ToArray();
            _deferredBlueprints.Clear();
            for (int i = 0; i < batch.Length; i++)
            {
                var piece = batch[i];
                if (piece != null) Enqueue(piece);
            }
            Debug.Log($"{VaPieces.PluginName}: VAPieceIconRenderer — Blueprints tab opened; flushed {n} deferred blueprint icon(s) into the cache-first render path.");
            return n;
        }

        // ──────────────────────────────────────────────────────────────
        //  Queue processing
        // ──────────────────────────────────────────────────────────────

        // Gate predicate — true when it's safe to do heavy main-thread work
        // (Camera.Render, ReadPixels, EncodeToPNG, WriteAllBytes). Requires a
        // live, non-teleporting local player plus a streamed-in active area, so
        // the loading screen finishes before icon renders start competing for frames.
        private static bool IsSafeToRender()
        {
            if (VaPieces.IsHeadless) return false;

            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return false;
            try { if (!localPlayer) return false; } catch { return false; }
            try { if (localPlayer.IsTeleporting()) return false; } catch { return false; }

            if (ZNetScene.instance == null) return false;
            if (ZoneSystem.instance == null) return false;
            try { if (!ZoneSystem.instance.IsActiveAreaLoaded()) return false; } catch { return false; }

            return true;
        }

        // Renders the queued pieces under three behaviors: a spawn gate
        // (IsSafeToRender + slack) before any work, a per-frame time budget
        // capped at MaxPerFrame renders, and disk-save decoupling (renders push
        // texture copies onto _saveQueue, drained separately by ProcessSaveQueue).
        private IEnumerator ProcessQueue()
        {
            _processing = true;

            // Granular failure-mode buckets. Each item we attempt to
            // render lands in exactly one of these (or `totalRendered`
            // on success). startQueueDepth is no longer the denominator
            // — it's just a snapshot at coroutine start, but FlushPendingPaced
            // pushes items into the queue WHILE we're draining, so the
            // total attempts can vastly exceed the initial depth. We
            // report `totalAttempted = totalRendered + null buckets +
            // threw` instead so the denominator reflects the actual work.
            int startQueueDepth = _queue.Count;
            int totalRendered = 0;
            int totalParticleOnly = 0;  // has particles, no mesh — intentional skip (particle path disabled)
            int totalEmpty = 0;         // no mesh AND no particles — prefab has nothing renderable
            int totalOtherNull = 0;     // had mesh content but RenderIcon still returned null (unexpected)
            int totalThrown = 0;
            float startTime = Time.realtimeSinceStartup;
            int lastProgressLogAtRendered = 0;

            // 1. Wait for safe-to-render.
            int waitFrames = 0;
            while (!IsSafeToRender())
            {
                yield return null;
                waitFrames++;
            }
            if (waitFrames > 0)
            {
                Debug.Log($"{VaPieces.PluginName}: VAPieceIconRenderer waited {waitFrames} frame(s) for safe-to-render gate before starting queue drain (queue depth: {_queue.Count}).");
            }
            // Half-second slack so the spawn-finalization frame isn't
            // contending with our first heavy render.
            for (int i = 0; i < SpawnSlackFrames; i++) yield return null;

            const float FrameBudgetMs = 6f;
            const int MaxPerFrame = 4;
            const int ProgressLogInterval = 500;

            while (_queue.Count > 0)
            {
                // Mid-flight teleport / scene change? Pause until the
                // player is safe to write to again. Prevents renders
                // racing with player-spawn finalization on teleport.
                if (!IsSafeToRender())
                {
                    yield return null;
                    continue;
                }

                float frameStartMs = Time.realtimeSinceStartup * 1000f;
                int rendersThisFrame = 0;

                while (_queue.Count > 0
                    && rendersThisFrame < MaxPerFrame
                    && (Time.realtimeSinceStartup * 1000f - frameStartMs) < FrameBudgetMs)
                {
                    var piece = _queue.Dequeue();
                    if (piece == null || piece.m_icon != null) continue;

                    // Pre-classify so the null-result bucketing below can tell
                    // intentional no-mesh skips apart from unexpected failures.
                    var pieceObject = piece.gameObject;
                    bool hasMesh = pieceObject != null && HasMeshContent(pieceObject);
                    bool hasParticles = pieceObject != null && HasParticleContent(pieceObject);

                    Sprite sprite = null;
                    bool threw = false;
                    try
                    {
                        string prefabName = pieceObject != null ? pieceObject.name : null;
                        if (!string.IsNullOrEmpty(prefabName) && TryResolveBlueprintForIcon(prefabName, out var bp))
                        {
                            sprite = RenderBlueprintIcon(bp, prefabName);
                        }
                        else
                        {
                            sprite = RenderIcon(pieceObject);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: Icon render threw for {piece.name}: {ex.Message}");
                        totalThrown++;
                        threw = true;
                    }

                    if (sprite != null)
                    {
                        piece.m_icon = sprite;
                        totalRendered++;
                    }
                    else if (!threw)
                    {
                        // Bucket the null by likely cause.
                        if (!hasMesh)
                        {
                            if (hasParticles) totalParticleOnly++;
                            else totalEmpty++;
                        }
                        else totalOtherNull++;
                    }
                    rendersThisFrame++;
                }

                // Progress log every ProgressLogInterval successful renders so the
                // user can see the queue is actually draining (was
                // guessing at queue stalls before this landed).
                if (totalRendered - lastProgressLogAtRendered >= ProgressLogInterval)
                {
                    lastProgressLogAtRendered = totalRendered;
                    int attempted = totalRendered + totalParticleOnly + totalEmpty + totalOtherNull + totalThrown;
                    Debug.Log($"{VaPieces.PluginName}: VAPieceIconRenderer progress — rendered={totalRendered}, particle-only={totalParticleOnly}, empty={totalEmpty}, other-null={totalOtherNull}, threw={totalThrown}, attempted={attempted}, queue-left={_queue.Count}, elapsed={Time.realtimeSinceStartup - startTime:F1}s.");
                }

                yield return null;
            }

            // Final summary. Total attempted is the SUM of every
            // bucket — that's the true denominator the user wants to
            // see, not the original startQueueDepth (which can be
            // tiny because FlushPendingPaced kicks ProcessQueue on
            // the first miss when only 1 item is in the queue).
            int totalAttempted = totalRendered + totalParticleOnly + totalEmpty + totalOtherNull + totalThrown;

            // Verbose keeps the per-wave breakdown; the default path folds
            // every wave into the one debounced 🖼 ICONS DONE banner.
            if (VAFapLoadSummary.VerboseEnabled)
            {
                Debug.Log(
                    $"{VaPieces.PluginName}: VAPieceIconRenderer queue drain complete — " +
                    $"rendered={totalRendered}/{totalAttempted} in {Time.realtimeSinceStartup - startTime:F1}s. " +
                    $"INTENTIONAL SKIPS: particle-only={totalParticleOnly} (fx_/vfx_/sfx_ — fall back to vanilla shield), " +
                    $"empty={totalEmpty} (no mesh, no particles — nothing to render). " +
                    $"UNEXPECTED: other-null={totalOtherNull} (had mesh but render still failed — investigate if non-zero), " +
                    $"threw={totalThrown}. " +
                    $"(startQueueDepth at coroutine launch was {startQueueDepth}; the queue grew while draining.)");
            }

            // Accumulate this wave's buckets + (re)arm the debounced banner.
            if (_aggFirstStart < 0f) _aggFirstStart = startTime;
            _aggRendered += totalRendered;
            _aggParticleOnly += totalParticleOnly;
            _aggEmpty += totalEmpty;
            _aggOtherNull += totalOtherNull;
            _aggThrown += totalThrown;
            _aggWaves++;
            RequestDrainBanner();

            ReleasePieceTemplates();
            _processing = false;
        }

        // Cancel-and-restart debounce: only the final wave's timer survives, so
        // the banner fires once DrainBannerDebounceSeconds pass with no new wave.
        private void RequestDrainBanner()
        {
            if (_drainBannerDebounce != null) StopCoroutine(_drainBannerDebounce);
            _drainBannerDebounce = StartCoroutine(DrainBannerDebounce());
        }

        private IEnumerator DrainBannerDebounce()
        {
            float elapsed = 0f;
            while (elapsed < DrainBannerDebounceSeconds) { elapsed += Time.deltaTime; yield return null; }
            _drainBannerDebounce = null;
            EmitAggregatedDrainBanner();
        }

        private void EmitAggregatedDrainBanner()
        {
            if (_aggWaves == 0) return;
            int attempted = _aggRendered + _aggParticleOnly + _aggEmpty + _aggOtherNull + _aggThrown;
            int noMesh = _aggParticleOnly + _aggEmpty;
            int unexpected = _aggOtherNull + _aggThrown;
            float seconds = _aggFirstStart >= 0f ? Time.realtimeSinceStartup - _aggFirstStart : 0f;
            try
            {
                VAFapLoadSummary.EmitIconDrain(_aggRendered, attempted, noMesh, unexpected, _aggWaves, seconds,
                                               _cacheSaves, _cacheSaveFailures, _saveQueue.Count);
            }
            catch { }

            _aggRendered = _aggParticleOnly = _aggEmpty = _aggOtherNull = _aggThrown = _aggWaves = 0;
            _aggFirstStart = -1f;
        }

        // Drains _saveQueue. Each iteration encodes one Texture2D to PNG
        // and writes it to disk, then yields multiple frames before the
        // next save so the disk + encode work doesn't compound on top of
        // the active render loop.
        //
        // Gated on IsSafeToRender so we never write the icon cache while
        // the player is still in a loading/teleport state — file I/O can
        // contend with the engine's own disk activity (vault save, world
        // save, character profile save) and we want a clean window for
        // those.
        private IEnumerator ProcessSaveQueue()
        {
            _saveProcessing = true;

            while (!IsSafeToRender()) yield return null;
            for (int i = 0; i < SpawnSlackFrames; i++) yield return null;

            while (_saveQueue.Count > 0)
            {
                if (!IsSafeToRender())
                {
                    yield return null;
                    continue;
                }

                float frameStartMs = Time.realtimeSinceStartup * 1000f;
                while (_saveQueue.Count > 0
                       && (Time.realtimeSinceStartup * 1000f - frameStartMs) < SaveQueueFrameBudgetMs)
                {
                    SavePendingIcon(_saveQueue.Dequeue());
                }

                yield return null;
            }

            _saveProcessing = false;
        }

        private void SavePendingIcon(PendingSave pending)
        {
            if (pending.tex == null || string.IsNullOrEmpty(pending.prefabName)) return;
            try
            {
                if (!Directory.Exists(IconCacheDir)) Directory.CreateDirectory(IconCacheDir);
                byte[] png = EncodeTextureToPNG(pending.tex);
                if (png != null && png.Length > 0)
                {
                    string path = CachedIconPath(pending.prefabName);
                    File.WriteAllBytes(path, png);
                    _cacheSaves++;
                }
                else
                {
                    _cacheSaveFailures++;
                }
            }
            catch (Exception ex)
            {
                _cacheSaveFailures++;
                if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.ProcessSaveQueue failed for '{pending.prefabName}': {ex.Message}");
            }
            // Release the save-copy texture now that we've encoded it.
            try { UnityEngine.Object.Destroy(pending.tex); } catch { }
        }

        // Pushes (prefabName, copy-of-texture) onto _saveQueue. Called
        // from RenderIcon instead of the old inline SaveIconToCache. The
        // texture is COPIED so the original sprite's Texture2D can be
        // freely mutated / disposed by the rest of the pipeline (or by
        // Unity's GC) without invalidating our pending save.
        private void QueueIconSave(string prefabName, Texture2D src)
        {
            if (string.IsNullOrEmpty(prefabName) || src == null) return;
            if (VaPieces.IsHeadless) return;

            Texture2D copy = null;
            try
            {
                copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, mipChain: false);
                // Buffer-to-buffer; the old form round-tripped the whole icon through
                // a 16-bytes-a-pixel managed Color[] just to clone it.
                copy.SetPixelData(src.GetPixelData<Color32>(0), 0);
                copy.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                copy.name = "FAP_SaveCopy_" + prefabName;
            }
            catch (Exception ex)
            {
                if (copy != null) { try { UnityEngine.Object.Destroy(copy); } catch { } }
                _cacheSaveFailures++;
                if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.QueueIconSave — copy failed for '{prefabName}': {ex.Message}");
                return;
            }

            _saveQueue.Enqueue(new PendingSave { prefabName = prefabName, tex = copy });
            if (!_saveProcessing) StartCoroutine(ProcessSaveQueue());
        }

        // ──────────────────────────────────────────────────────────────
        //  Icon rendering
        // ──────────────────────────────────────────────────────────────

        private Sprite RenderIcon(GameObject prefab)
        {
            if (prefab == null || _iconCamera == null) return null;
            EnsureRenderTexture();

            // Picks a render strategy from prefab content: mesh content → cheap
            // no-Awake proxy (BuildRenderProxy); else particles → neutralized
            // safe-instance (BuildSafeParticleInstance); else nothing to render
            // and we bail so vanilla's shield fallback shows.
            GameObject renderTarget = null;
            try
            {
                if (HasMeshContent(prefab))
                {
                    renderTarget = BuildRenderProxy(prefab);
                }
                else if (HasParticleContent(prefab))
                {
                    renderTarget = BuildSafeParticleInstance(prefab);
                }
                // Nothing visual at all. If it makes noise, say so with a dedicated
                // icon rather than leaving vanilla's shield placeholder: a silent
                // prefab and a sound emitter look identical in the menu otherwise,
                // and "did the render fail?" is the wrong question to be asking of
                // something that was never going to draw anything.
                if (renderTarget == null)
                {
                    if (!HasAudioContent(prefab)) return null;

                    var soundTex = BuildSoundOnlyIcon(prefab.name);
                    if (soundTex == null) return null;
                    var soundSprite = Sprite.Create(soundTex, new Rect(0, 0, IconSize, IconSize),
                        SpritePivotCenter, SpritePixelsPerUnit);
                    soundSprite.name = prefab.name + "_icon_soundonly";
                    QueueIconSave(prefab.name, soundTex);
                    return soundSprite;
                }

                var renderers = renderTarget.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return null;

                // Particle icons must include ParticleSystemRenderer bounds (the
                // particles ARE the content); mesh icons exclude them so emission
                // volumes don't inflate framing past the silhouette.
                bool isParticleIcon = !HasMeshContent(prefab);
                Bounds bounds = ComputeRenderBounds(renderers, includeParticles: isParticleIcon);
                if (bounds.size.sqrMagnitude < DegenerateBoundsSqrThreshold) bounds.size = Vector3.one;

                FrameCameraOnBounds(bounds);

                _iconCamera.targetTexture = _renderTexture;

                // Mesh icons capture one static frame; particle icons capture a
                // multi-frame max-blend composite (brightest value per pixel across
                // the lifecycle). Ambient overrides are restored in finally so a
                // throw never leaks them to the main camera.
                var prevMode = RenderSettings.ambientMode;
                var prevLight = RenderSettings.ambientLight;
                var prevIntensity = RenderSettings.ambientIntensity;
                var prevSky = RenderSettings.ambientSkyColor;
                var prevEquator = RenderSettings.ambientEquatorColor;
                var prevGround = RenderSettings.ambientGroundColor;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = IconAmbientLight;
                RenderSettings.ambientIntensity = IconAmbientIntensity;
                RenderSettings.ambientSkyColor = IconAmbientSky;
                RenderSettings.ambientEquatorColor = IconAmbientEquator;
                RenderSettings.ambientGroundColor = IconAmbientGround;

                Texture2D tex;
                try
                {
                    tex = isParticleIcon
                        ? RenderParticleComposite(renderTarget, prefab.name)
                        : RenderSingleFrame(prefab.name);
                }
                finally
                {
                    RenderSettings.ambientMode = prevMode;
                    RenderSettings.ambientLight = prevLight;
                    RenderSettings.ambientIntensity = prevIntensity;
                    RenderSettings.ambientSkyColor = prevSky;
                    RenderSettings.ambientEquatorColor = prevEquator;
                    RenderSettings.ambientGroundColor = prevGround;
                }

                if (tex == null) return null;

                // Particle icons post-process: substitute a procedural fallback if
                // the capture is essentially empty, else crop-and-upscale if sparse.
                // Mesh icons skip both — their bounds-based framing fills well.
                if (isParticleIcon)
                {
                    var maybeFallback = SubstituteFallbackIfEmpty(tex, prefab.name);
                    if (maybeFallback != tex)
                    {
                        tex = maybeFallback;
                    }
                    else
                    {
                        tex = ZoomIfSparse(tex, prefab.name);
                    }
                }

                var sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, IconSize, IconSize),
                    SpritePivotCenter,
                    SpritePixelsPerUnit);
                sprite.name = prefab.name + "_icon";

                // Persist to the icon cache so the next session hits
                // FlushPendingPaced. Routed through QueueIconSave so the
                // EncodeToPNG + WriteAllBytes work is deferred off this coroutine.
                QueueIconSave(prefab.name, tex);

                return sprite;
            }
            finally
            {
                // SafeDestroyImmediate (not deferred Destroy): the frame-budget
                // loop renders up to MaxPerFrame proxies per frame, so each must be
                // torn down synchronously or the icon camera captures the stack.
                // SafeDestroyImmediate also routes through ZNetScene cleanup for any
                // ZNetView that slipped past the strip.
                if (renderTarget != null) NetworkObjectHelper.SafeDestroyImmediate(renderTarget);
            }
        }

        // Single Camera.Render() into the RT, then ReadPixels into a new
        // Texture2D. Used for mesh-content icons where there's no animation
        // and one frame is sufficient.
        private Texture2D RenderSingleFrame(string prefabName)
        {
            _iconCamera.Render();
            var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
            {
                name = prefabName + "_icon_tex",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            RenderTexture.active = _renderTexture;
            tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            return tex;
        }

        // Multi-frame composite for particle prefabs: renders CompositeFrameCount
        // snapshots at evenly-spaced simulation times and max-blends them into one
        // Texture2D, so the icon captures the effect's full motion rather than a
        // single (possibly empty) instant.
        private const int CompositeFrameCount = 4;

        // Particle simulation timing: default duration for systems whose own
        // duration can't be read, per-system cap, and a floor so ultra-short
        // bursts still emit on the first frame.
        private const float DefaultParticleDurationSeconds = 1.5f;
        private const float MaxParticleDurationSeconds = 5f;
        private const float MinParticleSimTimeSeconds = 0.05f;

        private Texture2D RenderParticleComposite(GameObject instance, string prefabName)
        {
            var particles = instance.GetComponentsInChildren<ParticleSystem>(includeInactive: true);

            // Precompute each system's effective duration (max of emission
            // duration and particle lifetime, per-system so mixed-duration
            // effects each sample their OWN lifecycle) once outside the frame loop.
            int psCount = particles.Length;
            float[] systemDurations = new float[psCount];
            for (int i = 0; i < psCount; i++)
            {
                var ps = particles[i];
                if (ps == null) { systemDurations[i] = DefaultParticleDurationSeconds; continue; }
                float duration = DefaultParticleDurationSeconds;
                try
                {
                    var main = ps.main;
                    if (main.duration > 0f) duration = main.duration;
                    try
                    {
                        float life = main.startLifetime.constantMax;
                        if (life > 0f && life > duration) duration = life;
                    }
                    catch { /* gradient/curve startLifetime — skip */ }
                }
                catch { /* main accessor edge config — skip */ }
                systemDurations[i] = Mathf.Min(duration, MaxParticleDurationSeconds);
            }

            // Color32 (4 bytes) rather than Color (16): this buffer is max-blended
            // against a GetPixelData view once per composite frame, so the old Color
            // form cost a 16-bytes-a-pixel read back on every one of them.
            var accumulator = new Color32[IconSize * IconSize];

            // Reusable read-back scratch texture. Allocating once outside
            // the loop saves N Texture2D allocations + GC.
            var scratch = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
            try
            {
                for (int frame = 0; frame < CompositeFrameCount; frame++)
                {
                    // (frame+1)/N puts the last sample at the system's effective
                    // end so the final fade frame is caught.
                    float fraction = (frame + 1f) / CompositeFrameCount;

                    // restart:true resets each system to t=0 then fast-forwards to
                    // simTime, so every frame is the same effect at a different
                    // lifecycle point rather than the cumulative state.
                    for (int i = 0; i < psCount; i++)
                    {
                        var ps = particles[i];
                        if (ps == null) continue;
                        float simTime = Mathf.Max(MinParticleSimTimeSeconds, systemDurations[i] * fraction);
                        // withChildren:false: we already Simulate every nested PS
                        // ourselves, so advancing children from the parent call too
                        // would double-fire sub-emitters and emission counters.
                        try { ps.Simulate(simTime, withChildren: false, restart: true); }
                        catch { /* unsimulable PS — skip */ }
                    }

                    _iconCamera.Render();
                    RenderTexture.active = _renderTexture;
                    // No Apply(): ReadPixels fills the CPU-side buffer, which is what
                    // GetPixelData reads below. Apply pushes CPU->GPU, and nothing ever
                    // samples this scratch texture — it is destroyed in the finally. It
                    // was four pointless uploads per particle icon.
                    scratch.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                    RenderTexture.active = null;

                    var framePixels = scratch.GetPixelData<Color32>(0);
                    for (int i = 0; i < accumulator.Length; i++)
                    {
                        var accumulated = accumulator[i];
                        var framePixel = framePixels[i];
                        if (framePixel.r > accumulated.r) accumulated.r = framePixel.r;
                        if (framePixel.g > accumulated.g) accumulated.g = framePixel.g;
                        if (framePixel.b > accumulated.b) accumulated.b = framePixel.b;
                        if (framePixel.a > accumulated.a) accumulated.a = framePixel.a;
                        accumulator[i] = accumulated;
                    }
                }
            }
            finally
            {
                Destroy(scratch);
            }

            var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
            {
                name = prefabName + "_icon_tex",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(accumulator);
            tex.Apply();
            return tex;
        }

        // ──────────────────────────────────────────────────────────────
        //  Sparse-icon zoom
        // ──────────────────────────────────────────────────────────────

        // If the icon's opaque content (alpha > AlphaThreshold) covers less than
        // SparseAreaThreshold of the area, crops to the content bbox plus padding
        // and upscales back to IconSize. Returns the original texture when it's
        // already well-framed or has no visible content.
        private const float SparseAreaThreshold = 0.30f;   // <30% coverage → zoom
        private const float AlphaThreshold = 0.10f;        // pixel "visible" gate
        private const byte AlphaThreshold255 = 25;         // same gate on Color32 (0.10f * 255)
        private const float ZoomPaddingFraction = 0.15f;   // extra padding around bbox
        private const float MaxZoomFactor = 6f;            // don't zoom past this multiplier

        private Texture2D ZoomIfSparse(Texture2D src, string prefabName)
        {
            if (src == null) return src;
            // GetPixelData is a view over the texture's own buffer — no managed copy.
            // GetPixels() handed back a Color[] at 16 bytes a pixel purely to read one
            // alpha channel, and on a cold icon cache that was the client's largest
            // single allocation on the join path.
            var pixels = src.GetPixelData<Color32>(0);
            int w = src.width;
            int h = src.height;

            // First pass: find bbox of opaque pixels + coverage count.
            int minX = w, minY = h, maxX = -1, maxY = -1;
            int opaqueCount = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a > AlphaThreshold255)
                    {
                        opaqueCount++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            // No visible content at all → return as-is (vanilla shield
            // fallback will show, which is more informative than an
            // empty zoom).
            if (opaqueCount == 0 || maxX < 0) return src;

            float coverage = (float)opaqueCount / (w * h);
            if (coverage >= SparseAreaThreshold) return src;  // already well-framed

            // Build a SQUARE bbox (so the zoom doesn't stretch the icon
            // in one axis). Anchor on the content's center to keep
            // particles in their visual position.
            int bboxW = maxX - minX + 1;
            int bboxH = maxY - minY + 1;
            int cx = (minX + maxX) / 2;
            int cy = (minY + maxY) / 2;
            int side = Mathf.Max(bboxW, bboxH);

            // Add padding so particles don't sit flush against the icon edge.
            side = Mathf.RoundToInt(side * (1f + ZoomPaddingFraction * 2f));

            // Enforce the max zoom cap — if the content is so sparse that
            // we'd zoom past MaxZoomFactor, clamp to it. Above this
            // multiplier the upscaled icon is too blurry to be useful.
            int minSide = Mathf.CeilToInt(w / MaxZoomFactor);
            if (side < minSide) side = minSide;

            // Center the crop square on (cx, cy) and clamp to texture bounds.
            int half = side / 2;
            int x0 = Mathf.Clamp(cx - half, 0, w - 1);
            int y0 = Mathf.Clamp(cy - half, 0, h - 1);
            int x1 = Mathf.Clamp(x0 + side - 1, 0, w - 1);
            int y1 = Mathf.Clamp(y0 + side - 1, 0, h - 1);
            // Re-derive the actual cropped extent in case clamping shrank it.
            int cropW = x1 - x0 + 1;
            int cropH = y1 - y0 + 1;

            // Bilinearly sample the crop region into a new IconSize texture in C#
            // (no extra RenderTexture / readback latency; cheap at this size).
            var dst = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
            {
                name = prefabName + "_icon_tex_zoomed",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var dstPixels = new Color[IconSize * IconSize];
            for (int dy = 0; dy < IconSize; dy++)
            {
                float v = (dy + 0.5f) / IconSize;
                float srcY = y0 + v * cropH - 0.5f;
                int sy0 = Mathf.Clamp(Mathf.FloorToInt(srcY), 0, h - 1);
                int sy1 = Mathf.Clamp(sy0 + 1, 0, h - 1);
                float ty = Mathf.Clamp01(srcY - sy0);

                for (int dx = 0; dx < IconSize; dx++)
                {
                    float u = (dx + 0.5f) / IconSize;
                    float srcX = x0 + u * cropW - 0.5f;
                    int sx0 = Mathf.Clamp(Mathf.FloorToInt(srcX), 0, w - 1);
                    int sx1 = Mathf.Clamp(sx0 + 1, 0, w - 1);
                    float tx = Mathf.Clamp01(srcX - sx0);

                    // Bilinear sample of the 4 surrounding texels.
                    var c00 = pixels[sy0 * w + sx0];
                    var c10 = pixels[sy0 * w + sx1];
                    var c01 = pixels[sy1 * w + sx0];
                    var c11 = pixels[sy1 * w + sx1];
                    var cx0 = Color.Lerp(c00, c10, tx);
                    var cx1 = Color.Lerp(c01, c11, tx);
                    dstPixels[dy * IconSize + dx] = Color.Lerp(cx0, cx1, ty);
                }
            }
            dst.SetPixels(dstPixels);
            dst.Apply();
            Destroy(src);
            return dst;
        }

        // ──────────────────────────────────────────────────────────────
        //  Fallback sprite for empty captures
        // ──────────────────────────────────────────────────────────────

        // Pixels under this count are considered "essentially empty" —
        // the particle simulation didn't produce anything visible. Below
        // this we ditch the captured texture and substitute a procedural
        // fallback. Tuned higher than ZoomIfSparse's "low coverage" gate
        // because the zoom can rescue a tiny but real capture, whereas
        // this gate is reserved for true blanks.
        private const int EmptyIconPixelThreshold = 40;

        // If the captured texture has fewer than EmptyIconPixelThreshold
        // opaque pixels, returns a fresh fallback texture (caller swaps
        // it in). Otherwise returns the same texture unchanged.
        //
        // The fallback is deterministic per prefab name: stable colors
        // across launches, distinguishable between adjacent FX prefabs,
        // and a center dot that hints "this is a particle effect, not
        // a real icon".
        private Texture2D SubstituteFallbackIfEmpty(Texture2D src, string prefabName)
        {
            if (src == null) return src;
            // Same reasoning as ZoomIfSparse: a view, not a 16-bytes-a-pixel copy.
            var pixels = src.GetPixelData<Color32>(0);
            int opaqueCount = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a > AlphaThreshold255 && ++opaqueCount >= EmptyIconPixelThreshold)
                    return src;  // enough content, keep the capture
            }
            var fallback = BuildFallbackIcon(prefabName);
            Destroy(src);
            return fallback;
        }

        // Generates a deterministic procedural icon for prefabs whose capture came
        // back blank: a muted name-hashed fill + edge band plus one of SymbolCount
        // hash-chosen shapes, so the same prefab always renders the same icon.
        private Texture2D BuildFallbackIcon(string prefabName)
        {
            var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
            {
                name = (prefabName ?? "unknown") + "_icon_fallback",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            int hash = 17;
            if (prefabName != null)
            {
                unchecked
                {
                    foreach (char character in prefabName) hash = hash * 31 + character;
                }
            }
            // Muted palette so a grid full of fallbacks stays readable without
            // fighting the panel for attention.
            float hue = ((hash & 0xFFFF) % 360) / 360f;
            float sat = 0.30f + (((hash >> 16) & 0xFF) % 30) / 200f;  // 0.30-0.45
            float val = 0.40f + (((hash >> 24) & 0xFF) % 30) / 200f;  // 0.40-0.55
            Color fill = Color.HSVToRGB(hue, sat, val);
            Color edge = Color.HSVToRGB(hue, sat * 0.6f, val * 0.4f);
            Color symbol = Color.HSVToRGB(hue, sat * 0.5f, Mathf.Min(0.85f, val + 0.30f));

            var px = new Color[IconSize * IconSize];
            float cx = IconSize * 0.5f;
            float cy = IconSize * 0.5f;
            float outerR = IconSize * 0.42f;
            const int border = 2;

            // Background pass — fill + edge band.
            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    int idx = y * IconSize + x;
                    float dx = x - cx;
                    float dy = y - cy;
                    if (Mathf.Abs(dx) > outerR + border || Mathf.Abs(dy) > outerR + border)
                    {
                        px[idx] = new Color(0, 0, 0, 0);
                    }
                    else if (Mathf.Abs(dx) <= outerR && Mathf.Abs(dy) <= outerR)
                    {
                        if (Mathf.Abs(dx) > outerR - border || Mathf.Abs(dy) > outerR - border)
                            px[idx] = edge;
                        else
                            px[idx] = fill;
                    }
                    else
                    {
                        px[idx] = edge;
                    }
                }
            }

            // Shape index from a SEPARATE part of the hash than color so
            // similar-colored neighbors still get distinct shapes.
            int shapeIndex = (hash >> 8) & 0x7FFFFFFF;
            DrawFallbackSymbol(px, IconSize, cx, cy, symbol, shapeIndex);

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // ──────────────────────────────────────────────────────────────
        //  Procedural symbol shapes for fallback icons
        // ──────────────────────────────────────────────────────────────

        // Number of distinct procedural shapes available. shapeIndex %
        // SymbolCount picks one. Adding more here is cheap — just
        // extend the switch in DrawFallbackSymbol.
        private const int SymbolCount = 80;

        // Dispatch — picks a shape based on (shapeIndex % SymbolCount)
        // and draws it centered on (cx, cy) in `color`. Each branch is
        // a small composition of DrawCircle / DrawLine / DrawArc /
        // DrawTriangle primitives, sized to fit comfortably inside the
        // outerR (~54px) icon area.
        private static void DrawFallbackSymbol(Color[] px, int size, float cx, float cy, Color c, int shapeIndex)
        {
            int shape = ((shapeIndex % SymbolCount) + SymbolCount) % SymbolCount;
            switch (shape)
            {
                // ─── Dots & circles ───
                case 0: DrawCircle(px, size, cx, cy, 16f, c); break;
                case 1: DrawRing(px, size, cx, cy, 18f, 3f, c); break;
                case 2: DrawRing(px, size, cx, cy, 14f, 6f, c); break;
                case 3: DrawCircle(px, size, cx, cy, 8f, c); DrawRing(px, size, cx, cy, 20f, 2f, c); break;
                case 4: DrawCircle(px, size, cx - 9f, cy, 6f, c); DrawCircle(px, size, cx + 9f, cy, 6f, c); break;
                case 5: DrawCircle(px, size, cx, cy - 9f, 6f, c); DrawCircle(px, size, cx, cy + 9f, 6f, c); break;
                case 6: DrawCircle(px, size, cx - 8f, cy - 8f, 5f, c); DrawCircle(px, size, cx + 8f, cy + 8f, 5f, c); break;
                case 7: for (int i = 0; i < 4; i++) DrawCircle(px, size, cx + Mathf.Cos(i * 1.57f) * 14f, cy + Mathf.Sin(i * 1.57f) * 14f, 4f, c); break;

                // ─── Squares & rectangles ───
                case 8: DrawSquare(px, size, cx, cy, 18f, c); break;
                case 9: DrawSquareOutline(px, size, cx, cy, 20f, 3f, c); break;
                case 10: DrawSquare(px, size, cx, cy - 10f, 8f, c); DrawSquare(px, size, cx, cy + 10f, 8f, c); break;
                case 11: DrawSquare(px, size, cx - 10f, cy, 8f, c); DrawSquare(px, size, cx + 10f, cy, 8f, c); break;
                case 12: DrawSquare(px, size, cx - 9f, cy - 9f, 7f, c); DrawSquare(px, size, cx + 9f, cy - 9f, 7f, c); DrawSquare(px, size, cx, cy + 9f, 7f, c); break;
                case 13: DrawSquareOutline(px, size, cx, cy, 22f, 2f, c); DrawSquare(px, size, cx, cy, 8f, c); break;

                // ─── Triangles ───
                case 14: DrawTriangle(px, size, cx, cy + 16f, cx - 16f, cy - 12f, cx + 16f, cy - 12f, c); break;
                case 15: DrawTriangle(px, size, cx, cy - 16f, cx - 16f, cy + 12f, cx + 16f, cy + 12f, c); break;
                case 16: DrawTriangle(px, size, cx - 16f, cy, cx + 12f, cy - 16f, cx + 12f, cy + 16f, c); break;
                case 17: DrawTriangle(px, size, cx + 16f, cy, cx - 12f, cy - 16f, cx - 12f, cy + 16f, c); break;
                case 18: DrawTriangle(px, size, cx, cy + 14f, cx - 12f, cy - 8f, cx + 12f, cy - 8f, c); DrawTriangle(px, size, cx, cy - 14f, cx - 12f, cy + 8f, cx + 12f, cy + 8f, c); break;

                // ─── Diamonds ───
                case 19: DrawDiamond(px, size, cx, cy, 16f, c); break;
                case 20: DrawDiamondOutline(px, size, cx, cy, 18f, 3f, c); break;
                case 21: DrawDiamond(px, size, cx - 9f, cy, 6f, c); DrawDiamond(px, size, cx + 9f, cy, 6f, c); break;

                // ─── Crosses ───
                case 22: DrawLine(px, size, cx, cy - 16f, cx, cy + 16f, c, 5f); DrawLine(px, size, cx - 16f, cy, cx + 16f, cy, c, 5f); break;
                case 23: DrawLine(px, size, cx - 14f, cy - 14f, cx + 14f, cy + 14f, c, 5f); DrawLine(px, size, cx - 14f, cy + 14f, cx + 14f, cy - 14f, c, 5f); break;
                case 24: DrawLine(px, size, cx, cy - 16f, cx, cy + 16f, c, 3f); DrawLine(px, size, cx - 12f, cy + 4f, cx + 12f, cy + 4f, c, 3f); break;  // cross +

                // ─── Stars ───
                case 25: DrawStar(px, size, cx, cy, 18f, 7f, 5, c); break;
                case 26: DrawStar(px, size, cx, cy, 16f, 6f, 6, c); break;
                case 27: DrawStar(px, size, cx, cy, 18f, 8f, 4, c); break;

                // ─── Arrows ───
                case 28: DrawArrow(px, size, cx, cy + 16f, cx, cy - 16f, c); break;  // up
                case 29: DrawArrow(px, size, cx, cy - 16f, cx, cy + 16f, c); break;  // down
                case 30: DrawArrow(px, size, cx - 16f, cy, cx + 16f, cy, c); break;  // right
                case 31: DrawArrow(px, size, cx + 16f, cy, cx - 16f, cy, c); break;  // left
                case 32: DrawArrow(px, size, cx - 12f, cy - 12f, cx + 12f, cy + 12f, c); break;  // diag

                // ─── Lines & bars ───
                case 33: DrawLine(px, size, cx - 16f, cy, cx + 16f, cy, c, 4f); break;
                case 34: DrawLine(px, size, cx, cy - 16f, cx, cy + 16f, c, 4f); break;
                case 35: DrawLine(px, size, cx - 14f, cy - 14f, cx + 14f, cy + 14f, c, 4f); break;
                case 36: DrawLine(px, size, cx - 16f, cy + 6f, cx + 16f, cy + 6f, c, 3f); DrawLine(px, size, cx - 16f, cy - 6f, cx + 16f, cy - 6f, c, 3f); break;  // equal
                case 37: DrawLine(px, size, cx - 16f, cy + 10f, cx + 16f, cy + 10f, c, 2f); DrawLine(px, size, cx - 16f, cy, cx + 16f, cy, c, 2f); DrawLine(px, size, cx - 16f, cy - 10f, cx + 16f, cy - 10f, c, 2f); break;  // 3-bars
                case 38: DrawLine(px, size, cx - 10f, cy - 16f, cx - 10f, cy + 16f, c, 3f); DrawLine(px, size, cx, cy - 16f, cx, cy + 16f, c, 3f); DrawLine(px, size, cx + 10f, cy - 16f, cx + 10f, cy + 16f, c, 3f); break;  // vert 3-bars

                // ─── Arcs & curves ───
                case 39: DrawArc(px, size, cx, cy, 18f, 0f, 180f, c, 4f); break;
                case 40: DrawArc(px, size, cx, cy, 18f, 180f, 360f, c, 4f); break;
                case 41: DrawArc(px, size, cx, cy, 16f, 30f, 330f, c, 4f); break;
                case 42: DrawArc(px, size, cx, cy, 18f, 0f, 90f, c, 4f); DrawArc(px, size, cx, cy, 18f, 180f, 270f, c, 4f); break;
                case 43: DrawRing(px, size, cx, cy, 20f, 2f, c); DrawCircle(px, size, cx, cy - 18f, 4f, c); break;  // clock

                // ─── Hexagons ───
                case 44: DrawPolygon(px, size, cx, cy, 18f, 6, c, true); break;
                case 45: DrawPolygon(px, size, cx, cy, 18f, 6, c, false); break;
                case 46: DrawPolygon(px, size, cx, cy, 18f, 5, c, true); break;
                case 47: DrawPolygon(px, size, cx, cy, 18f, 3, c, false); break;
                case 48: DrawPolygon(px, size, cx, cy, 18f, 8, c, false); break;

                // ─── Hearts / drops / leaves ───
                case 49:
                    DrawCircle(px, size, cx - 6f, cy + 6f, 7f, c);
                    DrawCircle(px, size, cx + 6f, cy + 6f, 7f, c);
                    DrawTriangle(px, size, cx, cy - 14f, cx - 12f, cy + 4f, cx + 12f, cy + 4f, c);
                    break;
                case 50:
                    DrawCircle(px, size, cx, cy + 4f, 10f, c);
                    DrawTriangle(px, size, cx, cy - 14f, cx - 8f, cy + 2f, cx + 8f, cy + 2f, c);
                    break;

                // ─── Compound (multi-shape) symbols ───
                case 51: DrawCircle(px, size, cx, cy + 8f, 8f, c); DrawLine(px, size, cx, cy + 8f, cx, cy - 14f, c, 3f); break;  // balloon
                case 52: DrawRing(px, size, cx - 8f, cy, 6f, 2f, c); DrawRing(px, size, cx + 8f, cy, 6f, 2f, c); break;  // double ring
                case 53: DrawSquareOutline(px, size, cx, cy, 18f, 2f, c); DrawDiamond(px, size, cx, cy, 6f, c); break;
                case 54: DrawTriangle(px, size, cx, cy + 14f, cx - 12f, cy - 6f, cx + 12f, cy - 6f, c); DrawCircle(px, size, cx, cy - 12f, 4f, c); break;
                case 55: DrawCircle(px, size, cx, cy, 6f, c); for (int i = 0; i < 8; i++) { float a = i * 0.785f; DrawLine(px, size, cx + Mathf.Cos(a) * 10f, cy + Mathf.Sin(a) * 10f, cx + Mathf.Cos(a) * 18f, cy + Mathf.Sin(a) * 18f, c, 2f); } break;  // sun
                case 56: DrawCircle(px, size, cx, cy, 14f, c); DrawCircle(px, size, cx + 6f, cy + 6f, 6f, c); break;  // crescent
                case 57: DrawSquare(px, size, cx, cy + 10f, 6f, c); DrawSquare(px, size, cx, cy, 6f, c); DrawSquare(px, size, cx, cy - 10f, 6f, c); break;  // 3 dots vert
                case 58: DrawSquare(px, size, cx - 10f, cy, 6f, c); DrawSquare(px, size, cx, cy, 6f, c); DrawSquare(px, size, cx + 10f, cy, 6f, c); break;
                case 59: DrawCircle(px, size, cx, cy + 12f, 5f, c); DrawCircle(px, size, cx, cy, 5f, c); DrawCircle(px, size, cx, cy - 12f, 5f, c); break;
                case 60: DrawLine(px, size, cx - 14f, cy - 14f, cx + 14f, cy - 14f, c, 3f); DrawLine(px, size, cx - 14f, cy + 14f, cx + 14f, cy + 14f, c, 3f); DrawLine(px, size, cx - 14f, cy - 14f, cx - 14f, cy + 14f, c, 3f); DrawLine(px, size, cx + 14f, cy - 14f, cx + 14f, cy + 14f, c, 3f); break;  // hollow square
                case 61: DrawArc(px, size, cx, cy + 8f, 12f, 180f, 360f, c, 3f); DrawArc(px, size, cx, cy - 8f, 12f, 0f, 180f, c, 3f); break;  // S
                case 62: DrawLine(px, size, cx, cy - 16f, cx + 14f, cy + 14f, c, 3f); DrawLine(px, size, cx, cy - 16f, cx - 14f, cy + 14f, c, 3f); DrawLine(px, size, cx - 10f, cy + 4f, cx + 10f, cy + 4f, c, 3f); break;  // A
                case 63: DrawLine(px, size, cx - 14f, cy - 14f, cx + 14f, cy - 14f, c, 3f); DrawLine(px, size, cx, cy - 14f, cx, cy + 14f, c, 3f); break;  // T
                case 64: DrawArc(px, size, cx, cy, 14f, 90f, 360f, c, 3f); break;  // open ring
                case 65: DrawTriangle(px, size, cx, cy + 14f, cx - 14f, cy - 14f, cx + 14f, cy - 14f, c); DrawCircle(px, size, cx, cy, 5f, c); break;
                case 66: DrawCircle(px, size, cx, cy + 8f, 8f, c); DrawCircle(px, size, cx, cy - 8f, 8f, c); break;  // figure 8
                case 67: DrawDiamond(px, size, cx, cy + 10f, 6f, c); DrawDiamond(px, size, cx, cy - 10f, 6f, c); break;
                case 68: DrawLine(px, size, cx - 14f, cy, cx + 14f, cy, c, 3f); DrawCircle(px, size, cx, cy, 8f, c); break;
                case 69: DrawRing(px, size, cx, cy, 16f, 2f, c); DrawLine(px, size, cx - 16f, cy - 16f, cx + 16f, cy + 16f, c, 3f); break;  // no/cancel
                case 70: for (int i = 0; i < 3; i++) DrawCircle(px, size, cx - 12f + i * 12f, cy, 4f, c); break;  // ellipsis
                case 71: DrawTriangle(px, size, cx, cy + 12f, cx - 14f, cy - 12f, cx + 14f, cy - 12f, c); DrawSquare(px, size, cx, cy + 4f, 4f, c); break;  // warning
                case 72: DrawSquareOutline(px, size, cx, cy, 16f, 2f, c); DrawLine(px, size, cx, cy - 12f, cx, cy + 12f, c, 2f); DrawLine(px, size, cx - 12f, cy, cx + 12f, cy, c, 2f); break;
                case 73: DrawCircle(px, size, cx - 10f, cy - 10f, 4f, c); DrawCircle(px, size, cx + 10f, cy - 10f, 4f, c); DrawCircle(px, size, cx - 10f, cy + 10f, 4f, c); DrawCircle(px, size, cx + 10f, cy + 10f, 4f, c); break;
                case 74: DrawArc(px, size, cx, cy, 18f, 45f, 315f, c, 4f); break;  // C
                case 75: DrawLine(px, size, cx - 12f, cy + 14f, cx - 12f, cy - 14f, c, 3f); DrawLine(px, size, cx - 12f, cy - 14f, cx + 12f, cy - 14f, c, 3f); DrawLine(px, size, cx + 12f, cy - 14f, cx + 12f, cy + 14f, c, 3f); break;  // U
                case 76: DrawDiamond(px, size, cx - 9f, cy - 9f, 6f, c); DrawDiamond(px, size, cx + 9f, cy - 9f, 6f, c); DrawDiamond(px, size, cx, cy + 9f, 6f, c); break;
                case 77: DrawPolygon(px, size, cx, cy, 14f, 4, c, true); DrawPolygon(px, size, cx, cy, 20f, 4, c, false); break;
                case 78: DrawArc(px, size, cx - 8f, cy, 10f, 270f, 450f, c, 3f); DrawArc(px, size, cx + 8f, cy, 10f, 90f, 270f, c, 3f); break;  // brackets
                default: DrawCircle(px, size, cx, cy, 10f, c); break;  // safety
            }
        }

        // ─── Drawing primitives ─────────────────────────────────────
        // Filled disc.
        private static void DrawCircle(Color[] px, int size, float cx, float cy, float radius, Color c)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius));
            float r2 = radius * radius;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r2) px[y * size + x] = c;
                }
        }

        // Ring (annulus): outer radius, ring thickness.
        private static void DrawRing(Color[] px, int size, float cx, float cy, float radius, float thickness, Color c)
        {
            float inner = radius - thickness;
            float r2outer = radius * radius;
            float r2inner = inner * inner;
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float d2 = dx * dx + dy * dy;
                    if (d2 <= r2outer && d2 >= r2inner) px[y * size + x] = c;
                }
        }

        // Filled square centered on (cx, cy), side = halfSize*2.
        private static void DrawSquare(Color[] px, int size, float cx, float cy, float halfSize, Color c)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - halfSize));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + halfSize));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - halfSize));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + halfSize));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++) px[y * size + x] = c;
        }

        // Square outline.
        private static void DrawSquareOutline(Color[] px, int size, float cx, float cy, float halfSize, float thickness, Color c)
        {
            DrawLine(px, size, cx - halfSize, cy - halfSize, cx + halfSize, cy - halfSize, c, thickness);
            DrawLine(px, size, cx - halfSize, cy + halfSize, cx + halfSize, cy + halfSize, c, thickness);
            DrawLine(px, size, cx - halfSize, cy - halfSize, cx - halfSize, cy + halfSize, c, thickness);
            DrawLine(px, size, cx + halfSize, cy - halfSize, cx + halfSize, cy + halfSize, c, thickness);
        }

        // Filled diamond (rotated square).
        private static void DrawDiamond(Color[] px, int size, float cx, float cy, float halfSize, Color c)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - halfSize));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + halfSize));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - halfSize));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + halfSize));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    if (Mathf.Abs(x - cx) + Mathf.Abs(y - cy) <= halfSize) px[y * size + x] = c;
                }
        }

        // Diamond outline (4 lines).
        private static void DrawDiamondOutline(Color[] px, int size, float cx, float cy, float halfSize, float thickness, Color c)
        {
            DrawLine(px, size, cx, cy - halfSize, cx + halfSize, cy, c, thickness);
            DrawLine(px, size, cx + halfSize, cy, cx, cy + halfSize, c, thickness);
            DrawLine(px, size, cx, cy + halfSize, cx - halfSize, cy, c, thickness);
            DrawLine(px, size, cx - halfSize, cy, cx, cy - halfSize, c, thickness);
        }

        // Filled triangle (3-point scanline).
        private static void DrawTriangle(Color[] px, int size, float x1, float y1, float x2, float y2, float x3, float y3, Color c)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(x1, Mathf.Min(x2, x3))));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(x1, Mathf.Max(x2, x3))));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(y1, Mathf.Min(y2, y3))));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(y1, Mathf.Max(y2, y3))));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float d1 = (x - x2) * (y1 - y2) - (x1 - x2) * (y - y2);
                    float d2 = (x - x3) * (y2 - y3) - (x2 - x3) * (y - y3);
                    float d3 = (x - x1) * (y3 - y1) - (x3 - x1) * (y - y1);
                    bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                    bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
                    if (!(neg && pos)) px[y * size + x] = c;
                }
        }

        // Anti-aliased line (point-stamp method).
        private static void DrawLine(Color[] px, int size, float x0, float y0, float x1, float y1, Color c, float thickness)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) * 2f));
            float halfT = thickness * 0.5f;
            float r2 = halfT * halfT;
            for (int step = 0; step <= steps; step++)
            {
                float t = (float)step / steps;
                float px2 = Mathf.Lerp(x0, x1, t);
                float py2 = Mathf.Lerp(y0, y1, t);
                int minX = Mathf.Max(0, Mathf.FloorToInt(px2 - halfT));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(px2 + halfT));
                int minY = Mathf.Max(0, Mathf.FloorToInt(py2 - halfT));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(py2 + halfT));
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        float dx = x - px2, dy = y - py2;
                        if (dx * dx + dy * dy <= r2) px[y * size + x] = c;
                    }
            }
        }

        // Arc.
        private static void DrawArc(Color[] px, int size, float cx, float cy, float radius, float startDeg, float endDeg, Color c, float thickness)
        {
            int steps = Mathf.Max(16, Mathf.CeilToInt(Mathf.Abs(endDeg - startDeg) * radius * 0.08f));
            for (int step = 0; step <= steps; step++)
            {
                float t = (float)step / steps;
                float a = Mathf.Lerp(startDeg, endDeg, t) * Mathf.Deg2Rad;
                float pxp = cx + Mathf.Cos(a) * radius;
                float pyp = cy + Mathf.Sin(a) * radius;
                DrawCircle(px, size, pxp, pyp, thickness * 0.5f, c);
            }
        }

        // N-sided polygon. Either filled (decomposed into triangles from
        // center) or outline (edges).
        private static void DrawPolygon(Color[] px, int size, float cx, float cy, float radius, int sides, Color c, bool filled)
        {
            sides = Mathf.Max(3, sides);
            var pts = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = (i / (float)sides) * 2f * Mathf.PI - Mathf.PI / 2f;
                pts[i] = new Vector2(cx + Mathf.Cos(a) * radius, cy + Mathf.Sin(a) * radius);
            }
            if (filled)
            {
                for (int i = 0; i < sides; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % sides];
                    DrawTriangle(px, size, cx, cy, a.x, a.y, b.x, b.y, c);
                }
            }
            else
            {
                for (int i = 0; i < sides; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % sides];
                    DrawLine(px, size, a.x, a.y, b.x, b.y, c, 3f);
                }
            }
        }

        // N-pointed star (alternating outer/inner radius points).
        private static void DrawStar(Color[] px, int size, float cx, float cy, float outerR, float innerR, int points, Color c)
        {
            int total = points * 2;
            var pts = new Vector2[total];
            for (int i = 0; i < total; i++)
            {
                float a = (i / (float)total) * 2f * Mathf.PI - Mathf.PI / 2f;
                float r = (i % 2 == 0) ? outerR : innerR;
                pts[i] = new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r);
            }
            for (int i = 0; i < total; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % total];
                DrawTriangle(px, size, cx, cy, a.x, a.y, b.x, b.y, c);
            }
        }

        // Arrow — line with a triangular head at the (toX, toY) end.
        private static void DrawArrow(Color[] px, int size, float fromX, float fromY, float toX, float toY, Color c)
        {
            DrawLine(px, size, fromX, fromY, toX, toY, c, 3f);
            // Triangular head perpendicular to direction
            float dx = toX - fromX, dy = toY - fromY;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;
            float ux = dx / len, uy = dy / len;       // forward
            float px2 = -uy, py2 = ux;                // perpendicular
            float headLen = 8f, headHalfW = 5f;
            float baseX = toX - ux * headLen;
            float baseY = toY - uy * headLen;
            DrawTriangle(px, size,
                toX, toY,
                baseX + px2 * headHalfW, baseY + py2 * headHalfW,
                baseX - px2 * headHalfW, baseY - py2 * headHalfW,
                c);
        }

        // Instantiates the real prefab under an INACTIVE root and strips everything
        // that isn't visual, rather than hand-copying the component types we happen
        // to know about.
        //
        // Why: the old CopyVisualsRecursive only copied MeshRenderer and
        // SkinnedMeshRenderer. Renderer has other subclasses —
        // ParticleSystemRenderer, TrailRenderer, LineRenderer, BillboardRenderer,
        // SpriteRenderer — so any prefab whose visuals live in one of those built an
        // empty stage and bucketed as "empty". Copy-by-inclusion can only ever
        // render what someone remembered to list; strip-by-exclusion renders
        // whatever the prefab actually has. Same shape kg_Blueprint's PhotoManager
        // and Utils.CreateViewGameObjectForBlueprint use.
        //
        // The root MUST be inactive before anything is instantiated into it:
        // instantiating into an inactive parent means Awake never runs, so no
        // ZNetView registers itself, no Piece hooks fire, and nothing NREs hunting
        // for game state out at the stage position. It is activated only once the
        // non-visual components are gone.
        private GameObject BuildRenderProxy(GameObject source)
        {
            if (source == null) return null;

            var root = new GameObject(source.name + "_va_render_proxy");
            root.SetActive(false);
            root.transform.position = StagePosition;
            root.transform.rotation = Quaternion.identity;

            var inst = UnityEngine.Object.Instantiate(source, root.transform);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = source.transform.localScale;

            // Instantiate carries activeSelf across, and some sources are inactive
            // templates — the clutter stand-ins are built SetActive(false) and parked
            // under a holder. The old copy path rebuilt them into a fresh always-on
            // hierarchy, so it never noticed; instantiating faithfully means an
            // inactive source renders nothing at all. Safe to switch on here because
            // the root is still inactive, so no Awake fires until the strip is done.
            inst.SetActive(true);

            PrepareProxyVisuals(root);
            SetLayerRecursive(root.transform, IconLayer);
            root.SetActive(true);
            return root;
        }

        // Collapses LODGroups to their LOD0 renderers, then destroys every
        // non-visual component.
        //
        // LODGroup has to go: the icon camera sits far from the subject, so a live
        // LODGroup would pick the lowest LOD or cull outright. But simply deleting
        // it leaves every LOD level renderable at once, co-located and z-fighting.
        // Keeping LOD0's renderers and dropping the rest gives the silhouette the
        // group was designed to show at full detail.
        private static void PrepareProxyVisuals(GameObject root)
        {
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                if (group == null) continue;
                var lods = group.GetLODs();
                var keep = new HashSet<Renderer>();
                if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                {
                    foreach (var r in lods[0].renderers)
                        if (r != null) keep.Add(r);
                }

                // Disabled, not destroyed: a SkinnedMeshRenderer with a Cloth on it
                // cannot be removed while the Cloth lives, and Unity logs an error
                // rather than failing quietly. Disabling keeps it out of the render
                // with no dependency to negotiate.
                foreach (var r in group.GetComponentsInChildren<Renderer>(true))
                    if (r != null && keep.Count > 0 && !keep.Contains(r))
                        r.enabled = false;

                SafeDestroyComponent(group);
            }

            // Destroy dependents before their dependencies. Unity refuses to remove a
            // component another one [RequireComponent]s and logs an error when you
            // try, so ordering by "how many things does this type require" puts e.g.
            // CharacterDrop ahead of the Humanoid it depends on.
            var all = new List<Component>(root.GetComponentsInChildren<Component>(true));
            all.Sort((a, b) => RequireDepth(b).CompareTo(RequireDepth(a)));

            foreach (var c in all)
            {
                if (c == null) continue;               // already went with a dependency
                if (IsIconVisualComponent(c)) continue;
                if (c is Animator animator) { animator.enabled = false; continue; }
                SafeDestroyComponent(c);
            }
        }

        private static readonly Dictionary<Type, int> _requireDepth = new Dictionary<Type, int>();

        private static int RequireDepth(Component c)
        {
            if (c == null) return 0;
            var t = c.GetType();
            if (_requireDepth.TryGetValue(t, out int depth)) return depth;

            depth = 0;
            try
            {
                foreach (var attr in t.GetCustomAttributes(typeof(RequireComponent), inherit: true))
                {
                    var rc = (RequireComponent)attr;
                    if (rc.m_Type0 != null) depth++;
                    if (rc.m_Type1 != null) depth++;
                    if (rc.m_Type2 != null) depth++;
                }
            }
            catch { depth = 0; }

            _requireDepth[t] = depth;
            return depth;
        }

        // ParticleSystem is kept because ParticleSystemRenderer has a
        // RequireComponent on it — destroying it would log a Unity error and no-op,
        // leaving the renderer behind anyway.
        private static bool IsIconVisualComponent(Component c)
        {
            switch (c)
            {
                case Transform _:
                case MeshFilter _:
                case Renderer _:
                case ParticleSystem _:
                    return true;
                default:
                    return false;
            }
        }

        private static void SafeDestroyComponent(Component c)
        {
            if (c == null) return;
            try { UnityEngine.Object.DestroyImmediate(c); }
            catch { /* required by something we kept; harmless, it just stays */ }
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
        }

        // ──────────────────────────────────────────────────────────────
        //  Blueprint icon rendering
        // ──────────────────────────────────────────────────────────────

        // One stripped copy per distinct piece prefab, cloned into every stage that uses that piece.
        // A null value marks a prefab with no mesh content, so no stage stages it twice to find out.
        // Instantiating the real prefab per piece deep-copied its whole component graph and then
        // PrepareProxyVisuals walked and destroyed the lot again, once per piece per icon; a stripped
        // template pays that once per prefab per drain and its clones carry only transforms and
        // renderers. Released with the holder when the queue drains, so a re-bake's next wave restages
        // from the current prefabs.
        private readonly Dictionary<string, GameObject> _strippedPieceTemplates =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        private GameObject _pieceTemplateHolder;

        private Transform EnsurePieceTemplateHolder()
        {
            if (_pieceTemplateHolder == null)
            {
                _pieceTemplateHolder = new GameObject("VA_IconPieceTemplates");
                _pieceTemplateHolder.SetActive(false);
                _pieceTemplateHolder.transform.SetParent(transform, false);
            }
            return _pieceTemplateHolder.transform;
        }

        private void ReleasePieceTemplates()
        {
            _strippedPieceTemplates.Clear();
            _pieceDrawsByPrefab.Clear();
            _stageDraws.Clear();
            if (_pieceTemplateHolder == null) return;
            NetworkObjectHelper.SafeDestroyImmediate(_pieceTemplateHolder);
            _pieceTemplateHolder = null;
        }

        // False when the prefab has nothing a thumbnail could show. The template is built under the
        // permanently inactive holder, so the strip runs before any Awake could — the same order
        // BuildRenderProxy relies on.
        private bool TryGetStrippedPieceTemplate(GameObject prefab, out GameObject template)
        {
            string prefabName = prefab.name;
            if (_strippedPieceTemplates.TryGetValue(prefabName, out template)) return template != null;

            if (!HasMeshContent(prefab))
            {
                _strippedPieceTemplates[prefabName] = null;
                template = null;
                return false;
            }

            template = UnityEngine.Object.Instantiate(prefab, EnsurePieceTemplateHolder());
            template.name = prefabName;
            template.transform.localPosition = Vector3.zero;
            template.transform.localRotation = Quaternion.identity;
            template.SetActive(true);
            PrepareProxyVisuals(template);
            SetLayerRecursive(template.transform, IconLayer);
            _strippedPieceTemplates[prefabName] = template;
            return true;
        }

        // What it takes to reproduce one of a template's renderers without a GameObject: the mesh and
        // submesh, the material that submesh uses, and where the renderer sits relative to the template
        // root. Recorded once per prefab; a stage then only supplies each piece's own matrix.
        private struct PieceDraw
        {
            public Mesh Mesh;
            public int SubmeshIndex;
            public Material Material;
            public Matrix4x4 TemplateLocalMatrix;
        }

        private struct StageDraw
        {
            public Mesh Mesh;
            public int SubmeshIndex;
            public Material Material;
            public Matrix4x4 WorldMatrix;
        }

        // Prefab name -> its draws, or null when the prefab has to be cloned instead. Cleared with the
        // templates it was read from.
        private readonly Dictionary<string, PieceDraw[]> _pieceDrawsByPrefab =
            new Dictionary<string, PieceDraw[]>(StringComparer.Ordinal);

        // The current stage's draws. Reused across icons, so a drain allocates this list once.
        private readonly List<StageDraw> _stageDraws = new List<StageDraw>(8192);

        private bool _immediateDrawsProven;
        private bool _immediateDrawsDisabled;

        private PieceDraw[] GetPieceDraws(string prefabName, GameObject template)
        {
            if (_immediateDrawsDisabled) return null;
            if (_pieceDrawsByPrefab.TryGetValue(prefabName, out var draws)) return draws;
            draws = BuildPieceDraws(template);
            _pieceDrawsByPrefab[prefabName] = draws;
            return draws;
        }

        // Null for any template this can't reproduce exactly: a Renderer subclass with no mesh of its own
        // (ParticleSystem, Trail, Line, Billboard, Sprite, Skinned), a MeshRenderer with no mesh, or a
        // multi-pass material setup. Those prefabs keep the clone path, which renders whatever they have.
        private PieceDraw[] BuildPieceDraws(GameObject template)
        {
            var renderers = template.GetComponentsInChildren<Renderer>(true);
            var draws = new List<PieceDraw>(renderers.Length);
            var worldToTemplate = template.transform.worldToLocalMatrix;

            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!IsActiveWithin(renderer.transform, template.transform)) continue;
                if (!(renderer is MeshRenderer)) return null;

                var meshFilter = renderer.GetComponent<MeshFilter>();
                var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                if (mesh == null) return null;

                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0 || materials.Length > mesh.subMeshCount) return null;

                var templateLocalMatrix = worldToTemplate * renderer.transform.localToWorldMatrix;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) return null;
                    draws.Add(new PieceDraw
                    {
                        Mesh = mesh,
                        SubmeshIndex = i,
                        Material = materials[i],
                        TemplateLocalMatrix = templateLocalMatrix,
                    });
                }
            }

            return draws.Count > 0 ? draws.ToArray() : null;
        }

        // Whether this renderer would draw once the stage root is switched on. The template hangs under a
        // permanently inactive holder, so activeInHierarchy is false for all of it and answers nothing.
        private static bool IsActiveWithin(Transform child, Transform root)
        {
            for (var current = child; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf) return false;
                if (current == root) break;
            }
            return true;
        }

        private void SubmitStageDraws()
        {
            for (int i = 0; i < _stageDraws.Count; i++)
            {
                var draw = _stageDraws[i];
                Graphics.DrawMesh(draw.Mesh, draw.WorldMatrix, draw.Material, IconLayer, _iconCamera,
                                  draw.SubmeshIndex, null, false, false);
            }
        }

        // The framing bounds of a stage that is part cloned renderers, part meshes that exist only as a
        // matrix. A drawn mesh has no Renderer to ask, so its world bounds come from transforming all eight
        // corners of the mesh's local bounds — the same thing Renderer.bounds reports.
        private Bounds ComputeStageBounds(Renderer[] clonedRenderers)
        {
            Bounds combined = default;
            bool initialized = false;

            foreach (var renderer in clonedRenderers)
            {
                if (renderer == null || renderer is ParticleSystemRenderer) continue;
                if (!initialized) { combined = renderer.bounds; initialized = true; }
                else combined.Encapsulate(renderer.bounds);
            }

            for (int i = 0; i < _stageDraws.Count; i++)
            {
                var draw = _stageDraws[i];
                if (draw.Mesh == null) continue;
                var localBounds = draw.Mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -localBounds.extents.x : localBounds.extents.x,
                        (corner & 2) == 0 ? -localBounds.extents.y : localBounds.extents.y,
                        (corner & 4) == 0 ? -localBounds.extents.z : localBounds.extents.z);
                    var world = draw.WorldMatrix.MultiplyPoint3x4(localBounds.center + offset);
                    if (!initialized) { combined = new Bounds(world, Vector3.zero); initialized = true; }
                    else combined.Encapsulate(world);
                }
            }

            return initialized ? combined : new Bounds(Vector3.zero, Vector3.one);
        }

        private static bool TextureHasVisiblePixels(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].a > 0) return true;
            return false;
        }

        // Builds one render-proxy stage from a multi-piece blueprint: each piece's
        // ZNetScene prefab is mesh-copied into a child at its recorded offset under
        // a stage root. Pieces missing from ZNetScene, or particle-only, are skipped.
        private GameObject BuildBlueprintProxyStage(VABlueprintLoader.Blueprint bp)
        {
            if (bp == null) return null;

            _stageDraws.Clear();

            var root = new GameObject("BP_Stage_" + (string.IsNullOrEmpty(bp.Name) ? "unnamed" : bp.Name));
            // Inactive until the strip is done — see BuildRenderProxy.
            root.SetActive(false);
            root.transform.position = StagePosition;
            root.transform.rotation = Quaternion.identity;

            var scene = ZNetScene.instance;
            if (scene == null) { root.SetActive(true); return root; }

            // A 128x128 thumbnail cannot show more than a few thousand parts, but
            // this loop used to deep-copy EVERY object in the blueprint — and
            // piece_Phalanx has 112,171. That is ~112k GameObjects plus recursive
            // visual copies inside a single frame of a 6ms-budgeted loop, which is
            // the multi-second freeze the icon drain was blamed for. Sampling with
            // a stride rather than truncating keeps the silhouette: taking the
            // first N would photograph one corner of the build.
            int total = bp.Objects?.Count ?? 0;
            int stride = total > MaxBlueprintProxyPieces
                ? Mathf.CeilToInt(total / (float)MaxBlueprintProxyPieces)
                : 1;
            if (stride > 1)
            {
                Debug.Log($"{VaPieces.PluginName}: BuildBlueprintProxyStage — bp '{bp.Name}' has {total} objects; "
                    + $"sampling every {stride} to stay under {MaxBlueprintProxyPieces} proxies for the thumbnail.");
            }

            int copied = 0, missing = 0, index = -1;
            foreach (var piece in bp.Objects)
            {
                index++;
                if (stride > 1 && (index % stride) != 0) continue;
                if (piece == null) continue;
                var prefab = scene.GetPrefab(piece.Prefab);
                if (prefab == null) { missing++; continue; }
                if (!TryGetStrippedPieceTemplate(prefab, out var template)) continue;

                var pieceScale = piece.Scale != Vector3.zero ? piece.Scale : Vector3.one;

                // Unity's transform hierarchy applies a parent's non-uniform scale per level instead of
                // shearing a rotated child, which a matrix product does do. A piece scaled unevenly (or
                // mirrored) therefore keeps the clone path and stays exactly the shape it was.
                bool uniformPieceScale = pieceScale.x > 0f
                                         && Mathf.Approximately(pieceScale.x, pieceScale.y)
                                         && Mathf.Approximately(pieceScale.x, pieceScale.z);

                var draws = uniformPieceScale ? GetPieceDraws(prefab.name, template) : null;
                if (draws != null)
                {
                    var pieceMatrix = Matrix4x4.TRS(StagePosition + piece.Pos, piece.Rot, pieceScale);
                    for (int i = 0; i < draws.Length; i++)
                    {
                        var draw = draws[i];
                        _stageDraws.Add(new StageDraw
                        {
                            Mesh = draw.Mesh,
                            SubmeshIndex = draw.SubmeshIndex,
                            Material = draw.Material,
                            WorldMatrix = pieceMatrix * draw.TemplateLocalMatrix,
                        });
                    }
                    copied++;
                    continue;
                }

                var child = UnityEngine.Object.Instantiate(template, root.transform);
                child.name = prefab.name + "_bp_proxy";
                child.transform.localPosition = piece.Pos;
                child.transform.localRotation = piece.Rot;
                child.transform.localScale = pieceScale;
                copied++;
            }

            // The templates are already stripped and already on IconLayer, which their clones inherit,
            // so the stage root is the only object left to move onto the layer the icon camera sees.
            root.layer = IconLayer;
            root.SetActive(true);

            if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
            {
                Debug.Log($"{VaPieces.PluginName}: BuildBlueprintProxyStage — bp '{bp.Name}' copied {copied} piece(s), {missing} missing from ZNetScene.");
            }
            return root;
        }

        // Renders a single icon snapshot of an entire blueprint via the
        // multi-prefab proxy stage. Same camera framing + capture path
        // RenderIcon uses for single prefabs — just on a composite
        // root instead of a one-prefab proxy. The captured PNG is
        // saved to the icon cache so subsequent loads skip the
        // expensive stage build.
        //
        // Returns null on failure; caller leaves m_icon unassigned and
        // vanilla's shield fallback shows.
        // Blueprint stubs render from the blueprint itself. So does a baked bundle's prefab, which
        // is registered mesh-less — its mesh is only built when something places or selects it, so
        // the mesh path finds nothing and every bundle buckets as "empty". That is why the Baked
        // Meshes tab comes back blank after a re-bake drops its cached icons. Rendering the
        // blueprint it was baked from avoids reading the bundles back (1.8GB here) mid-drain.
        private static bool TryResolveBlueprintForIcon(string prefabName, out VABlueprintLoader.Blueprint bp)
        {
            if (VABlueprintHammerIntegration.TryGetBlueprint(prefabName, out bp) && bp != null) return true;

            string cacheKey = VABakeBundle.TryGetCacheKeyForPrefab(prefabName);
            if (!string.IsNullOrEmpty(cacheKey)
                && VABlueprintHammerIntegration.TryGetBlueprintBySourceFileName(cacheKey, out bp)
                && bp != null) return true;

            bp = null;
            return false;
        }

        // Most pieces are drawn straight from their mesh with no GameObject at all, and that path is only
        // believed once it has produced a visible pixel. The first icon that submits draws and comes back
        // blank turns the draws off for the session and is rendered again from proxy clones, so a machine
        // where immediate-mode geometry never reaches a manual Camera.Render() still gets its icons.
        private Sprite RenderBlueprintIcon(VABlueprintLoader.Blueprint bp, string outputName)
        {
            var sprite = RenderBlueprintIconPass(bp, outputName, out bool retryWithProxyClones);
            if (retryWithProxyClones) sprite = RenderBlueprintIconPass(bp, outputName, out _);
            return sprite;
        }

        private Sprite RenderBlueprintIconPass(VABlueprintLoader.Blueprint bp, string outputName,
                                               out bool retryWithProxyClones)
        {
            retryWithProxyClones = false;
            if (bp == null || _iconCamera == null) return null;
            EnsureRenderTexture();

            GameObject stage = null;
            try
            {
                stage = BuildBlueprintProxyStage(bp);
                if (stage == null) return null;

                var renderers = stage.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0 && _stageDraws.Count == 0)
                {
                    // Some blueprints (LocationProxy stubs, exports referencing
                    // prefabs not in ZNetScene) resolve to nothing. Counted once
                    // per name for the summary banner; per-blueprint warn is verbose-only.
                    string blueprintName = bp.Name ?? "<unnamed>";
                    if (_zeroRendererBlueprintWarned.Add(blueprintName))
                    {
                        ZeroRendererBlueprintCount++;
                        if (VAFapLoadSummary.VerboseEnabled)
                        {
                            Debug.LogWarning($"{VaPieces.PluginName}: RenderBlueprintIcon — bp '{blueprintName}' produced zero renderers (all pieces missing or non-mesh). Suppressing further warnings for this blueprint name.");
                        }
                    }
                    return null;
                }

                Bounds bounds = ComputeStageBounds(renderers);
                if (bounds.size.sqrMagnitude < DegenerateBoundsSqrThreshold) bounds.size = Vector3.one;

                FrameCameraOnBounds(bounds);

                _iconCamera.targetTexture = _renderTexture;
                SubmitStageDraws();

                // Same ambient overrides as the single-prefab path so blueprint
                // icons match the rest of the tab strip; restored in finally.
                var prevMode = RenderSettings.ambientMode;
                var prevLight = RenderSettings.ambientLight;
                var prevIntensity = RenderSettings.ambientIntensity;
                var prevSky = RenderSettings.ambientSkyColor;
                var prevEquator = RenderSettings.ambientEquatorColor;
                var prevGround = RenderSettings.ambientGroundColor;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = IconAmbientLight;
                RenderSettings.ambientIntensity = IconAmbientIntensity;
                RenderSettings.ambientSkyColor = IconAmbientSky;
                RenderSettings.ambientEquatorColor = IconAmbientEquator;
                RenderSettings.ambientGroundColor = IconAmbientGround;

                Texture2D tex;
                try { tex = RenderSingleFrame(outputName); }
                finally
                {
                    RenderSettings.ambientMode = prevMode;
                    RenderSettings.ambientLight = prevLight;
                    RenderSettings.ambientIntensity = prevIntensity;
                    RenderSettings.ambientSkyColor = prevSky;
                    RenderSettings.ambientEquatorColor = prevEquator;
                    RenderSettings.ambientGroundColor = prevGround;
                }

                if (tex == null) return null;

                if (!_immediateDrawsProven && _stageDraws.Count > 0)
                {
                    if (TextureHasVisiblePixels(tex)) _immediateDrawsProven = true;
                    else
                    {
                        _immediateDrawsDisabled = true;
                        _pieceDrawsByPrefab.Clear();
                        Debug.LogWarning($"{VaPieces.PluginName}: RenderBlueprintIcon — {_stageDraws.Count} mesh draw(s) for bp '{bp.Name ?? "<unnamed>"}' captured nothing, so blueprint icons will use proxy clones for the rest of the session (a blueprint made only of fully transparent materials would read the same way).");
                        UnityEngine.Object.Destroy(tex);
                        retryWithProxyClones = true;
                        return null;
                    }
                }

                var sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, IconSize, IconSize),
                    SpritePivotCenter,
                    SpritePixelsPerUnit);
                sprite.name = outputName + "_icon";

                // Save the PNG (via the deferred queue) so the next session hits
                // FlushPendingPaced and skips the multi-prefab stage build.
                QueueIconSave(outputName, tex);

                return sprite;
            }
            finally
            {
                // SafeDestroyImmediate (not deferred Destroy): the queue can drain
                // multiple stages per frame, so each must be gone before the next
                // is built or the camera captures both at StagePosition.
                if (stage != null) NetworkObjectHelper.SafeDestroyImmediate(stage);
            }
        }

        // Public wrapper so VABlueprintHammerIntegration can use the same
        // mesh-copy logic to build ghost-preview visuals on blueprint
        // stubs without duplicating the recursion. Forwards directly to
        // the internal implementation — no additional state.
        public static void CopyVisualsRecursivePublic(Transform sourceT, Transform proxyT)
            => CopyVisualsRecursive(sourceT, proxyT);

        private static void CopyVisualsRecursive(Transform sourceT, Transform proxyT)
        {
            var meshFilter = sourceT.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                var proxyMeshFilter = proxyT.gameObject.AddComponent<MeshFilter>();
                proxyMeshFilter.sharedMesh = meshFilter.sharedMesh;
            }

            var meshRenderer = sourceT.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                var proxyMeshRenderer = proxyT.gameObject.AddComponent<MeshRenderer>();
                proxyMeshRenderer.sharedMaterials = meshRenderer.sharedMaterials;
                proxyMeshRenderer.shadowCastingMode = meshRenderer.shadowCastingMode;
                proxyMeshRenderer.receiveShadows = meshRenderer.receiveShadows;
            }

            var skinnedRenderer = sourceT.GetComponent<SkinnedMeshRenderer>();
            if (skinnedRenderer != null && skinnedRenderer.sharedMesh != null)
            {
                // No bones bound — renders in bind pose, which is fine for a thumbnail.
                var proxySkinnedRenderer = proxyT.gameObject.AddComponent<SkinnedMeshRenderer>();
                proxySkinnedRenderer.sharedMesh = skinnedRenderer.sharedMesh;
                proxySkinnedRenderer.sharedMaterials = skinnedRenderer.sharedMaterials;
                proxySkinnedRenderer.shadowCastingMode = skinnedRenderer.shadowCastingMode;
                proxySkinnedRenderer.receiveShadows = skinnedRenderer.receiveShadows;
            }

            int childCount = sourceT.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var sourceChild = sourceT.GetChild(i);
                if (sourceChild == null) continue;

                // Tag-only "snappoint" children contribute nothing visual.
                if (sourceChild.CompareTag("snappoint")) continue;

                var proxyChild = new GameObject(sourceChild.name);
                proxyChild.transform.SetParent(proxyT, false);
                proxyChild.transform.localPosition = sourceChild.localPosition;
                proxyChild.transform.localRotation = sourceChild.localRotation;
                proxyChild.transform.localScale = sourceChild.localScale;
                CopyVisualsRecursive(sourceChild, proxyChild.transform);
            }
        }

        private static Bounds ComputeRenderBounds(Renderer[] renderers, bool includeParticles = false)
        {
            // Mesh icons exclude ParticleSystemRenderer (its emission volume would
            // inflate framing past the silhouette); particle icons include it
            // because the simulated particles ARE the visible content.
            Bounds combined = default;
            bool initialized = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!includeParticles && renderer is ParticleSystemRenderer) continue;
                if (!initialized)
                {
                    combined = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(renderer.bounds);
                }
            }
            if (!initialized) return new Bounds(Vector3.zero, Vector3.one);

            // Clamp particle framing so long-lived emitters drifting outward
            // during Simulate don't shrink the icon to a few pixels.
            if (includeParticles)
            {
                const float maxParticleIconExtent = 4f;
                var size = combined.size;
                if (size.x > maxParticleIconExtent) size.x = maxParticleIconExtent;
                if (size.y > maxParticleIconExtent) size.y = maxParticleIconExtent;
                if (size.z > maxParticleIconExtent) size.z = maxParticleIconExtent;
                combined.size = size;
            }
            return combined;
        }

        // ──────────────────────────────────────────────────────────────
        //  Prefab classification (which render path to use)
        // ──────────────────────────────────────────────────────────────

        // Returns true if the prefab's visible content is mesh geometry
        // (i.e. at least one MeshFilter or SkinnedMeshRenderer has an
        // actual sharedMesh set). Drives the proxy-vs-instance decision
        // in RenderIcon.
        private static bool HasMeshContent(GameObject prefab)
        {
            if (prefab == null) return false;
            var meshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length; i++)
                if (meshFilters[i] != null && meshFilters[i].sharedMesh != null) return true;
            var skinnedRenderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
                if (skinnedRenderers[i] != null && skinnedRenderers[i].sharedMesh != null) return true;
            return false;
        }

        // Returns true if the prefab contains at least one ParticleSystem
        // anywhere in its hierarchy. Used to gate the safe-instance path
        // for FX/VFX prefabs that have no mesh content.
        private static bool HasParticleContent(GameObject prefab)
        {
            if (prefab == null) return false;
            return prefab.GetComponentInChildren<ParticleSystem>(true) != null;
        }

        // Only asked once a prefab has been found to have no mesh and no particles,
        // so this is the difference between "sound emitter" and "nothing at all".
        private static bool HasAudioContent(GameObject prefab)
        {
            if (prefab == null) return false;
            if (prefab.GetComponentInChildren<AudioSource>(true) != null) return true;
            return prefab.GetComponentInChildren<ZSFX>(true) != null;
        }

        // Deliberately NOT name-hashed like BuildFallbackIcon: every sound-only
        // prefab gets the same slate-blue speaker so a screenful of them reads as
        // one category at a glance instead of confetti.
        private Texture2D BuildSoundOnlyIcon(string prefabName)
        {
            var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
            {
                name = (prefabName ?? "unknown") + "_icon_soundonly",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            Color fill = new Color(0.16f, 0.20f, 0.28f, 1f);
            Color edge = new Color(0.10f, 0.13f, 0.19f, 1f);
            Color glyph = new Color(0.62f, 0.74f, 0.88f, 1f);

            var px = new Color32[IconSize * IconSize];
            float cx = IconSize * 0.5f, cy = IconSize * 0.5f;
            float outerR = IconSize * 0.42f;
            const int border = 2;

            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    int idx = y * IconSize + x;
                    float dx = x - cx, dy = y - cy;
                    if (Mathf.Abs(dx) > outerR + border || Mathf.Abs(dy) > outerR + border)
                        px[idx] = new Color32(0, 0, 0, 0);
                    else if (Mathf.Abs(dx) > outerR - border || Mathf.Abs(dy) > outerR - border)
                        px[idx] = edge;
                    else
                        px[idx] = fill;
                }
            }

            // Speaker box + cone, then two arcs to the right of it.
            float u = IconSize / 32f;
            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    float sx = (x - cx) / u, sy = (y - cy) / u;
                    bool on = false;

                    if (sx >= -9f && sx <= -5f && Mathf.Abs(sy) <= 2.5f) on = true;          // box
                    else if (sx > -5f && sx <= -1f && Mathf.Abs(sy) <= 2.5f + (sx + 5f) * 1.4f) on = true; // cone

                    if (!on)
                    {
                        float r = Mathf.Sqrt(sx * sx + sy * sy);
                        bool rightSide = sx > 0f && Mathf.Abs(sy) < sx * 1.6f;
                        if (rightSide && (Mathf.Abs(r - 4.5f) < 0.7f || Mathf.Abs(r - 7.5f) < 0.7f)) on = true;
                    }

                    if (on) px[y * IconSize + x] = glyph;
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // ──────────────────────────────────────────────────────────────
        //  Safe-instance path (used for particle-only prefabs)
        // ──────────────────────────────────────────────────────────────

        // Particle-aware safe-clone for FX/VFX/SFX icons. Instantiates the prefab
        // into an inactive staging parent (so no Awake fires), strips every
        // MonoBehaviour/Collider/LODGroup and mutes audio, then activates it on the
        // icon stage and pre-simulates particles. Caller destroys it after ReadPixels.
        private GameObject BuildSafeParticleInstance(GameObject prefab)
        {
            if (prefab == null) return null;

            EnsureInactiveStagingRoot();

            // Instantiate into the inactive staging parent so no Awake fires.
            GameObject instance = UnityEngine.Object.Instantiate(prefab, _inactiveStagingRoot.transform);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.name = "FAP_ParticleIconClone_" + prefab.name;

            // Strip ALL MonoBehaviours while inactive. DestroyImmediate so the
            // strip completes before SetActive(true) (Destroy defers a frame).
            var monoBehaviours = instance.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            for (int i = 0; i < monoBehaviours.Length; i++)
            {
                var monoBehaviour = monoBehaviours[i];
                if (monoBehaviour == null) continue;
                try { UnityEngine.Object.DestroyImmediate(monoBehaviour); }
                catch { /* component might have RequireComponent guards; ignore */ }
            }

            // Strip Colliders (no physics on the icon stage) and LODGroups (force LOD0).
            var colliders = instance.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (collider == null) continue;
                try { UnityEngine.Object.DestroyImmediate(collider); }
                catch { }
            }
            var lodGroups = instance.GetComponentsInChildren<LODGroup>(includeInactive: true);
            for (int i = 0; i < lodGroups.Length; i++)
            {
                var lodGroup = lodGroups[i];
                if (lodGroup == null) continue;
                try { UnityEngine.Object.DestroyImmediate(lodGroup); }
                catch { }
            }

            // Neutralize audio so no AudioSource Plays on activation.
            var audioSources = instance.GetComponentsInChildren<AudioSource>(includeInactive: true);
            for (int i = 0; i < audioSources.Length; i++)
            {
                var audioSource = audioSources[i];
                if (audioSource == null) continue;
                audioSource.mute = true;
                audioSource.playOnAwake = false;
                audioSource.volume = 0f;
                audioSource.enabled = false;
            }

            // Reparent onto the icon stage (this renderer's DontDestroyOnLoad GO)
            // and activate; surviving Unity components Awake and populate bounds.
            instance.transform.SetParent(transform, worldPositionStays: false);
            instance.transform.position = StagePosition;
            instance.transform.rotation = Quaternion.identity;
            instance.SetActive(true);

            // Pre-simulate particles so Renderer.bounds reflect the widest extent
            // the effect reaches; without it the camera frames the emitter origin
            // and the icon renders blank.
            var particles = instance.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < particles.Length; i++)
            {
                var ps = particles[i];
                if (ps == null) continue;
                float duration = DefaultParticleDurationSeconds;
                try
                {
                    var main = ps.main;
                    if (main.duration > 0f) duration = main.duration;
                    try
                    {
                        float life = main.startLifetime.constantMax;
                        if (life > 0f && life > duration) duration = life;
                    }
                    catch { /* curve/gradient startLifetime — skip */ }
                }
                catch { /* main accessor edge config — skip */ }
                duration = Mathf.Min(duration, MaxParticleDurationSeconds);
                try { ps.Simulate(duration, withChildren: true, restart: true); }
                catch { /* unsimulable PS — skip */ }
            }

            SetLayerRecursive(instance.transform, IconLayer);

            return instance;
        }

        // Inactive parent used as the staging area for cloned particle
        // prefabs while we strip their dangerous components. Setting
        // the parent inactive means cloned components don't Awake
        // during Instantiate — we get to mutate them first.
        private GameObject _inactiveStagingRoot;

        private void EnsureInactiveStagingRoot()
        {
            if (_inactiveStagingRoot != null) return;
            _inactiveStagingRoot = new GameObject("FAP_IconRenderInactiveStage");
            _inactiveStagingRoot.transform.SetParent(transform, false);
            _inactiveStagingRoot.SetActive(false);
        }

        // Legacy alias preserved for the (now unused) commented-out
        // reference body below. New call sites use BuildSafeParticleInstance.
        // Returns null to make any accidental call land safely.
        private GameObject BuildSafeInstance(GameObject prefab)
        {
            Debug.LogWarning($"{VaPieces.PluginName}: VAPieceIconRenderer.BuildSafeInstance — legacy method; new path is BuildSafeParticleInstance. Forwarding.");
            return BuildSafeParticleInstance(prefab);

#pragma warning disable CS0162   // unreachable code — kept for reference
            if (prefab == null) return null;

            // Save + override ZNetView init suppression and global audio.
            // Both restored in finally so even if Instantiate throws we
            // don't leak the suppression to other code that might
            // legitimately Instantiate prefabs in the same frame.
            bool prevForceDisableInit = ZNetView.m_forceDisableInit;
            float prevAudioVolume = AudioListener.volume;
            ZNetView.m_forceDisableInit = true;
            AudioListener.volume = 0f;
            GameObject instance;
            try
            {
                instance = UnityEngine.Object.Instantiate(
                    prefab, StagePosition, Quaternion.identity);

                // Silence audio sources (mute → playOnAwake off → volume 0 → Stop → disable).
                var audioSources = instance.GetComponentsInChildren<AudioSource>(includeInactive: true);
                for (int i = 0; i < audioSources.Length; i++)
                {
                    var audioSource = audioSources[i];
                    if (audioSource == null) continue;
                    audioSource.mute = true;
                    audioSource.playOnAwake = false;
                    audioSource.volume = 0f;
                    audioSource.Stop();
                    audioSource.enabled = false;
                }
            }
            finally
            {
                AudioListener.volume = prevAudioVolume;
                ZNetView.m_forceDisableInit = prevForceDisableInit;
            }

            // Cancel destroy timers so we have time to render.
            var timedDestructions = instance.GetComponentsInChildren<TimedDestruction>(includeInactive: true);
            for (int i = 0; i < timedDestructions.Length; i++)
            {
                var timedDestruction = timedDestructions[i];
                if (timedDestruction == null) continue;
                timedDestruction.CancelInvoke();
                timedDestruction.enabled = false;
            }

            // Pre-simulate particles so Renderer.bounds (read by camera framing
            // before the composite loop) reflect the effect's widest extent.
            var particles = instance.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < particles.Length; i++)
            {
                var ps = particles[i];
                if (ps == null) continue;
                float duration = DefaultParticleDurationSeconds;
                try
                {
                    var main = ps.main;
                    if (main.duration > 0f) duration = main.duration;
                    try
                    {
                        float life = main.startLifetime.constantMax;
                        if (life > 0f && life > duration) duration = life;
                    }
                    catch { /* curve/gradient startLifetime — skip */ }
                }
                catch { /* main accessor edge config — skip */ }
                duration = Mathf.Min(duration, MaxParticleDurationSeconds);
                try { ps.Simulate(duration, withChildren: true, restart: true); }
                catch { /* unsimulable PS — skip */ }
            }

            SetLayerRecursive(instance.transform, IconLayer);

            return instance;
#pragma warning restore CS0162
        }

        private void FrameCameraOnBounds(Bounds bounds)
        {
            Vector3 center = bounds.center;
            float size = bounds.size.magnitude;
            float distance = size * CameraDistanceFactor;

            // Three-quarter angled view matching the Unity inspector-preview look.
            Vector3 cameraDir = CameraViewDirection.normalized;
            _iconCamera.transform.position = center - cameraDir * distance;
            _iconCamera.transform.LookAt(center);

            _iconCamera.nearClipPlane = distance * NearClipDistanceFactor;
            _iconCamera.farClipPlane = distance * FarClipDistanceFactor;
        }

        // ──────────────────────────────────────────────────────────────
        //  Camera setup
        // ──────────────────────────────────────────────────────────────

        private void CreateCamera()
        {
            var cameraObject = new GameObject("VA_IconCam");
            cameraObject.transform.SetParent(transform, false);

            _iconCamera = cameraObject.AddComponent<Camera>();
            _iconCamera.clearFlags = CameraClearFlags.SolidColor;
            // Transparent background — alpha=0 so the icon renders against
            // the hammer UI panel color without an opaque rectangle.
            _iconCamera.backgroundColor = IconCameraBackground;
            _iconCamera.fieldOfView = IconCameraFieldOfView;
            _iconCamera.nearClipPlane = IconCameraNearClip;
            _iconCamera.farClipPlane = IconCameraFarClip;
            // CullingMask restricted to IconLayer so the camera only captures
            // proxy renderers (also on IconLayer), never world geometry.
            _iconCamera.cullingMask = 1 << IconLayer;
            _iconCamera.depth = IconCameraDepth;
            // Disabled by default; we drive frames explicitly via Render().
            _iconCamera.enabled = false;

            // NO Light components — a directional Light can get promoted to Unity's
            // main-light slot mid-render and bleed into world shaders regardless of
            // cullingMask. The proxy is lit via RenderSettings.ambient* in RenderIcon.

            EnsureRenderTexture();
        }

        private void EnsureRenderTexture()
        {
            if (_renderTexture != null && _renderTexture.IsCreated()) return;
            ReleaseRenderTexture();
            _renderTexture = new RenderTexture(IconSize, IconSize, IconRenderTextureDepthBits, RenderTextureFormat.ARGB32)
            {
                name = "VA_IconRT",
                antiAliasing = IconRenderTextureAntiAliasing,
            };
            _renderTexture.Create();
        }

        private void ReleaseRenderTexture()
        {
            if (_renderTexture != null)
            {
                if (_iconCamera != null && _iconCamera.targetTexture == _renderTexture)
                    _iconCamera.targetTexture = null;
                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }
        }

        private void ReleaseResources()
        {
            ReleaseRenderTexture();
            ReleasePieceTemplates();
            if (_iconCamera != null)
            {
                Destroy(_iconCamera.gameObject);
                _iconCamera = null;
            }
        }

    }
}
