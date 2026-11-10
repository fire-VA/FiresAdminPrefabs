using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // 1.1.267 (LOG_AUDIT_20261015 B13/B14, [lead] 10-15): the client logged 'Missing prefab hash' 21,084 times in 3 min,
    // 11,709 of them for -1981681913 = Pickable_Straw_bal (121 placed, no longer in any bundle). Vanilla retries every
    // missing ZDO every frame it is in range.
    //  - A hash whose NAME FAP knows (hammer-tab prefabs.txt, PieceConfigs "Prefab") gets an invisible placeholder, like
    //    RemovedPrefabPlaceholders: the save data is kept and the piece comes back if a bundle carries it again.
    //  - An unknown hash is logged ONCE as a Warning; later hits are counted (no per-frame line) and summed up every 5 min.
    // A fix, so always on (no key).
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateObject))]
    public static class MissingPrefabHashGuard
    {
        private static Dictionary<int, string> _known;
        private static readonly Dictionary<int, int> _misses = new Dictionary<int, int>();
        private static float _nextSummary;
        private static int _missesSinceSummary;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(ZNetScene __instance, ZDO zdo, ref GameObject __result)
        {
            if (__instance == null || zdo == null) return true;
            int hash = zdo.GetPrefab();
            if (hash == 0 || __instance.m_namedPrefabs.ContainsKey(hash)) return true;
            try
            {
                if (VerdantsAscent.Utilities.VALazyBundles.TryRealizeLead(__instance, hash)) return true;
                if (Known().TryGetValue(hash, out var name) && VAExternalBundleLoader.AddLatePlaceholder(__instance, name, hash))
                {
                    Debug.Log($"{VaPieces.PluginName}: placeholder for missing prefab {name} (hash {hash}): it is placed in this world but no bundle provides it; it loads invisible and keeps its save data");
                    return true;
                }
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: missing-prefab placeholder for {hash} failed: {e.Message}"); }

            _misses.TryGetValue(hash, out int n);
            _misses[hash] = n + 1;
            _missesSinceSummary++;
            if (n == 0)
                Debug.LogWarning($"{VaPieces.PluginName}: Missing prefab hash {hash} (name unknown to FAP). Further hits are counted, not logged; summary every 5 min.");
            if (Time.realtimeSinceStartup >= _nextSummary)
            {
                if (_nextSummary > 0f && _missesSinceSummary > 1)
                    VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: missing prefab hashes, last 5 min: {_missesSinceSummary} create attempts over {_misses.Count} hash(es)");
                _nextSummary = Time.realtimeSinceStartup + 300f;
                _missesSinceSummary = 0;
            }
            __result = null;
            return false;
        }

        // hash -> name for every prefab name FAP's own config lists. Built once, on the first miss.
        private static Dictionary<int, string> Known()
        {
            if (_known != null) return _known;
            _known = new Dictionary<int, string>();
            string root = Path.GetFullPath(Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs"));
            int files = 0;
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var f in Directory.GetFiles(Path.Combine(root, "HammerTabs"), "prefabs.txt", SearchOption.AllDirectories))
                    {
                        files++;
                        foreach (var line in File.ReadAllLines(f)) AddName(line.Trim());
                    }
                    string pc = Path.Combine(root, "PieceConfigs");
                    if (Directory.Exists(pc))
                        foreach (var f in Directory.GetFiles(pc, "*.json", SearchOption.AllDirectories))
                        {
                            files++;
                            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"Prefab\"\\s*:\\s*\"([^\"]+)\"")) AddName(m.Groups[1].Value);
                        }
                }
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: missing-prefab name table: {e.Message}"); }
            Debug.Log($"{VaPieces.PluginName}: missing-prefab name table: {_known.Count} names from {files} file(s)");
            return _known;
        }

        private static void AddName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("#") || name.StartsWith("//")) return;
            int h = name.GetStableHashCode();
            if (!_known.ContainsKey(h)) _known[h] = name;
        }
    }
}
