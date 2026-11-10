using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Animations;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.398 (Fire 11-07: phantom wet drops at ~1651,536 that survive a relog).
    //
    // Cause: up to 1.1.397 FAP set m_persistent on the SHARED vanilla vfx prefabs it put on the
    // hammer. A status effect's FX (vfx_Wet...) carries a ZNetView, so each creature's wet drops
    // became a SAVED ZDO; vanilla's ZNetScene.RemoveObjects only drops a ZDO that is
    // !Persistent, so the drops outlived the creature and the relog. 1.1.398 leaves the shared
    // prefab alone and saves each placement on its own ZDO instead.
    //
    // This class finds the ones already saved: a ZDO of a status-effect FX prefab whose shared
    // prefab is transient, yet the ZDO is Persistent and nobody placed it (no creator, no
    // fap_piece / FAP_persist / no-TTL marker). Census = counts only; Remove = claims and destroys.
    public static class VAStatusFxOrphans
    {
        private static readonly int FapPieceKey = "fap_piece".GetStableHashCode();
        private static readonly int FapPersistKey = "FAP_persist".GetStableHashCode();
        private static readonly int EffectNoTtlKey = "vap_effect_no_ttl".GetStableHashCode();

        public sealed class Census
        {
            public int StatusFxPrefabs;
            public int Zdos;          // ZDOs of status-effect FX prefabs this machine holds
            public int Transient;     // not persistent (vanilla behaviour)
            public int Placed;        // persistent and placed (creator or FAP marker)
            public int Orphans;       // persistent, never placed: the leak
            public int AttachedLive;  // persistent orphan whose instance still follows a live character
            public readonly Dictionary<string, int> OrphansByPrefab = new Dictionary<string, int>();
            public readonly List<ZDO> OrphanZdos = new List<ZDO>();

            public string ByPrefabText()
            {
                if (OrphansByPrefab.Count == 0) return "none";
                var parts = new List<string>();
                foreach (var kv in OrphansByPrefab) parts.Add($"{kv.Key} {kv.Value}");
                return string.Join(", ", parts);
            }
        }

        private static Dictionary<int, string> s_statusFx;

        // Every prefab with a ZNetView that a status effect spawns through any EffectList field,
        // whose shared prefab is transient. Read by reflection over the fields so no name is assumed.
        internal static Dictionary<int, string> StatusFxPrefabs()
        {
            if (s_statusFx != null) return s_statusFx;
            var map = new Dictionary<int, string>();
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_StatusEffects == null) return map;
            foreach (var se in odb.m_StatusEffects)
            {
                if (se == null) continue;
                foreach (var f in se.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(EffectList)) continue;
                    if (!(f.GetValue(se) is EffectList el) || el.m_effectPrefabs == null) continue;
                    foreach (var data in el.m_effectPrefabs)
                    {
                        var prefab = data?.m_prefab;
                        if (prefab == null) continue;
                        var view = prefab.GetComponent<ZNetView>();
                        if (view == null || view.m_persistent) continue;
                        map[prefab.name.GetStableHashCode()] = prefab.name;
                    }
                }
            }
            if (map.Count > 0) s_statusFx = map;
            return map;
        }

        // center/radius null = every ZDO this machine holds.
        public static Census Take(Vector3? center, float radius)
        {
            var c = new Census();
            var fx = StatusFxPrefabs();
            c.StatusFxPrefabs = fx.Count;
            var all = VAZdoManAccess.SnapshotAllZdos();
            if (all == null || fx.Count == 0) return c;
            float r2 = radius * radius;
            for (int i = 0; i < all.Count; i++)
            {
                var zdo = all[i];
                if (zdo == null || !fx.TryGetValue(zdo.GetPrefab(), out string name)) continue;
                if (center.HasValue)
                {
                    Vector3 p = zdo.GetPosition();
                    float dx = p.x - center.Value.x, dz = p.z - center.Value.z;
                    if (dx * dx + dz * dz > r2) continue;
                }
                c.Zdos++;
                if (!zdo.Persistent) { c.Transient++; continue; }
                if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L || zdo.GetInt(FapPieceKey, 0) != 0
                    || zdo.GetInt(FapPersistKey, 0) != 0 || zdo.GetBool(EffectNoTtlKey, false))
                { c.Placed++; continue; }
                if (FollowsLiveCharacter(zdo)) { c.AttachedLive++; continue; }
                c.Orphans++;
                c.OrphanZdos.Add(zdo);
                c.OrphansByPrefab.TryGetValue(name, out int n);
                c.OrphansByPrefab[name] = n + 1;
            }
            return c;
        }

        // A loaded instance still constrained to (or parented under) a live character is an active
        // effect, never an orphan.
        private static bool FollowsLiveCharacter(ZDO zdo)
        {
            var scene = ZNetScene.instance;
            var view = scene != null ? scene.FindInstance(zdo) : null;
            if (view == null) return false;
            if (view.GetComponentInParent<Character>() != null) return true;
            var pc = view.GetComponentInChildren<ParentConstraint>(true);
            if (pc == null) return false;
            for (int i = 0; i < pc.sourceCount; i++)
            {
                var t = pc.GetSource(i).sourceTransform;
                if (t != null && t.GetComponentInParent<Character>() != null) return true;
            }
            return false;
        }

        public static int Remove(Census c)
        {
            if (c == null || c.OrphanZdos.Count == 0 || ZDOMan.instance == null) return 0;
            long session = ZDOMan.GetSessionID();
            int removed = 0;
            foreach (var zdo in c.OrphanZdos)
            {
                try
                {
                    var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                    if (!zdo.IsOwner()) zdo.SetOwner(session);
                    if (view != null) ZNetScene.instance.Destroy(view.gameObject);
                    else ZDOMan.instance.DestroyZDO(zdo);
                    removed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: [StatusFx] removing orphan {zdo.m_uid} threw: {ex.Message}");
                }
            }
            return removed;
        }

        // ── One-time load sweep (server / host) ─────────────────────────────

        private static string s_sweptWorld;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNet_Awake_StatusFxSweep
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (VaPieces.Instance != null) VaPieces.Instance.StartCoroutine(SweepWhenReady());
            }
        }

        private static IEnumerator SweepWhenReady()
        {
            float give = Time.realtimeSinceStartup + 120f;
            while (ZNet.instance == null || ZNetScene.instance == null || ObjectDB.instance == null || ZDOMan.instance == null)
            {
                if (Time.realtimeSinceStartup > give) yield break;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(10f);   // world ZDOs loaded, ObjectDB status effects registered
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) yield break;
            string world = net.GetWorldName() ?? "?";
            if (s_sweptWorld == world) yield break;
            s_sweptWorld = world;

            Census c;
            try { c = Take(null, 0f); }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [StatusFx] census threw: {ex.Message}"); yield break; }

            bool on = ConfigManager.Instance.configFxClearOrphanSweepOnLoad.Value;
            int removed = on ? Remove(c) : 0;
            Debug.Log($"{VaPieces.PluginName}: [StatusFx] world '{world}': {c.Zdos} status-effect FX ZDO(s) of {c.StatusFxPrefabs} prefab(s): {c.Transient} transient, {c.Placed} placed, {c.AttachedLive} on a live character, {c.Orphans} left behind ({c.ByPrefabText()}); "
                + (on ? $"removed {removed} (1.1.398)." : "kept, RemoveOrphanStatusFxOnLoad is off (1.1.398)."));
        }
    }
}
