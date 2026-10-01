using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Finds the placeable creature prefabs in ZNetScene for the Monsters tab and the
    // monster placement intercept: anything with a Character and a ZNetView, excluding
    // the Player prefab and editor/test leftovers (a leading underscore, a "_test"
    // suffix, known strays). Bosses are allowed — admins place them on purpose, and the
    // config UI can toggle boss status. Built once per session at InitializeOnce, by
    // which point bundle creatures are already in ZNetScene alongside vanilla's.
    public static class VAMonsterRegistry
    {
        public sealed class Entry
        {
            public string PrefabName;       // ZNetScene key
            public string DisplayName;      // m_name from Humanoid, falls back to prefab name
            public GameObject Prefab;       // resolved ZNetScene prefab
            public bool HasHumanoid;        // distinguishes mobs from fish/etc.
            public bool IsBoss;             // baked m_boss flag from Character
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly Dictionary<string, Entry> _byName =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static IReadOnlyList<Entry> All => _entries;

        public static bool TryGet(string prefabName, out Entry entry)
            => _byName.TryGetValue(prefabName ?? string.Empty, out entry);

        // Idempotent — re-scanning replaces the prior list. Called from
        // VAVanillaPieceManager.InitializeOnce after ZNetScene awakens
        // and after our bundle loader injects external creatures.
        public static int RebuildFromScene()
        {
            _entries.Clear();
            _byName.Clear();

            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return 0;

            foreach (var prefab in scene.m_prefabs)
            {
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null) continue;
                var name = prefab.name;
                if (string.IsNullOrEmpty(name)) continue;

                // Skip editor leftovers / templates.
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (name.EndsWith("_test", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Player", StringComparison.Ordinal)) continue;

                var character = prefab.GetComponent<Character>();
                if (character == null) continue;

                var znv = prefab.GetComponent<ZNetView>();
                if (znv == null) continue;

                var humanoid = prefab.GetComponent<Humanoid>();
                string displayName = !string.IsNullOrEmpty(character.m_name)
                    ? character.m_name
                    : name;

                var entry = new Entry
                {
                    PrefabName = name,
                    DisplayName = displayName,
                    Prefab = prefab,
                    HasHumanoid = humanoid != null,
                    IsBoss = character.m_boss,
                };

                _entries.Add(entry);
                _byName[name] = entry;
            }

            // Sort by display name for stable tab presentation.
            _entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return _entries.Count;
        }
    }
}
