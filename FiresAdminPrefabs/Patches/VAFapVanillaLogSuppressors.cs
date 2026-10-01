using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Filters two specific vanilla log lines that flood by the hundred on
    // world load and replaces them with a single cooldown-batched VANILLA
    // NOISE banner. Both messages are informational and the underlying
    // conditions are vanilla-internal (nested ZNetViews on shipped prefabs,
    // orphan ZDOs whose prefab hash no longer resolves).
    [HarmonyPatch(typeof(ZLog))]
    public static class ZLog_FilterFiresAdminPrefabs
    {
        private const string DoubleZNetPrefix = "Double ZNetview when initializing object ";
        private const string OrphanZdoPrefix = "Destroyed invalid predab ZDO:";
        private const float BannerCooldownSeconds = 30f;
        private const int MaxTopPrefabNameLength = 16;
        private const int TopPrefabTruncatedLength = 13;

        private static int _doubleZNetCount;
        private static int _orphanZdoCount;
        private static readonly Dictionary<string, int> _doubleZNetByPrefab =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static float _lastBannerTime = -999f;
        private static bool _dirtySinceLastBanner;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZLog.Log), new Type[] { typeof(object) })]
        public static bool LogPrefix(object o) => !TryFilter(o);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZLog.LogWarning), new Type[] { typeof(object) })]
        public static bool LogWarningPrefix(object o) => !TryFilter(o);

        public static void ForceFlushIfDirty()
        {
            if (!_dirtySinceLastBanner) return;
            EmitSummaryBanner();
            _lastBannerTime = SafeRealtime();
            _dirtySinceLastBanner = false;
        }

        private static bool TryFilter(object o)
        {
            try
            {
                if (o == null) return false;
                if (!(o is string msg) || msg.Length == 0) return false;

                if (msg.StartsWith(DoubleZNetPrefix, StringComparison.Ordinal))
                {
                    _doubleZNetCount++;

                    string name = msg.Substring(DoubleZNetPrefix.Length);
                    int cloneIdx = name.IndexOf("(Clone)", StringComparison.Ordinal);
                    if (cloneIdx > 0) name = name.Substring(0, cloneIdx).TrimEnd();

                    if (_doubleZNetByPrefab.TryGetValue(name, out var prev))
                        _doubleZNetByPrefab[name] = prev + 1;
                    else
                        _doubleZNetByPrefab[name] = 1;

                    MarkDirtyAndMaybeBanner();
                    return true;
                }

                if (msg.StartsWith(OrphanZdoPrefix, StringComparison.Ordinal))
                {
                    _orphanZdoCount++;
                    MarkDirtyAndMaybeBanner();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void MarkDirtyAndMaybeBanner()
        {
            _dirtySinceLastBanner = true;
            float now = SafeRealtime();
            if (now - _lastBannerTime < BannerCooldownSeconds) return;
            EmitSummaryBanner();
            _lastBannerTime = now;
            _dirtySinceLastBanner = false;
        }

        private static float SafeRealtime()
        {
            try { return Time.realtimeSinceStartup; }
            catch { return 0f; }
        }

        private static void EmitSummaryBanner()
        {
            if (_doubleZNetCount == 0 && _orphanZdoCount == 0) return;
            string topNote = null;
            try
            {
                if (_doubleZNetByPrefab.Count > 0)
                {
                    var top = _doubleZNetByPrefab
                        .OrderByDescending(kv => kv.Value)
                        .First();
                    string name = top.Key;
                    if (name.Length > MaxTopPrefabNameLength)
                        name = name.Substring(0, TopPrefabTruncatedLength) + "...";
                    topNote = $"{name}×{top.Value}";
                }
            }
            catch { }

            try { VAFapLoadSummary.EmitVanillaNoise(_doubleZNetCount, _orphanZdoCount, topNote); }
            catch { }
        }
    }
}
