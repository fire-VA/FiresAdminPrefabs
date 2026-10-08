using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.169 (Fire 10-06: "Create data preset" on any prefab): a named, saved set of the field edits the extra-data UI offers (the
    // fields Valheim's own ZNetView.LoadFields applies, read from the prefab's real components by VAExtraFieldCatalogue). A data
    // preset is queued on the hammer like chest / monster presets and stays queued until changed. Stored in
    // config/data/data_FAP_datapresets.json (the game folder). Values are kept as the text the admin typed and parsed against
    // the prefab's current fields when armed, so a preset survives a field changing type (it then reports that field).
    public sealed class VADataPresetValue
    {
        public string Key = "";
        public string Text = "";
    }

    public sealed class VADataPreset
    {
        public string name = "";
        public string Prefab = "";
        public List<VADataPresetValue> Values = new List<VADataPresetValue>();
    }

    public static class VADataPresetStore
    {
        public static string DataFilePath => Path.Combine(VAChestPresetStore.DataDir, "data_FAP_datapresets.json");

        private static readonly Dictionary<string, VADataPreset> _byName = new Dictionary<string, VADataPreset>(StringComparer.Ordinal);
        private static bool _loaded;

        public static IEnumerable<VADataPreset> For(string prefabName)
        {
            EnsureLoaded();
            return _byName.Values.Where(p => p.Prefab == prefabName).OrderBy(p => p.name, StringComparer.Ordinal);
        }

        public static bool TryGet(string name, out VADataPreset preset)
        {
            EnsureLoaded();
            preset = null;
            return !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out preset);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(DataFilePath)) return;
                foreach (var p in JsonConvert.DeserializeObject<List<VADataPreset>>(File.ReadAllText(DataFilePath)) ?? new List<VADataPreset>())
                    if (p != null && !string.IsNullOrEmpty(p.name)) _byName[p.name] = p;
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: data presets could not be read: {ex.Message}"); }
        }

        public static bool Save(VADataPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return false;
            EnsureLoaded();
            _byName[preset.name] = preset;
            return Write();
        }

        public static bool Delete(string name)
        {
            EnsureLoaded();
            return _byName.Remove(name ?? "") && Write();
        }

        private static bool Write()
        {
            try
            {
                Directory.CreateDirectory(VAChestPresetStore.DataDir);
                string tmp = DataFilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_byName.Values.OrderBy(p => p.name).ToList(), Formatting.Indented));
                if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
                File.Move(tmp, DataFilePath);
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: data presets could not be saved: {ex.Message}"); return false; }
        }

        /// <summary>Parses a preset against the prefab's fields and arms it. null on success, else what is wrong.</summary>
        public static string Arm(VADataPreset preset)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(preset.Prefab) : null;
            if (prefab == null) return $"no prefab '{preset.Prefab}'";
            var fields = VAExtraFieldCatalogue.Build(prefab).Where(f => f.Settable).ToDictionary(f => f.Key);
            var values = new List<VAExtraValue>();
            foreach (var v in preset.Values)
            {
                if (!fields.TryGetValue(v.Key, out var field)) return $"{v.Key}: {preset.Prefab} has no such settable field now";
                if (!VAExtraFieldCatalogue.TryParse(field.Kind, v.Text, out object parsed, out string problem)) return $"{v.Key}: {problem}";
                values.Add(new VAExtraValue { Key = field.Key, Kind = field.Kind, Value = parsed });
            }
            VAExtraDataQueue.Arm(preset.Prefab, values, preset.name);
            return null;
        }
    }
}
