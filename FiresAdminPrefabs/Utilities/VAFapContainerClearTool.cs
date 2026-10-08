using System;
using System.Collections.Generic;
using FiresCore.Net;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // Area brush in the Furniture tab that clears preset-driven chest respawning in a radius.
    //
    // Every Container in range carrying FAP's chest marker has the marker zeroed, so the generic
    // EWP destroy rule stops matching, and its preset name cleared, then is destroyed so the
    // clean-up is visible. Chests with no FAP preset are left alone. It is a separate brush from
    // the spawner clear so admins can target one respawn system at a time.
    public static class VAFapContainerClearTool
    {
        public const string NamePrefix = "FAP_ContainerClearTool";

        private const float IconSlashHalfWidth = 3f;
        private static readonly Color32 IconRingColor = new Color32(170, 110, 230, 255);
        private static readonly Color32 IconFillColor = new Color32(120, 70, 180, 200);
        private static readonly Vector2 IconSlashStart = new Vector2(-34f, 34f);
        private static readonly Vector2 IconSlashEnd = new Vector2(34f, -34f);

        // A one-pixel chest outline with its lid line a third of the way down.
        private const float IconChestLeft = -16f;
        private const float IconChestRight = 16f;
        private const float IconChestTop = -14f;
        private const float IconChestBottom = 14f;
        private const float IconChestLid = -5f;

        private static readonly int ChestMarkerKey = VAChestConfigApplier.KeyChestMarker.GetStableHashCode();
        private static readonly int ChestPresetKey = VAChestConfigApplier.KeyAppliedPresetName.GetStableHashCode();

        private static GameObject _standin;

        public static bool IsStandinName(string name)
            => !string.IsNullOrEmpty(name) && name.StartsWith(NamePrefix, StringComparison.Ordinal);

        public static GameObject EnsureStandin(Piece.PieceCategory furnitureCategory)
        {
            if (_standin != null) return _standin;
            var icon = VAAreaClearBrush.CreateIcon("FAP_ContainerClear_Icon", IconRingColor, IconFillColor, IsIconMark);
            _standin = VAAreaClearBrush.CreateStandin(NamePrefix, NamePrefix, "Container Clear Brush", BuildDescription(), furnitureCategory, icon);
            return _standin;
        }

        // Returns how many chests the first pass destroyed.
        public static int ClearArea(Vector3 center, float radius, float durationSec)
        {
            int destroyed = ClearOnce(center, radius);
            VAAreaClearBrush.RunWatchdog(durationSec, () => ClearOnce(center, radius));
            return destroyed;
        }

        private static string BuildDescription()
        {
            float radius = ConfigManager.Instance.configFxClearRadius.Value;
            return "<color=#a070ff>Container Clear</color> — destroys preset-driven chests "
                 + "in a radius AND disarms their preset marker so EWP's respawn rule won't "
                 + "refire. Vanilla chests in the area are untouched. After clearing, the "
                 + "area will stay quiet unless you explicitly place a new preset chest "
                 + $"there. Radius {radius:F0}m. Shift+Scroll resizes.";
        }

        private static int ClearOnce(Vector3 center, float radius)
        {
            var chests = new List<ZNetView>();
            foreach (var netView in VAAreaClearBrush.FindNetViewsInRadius(center, radius))
            {
                if (netView.GetZDO().GetInt(ChestMarkerKey, 0) == 0) continue;   // 1 = EWP respawn, 2 = Core respawn (1.1.171)
                if (netView.GetComponent<Container>() == null) continue;
                chests.Add(netView);
            }

            int destroyed = 0;
            foreach (var chest in chests)
            {
                try
                {
                    var zdo = chest.GetZDO();
                    zdo.Set(ChestMarkerKey, 0);
                    zdo.Set(ChestPresetKey, string.Empty);
                    zdo.IncreaseDataRevision();
                    NetworkObjectHelper.SafeDestroy(chest.gameObject);
                    destroyed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: ContainerClear.DestroyChest threw on '{chest.name}': {ex.Message}");
                }
            }

            if (destroyed > 0)
                Debug.Log($"{VaPieces.PluginName}: ContainerClear.ClearOnce — destroyed {destroyed} preset chest(s) at {center} r={radius:F1}.");
            return destroyed;
        }

        private static bool IsIconMark(float offsetX, float offsetY)
        {
            if (VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, IconSlashStart, IconSlashEnd, IconSlashHalfWidth)) return true;
            bool withinChestColumns = offsetX >= IconChestLeft && offsetX <= IconChestRight;
            bool withinChestRows = offsetY >= IconChestTop && offsetY <= IconChestBottom;
            bool onChestSide = (offsetX == IconChestLeft || offsetX == IconChestRight) && withinChestRows;
            bool onChestEdge = (offsetY == IconChestTop || offsetY == IconChestBottom || offsetY == IconChestLid) && withinChestColumns;
            return onChestSide || onChestEdge;
        }
    }
}
