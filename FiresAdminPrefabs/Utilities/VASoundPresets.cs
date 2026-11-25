using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FiresCore.Sync;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.295 (Fire: Sound Presets): per-prefab sound edits an admin saves from the FAP context menu ("Edit sound preset").
    //
    // Detection (cached per prefab): every GameObject in the prefab holding an AudioSource (with or without ZSFX: ambient loops,
    // Fireplace / Smelter / CookingStation loops), plus every EffectList field on any of its components (Piece.m_placeEffect,
    // WearNTear.m_destroyedEffect / m_hitEffect, Door open / close, Fireplace / Smelter / CookingStation effects ...) whose entry
    // prefab carries an AudioSource / ZSFX.
    //
    // Apply: the prefab template AND every live instance. Hierarchy sources are edited in place. Effect entries point at shared sfx
    // prefabs (sfx_build_hammer_wood serves hundreds of pieces), so an edited entry gets its own clone of the ORIGINAL effect prefab
    // (inactive holder, never registered) and only this prefab's EffectList entry points at it. The vanilla values and the original
    // effect references are kept, so Reset / Delete / leaving the world restores them exactly.
    //
    // Sync: the whole preset set is one JSON custom value on FAP's ConfigSync (the same path as VAFapPlayerPieces' PlayerPieces.yaml):
    // the server's copy is authoritative and reaches every player on join and on every change; an admin client's Save is pushed to the
    // server by ConfigSync, which accepts it only from an admin while the config is locked (FAP's existing server-enforced gate).
    // The server keeps the file in <BepInEx>\cache\FiresAdminPrefabs\sound_presets.json. With no FAP on the server (client-only),
    // the admin's own file is used locally.
    //
    // 1.1.297 (Fire: "it should only be an override for the placed piece that's edited"): the global per-prefab apply above is GONE.
    // No prefab template or shared effect is changed any more. An override lives on the edited piece's ZDO ("fap_sound", see
    // "per placed piece" below); saved presets are reusable templates only, and the server file no longer applies to prefabs.
    public static class VASoundPresets
    {
        // ---- data ----

        [Serializable]   // 1.1.335: so FapPresets can hand it out as JSON (attribute only)
        public sealed class SoundValues
        {
            // AudioSource
            public float Volume = 1f, Pitch = 1f, SpatialBlend = 1f, MinDistance = 1f, MaxDistance = 500f, DopplerLevel = 1f, Spread = 0f;
            public int RolloffMode;        // AudioRolloffMode
            public bool Loop;
            public int Priority = 128;
            // ZSFX (HasZsfx)
            public bool HasZsfx;
            public float MinPitch = 1f, MaxPitch = 1f, MinVol = 1f, MaxVol = 1f;
            public float FadeIn, FadeOut, FadeOutDelay;
            public bool FadeOutOnAwake;
            public bool RandomPan;
            public float MinPan = -1f, MaxPan = 1f;
            public float MinDelay, MaxDelay;
            public int MaxConcurrent;
            public bool IgnoreConcurrencyDistance;
            public bool DistanceReverb = true;
            public bool PlayOnAwake = true;
            public int Clip = -1;          // -1 = every clip (vanilla random pick); else only that clip of the ZSFX's list

            public SoundValues Copy() => (SoundValues)MemberwiseClone();
        }

        internal sealed class Source
        {
            public string Key;              // stable id inside the prefab
            public string Label;            // component path + clip name
            public bool IsEffect;
            public string HostPath;         // effect: path of the component owning the EffectList
            public string CompType;         // effect: the component's type name
            public string Field;            // effect: EffectList field name
            public int Index;               // effect: entry index
            public string SubPath;          // path of the AudioSource GameObject (prefab-relative, or effect-prefab-relative)
            public SoundValues Vanilla;
            public string[] ClipNames = new string[0];
            public GameObject OriginalEffect;   // effect: the shared effect prefab vanilla points at
        }

        private const string SyncedValueIdentifier = "SoundPresets";
        private const string FileName = "sound_presets.json";

        internal static string FilePath => Path.Combine(Path.GetFullPath(VAFapCachePaths.Root), FileName);

        private static CustomSyncedValue<string> _synced;
        private static Dictionary<string, Dictionary<string, SoundValues>> _active = new Dictionary<string, Dictionary<string, SoundValues>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Source>> _detect = new Dictionary<string, List<Source>>(StringComparer.Ordinal);
        private static readonly Dictionary<int, bool> s_soundByPrefab = new Dictionary<int, bool>();   // 1.1.402: prefab hash -> piece with sound (cleared with _detect)
        private static readonly Dictionary<string, GameObject> _clones = new Dictionary<string, GameObject>(StringComparer.Ordinal);   // prefab|effectKey
        private static readonly Dictionary<Type, FieldInfo[]> _effectFields = new Dictionary<Type, FieldInfo[]>();
        private static GameObject _cloneHolder;
        private static string _lastWritten;

        // ---- detection ----

        public static bool HasSound(string prefabName) => Sources(prefabName).Count > 0;

        internal static List<Source> Sources(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return new List<Source>();
            if (_detect.TryGetValue(prefabName, out var cached)) return cached;
            var list = new List<Source>();
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null) return list;   // not cached: the scene may not be up yet
            try { Detect(prefab, list); }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] detection on {prefabName} failed: {ex.Message}"); }
            _detect[prefabName] = list;
            return list;
        }

        private static void Detect(GameObject prefab, List<Source> list)
        {
            var root = prefab.transform;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in prefab.GetComponentsInChildren<AudioSource>(true))
            {
                string path = Unique(PathOf(root, a.transform), seen);
                var z = a.GetComponent<ZSFX>();
                var s = new Source { Key = "h|" + path, SubPath = path, Vanilla = Read(a, z), ClipNames = ClipNames(a, z) };
                s.Label = $"{(path.Length == 0 ? prefab.name : path)}  [{(z != null ? "ZSFX" : "AudioSource")}]  {ClipLabel(s.ClipNames)}";
                if (z != null)   // 1.1.296 (SP4 proof): the vanilla ZSFX ranges as read from the prefab template
                    VaPieces.Log?.LogDebug($"[SoundPresets] vanilla {prefab.name} {path}: pitch {z.m_minPitch:0.###}..{z.m_maxPitch:0.###}, vol {z.m_minVol:0.###}..{z.m_maxVol:0.###}, loop {a.loop}, dist {a.minDistance:0.#}..{a.maxDistance:0.#}");
                list.Add(s);
            }
            foreach (var comp in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;
                string host = PathOf(root, comp.transform);
                foreach (var f in EffectFields(comp.GetType()))
                {
                    if (!(f.GetValue(comp) is EffectList el) || el.m_effectPrefabs == null) continue;
                    for (int i = 0; i < el.m_effectPrefabs.Length; i++)
                    {
                        var fx = el.m_effectPrefabs[i]?.m_prefab;
                        if (fx == null) continue;
                        var subSeen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var a in fx.GetComponentsInChildren<AudioSource>(true))
                        {
                            string sub = Unique(PathOf(fx.transform, a.transform), subSeen);
                            var z = a.GetComponent<ZSFX>();
                            var s = new Source
                            {
                                IsEffect = true, HostPath = host, CompType = comp.GetType().Name, Field = f.Name, Index = i, SubPath = sub,
                                OriginalEffect = fx, Vanilla = Read(a, z), ClipNames = ClipNames(a, z),
                            };
                            s.Key = $"fx|{host}|{s.CompType}.{f.Name}|{i}|{sub}";
                            s.Label = $"{s.CompType}.{f.Name}[{i}] {fx.name}{(sub.Length > 0 ? "/" + sub : "")}  {ClipLabel(s.ClipNames)}";
                            list.Add(s);
                        }
                    }
                }
            }
        }

        private static FieldInfo[] EffectFields(Type t)
        {
            if (_effectFields.TryGetValue(t, out var f)) return f;
            if (BuildTypes.Any(b => b.IsAssignableFrom(t))) { f = new FieldInfo[0]; _effectFields[t] = f; return f; }   // 1.1.299: placement / impact components carry only building sounds
            bool hitOnly = HitOnlyTypes.Any(b => b.IsAssignableFrom(t));   // 1.1.299: WearNTear / Destructible keep only their hit sounds
            f = AccessTools.GetDeclaredFields(t).Where(x => !x.IsStatic && x.FieldType == typeof(EffectList) && IsListedEffect(x.Name)).ToArray();   // 1.1.296: + IsListedEffect
            var all = new List<FieldInfo>(f);
            for (var b = t.BaseType; b != null && b != typeof(MonoBehaviour); b = b.BaseType)
                all.AddRange(AccessTools.GetDeclaredFields(b).Where(x => !x.IsStatic && x.FieldType == typeof(EffectList) && IsListedEffect(x.Name)));   // 1.1.296: + IsListedEffect
            f = (hitOnly ? all.Where(x => x.Name.ToLowerInvariant().Contains("hit")) : all).ToArray();
            _effectFields[t] = f;
            return f;
        }

        // 1.1.296 (Fire: list only the sounds that matter): the hierarchy sources (ambient / loop) plus these effect lists only:
        // the add-fuel sound (Fireplace / Smelter / CookingStation.m_fuelAddedEffects) and a door's use sound (Door open / close).
        // Placement, destroy and hit effects (Piece.m_placeEffect, WearNTear.m_destroyedEffect / m_hitEffect ...) are not listed.
        // 1.1.298 (Fire 10-20: "any piece with an interact sound should also be editable"): every effect list on the piece's own
        // components is listed (doors, chests, stations' craft / repair, smelter ore / produce, fermenter, beehive, item stand, turret,
        // trap, incinerator lever, map table, portal ...), except the build-and-break ones: placement, destroy and hit. Those live on
        // Piece / WearNTear / Destructible / ImpactEffect (skipped whole in EffectFields) or carry place / destroy / hit in the name.

        // 1.1.299 (Fire 10-20): ANY sound on a piece is editable; left out are the one-time placement / destroy sounds and the build
        // components (Piece / ImpactEffect); WearNTear / Destructible list only their hit sounds (Fire: "hit sounds could stay").
        private static readonly HashSet<Type> BuildTypes = new HashSet<Type> { typeof(Piece), typeof(ImpactEffect) };
        private static readonly HashSet<Type> HitOnlyTypes = new HashSet<Type> { typeof(WearNTear), typeof(Destructible) };

        private static bool IsListedEffect(string field)
        {
            string n = field.ToLowerInvariant();
            return !(n.Contains("place") || n.Contains("destroy"));   // 1.1.299 (Fire 10-20: only the one-time placement + destroy sounds are left out; hit, use, loop, ambient all listed)
        }

        private static string Unique(string path, HashSet<string> seen)
        {
            string p = path; int n = 1;
            while (!seen.Add(p)) p = path + "#" + (n++);
            return p;
        }

        internal static string PathOf(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // "#n" suffix = the n-th duplicate name in walk order (same order as Detect).
        private static AudioSource FindSource(Transform root, string sub)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in root.GetComponentsInChildren<AudioSource>(true))
                if (Unique(PathOf(root, a.transform), seen) == sub) return a;
            return null;
        }

        private static string[] ClipNames(AudioSource a, ZSFX z)
        {
            if (z != null && z.m_audioClips != null && z.m_audioClips.Length > 0) return z.m_audioClips.Select(c => c != null ? c.name : "?").ToArray();
            return a.clip != null ? new[] { a.clip.name } : new string[0];
        }

        private static string ClipLabel(string[] clips)
            => clips.Length == 0 ? "(no clip)" : clips.Length == 1 ? clips[0] : $"{clips[0]} +{clips.Length - 1}";

        private static SoundValues Read(AudioSource a, ZSFX z)
        {
            var v = new SoundValues
            {
                Volume = a.volume, Pitch = a.pitch, SpatialBlend = a.spatialBlend, MinDistance = a.minDistance, MaxDistance = a.maxDistance,
                RolloffMode = (int)a.rolloffMode, Loop = a.loop, Priority = a.priority, DopplerLevel = a.dopplerLevel, Spread = a.spread,
                HasZsfx = z != null,
            };
            if (z != null)
            {
                v.MinPitch = z.m_minPitch; v.MaxPitch = z.m_maxPitch; v.MinVol = z.m_minVol; v.MaxVol = z.m_maxVol;
                v.FadeIn = z.m_fadeInDuration; v.FadeOut = z.m_fadeOutDuration; v.FadeOutDelay = z.m_fadeOutDelay; v.FadeOutOnAwake = z.m_fadeOutOnAwake;
                v.RandomPan = z.m_randomPan; v.MinPan = z.m_minPan; v.MaxPan = z.m_maxPan; v.MinDelay = z.m_minDelay; v.MaxDelay = z.m_maxDelay;
                v.MaxConcurrent = z.m_maxConcurrentSources; v.IgnoreConcurrencyDistance = z.m_ignoreConcurrencyDistance;
                v.DistanceReverb = z.m_distanceReverb; v.PlayOnAwake = z.m_playOnAwake;
            }
            return v;
        }

        // vanillaClips: the ZSFX's own clip list (kept on Source via the original), so a clip choice can always be undone.
        private static void Write(AudioSource a, ZSFX z, SoundValues v, AudioClip[] vanillaClips)
        {
            a.volume = v.Volume; a.pitch = v.Pitch; a.spatialBlend = v.SpatialBlend; a.minDistance = v.MinDistance; a.maxDistance = v.MaxDistance;
            a.rolloffMode = (AudioRolloffMode)v.RolloffMode; a.loop = v.Loop; a.priority = v.Priority; a.dopplerLevel = v.DopplerLevel; a.spread = v.Spread;
            if (z == null) return;
            z.m_minPitch = v.MinPitch; z.m_maxPitch = v.MaxPitch; z.m_minVol = v.MinVol; z.m_maxVol = v.MaxVol;
            z.m_fadeInDuration = v.FadeIn; z.m_fadeOutDuration = v.FadeOut; z.m_fadeOutDelay = v.FadeOutDelay; z.m_fadeOutOnAwake = v.FadeOutOnAwake;
            z.m_randomPan = v.RandomPan; z.m_minPan = v.MinPan; z.m_maxPan = v.MaxPan; z.m_minDelay = v.MinDelay; z.m_maxDelay = v.MaxDelay;
            z.m_maxConcurrentSources = v.MaxConcurrent; z.m_ignoreConcurrencyDistance = v.IgnoreConcurrencyDistance;
            z.m_distanceReverb = v.DistanceReverb; z.m_playOnAwake = v.PlayOnAwake;
            if (vanillaClips != null)
                z.m_audioClips = v.Clip >= 0 && v.Clip < vanillaClips.Length ? new[] { vanillaClips[v.Clip] } : vanillaClips;
        }

        private static readonly Dictionary<string, AudioClip[]> _vanillaClips = new Dictionary<string, AudioClip[]>(StringComparer.Ordinal);

        private static AudioClip[] VanillaClips(string prefabName, Source s, ZSFX templateZ)
        {
            string k = prefabName + "|" + s.Key;
            if (_vanillaClips.TryGetValue(k, out var c)) return c;
            if (s.IsEffect)
            {
                var a = s.OriginalEffect != null ? FindSource(s.OriginalEffect.transform, s.SubPath) : null;
                c = a != null ? a.GetComponent<ZSFX>()?.m_audioClips : null;
            }
            else c = templateZ?.m_audioClips;
            if (c != null) _vanillaClips[k] = c;
            return c;
        }
        // ---- templates ----
        // 1.1.297 (Fire: "it should only be an override for the placed piece that's edited"): saved presets are TEMPLATES only
        // (name -> source key -> values). They never touch a prefab; applying one fills the window, and Save writes it to that piece.
        // Same store as 1.1.295/296 (ConfigSync value + server file BepInEx\cache\FiresAdminPrefabs\sound_presets.json); the old
        // per-prefab entries simply show as templates named after the prefab.

        internal static IEnumerable<string> TemplateNames() => _active.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        internal static Dictionary<string, SoundValues> Template(string name)
            => name != null && _active.TryGetValue(name, out var p) ? p : null;

        internal static string NextTemplateName(string prefabName)
        {
            for (int n = 1; ; n++) { string s = $"{prefabName} {n}"; if (!_active.ContainsKey(s)) return s; }
        }

        // Save a template (values == null: delete it). Admin only.
        internal static bool SaveTemplate(string name, Dictionary<string, SoundValues> values, out string message)
        {
            message = null;
            if (!VAVanillaPieceManager.IsAdminGateOpen()) { message = "Admins only."; return false; }
            var next = new Dictionary<string, Dictionary<string, SoundValues>>(_active, StringComparer.Ordinal);
            if (values == null || values.Count == 0) next.Remove(name);
            else next[name] = values.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal);
            string json = JsonConvert.SerializeObject(next, Formatting.Indented);
            bool server = ZNet.instance == null || ZNet.instance.IsServer();
            bool synced = _synced != null && (server || VAFapServerPresence.ServerRunsFap);
            if (!synced) { WriteFile(json); SetActive(json); message = $"Template '{name}' {(values == null ? "deleted" : "saved")} (this PC only)."; }
            else
            {
                _synced.Value = json;
                if (!server) SetActive(json);
                message = $"Template '{name}' {(values == null ? "deleted" : "saved")}.";
            }
            VaPieces.Log?.LogInfo($"[SoundPresets] template {(values == null ? "deleted" : "saved")}: {name} ({values?.Count ?? 0} source(s))");
            return true;
        }

        internal static void AddToConfigSync(ConfigSync configSync)
        {
            _synced = new CustomSyncedValue<string>(configSync, SyncedValueIdentifier, string.Empty);
            _synced.ValueChanged += OnSyncedChanged;
        }

        private static void OnSyncedChanged()
        {
            try
            {
                string json = _synced.Value ?? "";
                if (ZNet.instance != null && ZNet.instance.IsServer()) WriteFile(json);
                SetActive(json);
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] synced templates could not be read: {ex.Message}"); }
        }

        private static void WriteFile(string json)
        {
            if (string.IsNullOrEmpty(json) || json == _lastWritten) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, json);
                _lastWritten = json;
                VaPieces.Log?.LogInfo($"[SoundPresets] templates saved to {FilePath}");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] did not save: {ex.Message}"); }
        }

        private static string ReadFile()
        {
            try { return File.Exists(FilePath) ? (_lastWritten = File.ReadAllText(FilePath)) : ""; }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] did not load ({ex.Message}); no templates"); return ""; }
        }

        private static void SetActive(string json)   // templates only (1.1.297: nothing is applied to prefabs)
        {
            Dictionary<string, Dictionary<string, SoundValues>> parsed = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { parsed = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, SoundValues>>>(json); }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] templates could not be read ({ex.Message}); the last good set stays"); return; }
            }
            _active = parsed != null
                ? new Dictionary<string, Dictionary<string, SoundValues>>(parsed.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
                : new Dictionary<string, Dictionary<string, SoundValues>>(StringComparer.Ordinal);
        }

        // ---- world session ----

        private static IEnumerator BeginSession()
        {
            while (ZNet.instance == null) { if (ZNetScene.instance == null) yield break; yield return null; }
            if (ZNet.instance.IsServer())
            {
                string json = ReadFile();
                if (_synced != null && _synced.Value != json) _synced.Value = json;
                else SetActive(json);
            }
        }

        private static void OnPlayerSpawned()
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || VAFapServerPresence.ServerRunsFap) return;
            if (!string.IsNullOrEmpty(_synced?.Value)) return;
            SetActive(ReadFile());
        }

        private static void EndSession()
        {
            try
            {
                StopPreview();
                OverrideWatch.Clear();   // 1.1.417
                foreach (var c in _clones.Values) if (c != null) UnityEngine.Object.Destroy(c);
                _clones.Clear();
                _detect.Clear(); s_soundByPrefab.Clear();
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] cleanup at logout failed: {ex.Message}"); }
        }

        // ---- per placed piece (1.1.297) ----
        // The override lives on THAT piece's ZDO: string key "fap_sound" = compact JSON { sourceKey: { field: value, ... } } holding only
        // the fields that differ from vanilla. Every client applies it in ZNetView.Awake (like VAFapPieceShaderOverridePatch /
        // VAAreaScaleAwakePatch) and again whenever the ZDO's data revision moves (1.1.417: OverrideWatch, edited pieces only), so a Save
        // shows for everyone live. Hierarchy sources are written on the instance. Effect entries (fuel added, door open / close) point
        // the INSTANCE's own EffectList entry (Instantiate deep-copies it) at a clone of the original effect with the values; clones are
        // shared by content (prefab + entry + values), so a thousand identical doors use one clone and nothing grows per piece.

        public const string ZdoKeyName = "fap_sound";
        internal static readonly int ZdoKey = ZdoKeyName.GetStableHashCode();

        internal static string PrefabNameOf(ZNetView nv)
        {
            var zdo = nv != null ? nv.GetZDO() : null;
            if (zdo == null) return nv != null ? Utils.GetPrefabName(nv.gameObject) : null;
            var p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return p != null ? p.name : Utils.GetPrefabName(nv.gameObject);
        }

        internal static string ReadOverrideJson(ZNetView nv)
        {
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            return zdo != null ? zdo.GetString(ZdoKey, "") : "";
        }

        // Full values per source (vanilla + the stored differences); null = no override.
        internal static Dictionary<string, SoundValues> ReadOverride(ZNetView nv, List<Source> sources)
            => Expand(ReadOverrideJson(nv), sources);

        private static Dictionary<string, SoundValues> Expand(string json, List<Source> sources)
        {
            if (string.IsNullOrEmpty(json) || sources == null) return null;
            Dictionary<string, Newtonsoft.Json.Linq.JObject> diff;
            try { diff = JsonConvert.DeserializeObject<Dictionary<string, Newtonsoft.Json.Linq.JObject>>(json); }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] bad {ZdoKeyName} on a piece ({ex.Message}); vanilla"); return null; }
            if (diff == null || diff.Count == 0) return null;
            var result = new Dictionary<string, SoundValues>(StringComparer.Ordinal);
            foreach (var s in sources)
            {
                if (!diff.TryGetValue(s.Key, out var d) || d == null) continue;
                var full = Newtonsoft.Json.Linq.JObject.FromObject(s.Vanilla);
                full.Merge(d);
                try { result[s.Key] = full.ToObject<SoundValues>(); } catch { }
            }
            return result.Count > 0 ? result : null;
        }

        // Only the changed fields of the changed sources; "" = all vanilla.
        internal static string Compact(Dictionary<string, SoundValues> values, List<Source> sources)
        {
            var outd = new Dictionary<string, Newtonsoft.Json.Linq.JObject>(StringComparer.Ordinal);
            foreach (var s in sources)
            {
                if (values == null || !values.TryGetValue(s.Key, out var v) || v == null) continue;
                var a = Newtonsoft.Json.Linq.JObject.FromObject(v);
                var b = Newtonsoft.Json.Linq.JObject.FromObject(s.Vanilla);
                var d = new Newtonsoft.Json.Linq.JObject();
                foreach (var p in a.Properties())
                    if (!Newtonsoft.Json.Linq.JToken.DeepEquals(p.Value, b[p.Name])) d[p.Name] = p.Value;
                if (d.Count > 0) outd[s.Key] = d;
            }
            return outd.Count > 0 ? JsonConvert.SerializeObject(outd, Formatting.None) : "";
        }

        // Save to the piece (values null / all vanilla: the key is cleared). Ownership claimed like FAP's other piece edits
        // (VAHopperContextProvider.SetHopper: ClaimOwnership, then ZDO.Set).
        // 1.1.303 (Fire: preset-aware Radius Swapper): presetName = the template the caller filled in (the window's selected template,
        // the swapper's TO preset). The piece is stamped with the template its values actually equal (presetName first), "" if none.
        internal static bool SaveToPiece(ZNetView nv, Dictionary<string, SoundValues> values, out string message, string presetName = null)
        {
            message = null;
            if (!VAVanillaPieceManager.IsAdminGateOpen()) { message = "Admins only."; return false; }
            if (nv == null || !nv.IsValid()) { message = "The piece is gone."; return false; }
            string prefabName = PrefabNameOf(nv);
            var sources = Sources(prefabName);
            string json = Compact(values, sources);
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(ZdoKey, json);   // Set raises the data revision: other clients' OverrideWatch re-applies (after the routed notice below)
            string stamp = json.Length == 0 ? "" : (MatchingTemplate(json, sources, presetName) ?? "");
            nv.GetZDO().Set(ZdoNameKey, stamp);   // 1.1.303: the template name stamp (fap_sound_name)
            string applied = ApplyToInstance(nv) ?? "";
            OverrideWatch.Track(nv, applied);   // 1.1.417: this piece now has (or just lost) an override: on the short list
            OverrideWatch.Announce(nv);          // 1.1.417: every other client re-checks this piece (routed RPC; replaces per-piece polling)
            message = json.Length == 0 ? "Saved: this piece is vanilla again." : "Saved to this piece.";
            VaPieces.Log?.LogInfo($"[SoundPresets] {prefabName} {nv.GetZDO().m_uid}: {(json.Length == 0 ? "override cleared" : $"{ZdoKeyName} = {json.Length} chars")}{(stamp.Length > 0 ? $", template '{stamp}'" : "")}");
            return true;
        }

        // ---- 1.1.303 (Fire: preset-aware Radius Swapper) ----
        // "fap_sound_name" = the template this piece's fap_sound equals, written by SaveToPiece (every FAP path that writes fap_sound).
        // A piece from before the stamp is matched by its values: its fap_sound equals the template's values for its sources.
        public const string ZdoNameKeyName = "fap_sound_name";
        internal static readonly int ZdoNameKey = ZdoNameKeyName.GetStableHashCode();

        // The template's values for this prefab's sources only (null = the template covers none of them).
        internal static Dictionary<string, SoundValues> TemplateFor(string name, List<Source> sources)
        {
            var t = Template(name);
            if (t == null || sources == null) return null;
            var d = new Dictionary<string, SoundValues>(StringComparer.Ordinal);
            foreach (var s in sources) if (t.TryGetValue(s.Key, out var v) && v != null) d[s.Key] = v.Copy();
            return d.Count > 0 && Compact(d, sources).Length > 0 ? d : null;
        }

        internal static IEnumerable<string> TemplatesFor(string prefabName)
        {
            var sources = Sources(prefabName);
            if (sources.Count == 0) return new string[0];
            return TemplateNames().Where(n => TemplateFor(n, sources) != null).ToList();
        }

        private static bool JsonEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            if (a == b) return true;
            try { return Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.Parse(a), Newtonsoft.Json.Linq.JToken.Parse(b)); }
            catch { return false; }
        }

        internal static bool JsonMatchesTemplate(string json, string name, List<Source> sources)
        {
            var d = TemplateFor(name, sources);
            return d != null && JsonEquals(json, Compact(d, sources));
        }

        internal static string MatchingTemplate(string json, List<Source> sources, string preferred)
        {
            if (string.IsNullOrEmpty(json)) return null;
            if (!string.IsNullOrEmpty(preferred) && JsonMatchesTemplate(json, preferred, sources)) return preferred;
            return TemplateNames().FirstOrDefault(n => JsonMatchesTemplate(json, n, sources));
        }

        // Does this placed piece carry the template? The stamp when there is one, else its values.
        internal static bool Carries(ZNetView nv, string name)
        {
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            if (zdo == null || string.IsNullOrEmpty(name)) return false;
            string json = zdo.GetString(ZdoKey, "");
            if (json.Length == 0) return false;
            string stamp = zdo.GetString(ZdoNameKey, "");
            if (stamp.Length > 0) return string.Equals(stamp, name, StringComparison.Ordinal);
            return JsonMatchesTemplate(json, name, Sources(PrefabNameOf(nv)));
        }

        // Vanilla first, then this piece's override on top. Returns the json applied.
        internal static string ApplyToInstance(ZNetView nv)
        {
            if (nv == null || ZNetScene.instance == null) return null;
            string prefabName = PrefabNameOf(nv);
            if (string.IsNullOrEmpty(prefabName)) return null;
            var sources = Sources(prefabName);
            if (sources.Count == 0) return null;
            string json = ReadOverrideJson(nv);
            var ov = Expand(json, sources);
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            var go = nv.gameObject;
            try
            {
                foreach (var s in sources)
                {
                    if (s.IsEffect) continue;
                    var a = FindSource(go.transform, s.SubPath);
                    if (a == null) continue;
                    var z = a.GetComponent<ZSFX>();
                    var templateZ = prefab != null ? FindSource(prefab.transform, s.SubPath)?.GetComponent<ZSFX>() : null;
                    bool playing = a.isActiveAndEnabled && a.isPlaying;
                    Write(a, z, ov != null && ov.TryGetValue(s.Key, out var v) ? v : s.Vanilla, VanillaClips(prefabName, s, templateZ));
                    if (playing && z != null && a.loop) z.Play();   // a running loop picks the new ranges at once
                }
                foreach (var grp in sources.Where(s => s.IsEffect).GroupBy(EntryKey))
                {
                    var first = grp.First();
                    var el = FindEffectList(go.transform, first);
                    if (el == null || el.m_effectPrefabs == null || first.Index >= el.m_effectPrefabs.Length || el.m_effectPrefabs[first.Index] == null) continue;
                    el.m_effectPrefabs[first.Index].m_prefab = ov != null && grp.Any(s => ov.ContainsKey(s.Key))
                        ? EffectClone(prefabName, grp.Key, grp.ToList(), ov) : first.OriginalEffect;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] applying {ZdoKeyName} on {prefabName} failed: {ex.Message}"); }
            return json;
        }

        private static GameObject EffectClone(string prefabName, string entryKey, List<Source> grp, Dictionary<string, SoundValues> ov)
        {
            string content = JsonConvert.SerializeObject(grp.Where(s => ov.ContainsKey(s.Key)).Select(s => new { s.Key, V = ov[s.Key] }), Formatting.None);
            string ck = prefabName + "|" + entryKey + "|" + content;
            if (_clones.TryGetValue(ck, out var have) && have != null) return have;
            var first = grp[0];
            var clone = UnityEngine.Object.Instantiate(first.OriginalEffect, CloneHolder.transform, false);
            clone.name = first.OriginalEffect.name;
            foreach (var s in grp)
            {
                if (!ov.TryGetValue(s.Key, out var v)) continue;
                var a = FindSource(clone.transform, s.SubPath);
                if (a != null) Write(a, a.GetComponent<ZSFX>(), v, VanillaClips(prefabName, s, null));
            }
            _clones[ck] = clone;
            return clone;
        }

        private static string EntryKey(Source s) => $"fx|{s.HostPath}|{s.CompType}.{s.Field}|{s.Index}";

        private static EffectList FindEffectList(Transform root, Source s)
        {
            var host = s.HostPath.Length == 0 ? root : root.Find(s.HostPath);
            if (host == null) return null;
            foreach (var comp in host.GetComponents<MonoBehaviour>())
            {
                if (comp == null || comp.GetType().Name != s.CompType) continue;
                var f = EffectFields(comp.GetType()).FirstOrDefault(x => x.Name == s.Field);
                if (f != null) return f.GetValue(comp) as EffectList;
            }
            return null;
        }

        private static IEnumerable<GameObject> LiveInstances(string prefabName)
        {
            var scene = ZNetScene.instance;
            if (scene == null) yield break;
            int hash = prefabName.GetStableHashCode();
            foreach (var nv in UnityEngine.Object.FindObjectsOfType<ZNetView>())
            {
                if (nv == null) continue;
                var zdo = nv.GetZDO();
                bool match = zdo != null ? zdo.GetPrefab() == hash : Utils.GetPrefabName(nv.gameObject) == prefabName;
                if (match) yield return nv.gameObject;
            }
        }

        private static GameObject CloneHolder
        {
            get
            {
                if (_cloneHolder != null) return _cloneHolder;
                _cloneHolder = new GameObject("FAP_SoundPresetClones");
                _cloneHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_cloneHolder);
                return _cloneHolder;
            }
        }

        // 1.1.417 ([ghost], lead 11-12, profiler-proven Tools\Rig\results\samepose_20261111\WRITEUP_samepose_20261112.md: FAP's per-piece SoundWatch
        // MonoBehaviour was ~7 ms of Fire's town frame: 23,942 Update calls a frame, bodies 1.64 ms + Unity's per-component dispatch 5.52 ms).
        // NO component per piece any more. Only pieces that HAVE a fap_sound override are watched, on ONE flat list:
        //   - joined at load (OnPieceAwake) when the piece's ZDO already carries an override;
        //   - joined where an override is written (SaveToPiece, the only writer of fap_sound in FAP);
        //   - joined on every other client by a global routed RPC "FAP_SoundChanged" (ZDOID) that SaveToPiece sends, so a piece that had NO override
        //     and gets one from another admin is picked up (a peer without this FAP ignores a global routed RPC it has no handler for).
        // ONE manager pass at 2 Hz walks that short list: data revision moved and the fap_sound string differs from what was applied -> re-apply.
        // A piece whose override is gone (cleared, applied as vanilla) leaves the list. Dead pieces leave by swap-remove. No allocations on the pass.
        // Census (Debug, 60 s): pieces watched, re-applies, RPCs. The watched count is the number of edited pieces in range (tens), not every piece
        // with a sound (tens of thousands in a town).
        internal static class OverrideWatch
        {
            private struct Entry { public ZNetView NView; public uint Rev; public string Applied; }
            private static readonly List<Entry> s_list = new List<Entry>();
            private static readonly HashSet<int> s_ids = new HashSet<int>();
            private static GameObject s_host;
            private static float s_next, s_nextReport;
            private static int c_reapplied, c_rpcs, c_joined;
            private static ZRoutedRpc s_rpcOn;
            private const string RpcName = "FAP_SoundChanged";

            internal static void Track(ZNetView nv, string applied)
            {
                if (nv == null || !nv.IsValid()) return;
                EnsureHost();
                int id = nv.GetInstanceID();
                uint rev = nv.GetZDO().DataRevision;
                if (s_ids.Contains(id))
                {
                    for (int i = 0; i < s_list.Count; i++)
                        if (s_list[i].NView == nv) { var e = s_list[i]; e.Applied = applied ?? ""; e.Rev = rev; s_list[i] = e; return; }
                }
                s_ids.Add(id);
                s_list.Add(new Entry { NView = nv, Rev = rev, Applied = applied ?? "" });
                c_joined++;
            }

            // a piece another client changed: join with revision 0, so the next pass checks it (and again whenever its data arrives later)
            private static void TrackForCheck(ZNetView nv)
            {
                if (nv == null || !nv.IsValid()) return;
                EnsureHost();
                int id = nv.GetInstanceID();
                if (s_ids.Contains(id))
                {
                    for (int i = 0; i < s_list.Count; i++) if (s_list[i].NView == nv) { var e = s_list[i]; e.Rev = 0; s_list[i] = e; return; }
                }
                s_ids.Add(id);
                s_list.Add(new Entry { NView = nv, Rev = 0, Applied = null });   // null = never applied here: any string differs
                c_joined++;
            }

            internal static void Announce(ZNetView nv)
            {
                try
                {
                    if (nv == null || !nv.IsValid() || ZRoutedRpc.instance == null) return;
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcName, nv.GetZDO().m_uid);
                }
                catch (Exception ex) { VaPieces.Log?.LogDebug($"[SoundPresets] change announce skipped: {ex.Message}"); }
            }

            internal static void RegisterRpc()
            {
                var r = ZRoutedRpc.instance;
                if (r == null || r == s_rpcOn) return;   // ZRoutedRpc is made per session; register once per instance
                r.Register<ZDOID>(RpcName, OnChanged);
                s_rpcOn = r;
            }

            private static void OnChanged(long sender, ZDOID id)
            {
                try
                {
                    c_rpcs++;
                    if (ZNetScene.instance == null || id.IsNone()) return;
                    var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
                    var nv = zdo != null ? ZNetScene.instance.FindInstance(zdo) : null;
                    if (nv != null) TrackForCheck(nv);   // not loaded here: its override is applied at load (OnPieceAwake) when it comes into range
                }
                catch (Exception ex) { VaPieces.Log?.LogDebug($"[SoundPresets] change notice skipped: {ex.Message}"); }
            }

            private static void EnsureHost()
            {
                if (s_host != null) return;
                s_host = new GameObject("FAP_SoundOverrideWatch");
                UnityEngine.Object.DontDestroyOnLoad(s_host);
                s_host.hideFlags = HideFlags.HideAndDontSave;
                s_host.AddComponent<Host>();
            }

            internal static void Clear()
            {
                s_list.Clear(); s_ids.Clear(); s_rpcOn = null;
            }

            private sealed class Host : MonoBehaviour
            {
                private void Update()
                {
                    float now = Time.time;
                    if (now < s_next) return;
                    s_next = now + 0.5f;   // 2 Hz
                    for (int i = s_list.Count - 1; i >= 0; i--)
                    {
                        var e = s_list[i];
                        if (e.NView == null || !e.NView.IsValid()) { Drop(i); continue; }
                        var zdo = e.NView.GetZDO();
                        uint rev = zdo.DataRevision;
                        if (rev == e.Rev) continue;
                        e.Rev = rev;
                        string json = zdo.GetString(ZdoKey, "");
                        if (e.Applied == null || json != e.Applied)
                        {
                            try { e.Applied = ApplyToInstance(e.NView) ?? ""; c_reapplied++; }
                            catch (Exception ex) { VaPieces.Log?.LogWarning($"[SoundPresets] re-apply failed: {ex.Message}"); e.Applied = json; }
                        }
                        if (json.Length == 0 && e.Applied.Length == 0) { Drop(i); continue; }   // vanilla again: nothing left to watch
                        s_list[i] = e;
                    }
                    if (now >= s_nextReport)
                    {
                        s_nextReport = now + 60f;
                        if (c_joined + c_reapplied + c_rpcs > 0)
                            VaPieces.Log?.LogDebug($"[SoundPresets] override watch, last 60 s: {s_list.Count} edited piece(s) watched (no component per piece), {c_joined} joined, {c_reapplied} re-applied, {c_rpcs} change notice(s)");
                        c_joined = c_reapplied = c_rpcs = 0;
                    }
                }

                private static void Drop(int i)
                {
                    var nv = s_list[i].NView;
                    if ((object)nv != null) s_ids.Remove(nv.GetInstanceID());   // a destroyed view still answers its instance id (managed field)
                    int last = s_list.Count - 1;
                    s_list[i] = s_list[last];
                    s_list.RemoveAt(last);
                }
            }
        }

        internal static void OnPieceAwake(ZNetView nv)
        {
            if (nv == null || !nv.IsValid() || ZNetScene.instance == null) return;
            // 1.1.402 ([visual] audit via [lead] 11-08): every ZNetView.Awake paid a GetComponent<Piece>, a prefab-name string and a lookup even for
            // prefabs with no sound. "Piece with sound" is a prefab fact: cached by prefab hash once detection has really run (Sources caches it).
            int hash = nv.GetZDO().GetPrefab();
            if (!s_soundByPrefab.TryGetValue(hash, out bool has))
            {
                if (nv.GetComponent<Piece>() == null) { s_soundByPrefab[hash] = false; return; }
                string prefabName = PrefabNameOf(nv);
                has = HasSound(prefabName);
                if (!string.IsNullOrEmpty(prefabName) && _detect.ContainsKey(prefabName)) s_soundByPrefab[hash] = has;   // only once detection is cached (scene up)
            }
            if (!has) return;
            // 1.1.417: no component per piece. A piece with an override is applied now and joins the one short watch list; a piece without one
            // costs nothing more (a later override from any admin reaches it through SaveToPiece's routed notice).
            string json = ReadOverrideJson(nv);
            if (json.Length > 0) OverrideWatch.Track(nv, ApplyToInstance(nv) ?? "");
        }

        // ---- preview ----
        // 1.1.296 (Fire: preview loops while the section is open): one looping preview at a time. It plays the selected source with
        // the LIVE edit values (the window's SoundValues object, read every frame, so a slider change is heard at once); a one-shot
        // sound replays after a short gap. It sits at the nearest placed instance of the piece (within 40 m) or 4 m in front of the
        // player, so distance / rolloff / spatial blend are audible (1.1.295 played it on the listener, where they do nothing).
        // StopPreview: window closed, another section's Preview, another prefab loaded, Reset, logout.

        private static GameObject _previewGo;
        internal static string PreviewKey { get; private set; }

        internal static void Preview(string prefabName, Source s, SoundValues v)
        {
            StopPreview();
            var player = Player.m_localPlayer;
            if (player == null || s == null || v == null) return;
            AudioSource original = s.IsEffect
                ? (s.OriginalEffect != null ? FindSource(s.OriginalEffect.transform, s.SubPath) : null)
                : (ZNetScene.instance?.GetPrefab(prefabName) is GameObject p ? FindSource(p.transform, s.SubPath) : null);
            if (original == null) return;
            var z = original.GetComponent<ZSFX>();
            var clips = z != null && z.m_audioClips != null && z.m_audioClips.Length > 0 ? z.m_audioClips : (original.clip != null ? new[] { original.clip } : new AudioClip[0]);
            if (clips.Length == 0) { player.Message(MessageHud.MessageType.TopLeft, "This source has no clip to preview"); return; }
            _previewGo = new GameObject("FAP_SoundPreview");
            UnityEngine.Object.DontDestroyOnLoad(_previewGo);
            _previewGo.transform.position = PreviewSpot(prefabName, s, player, out string where);
            var a = _previewGo.AddComponent<AudioSource>();
            a.playOnAwake = false;
            a.outputAudioMixerGroup = original.outputAudioMixerGroup;
            var curve = original.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
            var loop = _previewGo.AddComponent<PreviewLoop>();
            loop.Init(a, v, clips, curve);
            PreviewKey = s.Key;
            VerdantsAscent.VaPieces.Log?.LogDebug($"[SoundPresets] preview loop {prefabName} {s.Key} ({clips.Length} clip(s)) at {where}");
        }

        internal static void StopPreview()
        {
            if (_previewGo != null)
            {
                VerdantsAscent.VaPieces.Log?.LogDebug($"[SoundPresets] preview stopped ({PreviewKey})");
                UnityEngine.Object.Destroy(_previewGo);
            }
            _previewGo = null;
            PreviewKey = null;
        }

        // 1.1.297: the preview plays at the edited piece (set by the window); else the nearest instance as before.
        internal static ZNetView PreviewAnchor;

        private static Vector3 PreviewSpot(string prefabName, Source s, Player player, out string where)
        {
            if (PreviewAnchor != null && PreviewAnchor.IsValid())
            {
                Transform at = PreviewAnchor.transform;
                if (!s.IsEffect) { var pa = FindSource(at, s.SubPath); if (pa != null) at = pa.transform; }
                where = "the edited piece";
                return at.position;
            }
            Vector3 best = Vector3.zero; float bestD = 40f * 40f; bool found = false;
            foreach (var go in LiveInstances(prefabName))
            {
                if (go == null) continue;
                Transform t = go.transform;
                if (!s.IsEffect) { var a = FindSource(go.transform, s.SubPath); if (a != null) t = a.transform; }
                float d = (t.position - player.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = t.position; found = true; }
            }
            if (found) { where = $"the nearest {prefabName} ({Mathf.Sqrt(bestD):0.#} m)"; return best; }
            where = "4 m in front of the player";
            return player.transform.position + Vector3.up + player.transform.forward * 4f;
        }

        private sealed class PreviewLoop : MonoBehaviour
        {
            private AudioSource _a;
            private SoundValues _v;
            private AudioClip[] _clips;
            private AnimationCurve _curve;
            private float _pitch = 1f, _vol = 1f, _next;
            private bool _played;

            internal void Init(AudioSource a, SoundValues v, AudioClip[] clips, AnimationCurve curve)
            { _a = a; _v = v; _clips = clips; _curve = curve; }

            private void Update()
            {
                if (_a == null || _v == null) return;
                if (Player.m_localPlayer == null) { StopPreview(); return; }
                _a.spatialBlend = _v.SpatialBlend; _a.minDistance = _v.MinDistance; _a.maxDistance = _v.MaxDistance;
                if (_a.rolloffMode != (AudioRolloffMode)_v.RolloffMode)
                {
                    _a.rolloffMode = (AudioRolloffMode)_v.RolloffMode;
                    if (_v.RolloffMode == (int)AudioRolloffMode.Custom && _curve != null) _a.SetCustomCurve(AudioSourceCurveType.CustomRolloff, _curve);
                }
                _a.priority = _v.Priority; _a.dopplerLevel = _v.DopplerLevel; _a.spread = _v.Spread;
                _a.loop = _v.Loop;
                if (_v.HasZsfx)
                {
                    // the ZSFX ranges, re-picked on each play and whenever an edit moves the range past the picked value
                    if (!InRange(_pitch, _v.MinPitch, _v.MaxPitch)) _pitch = Pick(_v.MinPitch, _v.MaxPitch);
                    if (!InRange(_vol, _v.MinVol, _v.MaxVol)) _vol = Pick(_v.MinVol, _v.MaxVol);
                    _a.pitch = _pitch; _a.volume = _vol;
                }
                else { _a.pitch = _v.Pitch; _a.volume = _v.Volume; }
                if (_a.isPlaying) return;
                if (_played && Time.unscaledTime < _next) return;
                if (_played && _next == 0f) { _next = Time.unscaledTime + 0.6f; return; }   // one-shot finished: short gap, then again
                _next = 0f;
                _played = true;
                int clip = _v.Clip >= 0 && _v.Clip < _clips.Length ? _v.Clip : UnityEngine.Random.Range(0, _clips.Length);
                _a.clip = _clips[clip];
                if (_v.HasZsfx)
                {
                    _pitch = Pick(_v.MinPitch, _v.MaxPitch); _vol = Pick(_v.MinVol, _v.MaxVol);
                    _a.pitch = _pitch; _a.volume = _vol;
                    _a.panStereo = _v.RandomPan ? Pick(_v.MinPan, _v.MaxPan) : 0f;
                }
                _a.Play();
            }

            private static bool InRange(float x, float a, float b) => x >= Mathf.Min(a, b) - 0.0001f && x <= Mathf.Max(a, b) + 0.0001f;
            private static float Pick(float a, float b) => UnityEngine.Random.Range(a, b);
        }

        // ---- hooks ----

        [HarmonyPatch]
        internal static class Patches
        {
            [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
            private static void ZNetSceneAwake()
            {
                try   // 1.1.318 (audit M8)
                {
                    _detect.Clear(); s_soundByPrefab.Clear();
                    OverrideWatch.Clear(); OverrideWatch.RegisterRpc();   // 1.1.417: the short watch list + the change notice, per session
                    if (VaPieces.Instance != null) VaPieces.Instance.StartCoroutine(BeginSession());
                }
                catch (System.Exception e) { UnityEngine.Debug.LogError($"[FAP] sound presets: ZNetScene.Awake session start failed: {e.Message}"); }
            }

            [HarmonyPatch(typeof(ZNetScene), "OnDestroy"), HarmonyPrefix]
            private static void ZNetSceneDestroy() => EndSession();

            [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned)), HarmonyPostfix]
            private static void PlayerSpawned(Player __instance)
            {
                if (__instance == Player.m_localPlayer) { OverrideWatch.RegisterRpc(); OnPlayerSpawned(); }   // 1.1.417: ZRoutedRpc is up by now (once per session)
            }

            // 1.1.297: every placed piece with sound, on load / spawn, for everyone (same hook as VAFapPieceShaderOverridePatch).
            // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
            internal static void NViewAwake(ZNetView __instance)
            {
                try { OnPieceAwake(__instance); }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [SoundPresets] {ZdoKeyName} on load failed: {ex.Message}"); }
            }
        }
    }
}
