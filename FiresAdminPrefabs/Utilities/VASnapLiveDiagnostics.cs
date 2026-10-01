using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Reports which snap pair vanilla ACTUALLY chooses while the build ghost is
    // up. Dumping the prefab's snap points only proves the geometry exists; it
    // says nothing about which of them wins, and "snaps from the centre" is a
    // statement about the winner. This mirrors Player.FindClosestSnapPoints
    // rather than re-deriving it: same 0.5m pairing distance, same 10m target
    // search, same nearest-wins rule.
    //
    // AccessTools rather than direct private access: a FieldAccessException in a
    // diagnostic's try/catch produces silence, which reads exactly like "the
    // feature works and found nothing".
    internal static class VASnapLiveDiagnostics
    {
        private const float SampleIntervalSec = 1f;
        private const float PairMaxDistance = 0.5f;   // Player.UpdatePlacementGhost
        private const float TargetSearchRadius = 10f; // Player.FindClosestSnapPoints

        private static AccessTools.FieldRef<Player, GameObject> _ghostRef;
        private static AccessTools.FieldRef<Player, int> _manualSnapRef;
        private static bool _resolved;
        private static float _nextSample;

        public static bool Enabled { get; private set; }

        public static string Toggle(bool on)
        {
            Enabled = on;
            _nextSample = 0f;
            if (!on) return "[fap_snaplive] off.";
            return "[fap_snaplive] ON — aim at the piece you want to snap to, with the ghost up. "
                 + "Sampling once a second to the log; run 'fap_snaplive off' when done.";
        }

        private static bool Resolve()
        {
            if (_resolved) return _ghostRef != null && _manualSnapRef != null;
            _resolved = true;
            try
            {
                _ghostRef = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
                _manualSnapRef = AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: fap_snaplive could not bind Player fields: {ex.Message}");
                _ghostRef = null;
                _manualSnapRef = null;
            }
            return _ghostRef != null && _manualSnapRef != null;
        }

        public static void Sample()
        {
            if (!Enabled || !Resolve()) return;
            if (Time.realtimeSinceStartup < _nextSample) return;
            _nextSample = Time.realtimeSinceStartup + SampleIntervalSec;

            var player = Player.m_localPlayer;
            if (player == null) return;

            var ghost = _ghostRef(player);
            if (ghost == null) return;

            var ghostPiece = ghost.GetComponent<Piece>();
            if (ghostPiece == null) return;

            var ghostPoints = new List<Transform>();
            ghostPiece.GetSnapPoints(ghostPoints);

            var targetPoints = new List<Transform>();
            var targetPieces = new List<Piece>();
            Piece.GetSnapPoints(ghost.transform.position, TargetSearchRadius, targetPoints, targetPieces);

            int manual = _manualSnapRef(player);
            var header = $"{VaPieces.PluginName}: [snaplive] '{ghost.name}' ghostPts={ghostPoints.Count} "
                + $"targetPts={targetPoints.Count} across {targetPieces.Count} piece(s); "
                + $"mode={(manual < 0 ? "AUTO" : "MANUAL idx " + manual)}";

            if (ghostPoints.Count == 0 || targetPoints.Count == 0)
            {
                Debug.Log(header + " — nothing to pair with.");
                return;
            }

            // Winner under vanilla's auto rule: the ghost point whose nearest
            // target point is closest overall, capped at PairMaxDistance.
            Transform bestGhost = null, bestTarget = null;
            float best = float.PositiveInfinity;
            int bestGhostIdx = -1;
            for (int i = 0; i < ghostPoints.Count; i++)
            {
                for (int j = 0; j < targetPoints.Count; j++)
                {
                    float d = Vector3.Distance(ghostPoints[i].position, targetPoints[j].position);
                    if (d >= best || d > PairMaxDistance) continue;
                    best = d;
                    bestGhost = ghostPoints[i];
                    bestTarget = targetPoints[j];
                    bestGhostIdx = i;
                }
            }

            if (bestGhost == null)
            {
                Debug.Log(header + $" — NO pair within {PairMaxDistance}m, so the ghost stays on the cursor "
                    + "(this is what reads as 'placing from the centre').");
                return;
            }

            var gl = bestGhost.localPosition;
            var tl = bestTarget.localPosition;
            Debug.Log(header + $" — winner: ghost[{bestGhostIdx}] '{bestGhost.name}' local "
                + $"({gl.x:0.##},{gl.y:0.##},{gl.z:0.##}) -> target '{bestTarget.name}' local "
                + $"({tl.x:0.##},{tl.y:0.##},{tl.z:0.##}) on '{bestTarget.parent?.name}' at {best:0.###}m.");
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class UpdatePlacementGhostPatch
        {
            private static void Postfix()
            {
                if (!Enabled) return;
                try { Sample(); }
                catch (Exception ex)
                {
                    Enabled = false;
                    Debug.LogWarning($"{VaPieces.PluginName}: fap_snaplive threw, disabling: {ex.Message}");
                }
            }
        }
    }
}
