using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.352 (Fire 10-31: "when we have the hammer out the spawners should get a ghost box we can interact with like our wayshrines
    // mod does" + "no, just the placed spawners that usually dont have a physical interactable object, nests can be deleted with the
    // hammer normally" + "give it a config toggle, "show spawners with hammer out"").
    //
    // A CreatureSpawner / TriggerSpawner placed from FAP's Hammer gets ZDO "fap_placed_spawner" (VAPlacedPieceIH). On every FAP client
    // such an instance gets Core's HammerBox (FiresCore.Pieces.HammerBox.Attach, moved out of FiresWayshrines' trigger wire: one
    // implementation): a hoverable / hammer-removable box, drawn while the local player holds the Hammer (Core's equip event). World
    // spawners and SpawnArea nests are never touched. [HammerToggles] "Show spawners with hammer out" (default on since 1.1.353) adds / removes the
    // boxes live. An older Core = no boxes, one line saying why.
    [HarmonyPatch]
    public static class VAPlacedSpawnerBoxes
    {
        public static readonly int ZdoKey = "fap_placed_spawner".GetStableHashCode();
        private static readonly Vector3 BoxSize = new Vector3(1f, 1.5f, 1f);
        private static readonly Color BoxColor = new Color(0.9f, 0.3f, 0.3f, 1f);

        private static readonly List<ZNetView> _placed = new List<ZNetView>();
        private static readonly Dictionary<int, Component> _boxes = new Dictionary<int, Component>();
        private static MethodInfo _attach;
        private static bool _looked, _hooked;

        private static bool On => ConfigManager.Instance?.configShowSpawnersWithHammer?.Value ?? false;

        // 1.1.353 (Fire 10-31 launch 2: "placed spawners and event zones get no wireframe when the hammer is out"): FAP-placed event zones
        // (an invisible trigger, nothing to hover or hit) get the same box.
        private static bool IsBoxable(GameObject go) => go != null && (go.GetComponent<CreatureSpawner>() != null || go.GetComponent<TriggerSpawner>() != null || go.GetComponent<EventZone>() != null || go.GetComponent<LocationProxy>() != null);   // 1.1.361: FAP entrances; world proxies have no ZdoKey

        internal static void OnPlaced(GameObject placed)
        {
            if (!IsBoxable(placed)) return;
            var nv = placed.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            nv.GetZDO().Set(ZdoKey, true);
            Track(nv);
        }

        private static MethodInfo Attach()
        {
            if (_looked) return _attach;
            _looked = true;
            var t = AccessTools.TypeByName("FiresCore.Pieces.HammerBox");
            _attach = t != null ? AccessTools.Method(t, "Attach", new[] { typeof(GameObject), typeof(Vector3), typeof(Vector3), typeof(Color), typeof(bool) }) : null;
            if (_attach == null) Debug.Log("[FAP] spawner boxes: Core FiresCore.Pieces.HammerBox.Attach not found (older Core); placed spawners get no box");
            return _attach;
        }

        internal static void TrackAny(ZNetView nv) { if (nv != null && nv.IsValid()) Track(nv); }   // 1.1.362

        private static void Track(ZNetView nv)
        {
            HookSetting();
            _placed.RemoveAll(x => x == null);
            if (!_placed.Contains(nv)) _placed.Add(nv);
            if (On) AddBox(nv);
        }

        private static void AddBox(ZNetView nv)
        {
            if (nv == null || Application.isBatchMode) return;
            int id = nv.GetInstanceID();
            if (_boxes.TryGetValue(id, out var existing) && existing != null) { SetBoxChildren(nv, true); return; }
            var m = Attach();
            if (m == null) return;
            try
            {
                var c = m.Invoke(null, new object[] { nv.gameObject, BoxSize, new Vector3(0f, BoxSize.y * 0.5f, 0f), BoxColor, true }) as Component;
                if (c != null) _boxes[id] = c;
                VaPieces.Log?.LogDebug($"[FAP] spawner box on placed {Utils.GetPrefabName(nv.gameObject)} at {nv.transform.position.ToString("F1")}");
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP] spawner box failed on {nv.name}: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        // Core's HammerBox component sits on the spawner's ROOT; its collider ("HammerBox") and wire ("HammerBoxWire") are children.
        // The toggle switches only those two children, never the spawner itself.
        private static void SetBoxChildren(ZNetView nv, bool on)
        {
            if (nv == null) return;
            foreach (var n in new[] { "HammerBox", "HammerBoxWire" })
            {
                var t = nv.transform.Find(n);
                if (t != null) t.gameObject.SetActive(on);
            }
        }

        private static void HookSetting()
        {
            if (_hooked || ConfigManager.Instance?.configShowSpawnersWithHammer == null) return;
            _hooked = true;
            ConfigManager.Instance.configShowSpawnersWithHammer.SettingChanged += (s, e) =>
            {
                _placed.RemoveAll(x => x == null);
                int n = 0;
                foreach (var nv in _placed)
                {
                    if (On) { AddBox(nv); n++; }
                    else if (_boxes.TryGetValue(nv.GetInstanceID(), out var c) && c != null) { SetBoxChildren(nv, false); n++; }
                }
                Debug.Log($"[FAP] Show spawners with hammer out = {On}: {n} placed spawner box(es) {(On ? "shown" : "hidden")}");
            };
        }

        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        internal static void NViewAwake(ZNetView __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsValid()) return;
                if (!__instance.GetZDO().GetBool(ZdoKey) || !IsBoxable(__instance.gameObject)) return;   // 1.1.402: the ZDO flag first, the 4 GetComponents only for flagged ones
                Track(__instance);
            }
            catch { }
        }
    }
}
