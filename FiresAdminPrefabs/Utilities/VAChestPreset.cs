using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace VerdantsAscent.Utilities
{
    // DTO matching EWP's named data-entry format for containers.
    // YamlDotNet deserializes data_FAP_chests.yaml into a
    // List<VAChestPreset>; EWP consumes the same file at apply + respawn.
    //
    // Bools list maps to EWP-format "key, true|false" entries written via
    // zdo.Set(key, bool) — used for vanilla LoadFields gates (HasFields,
    // HasFieldsContainer) and Container.m_autoDestroyEmpty.
    //
    // FAP-only fields live inside the strings list so EWP's strict
    // deserializer doesn't reject them:
    //   strings: va_chest_prefab            — target container prefab
    //   floats:  va_respawn_seconds         — base respawn delay
    //   floats:  va_respawn_seconds_max     — optional upper bound for "min;max"
    //   floats:  va_prevent_stacking_radius — radius scan to skip duplicates
    public class VAChestPreset
    {
        public string name = string.Empty;

        public List<string> ints = new List<string>();
        public List<string> floats = new List<string>();
        public List<string> strings = new List<string>();
        public List<string> bools = new List<string>();

        public List<VAItemEntry> items = new List<VAItemEntry>();

        [YamlMember(Alias = "itemAmount", ApplyNamingConventions = false)]
        public string itemAmount;

        [YamlMember(Alias = "containerSize", ApplyNamingConventions = false)]
        public string containerSize;

        // Adapter that reads/writes the va_chest_prefab entry inside
        // strings[]. YamlIgnore is required so the property doesn't
        // serialize back as a top-level key (EWP rejects unknown keys).
        [YamlIgnore]
        public string ContainerPrefab
        {
            get { return TryGetString("va_chest_prefab") ?? string.Empty; }
            set
            {
                for (int i = strings.Count - 1; i >= 0; i--)
                {
                    var line = strings[i];
                    int comma = line.IndexOf(',');
                    if (comma > 0 && line.Substring(0, comma).Trim() == "va_chest_prefab")
                        strings.RemoveAt(i);
                }
                if (!string.IsNullOrEmpty(value))
                    strings.Add($"va_chest_prefab, {value}");
            }
        }

        public string TryGetString(string key) => FindValue(strings, key);
        public string TryGetIntStr(string key) => FindValue(ints, key);
        public string TryGetFloatStr(string key) => FindValue(floats, key);

        private static string FindValue(List<string> list, string key)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var line = list[i];
                if (string.IsNullOrEmpty(line)) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                string lineKey = line.Substring(0, comma).Trim();
                if (lineKey == key)
                    return line.Substring(comma + 1).Trim();
            }
            return null;
        }
    }
}
