using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Patches;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Managers
{
    // Disk-driven hammer-tab organization: the user reshapes the hammer by editing
    // files under config/FiresAdminPrefabs/HammerTabs/. Each folder is a tab, its
    // prefabs.txt lists one prefab name per line (# comments), and a new folder makes
    // a new tab. Moving a prefab between tabs is cut-and-paste plus a world reload.
    //
    // Four steps inside VAVanillaPieceManager.InitializeOnce:
    //   LoadFromDisk       builds the prefab→tab override map and records every folder
    //                      in KnownTabNames, before categories are registered
    //   RegisterAll        turns those names into real PieceCategory values
    //   AutoCategorize     consults the override map FIRST — disk beats every built-in
    //                      clutter/FX/Nature/sort rule, by design
    //   SaveCurrentLayout  appends newly-seen prefabs to their assigned tab's file, so
    //                      a freshly installed mod shows up organized next launch.
    //                      Comments at the top of the file survive the rewrite.
    //
    // Per-client: HammerTabs/ is local config, never ConfigSync'd. The folder name is
    // the tab name (sanitized for the path, original text shown in the hammer), and
    // the override map is built at scan time — a rename needs a world reload.
    public static class VAHammerTabOrganizer
    {
        // Per-prefab override read from disk. Keyed by prefab name,
        // value is the tab name (= folder name). First-folder-wins on
        // duplicates — directory enumeration is sorted alphabetically
        // for determinism.
        private static readonly Dictionary<string, string> _prefabToTabOverride =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Tab names found on disk. Used by VAVanillaPieceManager.InitializeOnce
        // to pre-register every user-defined tab in VACategoryRegistry
        // BEFORE any prefab tries to route to one. Includes built-in
        // tabs too if the folder exists for them.
        //
        // EXCLUDES the special "Ignore" folder — that folder lists prefabs
        // the user wants kept OUT of the hammer entirely, not routed to
        // a visible tab. See _ignoredPrefabs below.
        private static readonly HashSet<string> _knownTabNames =
            new HashSet<string>(StringComparer.Ordinal);

        // Custom per-tab DISPLAY names, decoupled from the on-disk folder name
        // (the internal key). The right-click "Rename" sets a friendly label here
        // without moving folders; persisted per-client in the FAP .cfg
        // (HammerTabs/TabDisplayNames) and lazily loaded from it on first access.
        private static readonly Dictionary<string, string> _tabDisplayNames =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool _displayNamesParsed;

        // Prefab names listed in HammerTabs/Ignore/prefabs.txt. These
        // prefabs:
        //   * Stay registered in ZNetScene (functional via spawn commands,
        //     ZDO sync, paint tools, etc.)
        //   * Are SKIPPED by VAVanillaPieceManager.InitializeOnce —
        //     no Piece component injected, not added to the PieceTable,
        //     not in any hammer tab.
        //   * The "Ignore" folder is NOT registered as a visible tab
        //     category (excluded from _knownTabNames during LoadFromDisk).
        //
        // Lets the user hide bulk-added clutter / odd prefabs that FAP
        // auto-routes to the hammer but they don't want cluttering the
        // tab list. Move the line BACK into a real tab's prefabs.txt to
        // un-hide.
        private static readonly HashSet<string> _ignoredPrefabs =
            new HashSet<string>(StringComparer.Ordinal);

        // Split-out counters populated during LoadFromDisk so the
        // startup log + post-restart diagnostics can break down WHERE
        // the ignored entries came from (per-name hide via Ignore/
        // vs hide-tab marker via Hidden_<name>/). Plus the set of
        // currently-hidden tab names for direct accessor use.
        private static int _hiddenViaTabFolder;
        private static readonly HashSet<string> _hiddenTabsLoaded =
            new HashSet<string>(StringComparer.Ordinal);

        // True if the tab is currently hidden, whether restored from a Hidden_<tab>/ folder
        // or hidden this session. AutoCategorize uses it to route a hidden tab's prefabs to
        // PieceCategory.Max — which UpdateAvailable skips — instead of recreating the tab, and
        // SaveCurrentLayout uses it to avoid re-creating the folder it just renamed.
        //
        // The check is by name, so a tab hidden with an empty prefabs.txt stays hidden too.
        public static bool IsTabHidden(string tabName)
        {
            if (string.IsNullOrEmpty(tabName)) return false;
            return _hiddenTabsLoaded.Contains(tabName);
        }

        // Special-case folder name for the ignored-prefab list. Case-
        // sensitive on disk (matches the folder exactly). Documented in
        // the root README so users know.
        internal const string IgnoreFolderName = "Ignore";

        // ──────────────────────────────────────────────────────────────
        //  Favorites — bookmark tab, NOT a move destination
        // ──────────────────────────────────────────────────────────────
        //
        // A favorited piece APPEARS in the Favorites tab while STAYING in its
        // home tab (the override map is untouched — favorites are bookmarks,
        // not moves). Stored per-client as one prefab name per line in
        // HammerTabs/favorites.txt; the tab itself is registered
        // unconditionally in LoadFromDisk so it exists (empty) from the first
        // launch. Injection into the live piece lists happens in the
        // PieceTable.UpdateAvailable postfix (HammerScrollPatches).
        public const string FavoritesTabName = "Favorites";

        // Favorites are ORDERED — the file's line order is the Favorites tab's
        // display order (same rule as every other tab). List = order, set = O(1)
        // membership.
        private static readonly List<string> _favoritesOrdered = new List<string>();
        private static readonly HashSet<string> _favorites =
            new HashSet<string>(StringComparer.Ordinal);

        // Favorites lives as a real FOLDER like every other tab
        // (HammerTabs/Favorites/prefabs.txt) so it's visible and editable
        // alongside them. It is special-cased in LoadFromDisk: a bookmark list,
        // never a membership override (a favorited piece stays in its home tab).
        private static string FavoritesDirPath =>
            Path.Combine(HammerTabsDir, FavoritesTabName);

        private static string FavoritesFilePath =>
            Path.Combine(FavoritesDirPath, PrefabsFileName);

        // Pre-folder location (root favorites.txt) — migrated on load.
        private static string LegacyFavoritesFilePath =>
            Path.Combine(HammerTabsDir, "favorites.txt");

        public static int FavoriteCount => _favorites.Count;

        public static bool IsFavorite(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && _favorites.Contains(prefabName);

        // File line index within Favorites/prefabs.txt → Favorites tab order.
        public static bool TryGetFavoriteOrder(string prefabName, out int orderIndex)
        {
            orderIndex = string.IsNullOrEmpty(prefabName)
                ? -1 : _favoritesOrdered.IndexOf(prefabName);
            return orderIndex >= 0;
        }

        // Returns the NEW favorite state (true = now favorited).
        public static bool ToggleFavorite(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            bool nowFavorite;
            if (_favorites.Contains(prefabName))
            {
                _favorites.Remove(prefabName);
                _favoritesOrdered.Remove(prefabName);
                nowFavorite = false;
            }
            else
            {
                _favorites.Add(prefabName);
                _favoritesOrdered.Add(prefabName);
                nowFavorite = true;
            }
            SaveFavorites();
            return nowFavorite;
        }

        private static void LoadFavorites()
        {
            _favorites.Clear();
            _favoritesOrdered.Clear();
            try
            {
                // Migrate the pre-folder root favorites.txt into the folder once.
                string legacy = LegacyFavoritesFilePath;
                if (!File.Exists(FavoritesFilePath) && File.Exists(legacy))
                {
                    ReadFavoriteLines(legacy);
                    SaveFavorites();
                    try { File.Delete(legacy); } catch { }
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer — migrated favorites.txt → Favorites/{PrefabsFileName}.");
                }
                else if (File.Exists(FavoritesFilePath))
                {
                    ReadFavoriteLines(FavoritesFilePath);
                }

                // Seed the folder + file even when empty so Favorites is VISIBLE
                // in HammerTabs alongside every other tab from the first launch.
                if (!File.Exists(FavoritesFilePath)) SaveFavorites();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.LoadFavorites failed: {ex.Message}");
            }
        }

        private static void ReadFavoriteLines(string path)
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (_favorites.Add(line)) _favoritesOrdered.Add(line);
            }
        }

        private static void SaveFavorites()
        {
            try
            {
                Directory.CreateDirectory(FavoritesDirPath);
                var lines = new List<string>(_favoritesOrdered.Count + 3)
                {
                    "# Favorites — one prefab name per line. Right-click a piece icon",
                    "# in the hammer to add/remove. LINE ORDER = tab display order.",
                    "# Bookmarks only: a favorited piece also stays in its home tab.",
                };
                lines.AddRange(_favoritesOrdered);
                File.WriteAllLines(FavoritesFilePath, lines);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.SaveFavorites failed: {ex.Message}");
            }
        }

        // Blueprints is a visible tab but its content model is inverted: the user drops
        // .blueprint/.vbuild files straight into the folder and VABlueprintLoader turns
        // each into a synthetic stub, instead of a prefabs.txt naming existing prefabs.
        //
        // So LoadFromDisk skips any prefabs.txt in there — older FAP versions auto-saved
        // BP_<filename> stub names into one, which then polluted the override map — and
        // SaveCurrentLayout never writes to it, since those names are regenerated from
        // disk every run. The tab itself comes from VACategoryRegistry's CatBlueprints,
        // so it exists whether or not the folder has anything in it.
        private const string BlueprintsFolderName = "Blueprints";

        // FiresAdminTerrain's voxel tools. The prefix is FAT's own naming
        // (VoxelToolPieces.AddTool creates "fires_vtool_<Name>"), so this is an agreed name
        // between the two mods rather than a guess about what a terrain tool looks like.
        internal const string VoxelToolPrefabPrefix = "fires_vtool_";
        internal const string TerrainTabName = "Terrain";

        // Skip works like Ignore — its listed prefabs are dropped from hammer eligibility —
        // and additionally skips a whole BUNDLE when every prefab in the folder comes from
        // that one bundle. Renaming a bundle's folder to "Skip" therefore hides every prefab
        // it ships, including ones a future update adds. A Skip folder mixing several
        // bundles only hides the prefabs it names.
        private const string SkipFolderName = "Skip";

        // Public accessor — used by VAVanillaPieceManager.InitializeOnce
        // to gate the per-prefab eligibility flow.
        public static bool IsIgnored(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            return _ignoredPrefabs.Contains(prefabName);
        }

        // Diagnostic accessor — count of ignored prefab names.
        public static int IgnoredPrefabCount => _ignoredPrefabs.Count;

        // Bundles whose folder was renamed (or merged) to Skip. New
        // prefabs from these bundles get filtered out at IsEligible
        // even though they aren't individually listed in any prefabs.txt.
        // Populated during the alias-detection pass — any bundle alias
        // whose target tab name is "Skip" gets added here.
        private static readonly HashSet<string> _skippedBundles =
            new HashSet<string>(StringComparer.Ordinal);

        // Public accessor — used by VAVanillaPieceManager.IsEligible
        // alongside IsIgnored.
        public static bool IsBundleSkipped(string bundleName)
        {
            if (!_loaded || string.IsNullOrEmpty(bundleName)) return false;
            return _skippedBundles.Contains(bundleName);
        }

        // Bundle name → tab folder name. When every prefab in a tab folder comes from one
        // source bundle, LoadFromDisk treats that folder as an alias for the bundle, and
        // its name takes over everywhere the bundle's would have appeared: RegisterAll
        // stops registering the original bundle tab, and SortHammerByMod routes the
        // bundle's prefabs — including ones added by a later update — to the folder.
        // Renaming "start_scene/" to "KrumStart/" is all it takes.
        private static readonly Dictionary<string, string> _bundleAliases =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Public accessor for VACategoryRegistry.RegisterAll +
        // VAVanillaPieceManager.AutoCategorize.
        public static bool TryGetBundleAlias(string bundleName, out string aliasTabName)
        {
            aliasTabName = null;
            if (!_loaded || string.IsNullOrEmpty(bundleName)) return false;
            return _bundleAliases.TryGetValue(bundleName, out aliasTabName);
        }

        // Per-tab-folder, all-prefabs-from-same-bundle index built
        // during LoadFromDisk. Used internally during the alias-
        // detection second pass.
        private static readonly Dictionary<string, List<string>> _prefabsByTab =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Per-tab comment preamble captured on load so SaveCurrentLayout
        // can preserve user notes when it rewrites the file. Keyed by
        // tab name → list of leading comment lines (each starting with
        // '#' or blank). Anything after the first non-comment line is
        // treated as data and rewritten freshly.
        private static readonly Dictionary<string, List<string>> _tabHeaderComments =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // FILE LINE ORDER → IN-GAME ORDER. Each prefab's line index within its
        // tab file (first-wins alongside membership). The UpdateAvailable
        // ordering pass sorts listed pieces by this index ahead of unlisted
        // ones, so reordering lines in prefabs.txt reorders the tab.
        private static readonly Dictionary<string, int> _prefabOrderIndex =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // Vanilla hammer categories (enum names below Max, excluding All) —
        // these ALSO materialize as HammerTabs folders so vanilla tabs can be
        // reorganized/reordered from disk exactly like custom tabs. A folder
        // with one of these names maps onto the REAL vanilla category; it is
        // never registered as a custom tab.
        private static readonly HashSet<string> _vanillaTabNames = BuildVanillaTabNames();

        private static HashSet<string> BuildVanillaTabNames()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in Enum.GetNames(typeof(Piece.PieceCategory)))
            {
                if (name == nameof(Piece.PieceCategory.All)) continue;
                if (name == nameof(Piece.PieceCategory.Max)) continue;
                set.Add(name);
            }
            return set;
        }

        public static bool IsVanillaTabName(string tabName)
            => !string.IsNullOrEmpty(tabName) && _vanillaTabNames.Contains(tabName);

        public static IReadOnlyCollection<string> VanillaTabNames => _vanillaTabNames;

        public static bool TryGetOrderIndex(string prefabName, out int orderIndex)
        {
            if (_loaded && !string.IsNullOrEmpty(prefabName))
                return _prefabOrderIndex.TryGetValue(prefabName, out orderIndex);
            orderIndex = 0;
            return false;
        }

        public static bool HasAnyOrderData => _loaded && _prefabOrderIndex.Count > 0;

        private static bool _loaded;

        public static string HammerTabsDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "HammerTabs");

        private const string PrefabsFileName = "prefabs.txt";

        public static IReadOnlyCollection<string> KnownTabNames => _knownTabNames;

        // Prefix used to mark a tab folder as hidden. Disk-load skips
        // any folder whose name starts with this prefix; the
        // "Show hidden tabs" right-click action enumerates them so the
        // user can un-hide. Stable on-disk convention so a user who
        // pokes around the folder tree sees "Hidden_<name>" and knows
        // what's going on.
        public const string HiddenTabPrefix = "Hidden_";

        // ──────────────────────────────────────────────────────────────
        //  Right-click menu disk operations
        // ──────────────────────────────────────────────────────────────
        //
        // All methods write directly to disk under HammerTabs/. After
        // any successful mutation the loader's in-memory state is stale;
        // caller is responsible for triggering a reload (typically via
        // VAVanillaPieceManager.RecategorizeEligiblePieces +
        // Player.UpdateAvailablePiecesList, or a heavier
        // InitializeOnce re-run).
        //
        // Failures are non-fatal — methods log a warning and return false.

        // Snapshot of currently-hidden prefab names. Sorted for stable
        // UI ordering. Used by the right-click "Restore hidden..."
        // submenu so admins can re-surface a piece they previously
        // hid without hand-editing Ignore/prefabs.txt.
        public static System.Collections.Generic.List<string> GetHiddenPrefabs()
        {
            var list = new System.Collections.Generic.List<string>(_ignoredPrefabs);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // Remove a prefab name from Ignore/prefabs.txt + the in-memory
        // ignored set. The prefab becomes eligible for hammer routing
        // again on the next VAVanillaPieceManager.InitializeOnce pass
        // (typically a world reload). Returns false if the name wasn't
        // ignored to begin with (caller can surface a "wasn't hidden"
        // message instead of misleading success).
        //
        // We DON'T attempt an immediate in-place re-categorize because
        // InitializeOnce builds tab membership during its first run and
        // mutating the PieceTable live is full of race conditions —
        // safer to defer to the next clean rebuild and tell the admin
        // a reload is needed.
        public static bool UnhidePrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (!_ignoredPrefabs.Contains(prefabName)) return false;
            try
            {
                string path = Path.Combine(HammerTabsDir, IgnoreFolderName, PrefabsFileName);
                if (File.Exists(path))
                {
                    var lines = new List<string>(File.ReadAllLines(path));
                    int removed = lines.RemoveAll(line =>
                        string.Equals(line?.Trim(), prefabName, StringComparison.Ordinal));
                    if (removed > 0)
                    {
                        File.WriteAllLines(path, lines);
                    }
                }
                _ignoredPrefabs.Remove(prefabName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.UnhidePrefab('{prefabName}') failed: {ex.Message}");
                return false;
            }
        }

        // Append a prefab name to Ignore/prefabs.txt so it disappears
        // from the hammer on next load. Idempotent: if the name is
        // already on the list, no second line is added.
        public static bool HidePrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            try
            {
                EnsureIgnoreFolder();
                string path = Path.Combine(HammerTabsDir, IgnoreFolderName, PrefabsFileName);
                var lines = File.Exists(path)
                    ? new List<string>(File.ReadAllLines(path))
                    : new List<string>();
                foreach (var rawLine in lines)
                {
                    var line = rawLine?.Trim();
                    if (string.Equals(line, prefabName, StringComparison.Ordinal))
                        return true;
                }
                if (lines.Count > 0 && lines[lines.Count - 1].Length != 0)
                    lines.Add(string.Empty);
                lines.Add(prefabName);
                File.WriteAllLines(path, lines);
                _ignoredPrefabs.Add(prefabName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.HidePrefab('{prefabName}') failed: {ex.Message}");
                return false;
            }
        }

        // Remove `prefabName` from any tab's prefabs.txt that currently
        // claims it, then append it to `toTabName`'s prefabs.txt. Creates
        // the destination folder if missing. Updates the in-memory
        // override map so subsequent categorize passes route correctly.
        public static bool MovePrefab(string prefabName, string toTabName)
        {
            if (string.IsNullOrEmpty(prefabName) || string.IsNullOrEmpty(toTabName)) return false;
            try
            {
                if (!Directory.Exists(HammerTabsDir)) Directory.CreateDirectory(HammerTabsDir);

                foreach (var dir in Directory.GetDirectories(HammerTabsDir))
                {
                    string file = Path.Combine(dir, PrefabsFileName);
                    if (!File.Exists(file)) continue;
                    var lines = new List<string>(File.ReadAllLines(file));
                    int removed = lines.RemoveAll(line =>
                        string.Equals(line?.Trim(), prefabName, StringComparison.Ordinal));
                    if (removed > 0) File.WriteAllLines(file, lines);
                }

                string toDir = Path.Combine(HammerTabsDir, toTabName);
                if (!Directory.Exists(toDir)) Directory.CreateDirectory(toDir);
                string toFile = Path.Combine(toDir, PrefabsFileName);
                var outLines = File.Exists(toFile)
                    ? new List<string>(File.ReadAllLines(toFile))
                    : new List<string>();
                if (outLines.Count > 0 && outLines[outLines.Count - 1].Length != 0)
                    outLines.Add(string.Empty);
                outLines.Add(prefabName);
                File.WriteAllLines(toFile, outLines);

                _prefabToTabOverride[prefabName] = toTabName;
                // Vanilla category names map onto the REAL vanilla tabs — never
                // register them as custom tabs.
                if (!IsVanillaTabName(toTabName)) _knownTabNames.Add(toTabName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.MovePrefab('{prefabName}' → '{toTabName}') failed: {ex.Message}");
                return false;
            }
        }

        // Rename the folder for `oldTabName` to `newTabName`. Fails if
        // newTabName conflicts with an existing folder (no merge).
        public static bool RenameTab(string oldTabName, string newTabName)
        {
            if (string.IsNullOrEmpty(oldTabName) || string.IsNullOrEmpty(newTabName)) return false;
            if (string.Equals(oldTabName, newTabName, StringComparison.Ordinal)) return true;
            try
            {
                string src = Path.Combine(HammerTabsDir, oldTabName);
                string dst = Path.Combine(HammerTabsDir, newTabName);
                if (!Directory.Exists(src)) return false;
                if (Directory.Exists(dst))
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: RenameTab refused — destination '{newTabName}' already exists.");
                    return false;
                }
                Directory.Move(src, dst);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.RenameTab('{oldTabName}' → '{newTabName}') failed: {ex.Message}");
                return false;
            }
        }

        // ── Custom tab DISPLAY names (the on-disk folder name stays the key) ──

        public static bool TryGetDisplayName(string internalName, out string display)
        {
            EnsureDisplayNamesParsed();
            display = null;
            return !string.IsNullOrEmpty(internalName)
                   && _tabDisplayNames.TryGetValue(internalName, out display)
                   && !string.IsNullOrEmpty(display);
        }

        public static void SetDisplayName(string internalName, string display)
        {
            if (string.IsNullOrEmpty(internalName)) return;
            EnsureDisplayNamesParsed();
            display = display?.Trim();
            // Empty, or equal to the folder name, means "no override" — drop it.
            if (string.IsNullOrEmpty(display) || string.Equals(display, internalName, StringComparison.Ordinal))
                _tabDisplayNames.Remove(internalName);
            else
                _tabDisplayNames[internalName] = display;
            SerializeDisplayNames();
        }

        // Lazy load from the .cfg. Does NOT latch until the config entry actually
        // exists, so a very-early read before ConfigManager.Initialize can't pin
        // an empty map and lose the persisted names.
        private static void EnsureDisplayNamesParsed()
        {
            if (_displayNamesParsed) return;
            var entry = ConfigManager.Instance?.configTabDisplayNames;
            if (entry == null) return;
            _displayNamesParsed = true;
            _tabDisplayNames.Clear();
            try
            {
                var raw = entry.Value;
                if (string.IsNullOrEmpty(raw)) return;
                foreach (var pair in raw.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = pair.Substring(0, eq).Trim();
                    string val = pair.Substring(eq + 1).Trim();
                    if (key.Length > 0 && val.Length > 0) _tabDisplayNames[key] = val;
                }
            }
            catch { }
        }

        private static void SerializeDisplayNames()
        {
            try
            {
                var entry = ConfigManager.Instance?.configTabDisplayNames;
                if (entry == null) return;
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _tabDisplayNames)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                entry.Value = sb.ToString();
            }
            catch { }
        }

        // Hide a tab. If no Hidden_<name> folder exists yet, rename X → Hidden_X.
        // If one already exists (tab was re-created since a prior hide), merge the
        // new folder's prefabs.txt into it (deduped) and delete the source folder.
        public static bool HideTab(string tabName)
        {
            if (string.IsNullOrEmpty(tabName)) return false;
            if (tabName.StartsWith(HiddenTabPrefix, StringComparison.Ordinal)) return true;

            _hiddenTabsLoaded.Add(tabName);
            _knownTabNames.Remove(tabName);

            bool liveHidden = TryHideCategoryLive(tabName);

            string src = Path.Combine(HammerTabsDir, tabName);
            string dst = Path.Combine(HammerTabsDir, HiddenTabPrefix + tabName);

            // Memory-only / empty tab — no folder on disk. Create the
            // Hidden_<name> folder as a marker so the next LoadFromDisk pass
            // treats it as hidden (presence of the folder is the signal).
            if (!Directory.Exists(src))
            {
                try
                {
                    if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
                    string markerFile = Path.Combine(dst, PrefabsFileName);
                    if (!File.Exists(markerFile))
                        File.WriteAllText(markerFile,
                            "# Tab hidden via right-click; was an empty/memory-only tab\n" +
                            "# with no source folder. Move lines back into another tab's\n" +
                            "# prefabs.txt to restore them.\n");
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer — hid empty tab '{tabName}' by creating marker folder '{HiddenTabPrefix + tabName}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.HideTab marker create failed for empty tab '{tabName}': {ex.Message}");
                    return false;
                }
            }

            if (!Directory.Exists(dst))
            {
                return RenameTab(tabName, HiddenTabPrefix + tabName);
            }

            // Merge into the existing Hidden_<name>/prefabs.txt: union the
            // prefab-name lines, preserve that folder's header comments verbatim.
            try
            {
                string srcFile = Path.Combine(src, PrefabsFileName);
                string dstFile = Path.Combine(dst, PrefabsFileName);

                var union = new SortedSet<string>(StringComparer.Ordinal);
                List<string> dstHeader = new List<string>();

                if (File.Exists(dstFile))
                {
                    bool stillHeader = true;
                    foreach (var rawLine in File.ReadAllLines(dstFile))
                    {
                        string line = rawLine == null ? string.Empty : rawLine.Trim();
                        if (stillHeader && (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)))
                        {
                            dstHeader.Add(rawLine ?? string.Empty);
                            continue;
                        }
                        stillHeader = false;
                        if (line.Length == 0) continue;
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        union.Add(line);
                    }
                }

                if (File.Exists(srcFile))
                {
                    foreach (var rawLine in File.ReadAllLines(srcFile))
                    {
                        string line = rawLine == null ? string.Empty : rawLine.Trim();
                        if (line.Length == 0) continue;
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        union.Add(line);
                    }
                }

                var sb = new StringBuilder();
                foreach (var headerLine in dstHeader) sb.AppendLine(headerLine);
                if (dstHeader.Count > 0 && (dstHeader[dstHeader.Count - 1] ?? "").Length != 0)
                    sb.AppendLine();
                foreach (var name in union) sb.AppendLine(name);
                File.WriteAllText(dstFile, sb.ToString());

                Directory.Delete(src, recursive: true);

                Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer — merged '{tabName}' into existing '{HiddenTabPrefix + tabName}' ({union.Count} unique prefab(s) now hidden). Source folder removed.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.HideTab merge failed for '{tabName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes a category from the LIVE hammer PieceTable so the tab disappears
        /// immediately (no world reload). Returns false silently if the table isn't
        /// ready or the category isn't present. Persistence is handled by HideTab.
        /// </summary>
        private static bool TryHideCategoryLive(string tabName)
        {
            try
            {
                var hammer = UnityEngine.Object.FindObjectOfType<ObjectDB>() == null
                    ? null
                    : ObjectDB.instance?.GetItemPrefab("Hammer");
                if (hammer == null) return false;
                var table = hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
                if (table == null) return false;

                if (!VACategoryRegistry.TryGet(tabName, out var category)) return false;
                int categoryIndex = table.m_categories.IndexOf(category);
                if (categoryIndex < 0) return false;

                table.m_categories.RemoveAt(categoryIndex);
                table.m_categoryLabels.RemoveAt(categoryIndex);

                if (table.m_pieces != null)
                {
                    // Scoped to pieces FAP put here. m_pieces is what other mods resolve prefabs
                    // through — WackysDatabase reads it to find a clone's source — so dropping a
                    // foreign piece filed under this category would take it away from them with
                    // no way to notice.
                    int pruned = table.m_pieces.RemoveAll(pieceObject =>
                    {
                        if (pieceObject == null) return false;
                        var piece = pieceObject.GetComponent<Piece>();
                        if (piece == null || piece.m_category != category) return false;
                        return VAVanillaPieceManager.IsFapOwnedPiece(pieceObject);
                    });
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer.TryHideCategoryLive — removed category '{tabName}' (enum={category}) from live table; pruned {pruned} piece(s).");
                }
                else
                {
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer.TryHideCategoryLive — removed empty category '{tabName}' (enum={category}) from live table.");
                }

                if (Player.m_localPlayer != null)
                    VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAHammerTabOrganizer");
                VABuildUiCategoryTags.RequestBuildMenuRefresh();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.TryHideCategoryLive failed for '{tabName}': {ex.Message}");
                return false;
            }
        }

        public static bool UnhideTab(string hiddenTabName)
        {
            if (string.IsNullOrEmpty(hiddenTabName)) return false;
            if (!hiddenTabName.StartsWith(HiddenTabPrefix, StringComparison.Ordinal)) return false;
            string original = hiddenTabName.Substring(HiddenTabPrefix.Length);
            if (string.IsNullOrEmpty(original)) return false;

            _hiddenTabsLoaded.Remove(original);

            // No live re-show: re-registering the prefabs runs only at world load
            // (via SortHammerByMod), so unhide still needs a reload to restore them.
            return RenameTab(hiddenTabName, original);
        }

        // Returns every disk folder whose name starts with HiddenTabPrefix.
        // Used by the "Show hidden tabs" right-click action so the user
        // can pick one to un-hide.
        public static List<string> EnumerateHiddenTabs()
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(HammerTabsDir)) return result;
                foreach (var dir in Directory.GetDirectories(HammerTabsDir))
                {
                    string name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name)
                        && name.StartsWith(HiddenTabPrefix, StringComparison.Ordinal))
                    {
                        result.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: EnumerateHiddenTabs failed: {ex.Message}");
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // Legacy → canonical tab-name alias. Used during disk load so
        // user folders from the pre-consolidation build still route to
        // the new single FX / VFX tabs. Keeps old configs working
        // transparently; user can clean up the old folders at their
        // leisure.
        public static string MigrateLegacyTabName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return folderName;
            if (folderName == "FX (Vanilla)" || folderName == "FX (Modded)") return "FX";
            if (folderName == "VFX (Vanilla)" || folderName == "VFX (Modded)") return "VFX";
            return folderName;
        }

        // ──────────────────────────────────────────────────────────────
        //  Load (called at start of InitializeOnce)
        // ──────────────────────────────────────────────────────────────

        // Walks HammerTabs/<TabName>/prefabs.txt and builds the override map.
        // First-folder-wins on duplicate prefab names (directory enumeration is
        // sorted for determinism). Idempotent: clears prior state and re-reads.
        public static void LoadFromDisk()
        {
            _prefabToTabOverride.Clear();
            _knownTabNames.Clear();
            _tabHeaderComments.Clear();
            _ignoredPrefabs.Clear();
            _bundleAliases.Clear();
            _prefabsByTab.Clear();
            _prefabOrderIndex.Clear();
            _skippedBundles.Clear();
            _hiddenViaTabFolder = 0;
            _hiddenTabsLoaded.Clear();
            _loaded = true;

            try
            {
                if (!Directory.Exists(HammerTabsDir))
                {
                    Directory.CreateDirectory(HammerTabsDir);
                }

                // Run on EVERY load — each call is idempotent and skips when already
                // up-to-date, so README/folder updates reach existing users.
                WriteRootReadme();
                EnsureIgnoreFolder();
                EnsureSkipFolder();

                // Favorites: the tab is ALWAYS registered (even empty) so the
                // right-click toggle has somewhere to land from the very first
                // session; membership loads from favorites.txt alongside.
                _knownTabNames.Add(FavoritesTabName);
                LoadFavorites();

                string[] tabDirs;
                try { tabDirs = Directory.GetDirectories(HammerTabsDir); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer enumerate failed on '{HammerTabsDir}': {ex.Message}");
                    return;
                }
                Array.Sort(tabDirs, StringComparer.Ordinal);

                foreach (var dir in tabDirs)
                {
                    string tabName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(tabName)) continue;
                    if (tabName.Equals("All", StringComparison.Ordinal))
                    {
                        // 'All' is vanilla's filter-all sentinel — never
                        // a real tab; skip the folder if a user creates
                        // one with that name.
                        continue;
                    }
                    if (tabName.Equals(FavoritesTabName, StringComparison.Ordinal))
                    {
                        // Favorites/prefabs.txt is a BOOKMARK list, already read by
                        // LoadFavorites above — never membership overrides (a favorited
                        // piece stays in its home tab) and never a re-registration.
                        continue;
                    }

                    // Hidden-tab marker (renamed via the right-click "Hide tab" action).
                    // Treat its prefabs.txt as an extension of the Ignore list so the
                    // listed names are filtered at IsEligible and the tab can't regenerate.
                    // STRICTLY per-name — never promote to a bundle-wide skip (the user may
                    // have split a bundle across other tabs); whole-bundle hide is Skip's job.
                    if (tabName.StartsWith(HiddenTabPrefix, StringComparison.Ordinal))
                    {
                        _hiddenTabsLoaded.Add(tabName.Substring(HiddenTabPrefix.Length));
                        string hiddenFile = Path.Combine(dir, PrefabsFileName);
                        if (File.Exists(hiddenFile))
                        {
                            string[] hiddenLines;
                            try { hiddenLines = File.ReadAllLines(hiddenFile); }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer hidden-folder read failed for '{hiddenFile}': {ex.Message}");
                                continue;
                            }
                            foreach (var rawHidden in hiddenLines)
                            {
                                string lineHidden = rawHidden == null ? string.Empty : rawHidden.Trim();
                                if (lineHidden.Length == 0) continue;
                                if (lineHidden.StartsWith("#", StringComparison.Ordinal)) continue;
                                _ignoredPrefabs.Add(lineHidden);
                                _hiddenViaTabFolder++;
                            }
                        }
                        continue;
                    }

                    // Merge legacy 4-way FX/VFX folder names into the canonical FX/VFX.
                    tabName = MigrateLegacyTabName(tabName);

                    // Special-case folders:
                    //   Ignore     — per-name hide; listed prefabs go to _ignoredPrefabs.
                    //   Skip       — per-name hide PLUS bundle-alias detection (whole bundle).
                    //   Blueprints — content folder for .blueprint/.vbuild files, not a
                    //                prefabs.txt; any prefabs.txt here is stale and deleted.
                    bool isIgnoreFolder = tabName.Equals(IgnoreFolderName, StringComparison.Ordinal);
                    bool isSkipFolder = tabName.Equals(SkipFolderName, StringComparison.Ordinal);
                    bool isBlueprintsFolder = tabName.Equals(BlueprintsFolderName, StringComparison.Ordinal);
                    bool isSpecialFolder = isIgnoreFolder || isSkipFolder || isBlueprintsFolder;

                    // Vanilla-category folders (Misc, Crafting, BuildingWorkbench, ...)
                    // map onto the REAL vanilla categories — membership + ordering load
                    // like any tab, but they never register as custom tabs.
                    if (!isSpecialFolder && !IsVanillaTabName(tabName))
                    {
                        _knownTabNames.Add(tabName);
                    }

                    // Delete any stale prefabs.txt left in Blueprints by older FAP
                    // versions (the folder is for .blueprint files only).
                    if (isBlueprintsFolder)
                    {
                        string staleFile = Path.Combine(dir, PrefabsFileName);
                        if (File.Exists(staleFile))
                        {
                            try
                            {
                                File.Delete(staleFile);
                                Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer — deleted stale '{PrefabsFileName}' from Blueprints folder (legacy auto-saved BP_xxx stub names). The folder is for .blueprint files only.");
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer — could not delete stale Blueprints/{PrefabsFileName}: {ex.Message}");
                            }
                        }
                        continue;
                    }

                    string file = Path.Combine(dir, PrefabsFileName);
                    if (!File.Exists(file)) continue;

                    string[] lines;
                    try { lines = File.ReadAllLines(file); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer read failed for '{file}': {ex.Message}");
                        continue;
                    }

                    var headerComments = new List<string>();
                    bool stillInHeader = true;
                    foreach (var rawLine in lines)
                    {
                        string line = rawLine == null ? string.Empty : rawLine.Trim();

                        if (stillInHeader)
                        {
                            // Capture leading comments/blanks verbatim so they survive rewrite.
                            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                            {
                                headerComments.Add(rawLine ?? string.Empty);
                                continue;
                            }
                            stillInHeader = false;
                        }

                        if (line.Length == 0) continue;
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                        if (isSpecialFolder)
                        {
                            _ignoredPrefabs.Add(line);

                            // Skip also feeds alias detection so its single source bundle
                            // can be hidden wholesale; Ignore is per-name only.
                            if (isSkipFolder)
                            {
                                if (!_prefabsByTab.TryGetValue(tabName, out var perSkip))
                                {
                                    perSkip = new List<string>();
                                    _prefabsByTab[tabName] = perSkip;
                                }
                                perSkip.Add(line);
                            }
                        }
                        else
                        {
                            // First-folder-wins on duplicates (enumeration is alphabetical).
                            if (!_prefabToTabOverride.ContainsKey(line))
                                _prefabToTabOverride[line] = tabName;

                            if (!_prefabsByTab.TryGetValue(tabName, out var perTab))
                            {
                                perTab = new List<string>();
                                _prefabsByTab[tabName] = perTab;
                            }
                            perTab.Add(line);

                            // Line index within the winning tab = in-game order rank.
                            if (!_prefabOrderIndex.ContainsKey(line))
                                _prefabOrderIndex[line] = perTab.Count - 1;
                        }
                    }

                    _tabHeaderComments[tabName] = headerComments;
                }

                // ── Bundle-alias detection pass ──────────────────────
                // If every prefab in a folder comes from one source bundle (and the
                // folder was renamed off the bundle name), record folder-as-alias so
                // both existing and future prefabs from that bundle route to it.
                int aliasesDetected = 0;
                foreach (var kvp in _prefabsByTab)
                {
                    string tabName = kvp.Key;
                    var prefabList = kvp.Value;
                    if (prefabList == null || prefabList.Count == 0) continue;

                    string commonBundle = null;
                    bool allSameBundle = true;
                    foreach (var prefabName in prefabList)
                    {
                        if (!VAExternalBundleLoader.TryGetSourceBundle(prefabName, out var sourceBundle)
                            || string.IsNullOrEmpty(sourceBundle))
                        {
                            allSameBundle = false;
                            break;
                        }
                        if (commonBundle == null) commonBundle = sourceBundle;
                        else if (!string.Equals(commonBundle, sourceBundle, StringComparison.Ordinal))
                        {
                            allSameBundle = false;
                            break;
                        }
                    }

                    if (!allSameBundle) continue;
                    if (string.IsNullOrEmpty(commonBundle)) continue;
                    if (string.Equals(commonBundle, tabName, StringComparison.Ordinal)) continue;

                    _bundleAliases[commonBundle] = tabName;
                    aliasesDetected++;
                }

                // Promote aliases that target the Skip folder into full bundle-skips so
                // IsEligible filters every prefab from those bundles, present and future.
                // Only Skip promotes — Hidden_X stays strictly per-name (see above).
                int bundlesSkipped = 0;
                foreach (var kvp in _bundleAliases)
                {
                    if (kvp.Value.Equals(SkipFolderName, StringComparison.Ordinal))
                    {
                        _skippedBundles.Add(kvp.Key);
                        bundlesSkipped++;
                    }
                }

                // If both `<tab>/` and `Hidden_<tab>/` exist on disk, the hidden flag
                // wins — drop the name from _knownTabNames so no empty category registers.
                int conflictResolved = 0;
                foreach (var hidden in _hiddenTabsLoaded)
                {
                    if (_knownTabNames.Remove(hidden)) conflictResolved++;
                }
                if (conflictResolved > 0)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer — resolved {conflictResolved} tab name(s) that existed BOTH as <name>/ and Hidden_<name>/ on disk; hidden state wins.");
                }

                int hiddenViaIgnore = _ignoredPrefabs.Count - _hiddenViaTabFolder;
                if (hiddenViaIgnore < 0) hiddenViaIgnore = 0;
                Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer loaded {_prefabToTabOverride.Count} prefab override(s) across {_knownTabNames.Count} tab folder(s); " +
                    $"{_ignoredPrefabs.Count} prefab(s) ignored ({hiddenViaIgnore} via Ignore/prefabs.txt + {_hiddenViaTabFolder} via Hidden_*/prefabs.txt); " +
                    $"{_hiddenTabsLoaded.Count} hidden tab(s) restored from disk; " +
                    $"{aliasesDetected} bundle alias(es) detected (renamed folders); " +
                    $"{bundlesSkipped} bundle(s) fully skipped via Skip folder.");
                if (_hiddenTabsLoaded.Count > 0)
                {
                    var tabList = new List<string>(_hiddenTabsLoaded);
                    tabList.Sort(StringComparer.Ordinal);
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer hidden tabs persisted across restart: [{string.Join(", ", tabList)}]");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.LoadFromDisk failed: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Override lookup (called from AutoCategorize)
        // ──────────────────────────────────────────────────────────────

        // Returns true + the tab name if the prefab is in the disk
        // override map. AutoCategorize should call this BEFORE any other
        // categorization logic — disk overrides win.
        public static bool TryGetTabOverride(string prefabName, out string tabName)
        {
            if (!_loaded || string.IsNullOrEmpty(prefabName))
            {
                tabName = null;
                return false;
            }
            return _prefabToTabOverride.TryGetValue(prefabName, out tabName);
        }

        // ──────────────────────────────────────────────────────────────
        //  Save (called at end of InitializeOnce after scan completes)
        // ──────────────────────────────────────────────────────────────

        // Resolve a tab name (custom OR vanilla) to a piece category. Custom tabs go
        // through the registry; vanilla folder names parse straight to the enum.
        public static bool TryResolveTabCategory(string tabName, out Piece.PieceCategory category)
        {
            if (!string.IsNullOrEmpty(tabName))
            {
                if (VerdantsAscent.Patches.VACategoryRegistry.TryGet(tabName, out category)) return true;
                if (IsVanillaTabName(tabName) && Enum.TryParse(tabName, false, out category)) return true;
            }
            category = Piece.PieceCategory.Misc;
            return false;
        }

        // Apply disk tab overrides to EVERY piece on the table — vanilla builtins and
        // other mods' direct registrations included, not just FAP-injected prefabs.
        // This is what makes "cut the line into another tab's prefabs.txt" work for
        // ANY piece. Runs as an UpdateAvailable prefix (idempotent, dictionary-cheap).
        public static void ApplyCategoryOverrides(PieceTable table)
        {
            if (!_loaded || table == null || table.m_pieces == null) return;
            if (_prefabToTabOverride.Count == 0) return;

            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                var go = table.m_pieces[i];
                if (go == null) continue;
                // A published piece's tab comes from the server's PlayerPieces.yaml, which every
                // player shares; a local tab file would put it somewhere else on this machine only.
                if (Utilities.VAFapPlayerPieces.IsPublished(go.name)) continue;
                if (!_prefabToTabOverride.TryGetValue(go.name, out var tabName)) continue;
                if (!TryResolveTabCategory(tabName, out var cat)) continue;
                var piece = go.GetComponent<Piece>();
                if (piece != null && piece.m_category != cat) piece.m_category = cat;
            }
        }

        // Materialize the VANILLA categories as HammerTabs folders so Misc/Crafting/
        // Building*/Furniture can be reorganized and reordered from disk like any
        // custom tab. APPEND-ONLY like SaveCurrentLayout, and runs AFTER it so FAP
        // tab files claim their prefabs first: only pieces not listed anywhere on
        // disk get seeded here, in their current in-game order.
        public static void EnsureVanillaTabFolders(PieceTable table)
        {
            if (!_loaded || table == null || table.m_pieces == null) return;

            try
            {
                var onDiskNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in _prefabToTabOverride.Keys) onDiskNames.Add(name);
                foreach (var name in _ignoredPrefabs) onDiskNames.Add(name);

                var newByCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var go in table.m_pieces)
                {
                    if (go == null) continue;
                    string name = go.name;
                    if (onDiskNames.Contains(name) || !seenNames.Add(name)) continue;
                    var piece = go.GetComponent<Piece>();
                    if (piece == null) continue;
                    string catName = piece.m_category.ToString();
                    if (!_vanillaTabNames.Contains(catName)) continue;   // custom category → FAP tab files own it
                    if (!newByCategory.TryGetValue(catName, out var list))
                    {
                        list = new List<string>();
                        newByCategory[catName] = list;
                    }
                    list.Add(name);
                }

                foreach (var kvp in newByCategory)
                {
                    string dir = Path.Combine(HammerTabsDir, kvp.Key);
                    string file = Path.Combine(dir, PrefabsFileName);
                    Directory.CreateDirectory(dir);
                    if (!File.Exists(file))
                    {
                        var lines = new List<string>(kvp.Value.Count + 4)
                        {
                            "# Vanilla '" + kvp.Key + "' tab — one prefab name per line.",
                            "# LINE ORDER = IN-GAME ORDER. Cut/paste lines between tab files to",
                            "# move pieces (any tab, vanilla or custom). Applies on world reload.",
                        };
                        lines.AddRange(kvp.Value);
                        File.WriteAllLines(file, lines);
                    }
                    else
                    {
                        File.AppendAllLines(file, kvp.Value);
                    }

                    // Same-session backfill so the seeded order is queryable immediately
                    // (a no-op visually — seeding order IS the current order).
                    foreach (var name in kvp.Value)
                    {
                        if (!_prefabToTabOverride.ContainsKey(name)) _prefabToTabOverride[name] = kvp.Key;
                        if (!_prefabsByTab.TryGetValue(kvp.Key, out var perTab))
                        {
                            perTab = new List<string>();
                            _prefabsByTab[kvp.Key] = perTab;
                        }
                        perTab.Add(name);
                        if (!_prefabOrderIndex.ContainsKey(name)) _prefabOrderIndex[name] = perTab.Count - 1;
                    }
                }

                if (newByCategory.Count > 0)
                {
                    int seeded = 0;
                    foreach (var kvp in newByCategory) seeded += kvp.Value.Count;
                    Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer — seeded {seeded} piece name(s) into {newByCategory.Count} vanilla tab folder(s).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.EnsureVanillaTabFolders failed: {ex.Message}");
            }
        }

        // APPEND-ONLY. On-disk folders + listings are authoritative and never rewritten;
        // only prefabs not already on disk (in _prefabToTabOverride or _ignoredPrefabs)
        // get appended to their natural-category tab's prefabs.txt.
        public static void SaveCurrentLayout(IDictionary<string, GameObject> eligibleByName)
        {
            if (!_loaded) return;
            if (eligibleByName == null || eligibleByName.Count == 0) return;

            try
            {
                Directory.CreateDirectory(HammerTabsDir);

                var onDiskNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in _prefabToTabOverride.Keys) onDiskNames.Add(name);
                foreach (var name in _ignoredPrefabs) onDiskNames.Add(name);

                var newByTab = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                int unresolved = 0;
                int alreadyOnDisk = 0;
                foreach (var kvp in eligibleByName)
                {
                    if (onDiskNames.Contains(kvp.Key))
                    {
                        alreadyOnDisk++;
                        continue;
                    }
                    var prefab = kvp.Value;
                    if (prefab == null) continue;
                    var piece = prefab.GetComponent<Piece>();
                    if (piece == null) continue;

                    // FiresAdminTerrain's voxel tools get their OWN tab instead of following their
                    // category. They register as native Hammer pieces stamped PieceCategory.Misc
                    // (VoxelToolPieces.AddTool), which put all eight of them among 260-odd other
                    // entries in Misc - registered, permitted, and unfindable. Fire reported them as
                    // simply missing from the hammer and the hoe.
                    //
                    // Matched on the prefab-name prefix FAT itself assigns ("fires_vtool_<Name>"),
                    // which is a name we own rather than a heuristic about what a piece looks like.
                    // Doing it here rather than changing FAT's category keeps the two mods
                    // independent: FAT has no idea FAP exists, and FAP stays the one authority for
                    // where a piece lands.
                    string tabName;
                    if (kvp.Key.StartsWith(VoxelToolPrefabPrefix, StringComparison.Ordinal))
                    {
                        tabName = TerrainTabName;
                    }
                    else if (!VACategoryRegistry.TryGetName(piece.m_category, out tabName))
                    {
                        unresolved++;
                        continue;
                    }
                    // Never auto-place into Ignore/Skip (hide lists) or Blueprints
                    // (runtime-generated stubs that must not be persisted as overrides).
                    if (tabName.Equals(IgnoreFolderName, StringComparison.Ordinal)) continue;
                    if (tabName.Equals(SkipFolderName, StringComparison.Ordinal)) continue;
                    if (tabName.Equals(BlueprintsFolderName, StringComparison.Ordinal)) continue;

                    // Don't re-materialize a folder for a hidden tab — it would defeat
                    // the hide HideTab just applied by renaming it to Hidden_<tab>/.
                    if (IsTabHidden(tabName)) continue;

                    if (!newByTab.TryGetValue(tabName, out var set))
                    {
                        set = new SortedSet<string>(StringComparer.Ordinal);
                        newByTab[tabName] = set;
                    }
                    set.Add(kvp.Key);
                }

                int filesCreated = 0;
                int filesAppended = 0;
                int totalNewLines = 0;
                foreach (var kvp in newByTab)
                {
                    string tabName = kvp.Key;
                    string folder = Path.Combine(HammerTabsDir, VAFapFileNames.ToSafeFileName(tabName));
                    string file = Path.Combine(folder, PrefabsFileName);

                    try { Directory.CreateDirectory(folder); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer create-folder failed for '{folder}': {ex.Message}");
                        continue;
                    }

                    bool fileExisted = File.Exists(file);
                    var sb = new StringBuilder();

                    if (fileExisted)
                    {
                        // Append verbatim — keep existing content, ensure a trailing
                        // newline, then add the new lines. Never reorders or strips.
                        try
                        {
                            string existing = File.ReadAllText(file);
                            sb.Append(existing);
                            if (existing.Length > 0
                                && existing[existing.Length - 1] != '\n'
                                && existing[existing.Length - 1] != '\r')
                            {
                                sb.AppendLine();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer read-before-append failed for '{file}': {ex.Message}. Skipping this tab to avoid clobbering.");
                            continue;
                        }
                    }
                    else
                    {
                        sb.AppendLine($"# Hammer tab: {tabName}");
                        sb.AppendLine("# Auto-managed by FiresAdminPrefabs (FAP).");
                        sb.AppendLine("# One prefab name per line. Lines starting with # are comments.");
                        sb.AppendLine("# To move a prefab to a different tab: cut the line, paste into another tab's prefabs.txt.");
                        sb.AppendLine("# To rename this tab: rename the folder. Reload world to apply.");
                        sb.AppendLine("# FAP only APPENDS new entries — your edits are preserved verbatim across saves.");
                        sb.AppendLine();
                    }

                    foreach (var name in kvp.Value)
                    {
                        sb.AppendLine(name);
                        totalNewLines++;
                    }

                    try
                    {
                        File.WriteAllText(file, sb.ToString());
                        if (fileExisted) filesAppended++;
                        else filesCreated++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer write failed for '{file}': {ex.Message}");
                    }
                }

                Debug.Log($"{VaPieces.PluginName}: VAHammerTabOrganizer append-only save — {totalNewLines} new prefab line(s) across {newByTab.Count} tab(s) ({filesCreated} folder(s) created, {filesAppended} file(s) appended). {alreadyOnDisk} prefab(s) already on disk (left untouched), {unresolved} had no resolved category.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.SaveCurrentLayout failed: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Recategorize clutter (fap_recategorize_clutter command)
        // ──────────────────────────────────────────────────────────────

        // Moves every clutter-classified prefab whose disk-override tab isn't "Clutter"
        // into the Clutter folder, repairing prior saves that landed clutter in
        // bundle-named tabs. dryRun reports counts only; otherwise rewrites the source
        // files (lines removed), appends to Clutter/prefabs.txt, and reloads the map.
        public static (int moved, int sourceFolderCount, int unresolvedNames) RecategorizeClutter(bool dryRun)
        {
            if (!_loaded) return (0, 0, 0);

            var eligibleByName = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var pair in VAVanillaPieceManager.EligiblePieces)
            {
                if (pair.Value == null) continue;
                eligibleByName[pair.Key] = pair.Value;
            }

            // Group migrating names by source folder so each file is rewritten once.
            var movesBySourceFolder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int unresolvedNames = 0;
            foreach (var pair in _prefabToTabOverride)
            {
                string prefabName = pair.Key;
                string currentTab = pair.Value;

                if (currentTab.Equals("Clutter", StringComparison.Ordinal)) continue;

                if (!eligibleByName.TryGetValue(prefabName, out var prefabObject) || prefabObject == null)
                {
                    // No loaded GameObject (e.g. from an unloaded mod) — can't classify.
                    unresolvedNames++;
                    continue;
                }

                if (!VAVanillaPieceManager.IsClutterPrefab_Public(prefabObject)) continue;

                if (!movesBySourceFolder.TryGetValue(currentTab, out var list))
                {
                    list = new List<string>();
                    movesBySourceFolder[currentTab] = list;
                }
                list.Add(prefabName);
            }

            int totalMoves = 0;
            foreach (var kvp in movesBySourceFolder) totalMoves += kvp.Value.Count;

            if (dryRun || totalMoves == 0)
            {
                return (totalMoves, movesBySourceFolder.Count, unresolvedNames);
            }

            // ── Real run: strip migrating lines from each source, append to Clutter ──
            string clutterFolder = Path.Combine(HammerTabsDir, VAFapFileNames.ToSafeFileName("Clutter"));
            try { Directory.CreateDirectory(clutterFolder); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.RecategorizeClutter — create Clutter folder failed: {ex.Message}");
                return (0, 0, 0);
            }

            var allMigratedNames = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var kvp in movesBySourceFolder)
            {
                string sourceTab = kvp.Key;
                var namesToMove = new HashSet<string>(kvp.Value, StringComparer.Ordinal);

                string sourceFolder = Path.Combine(HammerTabsDir, VAFapFileNames.ToSafeFileName(sourceTab));
                string sourceFile = Path.Combine(sourceFolder, PrefabsFileName);
                if (!File.Exists(sourceFile))
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.RecategorizeClutter — source file missing: '{sourceFile}'. Skipping {namesToMove.Count} move(s).");
                    continue;
                }

                string[] lines;
                try { lines = File.ReadAllLines(sourceFile); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.RecategorizeClutter — read failed for '{sourceFile}': {ex.Message}");
                    continue;
                }

                var sb = new StringBuilder();
                foreach (var raw in lines)
                {
                    string trimmed = raw == null ? string.Empty : raw.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        sb.AppendLine(raw ?? string.Empty);
                        continue;
                    }
                    if (namesToMove.Contains(trimmed))
                    {
                        allMigratedNames.Add(trimmed);
                        continue;
                    }
                    sb.AppendLine(raw ?? string.Empty);
                }

                try { File.WriteAllText(sourceFile, sb.ToString()); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.RecategorizeClutter — write failed for '{sourceFile}': {ex.Message}");
                }
            }

            string clutterFile = Path.Combine(clutterFolder, PrefabsFileName);
            try
            {
                var sb = new StringBuilder();
                if (File.Exists(clutterFile))
                {
                    string existing = File.ReadAllText(clutterFile);
                    sb.Append(existing);
                    if (existing.Length > 0
                        && existing[existing.Length - 1] != '\n'
                        && existing[existing.Length - 1] != '\r')
                    {
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine("# Hammer tab: Clutter");
                    sb.AppendLine("# Auto-managed by FiresAdminPrefabs (FAP).");
                    sb.AppendLine("# One prefab name per line. Lines starting with # are comments.");
                    sb.AppendLine();
                }
                // Stamp the migrated block so the user can see where these came from.
                sb.AppendLine($"# --- recategorized via fap_recategorize_clutter on {DateTime.Now:yyyy-MM-dd HH:mm} ---");
                foreach (var name in allMigratedNames) sb.AppendLine(name);

                File.WriteAllText(clutterFile, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.RecategorizeClutter — write to Clutter file failed: {ex.Message}");
            }

            // Refresh the in-memory override map so later AutoCategorize calls see it.
            LoadFromDisk();

            return (totalMoves, movesBySourceFolder.Count, unresolvedNames);
        }

        // ──────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────

        // Creates HammerTabs/Ignore/ + a header-only prefabs.txt when missing.
        // SaveCurrentLayout never writes here; the user fills in names to hide.
        private static void EnsureIgnoreFolder()
        {
            try
            {
                string ignoreDir = Path.Combine(HammerTabsDir, IgnoreFolderName);
                if (!Directory.Exists(ignoreDir)) Directory.CreateDirectory(ignoreDir);

                string ignoreFile = Path.Combine(ignoreDir, PrefabsFileName);
                if (File.Exists(ignoreFile)) return;

                var sb = new StringBuilder();
                sb.AppendLine("# Ignore — per-prefab-name hide list");
                sb.AppendLine("#");
                sb.AppendLine("# Add prefab names here (one per line) to hide them from the");
                sb.AppendLine("# hammer. Only the listed names are hidden — other prefabs from");
                sb.AppendLine("# the same bundle remain visible.");
                sb.AppendLine("#");
                sb.AppendLine("# To hide a WHOLE bundle, use the Skip/ folder instead.");
                sb.AppendLine("# See ../README.txt for the full workflow guide.");
                sb.AppendLine("#");
                sb.AppendLine("# Reload the world to apply changes.");
                sb.AppendLine();

                File.WriteAllText(ignoreFile, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.EnsureIgnoreFolder failed: {ex.Message}");
            }
        }

        // Sibling to EnsureIgnoreFolder for HammerTabs/Skip/. Unlike Ignore, a
        // single-bundle Skip folder hides the whole bundle, including future prefabs.
        private static void EnsureSkipFolder()
        {
            try
            {
                string skipDir = Path.Combine(HammerTabsDir, SkipFolderName);
                if (!Directory.Exists(skipDir)) Directory.CreateDirectory(skipDir);

                string skipFile = Path.Combine(skipDir, PrefabsFileName);
                if (File.Exists(skipFile)) return;

                var sb = new StringBuilder();
                sb.AppendLine("# Skip — bundle-wide hide");
                sb.AppendLine("#");
                sb.AppendLine("# Drop a whole bundle's prefabs in here to hide the entire");
                sb.AppendLine("# bundle from the hammer. The easiest way: rename the bundle's");
                sb.AppendLine("# auto-generated folder to 'Skip' (e.g. 'bong/' -> 'Skip/').");
                sb.AppendLine("#");
                sb.AppendLine("# When all prefabs in this folder come from a single bundle,");
                sb.AppendLine("# FAP aliases that bundle to Skip — every prefab from it is");
                sb.AppendLine("# filtered out, INCLUDING future prefabs from mod updates.");
                sb.AppendLine("#");
                sb.AppendLine("# To hide only specific prefab NAMES, use the Ignore/ folder.");
                sb.AppendLine("# See ../README.txt for the full workflow guide.");
                sb.AppendLine("#");
                sb.AppendLine("# Reload the world to apply changes.");
                sb.AppendLine();

                File.WriteAllText(skipFile, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer.EnsureSkipFolder failed: {ex.Message}");
            }
        }

        // Version-tagged README, rewritten when the on-disk first line doesn't match
        // the current version tag so updates reach existing users on next load.
        // Bump README_VERSION whenever the README content meaningfully changes.
        private const string README_VERSION = "v4 (2026-07-21)";
        private const string README_VERSION_MARKER = "# FAP HammerTabs README ";

        private static void WriteRootReadme()
        {
            string readmePath = Path.Combine(HammerTabsDir, "README.txt");
            try
            {
                // Leave it alone if the first line already matches the current version
                // tag, in case the user added their own notes below it.
                if (File.Exists(readmePath))
                {
                    try
                    {
                        using (var reader = new StreamReader(readmePath))
                        {
                            string firstLine = reader.ReadLine() ?? string.Empty;
                            string expectedFirst = README_VERSION_MARKER + README_VERSION;
                            if (firstLine.Trim() == expectedFirst.Trim()) return;
                        }
                    }
                    catch { /* fall through to rewrite */ }
                }

                File.WriteAllText(readmePath,
@"# FAP HammerTabs README " + README_VERSION + @"

FiresAdminPrefabs — Hammer tab organization
===========================================

This folder controls how prefabs are grouped into hammer tabs.
Each SUBFOLDER here = ONE TAB in the hammer. The folder name IS the
tab name as it appears in the hammer UI.

Inside each tab folder, `prefabs.txt` lists the prefabs that go on
that tab. Format:

    # Comments start with hash
    PrefabName1
    PrefabName2
    ...

LINE ORDER = IN-GAME ORDER. The pieces appear in the hammer in the
same order as their lines in the file — reorder the lines to reorder
the tab. Bulk moves are just cut/paste of lines between files.

VANILLA TABS TOO: the vanilla categories (Misc, Crafting,
BuildingWorkbench, BuildingStonecutter, Furniture, ...) materialize
here as folders seeded with their current contents, so the vanilla
tabs can be reorganized and reordered exactly like custom ones. Any
piece — vanilla, modded, or FAP-injected — can be moved between any
tabs by moving its line.


===== QUICK REFERENCE =====

  Goal                              Action
  ────────────────────────────────  ──────────────────────────────────────
  Rename a hammer tab               Rename the folder to your new tab name
  Move a prefab to a different tab  Cut its line from one prefabs.txt,
                                    paste into another's (works for
                                    vanilla + modded pieces too)
  Reorder pieces within a tab       Reorder the lines in its prefabs.txt
  Edit / reorder your favorites     Edit Favorites/prefabs.txt (bookmarks —
                                    pieces also stay in their home tabs)
  Create a new tab                  Make a new folder + prefabs.txt
  Delete a tab                      Delete the folder
  Add a blueprint to the hammer     Drop the .blueprint file into
                                    Blueprints/  (no prefabs.txt — the
                                    file itself is the entry)
  Hide ONE specific prefab          Add its name to Ignore/prefabs.txt
  Hide a WHOLE bundle               Rename the bundle's folder to ""Skip""
                                    (or move all its prefabs into Skip/)
  Un-hide                           Move the line back into a normal tab
  Apply any change                  Reload the world

If a prefab name appears in TWO files, the alphabetically-first
folder wins — after a cut/paste double-check the name really left
the source file.

YOU rename folders manually in your file explorer. FAP detects the
rename on next world load and updates the hammer accordingly.


===== HOW TO RENAME A TAB =====

Example: the auto-generated tab `start_scene` is ugly. You want it
to show up as `KrumStart` in the hammer instead.

  1. Browse to: BepInEx/config/FiresAdminPrefabs/HammerTabs/
  2. Find the folder named `start_scene`.
  3. Right-click → Rename → type `KrumStart`.
  4. Reload the world.
  5. The hammer now shows `KrumStart` as a tab. The old `start_scene`
     tab is gone. Every prefab from the start_scene bundle (current
     and future) routes to KrumStart automatically.

What's happening under the hood: FAP sees that all the prefabs in
the renamed folder came from the bundle `start_scene`, so it records
an alias: `start_scene → KrumStart`. The alias makes both existing
AND new prefabs from that bundle route to the renamed tab.


===== HOW TO HIDE A WHOLE BUNDLE (Skip) =====

Example: you don't want anything from the `bong` mod cluttering up
your hammer. You want the entire `bong` tab gone, and any future
prefabs from bong should also stay hidden.

  1. Browse to: BepInEx/config/FiresAdminPrefabs/HammerTabs/
  2. Find the folder named `bong`.
  3. Right-click → Rename → type `Skip`.
     (If a `Skip/` folder already exists, instead move the contents
      of `bong/prefabs.txt` into `Skip/prefabs.txt` and delete the
      empty bong folder.)
  4. Reload the world.
  5. The hammer no longer shows a `bong` tab. Every prefab from the
     bong bundle is filtered out at hammer eligibility — current
     prefabs AND any future ones from mod updates.

Note: the Skip folder is NOT itself a visible tab. It's just where
you put bundles you want gone.

LIMITATION: Skip detects a bundle-wide hide only when ALL prefabs in
Skip/ come from a single source bundle. To skip multiple bundles,
either rename them one at a time (reloading between each) or accept
that multi-bundle Skip only hides the LISTED prefab names (not
future ones from those bundles).


===== HOW TO ADD BLUEPRINTS TO THE HAMMER =====

Example: you have a 'AmazingNature.blueprint' file in your
PlanBuild blueprints folder and want it on the hammer's
Blueprints tab so you can place the whole structure with one click.

  1. Browse to: BepInEx/config/FiresAdminPrefabs/HammerTabs/Blueprints/
  2. Copy your .blueprint (or .vbuild) file in. Just the file —
     no rename, no prefabs.txt edits, nothing else.
  3. Reload the world.
  4. Open the hammer → 'Blueprints' tab. Your blueprint appears
     by its own name. Clicking places every piece it contains.

Supported formats: .blueprint (PlanBuild), .vbuild (BuildShare).

The Blueprints folder is the ONLY tab folder where you drop files
directly instead of editing a prefabs.txt. The .blueprint files
ARE the hammer entries — FAP reads them at world load and
generates the entries automatically.

If you see an old 'prefabs.txt' inside Blueprints/ from a previous
FAP version, ignore it — FAP will delete it on next world load.


===== HOW TO HIDE A SPECIFIC PREFAB (Ignore) =====

Example: there's one weird prefab `vfx_some_test_marker` showing
up in your VFX tab that you want gone. You DON'T want to hide the
rest of the bundle it came from.

  1. Browse to: BepInEx/config/FiresAdminPrefabs/HammerTabs/Ignore/
  2. Open `prefabs.txt` in a text editor.
  3. Add a line: `vfx_some_test_marker`
  4. Save. Reload the world.

The prefab is hidden from the hammer but other prefabs from its
bundle remain visible.

The Ignore folder is NOT itself a visible tab.


===== SPECIAL FOLDERS AT A GLANCE =====

  Ignore/      Per-name hide. Just the prefab names you list. Other
               prefabs from the same bundle stay visible. FAP never
               auto-fills this; everything in it is your choice.

  Skip/        Per-name hide PLUS bundle-wide hide when single-bundle.
               Drop a bundle's whole folder contents here to hide the
               entire bundle, including future prefabs from updates.

  Blueprints/  CONTENT folder — different from every other tab. Drop
               actual .blueprint or .vbuild files here (copy-paste
               from your PlanBuild folder). FAP reads each file
               directly, parses it, and creates a hammer entry
               using the blueprint's own name. NO prefabs.txt here —
               the .blueprint FILES themselves ARE the entries. The
               Blueprints tab IS visible in the hammer; its entries
               place every piece the blueprint contains.

Ignore and Skip do NOT appear as tabs in the hammer.
Blueprints DOES appear as a tab.


===== APPEND-ONLY SAVE =====

FAP NEVER rewrites your prefabs.txt files. It only APPENDS new
prefabs (from newly-installed mods or fresh bundle drops) into
their natural-fit tab's file.

This means your edits — line order, comments, grouping, blank
lines — are PRESERVED VERBATIM across every world load. FAP only
adds; it never removes or reorders what's already there.

If you delete a tab folder entirely, FAP recreates it only for
prefabs that aren't already accounted for somewhere else on disk.
A bundle whose prefabs you moved into another tab won't have its
original folder recreated.


===== PER-CLIENT =====

These files are LOCAL to your client. Multiplayer servers do NOT
sync them. Every player organizes their hammer independently.


===== FIRST-RUN =====

On first launch, FAP populates this folder with one subfolder per
currently-active tab (Clutter, Nature, CreatorShop, FX/VFX variants,
plus one per loaded bundle when SortHammerByMod is on). Each
prefabs.txt is auto-filled with the prefabs FAP's heuristics
assigned to that tab.

After that first run, your edits are the source of truth.
");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAHammerTabOrganizer could not write README: {ex.Message}");
            }
        }
    }
}
