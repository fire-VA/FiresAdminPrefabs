using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.215 (Fire 10-12): light presets for torches, braziers, lanterns, fires and other light props. A preset sets the whole look:
    // the Light (colour, intensity, range, shadows), its flicker (vanilla LightFlicker tuned, or added on a steady light when asked),
    // the flame / particle tint and the emissive material colour, so the visible flame matches the light.
    //   Hammer right-click on a light piece -> the editor presets the NEXT placement (queued, stamped at Piece.SetCreator).
    //   Shift+RMB on a placed light (admins) -> the editor edits THAT light live: the values go in its ZDO, every peer re-applies.
    // Values live in the ZDO (vanilla sync); presets are named templates saved in config\FiresAdminPrefabs\LightPresets.json on the
    // admin's machine. Applied once on load / change, never per frame. Clearing the light (Reset) restores the prefab's own values.
    public sealed class VALightPreset
    {
        public string name = "";
        public string Color = "FFB35C";          // hex RRGGBB
        public float Intensity = 1.5f;
        public float Range = 10f;
        public bool Shadows = true;
        public bool Flicker = true;
        public float FlickerAmount = 0.1f;       // LightFlicker.m_flickerIntensity
        public float FlickerSpeed = 10f;         // LightFlicker.m_flickerSpeed
        public float FlickerMovement = 0.1f;     // LightFlicker.m_movement (how far the light jitters)
        public float LightDistance = 40f;        // LightLod.m_lightDistance (the light stays on up to this far)
        public float ShadowDistance = 20f;       // LightLod.m_shadowDistance
        // 1.1.262 (Fire / Raaka's spotlight): the rest of what vanilla's LightFlicker / LightLod carry. -1 = leave the prefab's value.
        public float FadeDuration = -1f;         // LightFlicker.m_fadeDuration
        public float FadeInDuration = -1f;       // LightFlicker.m_fadeInDuration
        public float Ttl = -1f;                  // LightFlicker.m_ttl (0 = never expires)
        public bool LightLodOn = true;           // LightLod.m_lightLod (off = never switched off by distance)
        public bool ShadowLodOn = true;          // LightLod.m_shadowLod (off = shadows never dropped by distance)
        public string Note = "";                 // shown in the editor (built-ins explain themselves here)
        public bool HasFire;                     // the piece has a Fireplace: the fuel fields below are written
        public bool InfiniteFuel;                // Fireplace.m_infiniteFuel
        public float MaxFuel = 10f;              // Fireplace.m_maxFuel
        public float SecPerFuel = 3f;            // Fireplace.m_secPerFuel (burn rate: seconds per fuel)
        public bool CanTurnOff;                  // Fireplace.m_canTurnOff
        public string FlameTint = "";            // hex, blank = the prefab's own flame colours
        public string Emissive = "";             // hex, blank = the prefab's own glow
    }

    public static class VALightPresetStore
    {
        private static readonly Dictionary<string, VALightPreset> _byName = new Dictionary<string, VALightPreset>(StringComparer.Ordinal);
        private static bool _loaded;

        public static string FilePath => Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "LightPresets.json");
        public static IEnumerable<VALightPreset> All { get { Load(); return _byName.Values; } }
        public static void EnsureLoaded() => Load();   // 1.1.264: called at plugin load, so the built-ins exist from the start

        public static bool TryGet(string name, out VALightPreset preset)
        {
            Load();
            preset = null;
            return !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out preset);
        }

        public static bool Save(VALightPreset p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.name)) return false;
            Load();
            _byName[p.name] = p;
            return Write();
        }

        public static bool Delete(string name)
        {
            Load();
            if (string.IsNullOrEmpty(name) || !_byName.Remove(name)) return false;
            return Write();
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            // 1.1.264 (Round B: fap_light_lod_test "preset not found"): with no LightPresets.json yet, the early return here skipped
            // AddBuiltIns, so a client that never saved a preset had no built-ins at all. The built-ins are now always added.
            int saved = 0;
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonConvert.DeserializeObject<List<VALightPreset>>(File.ReadAllText(FilePath));
                    if (list != null) foreach (var p in list) if (p != null && !string.IsNullOrEmpty(p.name)) { _byName[p.name] = p; saved++; }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: light presets: could not read {FilePath}: {ex.Message}"); }
            AddBuiltIns();
            VaPieces.Log?.LogDebug($"light presets loaded: {saved} saved + built-ins = {_byName.Count} ({(File.Exists(FilePath) ? "file" : "no file yet")})");
        }

        // 1.1.262: built-ins, added in memory when the user has none of that name (a saved one of the same name wins).
        private static void AddBuiltIns()
        {
            var vanilla = new VALightPreset
            {
                name = "Spotlight (vanilla friendly)",
                FlickerAmount = 0f, FlickerSpeed = 0f, FlickerMovement = 0f,   // steady: never dim, never dark
                FadeDuration = 1E+30f, FadeInDuration = 0f, Ttl = 0f,
                LightLodOn = true, ShadowLodOn = true, LightDistance = 200f, ShadowDistance = 200f,
                Note = "Far-visible light and shadows (200 m instead of 40 / 20) that VANILLA clients see too. LOD idea by Raaka. "
                     + "Vanilla can't be made brighter reliably: a frozen flicker (speed 0) sets a random brightness per lantern per load, "
                     + "from dark to about 2.2x, so this preset keeps the light steady.",
            };
            var fap = new VALightPreset
            {
                name = "Spotlight (FAP clients)",
                Intensity = 3f, Range = 30f, Shadows = true, Flicker = false,
                FlickerAmount = 0f, FlickerSpeed = 0f, FlickerMovement = 0f, FadeDuration = 1E+30f, FadeInDuration = 0f, Ttl = 0f,
                LightLodOn = true, ShadowLodOn = true, LightDistance = 200f, ShadowDistance = 200f,
                Note = "Brighter (intensity 3, range 30 m) and far-visible. The brightness and range are NOT visible to vanilla clients: "
                     + "they see the far-visible but normal-brightness light. Needs VanillaFriendly off to edit the FAP-only values.",
            };
            foreach (var b in new[] { vanilla, fap }) if (!_byName.ContainsKey(b.name)) _byName[b.name] = b;
        }

        private static bool Write()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(new List<VALightPreset>(_byName.Values), Formatting.Indented));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: light presets: could not write {FilePath}: {ex.Message}"); return false; }
        }
    }

    // The next placement of a prefab takes the queued preset (kept until changed), as chests / stands / monsters do.
    public static class VALightQueue
    {
        private static readonly Dictionary<string, string> _queued = new Dictionary<string, string>(StringComparer.Ordinal);
        public static void Enqueue(string prefab, string preset) { if (!string.IsNullOrEmpty(prefab) && !string.IsNullOrEmpty(preset)) _queued[prefab] = preset; }
        public static void Clear(string prefab) { if (!string.IsNullOrEmpty(prefab)) _queued.Remove(prefab); }
        public static bool TryGet(string prefab, out string preset) => _queued.TryGetValue(prefab ?? "", out preset);
    }

    public static class VALightApplier
    {
        public const string KeyOn = "va_light", KeyPreset = "va_light_preset", KeyColor = "va_light_col", KeyIntensity = "va_light_int",
            KeyRange = "va_light_rng", KeyShadows = "va_light_shd", KeyFlicker = "va_light_flk", KeyFlickerAmount = "va_light_flka",
            KeyFlickerSpeed = "va_light_flks", KeyFlame = "va_light_flame", KeyEmissive = "va_light_emit";
        private const string RpcRefresh = "FiresAdminPrefabs LightRefresh";

        public static bool HasLight(GameObject go) => go != null && go.GetComponentInChildren<Light>(true) != null;

        public static void Stamp(ZDO zdo, VALightPreset p)
        {
            if (zdo == null || p == null) return;
            zdo.Set(KeyOn, 1);
            zdo.Set(KeyPreset, p.name ?? "");
            zdo.Set(KeyColor, Hex(p.Color));
            zdo.Set(KeyIntensity, Mathf.Max(0f, p.Intensity));
            zdo.Set(KeyRange, Mathf.Max(0.1f, p.Range));
            zdo.Set(KeyShadows, p.Shadows ? 1 : 0);
            zdo.Set(KeyFlicker, p.Flicker ? 1 : 0);
            zdo.Set(KeyFlickerAmount, Mathf.Max(0f, p.FlickerAmount));
            zdo.Set(KeyFlickerSpeed, Mathf.Max(0f, p.FlickerSpeed));
            zdo.Set(KeyFlame, Hex(p.FlameTint));
            zdo.Set(KeyEmissive, Hex(p.Emissive));
            // The vanilla-visible part, the way ZNetView.LoadFields reads it, so a client WITHOUT FAP applies it on load too.
            zdo.Set("HasFields", true);
            zdo.Set("HasFieldsLightFlicker", true);
            zdo.Set("LightFlicker.m_flickerIntensity", Mathf.Max(0f, p.FlickerAmount));
            zdo.Set("LightFlicker.m_flickerSpeed", Mathf.Max(0f, p.FlickerSpeed));
            zdo.Set("LightFlicker.m_movement", Mathf.Max(0f, p.FlickerMovement));
            zdo.Set("HasFieldsLightLod", true);
            zdo.Set("LightLod.m_lightDistance", Mathf.Max(1f, p.LightDistance));
            zdo.Set("LightLod.m_shadowDistance", Mathf.Max(1f, p.ShadowDistance));
            // 1.1.262: the remaining vanilla-carried fields
            zdo.Set("LightLod.m_lightLod", p.LightLodOn);
            zdo.Set("LightLod.m_shadowLod", p.ShadowLodOn);
            if (p.FadeDuration >= 0f) zdo.Set("LightFlicker.m_fadeDuration", p.FadeDuration);
            if (p.FadeInDuration >= 0f) zdo.Set("LightFlicker.m_fadeInDuration", p.FadeInDuration);
            if (p.Ttl >= 0f) zdo.Set("LightFlicker.m_ttl", p.Ttl);
            VaPieces.Log?.LogDebug($"[LightPreset] stamped '{p.name}': flicker {p.FlickerAmount:0.###} speed {p.FlickerSpeed:0.##} movement {p.FlickerMovement:0.###}, "
                + $"fade {(p.FadeDuration >= 0f ? p.FadeDuration.ToString("G3") : "-")} fadeIn {(p.FadeInDuration >= 0f ? p.FadeInDuration.ToString("0.##") : "-")} ttl {(p.Ttl >= 0f ? p.Ttl.ToString("0.##") : "-")}, "
                + $"lightLod {p.LightLodOn} {p.LightDistance:0} m, shadowLod {p.ShadowLodOn} {p.ShadowDistance:0} m, at {zdo.GetPosition():F1}");
            if (p.HasFire)
            {
                zdo.Set("HasFieldsFireplace", true);
                zdo.Set("Fireplace.m_infiniteFuel", p.InfiniteFuel);
                zdo.Set("Fireplace.m_maxFuel", Mathf.Max(1f, p.MaxFuel));
                zdo.Set("Fireplace.m_secPerFuel", Mathf.Max(0.1f, p.SecPerFuel));
                zdo.Set("Fireplace.m_canTurnOff", p.CanTurnOff);
            }
        }

        public static VALightPreset Read(ZDO zdo)
        {
            if (zdo == null || zdo.GetInt(KeyOn) != 1) return null;
            return new VALightPreset
            {
                name = zdo.GetString(KeyPreset), Color = zdo.GetString(KeyColor), Intensity = zdo.GetFloat(KeyIntensity, 1f),
                Range = zdo.GetFloat(KeyRange, 10f), Shadows = zdo.GetInt(KeyShadows, 1) == 1, Flicker = zdo.GetInt(KeyFlicker) == 1,
                FlickerAmount = zdo.GetFloat(KeyFlickerAmount), FlickerSpeed = zdo.GetFloat(KeyFlickerSpeed), FlickerMovement = zdo.GetFloat("LightFlicker.m_movement", 0.1f), LightDistance = zdo.GetFloat("LightLod.m_lightDistance", 40f), ShadowDistance = zdo.GetFloat("LightLod.m_shadowDistance", 20f), HasFire = zdo.GetBool("HasFieldsFireplace"), InfiniteFuel = zdo.GetBool("Fireplace.m_infiniteFuel"), MaxFuel = zdo.GetFloat("Fireplace.m_maxFuel", 10f), SecPerFuel = zdo.GetFloat("Fireplace.m_secPerFuel", 3f), CanTurnOff = zdo.GetBool("Fireplace.m_canTurnOff"), FlameTint = zdo.GetString(KeyFlame),
                Emissive = zdo.GetString(KeyEmissive),
            };
        }

        // Live edit (an admin, Shift+RMB): take the ZDO, write the values (or clear them: preset null), and tell every peer to re-apply.
        // 1.1.216 (the lead 10-12: a client-side admin gate let any FAP client restyle everyone's lights): a client asks the SERVER,
        // which checks the admin list, writes the ZDO and tells every peer to re-apply. A host (IsServer) does it directly.
        public static void SetLive(ZNetView view, VALightPreset p)
        {
            if (view == null || !view.IsValid()) return;
            EnsureRpc();
            var id = view.GetZDO().m_uid;
            if (ZNet.instance != null && ZNet.instance.IsServer()) { WriteAndBroadcast(id, p, "host"); return; }
            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write(p != null ? JsonConvert.SerializeObject(p) : "");
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcSet, pkg);
            VaPieces.Log?.LogDebug($"[FAP] light {(p != null ? $"preset '{p.name}'" : "reset")} for {view.gameObject.name} ({id}) sent to the server");
        }

        private const string RpcSet = "FiresAdminPrefabs LightSet";

        private static void OnSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZDOID id = pkg.ReadZDOID();
            string json = pkg.ReadString();
            bool admin;
            try { admin = FiresCore.Sync.AdminSyncing.IsAdmin(sender); } catch { admin = false; }
            if (!admin) { Debug.LogWarning($"{VaPieces.PluginName}: light edit from non-admin peer {sender} on {id} refused"); return; }
            VALightPreset p = null;
            if (!string.IsNullOrEmpty(json)) { try { p = JsonConvert.DeserializeObject<VALightPreset>(json); } catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: light edit from {sender}: bad preset ({ex.Message})"); return; } }
            WriteAndBroadcast(id, p, $"admin peer {sender}");
        }

        private static void WriteAndBroadcast(ZDOID id, VALightPreset p, string by)
        {
            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            if (zdo == null) { Debug.LogWarning($"{VaPieces.PluginName}: light edit by {by}: no object {id}"); return; }
            zdo.SetOwner(ZDOMan.GetSessionID());
            if (p != null) Stamp(zdo, p); else { zdo.Set(KeyOn, 0); zdo.Set("HasFieldsLightFlicker", false); zdo.Set("HasFieldsLightLod", false); zdo.Set("HasFieldsFireplace", false); }
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcRefresh, id);
            Debug.Log($"{VaPieces.PluginName}: light {(p != null ? $"preset '{p.name}' set" : "reset to vanilla")} on {id} by {by}");
        }

        private static ZRoutedRpc _registeredOn;
        internal static void EnsureRpc()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(_registeredOn, rpc)) return;
            rpc.Register<ZDOID>(RpcRefresh, OnRefresh);
            rpc.Register<ZPackage>(RpcSet, OnSet);
            _registeredOn = rpc;
        }

        // The ZDO data may arrive after the RPC: re-applied now and again shortly after.
        private static void OnRefresh(long sender, ZDOID id)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
            var state = go != null ? go.GetComponent<VALightState>() : null;
            if (state == null) return;
            state.ApplyFromZdo();
            state.StartCoroutine(state.ApplyLater());
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        public static class ZNet_Awake_VALight_Patch { [HarmonyPostfix] public static void Postfix() => EnsureRpc(); }

        // Every placed piece with a Light gets the state that caches its own values and applies the ZDO's.
        [HarmonyPatch(typeof(Piece), "Awake")]
        public static class Piece_Awake_VALight_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Piece __instance)
            {
                if (__instance == null || __instance.GetComponent<VALightState>() != null || !HasLight(__instance.gameObject)) return;
                if (__instance.GetComponent<ZNetView>() == null) return;   // a hammer ghost / menu copy
                __instance.gameObject.AddComponent<VALightState>();
            }
        }

        // A queued preset is stamped on the placing player's new piece.
        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        public static class Piece_SetCreator_VALight_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Piece __instance, long uid)
            {
                if (__instance == null || uid == 0) return;
                string prefab = Utils.GetPrefabName(__instance.gameObject);
                if (!VALightQueue.TryGet(prefab, out string presetName) || !VALightPresetStore.TryGet(presetName, out var p)) return;
                var view = __instance.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) return;
                Stamp(view.GetZDO(), p);
                __instance.GetComponent<VALightState>()?.ApplyFromZdo();
                VaPieces.Log?.LogDebug($"[FAP] light preset '{p.name}' stamped on the new {prefab} ({view.GetZDO().m_uid})");
            }
        }

        internal static string Hex(string s) { s = (s ?? "").Trim().TrimStart('#'); return s.Length == 6 ? s.ToUpperInvariant() : ""; }
        internal static bool TryColor(string hex, out Color c) { c = Color.white; hex = Hex(hex); return hex.Length == 6 && ColorUtility.TryParseHtmlString("#" + hex, out c); }
    }

    // Per placed light piece: the prefab's own values cached once, the ZDO's preset applied on Start and on every refresh.
    public sealed class VALightState : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<LightFlicker, float> BaseIntensity = AccessTools.FieldRefAccess<LightFlicker, float>("m_baseIntensity");
        // 1.1.216 (R-1012T: Reset left the range at 14, not 10): vanilla LightLod caches the light's range in Awake and keeps fading
        // light.range toward it, so the preset sets that cached range too.
        private static readonly AccessTools.FieldRef<LightLod, float> BaseRange = AccessTools.FieldRefAccess<LightLod, float>("m_baseRange");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        private struct LightOrig { public Light L; public Color C; public float I, R; public LightShadows S; }
        private struct FlickerOrig { public LightFlicker F; public float Base, Amount, Speed, Movement, Fade, FadeIn, Ttl; public bool Enabled; }
        private struct LodOrig { public LightLod L; public float Light, Shadow; public bool LodOn, ShadowLodOn; }   // 1.1.262: + the LOD bools
        private struct FireOrig { public Fireplace F; public bool Infinite, TurnOff; public float Max, Sec; }
        private FireOrig _fire;
        private struct ParticleOrig { public ParticleSystem P; public ParticleSystem.MinMaxGradient C; }

        private readonly List<LightOrig> _lights = new List<LightOrig>();
        private readonly List<FlickerOrig> _flickers = new List<FlickerOrig>();
        private readonly List<ParticleOrig> _particles = new List<ParticleOrig>();
        private readonly List<LodOrig> _lods = new List<LodOrig>();
        private readonly List<Renderer> _glow = new List<Renderer>();
        private readonly List<LightFlicker> _added = new List<LightFlicker>();
        private ZNetView _view;
        private bool _cached, _applied;
        private MaterialPropertyBlock _mpb;

        private void Start()
        {
            _view = GetComponent<ZNetView>();
            Cache();
            ApplyFromZdo();
        }

        private void Cache()
        {
            if (_cached) return;
            _cached = true;
            // The originals come from the PREFAB's lights (by order): LightLod may still be fading this copy's range in from 0.
            var prefabForLights = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(gameObject)) : null;
            var pls = prefabForLights != null ? prefabForLights.GetComponentsInChildren<Light>(true) : new Light[0];
            var ls = GetComponentsInChildren<Light>(true);
            for (int i = 0; i < ls.Length; i++)
            {
                var src = i < pls.Length ? pls[i] : ls[i];
                _lights.Add(new LightOrig { L = ls[i], C = src.color, I = src.intensity, R = src.range, S = src.shadows });
            }
            // The vanilla-visible fields may already hold the ZDO's values (ZNetView.LoadFields ran in Awake), so their originals come
            // from the prefab, matched by order.
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(gameObject)) : null;
            var pf = prefab != null ? prefab.GetComponentsInChildren<LightFlicker>(true) : new LightFlicker[0];
            var pl = prefab != null ? prefab.GetComponentsInChildren<LightLod>(true) : new LightLod[0];
            var fl = GetComponentsInChildren<LightFlicker>(true);
            for (int i = 0; i < fl.Length; i++)
            {
                var src = i < pf.Length ? pf[i] : fl[i];
                _flickers.Add(new FlickerOrig { F = fl[i], Base = BaseIntensity(fl[i]), Fade = src.m_fadeDuration, FadeIn = src.m_fadeInDuration, Ttl = src.m_ttl, Amount = src.m_flickerIntensity, Speed = src.m_flickerSpeed, Movement = src.m_movement, Enabled = fl[i].enabled });
            }
            var fp = GetComponent<Fireplace>();
            var pfp = prefab != null ? prefab.GetComponent<Fireplace>() : null;
            if (fp != null) { var s = pfp ?? fp; _fire = new FireOrig { F = fp, Infinite = s.m_infiniteFuel, TurnOff = s.m_canTurnOff, Max = s.m_maxFuel, Sec = s.m_secPerFuel }; }
            var ll = GetComponentsInChildren<LightLod>(true);
            for (int i = 0; i < ll.Length; i++)
            {
                var src = i < pl.Length ? pl[i] : ll[i];
                _lods.Add(new LodOrig { L = ll[i], Light = src.m_lightDistance, Shadow = src.m_shadowDistance, LodOn = src.m_lightLod, ShadowLodOn = src.m_shadowLod });
            }
            foreach (var p in GetComponentsInChildren<ParticleSystem>(true)) _particles.Add(new ParticleOrig { P = p, C = p.main.startColor });
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                foreach (var m in r.sharedMaterials) if (m != null && m.HasProperty(EmissionColor)) { _glow.Add(r); break; }
            }
        }

        internal IEnumerator ApplyLater() { yield return new WaitForSeconds(0.75f); ApplyFromZdo(); yield return new WaitForSeconds(2f); ApplyFromZdo(); }

        public void ApplyFromZdo()
        {
            if (_view == null) _view = GetComponent<ZNetView>();
            if (_view == null || !_view.IsValid()) return;
            Cache();
            var p = VALightApplier.Read(_view.GetZDO());
            if (p == null) { if (_applied) Restore(); return; }
            Apply(p);
        }

        private void Apply(VALightPreset p)
        {
            bool hasColor = VALightApplier.TryColor(p.Color, out Color color);
            foreach (var o in _lights)
            {
                if (o.L == null) continue;
                if (hasColor) o.L.color = color;
                o.L.intensity = p.Intensity;
                o.L.range = p.Range;
                var lod = o.L.GetComponent<LightLod>(); if (lod != null) BaseRange(lod) = p.Range;
                o.L.shadows = p.Shadows ? (o.S != LightShadows.None ? o.S : LightShadows.Soft) : LightShadows.None;
            }
            // Vanilla flicker multiplies its cached base intensity every update, so the base is what the preset sets.
            foreach (var o in _flickers)
            {
                if (o.F == null) continue;
                BaseIntensity(o.F) = p.Intensity;
                o.F.m_flickerIntensity = p.FlickerAmount;
                o.F.m_flickerSpeed = p.FlickerSpeed;
                o.F.m_movement = p.FlickerMovement;
                if (p.FadeDuration >= 0f) o.F.m_fadeDuration = p.FadeDuration;     // 1.1.262
                if (p.FadeInDuration >= 0f) o.F.m_fadeInDuration = p.FadeInDuration;
                if (p.Ttl >= 0f) o.F.m_ttl = p.Ttl;
                o.F.enabled = p.Flicker;
            }
            // Opt-in flicker on a light that has none (steady lanterns, glowing props); removed again when flicker goes off.
            if (p.Flicker)
            {
                foreach (var o in _lights)
                {
                    if (o.L == null || o.L.GetComponent<LightFlicker>() != null) continue;
                    var f = o.L.gameObject.AddComponent<LightFlicker>();   // its Awake caches the intensity just set
                    f.m_flickerIntensity = p.FlickerAmount; f.m_flickerSpeed = p.FlickerSpeed; f.m_movement = 0f;
                    _added.Add(f);
                }
            }
            else RemoveAddedFlicker();
            foreach (var o in _added) if (o != null) { BaseIntensity(o) = p.Intensity; o.m_flickerIntensity = p.FlickerAmount; o.m_flickerSpeed = p.FlickerSpeed; }

            foreach (var o in _lods) if (o.L != null) { o.L.m_lightDistance = p.LightDistance; o.L.m_shadowDistance = p.ShadowDistance; o.L.m_lightLod = p.LightLodOn; o.L.m_shadowLod = p.ShadowLodOn; }
            if (_fire.F != null && p.HasFire) { _fire.F.m_infiniteFuel = p.InfiniteFuel; _fire.F.m_maxFuel = p.MaxFuel; _fire.F.m_secPerFuel = p.SecPerFuel; _fire.F.m_canTurnOff = p.CanTurnOff; }

            bool tint = VALightApplier.TryColor(p.FlameTint, out Color flame);
            foreach (var o in _particles)
            {
                if (o.P == null) continue;
                var main = o.P.main;
                main.startColor = tint ? new ParticleSystem.MinMaxGradient(flame) : o.C;
            }

            bool glow = VALightApplier.TryColor(p.Emissive, out Color emit);
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            foreach (var r in _glow)
            {
                if (r == null) continue;
                if (!glow) { r.SetPropertyBlock(null); continue; }
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionColor, emit * 2f);   // HDR: vanilla flames glow above 1
                r.SetPropertyBlock(_mpb);
            }
            _applied = true;
        }

        private void Restore()
        {
            foreach (var o in _lights) if (o.L != null) { o.L.color = o.C; o.L.intensity = o.I; o.L.range = o.R; o.L.shadows = o.S; var lod = o.L.GetComponent<LightLod>(); if (lod != null) BaseRange(lod) = o.R; }
            foreach (var o in _flickers) if (o.F != null) { BaseIntensity(o.F) = o.Base; o.F.m_fadeDuration = o.Fade; o.F.m_fadeInDuration = o.FadeIn; o.F.m_ttl = o.Ttl; o.F.m_flickerIntensity = o.Amount; o.F.m_flickerSpeed = o.Speed; o.F.m_movement = o.Movement; o.F.enabled = o.Enabled; }
            foreach (var o in _lods) if (o.L != null) { o.L.m_lightDistance = o.Light; o.L.m_shadowDistance = o.Shadow; o.L.m_lightLod = o.LodOn; o.L.m_shadowLod = o.ShadowLodOn; }
            if (_fire.F != null) { _fire.F.m_infiniteFuel = _fire.Infinite; _fire.F.m_maxFuel = _fire.Max; _fire.F.m_secPerFuel = _fire.Sec; _fire.F.m_canTurnOff = _fire.TurnOff; }
            RemoveAddedFlicker();
            foreach (var o in _particles) if (o.P != null) { var main = o.P.main; main.startColor = o.C; }
            foreach (var r in _glow) if (r != null) r.SetPropertyBlock(null);
            _applied = false;
        }

        private void RemoveAddedFlicker()
        {
            foreach (var f in _added) if (f != null) Destroy(f);
            _added.Clear();
            foreach (var o in _lights) if (o.L != null && _applied == false) o.L.intensity = o.I;
        }

        // What the editor starts from: the ZDO's preset, else the piece's own (cached) values.
        public VALightPreset Current()
        {
            Cache();
            var p = _view != null && _view.IsValid() ? VALightApplier.Read(_view.GetZDO()) : null;
            if (p != null) return p;
            var l = _lights.Count > 0 ? _lights[0] : default;
            var f = _flickers.Count > 0 ? _flickers[0] : default;
            return new VALightPreset
            {
                Color = l.L != null ? ColorUtility.ToHtmlStringRGB(l.C) : "FFB35C", Intensity = l.L != null ? l.I : 1.5f, Range = l.L != null ? l.R : 10f,
                Shadows = l.L == null || l.S != LightShadows.None, Flicker = f.F != null && f.Enabled,
                FlickerAmount = f.F != null ? f.Amount : 0.1f, FlickerSpeed = f.F != null ? f.Speed : 10f, FlickerMovement = f.F != null ? f.Movement : 0.1f,
                LightDistance = _lods.Count > 0 ? _lods[0].Light : 40f, ShadowDistance = _lods.Count > 0 ? _lods[0].Shadow : 20f,
                HasFire = _fire.F != null, InfiniteFuel = _fire.Infinite, MaxFuel = _fire.F != null ? _fire.Max : 10f, SecPerFuel = _fire.F != null ? _fire.Sec : 3f, CanTurnOff = _fire.TurnOff,
            };
        }
    }
}
