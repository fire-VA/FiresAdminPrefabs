using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Api
{
    /// <summary>
    /// Public hook for other mods to add entries to FAP's build-menu right-click (piece) menu. 1.1.200, agreed with
    /// [seasons] for FDM's dungeon/env-box presets. Client-only. Providers run in registration order, after FAP's own
    /// entries and below a separator; a provider returns null or nothing for pieces it does not handle.
    /// </summary>
    public static class FapPieceMenu
    {
        private static readonly List<KeyValuePair<string, Func<Piece, IEnumerable<KeyValuePair<string, Action>>>>> s_providers =
            new List<KeyValuePair<string, Func<Piece, IEnumerable<KeyValuePair<string, Action>>>>>();
        private static readonly HashSet<string> s_warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Registers (or replaces, for the same id) a provider of menu entries: label = Key, click = Value.</summary>
        public static void RegisterProvider(string id, Func<Piece, IEnumerable<KeyValuePair<string, Action>>> provider)
        {
            if (string.IsNullOrEmpty(id) || provider == null) return;
            UnregisterProvider(id);
            s_providers.Add(new KeyValuePair<string, Func<Piece, IEnumerable<KeyValuePair<string, Action>>>>(id, provider));
        }

        public static void UnregisterProvider(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            s_providers.RemoveAll(p => string.Equals(p.Key, id, StringComparison.Ordinal));
        }

        // Collected for ShowPieceMenu. A throwing provider is skipped and logged once per id.
        internal static List<KeyValuePair<string, Action>> Collect(Piece piece)
        {
            var result = new List<KeyValuePair<string, Action>>();
            if (piece == null) return result;
            foreach (var p in s_providers.ToArray())
            {
                try
                {
                    var entries = p.Value(piece);
                    if (entries == null) continue;
                    foreach (var e in entries)
                        if (!string.IsNullOrEmpty(e.Key)) result.Add(e);
                }
                catch (Exception ex)
                {
                    if (s_warned.Add(p.Key))
                        Debug.LogWarning($"FiresAdminPrefabs: FapPieceMenu provider '{p.Key}' threw (logged once): {ex.Message}");
                }
            }
            return result;
        }
    }
}
