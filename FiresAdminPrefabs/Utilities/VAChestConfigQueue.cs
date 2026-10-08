using System;
using System.Collections.Generic;
using System.Globalization;

namespace VerdantsAscent.Utilities
{
    // VAChestConfigQueue — pending-preset buffer between the "Configure
    // container" UI's Apply button and the next placement of that
    // container prefab. 1.1.167: kept until the player changes it (was one-shot: the queued preset applied to the next
    // placement then cleared).
    //
    // Mirrors VAMonsterConfigQueue (also kept until changed). Unlike the
    // monster queue we don't track per-creature spawn counts — chests
    // are always placed one-at-a-time via the hammer.
    public static class VAChestConfigQueue
    {
        private static readonly Dictionary<string, string> _queuedPresetByContainer =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static void EnqueueForNextPlacement(string containerPrefabName, string presetName)
        {
            if (string.IsNullOrEmpty(containerPrefabName)) return;
            if (string.IsNullOrEmpty(presetName))
            {
                _queuedPresetByContainer.Remove(containerPrefabName);
                return;
            }
            _queuedPresetByContainer[containerPrefabName] = presetName;
        }

        public static void Clear(string containerPrefabName)
        {
            if (string.IsNullOrEmpty(containerPrefabName)) return;
            _queuedPresetByContainer.Remove(containerPrefabName);
        }

        public static bool TryGetQueuedPresetName(string containerPrefabName, out string presetName)
        {
            presetName = null;
            return !string.IsNullOrEmpty(containerPrefabName)
                && _queuedPresetByContainer.TryGetValue(containerPrefabName, out presetName)
                && !string.IsNullOrEmpty(presetName);
        }

        // The queued preset for this container prefab, for a placement. 1.1.167 (Fire 10-06: "the next chest placed was a default
        // container"): the preset STAYS queued, so several in a row carry it, until the player picks another one or "No preset"
        // in the hammer menu. Only a preset that no longer exists in the store drops out of the queue.
        public static VAChestPreset ConsumeQueuedPreset(string containerPrefabName)
        {
            if (!TryGetQueuedPresetName(containerPrefabName, out var presetName))
                return null;
            if (!VAChestPresetStore.TryGet(presetName, out var preset))
            {
                _queuedPresetByContainer.Remove(containerPrefabName);
                return null;
            }
            return preset;
        }
    }
}
