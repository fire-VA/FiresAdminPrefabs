using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.405 (Fire 11-08: "reduce the triangle count while not making the game look worse ... better lod logic"; [classes-ui]: at the
    // TOWN50 615 pose FirTree 213k tris / 358 renderers, Pinetree 168k / 352). [Vegetation LOD] "Switch sooner" scales the LODGroup
    // screen-height transitions of every prefab vanilla places as VEGETATION (ZoneSystem.m_vegetation), so trees drop to their own,
    // already-authored lower LODs at a shorter distance: 1.0 = vanilla (default), 1.25 = each LOD ends when the tree is 25 % taller on
    // screen than vanilla's (i.e. sooner). Applied to the prefabs AND to the live instances, live on a key change; originals kept per
    // LODGroup, so 1.0 restores them exactly. A look change: Fire judges it on the morning hand list. Info line on each change.
    internal static class FapVegLod
    {
        internal static ConfigEntry<float> SwitchSooner;
        private static readonly Dictionary<int, float[]> s_orig = new Dictionary<int, float[]>();
        private static HashSet<int> s_vegHashes;
        private static float s_applied = 1f;

        internal static void Bind(ConfigFile cfg)
        {
            SwitchSooner = cfg.Bind("Vegetation LOD", "Switch sooner", 1f, new ConfigDescription(
                "Trees and other vegetation switch to their own lower LODs sooner (1.0 = vanilla). 1.25 = each LOD ends 25 % sooner on screen. Fewer triangles, a look change.",
                new AcceptableValueRange<float>(1f, 2f)));
            SwitchSooner.SettingChanged += (_, __) => Apply("key changed");
        }

        // FAP's ZNetScene.Awake postfix (prefabs + vegetation list exist) and every key change.
        internal static void Apply(string why)
        {
            if (SwitchSooner == null || ZNetScene.instance == null || ZoneSystem.instance == null) return;
            float k = SwitchSooner.Value;
            if (Mathf.Approximately(k, s_applied) && s_orig.Count > 0) return;
            if (s_vegHashes == null)
            {
                s_vegHashes = new HashSet<int>();
                foreach (var v in ZoneSystem.instance.m_vegetation) if (v != null && v.m_prefab != null) s_vegHashes.Add(v.m_prefab.name.GetStableHashCode());
            }
            int groups = 0;
            foreach (var p in ZNetScene.instance.m_prefabs)
                if (p != null && s_vegHashes.Contains(p.name.GetStableHashCode())) groups += ScaleAll(p, k);
            var inst = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances")(ZNetScene.instance);
            if (inst != null)
                foreach (var nv in inst.Values)
                {
                    var zdo = nv != null ? nv.GetZDO() : null;
                    if (zdo != null && s_vegHashes.Contains(zdo.GetPrefab())) groups += ScaleAll(nv.gameObject, k);
                }
            s_applied = k;
            if (s_orig.Count > 20000) s_orig.Clear();   // destroyed instances' ids; the prefabs re-capture on the next change
            VerdantsAscent.VaPieces.Log?.LogInfo($"[VegetationLOD] switch sooner x{k:0.00} ({(Mathf.Approximately(k, 1f) ? "vanilla" : "lower LODs sooner")}) on {groups} LODGroup(s) of {s_vegHashes.Count} vegetation prefab(s) + live instances ({why}).");
        }

        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        private static class ZoneSystem_Start
        {
            private static void Postfix()
            {
                s_vegHashes = null;   // this world's vegetation list
                if (SwitchSooner != null && !Mathf.Approximately(SwitchSooner.Value, 1f)) { s_applied = 1f; Apply("world start"); }
            }
        }

        private static int ScaleAll(GameObject go, float k)
        {
            int n = 0;
            foreach (var lg in go.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                int id = lg.GetInstanceID();
                if (!s_orig.TryGetValue(id, out var orig))
                {
                    orig = new float[lods.Length];
                    for (int i = 0; i < lods.Length; i++) orig[i] = lods[i].screenRelativeTransitionHeight;
                    s_orig[id] = orig;
                }
                if (orig.Length != lods.Length) continue;
                float prev = 1f;
                for (int i = 0; i < lods.Length; i++)
                {
                    float t = Mathf.Min(orig[i] * k, 0.999f);
                    if (t >= prev) t = prev * 0.999f;   // Unity needs strictly decreasing transitions
                    lods[i].screenRelativeTransitionHeight = t;
                    prev = t;
                }
                lg.SetLODs(lods);
                n++;
            }
            return n;
        }
    }
}
