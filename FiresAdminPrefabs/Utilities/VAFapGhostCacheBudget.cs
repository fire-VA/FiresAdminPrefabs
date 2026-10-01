using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VerdantsAscent.Utilities
{
    // Keeps the ghost mesh cache under a total-size budget.
    //
    // Nothing used to bound this directory. MaxCombinedPartVertices splits a material into _part{N}
    // meshes, which caps a single MESH, not a file and not the folder: a 737-blueprint library reached
    // 9.9 GB across 516 files, with 24 of those files holding 77% of the bytes.
    //
    // Two separate levers, because they do different jobs:
    //  - The per-blueprint threshold only WARNS. A blueprint that bakes huge is baking what it was asked
    //    to, and quietly substituting something cheaper would change what the player sees while placing.
    //  - The directory budget is the one that reclaims, and it is a runaway guard rather than a tight
    //    cap. Every cache entry maps to a live blueprint - there are no orphans to collect - so evicting
    //    costs a rebake the next time that blueprint is picked. Set the budget where it catches drift,
    //    not where it churns.
    //
    // Recency comes from an index this class writes, never from the filesystem. NTFS last-access updates
    // are on by default but any directory enumeration, backup pass or AV scan rewrites them all at once,
    // which makes LastAccessTime worthless for ranking.
    public static class VAFapGhostCacheBudget
    {
        private const string IndexFileName = ".ghost_access";
        private const string CacheFileExtension = ".bpghost";
        private const int DefaultBudgetMb = 12288;
        private const int DefaultWarnPerBlueprintMb = 512;
        private const long BytesPerMb = 1024 * 1024;

        private static readonly object Gate = new object();
        private static Dictionary<string, long> _lastUsedTicks;

        private static long BudgetBytes =>
            (long)Math.Max(0, Managers.ConfigManager.Instance?.configGhostCacheBudgetMB?.Value ?? DefaultBudgetMb) * BytesPerMb;

        private static long WarnPerBlueprintBytes =>
            (long)Math.Max(0, Managers.ConfigManager.Instance?.configGhostCacheWarnPerBlueprintMB?.Value ?? DefaultWarnPerBlueprintMb) * BytesPerMb;

        // Called when a blueprint's ghost is actually served from disk. That is the only event that says
        // "somebody wants this one", so it is the only thing recency is built from.
        public static void Touch(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return;
            lock (Gate)
            {
                EnsureIndexLoaded();
                _lastUsedTicks[blueprintName] = DateTime.UtcNow.Ticks;
                SaveIndex();
            }
        }

        // Runs on the bake's worker thread once the file is in place.
        public static void NoteWritten(string blueprintName, long fileBytes)
        {
            long warnBytes = WarnPerBlueprintBytes;
            if (warnBytes > 0 && fileBytes >= warnBytes)
                VAFapThreadLog.Warning(
                    $"{VaPieces.PluginName}: VAFapGhostCacheBudget - the ghost cache for '{blueprintName}' is " +
                    $"{fileBytes / (double)BytesPerMb:N0} MB, over the {warnBytes / BytesPerMb:N0} MB per-blueprint threshold. " +
                    "It was written anyway; the threshold only reports which blueprints dominate the cache.");

            lock (Gate)
            {
                EnsureIndexLoaded();
                _lastUsedTicks[blueprintName] = DateTime.UtcNow.Ticks;
                SaveIndex();
            }

            Enforce(justWritten: blueprintName);
        }

        private static void Enforce(string justWritten)
        {
            long budget = BudgetBytes;
            if (budget <= 0) return;

            string dir = VAFapCachePaths.GhostMeshCacheDir;
            List<FileInfo> files;
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) return;
                files = new List<FileInfo>(info.GetFiles("*" + CacheFileExtension));
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapGhostCacheBudget - couldn't read the cache folder: {ex.Message}");
                return;
            }

            long total = 0;
            foreach (var file in files) total += file.Length;
            if (total <= budget) return;

            Dictionary<string, long> recency;
            lock (Gate)
            {
                EnsureIndexLoaded();
                recency = new Dictionary<string, long>(_lastUsedTicks, StringComparer.Ordinal);
            }

            // A file with no index entry has never been picked since the index existed, so it sorts
            // oldest - among themselves by write time, which is the only signal such a file carries.
            files.Sort((a, b) => RecencyOf(a, recency).CompareTo(RecencyOf(b, recency)));

            long freed = 0;
            int evicted = 0;
            var evictedNames = new List<string>();
            foreach (var file in files)
            {
                if (total - freed <= budget) break;
                string name = Path.GetFileNameWithoutExtension(file.Name);
                if (string.Equals(name, justWritten, StringComparison.Ordinal)) continue;
                if (VAFapBlueprintGhostCache.IsBakeInFlight(name)) continue;

                long size = file.Length;
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapGhostCacheBudget - couldn't evict '{file.Name}': {ex.Message}");
                    continue;
                }

                freed += size;
                evicted++;
                evictedNames.Add(name);
            }

            if (evicted == 0) return;

            lock (Gate)
            {
                EnsureIndexLoaded();
                foreach (string name in evictedNames) _lastUsedTicks.Remove(name);
                SaveIndex();
            }

            foreach (string name in evictedNames) VAFapBlueprintGhostCache.InvalidateCacheStatus(name);

            VAFapThreadLog.Info(
                $"{VaPieces.PluginName}: VAFapGhostCacheBudget - the ghost cache was {total / (double)BytesPerMb:N0} MB against a " +
                $"{budget / BytesPerMb:N0} MB budget; evicted {evicted} least-recently-picked " +
                $"{(evicted == 1 ? "entry" : "entries")} to free {freed / (double)BytesPerMb:N0} MB. " +
                "Each one rebakes the next time its blueprint is picked.");
        }

        private static long RecencyOf(FileInfo file, Dictionary<string, long> recency) =>
            recency.TryGetValue(Path.GetFileNameWithoutExtension(file.Name), out long ticks)
                ? ticks
                : file.LastWriteTimeUtc.Ticks - TimeSpan.TicksPerDay * 3650;

        private static string IndexPath => Path.Combine(VAFapCachePaths.GhostMeshCacheDir, IndexFileName);

        private static void EnsureIndexLoaded()
        {
            if (_lastUsedTicks != null) return;
            _lastUsedTicks = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                string path = IndexPath;
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    int split = line.IndexOf('|');
                    if (split <= 0 || split == line.Length - 1) continue;
                    if (!long.TryParse(line.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) continue;
                    _lastUsedTicks[line.Substring(split + 1)] = ticks;
                }
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapGhostCacheBudget - couldn't read the recency index, starting a new one: {ex.Message}");
                _lastUsedTicks.Clear();
            }
        }

        private static void SaveIndex()
        {
            try
            {
                string dir = VAFapCachePaths.GhostMeshCacheDir;
                Directory.CreateDirectory(dir);
                var text = new StringBuilder();
                foreach (var pair in _lastUsedTicks)
                    text.Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append('|').AppendLine(pair.Key);
                File.WriteAllText(IndexPath, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapGhostCacheBudget - couldn't write the recency index: {ex.Message}");
            }
        }
    }
}
