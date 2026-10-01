using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // One-shot pending-preset buffer between the "Configure monster" UI's
    // Apply button and the next placement of that creature. Stores only
    // the preset NAME so live edits to the store affect both the next
    // placement and future respawn cycles.
    public static class VAMonsterConfigQueue
    {
        private static readonly Dictionary<string, string> _queuedPresetByCreature =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static void EnqueueForNextPlacement(string creaturePrefabName, string presetName)
        {
            if (string.IsNullOrEmpty(creaturePrefabName)) return;
            if (string.IsNullOrEmpty(presetName))
            {
                _queuedPresetByCreature.Remove(creaturePrefabName);
                return;
            }
            _queuedPresetByCreature[creaturePrefabName] = presetName;
        }

        public static void Clear(string creaturePrefabName)
        {
            if (string.IsNullOrEmpty(creaturePrefabName)) return;
            _queuedPresetByCreature.Remove(creaturePrefabName);
        }

        public static bool TryGetQueuedPresetName(string creaturePrefabName, out string presetName)
        {
            presetName = null;
            return !string.IsNullOrEmpty(creaturePrefabName)
                && _queuedPresetByCreature.TryGetValue(creaturePrefabName, out presetName)
                && !string.IsNullOrEmpty(presetName);
        }

        public static int ResolveSpawnCount(string creaturePrefabName)
        {
            if (!TryGetQueuedPresetName(creaturePrefabName, out var presetName))
                return 1;
            if (!VAMonsterPresetStore.TryGet(presetName, out var preset))
                return 1;

            int min = ParseInt(preset.TryGetIntStr("va_group_min"), 1);
            int max = ParseInt(preset.TryGetIntStr("va_group_max"), min);
            if (min < 1) min = 1;
            if (max < min) max = min;
            return min == max ? min : UnityEngine.Random.Range(min, max + 1);
        }

        public static void ApplyToSpawnedInstance(string creaturePrefabName, GameObject instance)
        {
            if (instance == null) return;
            if (!TryGetQueuedPresetName(creaturePrefabName, out var presetName)) return;
            if (!VAMonsterPresetStore.TryGet(presetName, out var preset)) return;

            VAMonsterConfigApplier.StampPresetAndRefresh(preset, instance);
            _queuedPresetByCreature.Remove(creaturePrefabName);
        }

        private static int ParseInt(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
