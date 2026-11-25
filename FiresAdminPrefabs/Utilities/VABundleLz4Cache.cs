using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.414 ([saddles] 11-10, lead: Hexium refused VAPieces 1.0.0 at 602 MB; it accepted 172 MB). VAPieces ships its bundles LZMA again
    // (253.5 MB, the same content as the 11-08 LZ4 files). LZMA opens slowly, so a client re-packs each shipped
    // LZMA bundle once to LZ4 (AssetBundle.RecompressAssetBundleAsync, the runtime API the 11-08 repack used) into
    // <BepInEx>\cache\FiresAdminPrefabs\BundleLz4\<mod>\<path> and opens that copy from then on: LZ4 speed after the first start, disk over RAM.
    //  - Which files: any bundle under plugins\<mod>\FapBundles\ (root and Near\) whose UnityFS header says LZMA. Anything else opens as shipped.
    //  - Trust: a cached copy is opened only when its stamp file names the shipped file's size, last-write time and a hash of its first and last
    //    64 KB, and the copy's own size; a changed source (a package update) or a missing / short copy = the shipped file opens, and it is re-packed.
    //  - When: new re-packs START only while there is no local player (main menu, world loading screen), one at a time, main bundles before
    //    Near ones; never in play. A re-pack already running when the player spawns finishes on Unity's loader thread.
    //  - Failure (no disk space, the API refuses, an open of the copy fails): the shipped LZMA opens exactly as through 11-07, one Info line says why,
    //    and that bundle is not retried this session. A copy that fails to open is deleted.
    //  - Cost: one Info line per bundle (MB in -> MB out, seconds) and one total when the queue empties. Key [Bundles] Local LZ4 Cache, default ON.
    //  - A dedicated server opens every bundle as shipped (it loads bundles once and never opens Near ones).
    internal static class VABundleLz4Cache
    {
        private const string FolderName = "BundleLz4";
        private const int EdgeBytes = 64 * 1024;
        private static ConfigEntry<bool> s_enabled;
        private static bool s_bound;

        private static bool Enabled
        {
            get
            {
                if (!s_bound && VaPieces.Instance != null)
                {
                    s_bound = true;
                    s_enabled = VaPieces.Instance.Config.Bind("Bundles", "Local LZ4 Cache", true,
                        "Bundles shipped compressed small (LZMA) are re-packed once, on the main menu or a loading screen, into a fast-opening copy (LZ4) in BepInEx\\cache, " +
                        "and that copy is opened from then on. Same content. Costs disk space (about 3.5x the bundle) instead of load time. Off = bundles open as shipped.");
                }
                return s_enabled == null || s_enabled.Value;
            }
        }

        private sealed class Job { public string Source, Cache; public bool Near; }
        private static readonly List<Job> s_queue = new List<Job>();
        private static readonly HashSet<string> s_queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_running, s_scanned;
        private static double s_totalSeconds;
        private static long s_totalIn, s_totalOut;
        private static int s_done;

        private static string Root => Path.Combine(VAFapCachePaths.Root, FolderName);

        // plugins\<mod>\FapBundles\<rel> -> cache\BundleLz4\<mod>\<rel>; null for any other file.
        private static string CachePathFor(string source)
        {
            try
            {
                string full = Path.GetFullPath(source);
                string plugins = Path.GetFullPath(Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) return null;
                string rel = full.Substring(plugins.Length);   // <mod>\FapBundles\...
                string[] parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts.Length < 3 || !string.Equals(parts[1], "FapBundles", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.Combine(Root, parts[0], string.Join(Path.DirectorySeparatorChar.ToString(), parts, 2, parts.Length - 2));
            }
            catch { return null; }
        }

        /// <summary>The file to open for a shipped bundle: its verified LZ4 copy, or the shipped file (queued for a re-pack when it is LZMA).</summary>
        internal static string OpenPath(string source)
        {
            if (string.IsNullOrEmpty(source) || VaPieces.IsDedicatedServer || !Enabled) return source;
            try
            {
                string cache = CachePathFor(source);
                if (cache == null) return source;
                if (IsTrusted(source, cache)) return cache;
                if (!s_failed.Contains(source) && IsLzma(source)) Enqueue(source, cache);
            }
            catch (Exception ex) { Info($"cache lookup for '{Path.GetFileName(source)}' failed ({ex.Message}); opening the shipped file"); }
            return source;
        }

        /// <summary>An opened copy failed: delete it, never use it again this session; the caller opens the shipped file.</summary>
        internal static void CopyFailedToOpen(string source, string opened)
        {
            if (string.Equals(source, opened, StringComparison.OrdinalIgnoreCase)) return;
            s_failed.Add(source);
            try { File.Delete(opened); File.Delete(opened + ".stamp"); } catch { }
            Info($"the LZ4 copy of '{Path.GetFileName(source)}' could not be opened; deleted, opening the shipped file (re-packed next start)");
        }

        // At startup: queue every LZMA bundle under any FapBundles (root + Near), so Near bundles are ready before a tower is ever near.
        internal static void ScanAll()
        {
            if (s_scanned || VaPieces.IsDedicatedServer || !Enabled) return;
            s_scanned = true;
            try
            {
                foreach (var mod in Directory.GetDirectories(Paths.PluginPath))
                {
                    string dir = Path.Combine(mod, "FapBundles");
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir)) OpenPath(f);
                    string near = Path.Combine(dir, "Near");
                    if (Directory.Exists(near)) foreach (var f in Directory.GetFiles(near)) OpenPath(f);
                }
            }
            catch (Exception ex) { Info($"bundle scan failed ({ex.Message}); bundles open as shipped"); }
        }

        // ---- stamp: shipped size | last-write ticks | FNV-1a of its first and last 64 KB | the copy's size
        private static string StampOf(string source, long copyLength)
        {
            var fi = new FileInfo(source);
            return $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{EdgeHash(source, fi.Length):X16}|{copyLength}";
        }

        private static ulong EdgeHash(string path, long length)
        {
            ulong h = 14695981039346656037UL;
            var buf = new byte[EdgeBytes];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n = fs.Read(buf, 0, (int)Math.Min(EdgeBytes, length));
                for (int i = 0; i < n; i++) h = (h ^ buf[i]) * 1099511628211UL;
                if (length > EdgeBytes)
                {
                    fs.Seek(Math.Max(EdgeBytes, length - EdgeBytes), SeekOrigin.Begin);
                    n = fs.Read(buf, 0, buf.Length);
                    for (int i = 0; i < n; i++) h = (h ^ buf[i]) * 1099511628211UL;
                }
            }
            return h;
        }

        private static bool IsTrusted(string source, string cache)
        {
            string stampPath = cache + ".stamp";
            if (!File.Exists(cache) || !File.Exists(stampPath)) return false;
            long copy = new FileInfo(cache).Length;
            return copy > 0 && File.ReadAllText(stampPath).Trim() == StampOf(source, copy);
        }

        // UnityFS header: "UnityFS\0", uint32 format, version string, revision string, int64 size, uint32 packed info, uint32 unpacked info,
        // uint32 flags (low 6 bits = the block info compression: 0 none, 1 LZMA, 2 LZ4, 3 LZ4HC). Unity writes an LZMA bundle's block info LZMA.
        private static bool IsLzma(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new BinaryReader(fs, Encoding.ASCII))
                {
                    if (ReadCString(r) != "UnityFS") return false;
                    ReadBigU32(r); ReadCString(r); ReadCString(r);
                    r.ReadBytes(8); ReadBigU32(r); ReadBigU32(r);
                    uint flags = ReadBigU32(r);
                    return (flags & 0x3F) == 1;
                }
            }
            catch { return false; }
        }

        private static string ReadCString(BinaryReader r)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 64; i++) { byte b = r.ReadByte(); if (b == 0) break; sb.Append((char)b); }
            return sb.ToString();
        }

        private static uint ReadBigU32(BinaryReader r) { var b = r.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }

        private static void Enqueue(string source, string cache)
        {
            if (!s_queued.Add(source)) return;
            bool near = source.IndexOf(Path.DirectorySeparatorChar + "Near" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
            var job = new Job { Source = source, Cache = cache, Near = near };
            if (near) s_queue.Add(job); else s_queue.Insert(0, job);   // main bundles first: they open at the next start's menu
            if (!s_running && VaPieces.Instance != null) { s_running = true; VaPieces.Instance.StartCoroutine(Drive()); }
        }

        private static bool InPlay => Player.m_localPlayer != null;

        private static IEnumerator Drive()
        {
            var waitPlay = new WaitForSeconds(2f);
            while (s_queue.Count > 0)
            {
                if (InPlay) { yield return waitPlay; continue; }   // never START a re-pack in play
                Job job = s_queue[0];
                s_queue.RemoveAt(0);
                string tmp = job.Cache + ".partial";
                var clock = System.Diagnostics.Stopwatch.StartNew();
                AssetBundleRecompressOperation op = null;
                string why = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.Cache));
                    if (File.Exists(tmp)) File.Delete(tmp);
                    op = AssetBundle.RecompressAssetBundleAsync(job.Source, tmp, BuildCompression.LZ4Runtime);
                }
                catch (Exception ex) { why = ex.Message; }
                if (op != null) yield return op;
                try
                {
                    if (why == null && (op == null || !op.success)) why = op != null ? op.humanReadableResult : "no operation";
                    if (why == null && (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)) why = "the copy was not written";
                    if (why == null)
                    {
                        if (File.Exists(job.Cache)) File.Delete(job.Cache);
                        File.Move(tmp, job.Cache);
                        long inLen = new FileInfo(job.Source).Length, outLen = new FileInfo(job.Cache).Length;
                        File.WriteAllText(job.Cache + ".stamp", StampOf(job.Source, outLen));
                        double s = clock.Elapsed.TotalSeconds;
                        s_done++; s_totalSeconds += s; s_totalIn += inLen; s_totalOut += outLen;
                        Info($"cached a fast-opening (LZ4) copy of '{Path.GetFileName(job.Source)}': {inLen / 1048576.0:0.0} MB -> {outLen / 1048576.0:0.0} MB in {s:0.0} s (one time; used from the next time it opens)");
                    }
                }
                catch (Exception ex) { why = ex.Message; }
                if (why != null)
                {
                    s_failed.Add(job.Source);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    Info($"could not make an LZ4 copy of '{Path.GetFileName(job.Source)}' ({why}); it opens as shipped (LZMA), as before");
                }
                yield return null;
            }
            if (s_done > 0)
                Info($"LZ4 cache: {s_done} bundle(s) re-packed, {s_totalIn / 1048576.0:0} MB -> {s_totalOut / 1048576.0:0} MB, {s_totalSeconds:0.0} s in total (one time)");
            s_done = 0; s_totalSeconds = 0; s_totalIn = s_totalOut = 0;
            s_running = false;
        }

        private static void Info(string line) => Debug.Log($"{VaPieces.PluginName}: [BundleLz4] {line}");
    }
}
