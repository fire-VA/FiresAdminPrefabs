using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Bundle prefabs name themselves with their source mod's localization keys ("$piece_mpwoodendrawer"), and without that mod
    // nothing translates them: vanilla prints the bare key in brackets ("[piece_mpwoodendrawer]", the Missing Pieces drawer's
    // hover in H9). A key the game can't translate is replaced on the prefab by a readable name made from the prefab's own
    // name ("piece_chest_wooden_drawer" -> "Chest wooden drawer"), and an untranslatable description by none. A key that
    // translates is left alone, so the source mod or a translation pack still wins whenever it is present.
    internal static class VAReadableNames
    {
        // Localization.Translate is private; its table is read directly, a dictionary lookup per key rather than a
        // Localize pass, because this runs for every bundle prefab at injection.
        private static readonly AccessTools.FieldRef<Localization, Dictionary<string, string>> s_translations =
            AccessTools.FieldRefAccess<Localization, Dictionary<string, string>>("m_translations");

        // Returns how many names on the prefab were replaced.
        internal static int Apply(GameObject prefab)
        {
            if (prefab == null || Localization.instance == null) return 0;
            int replaced = 0;
            var piece = prefab.GetComponent<Piece>();
            if (piece != null)
            {
                if (Untranslated(piece.m_name)) { piece.m_name = FromPrefabName(prefab.name); replaced++; }
                if (Untranslated(piece.m_description)) { piece.m_description = string.Empty; replaced++; }
            }
            var container = prefab.GetComponent<Container>();
            if (container != null && Untranslated(container.m_name))
            {
                container.m_name = piece != null && !Untranslated(piece.m_name) ? piece.m_name : FromPrefabName(prefab.name);
                replaced++;
            }
            return replaced;
        }

        // A lone key ("$word") the current language has no entry for; Localization prints those as "[word]".
        internal static bool Untranslated(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '$' || text.Length < 2 || text.IndexOf(' ') >= 0) return false;
            // An empty table (no language loaded, e.g. a headless server) can't tell, so nothing is renamed.
            var table = Localization.instance != null ? s_translations(Localization.instance) : null;
            return table != null && table.Count > 0 && !table.ContainsKey(text.Substring(1));
        }

        internal static string FromPrefabName(string prefabName)
        {
            string name = prefabName ?? string.Empty;
            if (name.StartsWith("piece_", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
            name = name.Replace('_', ' ').Trim();
            while (name.Contains("  ")) name = name.Replace("  ", " ");
            return name.Length == 0 ? prefabName : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }
}
