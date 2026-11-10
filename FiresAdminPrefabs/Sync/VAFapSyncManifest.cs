using BepInEx;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Sync
{
    // Server↔client asset-folder manifest: one (path, size, mtime, SHA256) entry per
    // file under the synced folders. SHA256 is the only equality test — size and
    // mtime just decide whether a cached hash can be reused instead of re-reading
    // the file. Paths are forward-slash so Linux servers diff cleanly against
    // Windows clients.
    //
    // HammerTabs/ and IconCache/ are per-user state and are deliberately not synced.
    public static class VAFapSyncManifest
    {
        // Adding a folder here is the whole opt-in; nothing ever deletes client files.
        public static readonly string[] SyncedFolders = new[]
        {
            "Assets",
            "Manifests",
            "Shaders",
            "Textures",
        };

        private const int ManifestFormatVersion = 1;
        private const int HashReadBufferBytes = 1048576;
        private const string CacheFileName = ".sync_hashes.json";

        public static string RootDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs");

        // Lives at the root, outside every synced folder, so it never syncs itself.
        public static string CacheFilePath =>
            Path.Combine(RootDir, CacheFileName);

        // 1.1.254: a synced folder can live outside RootDir. Wire paths stay "<Folder>/..." on every side; only the local
        // directory behind a folder changes. Manifests now sits in FAP's cache root (out of config, Fire 10-14).
        public static string FolderPath(string folder) =>
            string.Equals(folder, VerdantsAscent.Utilities.VAFapCachePaths.ManifestsFolderName, StringComparison.Ordinal)
                ? VerdantsAscent.Utilities.VAFapCachePaths.ManifestsDir
                : Path.Combine(RootDir, folder);

        // "<Folder>/rest" -> absolute path, through FolderPath. Wire paths come from other peers, so a path is accepted only
        // when it names a file INSIDE one of the synced folders: no rooted path, drive or stream colon, "." / ".." segment,
        // empty segment, control character or over-long path, and the resolved full path must still lie under that folder.
        // Anything else returns null; every caller treats null as "refuse".
        private const int MaxRelPathLength = 240;

        public static string AbsolutePath(string relPath)
        {
            if (!IsSafeRelPath(relPath, out string folder, out string rest)) return null;
            string baseDir = Path.GetFullPath(FolderPath(folder));
            string full;
            try { full = Path.GetFullPath(Path.Combine(baseDir, rest.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception) { return null; }
            string root = baseDir.EndsWith(Path.DirectorySeparatorChar.ToString()) ? baseDir : baseDir + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        // "<SyncedFolder>/<one or more clean segments>", forward or back slashes.
        public static bool IsSafeRelPath(string relPath, out string folder, out string rest)
        {
            folder = rest = null;
            if (string.IsNullOrEmpty(relPath) || relPath.Length > MaxRelPathLength) return false;
            string rel = relPath.Replace('\\', '/');
            if (rel.StartsWith("/", StringComparison.Ordinal) || rel.IndexOf(':') >= 0 || Path.IsPathRooted(rel)) return false;
            foreach (char ch in rel) if (ch < ' ' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|') return false;
            string[] parts = rel.Split('/');
            if (parts.Length < 2) return false;
            foreach (var part in parts) if (part.Length == 0 || part == "." || part == ".." || part.Trim().Length == 0) return false;
            foreach (var f in SyncedFolders)
                if (string.Equals(f, parts[0], StringComparison.Ordinal))
                {
                    folder = f;
                    rest = rel.Substring(f.Length + 1);
                    return true;
                }
            return false;
        }

        // Absolute path -> "<Folder>/rest" when it sits inside a synced folder, else null.
        public static string RelativePath(string absPath)
        {
            string full = Path.GetFullPath(absPath);
            foreach (var f in SyncedFolders)
            {
                string prefix = Path.GetFullPath(FolderPath(f)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return f + "/" + full.Substring(prefix.Length).Replace('\\', '/');
            }
            return null;
        }

        private static Dictionary<string, ManifestEntry> _cache;
        private static bool _cacheLoaded;
        private static bool _cacheDirty;

        // The build mutates the shared hash cache, so only one may run at a time whichever
        // thread asked for it.
        private static readonly object _buildLock = new object();

        [Serializable]
        public class ManifestEntry
        {
            public string Path;
            public long Size;
            public long MtimeTicks;
            public string Sha256;
        }

        [Serializable]
        public class Manifest
        {
            public int Version = ManifestFormatVersion;
            public List<ManifestEntry> Entries = new List<ManifestEntry>();
        }

        // Hashing the synced folders reads every byte of every bundle. A re-bake changes all of
        // them at once, so the cache reuses nothing and the build ran 14.5s on the main thread —
        // a hard stall at world load. Nothing in the build touches a Unity API, so it runs on a
        // worker and the caller yields; logging goes through VAFapThreadLog for the same reason.
        public static IEnumerator BuildLocalManifestAsync(Action<Manifest, Exception> onComplete)
        {
            Manifest built = null;
            Exception failure = null;
            bool finished = false;

            if (!ThreadPool.QueueUserWorkItem(_ =>
            {
                try { built = BuildLocalManifest(); }
                catch (Exception ex) { failure = ex; }
                finally { Volatile.Write(ref finished, true); }
            }))
            {
                // No worker available: fall back to the blocking build rather than never finishing.
                try { built = BuildLocalManifest(); }
                catch (Exception ex) { failure = ex; }
                Volatile.Write(ref finished, true);
            }

            while (!Volatile.Read(ref finished)) yield return null;

            onComplete?.Invoke(built, failure);
        }

        public static Manifest BuildLocalManifest()
        {
            lock (_buildLock) return BuildLocalManifestCore();
        }

        // Walks every synced folder, reusing cached hashes for files whose size and
        // mtime are unchanged. Unreadable files are skipped, not fatal.
        private static Manifest BuildLocalManifestCore()
        {
            LoadCacheIfNeeded();

            var manifest = new Manifest();
            string root = RootDir;
            if (!Directory.Exists(root))
            {
                VAFapThreadLog.Info($"[FAP Sync] RootDir '{root}' does not exist — manifest is empty.");
                return manifest;
            }

            int reused = 0;
            int rehashed = 0;
            foreach (var folder in SyncedFolders)
            {
                string folderPath = FolderPath(folder);
                if (!Directory.Exists(folderPath)) continue;

                foreach (var path in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (IsReparsePoint(path)) continue;

                        var fileInfo = new FileInfo(path);
                        long size = fileInfo.Length;
                        long mtime = fileInfo.LastWriteTimeUtc.Ticks;
                        string relPath = folder + "/" + path.Substring(folderPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

                        string sha;
                        if (TryGetCachedHash(relPath, size, mtime, out sha))
                        {
                            reused++;
                        }
                        else
                        {
                            sha = ComputeSha256(path);
                            rehashed++;
                            _cache[relPath] = new ManifestEntry { Path = relPath, Size = size, MtimeTicks = mtime, Sha256 = sha };
                            _cacheDirty = true;
                        }

                        manifest.Entries.Add(new ManifestEntry { Path = relPath, Size = size, MtimeTicks = mtime, Sha256 = sha });
                    }
                    catch (Exception ex)
                    {
                        VAFapThreadLog.Warning($"[FAP Sync] Skipping '{path}' during manifest build: {ex.Message}");
                    }
                }
            }

            int pruned = PruneCacheEntriesMissingFrom(manifest);
            if (_cacheDirty) SaveCache();

            VAFapThreadLog.Info($"[FAP Sync] BuildLocalManifest — {manifest.Entries.Count} file(s) "
                + $"({reused} cached, {rehashed} re-hashed, {pruned} stale entries pruned).");

            return manifest;
        }

        // Symlinks and junctions could lead the scan outside the synced folders.
        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static bool TryGetCachedHash(string relPath, long size, long mtime, out string sha)
        {
            sha = null;
            if (!_cache.TryGetValue(relPath, out var cached)) return false;
            if (cached.Size != size || cached.MtimeTicks != mtime) return false;
            if (string.IsNullOrEmpty(cached.Sha256)) return false;
            sha = cached.Sha256;
            return true;
        }

        private static int PruneCacheEntriesMissingFrom(Manifest manifest)
        {
            var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries) livePaths.Add(entry.Path);

            var stale = new List<string>();
            foreach (var cachedPath in _cache.Keys)
                if (!livePaths.Contains(cachedPath)) stale.Add(cachedPath);

            foreach (var cachedPath in stale)
            {
                _cache.Remove(cachedPath);
                _cacheDirty = true;
            }
            return stale.Count;
        }

        // Entries the client must fetch: missing locally, or present with a different
        // SHA. Files only the client has are never touched — sync is additive.
        public static List<ManifestEntry> Diff(Manifest client, Manifest server)
        {
            var clientByPath = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            if (client?.Entries != null)
                foreach (var entry in client.Entries) clientByPath[entry.Path] = entry;

            var needed = new List<ManifestEntry>();
            if (server?.Entries == null) return needed;

            foreach (var serverEntry in server.Entries)
            {
                if (serverEntry == null || string.IsNullOrEmpty(serverEntry.Path) || string.IsNullOrEmpty(serverEntry.Sha256))
                    continue;

                if (!clientByPath.TryGetValue(serverEntry.Path, out var clientEntry)
                    || !string.Equals(clientEntry.Sha256, serverEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    needed.Add(serverEntry);
                }
            }
            return needed;
        }

        // One file's hash for a push or a stream, without blocking the frame: the cached hash when the file sits in a
        // synced folder unchanged since it was last hashed, otherwise read on a worker, since a bundle can be hundreds of MB.
        public static IEnumerator ComputeSha256Async(string absPath, Action<string, Exception> onComplete)
        {
            string sha = null;
            Exception failure = null;
            bool finished = false;

            WaitCallback work = _ =>
            {
                try { sha = CachedOrComputedSha256(absPath); }
                catch (Exception ex) { failure = ex; }
                finally { Volatile.Write(ref finished, true); }
            };
            if (!ThreadPool.QueueUserWorkItem(work)) work(null);

            while (!Volatile.Read(ref finished)) yield return null;

            onComplete?.Invoke(sha, failure);
        }

        private static string CachedOrComputedSha256(string absPath)
        {
            string fullPath = Path.GetFullPath(absPath);
            string relPath = RelativePath(fullPath);   // 1.1.254: per-folder roots
            if (relPath != null)
            {
                var fileInfo = new FileInfo(fullPath);
                lock (_buildLock)
                {
                    LoadCacheIfNeeded();
                    if (TryGetCachedHash(relPath, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, out string cached))
                        return cached;
                }
            }
            return ComputeSha256(absPath);
        }

        // Streams the file through the hasher; a bundle is never read whole into memory.
        public static string ComputeSha256(string absPath)
        {
            using (var stream = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                HashReadBufferBytes, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var hex = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString();
            }
        }

        public static byte[] Serialize(Manifest manifest)
        {
            string json = JsonConvert.SerializeObject(manifest);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public static Manifest Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0) return new Manifest();
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(data);
                return JsonConvert.DeserializeObject<Manifest>(json) ?? new Manifest();
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"[FAP Sync] Manifest.Deserialize failed: {ex.Message}");
                return new Manifest();
            }
        }

        private static void LoadCacheIfNeeded()
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;
            _cache = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

            string path = CacheFilePath;
            if (!File.Exists(path)) return;

            try
            {
                var stored = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(path));
                if (stored?.Entries != null)
                {
                    foreach (var entry in stored.Entries)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Path)) continue;
                        _cache[entry.Path] = entry;
                    }
                }
                VAFapThreadLog.Info($"[FAP Sync] Loaded {_cache.Count} cached hash(es) from {path}.");
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"[FAP Sync] Cache load failed ({path}): {ex.Message} — starting with empty cache.");
                _cache.Clear();
            }
        }

        // Staged through a .tmp so a kill mid-write can't leave a torn cache behind.
        private static void SaveCache()
        {
            try
            {
                if (!Directory.Exists(RootDir)) Directory.CreateDirectory(RootDir);
                var stored = new Manifest();
                foreach (var entry in _cache) stored.Entries.Add(entry.Value);

                string finalPath = CacheFilePath;
                string tmpPath = finalPath + ".tmp";
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(stored, Formatting.None));
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
                _cacheDirty = false;
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"[FAP Sync] Cache save failed: {ex.Message}");
            }
        }

        public static void InvalidateCache()
        {
            _cache = null;
            _cacheLoaded = false;
            _cacheDirty = false;
            try { if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath); }
            catch (Exception ex) { VAFapThreadLog.Warning($"[FAP Sync] InvalidateCache delete failed: {ex.Message}"); }
        }
    }
}
