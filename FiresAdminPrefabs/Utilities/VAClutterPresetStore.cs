using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VerdantsAscent.Utilities
{
    // Loads and saves clutter presets under FAP's own config folder — EWP never reads
    // them, so they don't belong in its data folder. Definitions (clutter_presets.yaml,
    // written on save/delete) and the per-prefab active selection (clutter_active.yaml,
    // written on apply, restored at world load) are kept in separate files because they
    // change at different rates, which also lets an admin hand-edit the definitions
    // without touching which one is active.
    public static class VAClutterPresetStore
    {
        public static string DataDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs");
        public static string PresetsPath =>
            Path.Combine(DataDir, "clutter_presets.yaml");
        public static string ActivePath =>
            Path.Combine(DataDir, "clutter_active.yaml");

        private static readonly Dictionary<string, VAClutterPreset> _byName =
            new Dictionary<string, VAClutterPreset>(StringComparer.Ordinal);
        // clutterPrefab → currently-active preset name (empty = vanilla).
        private static readonly Dictionary<string, string> _activeByPrefab =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object _ioLock = new object();
        private static bool _loaded;

        public static IReadOnlyDictionary<string, VAClutterPreset> All => _byName;

        public static bool TryGet(string name, out VAClutterPreset preset)
        {
            preset = null;
            if (string.IsNullOrEmpty(name)) return false;
            return _byName.TryGetValue(name, out preset);
        }

        public static string GetActiveNameFor(string clutterPrefab)
        {
            if (string.IsNullOrEmpty(clutterPrefab)) return null;
            _activeByPrefab.TryGetValue(clutterPrefab, out var name);
            return name;
        }

        // Marks a preset as active for its target clutter prefab. Pass
        // null/empty preset name to clear (revert that clutter prefab
        // to vanilla defaults at next world-load).
        public static bool SetActive(string clutterPrefab, string presetName)
        {
            if (string.IsNullOrEmpty(clutterPrefab)) return false;
            string oldActive;
            lock (_ioLock)
            {
                if (!_loaded) LoadAll();
                _activeByPrefab.TryGetValue(clutterPrefab, out oldActive);
                if (string.IsNullOrEmpty(presetName))
                    _activeByPrefab.Remove(clutterPrefab);
                else
                    _activeByPrefab[clutterPrefab] = presetName;
                if (!WriteActive()) return false;
            }

            // Remove the old preset's injected clone from
            // ClutterSystem so deactivating a preset reverts to
            // vanilla-only (no orphan clones piling up across
            // preset switches). Live operation — the right-click
            // "Clear active" path completes immediately, no world
            // reload needed.
            if (!string.IsNullOrEmpty(oldActive)
                && !string.Equals(oldActive, presetName, StringComparison.Ordinal))
            {
                VAClutterConfigApplier.RemoveAddedEntry(oldActive);
            }
            return true;
        }

        public static void LoadAll()
        {
            lock (_ioLock)
            {
                _byName.Clear();
                _activeByPrefab.Clear();
                _loaded = true;

                LoadPresetsFile();
                LoadActiveFile();

                Debug.Log($"{VaPieces.PluginName}: VAClutterPresetStore loaded {_byName.Count} preset(s), {_activeByPrefab.Count} active mapping(s).");
            }
        }

        private static void LoadPresetsFile()
        {
            if (!File.Exists(PresetsPath)) return;
            string body;
            try { body = File.ReadAllText(PresetsPath); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPresetStore — failed to read {PresetsPath}: {ex.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(body)) return;

            List<VAClutterPreset> parsed;
            try { parsed = BuildDeserializer().Deserialize<List<VAClutterPreset>>(body); }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VAClutterPresetStore — yaml parse failed at {PresetsPath}: {ex.Message}");
                return;
            }
            if (parsed == null) return;

            foreach (var p in parsed)
            {
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                _byName[p.name] = p;
            }
        }

        private static void LoadActiveFile()
        {
            if (!File.Exists(ActivePath)) return;
            string body;
            try { body = File.ReadAllText(ActivePath); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPresetStore — failed to read {ActivePath}: {ex.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(body)) return;

            List<VAClutterActiveEntry> parsed;
            try { parsed = BuildDeserializer().Deserialize<List<VAClutterActiveEntry>>(body); }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VAClutterPresetStore — active yaml parse failed at {ActivePath}: {ex.Message}");
                return;
            }
            if (parsed == null) return;

            foreach (var entry in parsed)
            {
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.clutterPrefab)) continue;
                if (string.IsNullOrEmpty(entry.active)) continue;
                _activeByPrefab[entry.clutterPrefab] = entry.active;
            }
        }

        public static bool Save(VAClutterPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return false;
            lock (_ioLock)
            {
                if (!_loaded) LoadAll();
                _byName[preset.name] = preset;
                return WritePresets();
            }
        }

        public static bool Delete(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (_ioLock)
            {
                if (!_loaded) LoadAll();
                if (!_byName.Remove(name)) return false;
                var stale = new List<string>();
                foreach (var kv in _activeByPrefab)
                {
                    if (string.Equals(kv.Value, name, StringComparison.Ordinal))
                        stale.Add(kv.Key);
                }
                foreach (var key in stale) _activeByPrefab.Remove(key);
                bool ok = WritePresets();
                if (stale.Count > 0) ok &= WriteActive();
                return ok;
            }
        }

        private static bool WritePresets()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var sorted = new List<VAClutterPreset>(_byName.Values);
                sorted.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));

                string yaml = BuildSerializer().Serialize(sorted);
                string header =
                    "# Auto-managed by FiresAdminPrefabs — clutter preset definitions.\n" +
                    "# Each entry is a named override snapshot for a ClutterSystem.Clutter\n" +
                    "# field set. Apply via the right-click 'Configure clutter...' UI in the\n" +
                    "# hammer; the active selection per clutter prefab is tracked in\n" +
                    "# clutter_active.yaml (in this same folder).\n\n";

                string tmp = PresetsPath + ".tmp";
                File.WriteAllText(tmp, header + yaml);
                if (File.Exists(PresetsPath)) File.Delete(PresetsPath);
                File.Move(tmp, PresetsPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VAClutterPresetStore — preset write failed at {PresetsPath}: {ex.Message}");
                return false;
            }
        }

        private static bool WriteActive()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var entries = new List<VAClutterActiveEntry>();
                foreach (var kv in _activeByPrefab)
                {
                    entries.Add(new VAClutterActiveEntry
                    {
                        clutterPrefab = kv.Key,
                        active = kv.Value,
                    });
                }
                entries.Sort((a, b) => string.Compare(a.clutterPrefab, b.clutterPrefab, StringComparison.Ordinal));

                string yaml = BuildSerializer().Serialize(entries);
                string header =
                    "# Auto-managed by FiresAdminPrefabs — which clutter preset is currently\n" +
                    "# active per clutter prefab. Rewritten on every Apply click; loaded at\n" +
                    "# world-load to restore your selection across game restarts.\n\n";

                string tmp = ActivePath + ".tmp";
                File.WriteAllText(tmp, header + yaml);
                if (File.Exists(ActivePath)) File.Delete(ActivePath);
                File.Move(tmp, ActivePath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VAClutterPresetStore — active write failed at {ActivePath}: {ex.Message}");
                return false;
            }
        }

        // ── Hot reload ───────────────────────────────────────────────

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
                    Filter = "clutter_*.yaml",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnYamlChanged;
                _watcher.Created += OnYamlChanged;
                _watcher.Renamed += (_, e) => OnYamlChanged(_, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath) ?? string.Empty, Path.GetFileName(e.FullPath)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPresetStore — could not enable hot reload: {ex.Message}");
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
                // Re-apply all active presets to ClutterSystem so a
                // hand-edit takes effect without a world reload. The
                // applier handles the case where ClutterSystem isn't
                // ready yet (no current world) by no-op'ing.
                VAClutterConfigApplier.ApplyAllActive();
                Debug.Log($"{VaPieces.PluginName}: VAClutterPresetStore — hot-reloaded clutter presets and re-applied actives.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPresetStore — hot reload failed: {ex.Message}");
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
    }
}
