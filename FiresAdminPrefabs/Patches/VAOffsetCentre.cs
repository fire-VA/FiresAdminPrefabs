using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Managers.Snapping;

namespace VerdantsAscent.Patches
{
    // 1.1.379 (Fire 11-03: saw tooth, "it should be smooth"): Infinity Hammer's frozen offset puts a piece's ORIGIN at pivot + rotation x offset. A piece whose
    // origin isn't its centre (grausten walls: 1 m below) runs off the circle by that much. While frozen, the ghost moves so the piece's CENTRE rides
    // the circle. No reference to Infinity Hammer: its two public statics are found by name once; without them this does nothing.
    //
    // 1.1.431 ([ghost], Fire 11-16, Arbby: a freeze-selected rock jumps ~1 m on the FIRST nudge, whatever the step; "-1 m" looks like -2 m): IH's
    // freeze-select freezes at the selected object's ORIGIN (Hammer.cs: Position.Freeze(views[0].transform.position)) and its nudges only change the
    // offset (Position.Apply: origin = Override + rotation x Offset). 1.1.379 shifted by -(rotation x centre) only once the offset was non-zero, so the
    // first nudge added the whole centre-to-origin distance on top of IH's step. Now the shift is rotation x centre minus the same at the rotation
    // the piece had when it froze: zero at freeze time (the piece stays exactly on its spot), IH's nudge is exactly IH's step, and turning still
    // swings the piece's centre round the pivot (the grausten-wall arc). The centre is scaled by the ghost's own scale (IH scaled selections).
    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    public static class VAOffsetCentre
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly Dictionary<string, Vector3> _centres = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        private static FieldInfo _override, _offset;
        private static bool _looked;

        // the freeze this shift is measured from: which ghost, at which frozen point, at which rotation
        private static int s_frozenGhost;
        private static Vector3 s_frozenAt;
        private static Quaternion s_frozenRot;
        private static bool s_frozen;
        // the last ghost's centre (one dictionary lookup + name string per new ghost, not per frame)
        private static int s_centreGhost;
        private static Vector3 s_centre;

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (ConfigManager.Instance?.configOffsetUsesCentre?.Value != true) { s_frozen = false; return; }
            var ghost = Ghost(__instance);
            if (ghost == null || !ghost.activeSelf || !Find()) { s_frozen = false; return; }
            if (!(_override.GetValue(null) is Vector3 at)) { s_frozen = false; return; }   // not frozen (a boxed empty Vector3? is null)
            int id = ghost.GetInstanceID();
            var t = ghost.transform;
            if (!s_frozen || s_frozenGhost != id || s_frozenAt != at)
            {
                // a new freeze, a new frozen point (hammer_pos) or a new selection: this frame's rotation is the reference, so nothing moves now
                s_frozen = true; s_frozenGhost = id; s_frozenAt = at; s_frozenRot = t.rotation;
                return;
            }
            var c = Centre(ghost, id);
            if (c == Vector3.zero) return;
            c = Vector3.Scale(c, t.localScale);
            Vector3 shift = s_frozenRot * c - t.rotation * c;
            if (shift.sqrMagnitude > 1e-8f) t.position += shift;
        }

        private static bool Find()
        {
            if (!_looked)
            {
                _looked = true;
                var type = AccessTools.TypeByName("InfinityHammer.Position");
                _override = type?.GetField("Override", BindingFlags.Public | BindingFlags.Static);
                _offset = type?.GetField("Offset", BindingFlags.Public | BindingFlags.Static);
                VaPieces.Log?.LogDebug($"[OffsetCentre] Infinity Hammer offset {(_override != null && _offset != null ? "found" : "not found; inactive")}");
            }
            return _override != null && _offset != null;
        }

        private static Vector3 Centre(GameObject ghost, int id)
        {
            if (s_centreGhost == id) return s_centre;
            s_centreGhost = id;
            if (ghost.GetComponent<Piece>() == null) return s_centre = Vector3.zero;
            string name = Utils.GetPrefabName(ghost);
            if (!_centres.TryGetValue(name, out var c))
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                c = prefab != null && VASnapGeometry.TryLocalBounds(prefab, out Bounds b) ? b.center : Vector3.zero;
                _centres[name] = c;
                VaPieces.Log?.LogDebug($"[OffsetCentre] {name}: centre {c.ToString("F3")} from origin");
            }
            return s_centre = c;
        }
    }
}
