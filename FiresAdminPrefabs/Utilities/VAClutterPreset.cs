using System.ComponentModel;

namespace VerdantsAscent.Utilities
{
    // Strongly-typed DTO for a named runtime override of one
    // ClutterSystem.Clutter entry. Defaults match vanilla so an empty
    // preset is a no-op. The biome CSV is parsed into Heightmap.Biome
    // flags at apply time; empty leaves the vanilla mask untouched.
    //
    // Shader/material values are applied via MaterialPropertyBlock so the
    // shared Material asset stays pristine — if the shader doesn't expose
    // a property, the MPB write is a silent no-op.
    //
    // Field name `forestTresholdMin/Max` preserves vanilla's typo so the
    // value carries through to ClutterSystem unchanged.
    public class VAClutterPreset
    {
        public string name = string.Empty;
        [DefaultValue("")]
        public string clutterPrefab = string.Empty;
        [DefaultValue("")]
        public string biome = string.Empty;
        [DefaultValue(true)]
        public bool enabled = true;

        [DefaultValue(80)]
        public int amount = 80;
        [DefaultValue(false)]
        public bool snapToWater = false;
        [DefaultValue(true)]
        public bool onUncleared = true;
        [DefaultValue(false)]
        public bool onCleared = false;
        [DefaultValue(false)]
        public bool terrainTilt = false;
        [DefaultValue(false)]
        public bool instanced = false;
        [DefaultValue(0f)]
        public float randomOffset = 0f;
        [DefaultValue(27f)]
        public float minAlt = 27f;
        [DefaultValue(1000f)]
        public float maxAlt = 1000f;
        [DefaultValue(0f)]
        public float minOceanDepth = 0f;
        [DefaultValue(0f)]
        public float maxOceanDepth = 0f;
        [DefaultValue(0f)]
        public float minTilt = 0f;
        [DefaultValue(18f)]
        public float maxTilt = 18f;
        [DefaultValue(0f)]
        public float minVegetation = 0f;
        [DefaultValue(0f)]
        public float maxVegetation = 0f;

        [DefaultValue(1f)]
        public float scaleMin = 1f;
        [DefaultValue(1f)]
        public float scaleMax = 1f;

        [DefaultValue(0f)]
        public float fractalScale = 0f;
        [DefaultValue(0f)]
        public float fractalOffset = 0f;
        [DefaultValue(0.5f)]
        public float fractalTresholdMin = 0.5f;
        [DefaultValue(1f)]
        public float fractalTresholdMax = 1f;

        [DefaultValue(false)]
        public bool inForest = false;
        [DefaultValue(0f)]
        public float forestTresholdMin = 0f;
        [DefaultValue(1f)]
        public float forestTresholdMax = 1f;

        [DefaultValue(60f)]
        public float swaySpeed = 60f;
        [DefaultValue(2.3f)]
        public float swayDistance = 2.3f;
        [DefaultValue(2f)]
        public float pushDistance = 2f;
    }

    // One entry per clutter prefab tracking the admin's currently-active
    // preset name; "" means vanilla defaults. Persisted to a sidecar so
    // selections survive restart.
    public class VAClutterActiveEntry
    {
        public string clutterPrefab = string.Empty;
        public string active = string.Empty;
    }
}
