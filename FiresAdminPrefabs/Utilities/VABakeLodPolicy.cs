using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Single source of truth for how a baked blueprint switches detail and casts shadows.
    //
    // Unity picks a LOD level from the group's screen-relative height, which is its size divided
    // by its distance, so a switch distance in metres only becomes a threshold once a size is
    // fixed. Source pieces ship heights authored for a 2m wall; a bake bucket is a 64m cube of
    // thousands of them, and inheriting those numbers left the Phalanx at full detail out to
    // roughly 650m. Here the group's size is derived from the configured distance instead of
    // measured, so every bucket switches at that distance whatever it contains.
    public static class VABakeLodPolicy
    {
        // Levels every bucket carries: full detail, and each renderer's lowest authored LOD.
        // Every level is complete, so a bucket never switches to a partial copy of itself.
        public const int BakeLodLevels = 2;

        public const float LodFadeTransitionWidth = 0.3f;

        // Valheim's default field of view. A player on a wider FOV switches slightly later, the
        // same way every other LODGroup in the game does, and so does a lower LOD Bias.
        private const float ReferenceFieldOfViewDegrees = 65f;

        // Threshold the full-detail level hands over at. Any value inside Unity's 0-1 range
        // works; the group size is solved for it, and every later level lands below it.
        private const float FullDetailTransitionHeight = 0.5f;

        private const float NeverCullHeight = 0f;

        private static float TanHalfReferenceFieldOfView =>
            Mathf.Tan(ReferenceFieldOfViewDegrees * 0.5f * Mathf.Deg2Rad);

        public static float LowDetailDistanceMeters =>
            ConfigManager.Instance?.configBakedLodLowDetailDistance?.Value ?? 90f;

        public static float CullDistanceMeters =>
            ConfigManager.Instance?.configBakedLodCullDistance?.Value ?? 0f;

        public static bool ShadowProxyEnabled =>
            ConfigManager.Instance?.configBakedLodShadowProxy?.Value ?? true;

        // Size to stamp on a bucket's LODGroup so FullDetailTransitionHeight falls exactly at
        // LowDetailDistanceMeters. It only feeds LOD selection — frustum culling uses the
        // renderers' own bounds — so overriding the measured size costs nothing else.
        public static float LodGroupSize =>
            2f * FullDetailTransitionHeight * Mathf.Max(1f, LowDetailDistanceMeters) * TanHalfReferenceFieldOfView;

        public static float ScreenRelativeHeightAt(float distanceMeters, float worldSize)
        {
            if (distanceMeters <= 0f || worldSize <= 0f) return NeverCullHeight;
            return 0.5f * worldSize / (distanceMeters * TanHalfReferenceFieldOfView);
        }

        // Heights for one bucket, highest first, as SetLODs requires. Index i is the height at
        // which level i hands over to level i+1; the last is the height below which the bucket
        // stops rendering.
        public static float[] TransitionHeights(int levelCount)
        {
            float lodGroupSize = LodGroupSize;
            var heights = new float[levelCount];
            for (int level = 0; level < levelCount; level++)
            {
                heights[level] = level == levelCount - 1
                    ? ScreenRelativeHeightAt(CullDistanceMeters, lodGroupSize)
                    : FullDetailTransitionHeight;
                if (level > 0 && heights[level] >= heights[level - 1]) heights[level] = NeverCullHeight;
            }
            return heights;
        }

        // A level's own geometry is too dense to re-render once per shadow cascade, so each level
        // is shadowed by the next level down and only the cheapest level casts its own.
        public static ShadowCastingMode ShadowCastingModeForLevel(int level, int levelCount) =>
            LevelNeedsShadowProxy(level, levelCount) ? ShadowCastingMode.Off : ShadowCastingMode.On;

        public static bool LevelNeedsShadowProxy(int level, int levelCount) =>
            ShadowProxyEnabled && level < levelCount - 1;
    }
}
