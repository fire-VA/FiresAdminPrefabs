using System.ComponentModel;

namespace FiresCore.Loot
{
    // Shared item entry shape used by both monster-drop and chest-inventory presets.
    // Field names + types mirror EWP's Data.ItemData so the yaml round-trips through
    // EWP's deserializer.
    public class VAItemEntry
    {
        public string prefab = string.Empty;
        public float chance = 1f;
        public string stack;
        public string quality;
        public string variant;

        [DefaultValue("")]
        public string pos = string.Empty;
    }
}
