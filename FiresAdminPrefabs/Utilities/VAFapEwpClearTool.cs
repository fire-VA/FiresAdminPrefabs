using System;
using System.Collections.Generic;
using FiresCore.Net;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Area brush that stops ExpandWorld respawning FX in a radius, then removes FAP's own.
    //
    // The loop it breaks: a persistence-tagged FX self-destructs on its TimedDestruction, and EWP's
    // rule matches `int, FAP_persist, 1` on the destroy and respawns it with the same tag, forever.
    // Writing FAP_persist=0 on every object in range stops that at the next destroy, and is a no-op
    // on objects that never had the field. Objects marked as FAP placements are destroyed straight
    // away so the clean-up is visible; vanilla objects are left standing.
    //
    // Each FX tab gets its own stand-in, all dispatching here by name prefix.
    public static class VAFapEwpClearTool
    {
        public const string NamePrefix = "FAP_FxClearTool";

        private const float IconSlashHalfWidth = 6f;
        private static readonly Color32 IconRingColor = new Color32(220, 40, 40, 255);
        private static readonly Color32 IconFillColor = new Color32(180, 30, 30, 200);

        private static readonly int FapPersistKey = "FAP_persist".GetStableHashCode();
        private static readonly int FapPieceKey = "fap_piece".GetStableHashCode();

        // Set on an effect whose TTL FAP overrode, so it still marks a FAP placement that lost fap_piece.
        private static readonly int EffectNoTtlKey = "vap_effect_no_ttl".GetStableHashCode();

        private const string Description =
            "<color=#ff6633>FX Clear Brush.</color> Click to disarm EWP respawn on every "
            + "object in the brush radius (sets FAP_persist=0 on the ZDO, so EWP's "
            + "respawn filter stops matching at the next destroy event). FAP-placed "
            + "objects (fap_piece=1) are also destroyed on the spot for visible cleanup; "
            + "vanilla world content is left in place (only the FAP_persist field is "
            + "cleared, which is a no-op for objects that never had it set). Watchdog "
            + "re-runs every WatchdogTickIntervalSec for WatchdogDurationSec to catch "
            + "anything EWP respawned during the destroy/sync race. Shift+Scroll resizes.";

        private static readonly Dictionary<Piece.PieceCategory, GameObject> _standinsByCategory =
            new Dictionary<Piece.PieceCategory, GameObject>();
        private static Sprite _icon;

        public static bool IsStandinName(string name)
            => !string.IsNullOrEmpty(name) && name.StartsWith(NamePrefix, StringComparison.Ordinal);

        public static IEnumerable<KeyValuePair<Piece.PieceCategory, GameObject>> AllStandins => _standinsByCategory;

        // Vanilla files a piece under exactly one Piece.m_category, so each FX tab needs its own stand-in.
        public static GameObject EnsureStandinFor(Piece.PieceCategory category)
        {
            if (_standinsByCategory.TryGetValue(category, out var existing) && existing != null) return existing;
            if (_icon == null) _icon = VAAreaClearBrush.CreateIcon("FAP_FxClearIcon", IconRingColor, IconFillColor, IsIconSlash);

            var standin = VAAreaClearBrush.CreateStandin(NamePrefix, $"{NamePrefix}_{(int)category}", "FX Clear Brush", Description, category, _icon);
            _standinsByCategory[category] = standin;
            return standin;
        }

        // Returns how many FAP placements the first pass destroyed.
        public static int ClearArea(Vector3 center, float radius, float durationSec)
        {
            var (disarmed, destroyed) = ClearOnce(center, radius);
            if (VAFapLoadSummary.VerboseEnabled && (disarmed > 0 || destroyed > 0))
                Debug.Log($"{VaPieces.PluginName}: FxClear.ClearOnce — disarmed FAP_persist on {disarmed}, destroyed {destroyed} FAP placement(s) at {center} r={radius:F1}.");

            int caughtRespawns = 0;
            VAAreaClearBrush.RunWatchdog(durationSec,
                () => caughtRespawns += ClearOnce(center, radius).Destroyed,
                () =>
                {
                    if (caughtRespawns > 0 && VAFapLoadSummary.VerboseEnabled)
                        Debug.Log($"{VaPieces.PluginName}: FxClear.Watchdog — caught {caughtRespawns} respawned FAP placement(s) during {durationSec:F1}s watch at {center}.");
                });
            return destroyed;
        }

        // FAP placement markers are read before FAP_persist is cleared, since it is one of them.
        // Clearing the field before the destroy gives the field sync its best chance to reach the
        // server first.
        private static (int Disarmed, int Destroyed) ClearOnce(Vector3 center, float radius)
        {
            var inRange = VAAreaClearBrush.FindNetViewsInRadius(center, radius);
            var fapPlacements = new List<ZNetView>();
            foreach (var netView in inRange)
            {
                var zdo = netView.GetZDO();
                if (zdo.GetInt(FapPieceKey, 0) == 1 || zdo.GetInt(FapPersistKey, 0) == 1 || zdo.GetBool(EffectNoTtlKey, false))
                    fapPlacements.Add(netView);
            }

            int disarmed = 0;
            foreach (var netView in inRange)
            {
                try
                {
                    netView.GetZDO().Set(FapPersistKey, 0);
                    disarmed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: FxClear.Disarm threw on '{netView.name}': {ex.Message}");
                }
            }

            int destroyed = 0;
            foreach (var netView in fapPlacements)
            {
                try
                {
                    NetworkObjectHelper.SafeDestroy(netView.gameObject);
                    destroyed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: FxClear.Destroy threw on '{netView.name}': {ex.Message}");
                }
            }
            return (disarmed, destroyed);
        }

        private static bool IsIconSlash(float offsetX, float offsetY) =>
            VAAreaClearBrush.IsInsideIconFill(offsetX, offsetY)
            && VAAreaClearBrush.DistanceToIconDiagonal(offsetX, offsetY) <= IconSlashHalfWidth;
    }
}
