using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    internal struct VAExtraValue
    {
        public string Key;
        public VAExtraFieldKind Kind;
        public object Value;
    }

    // The armed field edits, per prefab. 1.1.169 (Fire 10-06: data presets stay queued like chest / monster presets): one
    // placement does not consume them and moving the hammer to another piece no longer drops them; they stay on THAT prefab
    // until the admin arms other values (or a data preset) for it, picks "No data preset", or leaves the world. Only that
    // prefab's placements get them.
    // Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    internal static class VAExtraDataQueue
    {
        private static readonly Dictionary<string, List<VAExtraValue>> _byPrefab = new Dictionary<string, List<VAExtraValue>>();
        private static readonly Dictionary<string, string> _presetByPrefab = new Dictionary<string, string>();

        public static bool IsArmedFor(string prefabName) => prefabName != null && _byPrefab.TryGetValue(prefabName, out var v) && v.Count > 0;
        public static int CountFor(string prefabName) => prefabName != null && _byPrefab.TryGetValue(prefabName, out var v) ? v.Count : 0;

        /// <summary>The data preset these values came from (null for values typed without saving a preset).</summary>
        public static string PresetFor(string prefabName) => prefabName != null && _presetByPrefab.TryGetValue(prefabName, out var p) ? p : null;

        public static void Arm(string prefabName, List<VAExtraValue> values, string presetName = null)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            if (values == null || values.Count == 0) { Clear(prefabName); return; }
            _byPrefab[prefabName] = new List<VAExtraValue>(values);
            if (string.IsNullOrEmpty(presetName)) _presetByPrefab.Remove(prefabName); else _presetByPrefab[prefabName] = presetName;
        }

        public static void Clear(string prefabName)
        {
            if (prefabName == null) return;
            _byPrefab.Remove(prefabName);
            _presetByPrefab.Remove(prefabName);
        }

        public static bool TryGetFor(string prefabName, out List<VAExtraValue> values)
        {
            values = null;
            return prefabName != null && _byPrefab.TryGetValue(prefabName, out values) && values.Count > 0;
        }

        public static string Describe(string prefabName)
        {
            if (!IsArmedFor(prefabName)) return "nothing armed";
            string preset = PresetFor(prefabName);
            return $"{CountFor(prefabName)} field(s) armed on {prefabName}" + (preset != null ? $" (data preset '{preset}')" : "");
        }
    }
}
