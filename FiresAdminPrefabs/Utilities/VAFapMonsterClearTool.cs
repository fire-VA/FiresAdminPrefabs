using System;
using System.Collections.Generic;
using FiresCore.Net;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Area brush in the Monsters tab that clears preset-driven respawning in a radius.
    //
    // A creature carrying any preset's marker has the marker zeroed, so its preset's EWP destroy
    // rule won't fire, and is destroyed. Each respawn anchor in range has FAP's per-preset anchor
    // keys and death timestamp zeroed, which detaches it from the recovery sweep; the anchor is a
    // vanilla Pickable_Flint, so the flint itself stays. Ambient respawning stops for every preset
    // that exists at clear time, until a new preset spawner is placed there.
    public static class VAFapMonsterClearTool
    {
        public const string NamePrefix = "FAP_MonsterClearTool";

        private const float IconSlashHalfWidth = 3f;
        private const float IconCrossHalfWidth = 2f;
        private static readonly Color32 IconRingColor = new Color32(40, 170, 200, 255);
        private static readonly Color32 IconFillColor = new Color32(30, 120, 150, 200);
        private static readonly Vector2 IconSlashStart = new Vector2(-34f, 34f);
        private static readonly Vector2 IconSlashEnd = new Vector2(34f, -34f);
        private static readonly Vector2 IconCrossExtent = new Vector2(8f, 8f);

        private static readonly int AnchorPrefabHash = VAMonsterAnchor.AnchorPrefabName.GetStableHashCode();
        private static readonly int DeadAtKey = VAMonsterAnchor.DeadAtKey.GetStableHashCode();

        private static GameObject _standin;

        public static bool IsStandinName(string name)
            => !string.IsNullOrEmpty(name) && name.StartsWith(NamePrefix, StringComparison.Ordinal);

        public static GameObject EnsureStandin(Piece.PieceCategory monstersCategory)
        {
            if (_standin != null) return _standin;
            var icon = VAAreaClearBrush.CreateIcon("FAP_MonsterClear_Icon", IconRingColor, IconFillColor, IsIconMark);
            _standin = VAAreaClearBrush.CreateStandin(NamePrefix, NamePrefix, "Spawner Clear Brush", BuildDescription(), monstersCategory, icon);
            return _standin;
        }

        // Returns how many creatures the first pass destroyed.
        public static int ClearArea(Vector3 center, float radius, float durationSec)
        {
            int destroyed = ClearOnce(center, radius);
            VAAreaClearBrush.RunWatchdog(durationSec, () => ClearOnce(center, radius));
            return destroyed;
        }

        private static string BuildDescription()
        {
            float radius = ConfigManager.Instance.configFxClearRadius.Value;
            return "<color=#88aaff>Spawner Clear</color> — destroys preset-driven monsters "
                 + "in a radius AND disarms the anchor markers that drive their respawn. "
                 + "Vanilla monsters and pickables in the area are untouched. After clearing, "
                 + "the area will stay quiet unless you explicitly place a new preset spawner "
                 + $"there. Radius {radius:F0}m. Shift+Scroll resizes.";
        }

        // Markers are zeroed before the destroy, so the EWP destroy rule no longer matches the ZDO.
        private static int ClearOnce(Vector3 center, float radius)
        {
            var presetMarkerKeys = new List<int>();
            var presetAnchorKeys = new List<int>();
            foreach (var preset in VAMonsterPresetStore.All.Values)
            {
                if (preset == null || string.IsNullOrEmpty(preset.name)) continue;
                presetMarkerKeys.Add(preset.name.GetStableHashCode());
                presetAnchorKeys.Add((VAMonsterAnchor.AnchorKeyPrefix + preset.name).GetStableHashCode());
            }
            if (presetMarkerKeys.Count == 0)
            {
                Debug.Log($"{VaPieces.PluginName}: SpawnerClear — no presets registered; nothing to clear.");
                return 0;
            }

            var anchors = new List<ZNetView>();
            var creatures = new List<ZNetView>();
            foreach (var netView in VAAreaClearBrush.FindNetViewsInRadius(center, radius))
            {
                var zdo = netView.GetZDO();
                if (zdo.GetPrefab() == AnchorPrefabHash)
                {
                    if (HasAnyNonZero(zdo, presetAnchorKeys)) anchors.Add(netView);
                }
                else if (HasAnyNonZero(zdo, presetMarkerKeys))
                {
                    creatures.Add(netView);
                }
            }

            int disarmedAnchors = 0;
            foreach (var anchor in anchors)
            {
                try
                {
                    var zdo = anchor.GetZDO();
                    ZeroAll(zdo, presetAnchorKeys);
                    if (zdo.GetFloat(DeadAtKey, 0f) != 0f) zdo.Set(DeadAtKey, 0f);
                    zdo.IncreaseDataRevision();
                    disarmedAnchors++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: SpawnerClear.DisarmAnchor threw: {ex.Message}");
                }
            }

            int destroyedCreatures = 0;
            foreach (var creature in creatures)
            {
                try
                {
                    var zdo = creature.GetZDO();
                    ZeroAll(zdo, presetMarkerKeys);
                    zdo.IncreaseDataRevision();
                    NetworkObjectHelper.SafeDestroy(creature.gameObject);
                    destroyedCreatures++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: SpawnerClear.DestroyCreature threw on '{creature.name}': {ex.Message}");
                }
            }

            if (destroyedCreatures > 0 || disarmedAnchors > 0)
                Debug.Log($"{VaPieces.PluginName}: SpawnerClear.ClearOnce — destroyed {destroyedCreatures} creature(s), disarmed {disarmedAnchors} anchor marker(s) at {center} r={radius:F1}.");
            return destroyedCreatures;
        }

        private static bool HasAnyNonZero(ZDO zdo, List<int> keys)
        {
            foreach (int key in keys)
                if (zdo.GetInt(key, 0) != 0) return true;
            return false;
        }

        private static void ZeroAll(ZDO zdo, List<int> keys)
        {
            foreach (int key in keys)
                if (zdo.GetInt(key, 0) != 0) zdo.Set(key, 0);
        }

        private static bool IsIconMark(float offsetX, float offsetY) =>
            VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, IconSlashStart, IconSlashEnd, IconSlashHalfWidth)
            || VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, -IconCrossExtent, IconCrossExtent, IconCrossHalfWidth)
            || VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(IconCrossExtent.x, -IconCrossExtent.y), new Vector2(-IconCrossExtent.x, IconCrossExtent.y), IconCrossHalfWidth);
    }
}
