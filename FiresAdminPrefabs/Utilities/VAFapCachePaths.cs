using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Where FAP keeps the files it GENERATES, as opposed to the ones you author.
    //
    // BepInEx/config is exactly what r2modman and Thunderstore sweep into a profile or modpack export,
    // so anything large and rebuildable that lives there ends up inside somebody's shared zip. The ghost
    // mesh cache reached 9.9 GB on one rig and 6.2 GB on another, and the icon cache another 68 MB.
    // None of it is worth a byte of an export: every file is derived from something the player already
    // has, and a missing one is rebuilt on demand.
    //
    // Only the caches move. HammerTabs stays in config because you author blueprints there, and
    // Assets/Manifests stay because VAFapSyncManifest.SyncedFolders pulls them from the server - moving
    // those would make an upgrading client re-download every bundle it already has.
    //
    // The root is <BepInEx>\cache\FiresAdminPrefabs: inside the game / profile folder, never outside it
    // (Fire, 2026-09-30: "any handling that reaches outside the valheim folder is handled by only our
    // private mods, itto and fdt"; FAP is public). It used to be %LOCALAPPDATA%\FiresAdminPrefabs, shared
    // per user; that folder is no longer read or written, and an old one is left for the player to delete
    // (it is only a rebuildable cache). Entries are keyed by blueprint name plus source SHA1, so two
    // profiles holding different blueprints under one name miss and rebake rather than collide.
    public static class VAFapCachePaths
    {
        private const string RootFolderName = "FiresAdminPrefabs";
        private const string CacheFolderName = "cache";
        public const string GhostMeshCacheFolderName = "GhostMeshCache";
        public const string IconCacheFolderName = "IconCache";

        private static string _root;
        private static bool _migrated;

        public static string Root => _root ?? (_root = ResolveRoot());

        public static string GhostMeshCacheDir => Path.Combine(Root, GhostMeshCacheFolderName);

        public static string IconCacheDir => Path.Combine(Root, IconCacheFolderName);

        // Mirrors FiresDiscordIntegration's BotTokenFile.IsExportedLocation. Config and plugins are the
        // two folders an export sweeps, so a cache root resolving under either defeats the whole point
        // of moving out of config.
        public static bool IsExportedLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                return IsUnder(full, Paths.ConfigPath) || IsUnder(full, Paths.PluginPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnder(string fullPath, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // <BepInEx>\cache\FiresAdminPrefabs: under the game or profile's BepInEx folder, so nothing outside
        // the Valheim install is touched, and outside config/plugins, which is what an export sweeps. The
        // BepInEx root itself is the fallback, still outside config.
        private static string ResolveRoot()
        {
            string candidate = null;
            try { candidate = Path.Combine(Paths.BepInExRootPath, CacheFolderName, RootFolderName); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapCachePaths - couldn't resolve the BepInEx cache folder: {ex.Message}");
            }

            if (candidate != null && IsExportedLocation(candidate))
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapCachePaths - '{candidate}' resolves inside an exported folder, using the BepInEx root instead.");
                candidate = null;
            }

            if (candidate != null && TryPrepare(candidate)) return candidate;

            string fallback = Path.Combine(Paths.BepInExRootPath, RootFolderName);
            TryPrepare(fallback);
            return fallback;
        }

        private static bool TryPrepare(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapCachePaths - can't use '{dir}' for the cache root: {ex.Message}");
                return false;
            }
        }

        // Folds a previous install's caches out of config into the new root. Idempotent, and the
        // destination wins on a per-file collision so migrating a second profile never overwrites
        // entries the first one already contributed.
        public static void MigrateFromConfig()
        {
            if (_migrated) return;
            _migrated = true;
            MigrateFolder(GhostMeshCacheFolderName, GhostMeshCacheDir);
            MigrateFolder(IconCacheFolderName, IconCacheDir);
        }

        private static void MigrateFolder(string folderName, string destination)
        {
            string source = Path.Combine(Paths.ConfigPath, RootFolderName, folderName);
            if (!Directory.Exists(source)) return;
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;

            // A same-volume rename is instant whatever the folder weighs: config and the cache root are both
            // under the same BepInEx folder.
            if (!Directory.Exists(destination))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Directory.Move(source, destination);
                    Debug.Log($"{VaPieces.PluginName}: VAFapCachePaths - moved {folderName} out of the exported config folder to '{destination}'.");
                    return;
                }
                catch (IOException)
                {
                    // Cross-volume, or something holds a handle. Fall through to the per-file path.
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapCachePaths - couldn't move {folderName}: {ex.Message}");
                    return;
                }
            }

            MergeInBackground(source, destination, folderName);
        }

        // The per-file merge can be a many-gigabyte copy across volumes, so it never runs on the main
        // thread. Anything that hasn't arrived yet simply reads as a cache miss and is rebaked, which is
        // already how a missing entry behaves.
        private static void MergeInBackground(string source, string destination, string folderName)
        {
            Task.Run(() =>
            {
                int moved = 0, kept = 0;
                try
                {
                    Directory.CreateDirectory(destination);
                    foreach (string file in Directory.GetFiles(source))
                    {
                        string target = Path.Combine(destination, Path.GetFileName(file));
                        try
                        {
                            if (File.Exists(target)) { File.Delete(file); kept++; continue; }
                            File.Move(file, target);
                            moved++;
                        }
                        catch (Exception ex)
                        {
                            VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapCachePaths - couldn't move '{Path.GetFileName(file)}': {ex.Message}");
                        }
                    }

                    if (Directory.GetFiles(source).Length == 0 && Directory.GetDirectories(source).Length == 0)
                        Directory.Delete(source);
                }
                catch (Exception ex)
                {
                    VAFapThreadLog.Warning($"{VaPieces.PluginName}: VAFapCachePaths - couldn't finish migrating {folderName}: {ex.Message}");
                }

                if (moved > 0 || kept > 0)
                    VAFapThreadLog.Info($"{VaPieces.PluginName}: VAFapCachePaths - migrated {folderName} out of the exported config folder: {moved} moved, {kept} already present at the destination.");
            });
        }
    }
}
