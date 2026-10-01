using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VerdantsAscent.Utilities
{
    // Loads and saves monster presets in config/data/data_FAP_presets.yaml. EWP reads
    // everything in config/data/ on its own, so living there makes each preset
    // available to EWP's data-by-name lookup with no registration step.
    //
    // This store is the source of truth: the config UI writes through it, placement
    // reads it, and VAMonsterEwpRuleWriter regenerates the rule yaml from it. Save and
    // Delete both re-emit the whole file atomically, and a FileSystemWatcher picks up
    // hand edits.
    public static class VAMonsterPresetStore
    {
        public static string DataDir =>
            Path.Combine(Paths.ConfigPath, "data");
        public static string DataFilePath =>
            Path.Combine(DataDir, "data_FAP_presets.yaml");

        private static readonly Dictionary<string, VAMonsterPreset> _byName =
            new Dictionary<string, VAMonsterPreset>(StringComparer.Ordinal);
        private static readonly object _ioLock = new object();
        private static bool _loaded;

        public static IReadOnlyDictionary<string, VAMonsterPreset> All => _byName;
        public static int Count => _byName.Count;

        public static bool TryGet(string name, out VAMonsterPreset preset)
        {
            preset = null;
            if (string.IsNullOrEmpty(name)) return false;
            return _byName.TryGetValue(name, out preset);
        }

        // Parses the on-disk yaml into the in-memory index. Idempotent
        // — re-running drains + repopulates. Safe to call from any
        // side (client / host / dedicated server); the file may not
        // exist yet (fresh install) which is treated as "empty store".
        public static void LoadAll()
        {
            lock (_ioLock)
            {
                _byName.Clear();
                _loaded = true;

                if (!File.Exists(DataFilePath))
                {
                    if (ConfigManagerVerbose())
                    {
                        Debug.Log($"{VaPieces.PluginName}: VAMonsterPresetStore — no preset yaml at {DataFilePath} (fresh install or none defined).");
                    }
                    return;
                }

                string body;
                try { body = File.ReadAllText(DataFilePath); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterPresetStore — failed to read {DataFilePath}: {ex.Message}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(body)) return;

                List<VAMonsterPreset> parsed;
                try
                {
                    parsed = BuildDeserializer().Deserialize<List<VAMonsterPreset>>(body);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{VaPieces.PluginName}: VAMonsterPresetStore — yaml parse failed at {DataFilePath}: {ex.Message}");
                    return;
                }
                if (parsed == null) return;

                bool anyMigrated = false;
                foreach (var p in parsed)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    if (MigrateLegacyVaDrops(p)) anyMigrated = true;
                    _byName[p.name] = p;
                }

                Debug.Log($"{VaPieces.PluginName}: VAMonsterPresetStore loaded {_byName.Count} monster preset(s) from {DataFilePath}.");

                // Persist migrated state so EWP reads the cleaned yaml
                // — without this, the in-memory fix is invisible to EWP
                // (which watches the file on disk). EWP's debounced
                // watcher picks up the change after the write.
                if (anyMigrated)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAMonsterPresetStore — auto-saving after migration so EWP gets the cleaned yaml.");
                    WriteAll();
                }
            }
        }

        // One-shot migration: presets saved before the EWP-native items[]
        // refactor stored their drops as `va_drops, prefab,min,max,chance|...`
        // in the strings list. After load, if the preset has a va_drops
        // string AND its items[] is empty, parse the pipe-string into
        // VAItemEntry rows. The next Save drops the va_drops string from
        // the strings list (items[] is now authoritative).
        //
        // Idempotent — if both are present, items[] wins and va_drops is
        // dropped. If neither is present, no-op. If only va_drops, migrate.
        // If only items[], no-op.
        // Returns true if the preset was actually modified (so the
        // caller knows to persist the fix). Returns false when no
        // migration was needed.
        private static bool MigrateLegacyVaDrops(VAMonsterPreset preset)
        {
            if (preset == null) return false;
            if (preset.items != null && preset.items.Count > 0)
            {
                // items[] already populated — just clean any stale
                // va_drops marker out of strings. Return true ONLY if
                // we actually removed something, so we don't trigger
                // a needless write on clean presets.
                return RemoveStringsKey(preset, "va_drops");
            }
            string legacy = preset.TryGetString("va_drops");
            if (string.IsNullOrEmpty(legacy)) return false;

            preset.items = new System.Collections.Generic.List<VAItemEntry>();
            var entries = legacy.Split('|');
            foreach (var raw in entries)
            {
                var fields = raw.Split(',');
                if (fields.Length < 1) continue;
                var prefab = fields[0].Trim();
                if (string.IsNullOrEmpty(prefab)) continue;

                var item = new VAItemEntry { prefab = prefab };
                if (fields.Length >= 3)
                {
                    var mn = fields[1].Trim();
                    var mx = fields[2].Trim();
                    if (!string.IsNullOrEmpty(mn) && !string.IsNullOrEmpty(mx))
                        item.stack = mn == mx ? mn : $"{mn};{mx}";
                }
                if (fields.Length >= 4)
                {
                    var chanceTxt = fields[3].Trim();
                    if (float.TryParse(chanceTxt, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var c))
                    {
                        item.chance = c;
                    }
                }
                preset.items.Add(item);
            }
            RemoveStringsKey(preset, "va_drops");
            return true;
        }

        // Returns true if any entries were removed (so callers can
        // decide whether to flag the preset as migration-dirty).
        private static bool RemoveStringsKey(VAMonsterPreset preset, string key)
        {
            if (preset?.strings == null) return false;
            bool removedAny = false;
            for (int i = preset.strings.Count - 1; i >= 0; i--)
            {
                var line = preset.strings[i];
                if (string.IsNullOrEmpty(line)) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (line.Substring(0, comma).Trim() == key)
                {
                    preset.strings.RemoveAt(i);
                    removedAny = true;
                }
            }
            return removedAny;
        }

        // Writes (or replaces) a single preset. Re-emits the entire
        // yaml file atomically. Caller is expected to follow up with
        // VAMonsterEwpRuleWriter.RegenerateRules() if EWP integration
        // is in play.
        public static bool Save(VAMonsterPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return false;
            lock (_ioLock)
            {
                if (!_loaded) LoadAll();
                _byName[preset.name] = preset;
                return WriteAll();
            }
        }

        // Removes a preset from the index + on-disk yaml.
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

        // Atomic write — emit the full preset list to a temp file then
        // move into place. Prevents a crashed mid-write from leaving a
        // truncated yaml that EWP would refuse to parse.
        private static bool WriteAll()
        {
            try
            {
                Directory.CreateDirectory(DataDir);

                // Stable ordering by name so diffs in source control
                // (or hand-inspecting the yaml) are clean across saves.
                var sorted = new List<VAMonsterPreset>(_byName.Values);
                sorted.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));

                // Ensure every preset's ints list carries
                // `<presetName>, 1` — the EWP destroy rule filters on
                // `int, <presetName>, 1` so each creature spawned via
                // the named data entry must stamp that ZDO key. Add or
                // refresh just before serialization so a rename (Save
                // under a different name) cleans up the old entry on
                // the next save cycle.
                foreach (var p in sorted)
                {
                    EnsurePresetNameIntPresent(p);
                }

                string yaml = BuildSerializer().Serialize(sorted);
                string header =
                    "# Auto-managed by FiresAdminPrefabs — monster preset definitions.\n" +
                    "# Edit by hand only when the game is closed (hot-reload IS supported but\n" +
                    "# overlapping writes between FAP's UI and your text editor can race).\n" +
                    "# Each entry is an EWP-format named data entry — EWP applies these via\n" +
                    "# rules in expand_world/expand_prefabs_FAP_presets.yaml (auto-generated).\n" +
                    "# Format: ints/floats/strings/vecs are lists of \"key, value\" strings.\n" +
                    "# FAP-only fields (va_drops, va_respawn_seconds, va_group_min/max) are\n" +
                    "# stamped to the ZDO but only FAP's Harmony patches read them — EWP\n" +
                    "# treats them as opaque write-through values.\n\n";

                string tmp = DataFilePath + ".tmp";
                File.WriteAllText(tmp, header + yaml);
                if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
                File.Move(tmp, DataFilePath);
                VAEwpYamlValidator.Validate(DataFilePath, VAEwpYamlValidator.FileKind.Data);

                // Mirror the change up to the server (debounced, config-gated,
                // admin + remote-server only). No-op on the server/host or
                // when not connected — see VAFapAssetSync.RequestAutoPush.
                VerdantsAscent.Sync.VAFapAssetSync.RequestAutoPush();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VAMonsterPresetStore — write failed at {DataFilePath}: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Hot reload — admin edits the yaml in a text editor, FAP
        //  picks up the change without restarting the game.
        // ─────────────────────────────────────────────────────────────

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
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterPresetStore — could not enable hot reload: {ex.Message}");
            }
        }

        private static void OnYamlChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce — Windows FileSystemWatcher fires Changed 2-3
            // times per save (write + flush + truncate). 500ms swallow.
            if ((DateTime.UtcNow - _lastReload).TotalMilliseconds < 500) return;
            _lastReload = DateTime.UtcNow;
            if (VaPieces.Instance == null) return;
            VaPieces.Instance.StartCoroutine(ReloadOnMainThread());
        }

        private static System.Collections.IEnumerator ReloadOnMainThread()
        {
            // One frame so the editor's writer fully releases the file.
            yield return null;
            try
            {
                LoadAll();
                // Regenerate the EWP rule file so any preset-rename or
                // respawn-delay change propagates to EWP without a
                // world reload.
                VAMonsterEwpRuleWriter.RegenerateRules();
                Debug.Log($"{VaPieces.PluginName}: VAMonsterPresetStore — hot-reloaded {_byName.Count} preset(s) from disk.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterPresetStore — hot reload failed: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  YamlDotNet setup
        // ─────────────────────────────────────────────────────────────

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

        // Defensive read of FAP's verbose flag — ConfigManager.Instance
        // is null on very-early calls (before BindConfigs runs).
        private static bool ConfigManagerVerbose()
        {
            try { return VerdantsAscent.Managers.ConfigManager.Instance?.configVerboseLogging?.Value ?? false; }
            catch { return false; }
        }

        // Walks the preset's ints list, removes any stale
        // `<otherName>, 1` markers we might have stamped previously
        // (where <otherName> != current preset name — handles renames),
        // then ensures the current name is present as `<presetName>, 1`.
        // Idempotent — safe to call every save.
        private static void EnsurePresetNameIntPresent(VAMonsterPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return;
            if (preset.ints == null) preset.ints = new List<string>();

            string desiredLine = $"{preset.name}, 1";

            // Drop any line whose key matches an OLD preset-name marker.
            // We can't distinguish a rename's stale entry from an admin's
            // intentional same-pattern entry, so we keep the heuristic
            // conservative: only remove lines that match the EXACT
            // "<key>, 1" shape AND whose key isn't one of the well-known
            // ZDO fields (level, override_boss, Humanoid.m_boss,
            // va_group_min/max). Anything else stays put.
            //
            // The clean way to handle this would be to namespace our
            // marker (e.g. "va_preset__<name>") but the user asked for
            // the bare preset name as the key, so we live with the
            // ambiguity and prefer false negatives (keep extra lines)
            // over false positives (delete admin data).
            bool alreadyPresent = false;
            for (int i = preset.ints.Count - 1; i >= 0; i--)
            {
                var line = preset.ints[i];
                if (string.IsNullOrEmpty(line)) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                string key = line.Substring(0, comma).Trim();
                string value = line.Substring(comma + 1).Trim();
                if (key == preset.name && value == "1")
                {
                    if (alreadyPresent)
                    {
                        // Duplicate of the current marker — drop the
                        // extra copy.
                        preset.ints.RemoveAt(i);
                    }
                    else
                    {
                        alreadyPresent = true;
                    }
                }
            }
            if (!alreadyPresent)
            {
                preset.ints.Add(desiredLine);
            }
        }
    }
}
