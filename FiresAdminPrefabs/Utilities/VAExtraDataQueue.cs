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

    // The armed field edits. Unlike the chest queue, one placement does NOT consume this: it stays until the
    // hammer's selection moves to a different prefab, the admin clears it, or the world is left.
    // Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    internal static class VAExtraDataQueue
    {
        private static string _prefabName;
        private static readonly List<VAExtraValue> _values = new List<VAExtraValue>();

        public static string ArmedPrefab => _prefabName;
        public static int Count => _values.Count;
        public static bool IsArmed => !string.IsNullOrEmpty(_prefabName) && _values.Count > 0;

        public static void Arm(string prefabName, List<VAExtraValue> values)
        {
            _prefabName = prefabName;
            _values.Clear();
            if (values != null) _values.AddRange(values);
        }

        public static void Clear()
        {
            _prefabName = null;
            _values.Clear();
        }

        public static bool TryGetFor(string prefabName, out List<VAExtraValue> values)
        {
            values = null;
            if (!IsArmed || prefabName != _prefabName) return false;
            values = _values;
            return true;
        }

        /// <summary>Re-selecting the same prefab keeps the queue; anything else drops it, so extra data is never
        /// applied to a piece the admin did not configure.</summary>
        public static void OnSelectedPieceChanged(Piece selected)
        {
            if (!IsArmed) return;
            string selectedName = selected != null && selected.gameObject != null ? selected.gameObject.name : null;
            if (selectedName == _prefabName) return;
            Debug.Log($"{VaPieces.PluginName}: extra data for '{_prefabName}' cleared - hammer moved to "
                + (string.IsNullOrEmpty(selectedName) ? "nothing" : selectedName));
            Clear();
        }

        public static string Describe() =>
            IsArmed ? $"Extra data: {_values.Count} field(s) on {_prefabName}" : string.Empty;
    }
}
