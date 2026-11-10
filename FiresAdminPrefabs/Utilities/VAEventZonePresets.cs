using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.351 (Fire 10-31: "event zones should be placeable and configurable with presets"; preset = event + radius).
    //
    // Vanilla EventZone (eventzone_eikthyr ... _fader) is a trigger SphereCollider (radius 30) + m_event: a local player inside makes
    // that event RandEventSystem's forced event (boss music / environment, or a raid's spawns). A preset is {event, radius}:
    //   - picked on the build menu's right-click ("Event zone preset..."), queued for every placement of that zone until changed;
    //   - written on the placed zone's ZDO "fap_eventzone" = "<event>|<radius>" and sent to everyone (RPC), and read again on every
    //     load (ZNetView.Awake), so each FAP client's zone uses it. A client without FAP keeps the prefab's own event and size, so the
    //     menu is off while VanillaFriendly is on.
    // Presets: one built-in per vanilla event zone (its own event + radius) and the admin's saved ones,
    // config\FiresAdminPrefabs\EventZonePresets.txt ("name|event|radius" lines, client-only like the snap defaults).
    // The ghost gets a translucent sphere of the queued radius (the prefab itself draws nothing).
    [HarmonyPatch]
    public static class VAEventZonePresets
    {
        public const string ZdoKeyName = "fap_eventzone";
        private static readonly int ZdoKey = ZdoKeyName.GetStableHashCode();
        private const string RpcApply = "FAP_EventZoneApply";
        private static readonly float[] Radii = { 5f, 10f, 20f, 30f, 50f, 80f, 120f };

        internal sealed class Preset { public string Name, Event; public float Radius; }

        private static readonly Dictionary<string, Preset> _queued = new Dictionary<string, Preset>(StringComparer.Ordinal);
        private static List<Preset> _saved;

        public static bool IsEventZonePrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) || ZNetScene.instance == null) return false;
            var go = ZNetScene.instance.GetPrefab(prefabName);
            return go != null && go.GetComponent<EventZone>() != null;
        }

        private static void PrefabDefaults(string prefabName, out string ev, out float radius)
        {
            ev = ""; radius = 30f;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (go == null) return;
            ev = go.GetComponent<EventZone>()?.m_event ?? "";
            var sc = go.GetComponent<SphereCollider>();
            if (sc != null) radius = sc.radius;
        }

        public static string Describe(string prefabName)
        {
            if (_queued.TryGetValue(prefabName, out var q)) return $"{q.Event}, r {q.Radius:0}";
            PrefabDefaults(prefabName, out string ev, out float r);
            return $"own: {ev}, r {r:0}";
        }

        // ---- presets file ----

        private static string FilePath => Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "EventZonePresets.txt");

        private static List<Preset> Saved()
        {
            if (_saved != null) return _saved;
            _saved = new List<Preset>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var f = line.Split('|');
                        if (f.Length == 3 && float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) && r > 0f)
                            _saved.Add(new Preset { Name = f[0], Event = f[1], Radius = r });
                    }
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP] event zone presets not read: {ex.Message}"); }
            return _saved;
        }

        private static void SaveFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, Saved().Select(p => $"{p.Name}|{p.Event}|{p.Radius.ToString("0.##", CultureInfo.InvariantCulture)}").ToArray());
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP] event zone presets not saved: {ex.Message}"); }
        }

        private static IEnumerable<Preset> BuiltIns()
        {
            if (ZNetScene.instance == null) yield break;
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                var ez = go != null ? go.GetComponent<EventZone>() : null;
                if (ez == null || go.transform.parent != null) continue;
                var sc = go.GetComponent<SphereCollider>();
                yield return new Preset { Name = go.name, Event = ez.m_event, Radius = sc != null ? sc.radius : 30f };
            }
        }

        private static List<string> EventNames()
        {
            var names = new List<string>();
            try { if (RandEventSystem.instance != null) names.AddRange(RandEventSystem.instance.m_events.Where(e => e != null && !string.IsNullOrEmpty(e.m_name)).Select(e => e.m_name)); }
            catch { }
            return names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // ---- build-menu right-click ----

        public static void OpenMenu(string prefabName)
        {
            var items = new List<VARightClickMenu.Item>();
            _queued.TryGetValue(prefabName, out var q);
            if (q != null)
                items.Add(new VARightClickMenu.Item("× No preset (the zone's own event + radius)", () => { _queued.Remove(prefabName); Toast($"{prefabName}: own event + radius"); }));
            foreach (var p in BuiltIns().Concat(Saved()))
            {
                var c = p;
                bool on = q != null && q.Event == c.Event && Mathf.Approximately(q.Radius, c.Radius);
                items.Add(new VARightClickMenu.Item($"{(on ? "✔ " : "» ")}{c.Name}   <color=#999>({c.Event}, r {c.Radius:0})</color>", () => Queue(prefabName, c.Event, c.Radius)));
            }
            items.Add(new VARightClickMenu.Item("──────────", null, disabled: true));
            items.Add(new VARightClickMenu.Item("Event...", () => OpenEventMenu(prefabName)));
            items.Add(new VARightClickMenu.Item("Radius...", () => OpenRadiusMenu(prefabName)));
            if (q != null)
                items.Add(new VARightClickMenu.Item($"Save current as preset ({q.Event} r{q.Radius:0})", () =>
                {
                    string name = $"{q.Event} r{q.Radius:0}";
                    Saved().RemoveAll(p => p.Name == name);
                    Saved().Add(new Preset { Name = name, Event = q.Event, Radius = q.Radius });
                    SaveFile();
                    Toast($"Event zone preset saved: {name}");
                }));
            VARightClickMenu.Show(items);
        }

        private static Preset Current(string prefabName)
        {
            if (_queued.TryGetValue(prefabName, out var q)) return q;
            PrefabDefaults(prefabName, out string ev, out float r);
            return new Preset { Event = ev, Radius = r };
        }

        private static void OpenEventMenu(string prefabName)
        {
            var cur = Current(prefabName);
            var items = EventNames().Select(n => new VARightClickMenu.Item((n == cur.Event ? "✔ " : "» ") + n, () => Queue(prefabName, n, cur.Radius))).ToList();
            if (items.Count == 0) items.Add(new VARightClickMenu.Item("(no events: not in a world)", null, disabled: true));
            VARightClickMenu.Show(items);
        }

        private static void OpenRadiusMenu(string prefabName)
        {
            var cur = Current(prefabName);
            VARightClickMenu.Show(Radii.Select(r => new VARightClickMenu.Item((Mathf.Approximately(r, cur.Radius) ? "✔ " : "» ") + r.ToString("0") + " m", () => Queue(prefabName, cur.Event, r))).ToList());
        }

        private static void Queue(string prefabName, string ev, float radius)
        {
            _queued[prefabName] = new Preset { Event = ev, Radius = radius };
            Toast($"Every {prefabName} placement: {ev}, radius {radius:0} m (until changed)");
            VaPieces.Log?.LogDebug($"[FAP] event zone {prefabName}: queued {ev} r{radius:0}");
        }

        private static void Toast(string msg) => MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, msg);

        // ---- placement: stamp + broadcast ----

        internal static void OnPlaced(GameObject placed, string prefabName)
        {
            if (placed == null || placed.GetComponent<EventZone>() == null) return;
            if (ConfigManager.Instance?.configVanillaFriendly?.Value ?? false) return;
            if (!_queued.TryGetValue(prefabName, out var q)) return;
            var nv = placed.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            string value = q.Event + "|" + q.Radius.ToString("0.##", CultureInfo.InvariantCulture);
            nv.GetZDO().Set(ZdoKey, value);
            Apply(nv);
            nv.InvokeRPC(ZNetView.Everybody, RpcApply);
            VerdantsAscent.VaPieces.Log?.LogDebug($"[FAP] event zone {prefabName} placed with {q.Event}, radius {q.Radius:0} m");
        }

        // ---- every load / every change, every FAP client ----

        private static void Apply(ZNetView nv)
        {
            var ez = nv.GetComponent<EventZone>();
            if (ez == null || !nv.IsValid()) return;
            string v = nv.GetZDO().GetString(ZdoKey, "");
            if (v.Length == 0) return;
            int bar = v.LastIndexOf('|');
            if (bar <= 0 || !float.TryParse(v.Substring(bar + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) || !(r > 0f && r <= 500f)) return;
            ez.m_event = v.Substring(0, bar);
            var sc = nv.GetComponent<SphereCollider>();
            if (sc != null) sc.radius = r;
        }

        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        internal static void NViewAwake(ZNetView __instance)
        {
            try
            {
                if (__instance == null || __instance.GetComponent<EventZone>() == null || !__instance.IsValid()) return;
                var nv = __instance;
                nv.Register(RpcApply, (long sender) => Apply(nv));
                Apply(nv);
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP] event zone preset on load failed: {ex.Message}"); }
        }

        // The ghost of an event zone draws nothing: show the queued sphere.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPostfix]
        private static void GhostSphere(Player __instance)
        {
            try
            {
                if (__instance != Player.m_localPlayer || __instance.m_placementGhost == null) return;
                var ghost = __instance.m_placementGhost;
                if (ghost.GetComponent<EventZone>() == null || ghost.transform.Find("FAP_EventZoneSphere") != null) return;
                var cur = Current(Utils.GetPrefabName(ghost));
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = s.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);
                s.name = "FAP_EventZoneSphere";
                s.transform.SetParent(ghost.transform, false);
                s.transform.localScale = Vector3.one * (cur.Radius * 2f);
                var mr = s.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null) { var m = new Material(mr.sharedMaterial); m.color = new Color(0.6f, 0.3f, 1f, 0.2f); mr.sharedMaterial = m; }
            }
            catch { }
        }
    }
}
