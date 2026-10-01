using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Area brush in the Clutter tab that removes FAP-painted clutter inside a radius. Painted
    // clutter lives in each zone's TerrainComp ZDO byte array rather than as networked objects,
    // so this edits those arrays through VAClutterPaintTool. Shift+Scroll resizes the brush.
    public static class VAClutterClearTool
    {
        public const string StandinName = "FAP_ClutterClearTool";

        public const float MinRadius = 0.5f;
        public const float MaxRadius = 30f;

        public static float Radius = 4f;
        public static float HeightBounds = 100f;

        private const float TerrainCompGridSize = 64f;
        private const float DefaultHeightBounds = 100f;
        private const float IconEraserHalfThickness = 5f;
        private static readonly Color32 IconRingColor = new Color32(60, 180, 90, 255);
        private static readonly Color32 IconFillColor = new Color32(40, 130, 60, 200);

        private const string Description =
            "<color=#66cc88>Clutter Clear Brush.</color> Click on terrain to remove "
            + "FAP-painted clutter inside the brush radius. Only affects clutter painted "
            + "with this mod's paint tool; vanilla biome clutter regrows naturally. "
            + "Shift+Scroll resizes; cleared positions persist (won't respawn on zone reload).";

        private static GameObject _standin;

        public static bool IsStandinName(string name) => name == StandinName;

        public static GameObject EnsureStandin(Piece.PieceCategory category)
        {
            if (_standin != null) return _standin;
            var icon = VAAreaClearBrush.CreateIcon("FAP_ClutterClearIcon", IconRingColor, IconFillColor, IsIconEraser);
            _standin = VAAreaClearBrush.CreateStandin(StandinName, StandinName, "Clutter Clear Brush", Description, category, icon);
            return _standin;
        }

        // The brush can cross zone borders, so the 3x3 neighbourhood of TerrainComps is checked; that
        // covers radii well past MaxRadius.
        public static int ClearArea(Vector3 center, float radius, float heightBounds)
        {
            if (radius <= 0f) return 0;
            if (heightBounds <= 0f) heightBounds = DefaultHeightBounds;

            int totalRemoved = 0;
            var visited = new HashSet<TerrainComp>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    var probe = center + new Vector3(dx * TerrainCompGridSize, 0f, dz * TerrainCompGridSize);
                    var terrainComp = TerrainComp.FindTerrainCompiler(probe);
                    if (terrainComp == null || !visited.Add(terrainComp)) continue;

                    int removed = VAClutterPaintTool.RemoveEntriesInRadiusAndHeight(terrainComp, center, radius, heightBounds);
                    if (removed == 0) continue;
                    totalRemoved += removed;
                    VAClutterPaintTool.RestoreFromZDO(terrainComp);
                }
            }

            if (totalRemoved > 0 && VAFapLoadSummary.VerboseEnabled)
                Debug.Log($"{VaPieces.PluginName}: ClutterClear.ClearArea removed {totalRemoved} clutter entries inside r={radius:F1} h=±{heightBounds:F1} at {center}.");
            return totalRemoved;
        }

        private static bool IsIconEraser(float offsetX, float offsetY) =>
            VAAreaClearBrush.IsInsideIconFill(offsetX, offsetY) && Mathf.Abs(offsetY) <= IconEraserHalfThickness;
    }
}
