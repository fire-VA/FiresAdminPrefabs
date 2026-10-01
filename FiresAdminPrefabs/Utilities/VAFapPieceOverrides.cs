using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Disk-backed per-piece configuration store. Each entry can carry a
    // render-distance override (enrolls the prefab in distant-render
    // scanning) and/or a shader-name override applied to every non-particle
    // renderer's sharedMaterials. JSON sits under
    // BepInEx/config/FiresAdminPrefabs/PieceOverrides.json.
    //
    // Both fields are optional; entries with neither populated get pruned
    // on write. OnOverridesChanged fires after every mutation so
    // DistantPieceRenderer rebuilds its scan list and VAPieceShaderOverrider
    // re-applies to live instances.
    public static class VAFapPieceOverrides
    {
        public const float MaxDistanceM = 1500f;
        public const float MinDistanceM = 0f;

        private static readonly Dictionary<string, PieceOverride> _byPrefab =
            new Dictionary<string, PieceOverride>(StringComparer.Ordinal);

        public static event Action OnOverridesChanged;

        private static bool _saveQueued;

        private static string OverridesPath =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "PieceOverrides.json");

        public static bool TryGetDistance(string prefabName, out float distanceM)
        {
            distanceM = 0f;
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (!_byPrefab.TryGetValue(prefabName, out var o)) return false;
            if (o.renderDistanceM <= 0f) return false;
            distanceM = Mathf.Clamp(o.renderDistanceM, MinDistanceM, MaxDistanceM);
            return true;
        }

        public static bool TryGetShader(string prefabName, out string shaderName)
        {
            shaderName = null;
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (!_byPrefab.TryGetValue(prefabName, out var o)) return false;
            if (string.IsNullOrEmpty(o.shaderName)) return false;
            shaderName = o.shaderName;
            return true;
        }

        public static IEnumerable<KeyValuePair<string, float>> GetAllWithDistance()
        {
            foreach (var kvp in _byPrefab)
            {
                if (kvp.Value == null) continue;
                if (kvp.Value.renderDistanceM <= 0f) continue;
                yield return new KeyValuePair<string, float>(
                    kvp.Key,
                    Mathf.Clamp(kvp.Value.renderDistanceM, MinDistanceM, MaxDistanceM));
            }
        }

        public static IEnumerable<KeyValuePair<string, string>> GetAllWithShader()
        {
            foreach (var kvp in _byPrefab)
            {
                if (kvp.Value == null) continue;
                if (string.IsNullOrEmpty(kvp.Value.shaderName)) continue;
                yield return new KeyValuePair<string, string>(kvp.Key, kvp.Value.shaderName);
            }
        }

        public static void SetDistance(string prefabName, float distanceM)
        {
            if (string.IsNullOrEmpty(prefabName)) return;

            if (distanceM <= 0f)
            {
                if (_byPrefab.TryGetValue(prefabName, out var existing))
                {
                    existing.renderDistanceM = 0f;
                    PruneIfEmpty(prefabName, existing);
                    QueueSave();
                    OnOverridesChanged?.Invoke();
                }
                return;
            }

            distanceM = Mathf.Clamp(distanceM, MinDistanceM, MaxDistanceM);

            if (!_byPrefab.TryGetValue(prefabName, out var o))
            {
                o = new PieceOverride();
                _byPrefab[prefabName] = o;
            }
            o.renderDistanceM = distanceM;
            QueueSave();
            OnOverridesChanged?.Invoke();
        }

        public static void SetShader(string prefabName, string shaderName)
        {
            if (string.IsNullOrEmpty(prefabName)) return;

            if (string.IsNullOrEmpty(shaderName))
            {
                if (_byPrefab.TryGetValue(prefabName, out var existing))
                {
                    existing.shaderName = null;
                    PruneIfEmpty(prefabName, existing);
                    QueueSave();
                    OnOverridesChanged?.Invoke();
                }
                return;
            }

            if (!_byPrefab.TryGetValue(prefabName, out var o))
            {
                o = new PieceOverride();
                _byPrefab[prefabName] = o;
            }
            o.shaderName = shaderName;
            QueueSave();
            OnOverridesChanged?.Invoke();
        }

        public static void Load()
        {
            _byPrefab.Clear();
            try
            {
                if (!File.Exists(OverridesPath)) return;
                var json = File.ReadAllText(OverridesPath);
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, PieceOverride>>(json);
                if (parsed == null) return;
                foreach (var kvp in parsed)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    if (kvp.Value == null) continue;
                    _byPrefab[kvp.Key] = kvp.Value;
                }
                Debug.Log($"{VaPieces.PluginName}: VAFapPieceOverrides loaded {_byPrefab.Count} entry(ies) from {OverridesPath}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapPieceOverrides — failed to parse {OverridesPath}: {ex.Message}");
            }
        }

        public static void SaveNow()
        {
            try
            {
                var dir = Path.GetDirectoryName(OverridesPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(_byPrefab, Formatting.Indented);
                File.WriteAllText(OverridesPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapPieceOverrides — failed to write {OverridesPath}: {ex.Message}");
            }
        }

        private static void QueueSave()
        {
            if (_saveQueued) return;
            _saveQueued = true;
            try { SaveNow(); }
            finally { _saveQueued = false; }
        }

        private static void PruneIfEmpty(string prefabName, PieceOverride o)
        {
            if (o == null) return;
            if (o.renderDistanceM > 0f) return;
            if (!string.IsNullOrEmpty(o.shaderName)) return;
            _byPrefab.Remove(prefabName);
        }

#pragma warning disable CS0649
        [Serializable]
        public class PieceOverride
        {
            public float renderDistanceM;
            public string shaderName;
        }
#pragma warning restore CS0649
    }
}
