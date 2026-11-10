using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.383 (Fire 11-04, combined VA prefabs: "keep interactables interactable"): a Unity-built combined prefab ships its blueprint's interactables as a list,
    // plugins\<mod>\FapChildren\<prefab>.txt, blueprint lines. The first time a placed root loads anywhere, the server creates each one as its own ZDO; a stamp on the root's ZDO says it is done.
    // That also covers roots already in a world, which no placement event reaches. Only ZDOs are created; vanilla instantiates them when near.
    // Fire's rules: a loose piece with the same prefab at exactly the same position, rotation and scale is adopted (no child made); anything else is left alone, a near one
    // is reported. Nothing is ever removed. A removed root leaves its children standing, so chests keep their items. Without a list file this does nothing.
    [HarmonyPatch]
    internal static class VAPrefabChildren
    {
        private const string Folder = "FapChildren";
        private const float ExactPos = 0.002f, ExactRotDeg = 0.01f, ExactScale = 0.001f, NearPos = 0.5f;
        private static readonly int DoneKey = "fap_children_done".GetStableHashCode();
        private static readonly int ChildKey = "fap_prefab_child".GetStableHashCode();
        private static Dictionary<string, string> s_lists;
        private static HashSet<int> s_listHashes;   // 1.1.402: hashes of Lists() keys, built with it
        private static readonly Dictionary<string, string> s_stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        private static ZRoutedRpc s_registered;
        private static readonly HashSet<ZDOID> s_tried = new HashSet<ZDOID>();
        private const string CheckRpc = "FAP_PrefabChildrenCheck";

        private struct Child { public string Prefab; public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public string Data; public string Extra; }

        // Any client that loads a root asks the server; the call only says "look at this ZDO", so a forged one does nothing the real one wouldn't.
        internal static void OnPieceAwake(Piece piece)
        {
            var nview = piece.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;   // the placement ghost has no ZDO
            var zdo = nview.GetZDO();
            if (zdo.GetBool(ChildKey)) { Free(piece); return; }
            // 1.1.402 ([visual] audit via [lead] 11-08): no child list for this prefab (by hash) = return before the name string
            if (s_listHashes == null) { var set = new HashSet<int>(); foreach (var k in Lists().Keys) set.Add(k.GetStableHashCode()); s_listHashes = set; }
            if (!s_listHashes.Contains(zdo.GetPrefab())) return;
            string name = Utils.GetPrefabName(piece.gameObject);
            if (!Lists().ContainsKey(name) || zdo.GetString(DoneKey) == ListStamp(name)) return;
            if (ZNet.instance != null && ZNet.instance.IsServer()) Check(zdo.m_uid);
            else if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), CheckRpc, zdo.m_uid);
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void ZNetScene_Awake()
        {
            if (ZRoutedRpc.instance == null || ReferenceEquals(s_registered, ZRoutedRpc.instance)) return;
            s_registered = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register<ZDOID>(CheckRpc, (sender, id) => { if (ZNet.instance != null && ZNet.instance.IsServer()) Check(id); });
        }

        // Server only: it alone holds every ZDO, so adoption sees loose pieces in zones no client has loaded, and one writer can't double up.
        // Done is the list's stamp, written after success: a failure or a changed list runs again, and what already stands is adopted, not doubled.
        private static void Check(ZDOID id)
        {
            var zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null || ZNetScene.instance == null) return;
            var rootPrefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (rootPrefab == null || rootPrefab.GetComponent<Piece>() == null) return;
            string name = rootPrefab.name;
            if (!Lists().TryGetValue(name, out string path) || zdo.GetString(DoneKey) == ListStamp(name)) return;
            if (!s_tried.Add(id)) return;   // 1.1.388: one try per root per server session, so repeat calls can't force world walks when a list has a missing prefab
            try { if (SpawnAll(zdo, name, path)) { zdo.SetOwner(ZDOMan.GetSessionID()); zdo.Set(DoneKey, ListStamp(name)); } }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [PrefabChildren] {name}: {e.Message}"); }
        }

        private static string ListStamp(string name)
        {
            if (s_stamps.TryGetValue(name, out var stamp)) return stamp;
            try { stamp = File.ReadAllText(s_lists[name]).GetStableHashCode().ToString(CultureInfo.InvariantCulture); } catch { stamp = "?"; }
            return s_stamps[name] = stamp;
        }

        // Blueprint children stand on a mesh WearNTear can't see as support, so they would fall. They still take damage and can be removed.
        private static void Free(Piece piece)
        {
            var wear = piece.GetComponent<WearNTear>();
            if (wear != null) wear.m_noSupportWear = false;
            piece.m_resources = System.Array.Empty<Piece.Requirement>();   // Fire 11-05: no refund; this instance only, the creator stays for regen protection
        }

        private static Dictionary<string, string> Lists()
        {
            if (s_lists != null) return s_lists;
            s_lists = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var mod in Directory.GetDirectories(Paths.PluginPath))
                {
                    string dir = Path.Combine(mod, Folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.txt")) s_lists[Path.GetFileNameWithoutExtension(f)] = f;
                }
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [PrefabChildren] list scan: {e.Message}"); }
            VaPieces.Log?.LogDebug($"[PrefabChildren] {s_lists.Count} prefab(s) with a child list");
            return s_lists;
        }

        private static bool SpawnAll(ZDO root, string name, string path)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            long creator = root.GetLong(ZDOVars.s_creator);
            Vector3 rootPos = root.GetPosition(); Quaternion rootRot = root.GetRotation();
            int made = 0, adopted = 0, near = 0, missing = 0;
            var byPrefab = new Dictionary<int, List<ZDO>>();
            var lines = File.ReadAllLines(path);
            Vector3 adoptOffset = lines.Length > 0 ? ParseOffset(lines[0]) : Vector3.zero;
            foreach (var line in lines)
            {
                if (!TryParse(line, out var c)) continue;
                var prefab = ZNetScene.instance.GetPrefab(c.Prefab);
                var view = prefab != null ? prefab.GetComponent<ZNetView>() : null;
                if (view == null) { missing++; continue; }
                Vector3 pos = rootPos + rootRot * c.Pos;
                Quaternion rot = rootRot * c.Rot;
                int hash = c.Prefab.GetStableHashCode();
                switch (Compare(byPrefab, hash, c.Prefab, pos + rootRot * adoptOffset, rot, c.Scale, out string diff))
                {
                    case Match.Exact: adopted++; continue;
                    case Match.Near: near++; VaPieces.Log?.LogDebug($"[PrefabChildren] {name}: {c.Prefab} near, may be intentional: {diff}"); break;
                }
                var zdo = ZDOMan.instance.CreateNewZDO(pos, hash);
                zdo.Persistent = view.m_persistent || VerdantsAscent.Managers.VAVanillaPieceManager.PersistsOnPlace(c.Prefab);   // 1.1.398
                zdo.Distant = view.m_distant;
                zdo.Type = view.m_type;
                zdo.m_prefab = hash;
                if (!string.IsNullOrEmpty(c.Data) && VAZdoDataBlob.TryParse(c.Data, out var blob) && blob != null) { blob.WriteTo(zdo); blob.ApplyHashConnection(zdo); }
                zdo.SetRotation(rot);
                if (c.Scale != Vector3.one && c.Scale != Vector3.zero) zdo.Set(ZDOVars.s_scaleHash, c.Scale);
                zdo.Set(ZDOVars.s_creator, creator);
                zdo.Set(ChildKey, true);
                byPrefab[hash].Add(zdo);
                made++;
            }
            Debug.Log($"{VaPieces.PluginName}: [PrefabChildren] {name}: {made} interactable(s) created, {adopted} adopted, {near} beside a near match, {missing} prefab(s) missing, {t.Elapsed.TotalMilliseconds:0} ms");
            return missing == 0;
        }

        private static Vector3 ParseOffset(string line)
        {
            if (!line.StartsWith("#adoptoffset ", StringComparison.Ordinal)) return Vector3.zero;
            var p = line.Split(' ');
            return p.Length >= 4 ? new Vector3(float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture)) : Vector3.zero;
        }

        private enum Match { None, Near, Exact }

        private static Match Compare(Dictionary<int, List<ZDO>> cache, int hash, string prefabName, Vector3 pos, Quaternion rot, Vector3 scale, out string diff)
        {
            diff = null;
            if (!cache.TryGetValue(hash, out var list))
            {
                cache[hash] = list = new List<ZDO>();
                int index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, list, ref index)) { }
            }
            var best = Match.None;
            foreach (var z in list)
            {
                float d = Vector3.Distance(z.GetPosition(), pos);
                if (d > NearPos) continue;
                float a = Quaternion.Angle(z.GetRotation(), rot);
                Vector3 s = z.GetVec3(ZDOVars.s_scaleHash, Vector3.one);
                float ds = Vector3.Distance(s, scale == Vector3.zero ? Vector3.one : scale);
                if (d <= ExactPos && a <= ExactRotDeg && ds <= ExactScale) return Match.Exact;
                best = Match.Near;
                diff = string.Format(CultureInfo.InvariantCulture, "at {0}, {1:0.000} m, {2:0.00} deg, scale {3:0.000}", pos.ToString("F2"), d, a, ds);
            }
            return best;
        }

        // Blueprint line: name;category;x;y;z;qx;qy;qz;qw;extra;sx;sy;sz;data
        private static bool TryParse(string line, out Child c)
        {
            c = default;
            if (string.IsNullOrEmpty(line) || line[0] == '#') return false;
            var p = line.Split(';');
            if (p.Length < 9) return false;
            float F(int i, float dflt) => i < p.Length && float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : dflt;
            c.Prefab = p[0];
            c.Pos = new Vector3(F(2, 0f), F(3, 0f), F(4, 0f));
            c.Rot = new Quaternion(F(5, 0f), F(6, 0f), F(7, 0f), F(8, 1f));
            c.Extra = p.Length > 9 ? p[9] : "";
            c.Scale = new Vector3(F(10, 1f), F(11, 1f), F(12, 1f));
            c.Data = p.Length > 13 ? p[13] : "";
            return c.Prefab.Length > 0;
        }
    }
}
