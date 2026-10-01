using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using FiresCore.Lifecycle;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Sync
{
    // Orchestrates both asset-folder sync directions.
    //
    // Pull: on world load the server builds a manifest of its synced folders; each
    // connecting client sends its own manifest, receives the delta, and requests
    // the missing files one at a time.
    //
    // Admin push: an admin client streams its preset/EWP yaml or baked blueprint
    // bundles up to the server, which admin-gates and path-allowlists every write.
    public static class VAFapAssetSync
    {
        private const int FileWriteBufferBytes = 1048576;
        private const long KiB = 1024;
        private const long MiB = KiB * 1024;
        private const long GiB = MiB * 1024;

        private static MonoBehaviour _host;
        private static bool _initialized;

        private class SyncHost : MonoBehaviour { }

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            var hostObject = new GameObject("VAFapAssetSyncHost");
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<SyncHost>();

            try
            {
                var harmony = new Harmony("com.Fire.adminprefabs.assetsync.zone");
                harmony.Patch(
                    original: AccessTools.Method(typeof(ZoneSystem), "Start"),
                    postfix: new HarmonyMethod(typeof(VAFapAssetSync).GetMethod(
                        nameof(ZoneSystem_Start_Postfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                VAFapDirectPull.Install(harmony);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] Harmony patch (ZoneSystem.Start) failed: {ex.Message}");
            }
        }

        private static void ZoneSystem_Start_Postfix()
        {
            try { VAFapSyncRpcs.RegisterAll(); }
            catch (Exception ex) { Debug.LogWarning($"[FAP Sync] RPC RegisterAll failed: {ex.Message}"); }

            if (IsServer())
                _host.StartCoroutine(Server_BuildManifestCoroutine());
            else
            {
                _serverBundleHashes.Clear();
                _serverBundleIndexReceived = false;
                ResetPushState();
                Client_KickoffOnceReady();
            }
        }

        private static bool IsServer() => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool IsLocalAdmin() => FiresCore.Sync.AdminSyncing.IsLocalAdmin();

        // One in-flight streamed file. Blocks append to a .tmp and fold into a running
        // SHA, so the file is never held in memory; a successful End renames the .tmp
        // into place. Shared by the client pull and the server admin-push receive.
        private sealed class StreamedFileReceiver
        {
            private readonly string _logPrefix;
            private FileStream _stream;
            private SHA256 _hasher;
            private string _tmpPath;
            private string _expectedSha;
            private int _nextIndex;

            public string RelPath { get; private set; }
            public long BytesWritten { get; private set; }
            public bool IsActive => _stream != null;

            public StreamedFileReceiver(string logPrefix) { _logPrefix = logPrefix; }

            // Any file still open here never got its End, so its partial is dropped.
            public void Begin(string absPath, string relPath, string expectedSha)
            {
                Abort();
                try
                {
                    string dir = Path.GetDirectoryName(absPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    _tmpPath = absPath + ".tmp";
                    _stream = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                             FileWriteBufferBytes, FileOptions.SequentialScan);
                    _hasher = SHA256.Create();
                    RelPath = relPath;
                    _expectedSha = expectedSha;
                    BytesWritten = 0;
                    _nextIndex = 0;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{_logPrefix} open failed '{relPath}': {ex.Message}");
                    Abort();
                }
            }

            public void Append(string relPath, int index, byte[] bytes)
            {
                if (_stream == null) return;
                if (!string.Equals(relPath, RelPath, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"{_logPrefix} chunk for '{relPath}' but active file is '{RelPath}'; aborting.");
                    Abort();
                    return;
                }
                if (index != _nextIndex)
                {
                    Debug.LogWarning($"{_logPrefix} chunk out of order for '{relPath}' (got {index}, expected {_nextIndex}); aborting.");
                    Abort();
                    return;
                }
                if (bytes == null || bytes.Length == 0) { _nextIndex++; return; }

                try
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _hasher.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    BytesWritten += bytes.Length;
                    _nextIndex++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{_logPrefix} write failed '{relPath}': {ex.Message}");
                    Abort();
                }
            }

            // Returns true only when the SHA matched and the .tmp was renamed into place.
            // The sender's ok=false, a mismatch, or any failure discards the partial.
            public bool End(string relPath, bool senderOk, string absPath)
            {
                bool committed = false;
                try
                {
                    if (_stream != null && string.Equals(relPath, RelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        string actualSha = ToHex(_hasher.Hash);
                        _stream.Flush();
                        _stream.Dispose();
                        _stream = null;

                        bool shaOk = string.IsNullOrEmpty(_expectedSha)
                                     || string.Equals(actualSha, _expectedSha, StringComparison.OrdinalIgnoreCase);
                        if (senderOk && shaOk)
                        {
                            if (File.Exists(absPath)) File.Delete(absPath);
                            File.Move(_tmpPath, absPath);
                            committed = true;
                        }
                        else if (!shaOk)
                        {
                            Debug.LogWarning($"{_logPrefix} SHA mismatch for '{relPath}' "
                                + $"(expected {_expectedSha}, got {actualSha}) — discarding.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{_logPrefix} commit failed '{relPath}': {ex.Message}");
                }
                finally
                {
                    if (committed) Clear();
                    else Abort();
                }
                return committed;
            }

            public void Abort()
            {
                try { _stream?.Dispose(); } catch { }
                string tmp = _tmpPath;
                Clear();
                try { if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            private void Clear()
            {
                try { _hasher?.Dispose(); } catch { }
                _stream = null;
                _hasher = null;
                _tmpPath = null;
                _expectedSha = null;
                _nextIndex = 0;
                RelPath = null;
            }
        }

        private static string ToHex(byte[] hash)
        {
            if (hash == null) return null;
            var hex = new System.Text.StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) hex.Append(hash[i].ToString("x2"));
            return hex.ToString();
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < KiB) return $"{bytes} B";
            if (bytes < MiB) return $"{bytes / (double)KiB:F1} KB";
            if (bytes < GiB) return $"{bytes / (double)MiB:F1} MB";
            return $"{bytes / (double)GiB:F2} GB";
        }

        private static string RootAbsolutePath(string relPath) =>
            Path.Combine(VAFapSyncManifest.RootDir, relPath.Replace('/', Path.DirectorySeparatorChar));

        private static string ConfigAbsolutePath(string relPath) =>
            Path.Combine(Paths.ConfigPath, relPath.Replace('/', Path.DirectorySeparatorChar));

        // ================================================================
        //  Pull — server side
        // ================================================================

        private static VAFapSyncManifest.Manifest _serverManifest;
        private static readonly object _serverManifestLock = new object();

        private static readonly Dictionary<long, HashSet<string>> _serverPendingByPeer
            = new Dictionary<long, HashSet<string>>();

        private static readonly HashSet<long> _serverStreamingPeers = new HashSet<long>();

        private static IEnumerator Server_BuildManifestCoroutine()
        {
            yield return null;

            float startTime = Time.realtimeSinceStartup;
            VAFapSyncManifest.Manifest manifest = null;
            Exception buildError = null;
            yield return VAFapSyncManifest.BuildLocalManifestAsync((built, error) =>
            {
                manifest = built;
                buildError = error;
            });
            if (buildError != null || manifest == null)
            {
                Debug.LogWarning($"[FAP Sync] Server manifest build failed: {buildError?.Message ?? "no manifest produced"}");
                yield break;
            }
            lock (_serverManifestLock) _serverManifest = manifest;

            Debug.Log($"[FAP Sync] Server manifest ready: {manifest.Entries.Count} file(s) in "
                + $"{(Time.realtimeSinceStartup - startTime):F1}s.");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { VAFapThreadLog.Info($"[FAP Sync] Compression warmed off the main thread in {VAFapSyncChunker.WarmCompression():F0} ms."); }
                catch (Exception ex) { VAFapThreadLog.Warning($"[FAP Sync] Compression warm-up failed: {ex.Message}"); }
            });
        }

        // A client that connects inside ZoneSystem.Start can beat the deferred
        // build, in which case the manifest is built inline.
        private static VAFapSyncManifest.Manifest GetOrBuildServerManifest()
        {
            VAFapSyncManifest.Manifest manifest;
            lock (_serverManifestLock) manifest = _serverManifest;
            if (manifest != null) return manifest;

            manifest = VAFapSyncManifest.BuildLocalManifest();
            lock (_serverManifestLock) _serverManifest = manifest;
            return manifest;
        }

        // A client's manifest is inflated, parsed and compared with the server's on a worker, and answered from the main
        // thread once that is done. [perf] R22b: at the bot's join this froze the dedi 1,028 ms inside the handler, the
        // main thread blocked (16 ms of CPU) while the bot loaded from the same disk; Fire's client's join 230 s later
        // cost nothing, so it was a first-use cost (the first DeflateStream loads Mono's native zlib) met under contention.
        public static void Server_HandleClientManifest(long sender, byte[] raw, bool compressed)
        {
            _host.StartCoroutine(Server_ClientManifestCoroutine(sender, raw, compressed));
        }

        private static IEnumerator Server_ClientManifestCoroutine(long sender, byte[] raw, bool compressed)
        {
            VAFapSyncManifest.Manifest clientManifest = null, serverManifest = null;
            List<VAFapSyncManifest.ManifestEntry> delta = null, bakedBundles = null;
            Exception failure = null;
            double workMs = 0;
            bool finished = false;

            WaitCallback work = _ =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    clientManifest = VAFapSyncManifest.Deserialize(compressed ? VAFapSyncChunker.Inflate(raw) : raw);
                    serverManifest = GetOrBuildServerManifest();
                    delta = VAFapSyncManifest.Diff(clientManifest, serverManifest);
                    bakedBundles = BakedBundleEntries(serverManifest);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    workMs = watch.Elapsed.TotalMilliseconds;
                    Volatile.Write(ref finished, true);
                }
            };
            if (!ThreadPool.QueueUserWorkItem(work)) work(null);
            while (!Volatile.Read(ref finished)) yield return null;

            if (failure != null)
            {
                Debug.LogWarning($"[FAP Sync] Client {sender}'s manifest could not be read: {failure.Message}");
                yield break;
            }
            Debug.Log($"[FAP Sync] Client {sender} needs {delta.Count} file(s) "
                + $"(server has {serverManifest.Entries.Count}, client has {clientManifest?.Entries?.Count ?? 0}; "
                + $"worked out off the main thread in {workMs:F0} ms).");

            lock (_serverPendingByPeer)
            {
                var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in delta) if (!string.IsNullOrEmpty(entry.Path)) pending.Add(entry.Path);
                _serverPendingByPeer[sender] = pending;
            }

            // A manifest is the start of that peer's sync, so any stream guard left over
            // from a session that dropped mid-file belongs to a connection that is gone.
            _serverStreamingPeers.Remove(sender);

            var peer = VAFapSyncRpcs.GetPeerByUid(sender);
            if (peer == null)
            {
                Debug.LogWarning($"[FAP Sync] No peer found for uid {sender} when replying with delta list.");
                yield break;
            }

            _host.StartCoroutine(Server_SendDeltaAndMaybeFinish(peer, delta, bakedBundles));
        }

        // The delta only says what the client is missing. Which bakes the server itself holds is
        // what lets an admin's client push a placed bake the server lacks.
        private static List<VAFapSyncManifest.ManifestEntry> BakedBundleEntries(VAFapSyncManifest.Manifest manifest)
        {
            var bundles = new List<VAFapSyncManifest.ManifestEntry>();
            if (manifest?.Entries == null) return bundles;
            foreach (var entry in manifest.Entries)
                if (entry?.Path != null && entry.Path.EndsWith(VABakeBundle.BundleExt, StringComparison.OrdinalIgnoreCase))
                    bundles.Add(entry);
            return bundles;
        }

        // An empty delta gets TransferDone immediately, since no request loop will run.
        private static IEnumerator Server_SendDeltaAndMaybeFinish(ZNetPeer peer, List<VAFapSyncManifest.ManifestEntry> delta,
                                                                 List<VAFapSyncManifest.ManifestEntry> bakedBundles)
        {
            VAFapDirectPull.SayHello(peer);
            var index = VAFapSyncRpcs.Server_SendBakedIndex(peer, bakedBundles);
            while (index.MoveNext()) yield return index.Current;

            var send = VAFapSyncRpcs.Server_SendDeltaList(peer, delta);
            while (send.MoveNext()) yield return send.Current;

            if (delta.Count == 0)
            {
                var done = VAFapSyncRpcs.Server_SendTransferDone(peer);
                while (done.MoveNext()) yield return done.Current;
                lock (_serverPendingByPeer) _serverPendingByPeer.Remove(peer.m_uid);
            }
        }

        public static void Server_HandleFileRequest(long sender, string relPath, bool direct = false)
        {
            var peer = VAFapSyncRpcs.GetPeerByUid(sender);
            if (peer == null)
            {
                Debug.LogWarning($"[FAP Sync] FileRequest from missing peer uid {sender}; ignoring.");
                return;
            }
            if (string.IsNullOrEmpty(relPath))
            {
                Debug.LogWarning($"[FAP Sync] FileRequest from {sender} with empty path; ignoring.");
                return;
            }
            if (!IsPathWithinSyncedFolders(relPath))
            {
                Debug.LogWarning($"[FAP Sync] FileRequest from {sender} for out-of-scope path '{relPath}'; refusing.");
                VAFapSyncChunker.SendFileUnavailable(peer, relPath);
                return;
            }

            string absPath = RootAbsolutePath(relPath);
            if (!File.Exists(absPath))
            {
                // Without an End the client has nothing to advance on and sits on this
                // request until its watchdog gives up.
                Debug.LogWarning($"[FAP Sync] FileRequest from {sender} for missing file '{relPath}'; skipping.");
                VAFapSyncChunker.SendFileUnavailable(peer, relPath);
                MarkServerPendingFulfilled(peer, relPath);
                return;
            }

            // One stream per peer. The client asks for one file at a time, but a repeated
            // request would put a second stream on the same socket, where the two starve
            // each other at the drain gate and the client's single receiver keeps only
            // the last file to send a Begin.
            if (!_serverStreamingPeers.Add(sender))
            {
                Debug.LogWarning($"[FAP Sync] FileRequest from {sender} for '{relPath}' while a file is already "
                    + "streaming to that peer; ignoring the duplicate.");
                return;
            }

            _host.StartCoroutine(Server_StreamFileCoroutine(peer, relPath, absPath, direct));
        }

        private static IEnumerator Server_StreamFileCoroutine(ZNetPeer peer, string relPath, string absPath, bool direct)
        {
            string sha = LookupServerSha(relPath);
            if (string.IsNullOrEmpty(sha))
            {
                Exception hashFailure = null;
                var hashing = VAFapSyncManifest.ComputeSha256Async(absPath, (hash, error) => { sha = hash; hashFailure = error; });
                while (hashing.MoveNext()) yield return hashing.Current;
                if (hashFailure != null)
                {
                    Debug.LogWarning($"[FAP Sync] Server hash failed '{absPath}': {hashFailure.Message}");
                    _serverStreamingPeers.Remove(peer.m_uid);
                    MarkServerPendingFulfilled(peer, relPath);
                    yield break;
                }
            }

            var outcome = new VAFapSyncChunker.StreamOutcome();
            var stream = VAFapSyncChunker.StreamFileToPeer(peer, relPath, sha, absPath, outcome: outcome, direct: direct);
            while (stream.MoveNext()) yield return stream.Current;

            if (!outcome.Completed)
                Debug.LogWarning($"[FAP Sync] Serving '{relPath}' to {peer.m_uid} failed after "
                    + $"{FormatBytes(outcome.BytesSent)} — {outcome.Failure ?? "transfer failed"}. "
                    + "The client keeps the old copy and retries on its next connect.");

            _serverStreamingPeers.Remove(peer.m_uid);
            MarkServerPendingFulfilled(peer, relPath);
        }

        // The cached manifest already holds every SHA; re-hashing a bundle per request
        // would be the expensive path.
        private static string LookupServerSha(string relPath)
        {
            VAFapSyncManifest.Manifest manifest;
            lock (_serverManifestLock) manifest = _serverManifest;
            if (manifest?.Entries == null) return null;
            foreach (var entry in manifest.Entries)
                if (entry != null && string.Equals(entry.Path, relPath, StringComparison.OrdinalIgnoreCase))
                    return entry.Sha256;
            return null;
        }

        private static void MarkServerPendingFulfilled(ZNetPeer peer, string relPath)
        {
            bool nowEmpty;
            lock (_serverPendingByPeer)
            {
                if (!_serverPendingByPeer.TryGetValue(peer.m_uid, out var pending))
                    return;
                pending.Remove(relPath);
                nowEmpty = pending.Count == 0;
                if (nowEmpty) _serverPendingByPeer.Remove(peer.m_uid);
            }
            if (nowEmpty)
                _host.StartCoroutine(Server_SendTransferDone(peer));
        }

        private static IEnumerator Server_SendTransferDone(ZNetPeer peer)
        {
            var done = VAFapSyncRpcs.Server_SendTransferDone(peer);
            while (done.MoveNext()) yield return done.Current;
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"[FAP Sync] Server: TransferDone sent to {peer.m_uid}.");
        }

        private static bool IsPathWithinSyncedFolders(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            if (relPath.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            string normalized = relPath.Replace('\\', '/');
            foreach (var folder in VAFapSyncManifest.SyncedFolders)
            {
                if (normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ================================================================
        //  Pull — client side
        // ================================================================

        private const string VAGN_GUID = "com.Fire.FiresGhettoNetworkMod";
        private const float AutoTunePollIntervalSec = 2f;
        private const float AutoTuneMaxWaitSec = 120f;
        private const float AutoTuneFallbackWaitSec = 10f;
        private const float SpawnGateTimeoutSec = 600f;

        private static readonly StreamedFileReceiver _clientReceiver = new StreamedFileReceiver("[FAP Sync] Client");

        private static List<VAFapSyncManifest.ManifestEntry> _clientDelta;
        private static int _clientNextIdx;
        private static long _clientBytesReceived;
        private static int _clientFilesReceived;
        private static int _clientFilesChecked;
        private static float _clientSyncStartTime;
        private static bool _clientSyncRunning;
        private static ZNet _clientOnlyNoticeFor;

        // A file that never committed used to leave no trace at all: the receiver was
        // simply not counted and the next one was requested, so the player was told
        // "transfer complete" for a sync that had dropped files.
        private static readonly List<string> _clientFailedFiles = new List<string>();
        private static bool _clientWarnedOrphanChunks;

        private const int ClientFileMaxAttempts = 3;
        private const float ClientRetryBackoffSec = 3f;

        // Longer than the sender's own 60s stall abort, so a torn stream reports itself
        // with a real reason and this only catches a request that produced no reply at
        // all — a file missing server-side sends neither Begin nor End, which used to
        // hang the per-file loop until the session ended.
        private const float ClientFileIdleTimeoutSec = 90f;
        private const float ClientDoneGraceSec = 30f;

        private static string _clientCurrentPath;
        private static int _clientCurrentAttempt;
        private static float _clientLastActivity;
        private static bool _clientAwaitingFile;
        private static bool _clientRetryPending;
        private static bool _clientServerSaidDone;

        // ZoneSystem.Start re-fires on teleport; only one sync flow runs per session.
        private static void Client_KickoffOnceReady()
        {
            if (_clientSyncRunning) return;
            _clientSyncRunning = true;

            PlayerSpawnGate.RunWhenLocalReady(SpawnGateTimeoutSec, OnLocalPlayerReadyForSync);
        }

        private static void OnLocalPlayerReadyForSync(Player player)
        {
            // A server without FAP has no manifest to compare against and never answers; sending would only leave
            // the long network timeout on for the session.
            if (Managers.VAFapServerPresence.IsClientOnly)
            {
                // Cleared so the next connection, perhaps to a server with FAP, still syncs; a teleport re-fires this.
                _clientSyncRunning = false;
                if (!ReferenceEquals(_clientOnlyNoticeFor, ZNet.instance))
                {
                    _clientOnlyNoticeFor = ZNet.instance;
                    Debug.Log("[FAP Sync] Client: the server doesn't run FiresAdminPrefabs, so there is no asset sync this session (client-only).");
                }
                return;
            }

            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"[FAP Sync] Client: local player ready ('{player.GetPlayerName()}') — starting sync flow.");
            _host.StartCoroutine(Client_SyncFlow());
        }

        private static IEnumerator Client_SyncFlow()
        {
            yield return WaitForAutoTuneIfApplicable();

            _clientSyncStartTime = Time.realtimeSinceStartup;

            VAFapSyncManifest.Manifest localManifest = null;
            Exception localBuildError = null;
            yield return VAFapSyncManifest.BuildLocalManifestAsync((built, error) =>
            {
                localManifest = built;
                localBuildError = error;
            });
            if (localBuildError != null || localManifest == null)
            {
                Debug.LogWarning($"[FAP Sync] Client local manifest build failed: {localBuildError?.Message ?? "no manifest produced"} — aborting sync.");
                _clientSyncRunning = false;
                yield break;
            }
            _clientFilesChecked = localManifest?.Entries?.Count ?? 0;

            var serverPeer = VAFapSyncRpcs.GetServerPeer();
            if (serverPeer == null)
            {
                Debug.LogWarning("[FAP Sync] Client: no server peer found — aborting sync.");
                _clientSyncRunning = false;
                yield break;
            }

            // Same 90s keep-alive grace vanilla uses during the world download;
            // restored in Client_HandleTransferDone and on the lost-peer abort.
            SetSyncNetTimeout(true);

            var send = VAFapSyncRpcs.Client_SendManifest(serverPeer, localManifest);
            while (send.MoveNext()) yield return send.Current;

            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log("[FAP Sync] Client: manifest sent, awaiting delta from server.");
        }

        // FGN's bandwidth probe would be skewed by a concurrent transfer, so the sync
        // waits for it when FGN is installed and auto-tune is enabled.
        private static IEnumerator WaitForAutoTuneIfApplicable()
        {
            if (!Chainloader.PluginInfos.ContainsKey(VAGN_GUID))
                yield break;

            FieldInfo completedField = FindAutoTuneCompletedField();
            if (completedField == null)
            {
                Debug.LogWarning("[FAP Sync] Could not locate AutoTuneProbe._probeCompletedThisSession; "
                    + $"proceeding after {AutoTuneFallbackWaitSec:F0}s defensive wait.");
                float waited = 0f;
                while (waited < AutoTuneFallbackWaitSec) { yield return null; waited += Time.deltaTime; }
                yield break;
            }

            if (!TryReadAutoTuneEnabledConfig())
            {
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log("[FAP Sync] VAGN auto-tune disabled in config; proceeding immediately.");
                yield break;
            }

            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log("[FAP Sync] Waiting for VAGN auto-tune probe to complete (max "
                    + $"{AutoTuneMaxWaitSec:F0}s) before sending manifest...");
            float elapsed = 0f;
            while (elapsed < AutoTuneMaxWaitSec)
            {
                bool done;
                try { done = (bool)completedField.GetValue(null); }
                catch { done = false; }

                if (done)
                {
                    if (VAFapLoadSummary.VerboseEnabled)
                        Debug.Log($"[FAP Sync] VAGN auto-tune complete after {elapsed:F1}s; starting manifest exchange.");
                    yield break;
                }

                float wait = AutoTunePollIntervalSec;
                while (wait > 0f) { yield return null; wait -= Time.deltaTime; }
                elapsed += AutoTunePollIntervalSec;
            }

            Debug.LogWarning($"[FAP Sync] VAGN auto-tune did not complete within {AutoTuneMaxWaitSec:F0}s; "
                + "proceeding with sync anyway.");
        }

        // FGN's assembly is named VAGhettoNetworking but its namespace is
        // FiresGhettoNetworkMod, so the simple-name scan is the reliable lookup.
        private static FieldInfo FindAutoTuneCompletedField()
        {
            try
            {
                var assembly = Chainloader.PluginInfos[VAGN_GUID].Instance?.GetType()?.Assembly;
                if (assembly == null) return null;

                Type probeType = assembly.GetType("FiresGhettoNetworkMod.AutoTune.AutoTuneProbe", throwOnError: false)
                    ?? assembly.GetType("VAGhettoNetworking.Networking.AutoTune.AutoTuneProbe", throwOnError: false);
                if (probeType == null)
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == "AutoTuneProbe") { probeType = type; break; }
                    }
                }
                return probeType?.GetField("_probeCompletedThisSession", BindingFlags.Static | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] VAGN type lookup failed: {ex.Message}");
                return null;
            }
        }

        // Assumes enabled on any failure: waiting needlessly beats stomping the probe.
        private static bool TryReadAutoTuneEnabledConfig()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(VAGN_GUID, out var info)) return true;
                var config = info?.Instance?.Config;
                if (config == null) return true;
                foreach (var entry in config)
                {
                    if (string.Equals(entry.Key.Section, "Auto-Tune", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(entry.Key.Key, "EnableClientAutoTune", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entry.Value.BoxedValue is bool enabled) return enabled;
                    }
                }
            }
            catch { }
            return true;
        }

        public static void Client_HandleDeltaList(List<VAFapSyncManifest.ManifestEntry> delta)
        {
            // Restarting the request loop mid-transfer would put a second server stream
            // on the same socket: the two starve each other at the drain gate and the
            // second FileBegin destroys the first one's .tmp.
            if (_clientDelta != null && _clientNextIdx > 0 && _clientNextIdx < _clientDelta.Count)
            {
                Debug.LogWarning($"[FAP Sync] Client: a second delta list arrived {_clientNextIdx} file(s) into "
                    + "a transfer; ignoring it and finishing the one in progress.");
                return;
            }

            _clientDelta = delta ?? new List<VAFapSyncManifest.ManifestEntry>();
            _clientNextIdx = 0;
            _clientBytesReceived = 0;
            _clientFilesReceived = 0;
            _clientFailedFiles.Clear();
            _clientWarnedOrphanChunks = false;
            _clientCurrentPath = null;
            _clientCurrentAttempt = 0;
            _clientAwaitingFile = false;
            _clientRetryPending = false;
            _clientLastActivity = Time.realtimeSinceStartup;

            long totalBytes = 0;
            foreach (var entry in _clientDelta) totalBytes += entry?.Size ?? 0;

            // Covers the empty-delta case too: if TransferDone never arrives, the grace
            // period wraps the sync up instead of leaving it running for the session.
            EnsureClientWatchdog();

            // The server still sends TransferDone for an empty delta, so wrap-up runs normally.
            if (_clientDelta.Count == 0)
            {
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log("[FAP Sync] Client: already in sync with server; no transfer needed.");
                return;
            }

            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"[FAP Sync] Client: server has {_clientDelta.Count} file(s) "
                    + $"({FormatBytes(totalBytes)}) we need. Beginning per-file transfer.");
            RequestNextFile();
        }

        public static void Client_HandleFileBegin(string relPath, string expectedSha, long size, int chunkCount)
        {
            // A live receiver here means the previous file's End never arrived, so its
            // partial is dead either way — but say so instead of dropping it in silence.
            if (_clientReceiver.IsActive)
            {
                Debug.LogWarning($"[FAP Sync] Client: '{relPath}' began while '{_clientReceiver.RelPath}' was still "
                    + "receiving; discarding the unfinished one.");
                NoteClientFileFailed(_clientReceiver.RelPath, "superseded before it finished");
            }
            _clientReceiver.Abort();

            if (_clientDelta == null || _clientNextIdx <= 0)
            {
                Debug.LogWarning($"[FAP Sync] Client FileBegin for '{relPath}' with no pending request; ignoring.");
                return;
            }

            _clientWarnedOrphanChunks = false;
            _clientLastActivity = Time.realtimeSinceStartup;
            _clientReceiver.Begin(RootAbsolutePath(relPath), relPath, expectedSha);
            if (!_clientReceiver.IsActive)
                OnClientFileFailed(relPath, "could not be opened for writing");
        }

        public static void Client_HandleFileChunk(string relPath, int index, byte[] bytes)
        {
            // Append is a no-op once the receiver has aborted, so without this the rest
            // of a half-gigabyte bundle arrives and is thrown away without a word.
            if (!_clientReceiver.IsActive)
            {
                if (!_clientWarnedOrphanChunks)
                {
                    _clientWarnedOrphanChunks = true;
                    Debug.LogWarning($"[FAP Sync] Client: blocks for '{relPath}' are arriving with no open receiver; "
                        + "the rest of this file is being discarded.");
                }
                return;
            }

            _clientLastActivity = Time.realtimeSinceStartup;
            bool wasActive = _clientReceiver.IsActive;
            _clientReceiver.Append(relPath, index, bytes);
            if (wasActive && !_clientReceiver.IsActive)
                OnClientFileFailed(relPath, $"dropped at block {index}");
        }

        public static void Client_HandleFileEnd(string relPath, bool ok)
        {
            _clientLastActivity = Time.realtimeSinceStartup;
            long written = _clientReceiver.BytesWritten;
            bool wasActive = _clientReceiver.IsActive;
            if (_clientReceiver.End(relPath, ok, RootAbsolutePath(relPath)))
            {
                _clientBytesReceived += written;
                _clientFilesReceived++;
                _clientAwaitingFile = false;
                RequestNextFile();
                return;
            }

            if (wasActive)
            {
                OnClientFileFailed(relPath, ok ? "hash mismatch or write failure" : "the server reported a torn transfer");
            }
            else if (string.Equals(relPath, _clientCurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                // No receiver was open but this is the file we asked for — the server
                // could not serve it at all (missing, or out of scope).
                OnClientFileFailed(relPath, "the server has no copy of it");
            }
            // Otherwise it is an End for something we are not waiting on, such as a late
            // one from a superseded attempt. Advancing on it would skip a live request.
        }

        // One decision point for every way a file can fail, and idempotent: clearing
        // _clientAwaitingFile means a late End for a file already given up on is ignored
        // instead of failing it twice.
        private static void OnClientFileFailed(string relPath, string why)
        {
            if (!_clientAwaitingFile) return;
            if (!string.Equals(relPath, _clientCurrentPath, StringComparison.OrdinalIgnoreCase)) return;

            _clientAwaitingFile = false;
            _clientReceiver.Abort();

            if (_clientCurrentAttempt < ClientFileMaxAttempts && VAFapSyncRpcs.GetServerPeer() != null)
            {
                Debug.LogWarning($"[FAP Sync] Client: '{relPath}' failed ({why}); asking for it again "
                    + $"(attempt {_clientCurrentAttempt + 1} of {ClientFileMaxAttempts}) in {ClientRetryBackoffSec:F0}s.");
                _clientRetryPending = true;
                _host.StartCoroutine(ClientRetryCoroutine(relPath, _clientCurrentAttempt + 1));
                return;
            }

            NoteClientFileFailed(relPath, $"{why}, after {_clientCurrentAttempt} attempt(s)");
            RequestNextFile();
        }

        private static IEnumerator ClientRetryCoroutine(string relPath, int attempt)
        {
            yield return new WaitForSeconds(ClientRetryBackoffSec);
            _clientRetryPending = false;

            // The sync may have wrapped up or lost its peer while we waited.
            if (!_clientSyncRunning || _clientDelta == null) yield break;
            if (!SendFileRequest(relPath, attempt))
                RequestNextFile();
        }

        // Named once per file; the old copy stays on disk, so the next connect's manifest
        // diff asks for it again.
        private static void NoteClientFileFailed(string relPath, string why)
        {
            if (string.IsNullOrEmpty(relPath)) return;
            string name = relPath + " (" + why + ")";
            if (_clientFailedFiles.Contains(name)) return;
            _clientFailedFiles.Add(name);
            Debug.LogWarning($"[FAP Sync] Client: '{relPath}' did NOT sync — {why}. "
                + "The old copy is kept and it is requested again on the next connect.");
        }

        private static void SetSyncNetTimeout(bool longTimeout)
        {
            try { ZRpc.SetLongTimeout(longTimeout); }
            catch (Exception ex) { Debug.LogWarning($"[FAP Sync] SetLongTimeout({longTimeout}) failed: {ex.Message}"); }
        }

        public static void Client_HandleTransferDone()
        {
            _clientServerSaidDone = true;
            _clientLastActivity = Time.realtimeSinceStartup;

            if (_clientAwaitingFile || _clientRetryPending)
            {
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"[FAP Sync] Client: server sent TransferDone while '{_clientCurrentPath}' is still "
                        + "in flight; finishing after it settles.");
                return;
            }

            MaybeFinishClientSync();
        }

        private static void FinishClientSync()
        {
            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"[FAP Sync] Client: transfer complete — received {_clientFilesReceived} file(s) "
                    + $"({FormatBytes(_clientBytesReceived)}).");

            if (_clientReceiver.IsActive)
                NoteClientFileFailed(_clientReceiver.RelPath, "the transfer ended before this file finished");

            if (_clientFailedFiles.Count > 0)
                Debug.LogWarning($"[FAP Sync] Client: {_clientFailedFiles.Count} file(s) did NOT sync — "
                    + string.Join(", ", _clientFailedFiles.ToArray()) + ". Pieces from those bundles will be "
                    + "missing until a later connect gets them.");

            SetSyncNetTimeout(false);
            _clientReceiver.Abort();

            int filesReceived = _clientFilesReceived;
            long bytesReceived = _clientBytesReceived;
            int filesChecked = _clientFilesChecked;
            float syncSeconds = Time.realtimeSinceStartup - _clientSyncStartTime;

            _clientDelta = null;
            _clientNextIdx = 0;
            _clientBytesReceived = 0;
            _clientFilesReceived = 0;
            _clientFilesChecked = 0;
            _clientSyncRunning = false;
            _clientFailedFiles.Clear();
            _clientWarnedOrphanChunks = false;
            _clientCurrentPath = null;
            _clientCurrentAttempt = 0;
            _clientAwaitingFile = false;
            _clientRetryPending = false;
            _clientServerSaidDone = false;

            // Released here rather than by the watchdog itself, so a relogin inside its
            // one-second tick still gets a watchdog; the old one sees _clientSyncRunning
            // false on its next tick and exits.
            _clientWatchdogRunning = false;

            VAFapLoadSummary.EmitSync(filesChecked, filesReceived, FormatBytes(bytesReceived), syncSeconds);

            if (filesReceived == 0)
                return;

            // Rendered icons may reference materials the new bundles changed.
            try
            {
                int cleared = VAPieceIconRenderer.ClearCache();
                if (VAFapLoadSummary.VerboseEnabled)
                    Debug.Log($"[FAP Sync] Cleared {cleared} cached icon PNG(s) after asset update.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] ClearCache failed: {ex.Message}");
            }

            try
            {
                VAFapSyncReconnectPrompt.Show(filesReceived, bytesReceived);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] ReconnectPrompt.Show failed: {ex.Message}");
            }
        }

        private static void RequestNextFile()
        {
            if (_clientDelta == null) { MaybeFinishClientSync(); return; }
            if (_clientNextIdx >= _clientDelta.Count) { MaybeFinishClientSync(); return; }

            var entry = _clientDelta[_clientNextIdx];
            _clientNextIdx++;

            if (VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"[FAP Sync] Client: requesting [{_clientNextIdx}/{_clientDelta.Count}] '{entry.Path}' "
                    + $"({FormatBytes(entry.Size)})");

            if (!SendFileRequest(entry.Path, attempt: 1))
                return;
        }

        private static bool SendFileRequest(string relPath, int attempt)
        {
            var serverPeer = VAFapSyncRpcs.GetServerPeer();
            if (serverPeer == null)
            {
                Debug.LogWarning("[FAP Sync] Lost server peer mid-sync; aborting.");
                _clientReceiver.Abort();
                SetSyncNetTimeout(false);
                _clientDelta = null;
                _clientAwaitingFile = false;
                _clientRetryPending = false;
                _clientSyncRunning = false;
                _clientWatchdogRunning = false;
                return false;
            }

            _clientCurrentPath = relPath;
            _clientCurrentAttempt = attempt;
            _clientAwaitingFile = true;
            _clientLastActivity = Time.realtimeSinceStartup;
            EnsureClientWatchdog();

            if (attempt > 1)
                Debug.Log($"[FAP Sync] Client: re-requesting '{relPath}' (attempt {attempt} of {ClientFileMaxAttempts}).");

            if (VAFapDirectPull.ServerOffersDirect) VAFapDirectPull.Request(serverPeer, relPath);
            else _host.StartCoroutine(VAFapSyncRpcs.Client_RequestFile(serverPeer, relPath));
            return true;
        }

        // The server sends TransferDone once it has ATTEMPTED every file, so it can land
        // while a retry is still in flight. The client decides when its own sync is over.
        private static void MaybeFinishClientSync()
        {
            if (_clientAwaitingFile || _clientRetryPending) return;
            if (_clientDelta != null && _clientNextIdx < _clientDelta.Count) return;
            if (!_clientServerSaidDone) return;
            FinishClientSync();
        }

        private static void EnsureClientWatchdog()
        {
            if (_clientWatchdogRunning) return;
            _clientWatchdogRunning = true;
            _host.StartCoroutine(ClientWatchdogCoroutine());
        }

        private static bool _clientWatchdogRunning;

        // A request that produces no reply at all has no other backstop: a file that is
        // missing server-side sends neither Begin nor End, and the per-file loop only
        // advances on End, so the sync used to sit there until the session ended.
        private static IEnumerator ClientWatchdogCoroutine()
        {
            while (_clientSyncRunning)
            {
                yield return new WaitForSeconds(1f);
                if (!_clientSyncRunning) break;

                float idle = Time.realtimeSinceStartup - _clientLastActivity;

                if (_clientAwaitingFile)
                {
                    if (idle > ClientFileIdleTimeoutSec)
                        OnClientFileFailed(_clientCurrentPath, $"no reply from the server for {ClientFileIdleTimeoutSec:F0}s");
                }
                else if (!_clientRetryPending && !_clientServerSaidDone && idle > ClientDoneGraceSec
                         && (_clientDelta == null || _clientNextIdx >= _clientDelta.Count))
                {
                    Debug.LogWarning("[FAP Sync] Client: every file has been handled but the server never sent "
                        + $"TransferDone within {ClientDoneGraceSec:F0}s; wrapping up anyway.");
                    _clientServerSaidDone = true;
                    FinishClientSync();
                }
            }
        }

        // ================================================================
        //  Admin push — client side
        // ================================================================

        // Preset and EWP yaml live outside the pull RootDir, so inbound paths are
        // config-root-relative. Baked artifacts live inside it, which is what lets
        // the pull sync re-serve a pushed bundle to every other client.
        private const string DataDirName = "data";
        private const string ExpandWorldDirName = "expand_world";
        private const string BakeBundleDirRel = "FiresAdminPrefabs/Assets";
        private const string BakeManifestDirRel = "FiresAdminPrefabs/Manifests";

        // Sync manifest paths are relative to the FAP config root, so the same bundle is
        // "Assets/<name>" there and "FiresAdminPrefabs/Assets/<name>" to the push allowlist.
        private const string SyncAssetsFolder = "Assets";

        private static readonly Dictionary<string, string> _serverBundleHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _bakePushesInFlight =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _serverBundleIndexReceived;

        private const float AutoPushDebounceSec = 2f;
        private const float InterFilePauseSec = 0.1f;
        private const float PushAckWaitSec = 60f;

        private static Coroutine _autoPushDebounce;

        // The server receives one file at a time, keyed by a single _inboundSender.
        // Two pushes overlapping tore each other's transfer apart and left the server
        // silently discarding everything, so every trigger queues behind one runner.
        private sealed class PushJob
        {
            public List<(string rel, string abs)> Files;
            public Action<string> Log;
            public string BakeSyncPath;
            public string BakeSha;
        }

        private static readonly Queue<PushJob> _pushQueue = new Queue<PushJob>();
        private static bool _pushRunnerActive;
        private static bool _pushAbortRequested;

        // The ack belongs to the job that is running right now. A shared "awaiting ack"
        // map would let one job's ack mark a queued bundle as delivered. _pushDoneSent
        // separates the server's final verdict from a mid-transfer refusal, so only the
        // verdict can retire a job.
        private static PushJob _pushAwaitingAck;
        private static bool _pushDoneSent;

        public static void PushPresetsNow(Action<string> log)
        {
            if (!TryGetPushTarget(log, "FAP data", out _)) return;

            var files = CollectPushFiles();
            if (files.Count == 0) { Emit(log, "[FAP Push] no FAP preset/EWP yaml found to push."); return; }

            EnqueuePush(new PushJob { Files = files, Log = log });
        }

        private static void EnqueuePush(PushJob job)
        {
            _pushQueue.Enqueue(job);
            if (_pushRunnerActive)
            {
                Emit(job.Log, $"[FAP Push] queued behind the push already running ({_pushQueue.Count} waiting).");
                return;
            }
            _pushRunnerActive = true;
            _host.StartCoroutine(Client_PushRunner());
        }

        private static IEnumerator Client_PushRunner()
        {
            while (_pushQueue.Count > 0)
            {
                var job = _pushQueue.Dequeue();
                var serverPeer = VAFapSyncRpcs.GetServerPeer();
                if (serverPeer == null)
                {
                    Emit(job.Log, "[FAP Push] lost the server connection — push cancelled.");
                }
                else
                {
                    _pushAwaitingAck = job;
                    var run = Client_PushCoroutine(serverPeer, job.Files, job.Log);
                    while (run.MoveNext()) yield return run.Current;

                    // The server is single-stream and only frees its receiver at Done,
                    // so the next job waits for the verdict. Releasing the placement
                    // guard before the ack would also let a third placement re-push a
                    // bundle the server is in the middle of confirming.
                    float ackDeadline = Time.realtimeSinceStartup + PushAckWaitSec;
                    while (_pushAwaitingAck == job && Time.realtimeSinceStartup < ackDeadline
                           && VAFapSyncRpcs.GetServerPeer() != null)
                        yield return null;

                    if (_pushAwaitingAck == job)
                    {
                        _pushAwaitingAck = null;
                        _pushDoneSent = false;
                        Emit(job.Log, $"[FAP Push] no confirmation from the server within {PushAckWaitSec:F0}s — "
                            + "treating the push as unconfirmed.");
                    }
                }
                if (job.BakeSyncPath != null) _bakePushesInFlight.Remove(job.BakeSyncPath);
            }
            _pushRunnerActive = false;
        }

        // A relogin builds a fresh ZRoutedRpc and a fresh server peer, so anything left
        // queued is aimed at a connection that is gone. A live runner is left alone:
        // ZoneSystem.Start also re-fires without a reconnect, and a runner whose socket
        // really did die ends itself on the next block and drains its own queue.
        private static void ResetPushState()
        {
            if (_pushRunnerActive) return;
            _pushQueue.Clear();
            _pushAbortRequested = false;
            _pushAwaitingAck = null;
            _pushDoneSent = false;
            _bakePushesInFlight.Clear();
        }

        // Each Assets/<name>.bakebundle travels with its Manifests/<name>.bakemanifest
        // sidecar. The server registers the piece on its next world load.
        public static void PushBakedBundlesNow(Action<string> log, string nameFilter)
            => PushBakedBundles(log, nameFilter, exactMatch: false, bakeSyncPath: null, bakeSha: null);

        private static void PushBakedBundles(Action<string> log, string nameFilter, bool exactMatch,
                                             string bakeSyncPath, string bakeSha)
        {
            if (!TryGetPushTarget(log, "baked bundles", out _))
            {
                if (bakeSyncPath != null) _bakePushesInFlight.Remove(bakeSyncPath);
                return;
            }

            var files = CollectBakedBundleFiles(nameFilter, exactMatch, log);
            if (files.Count == 0)
            {
                Emit(log, string.IsNullOrEmpty(nameFilter)
                    ? "[FAP Push] no baked bundles in Assets/ — bake a blueprint first."
                    : $"[FAP Push] no baked bundle matched '{nameFilter}'.");
                if (bakeSyncPath != null) _bakePushesInFlight.Remove(bakeSyncPath);
                return;
            }

            long totalBytes = 0;
            foreach (var entry in files)
            {
                try { totalBytes += new FileInfo(entry.abs).Length; } catch { }
            }
            Emit(log, $"[FAP Push] {files.Count} baked file(s), {FormatBytes(totalBytes)} total — large bundles take a while.");

            EnqueuePush(new PushJob { Files = files, Log = log, BakeSyncPath = bakeSyncPath, BakeSha = bakeSha });
        }

        private static bool TryGetPushTarget(Action<string> log, string what, out ZNetPeer serverPeer)
        {
            serverPeer = null;
            if (_host == null) { Emit(log, "[FAP Push] sync host not ready — load into a world first."); return false; }
            if (IsServer()) { Emit(log, $"[FAP Push] you are the server/host — {what} already on this machine; nothing to push."); return false; }

            serverPeer = VAFapSyncRpcs.GetServerPeer();
            if (serverPeer == null) { Emit(log, "[FAP Push] not connected to a server — nothing to push."); return false; }
            if (Managers.VAFapServerPresence.IsClientOnly) { Emit(log, $"[FAP Push] the server doesn't run FiresAdminPrefabs, so there is nowhere to push {what} (client-only)."); return false; }
            if (!IsLocalAdmin()) { Emit(log, $"[FAP Push] admin required to push {what} to the server."); return false; }
            return true;
        }

        // Called by the preset stores after a local write; a burst of writes
        // coalesces into one push.
        public static void RequestAutoPush()
        {
            try
            {
                if (_host == null || IsServer()) return;
                if (VAFapSyncRpcs.GetServerPeer() == null) return;
                if (Managers.VAFapServerPresence.IsClientOnly) return;
                if (!AutoPushEnabled() || !IsLocalAdmin()) return;

                if (_autoPushDebounce != null) _host.StopCoroutine(_autoPushDebounce);
                _autoPushDebounce = _host.StartCoroutine(AutoPushDebounceCoroutine());
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP Push] RequestAutoPush failed: {ex.Message}"); }
        }

        private static IEnumerator AutoPushDebounceCoroutine()
        {
            yield return new WaitForSeconds(AutoPushDebounceSec);
            _autoPushDebounce = null;
            if (IsServer() || VAFapSyncRpcs.GetServerPeer() == null) yield break;
            Debug.Log("[FAP Push] auto-push (debounced): sending FAP preset/EWP yaml to server.");
            PushPresetsNow(null);
        }

        // A file that tore mid-stream must not be counted as sent: the server would
        // then ack the batch as applied and the client would cache the bundle as
        // delivered, which is why the wedged Phalanx push was never retried.
        private static IEnumerator Client_PushCoroutine(ZNetPeer serverPeer, List<(string rel, string abs)> files, Action<string> log)
        {
            Emit(log, $"[FAP Push] pushing {files.Count} FAP file(s) to server...");
            _pushAbortRequested = false;
            _pushDoneSent = false;

            int sent = 0;
            var failures = new List<string>();
            var outcome = new VAFapSyncChunker.StreamOutcome();
            Func<bool> cancelled = () => _pushAbortRequested;

            foreach (var entry in files)
            {
                if (_pushAbortRequested) { failures.Add(entry.rel + " (server stopped the push)"); continue; }

                string sha = null;
                Exception hashFailure = null;
                var hashing = VAFapSyncManifest.ComputeSha256Async(entry.abs, (hash, error) => { sha = hash; hashFailure = error; });
                while (hashing.MoveNext()) yield return hashing.Current;
                if (hashFailure != null)
                {
                    Emit(log, $"[FAP Push] skip '{entry.rel}': {hashFailure.Message}");
                    failures.Add(entry.rel + " (hash failed)");
                    continue;
                }

                var send = VAFapSyncRpcs.Client_StreamAdminFile(serverPeer, entry.rel, sha, entry.abs, outcome, cancelled);
                while (send.MoveNext()) yield return send.Current;

                if (outcome.Completed) sent++;
                else
                {
                    failures.Add($"{entry.rel} ({outcome.Failure ?? "transfer failed"})");
                    Emit(log, $"[FAP Push] '{entry.rel}' FAILED after {FormatBytes(outcome.BytesSent)} — "
                        + $"{outcome.Failure ?? "transfer failed"}. Nothing was stored; push it again with fap_push_bundle.");
                }
                yield return new WaitForSeconds(InterFilePauseSec);
            }

            var done = VAFapSyncRpcs.Client_SendAdminPushDone(serverPeer, sent);
            while (done.MoveNext()) yield return done.Current;
            _pushDoneSent = true;

            if (failures.Count == 0)
                Emit(log, $"[FAP Push] sent {sent} file(s); awaiting server confirmation.");
            else
                Emit(log, $"[FAP Push] sent {sent} file(s), {failures.Count} FAILED: {string.Join(", ", failures)}");
        }

        // The ack lands a frame after the console command returned, so it goes to the HUD.
        public static void Client_HandleAdminPushAck(bool ok, string message)
        {
            string line = ok
                ? $"[FAP Push] server applied push: {message}"
                : $"[FAP Push] server REJECTED push: {message}";
            Debug.Log(line);
            try { Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, line); } catch { }

            // Any refusal kills the transfer in progress — the server discards what
            // follows, so streaming the rest is hundreds of MB straight into a void.
            if (!ok) _pushAbortRequested = true;

            // Before Done this is a mid-transfer refusal, not the verdict on the batch.
            if (!_pushDoneSent) return;
            _pushDoneSent = false;

            // A rejected bake stays unconfirmed, so placing it again retries.
            var job = _pushAwaitingAck;
            _pushAwaitingAck = null;
            if (ok && job?.BakeSyncPath != null && job.BakeSha != null)
                _serverBundleHashes[job.BakeSyncPath] = job.BakeSha;
        }

        // ================================================================
        //  Admin push — baked pieces placed on a server that lacks them
        // ================================================================

        // Until a bake is pushed it exists only on the admin who made it. A piece placed from it is
        // then invisible to every other player, whose client has no prefab to build it from, and
        // nothing tells the admin. Placing one is the moment that stops being hypothetical.
        public static void Client_HandleBakedIndex(List<VAFapSyncManifest.ManifestEntry> bundles)
        {
            _serverBundleHashes.Clear();
            if (bundles != null)
                foreach (var entry in bundles)
                    if (!string.IsNullOrEmpty(entry?.Path)) _serverBundleHashes[entry.Path] = entry.Sha256;
            _serverBundleIndexReceived = true;
        }

        public static void Client_PushPlacedBakeIfServerLacksIt(string prefabName)
        {
            try
            {
                if (_host == null || IsServer()) return;
                if (!_serverBundleIndexReceived || VAFapSyncRpcs.GetServerPeer() == null) return;
                if (!AutoPushEnabled() || !IsLocalAdmin()) return;

                string cacheKey = VABakeBundle.TryGetCacheKeyForPrefab(prefabName);
                if (string.IsNullOrEmpty(cacheKey)) return;

                string bundlePath = VABakeBundle.BundlePathFor(cacheKey);
                string syncPath = SyncAssetsFolder + "/" + Path.GetFileName(bundlePath);
                if (!_bakePushesInFlight.Add(syncPath)) return;

                _host.StartCoroutine(Client_PushBakeIfServerDiffersCoroutine(prefabName, bundlePath, syncPath));
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP Push] checking placed bake '{prefabName}' failed: {ex.Message}"); }
        }

        // Hashed on a worker: a bake can be hundreds of MB, and a rebake changes the hash the
        // join-time manifest recorded. A different hash means the server holds an older bake.
        private static IEnumerator Client_PushBakeIfServerDiffersCoroutine(string prefabName, string bundlePath, string syncPath)
        {
            VAFapSyncManifest.Manifest local = null;
            yield return VAFapSyncManifest.BuildLocalManifestAsync((built, error) => local = built);

            string localSha = null;
            if (local?.Entries != null)
                foreach (var entry in local.Entries)
                    if (string.Equals(entry.Path, syncPath, StringComparison.OrdinalIgnoreCase)) { localSha = entry.Sha256; break; }

            _serverBundleHashes.TryGetValue(syncPath, out string serverSha);
            if (string.IsNullOrEmpty(localSha) || string.Equals(localSha, serverSha, StringComparison.OrdinalIgnoreCase))
            {
                _bakePushesInFlight.Remove(syncPath);
                yield break;
            }

            string line = serverSha == null
                ? $"[FAP Push] '{prefabName}' isn't on the server yet — sending it. Other players see it after the server restarts."
                : $"[FAP Push] the server has an older bake of '{prefabName}' — sending this one. Other players see it after the server restarts.";
            Debug.Log(line);
            try { Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, line); } catch { }

            // The guard is released by the push runner when the transfer actually ends.
            // Clearing it here let a second placement start a second stream of the same
            // half-gigabyte bundle, and two streams on one socket wedged both of them.
            PushBakedBundles(null, Path.GetFileNameWithoutExtension(bundlePath), exactMatch: true,
                             bakeSyncPath: syncPath, bakeSha: localSha);
        }

        // Data yaml first: rule yaml references presets by name.
        private static List<(string rel, string abs)> CollectPushFiles()
        {
            var files = new List<(string rel, string abs)>();
            AddGlob(files, DataDirName, "data_FAP*.yaml");
            AddGlob(files, ExpandWorldDirName, "expand_prefabs_FAP*.yaml");
            return files;
        }

        // Bundle first: RegisterAll keys off the bundle and treats a missing sidecar
        // as "no cache key", so a torn transfer degrades instead of pairing a bundle
        // with a stale manifest.
        private static List<(string rel, string abs)> CollectBakedBundleFiles(string nameFilter, bool exactMatch, Action<string> log)
        {
            var files = new List<(string rel, string abs)>();
            string bundleExt = VABakeBundle.BundleExt;
            string manifestExt = VABakeBundle.ManifestExt;

            string[] bundles;
            try { bundles = Directory.GetFiles(VABakeBundle.AssetsDir, "*" + bundleExt, SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                Emit(log, $"[FAP Push] cannot read Assets/: {ex.Message}");
                return files;
            }

            foreach (string bundlePath in bundles)
            {
                string baseName = Path.GetFileNameWithoutExtension(bundlePath);
                if (!string.IsNullOrEmpty(nameFilter))
                {
                    bool matches = exactMatch
                        ? string.Equals(baseName, nameFilter, StringComparison.OrdinalIgnoreCase)
                        : baseName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matches) continue;
                }

                files.Add((BakeBundleDirRel + "/" + Path.GetFileName(bundlePath), bundlePath));

                string sidecarPath = Path.Combine(VABakeBundle.ManifestsDir, baseName + manifestExt);
                if (File.Exists(sidecarPath))
                    files.Add((BakeManifestDirRel + "/" + Path.GetFileName(sidecarPath), sidecarPath));
                else
                    Emit(log, $"[FAP Push] '{baseName}' has no {manifestExt} sidecar — pushing the bundle alone.");
            }
            return files;
        }

        private static void AddGlob(List<(string rel, string abs)> files, string subDir, string pattern)
        {
            try
            {
                string dir = Path.Combine(Paths.ConfigPath, subDir);
                if (!Directory.Exists(dir)) return;
                foreach (var path in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    files.Add((subDir + "/" + Path.GetFileName(path), path));
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP Push] enumerate '{subDir}' failed: {ex.Message}"); }
        }

        private static void Emit(Action<string> log, string message)
        {
            log?.Invoke(message);
            Debug.Log(message);
        }

        private static bool AutoPushEnabled()
        {
            try { return VerdantsAscent.Managers.ConfigManager.Instance?.configAutoPushOnPlace?.Value ?? false; }
            catch { return false; }
        }

        // ================================================================
        //  Admin push — server side
        // ================================================================

        private static readonly StreamedFileReceiver _inboundReceiver = new StreamedFileReceiver("[FAP Push] inbound");

        // A second admin starting a file mid-transfer would interleave blocks into
        // the same .tmp, so only the sender that began the file may continue it.
        private static long _inboundSender;
        private static int _pendingBakedWrites;

        // Nothing in the protocol distinguished "still arriving" from "abandoned",
        // so the receiver could neither refuse an overlapping push nor recover from
        // a sender that vanished mid-file. Blocks arrive every second or two even on
        // the slowest link, so this much silence means the sender is gone.
        private const float InboundIdleAbandonSec = 30f;

        private static float _inboundLastActivity = float.NegativeInfinity;
        private static long _inboundSessionSender;
        private static int _inboundCommitted;
        private static int _inboundFailed;

        public static void Server_HandleAdminPushBegin(long sender, string relPath, string sha256, long size, int chunkCount)
        {
            if (!IsServer()) return;

            if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender))
            {
                Debug.LogWarning($"[FAP Push] non-admin {sender} attempted to push '{relPath}'; refusing.");
                _host.StartCoroutine(Server_AckCoroutine(sender, false, "admin required"));
                return;
            }
            if (!IsAllowedInboundPath(relPath))
            {
                Debug.LogWarning($"[FAP Push] {sender} pushed out-of-scope path '{relPath}'; refusing.");
                _host.StartCoroutine(Server_AckCoroutine(sender, false, $"'{relPath}' is not a path this server accepts"));
                return;
            }

            bool abandoned = Time.time - _inboundLastActivity > InboundIdleAbandonSec;
            if (_inboundReceiver.IsActive && !abandoned)
            {
                string who = _inboundSender == sender ? "your own" : $"{_inboundSender}'s";
                Debug.LogWarning($"[FAP Push] {sender} began '{relPath}' while {who} push of "
                    + $"'{_inboundReceiver.RelPath}' is still streaming; refusing.");
                _host.StartCoroutine(Server_AckCoroutine(sender, false,
                    $"a push of '{_inboundReceiver.RelPath}' is already in progress — wait for it to finish"));
                return;
            }
            if (_inboundReceiver.IsActive)
                Debug.LogWarning($"[FAP Push] dropping abandoned inbound '{_inboundReceiver.RelPath}' "
                    + $"(no blocks for {InboundIdleAbandonSec:F0}s).");

            if (sender != _inboundSessionSender || abandoned)
            {
                _inboundSessionSender = sender;
                _inboundCommitted = 0;
                _inboundFailed = 0;
                _pendingBakedWrites = 0;
            }

            _inboundReceiver.Begin(ConfigAbsolutePath(relPath), relPath, sha256);
            if (_inboundReceiver.IsActive)
            {
                _inboundSender = sender;
                _inboundLastActivity = Time.time;
                Debug.Log($"[FAP Push] receiving '{relPath}' ({FormatBytes(size)}, {chunkCount} block(s)) from admin {sender}.");
            }
            else
            {
                _inboundFailed++;
                _host.StartCoroutine(Server_AckCoroutine(sender, false, $"could not open '{relPath}' for writing"));
            }
        }

        public static void Server_HandleAdminPushChunk(long sender, string relPath, int index, byte[] bytes)
        {
            if (!IsServer() || !_inboundReceiver.IsActive) return;
            if (sender != _inboundSender) return;

            _inboundLastActivity = Time.time;
            _inboundReceiver.Append(relPath, index, bytes);

            // Append aborts on an out-of-order block or a write failure, and from then
            // on every remaining block is dropped without a word. That is how 680 MB
            // went into a receiver that had already given up.
            if (!_inboundReceiver.IsActive)
            {
                _inboundFailed++;
                Debug.LogWarning($"[FAP Push] inbound '{relPath}' aborted at block {index}; telling {sender} to stop.");
                _host.StartCoroutine(Server_AckCoroutine(sender, false,
                    $"'{relPath}' was dropped mid-transfer and nothing was stored"));
            }
        }

        public static void Server_HandleAdminPushEnd(long sender, string relPath, bool ok)
        {
            if (!IsServer()) return;
            if (!_inboundReceiver.IsActive || sender != _inboundSender) return;

            _inboundLastActivity = Time.time;
            long written = _inboundReceiver.BytesWritten;
            if (_inboundReceiver.End(relPath, ok, ConfigAbsolutePath(relPath)))
            {
                _inboundCommitted++;
                if (IsBakedArtifactPath(relPath)) _pendingBakedWrites++;
                Debug.Log($"[FAP Push] wrote pushed file '{relPath}' ({FormatBytes(written)}) from admin {sender}.");
            }
            else
            {
                _inboundFailed++;
                Debug.LogWarning($"[FAP Push] inbound '{relPath}' did not commit "
                    + $"({(ok ? "hash mismatch or write failure" : "sender reported a torn transfer")}).");
            }
        }

        public static void Server_HandleAdminPushDone(long sender, int count)
        {
            if (!IsServer()) return;
            if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender)) return;

            int committed = _inboundCommitted;
            int failed = _inboundFailed;
            _inboundCommitted = 0;
            _inboundFailed = 0;
            _inboundSessionSender = 0;

            // A batch where nothing landed used to fall through to the preset reload
            // and ack "applied", so the client cached the bundle as delivered and
            // never pushed it again.
            if (failed > 0 || committed != count || count <= 0)
            {
                _pendingBakedWrites = 0;
                string why = $"{committed} of {count} file(s) stored, {failed} failed — nothing partial was kept";
                Debug.LogWarning($"[FAP Push] admin {sender} push incomplete: {why}.");
                _host.StartCoroutine(Server_AckCoroutine(sender, false, why));
                return;
            }

            int bakedWrites = _pendingBakedWrites;
            _pendingBakedWrites = 0;
            if (bakedWrites > 0)
            {
                // Without this the next client would diff against the pre-push hash
                // snapshot and never download the new bundle.
                VAFapSyncManifest.InvalidateCache();
                string bakedNote = $"{bakedWrites} baked file(s) stored — clients pull them on connect, "
                    + "the server registers the piece on its next world load";
                Debug.Log($"[FAP Push] {bakedNote}. Sync manifest cache invalidated.");
                if (bakedWrites == count)
                {
                    _host.StartCoroutine(Server_AckCoroutine(sender, true, bakedNote));
                    return;
                }
            }

            // EWP's own file watcher picks up the rule yaml; only FAP's stores need a reload.
            string summary;
            try
            {
                VAMonsterPresetStore.LoadAll();
                VAChestPresetStore.LoadAll();
                summary = $"{count} file(s); {VAMonsterPresetStore.Count} monster + "
                    + $"{VAChestPresetStore.Count} chest preset(s) live";
                Debug.Log($"[FAP Push] admin {sender} push complete — {summary}. EWP reloads the rule yaml via its watcher.");
            }
            catch (Exception ex)
            {
                summary = $"applied {count} file(s) but reload threw: {ex.Message}";
                Debug.LogWarning($"[FAP Push] post-push reload failed: {ex.Message}");
            }

            _host.StartCoroutine(Server_AckCoroutine(sender, true, summary));
        }

        private static IEnumerator Server_AckCoroutine(long sender, bool ok, string message)
        {
            var peer = VAFapSyncRpcs.GetPeerByUid(sender);
            if (peer == null) yield break;
            var ack = VAFapSyncRpcs.Server_SendAdminPushAck(peer, ok, message);
            while (ack.MoveNext()) yield return ack.Current;
        }

        // Config-root-relative write allowlist. Only these four dir+name shapes can
        // ever be written by a push.
        private static bool IsAllowedInboundPath(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            if (relPath.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (relPath.IndexOf(':') >= 0) return false;
            string normalized = relPath.Replace('\\', '/');
            if (normalized[0] == '/') return false;
            int slash = normalized.LastIndexOf('/');
            if (slash <= 0) return false;
            string dir = normalized.Substring(0, slash);
            string file = normalized.Substring(slash + 1);
            if (file.Length == 0) return false;

            if (DirectoryIs(dir, DataDirName))
                return file.StartsWith("data_FAP", StringComparison.Ordinal) && HasExtension(file, ".yaml");
            if (DirectoryIs(dir, ExpandWorldDirName))
                return file.StartsWith("expand_prefabs_FAP", StringComparison.Ordinal) && HasExtension(file, ".yaml");
            if (DirectoryIs(dir, BakeBundleDirRel))
                return HasExtension(file, VABakeBundle.BundleExt);
            if (DirectoryIs(dir, BakeManifestDirRel))
                return HasExtension(file, VABakeBundle.ManifestExt);
            return false;
        }

        private static bool DirectoryIs(string dir, string expected) =>
            string.Equals(dir, expected, StringComparison.OrdinalIgnoreCase);

        private static bool HasExtension(string file, string ext) =>
            file.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

        private static bool IsBakedArtifactPath(string relPath)
        {
            string normalized = (relPath ?? string.Empty).Replace('\\', '/');
            return normalized.StartsWith(BakeBundleDirRel + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(BakeManifestDirRel + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
