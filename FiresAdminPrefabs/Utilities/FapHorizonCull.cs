using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Utilities
{
    // 1.1.405 (Fire 11-08: "reduce the triangle count while not making the game look worse ... better mesh culling"; [visual] leads
    // Tools\DESIGN_OcclusionLOD_20261108.md, FAP's part is Tools\DESIGN_OcclusionLOD_FAT_20261108.md section A, moved into FAP so it
    // runs with FAT parked). TERRAIN-HORIZON CULL: loaded objects that sit wholly behind a hill or ridge from the camera are not drawn.
    //
    // Horizon profile, rebuilt when the camera's 32 m XZ cell or 8 m height band changes, on a worker:
    //   512 azimuth sectors; along each, ground samples from 48 m to Reach (8 m steps growing 5 % a step); per sector the running
    //   maximum elevation angle of the ground, kept at 64 distance stations (geometric 48 m .. Reach).
    //   Heights: the loaded zones' own Heightmap data (copied on the main thread first: it includes player terrain edits), else
    //   vanilla's WorldGenerator.GetHeight (worker-safe: vanilla's HeightmapBuilder thread calls it).
    // CONSERVATIVE by construction (a false cull is a missing object; a missed cull only costs triangles):
    //   occluder ground lowered OccluderSink (2 m); ground within 48 m of the eye ignored (the eye moves up to 16 m before a rebuild);
    //   eye at the TOP of its 8 m band; an object counts as hidden only when the top corners + centre of its bounds (+ TargetMargin)
    //   are all below the horizon, on two checks in a row; it is shown again at once.
    // Applying: over loaded ZNetView objects with MeshRenderers, as Low-priority steps on Core's FrameBudgetScheduler (1.1.406). Hidden = ShadowsOnly (its
    // main-pass triangles go; its shadow stays, so ridge-line shadows stay right); with "Cull shadows too" = forceRenderingOff.
    // Moving things (Character, Rigidbody) are never touched by the horizon cull. Key off = every changed renderer restored.
    //
    // 1.1.409 (Fire 11-08 target: 100+ fps at 615 y53, draws <= ~3.8k, 0 hitches, alloc <= ~1 MB/s; [lead]: FAP owns the NON-EBM side of the
    // per-cascade shadow caster cull, [visual]'s indirect path owns EBM groups; plan Tools\PLAN_NonEbmShadowCull_saddles_20261108.md):
    //   SHADOW CASTER CULL, in the SAME sliced pass and Entry table (one walk over loaded objects, no second scanner):
    //   a) vanilla method first: the sun's Light.layerShadowCullDistances (Unity's own per-layer shadow cull distance; no per-object cost),
    //      from the "Layer shadow distances" key (layer names resolved at runtime, unknown names logged and skipped; a layer some other
    //      system already set is left alone); the light's own array is put back when the key goes off.
    //   b) size-aware per-renderer cut: a caster whose bounds are smaller than "Texel factor" x the shadow-map texel of the cascade
    //      that covers it gets shadowCastingMode Off (its main pass is untouched). Texel = the cascade's slice cross-section diagonal
    //      (Unity's cascade sphere is at least that big, so this texel is never larger than the real one: conservative) / the
    //      per-cascade shadow-map size. Far small casters only ever land in c2/c3, so this is in effect a per-cascade cut.
    //      10 % distance hysteresis before a cut caster gets its shadow back.
    //   Never touched: EBM's baked pieces (EbmBakedPieces.Draws, [visual] 11-08) and renderers EBM switched off; the local player;
    //   shadow-only renderers (proxies); renderers another system re-moded since our last set (we adopt its value as the original).
    //   Only shadowCastingMode is changed by the new code, never forceRenderingOff ([ghost]'s coverage field).
    //   Moving things (creatures, carts, ships) take part in the shadow cut ("Include moving"), with bounds read fresh each check.
    // Allocation (1.1.409): the pass walks a reused list (no per-pass copy), pruning reuses its sets; steady state allocates only the
    // pass's iterator (one small object a sweep). The 30 s Debug line reports the cull's own allocated bytes when the runtime has
    // GC.GetAllocatedBytesForCurrentThread.
    internal static class FapHorizonCull
    {
        private const string S = "Horizon Cull", SC = "Shadow Caster Cull";
        internal static ConfigEntry<bool> Enable, CullShadows;
        internal static ConfigEntry<float> Reach;
        internal static ConfigEntry<bool> ShadowEnable, ShadowMoving;
        internal static ConfigEntry<float> TexelFactor;
        internal static ConfigEntry<int> ShadowMapSize, StepObjects;
        internal static ConfigEntry<string> LayerDistances;

        private const int Sectors = 512, Stations = 64;
        private const float CellXZ = 32f, BandY = 8f, NearIgnore = 48f, OccluderSink = 2f, TargetMargin = 2f;

        internal static void Bind(ConfigFile cfg)
        {
            Enable = cfg.Bind(S, "Enable", false, "Objects wholly behind a hill or ridge from the camera are not drawn (terrain-horizon occlusion; conservative: never hides anything the ground does not hide). Default off until the A/B passes.");
            CullShadows = cfg.Bind(S, "Cull shadows too", false, "Off: a hidden object still casts its shadow (ridge-line shadows stay right; only its main-pass triangles go). On: it is not drawn at all.");
            Reach = cfg.Bind(S, "Reach", 3000f, new ConfigDescription("How far the horizon is followed (m).", new AcceptableValueRange<float>(500f, 6000f)));
            ShadowEnable = cfg.Bind(SC, "Enable", false, "Far casters too small to show in their shadow cascade stop casting a shadow (the object itself is still drawn). EBM's baked pieces are left to EBM. Default off until the A/B passes.");
            TexelFactor = cfg.Bind(SC, "Texel factor", 2f, new ConfigDescription("A caster smaller than this many shadow-map texels of its cascade loses its shadow. Higher = more casters cut.", new AcceptableValueRange<float>(0.5f, 8f)));
            ShadowMapSize = cfg.Bind(SC, "Shadow map size", 0, new ConfigDescription("The sun's shadow atlas size in pixels (0 = from the light / quality settings).", new AcceptableValueRange<int>(0, 16384)));
            ShadowMoving = cfg.Bind(SC, "Include moving", true, "Creatures, carts and ships take part in the size cut (their bounds are read fresh each check).");
            LayerDistances = cfg.Bind(SC, "Layer shadow distances", "", "Unity's per-layer shadow cull distance on the sun, as layer:metres pairs, e.g. item:60,Default_small:120. Empty = none. Unknown layer names are logged and skipped.");
            StepObjects = cfg.Bind(SC, "Objects per step", 48, new ConfigDescription("Objects checked per frame-budget step (horizon + shadow pass).", new AcceptableValueRange<int>(8, 256)));
            if (FiresCore.Lifecycle.FiresMod.IsDedicatedServer || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var go = new GameObject("FAP_HorizonCull") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Driver>();
        }

        // ---------------------------------------------------------------- profile (worker)
        private sealed class Profile
        {
            public Vector3 Eye;
            public float[] Angle = new float[Sectors * Stations];   // max ground elevation angle (rad) of ground strictly nearer than the station
            public float[] StationD = new float[Stations];
            public float Reach;
        }

        private sealed class ZoneGrid { public float X0, Z0, Scale; public int W; public float[] H; }

        private static volatile Profile s_profile;
        private static volatile bool s_building;
        private static Vector3Int s_builtKey = new Vector3Int(int.MinValue, 0, 0);
        private static int s_rebuilds; private static long s_workerTicks; private static double s_workerWorst;

        private static readonly AccessTools.FieldRef<Heightmap, List<float>> HeightsRef = AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");

        private static Dictionary<long, ZoneGrid> SnapshotZones()
        {
            var d = new Dictionary<long, ZoneGrid>();
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || hm.IsDistantLod) continue;
                List<float> h;
                try { h = HeightsRef(hm); } catch { continue; }
                int n = hm.m_width + 1;
                if (h == null || h.Count != n * n) continue;
                Vector3 c = hm.transform.position;
                var g = new ZoneGrid { Scale = hm.m_scale, W = hm.m_width, X0 = c.x - hm.m_width * hm.m_scale * 0.5f, Z0 = c.z - hm.m_width * hm.m_scale * 0.5f, H = h.ToArray() };
                for (int i = 0; i < g.H.Length; i++) g.H[i] += c.y;
                var z = ZoneSystem.GetZone(c);
                d[((long)z.x << 32) | (uint)z.y] = g;
            }
            return d;
        }

        private static float Ground(Dictionary<long, ZoneGrid> zones, WorldGenerator wg, float x, float z)
        {
            var zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
            if (zones.TryGetValue(((long)zone.x << 32) | (uint)zone.y, out var g))
            {
                float fx = (x - g.X0) / g.Scale, fz = (z - g.Z0) / g.Scale;
                int ix = Mathf.Clamp(Mathf.FloorToInt(fx), 0, g.W - 1), iz = Mathf.Clamp(Mathf.FloorToInt(fz), 0, g.W - 1);
                int n = g.W + 1;
                float a = g.H[iz * n + ix], b = g.H[iz * n + ix + 1], c = g.H[(iz + 1) * n + ix], e = g.H[(iz + 1) * n + ix + 1];
                // the lowest of the cell's four corners: never higher than the drawn ground anywhere in the cell (conservative)
                return Mathf.Min(Mathf.Min(a, b), Mathf.Min(c, e));
            }
            return wg.GetHeight(x, z);
        }

        private static void Build(Vector3 eye, float reach, Dictionary<long, ZoneGrid> zones, WorldGenerator wg)
        {
            long t0 = Stopwatch.GetTimestamp();
            var p = new Profile { Eye = eye, Reach = reach };
            for (int j = 0; j < Stations; j++) p.StationD[j] = NearIgnore * Mathf.Pow(reach / NearIgnore, j / (float)(Stations - 1));
            for (int s = 0; s < Sectors; s++)
            {
                float th = (s + 0.5f) / Sectors * Mathf.PI * 2f;
                float dx = Mathf.Cos(th), dz = Mathf.Sin(th);
                float max = -Mathf.PI * 0.5f;
                int j = 0;
                float d = NearIgnore;
                while (d <= reach && j < Stations)
                {
                    while (j < Stations && p.StationD[j] <= d) { p.Angle[s * Stations + j] = max; j++; }   // stations at or before d see occluders strictly nearer
                    float h = Ground(zones, wg, eye.x + dx * d, eye.z + dz * d) - OccluderSink;
                    float a = Mathf.Atan2(h - eye.y, d);
                    if (a > max) max = a;
                    d += Mathf.Max(8f, d * 0.05f);
                }
                for (; j < Stations; j++) p.Angle[s * Stations + j] = max;
            }
            s_profile = p;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Interlocked.Increment(ref s_rebuilds);
            Interlocked.Add(ref s_workerTicks, Stopwatch.GetTimestamp() - t0);
            if (ms > s_workerWorst) s_workerWorst = ms;
        }

        // True when the point is below the horizon (hidden by ground nearer than it).
        private static bool Below(Profile p, Vector3 pt)
        {
            float dx = pt.x - p.Eye.x, dz = pt.z - p.Eye.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d <= NearIgnore + 8f || d >= p.Reach) return false;
            float th = Mathf.Atan2(dz, dx); if (th < 0f) th += Mathf.PI * 2f;
            int s = Mathf.Clamp((int)(th / (Mathf.PI * 2f) * Sectors), 0, Sectors - 1);
            // the last station at or before 0.97 d: only ground clearly nearer than the target counts
            int j = 0; float lim = d * 0.97f;
            while (j + 1 < Stations && p.StationD[j + 1] <= lim) j++;
            float horizon = p.Angle[s * Stations + j];
            return Mathf.Atan2(pt.y + TargetMargin - p.Eye.y, d) < horizon;
        }

        private static bool Hidden(Profile p, Bounds b)
        {
            float top = b.max.y;
            return Below(p, new Vector3(b.center.x, top, b.center.z))
                && Below(p, new Vector3(b.min.x, top, b.min.z)) && Below(p, new Vector3(b.max.x, top, b.min.z))
                && Below(p, new Vector3(b.min.x, top, b.max.z)) && Below(p, new Vector3(b.max.x, top, b.max.z));
        }

        // ---------------------------------------------------------------- shadow cascades (main thread, read once per pass)
        private sealed class Cascades
        {
            public int Count; public float[] Far = new float[4]; public float[] Texel = new float[4]; public float Distance; public int MapPerCascade;
        }
        private static readonly Cascades s_casc = new Cascades();

        private static void ReadCascades(Camera cam, Light sun)
        {
            var c = s_casc;
            c.Distance = Mathf.Min(QualitySettings.shadowDistance, cam.farClipPlane);
            c.Count = Mathf.Clamp(QualitySettings.shadowCascades, 1, 4);
            if (c.Count >= 4) { var v = QualitySettings.shadowCascade4Split; c.Far[0] = v.x; c.Far[1] = v.y; c.Far[2] = v.z; c.Far[3] = 1f; c.Count = 4; }
            else if (c.Count >= 2) { c.Far[0] = QualitySettings.shadowCascade2Split; c.Far[1] = 1f; c.Count = 2; }
            else c.Far[0] = 1f;
            int atlas = ShadowMapSize.Value;
            if (atlas <= 0 && sun != null && sun.shadowCustomResolution > 0) atlas = sun.shadowCustomResolution;
            if (atlas <= 0)
            {
                // larger than Unity's documented directional sizes on purpose: a bigger map = smaller texel = fewer cuts (conservative)
                var q = sun != null && sun.shadowResolution != LightShadowResolution.FromQualitySettings ? (int)sun.shadowResolution : (int)QualitySettings.shadowResolution;
                atlas = q <= 0 ? 1024 : q == 1 ? 2048 : 4096;
            }
            c.MapPerCascade = c.Count > 1 ? atlas / 2 : atlas;
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), diagK = 2f * tanV * Mathf.Sqrt(1f + cam.aspect * cam.aspect);
            float near = 0f;
            for (int i = 0; i < c.Count; i++)
            {
                float far = c.Far[i] * c.Distance;
                c.Far[i] = far;
                float diam = Mathf.Max(far - near, far * diagK);   // the slice's far cross-section diagonal: Unity's cascade sphere is at least this wide
                c.Texel[i] = diam / Mathf.Max(64, c.MapPerCascade);
                near = far;
            }
        }

        // texel size (m) of the cascade covering distance d; 0 = beyond the shadow distance (Unity draws no shadow there anyway)
        private static float TexelAt(float d)
        {
            var c = s_casc;
            for (int i = 0; i < c.Count; i++) if (d <= c.Far[i]) return c.Texel[i];
            return 0f;
        }

        // ---------------------------------------------------------------- sun layer shadow cull distances (vanilla Unity method)
        private static Light s_layerLight; private static float[] s_layerOrig; private static string s_layerApplied;

        private static void ApplyLayerDistances(Light sun, string spec)
        {
            if (sun == s_layerLight && spec == s_layerApplied) return;
            RestoreLayerDistances();
            if (sun == null || string.IsNullOrEmpty(spec)) { s_layerApplied = spec; return; }
            var orig = sun.layerShadowCullDistances;
            var arr = new float[32];
            if (orig != null && orig.Length == 32) Array.Copy(orig, arr, 32);
            var set = new List<string>(); var skipped = new List<string>();
            foreach (var part in spec.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) { if (part.Trim().Length > 0) skipped.Add(part.Trim() + " (not layer:metres)"); continue; }
                string name = kv[0].Trim();
                int layer = LayerMask.NameToLayer(name);
                if (layer < 0) { skipped.Add(name + " (no such layer)"); continue; }
                if (!float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) || m <= 0f) { skipped.Add(name + " (bad metres)"); continue; }
                if (arr[layer] > 0f) { skipped.Add($"{name} (already {arr[layer]:0} m from another system: left)"); continue; }
                arr[layer] = m; set.Add($"{name}={m:0} m");
            }
            s_layerOrig = orig != null && orig.Length == 32 ? (float[])orig.Clone() : null;
            sun.layerShadowCullDistances = arr;
            s_layerLight = sun; s_layerApplied = spec;
            VerdantsAscent.VaPieces.Log?.LogInfo($"[ShadowCull] sun layer shadow distances: {(set.Count > 0 ? string.Join(", ", set) : "none set")}{(skipped.Count > 0 ? "; skipped: " + string.Join(", ", skipped) : "")}");
        }

        private static void RestoreLayerDistances()
        {
            if (s_layerLight != null) { s_layerLight.layerShadowCullDistances = s_layerOrig ?? new float[32]; VerdantsAscent.VaPieces.Log?.LogInfo("[ShadowCull] sun layer shadow distances put back."); }
            s_layerLight = null; s_layerOrig = null; s_layerApplied = null;
        }

        // ---------------------------------------------------------------- apply (main thread, sliced)
        private sealed class Entry
        {
            public Renderer[] Rends; public ShadowCastingMode[] Orig, Set; public Bounds B; public int Tris; public float Size;
            public bool Moving, Baked, Hidden, LastHidden, Cut, ForcedOff;
        }

        private static readonly Dictionary<int, Entry> s_entries = new Dictionary<int, Entry>();
        private static readonly List<ZNetView> s_list = new List<ZNetView>(), s_passList = new List<ZNetView>();
        private static readonly HashSet<int> s_live = new HashSet<int>();
        private static readonly List<int> s_dead = new List<int>();
        private static readonly List<Renderer> s_rendScratch = new List<Renderer>();
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> InstancesRef = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        private static Func<long> s_allocBytes;
        private static bool s_allocBound;
        private static long AllocNow()
        {
            if (!s_allocBound)
            {
                s_allocBound = true;
                try { var m = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes); if (m != null) s_allocBytes = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), m); } catch { s_allocBytes = null; }
            }
            return s_allocBytes != null ? s_allocBytes() : -1;
        }

        private sealed class Driver : MonoBehaviour
        {
            private float _nextList, _nextLog; private bool _wasHorizon, _wasShadow, _saidOn, _saidShadow;
            private double _mainWorst, _mainSum; private int _frames; private long _allocSum;

            private void Update()
            {
                bool world = ZNetScene.instance != null && WorldGenerator.instance != null;
                bool horizon = world && Enable != null && Enable.Value;
                bool shadow = world && ShadowEnable != null && ShadowEnable.Value;
                if (!horizon && _wasHorizon) RestoreFlag(false);
                if (!shadow && _wasShadow) { RestoreFlag(true); RestoreLayerDistances(); }
                _wasHorizon = horizon; _wasShadow = shadow;
                if (!horizon && !shadow) { if (s_entries.Count > 0) { s_entries.Clear(); } return; }
                var cam = Utils.GetMainCamera();
                if (cam == null) return;
                long a0 = AllocNow();
                long t0 = Stopwatch.GetTimestamp();
                Vector3 c = cam.transform.position;
                if (horizon)
                {
                    var key = new Vector3Int(Mathf.FloorToInt(c.x / CellXZ), Mathf.FloorToInt(c.y / BandY), Mathf.FloorToInt(c.z / CellXZ));
                    if (!s_building && key != s_builtKey)
                    {
                        s_builtKey = key;
                        var eye = new Vector3((key.x + 0.5f) * CellXZ, (key.y + 1f) * BandY, (key.z + 0.5f) * CellXZ);   // band top
                        var zones = SnapshotZones(); var wg = WorldGenerator.instance; float reach = Reach.Value;
                        s_building = true;
                        ThreadPool.QueueUserWorkItem(_ => { try { Build(eye, reach, zones, wg); } catch (Exception e) { VerdantsAscent.VaPieces.Log?.LogDebug($"[HorizonCull] build threw: {e.Message}"); } finally { s_building = false; } });
                    }
                    var p = s_profile;
                    if (p != null && !_saidOn) { _saidOn = true; VerdantsAscent.VaPieces.Log?.LogInfo($"[HorizonCull] on: {Sectors} sectors x {Stations} stations to {p.Reach:0} m; heights: loaded zones' Heightmaps (edits included), else WorldGenerator; hidden objects {(CullShadows.Value ? "not drawn" : "shadow only")}."); }
                }
                var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
                if (shadow)
                {
                    ApplyLayerDistances(sun, LayerDistances.Value);
                    if (!_saidShadow && !s_passRunning) { ReadCascades(cam, sun); _saidShadow = true; LogCascades(); }
                }
                bool ready = shadow || s_profile != null;
                if (ready)
                {
                    if (Time.unscaledTime >= _nextList) { _nextList = Time.unscaledTime + 2f; RefreshList(); }
                    // 1.1.406 (Fire: reuse our methods; slicing = Core's ONE frame-budget queue): each check pass is a Low-priority coroutine
                    // on FiresCore.Async.FrameBudgetScheduler, yielding every "Objects per step" objects, so it shares the one per-frame budget.
                    // 1.1.409: the pass walks s_passList, refilled in place from s_list at the pass start (no per-pass list copy).
                    if (!s_passRunning && s_list.Count > 0)
                    {
                        s_passRunning = true;
                        s_passList.Clear(); s_passList.AddRange(s_list);
                        if (shadow) ReadCascades(cam, sun);
                        FiresCore.Async.FrameBudgetScheduler.ScheduleCoroutine(FiresCore.Async.WorkPriority.Low, CheckPass(), s_onPassDone);
                    }
                }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _mainSum += ms; _frames++; if (ms > _mainWorst) _mainWorst = ms;
                if (a0 >= 0) _allocSum += AllocNow() - a0;
                if (Time.unscaledTime >= _nextLog)
                {
                    _nextLog = Time.unscaledTime + 30f;
                    int hiddenNow = 0, hiddenTris = 0, cutNow = 0, cutTris = 0, cutMoving = 0;
                    foreach (var e in s_entries.Values)
                    {
                        if (e == null || e.Rends == null) continue;
                        if (e.Hidden) { hiddenNow++; hiddenTris += e.Tris; }
                        if (e.Cut) { cutNow++; cutTris += e.Tris; if (e.Moving) cutMoving++; }
                    }
                    double wms = s_rebuilds > 0 ? s_workerTicks * 1000.0 / Stopwatch.Frequency / s_rebuilds : 0;
                    long alloc = s_allocBound && s_allocBytes != null ? _allocSum + s_passAlloc : -1;
                    VerdantsAscent.VaPieces.Log?.LogDebug($"[HorizonCull] 30 s: {s_list.Count} object(s) tracked, {s_passChecked} check(s) (Core frame-budget steps: {s_steps}, avg {(s_steps > 0 ? s_stepSum / s_steps : 0):0.000} ms, worst {s_stepWorst:0.00}); horizon {(horizon ? "on" : "off")}: {hiddenNow} hidden now ({hiddenTris} tris off the main pass); shadow cut {(shadow ? "on" : "off")}: {cutNow} caster(s) cut now ({cutTris} tris off the shadow map, {cutMoving} moving); profile rebuilds {s_rebuilds} (worker {wms:0.0} ms avg, worst {s_workerWorst:0.0}); main {(_frames > 0 ? _mainSum / _frames : 0):0.000} ms/frame avg, worst {_mainWorst:0.00}; allocated {(alloc >= 0 ? (alloc / 1024.0).ToString("0.0") + " KB in 30 s" : "n/a (no per-thread counter)")}");
                    s_passChecked = 0; s_steps = 0; s_stepSum = 0; s_stepWorst = 0; _mainSum = 0; _frames = 0; _mainWorst = 0; s_workerWorst = 0; _allocSum = 0; s_passAlloc = 0; Interlocked.Exchange(ref s_rebuilds, 0); Interlocked.Exchange(ref s_workerTicks, 0);
                }
            }

            private static void LogCascades()
            {
                var c = s_casc;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < c.Count; i++) sb.Append($"{(i > 0 ? ", " : "")}c{i} to {c.Far[i]:0} m texel {c.Texel[i]:0.00} m");
                VerdantsAscent.VaPieces.Log?.LogInfo($"[ShadowCull] on: {c.Count} cascade(s) over {c.Distance:0} m, {c.MapPerCascade} px each ({sb}); casters under {TexelFactor.Value:0.0} texels of their cascade stop casting; EBM-baked pieces left to EBM.");
            }

            private static bool s_passRunning;
            private static readonly Action s_onPassDone = () => s_passRunning = false;
            private static int s_passChecked; private static double s_stepWorst, s_stepSum; private static int s_steps; private static long s_passAlloc;

            private static System.Collections.IEnumerator CheckPass()
            {
                long a0 = AllocNow();
                long t0 = Stopwatch.GetTimestamp(); int n = 0;
                int step = StepObjects.Value;
                for (int i = 0; i < s_passList.Count; i++)
                {
                    bool horizon = Enable != null && Enable.Value, shadow = ShadowEnable != null && ShadowEnable.Value;
                    if (!horizon && !shadow) yield break;
                    var nv = s_passList[i];
                    if (nv != null) Check(nv, horizon ? s_profile : null, shadow);
                    s_passChecked++;
                    if (++n % step == 0)
                    {
                        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        s_stepSum += ms; s_steps++; if (ms > s_stepWorst) s_stepWorst = ms;
                        if (a0 >= 0) s_passAlloc += AllocNow() - a0;
                        yield return null;
                        a0 = AllocNow();
                        t0 = Stopwatch.GetTimestamp();
                    }
                }
                if (a0 >= 0) s_passAlloc += AllocNow() - a0;
            }

            private static void RefreshList()
            {
                s_list.Clear();
                Dictionary<ZDO, ZNetView> inst;
                try { inst = InstancesRef(ZNetScene.instance); } catch { return; }
                if (inst == null) return;
                foreach (var nv in inst.Values) if (nv != null) s_list.Add(nv);
                // destroyed objects leave the table (their renderers are gone with them); 1.1.409: reused sets, no allocation
                if (s_entries.Count > s_list.Count + 256)
                {
                    s_live.Clear(); s_dead.Clear();
                    for (int i = 0; i < s_list.Count; i++) s_live.Add(s_list[i].GetInstanceID());
                    foreach (var k in s_entries.Keys) if (!s_live.Contains(k)) s_dead.Add(k);
                    for (int i = 0; i < s_dead.Count; i++) s_entries.Remove(s_dead[i]);
                }
            }

            private static void Check(ZNetView nv, Profile p, bool shadow)
            {
                int id = nv.GetInstanceID();
                if (!s_entries.TryGetValue(id, out var e) || (e != null && e.Baked && !FapAwakeDispatch.EbmDraws(nv.gameObject)))
                {
                    e = Make(nv);   // new, or EBM handed a baked piece back (its renderers are live now)
                    s_entries[id] = e;
                }
                if (e == null || e.Rends == null) return;
                if (!e.Moving && FapAwakeDispatch.EbmDraws(nv.gameObject))
                {
                    // EBM baked it after we met it: put back what we changed and leave it to EBM until it hands it back
                    Restore(e); e.Rends = null; e.Baked = true; return;
                }
                if (e.Moving)
                {
                    bool any = false; Bounds b = default;
                    for (int i = 0; i < e.Rends.Length; i++) { var r = e.Rends[i]; if (r == null) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                    if (!any) return;
                    e.B = b; var sz = b.size; e.Size = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
                }
                // horizon: two checks agree before hiding, shown again at once
                bool hide = false;
                if (p != null && !e.Moving)
                {
                    bool hidden = Hidden(p, e.B);
                    hide = hidden && e.LastHidden;
                    e.LastHidden = hidden;
                }
                else e.LastHidden = false;
                // shadow size cut, with 10 % distance hysteresis before a cut caster gets its shadow back
                bool cut = false;
                if (shadow)
                {
                    var cam = Utils.GetMainCamera();
                    if (cam != null)
                    {
                        float d = Vector3.Distance(cam.transform.position, e.B.center);
                        float k = TexelFactor.Value;
                        float tx = TexelAt(d);
                        cut = tx > 0f && e.Size < k * tx;
                        if (e.Cut && !cut) { float tn = TexelAt(d * 0.9f); cut = tn > 0f && e.Size < k * tn; }   // still cut until clearly in a finer cascade
                    }
                }
                if (hide == e.Hidden && cut == e.Cut) return;
                e.Hidden = hide; e.Cut = cut;
                Apply(e);
            }

            // One place decides each renderer's state from the two flags. Shadow cut: shadowCastingMode only.
            private static void Apply(Entry e)
            {
                bool forceOff = e.Hidden && CullShadows.Value;   // the 1.1.405 "Cull shadows too" option (forceRenderingOff), unchanged
                for (int i = 0; i < e.Rends.Length; i++)
                {
                    var r = e.Rends[i];
                    if (r == null) continue;
                    if (r.shadowCastingMode != e.Set[i]) e.Orig[i] = r.shadowCastingMode;   // another system re-moded it: that is the original now
                    var o = e.Orig[i];
                    ShadowCastingMode want;
                    if (e.Hidden && !forceOff) want = o == ShadowCastingMode.Off ? ShadowCastingMode.Off : ShadowCastingMode.ShadowsOnly;
                    else if (e.Cut && (o == ShadowCastingMode.On || o == ShadowCastingMode.TwoSided)) want = ShadowCastingMode.Off;
                    else want = o;
                    if (r.shadowCastingMode != want) r.shadowCastingMode = want;
                    e.Set[i] = want;
                    if (forceOff != e.ForcedOff) r.forceRenderingOff = forceOff;
                }
                e.ForcedOff = forceOff;
            }

            private static void Restore(Entry e)
            {
                if (e == null || e.Rends == null) return;
                bool was = e.Hidden || e.Cut || e.ForcedOff;
                e.Hidden = false; e.Cut = false; e.LastHidden = false;
                if (!was) return;
                for (int i = 0; i < e.Rends.Length; i++)
                {
                    var r = e.Rends[i];
                    if (r == null) continue;
                    if (r.shadowCastingMode == e.Set[i]) r.shadowCastingMode = e.Orig[i];   // only what we set
                    e.Set[i] = r.shadowCastingMode;
                    if (e.ForcedOff) r.forceRenderingOff = false;
                }
                e.ForcedOff = false;
            }

            private static Entry Make(ZNetView nv)
            {
                var go = nv.gameObject;
                if (FapAwakeDispatch.EbmDraws(go)) return new Entry { Baked = true };   // EBM's: left to EBM until PieceLeftBake
                if (Player.m_localPlayer != null && go == Player.m_localPlayer.gameObject) return new Entry();
                bool moving = go.GetComponent<Character>() != null || go.GetComponentInChildren<Rigidbody>() != null;
                if (moving && !ShadowMoving.Value) return new Entry();   // moving and not taking part: never touched
                // enabled renderers only: one another system has switched off (EBM's baked pieces) is left entirely to it.
                // Static objects: MeshRenderers (the horizon cull's set, as 1.1.405). Moving: MeshRenderers + SkinnedMeshRenderers (shadow cut only).
                s_rendScratch.Clear();
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(false)) if (r.enabled) s_rendScratch.Add(r);
                if (moving) foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(false)) if (r.enabled) s_rendScratch.Add(r);
                if (s_rendScratch.Count == 0) return new Entry();
                var rends = s_rendScratch.ToArray();
                var e = new Entry { Rends = rends, Orig = new ShadowCastingMode[rends.Length], Set = new ShadowCastingMode[rends.Length], Moving = moving };
                bool any = false; Bounds b = default; int tris = 0;
                for (int i = 0; i < rends.Length; i++)
                {
                    e.Orig[i] = rends[i].shadowCastingMode; e.Set[i] = e.Orig[i];
                    if (!any) { b = rends[i].bounds; any = true; } else b.Encapsulate(rends[i].bounds);
                    Mesh m = rends[i] is SkinnedMeshRenderer sk ? sk.sharedMesh : rends[i].GetComponent<MeshFilter>()?.sharedMesh;
                    if (m != null) for (int s = 0; s < m.subMeshCount; s++) tris += (int)(m.GetIndexCount(s) / 3);
                }
                e.B = b; e.Tris = tris; var size = b.size; e.Size = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                return e;
            }

            // a feature went off: put back that feature's changes on every entry at once (a config change, not gameplay)
            private static void RestoreFlag(bool shadowFlag)
            {
                int n = 0;
                foreach (var e in s_entries.Values)
                {
                    if (e == null || e.Rends == null) continue;
                    if (shadowFlag ? !e.Cut : !(e.Hidden || e.ForcedOff)) continue;
                    if (shadowFlag) e.Cut = false; else { e.Hidden = false; e.LastHidden = false; }
                    Apply(e); n++;
                }
                VerdantsAscent.VaPieces.Log?.LogInfo(shadowFlag ? $"[ShadowCull] off: {n} cut caster(s) given their shadow back." : $"[HorizonCull] off: {n} hidden object(s) restored.");
            }
        }
    }
}
