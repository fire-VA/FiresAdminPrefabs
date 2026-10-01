using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VerdantsAscent.Utilities
{
    // Loads and saves chest presets in config/data/data_FAP_chests.yaml, where EWP's
    // data-by-name lookup finds them with no registration step. This store is the source
    // of truth: the config UI writes through it, placement reads it, and
    // VAChestEwpRuleWriter regenerates the rule yaml from it.
    //
    // It deliberately parallels VAMonsterPresetStore rather than sharing a generic base:
    // the two differ in how they ensure each preset's name marker is present, and a
    // shared generic took more conditional branches than it saved.
    public static class VAChestPresetStore
    {
        public static string DataDir =>
            Path.Combine(Paths.ConfigPath, "data");
        public static string DataFilePath =>
            Path.Combine(DataDir, "data_FAP_chests.yaml");

        private static readonly Dictionary<string, VAChestPreset> _byName =
            new Dictionary<string, VAChestPreset>(StringComparer.Ordinal);
        private static readonly object _ioLock = new object();
        private static bool _loaded;

        public static IReadOnlyDictionary<string, VAChestPreset> All => _byName;
        public static int Count => _byName.Count;

        public static bool TryGet(string name, out VAChestPreset preset)
        {
            preset = null;
            if (string.IsNullOrEmpty(name)) return false;
            return _byName.TryGetValue(name, out preset);
        }

        public static void LoadAll()
        {
            lock (_ioLock)
            {
                _byName.Clear();
                _loaded = true;

                if (!File.Exists(DataFilePath))
                {
                    return;
                }

                string body;
                try { body = File.ReadAllText(DataFilePath); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAChestPresetStore — failed to read {DataFilePath}: {ex.Message}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(body)) return;

                List<VAChestPreset> parsed;
                try
                {
                    parsed = BuildDeserializer().Deserialize<List<VAChestPreset>>(body);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{VaPieces.PluginName}: VAChestPresetStore — yaml parse failed at {DataFilePath}: {ex.Message}");
                    return;
                }
                if (parsed == null) return;

                bool anyMigrated = false;
                foreach (var p in parsed)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    if (MigrateMalformedItems(p)) anyMigrated = true;
                    if (VAChestSlotPlan.MigrateSlots(p)) anyMigrated = true;
                    _byName[p.name] = p;
                }

                Debug.Log($"{VaPieces.PluginName}: VAChestPresetStore loaded {_byName.Count} chest preset(s) from {DataFilePath}.");

                // Persist the migrated state immediately. Without this,
                // EWP's file watcher would keep reading the broken yaml
                // from disk (because in-memory migration only fixes the
                // FAP-side preset dict, not what EWP reads). The save
                // re-emits the clean yaml; EWP's watcher picks up the
                // change on its next debounce cycle.
                if (anyMigrated)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAChestPresetStore — auto-saving after migration so EWP gets the cleaned yaml.");
                    WriteAll();
                }
            }
        }

        public static bool Save(VAChestPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return false;
            lock (_ioLock)
            {
                if (!_loaded) LoadAll();
                _byName[preset.name] = preset;
                return WriteAll();
            }
        }

        // Self-heals item entries the old textbox parser wrote wrong: when it found no
        // commas it stuffed a whole line ("Iron 1;3 1") into the prefab field. AddItem on
        // that name silently does nothing, so the chest spawned empty, va_chest_has_items
        // never flipped, and the respawn rule never fired. Any prefab field containing
        // whitespace is re-parsed through VALootTextFormat. Idempotent; returns true only
        // when something changed, so the caller knows to persist.
        private static bool MigrateMalformedItems(VAChestPreset preset)
        {
            if (preset?.items == null || preset.items.Count == 0) return false;

            bool needsFix = false;
            foreach (var item in preset.items)
            {
                if (item == null || string.IsNullOrEmpty(item.prefab)) continue;
                if (item.prefab.IndexOf(' ') >= 0 || item.prefab.IndexOf('\t') >= 0)
                {
                    needsFix = true;
                    break;
                }
            }
            if (!needsFix) return false;

            var rebuilt = new System.Collections.Generic.List<VAItemEntry>();
            foreach (var item in preset.items)
            {
                if (item == null || string.IsNullOrEmpty(item.prefab)) continue;
                if (item.prefab.IndexOf(' ') >= 0 || item.prefab.IndexOf('\t') >= 0)
                {
                    var reparsed = VALootTextFormat.Parse(item.prefab);
                    if (reparsed.Count == 0)
                    {
                        // Re-parse produced nothing — keep the broken
                        // entry rather than silently lose it. Admin
                        // will see it and can correct manually.
                        rebuilt.Add(item);
                    }
                    else
                    {
                        rebuilt.AddRange(reparsed);
                    }
                }
                else
                {
                    rebuilt.Add(item);
                }
            }
            preset.items = rebuilt;
            Debug.Log($"{VaPieces.PluginName}: VAChestPresetStore migrated malformed items in preset '{preset.name}'.");
            return true;
        }

        public static bool Delete(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (_ioLock)
            {
                if (!_loaded) LoadAll();
                if (!_byName.Remove(name)) return false;
                return WriteAll();
            }
        }

        private static bool WriteAll()
        {
            try
            {
                Directory.CreateDirectory(DataDir);

                var sorted = new List<VAChestPreset>(_byName.Values);
                sorted.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));

                // Ensure every preset's ints list carries `<presetName>, 1`
                // — the EWP destroy rule filters on `int, <presetName>, 1`
                // so each chest placed via the named data entry must
                // stamp that ZDO key. Same idempotent refresh as the
                // monster store's EnsurePresetNameIntPresent.
                foreach (var p in sorted)
                {
                    EnsurePresetNameIntPresent(p);
                }

                string yaml = BuildSerializer().Serialize(sorted);
                string header =
                    "# Auto-managed by FiresAdminPrefabs — chest preset definitions.\n" +
                    "# Edit by hand only when the game is closed (hot-reload IS supported but\n" +
                    "# overlapping writes between FAP's UI and your text editor can race).\n" +
                    "# Each entry is an EWP-format named data entry — EWP applies these via\n" +
                    "# rules in expand_world/expand_prefabs_FAP_chests.yaml (auto-generated).\n" +
                    "# Format: ints/floats/strings are lists of \"key, value\" strings; items[]\n" +
                    "# follows EWP's ItemData schema (prefab/stack/chance/quality/variant);\n" +
                    "# itemAmount picks N from M weighted by chance (unset = each item rolls\n" +
                    "# independently).\n\n";

                string tmp = DataFilePath + ".tmp";
                File.WriteAllText(tmp, header + yaml);
                if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
                File.Move(tmp, DataFilePath);
                VAEwpYamlValidator.Validate(DataFilePath, VAEwpYamlValidator.FileKind.Data);

                // Mirror the change up to the server (debounced, config-gated,
                // admin + remote-server only). See VAFapAssetSync.RequestAutoPush.
                VerdantsAscent.Sync.VAFapAssetSync.RequestAutoPush();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VAChestPresetStore — write failed at {DataFilePath}: {ex.Message}");
                return false;
            }
        }

        // ── Hot reload ────────────────────────────────────────────────

        private static FileSystemWatcher _watcher;
        private static DateTime _lastReload = DateTime.MinValue;

        public static void EnableHotReload()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (_watcher != null) return;
                _watcher = new FileSystemWatcher
                {
                    Path = DataDir,
                    Filter = Path.GetFileName(DataFilePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnYamlChanged;
                _watcher.Created += OnYamlChanged;
                _watcher.Renamed += (_, e) => OnYamlChanged(_, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath) ?? string.Empty, Path.GetFileName(e.FullPath)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAChestPresetStore — could not enable hot reload: {ex.Message}");
            }
        }

        private static void OnYamlChanged(object sender, FileSystemEventArgs e)
        {
            if ((DateTime.UtcNow - _lastReload).TotalMilliseconds < 500) return;
            _lastReload = DateTime.UtcNow;
            if (VaPieces.Instance == null) return;
            VaPieces.Instance.StartCoroutine(ReloadOnMainThread());
        }

        private static System.Collections.IEnumerator ReloadOnMainThread()
        {
            yield return null;
            try
            {
                LoadAll();
                // No rule regen — the generic chest rule is static
                // and was written once at mod load via EnsureFileExists.
                // Hot-editing the data yaml only needs to refresh the
                // in-memory preset store; EWP picks up the data yaml
                // changes via its own watcher.
                Debug.Log($"{VaPieces.PluginName}: VAChestPresetStore — hot-reloaded {_byName.Count} chest preset(s) from disk.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAChestPresetStore — hot reload failed: {ex.Message}");
            }
        }

        private static ISerializer BuildSerializer() =>
            new SerializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .DisableAliases()
                .Build();

        private static IDeserializer BuildDeserializer() =>
            new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

        // Stamps the preset name into the ints list as `<name>, 1` if
        // not already present. The EWP destroy rule filters on
        // `int, <presetName>, 1` so every chest placed via this preset
        // MUST carry that ZDO int. De-duplicates any stale copies on
        // re-save (e.g. rename followed by re-save under the new name).
        //
        // ALSO ensures the strings list carries
        // `va_chest_preset, <name>` so EWP re-applies the marker on
        // every respawn. Container_Awake_VAAutoDestroy_Patch reads
        // this string to decide whether to force m_autoDestroyEmpty
        // = true on the spawned Container instance — without the
        // string in the data entry the respawned chest would lose
        // its auto-destroy behavior and EWP would never see the
        // empty-loot destroy event.
        private static void EnsurePresetNameIntPresent(VAChestPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return;
            if (preset.ints == null) preset.ints = new List<string>();
            if (preset.strings == null) preset.strings = new List<string>();
            if (preset.bools == null) preset.bools = new List<string>();

            // Universal FAP-chest marker. The generic destroy rule in
            // expand_prefabs_FAP_chests.yaml filters on `int va_chest, 1`
            // so ONE rule covers every chest preset — much simpler than
            // emitting per-preset rules and avoids regenerating the
            // file every time a preset is added/removed.
            EnsureInt(preset, VerdantsAscent.Patches.VAChestConfigApplier.KeyChestMarker, 1);

            // Vanilla auto-destroy plumbing via ZNetView.LoadFields.
            // Setting Container.m_autoDestroyEmpty=true makes vanilla's
            // CheckForChanges (1s poll) destroy the chest the moment
            // it's empty AND not in use — exactly what loot crates
            // dropped from wagons/ships do. EWP catches the resulting
            // destroy event via HandleDestroyed and our `type: destroy`
            // rule fires the respawn. No custom items-string watcher
            // needed.
            //
            // The two HasFields bools are vanilla LoadFields gates:
            // HasFields=true tells vanilla to scan components for ZDO-
            // applied fields; HasFieldsContainer=true scopes the scan
            // to the Container component. Without both, vanilla skips
            // the load and m_autoDestroyEmpty stays at the prefab
            // default (false for normal chests).
            EnsureBool(preset, "HasFields", true);
            EnsureBool(preset, "HasFieldsContainer", true);
            EnsureBool(preset, "Container.m_autoDestroyEmpty", true);

            // --- strings: ensure `va_chest_preset, <name>` exactly once ---
            string desiredPresetLine = $"va_chest_preset, {preset.name}";
            bool presetMarkerPresent = false;
            for (int i = preset.strings.Count - 1; i >= 0; i--)
            {
                var line = preset.strings[i];
                if (string.IsNullOrEmpty(line)) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                string key = line.Substring(0, comma).Trim();
                string value = line.Substring(comma + 1).Trim();
                if (key == "va_chest_preset")
                {
                    // Keep only the entry whose value matches the
                    // current preset name. Drop stale copies from
                    // renames so the spawned chest doesn't end up
                    // tagged with two different preset names.
                    if (value == preset.name && !presetMarkerPresent)
                        presetMarkerPresent = true;
                    else
                        preset.strings.RemoveAt(i);
                }
            }
            if (!presetMarkerPresent)
            {
                preset.strings.Add(desiredPresetLine);
            }
        }

        // Idempotent bool-entry seeder. EWP-format bool lines look
        // like "key, true|false". De-dup on key, replace stale value.
        private static void EnsureBool(VAChestPreset preset, string key, bool value)
        {
            if (preset?.bools == null) return;
            string desired = $"{key}, {(value ? "true" : "false")}";
            bool found = false;
            for (int i = preset.bools.Count - 1; i >= 0; i--)
            {
                var line = preset.bools[i];
                if (string.IsNullOrEmpty(line)) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                string lineKey = line.Substring(0, comma).Trim();
                if (lineKey == key)
                {
                    if (found) preset.bools.RemoveAt(i);
                    else { preset.bools[i] = desired; found = true; }
                }
            }
            if (!found) preset.bools.Add(desired);
        }

        // Idempotent int-entry seeder. EWP-format int lines look like
        // "key, N" — we de-dup on the key and overwrite the value so
        // re-running this is safe (covers stale hand-edits).
        private static void EnsureInt(VAChestPreset preset, string key, int value)
        {
            if (preset?.ints == null) return;
            string desired = $"{key}, {value}";
            bool found = false;
            for (int i = preset.ints.Count - 1; i >= 0; i--)
            {
                var line = preset.ints[i];
                if (string.IsNullOrEmpty(line)) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                string lineKey = line.Substring(0, comma).Trim();
                if (lineKey == key)
                {
                    if (found)
                        preset.ints.RemoveAt(i);
                    else
                    {
                        preset.ints[i] = desired;
                        found = true;
                    }
                }
            }
            if (!found) preset.ints.Add(desired);
        }
    }
}
