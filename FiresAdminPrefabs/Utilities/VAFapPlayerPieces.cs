using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VerdantsAscent.Utilities
{
    // Baked pieces an admin publishes for players to build, listed in
    // config/FiresAdminPrefabs/PlayerPieces.yaml.
    //
    // Nothing reaches a player unless it is listed: every bake stays admin content by default.
    // Entries are matched by the baked prefab's name, which a rebake of the same blueprint
    // keeps, so an entry follows its blueprint through every rebake.
    //
    // Only the server's file counts. It is read at world load and whenever it changes on disk,
    // and its text is a custom value on FiresAdminPrefabs' ConfigSync, which sends it to each
    // player as they join and to everyone on a change. A connected client never reads its own
    // copy, and leaving the world returns it to an empty list.
    public static class VAFapPlayerPieces
    {
        public sealed class Entry
        {
            public string prefab;
            public string name;
            public string description;
            public string tab;
            public string station;
            public List<string> cost = new List<string>();
        }

        private sealed class AdminDefaults
        {
            public GameObject Template;
            public string Name;
            public string Description;
            public Piece.Requirement[] Resources;
            public CraftingStation Station;
            public Piece.PieceCategory Category;
            public Piece.UsageTagFlags Usage;
            public bool ClipEverything;
            public bool AllowRotatedOverlap;
        }

        private const string FileName = "PlayerPieces.yaml";
        private const string SyncedValueIdentifier = "PlayerPieces";
        private const char CostSeparator = ':';
        private const char TagSeparator = ',';
        private const int CostItemIndex = 0;
        private const int CostAmountIndex = 1;
        private const int CostRefundIndex = 2;
        private const int CostMinimumParts = 2;
        private const float ReloadQuietSeconds = 0.3f;
        private const float ReloadPollSeconds = 0.25f;

        private const string TemplateText =
            "# Baked pieces players can build.\n" +
            "#\n" +
            "# Nothing reaches players unless it is listed here. Entries are matched by the baked\n" +
            "# prefab's name (piece_<blueprint>), so re-baking the same blueprint keeps its entry.\n" +
            "# On a server only the server's copy counts: it goes to every player as they join, and\n" +
            "# again whenever this file changes. Each piece's baked bundle must be on the server too.\n" +
            "#\n" +
            "#   prefab       the baked prefab's name, e.g. piece_StarterCabin (required)\n" +
            "#   name         name shown in the build menu\n" +
            "#   description  description shown in the build menu\n" +
            "#   tab          one or more build menu tags, e.g. Building or \"Building, Wall\":\n" +
            "#                Misc, Crafting, Building, Floor, Wall, Roof, Architecture, Furniture,\n" +
            "#                Lighting, Decor, Storage, Transport, Food, Meads, Feasts, Defense,\n" +
            "#                Stacks, Stairs, Doors, Seasonal. A FiresAdminPrefabs tab name works too.\n" +
            "#   station      crafting station that must be nearby, e.g. piece_workbench\n" +
            "#   cost         one Item:amount per line; add :false to keep it from refunding on removal\n" +
            "#\n" +
            "# - prefab: piece_StarterCabin\n" +
            "#   name: Starter Cabin\n" +
            "#   description: A small wooden shelter for short trips.\n" +
            "#   tab: Building\n" +
            "#   station: piece_workbench\n" +
            "#   cost:\n" +
            "#     - Wood:40\n" +
            "#     - Resin:4\n";

        public static string FilePath =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", FileName);

        private static CustomSyncedValue<string> _syncedYaml;
        private static readonly HashSet<string> _published = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AdminDefaults> _defaults =
            new Dictionary<string, AdminDefaults>(StringComparer.Ordinal);
        private static readonly HashSet<string> _warnedThisSession = new HashSet<string>(StringComparer.Ordinal);

        private static FileSystemWatcher _watcher;
        private static long _fileChangedTicks;
        private static bool _watchLoopStarted;

        public static bool IsPublished(string prefabName) =>
            !string.IsNullOrEmpty(prefabName) && _published.Contains(prefabName);

        internal static void AddToConfigSync(ConfigSync configSync)
        {
            _syncedYaml = new CustomSyncedValue<string>(configSync, SyncedValueIdentifier, string.Empty);
            _syncedYaml.ValueChanged += OnSyncedYamlChanged;
        }

        // Only meaningful once ZNet exists; callers that can run earlier wait for it.
        private static bool IsAuthority() => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool IsClient() => ZNet.instance != null && !ZNet.instance.IsServer();

        // Called as a world load begins registering bakes. Templates are rebuilt with admin
        // defaults on every load, so nothing applied to the previous world's templates carries over.
        internal static void BeginSession() => ForgetAppliedPieces();

        // Called once every bake is registered. That can be the very frame the world starts
        // loading, before ZNet exists to say whether this machine is the server, and guessing
        // wrong would apply a player's own copy of the file; so the role waits for ZNet.
        internal static void OnBakedPiecesRegistered()
        {
            if (ZNet.instance == null && VaPieces.Instance != null)
            {
                VaPieces.Instance.StartCoroutine(ApplyOnceNetworkReady());
                return;
            }
            ApplyForRole();
        }

        private static IEnumerator ApplyOnceNetworkReady()
        {
            while (ZNet.instance == null)
            {
                if (ZNetScene.instance == null) yield break;
                yield return null;
            }
            ApplyForRole();
        }

        // A player applies the list the server has sent so far, which is empty until the join
        // package delivers it; OnSyncedYamlChanged applies a list that arrives after this.
        private static void ApplyForRole()
        {
            try
            {
                if (IsAuthority())
                {
                    EnsureTemplateFile();
                    ReloadFromDisk();
                    EnsureWatching();
                }
                else
                {
                    ApplyServerList();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: {FileName} — applying after bake registration failed: {ex.Message}");
            }
        }

        // The server's list can arrive or change while this world's bakes are still registering;
        // OnBakedPiecesRegistered applies it once they are. The server applies its own file in
        // ReloadFromDisk.
        private static void OnSyncedYamlChanged()
        {
            if (!VABakeBundle.BakeComplete || !IsClient()) return;
            try
            {
                ApplyServerList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: {FileName} from the server could not be applied: {ex.Message}");
            }
        }

        // A list that doesn't parse leaves the last good one applied.
        private static void ApplyServerList()
        {
            if (!TryParse(_syncedYaml.Value, out var entries, out string error))
            {
                Debug.LogWarning($"{VaPieces.PluginName}: {FileName} from the server could not be read: {error}");
                return;
            }
            ApplyEntries(entries);
        }

        // ZNet is already gone here, so the server's copy returns to empty without sending
        // anything, and a player's returns to empty whether Core's disconnect restore runs
        // before or after this.
        private static void EndSession()
        {
            _syncedYaml.AssignLocalValue(string.Empty);
            ForgetAppliedPieces();
        }

        private static void ForgetAppliedPieces()
        {
            _published.Clear();
            _defaults.Clear();
            _warnedThisSession.Clear();
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy_EndPlayerPiecesSession_Patch
        {
            [HarmonyPostfix]
            public static void Postfix() => EndSession();
        }

        private static void ReloadFromDisk()
        {
            string yaml = string.Empty;
            try
            {
                if (File.Exists(FilePath)) yaml = File.ReadAllText(FilePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: {FileName} could not be read: {ex.Message}");
                return;
            }

            // A typo mid-edit keeps the last good list live instead of pulling every piece
            // out from under players.
            if (!TryParse(yaml, out var entries, out string error))
            {
                Debug.LogWarning($"{VaPieces.PluginName}: {FileName} has an error and was not applied — the previous list stays live. {error}");
                return;
            }

            _syncedYaml.Value = yaml;
            ApplyEntries(entries);
        }

        private static bool TryParse(string yaml, out List<Entry> entries, out string error)
        {
            entries = new List<Entry>();
            error = null;
            if (string.IsNullOrWhiteSpace(yaml)) return true;
            try
            {
                var parsed = BuildDeserializer().Deserialize<List<Entry>>(yaml);
                if (parsed != null)
                    foreach (var entry in parsed)
                        if (entry != null) entries.Add(entry);
                return true;
            }
            catch (YamlException ex)
            {
                error = $"line {ex.Start.Line}, column {ex.Start.Column}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static IDeserializer BuildDeserializer() =>
            new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

        // ── Applying ──────────────────────────────────────────────────

        private static void ApplyEntries(List<Entry> entries)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return;

            var nowPublished = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                string prefabName = entry.prefab?.Trim();
                var piece = ResolveBakedPiece(scene, prefabName);
                if (piece == null)
                {
                    WarnMissing(prefabName);
                    continue;
                }
                RememberAdminDefaults(prefabName, piece);
                ApplyEntry(prefabName, piece, entry);
                nowPublished.Add(prefabName);
            }

            foreach (string prefabName in new List<string>(_published))
                if (!nowPublished.Contains(prefabName)) RestoreAdminDefaults(scene, prefabName);

            _published.Clear();
            _published.UnionWith(nowPublished);
            RefreshBuildMenu();
            VaPieces.Log?.LogInfo($"{FileName}: {_published.Count} baked piece(s) published for players.");
        }

        private static Piece ResolveBakedPiece(ZNetScene scene, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (VABakeBundle.TryGetCacheKeyForPrefab(prefabName) == null) return null;
            var prefab = scene.GetPrefab(prefabName);
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        // Bakes are still registering until BakeComplete, so a miss before then is expected.
        private static void WarnMissing(string prefabName)
        {
            if (!VABakeBundle.BakeComplete) return;
            string key = "missing:" + prefabName;
            if (!_warnedThisSession.Add(key)) return;

            string where = IsAuthority()
                ? "Push it from the admin who baked it with fap_push_bundle, then restart the server."
                : "The server lists it but this client doesn't have its bundle yet.";
            Debug.LogWarning($"{VaPieces.PluginName}: {FileName} lists '{prefabName}', but no baked piece by that name is installed here. {where}");
        }

        private static void RememberAdminDefaults(string prefabName, Piece piece)
        {
            if (_defaults.TryGetValue(prefabName, out var existing) && existing.Template == piece.gameObject) return;
            _defaults[prefabName] = new AdminDefaults
            {
                Template = piece.gameObject,
                Name = piece.m_name,
                Description = piece.m_description,
                Resources = piece.m_resources,
                Station = piece.m_craftingStation,
                Category = piece.m_category,
                Usage = piece.m_usage,
                ClipEverything = piece.m_clipEverything,
                AllowRotatedOverlap = piece.m_allowRotatedOverlap,
            };
        }

        private static void RestoreAdminDefaults(ZNetScene scene, string prefabName)
        {
            if (!_defaults.TryGetValue(prefabName, out var defaults)) return;
            _defaults.Remove(prefabName);

            var prefab = scene.GetPrefab(prefabName);
            if (prefab == null || prefab != defaults.Template) return;
            var piece = prefab.GetComponent<Piece>();
            if (piece == null) return;

            piece.m_name = defaults.Name;
            piece.m_description = defaults.Description;
            piece.m_resources = defaults.Resources;
            piece.m_craftingStation = defaults.Station;
            piece.m_category = defaults.Category;
            piece.m_usage = defaults.Usage;
            piece.m_clipEverything = defaults.ClipEverything;
            piece.m_allowRotatedOverlap = defaults.AllowRotatedOverlap;
        }

        private static void ApplyEntry(string prefabName, Piece piece, Entry entry)
        {
            var defaults = _defaults[prefabName];

            piece.m_name = string.IsNullOrWhiteSpace(entry.name) ? defaults.Name : entry.name.Trim();
            piece.m_description = entry.description ?? defaults.Description;
            piece.m_resources = ParseCost(prefabName, entry.cost);
            piece.m_craftingStation = ResolveStation(prefabName, entry.station);
            ApplyTab(prefabName, piece, entry.tab, defaults);

            // Admin set-dressing may be dropped through anything. A piece players build follows
            // the normal rule, so it can't be placed through someone else's build.
            piece.m_clipEverything = false;
            piece.m_allowRotatedOverlap = false;
        }

        private static Piece.Requirement[] ParseCost(string prefabName, List<string> cost)
        {
            var requirements = new List<Piece.Requirement>();
            if (cost == null) return requirements.ToArray();

            foreach (string line in cost)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split(CostSeparator);
                if (parts.Length < CostMinimumParts
                    || !int.TryParse(parts[CostAmountIndex].Trim(), out int amount) || amount <= 0)
                {
                    WarnOnce($"cost:{prefabName}:{line}", $"{FileName}: '{prefabName}' cost '{line}' should look like Wood:40 — skipped.");
                    continue;
                }

                string itemName = parts[CostItemIndex].Trim();
                var item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemName) : null;
                var itemDrop = item != null ? item.GetComponent<ItemDrop>() : null;
                if (itemDrop == null)
                {
                    WarnOnce($"item:{prefabName}:{itemName}", $"{FileName}: '{prefabName}' costs '{itemName}', which isn't an item — skipped.");
                    continue;
                }

                bool refund = parts.Length <= CostRefundIndex
                    || !bool.TryParse(parts[CostRefundIndex].Trim(), out bool parsedRefund)
                    || parsedRefund;
                requirements.Add(new Piece.Requirement { m_resItem = itemDrop, m_amount = amount, m_recover = refund });
            }
            return requirements.ToArray();
        }

        private static CraftingStation ResolveStation(string prefabName, string station)
        {
            if (string.IsNullOrWhiteSpace(station)) return null;
            var stationPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(station.Trim()) : null;
            var craftingStation = stationPrefab != null ? stationPrefab.GetComponent<CraftingStation>() : null;
            if (craftingStation == null)
                WarnOnce($"station:{prefabName}:{station}", $"{FileName}: '{prefabName}' station '{station}' isn't a crafting station — it will need none.");
            return craftingStation;
        }

        // Valheim 1.0 lists pieces under the tags in Piece.m_usage; FiresAdminPrefabs' own tabs
        // are rows keyed by m_category. Build menu tags move the piece out of the Baked Meshes
        // row onto vanilla's; a FiresAdminPrefabs tab name moves it to that row instead.
        private static void ApplyTab(string prefabName, Piece piece, string tab, AdminDefaults defaults)
        {
            if (string.IsNullOrWhiteSpace(tab))
            {
                piece.m_category = defaults.Category;
                piece.m_usage = defaults.Usage;
                return;
            }

            if (TryParseUsageTags(tab, out var usage))
            {
                piece.m_usage = usage;
                piece.m_category = Piece.PieceCategory.Misc;
                return;
            }

            if (Patches.VACategoryRegistry.TryGet(tab.Trim(), out var fapTab))
            {
                piece.m_category = fapTab;
                piece.m_usage = 0;
                return;
            }

            piece.m_category = defaults.Category;
            piece.m_usage = defaults.Usage;
            WarnOnce($"tab:{prefabName}:{tab}", $"{FileName}: '{prefabName}' tab '{tab}' is neither a build menu tag nor a FiresAdminPrefabs tab — left on its baked tab.");
        }

        private static bool TryParseUsageTags(string tab, out Piece.UsageTagFlags usage)
        {
            usage = 0;
            foreach (string part in tab.Split(TagSeparator))
            {
                string name = part.Trim();
                if (name.Length == 0) continue;
                if (!Enum.TryParse(name, ignoreCase: true, out Piece.UsageTagFlags flag)
                    || !Enum.IsDefined(typeof(Piece.UsageTagFlags), flag))
                {
                    usage = 0;
                    return false;
                }
                usage |= flag;
            }
            return usage != 0;
        }

        private static void WarnOnce(string key, string message)
        {
            if (_warnedThisSession.Add(key)) Debug.LogWarning($"{VaPieces.PluginName}: {message}");
        }

        private static void RefreshBuildMenu()
        {
            if (VaPieces.IsDedicatedServer) return;
            var player = Player.m_localPlayer;
            if (player != null) player.UpdateAvailablePiecesList();
            Patches.VABuildUiCategoryTags.RequestBuildMenuRefresh();
        }

        // ── Server file ───────────────────────────────────────────────

        private static void EnsureTemplateFile()
        {
            try
            {
                if (File.Exists(FilePath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, TemplateText, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: {FileName} template could not be written: {ex.Message}");
            }
        }

        // The watcher raises on a pool thread, often several times for one save, and can fire
        // before the writer has finished. It only stamps the time; the reload runs on the main
        // thread once the file has been quiet long enough to be complete.
        private static void EnsureWatching()
        {
            if (_watcher == null)
            {
                try
                {
                    _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath), FileName)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    };
                    _watcher.Changed += (_, __) => StampChange();
                    _watcher.Created += (_, __) => StampChange();
                    _watcher.Deleted += (_, __) => StampChange();
                    _watcher.Renamed += (_, __) => StampChange();
                    _watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: {FileName} changes won't apply live — the watcher failed: {ex.Message}");
                    _watcher = null;
                }
            }

            if (_watchLoopStarted || VaPieces.Instance == null) return;
            _watchLoopStarted = true;
            VaPieces.Instance.StartCoroutine(ReloadWhenQuiet());
        }

        private static void StampChange() => Interlocked.Exchange(ref _fileChangedTicks, DateTime.UtcNow.Ticks);

        private static IEnumerator ReloadWhenQuiet()
        {
            long quietTicks = TimeSpan.FromSeconds(ReloadQuietSeconds).Ticks;
            var poll = new WaitForSecondsRealtime(ReloadPollSeconds);
            while (true)
            {
                yield return poll;
                long changed = Interlocked.Read(ref _fileChangedTicks);
                if (changed == 0 || DateTime.UtcNow.Ticks - changed < quietTicks) continue;
                if (Interlocked.CompareExchange(ref _fileChangedTicks, 0, changed) != changed) continue;
                if (!IsAuthority()) continue;

                try { ReloadFromDisk(); }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: {FileName} live reload failed: {ex.Message}"); }
            }
        }
    }
}
