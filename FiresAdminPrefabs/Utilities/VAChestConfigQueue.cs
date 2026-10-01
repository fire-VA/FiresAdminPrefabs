using System;
using System.Collections.Generic;
using System.Globalization;

namespace VerdantsAscent.Utilities
{
    // VAChestConfigQueue — pending-preset buffer between the "Configure
    // container" UI's Apply button and the next placement of that
    // container prefab. One-shot: the queued preset applies to the next
    // placement then clears.
    //
    // Mirrors VAMonsterConfigQueue's one-shot semantics. Unlike the
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

        // Consume the queued preset for this container prefab (one-shot).
        // Returns the preset if one was queued; null otherwise. Removes
        // the entry on success.
        public static VAChestPreset ConsumeQueuedPreset(string containerPrefabName)
        {
            if (!TryGetQueuedPresetName(containerPrefabName, out var presetName))
                return null;
            if (!VAChestPresetStore.TryGet(presetName, out var preset))
            {
                _queuedPresetByContainer.Remove(containerPrefabName);
                return null;
            }
            _queuedPresetByContainer.Remove(containerPrefabName);
            return preset;
        }
    }
}
