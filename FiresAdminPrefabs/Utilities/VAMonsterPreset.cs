using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace VerdantsAscent.Utilities
{
    // DTO matching EWP's named data-entry format. YamlDotNet deserializes
    // the monster-preset yaml directly into a List<VAMonsterPreset>. Each
    // ints/floats/strings/vecs entry is a single line in "key, value" form
    // (or "key, x,y,z" for vecs); the parser at apply-time splits on the
    // FIRST comma so values containing additional commas survive intact.
    //
    // FAP-only fields that live INSIDE these lists rather than as top-level
    // properties (so EWP's strict deserializer doesn't reject them):
    //   strings: va_creature   — prefab this preset targets
    //   strings: va_drops      — legacy pipe-string (migrated to items[])
    //   floats:  va_respawn_seconds, va_group_min, va_group_max
    public class VAMonsterPreset
    {
        public string name = string.Empty;

        public List<string> ints = new List<string>();
        public List<string> floats = new List<string>();
        public List<string> strings = new List<string>();
        public List<string> vecs = new List<string>();

        // Death-drops table, shape-compatible with the chest preset's inventory
        // list so admins learn one format. VAMonsterConfigApplier copies it onto
        // the creature's own CharacterDrop when it dies, so vanilla rolls it and
        // the table works with EWP absent. quality and variant are carried for
        // the chest path only — vanilla's Drop has no field for either.
        public List<VAItemEntry> items = new List<VAItemEntry>();

        // EWP-native pick-N-from-M selector for items. "3" picks exactly
        // three; "2;4" picks a random count in [2, 4]. Selection is
        // weighted by each entry's chance.
        [YamlMember(Alias = "itemAmount", ApplyNamingConventions = false)]
        public string itemAmount;

        // Adapter that reads/writes the va_creature entry inside strings[].
        // YamlIgnore is required so the property doesn't serialize back as
        // a top-level `creature:` key (EWP would reject the file).
        [YamlIgnore]
        public string Creature
        {
            get { return TryGetString("va_creature") ?? string.Empty; }
            set
            {
                for (int i = strings.Count - 1; i >= 0; i--)
                {
                    var line = strings[i];
                    int comma = line.IndexOf(',');
                    if (comma > 0 && line.Substring(0, comma).Trim() == "va_creature")
                        strings.RemoveAt(i);
                }
                if (!string.IsNullOrEmpty(value))
                    strings.Add($"va_creature, {value}");
            }
        }

        // Adapter for the VCS bone-edit blob (FiresCustomizationStudio's compact legacy-vector string — opaque
        // to FAP). Lives inside strings[] as "va_bones, <blob>"; the blob is space-separated with no commas, so
        // the first-comma split keeps it intact. YamlIgnore for the same reason as Creature. Optional/back-compat:
        // old presets lack the line, so this returns empty and they serialize byte-identically.
        [YamlIgnore]
        public string Bones
        {
            get { return TryGetString("va_bones") ?? string.Empty; }
            set
            {
                for (int i = strings.Count - 1; i >= 0; i--)
                {
                    var line = strings[i];
                    int comma = line.IndexOf(',');
                    if (comma > 0 && line.Substring(0, comma).Trim() == "va_bones")
                        strings.RemoveAt(i);
                }
                if (!string.IsNullOrEmpty(value))
                    strings.Add($"va_bones, {value}");
            }
        }

        public string TryGetString(string key) => FindValue(strings, key);
        public string TryGetIntStr(string key) => FindValue(ints, key);
        public string TryGetFloatStr(string key) => FindValue(floats, key);
        public string TryGetVecStr(string key) => FindValue(vecs, key);

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
