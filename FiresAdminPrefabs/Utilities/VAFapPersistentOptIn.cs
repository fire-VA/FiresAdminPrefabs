using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Disk-backed store of prefab names the user has explicitly opted into
    // for respawn-on-destroy. Backs the right-click "Place with Persistence"
    // toggle. State survives game restart via an atomic plain-text file
    // under BepInEx/config/FiresAdminPrefabs/persistent_optins.txt.
    public static class VAFapPersistentOptIn
    {
        private static readonly HashSet<string> _optedIn =
            new HashSet<string>(StringComparer.Ordinal);

        public static int Count => _optedIn.Count;

        public static event Action Changed;

        // The CustomPersistentPrefabs config (plus FAP's curated list) counts as a
        // standing opt-in, so a prefab named there behaves as if the player had
        // right-clicked "Place with Persistence" on it.
        public static bool IsOptedIn(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (_optedIn.Contains(prefabName)) return true;
            return VAFapEwpYamlWriter.IsCustomPersistent(prefabName);
        }

        public static bool Toggle(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (_optedIn.Contains(prefabName))
            {
                _optedIn.Remove(prefabName);
                Debug.Log($"[FAP Persistence] '{prefabName}' OPTED OUT — future placements won't respawn.");
                SaveToDisk();
                Changed?.Invoke();
                return false;
            }
            _optedIn.Add(prefabName);
            Debug.Log($"[FAP Persistence] '{prefabName}' OPTED IN — future placements will respawn when destroyed.");
            SaveToDisk();
            Changed?.Invoke();
            return true;
        }

        public static void Set(string prefabName, bool optedIn)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            bool changed = optedIn ? _optedIn.Add(prefabName) : _optedIn.Remove(prefabName);
            if (!changed) return;
            SaveToDisk();
            Changed?.Invoke();
        }

        public static void Clear()
        {
            int n = _optedIn.Count;
            _optedIn.Clear();
            Debug.Log($"[FAP Persistence] Cleared opt-ins ({n} prefab name(s)).");
            SaveToDisk();
            Changed?.Invoke();
        }

        private static string StorePath =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "persistent_optins.txt");

        private static readonly object _ioLock = new object();

        public static void LoadFromDisk()
        {
            lock (_ioLock)
            {
                _optedIn.Clear();
                try
                {
                    if (!File.Exists(StorePath)) return;
                    var lines = File.ReadAllLines(StorePath);
                    foreach (var raw in lines)
                    {
                        var line = raw?.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        _optedIn.Add(line);
                    }
                    Debug.Log($"[FAP Persistence] Loaded {_optedIn.Count} opt-in(s) from {StorePath}.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FAP Persistence] LoadFromDisk failed: {ex.Message}");
                }
            }
        }

        private static void SaveToDisk()
        {
            lock (_ioLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(StorePath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    var sb = new StringBuilder();
                    sb.AppendLine("# FAP persistent-opt-in store — one prefab name per line.");
                    sb.AppendLine("# Auto-managed by VAFapPersistentOptIn; edit by hand only when game is closed.");
                    sb.AppendLine();
                    var sorted = new List<string>(_optedIn);
                    sorted.Sort(StringComparer.Ordinal);
                    foreach (var name in sorted) sb.AppendLine(name);

                    string tmp = StorePath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString());
                    if (File.Exists(StorePath)) File.Delete(StorePath);
                    File.Move(tmp, StorePath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FAP Persistence] SaveToDisk failed: {ex.Message}");
                }
            }
        }
    }
}
