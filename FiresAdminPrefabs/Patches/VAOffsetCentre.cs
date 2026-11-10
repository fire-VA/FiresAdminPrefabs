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
    // origin isn't its centre (grausten walls: 1 m below) runs off the circle by that much. While frozen with an offset, the ghost moves so
    // the piece's CENTRE rides the circle. Centred pieces don't move. No reference to Infinity Hammer: its two public statics are found by name
    // once; without them this does nothing. Zero offset = no shift, so a reselected tilted piece stays on itself.
    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    public static class VAOffsetCentre
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly Dictionary<string, Vector3> _centres = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        private static FieldInfo _override, _offset;
        private static bool _looked;

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (ConfigManager.Instance?.configOffsetUsesCentre?.Value != true) return;
            var ghost = Ghost(__instance);
            if (ghost == null || !ghost.activeSelf || ghost.GetComponent<Piece>() == null) return;
            if (!Find() || _override.GetValue(null) == null) return;   // not frozen (a boxed empty Vector3? is null)
            if (!(_offset.GetValue(null) is Vector3 offset) || offset == Vector3.zero) return;
            var c = Centre(ghost);
            if (c != Vector3.zero) ghost.transform.position -= ghost.transform.rotation * c;
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

        private static Vector3 Centre(GameObject ghost)
        {
            string name = Utils.GetPrefabName(ghost);
            if (_centres.TryGetValue(name, out var c)) return c;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            c = prefab != null && VASnapGeometry.TryLocalBounds(prefab, out Bounds b) ? b.center : Vector3.zero;
            _centres[name] = c;
            VaPieces.Log?.LogDebug($"[OffsetCentre] {name}: centre {c.ToString("F3")} from origin");
            return c;
        }
    }
}
