using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Api
{
    // 1.1.335 ([lead] for Fire: the Studio's E window gets FAP's preset rows - "Edit light...", "Edit sound..." - and the data presets):
    // one string-in / JSON-out API over FAP's OWN preset stores and in-game editor paths, no second store:
    //   light: VALightPresetStore (LightPresets.json) + VALightApplier.Read / SetLive - what "Edit light..." (VALightConfigUI) applies
    //   sound: VASoundPresets templates + ReadOverride / SaveToPiece - what "Edit sound..." (VASoundPresetUi) applies
    //   data:  VADataPresetStore + Arm - the build menu's data presets (they preset the NEXT placement; there is no placed-piece editor)
    // Runs on the admin's own CLIENT (as the in-game editors do: their ZDO writes + broadcasts reach every peer). Main thread.
    // zdoid = "userid:id". Fields come in the in-game editor's order. Add-only.
    public static partial class FapPresets
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        private static string Ok(string report) => "{\"ok\":true,\"report\":" + Q(report) + "}";
        private static string No(string report) => "{\"ok\":false,\"report\":" + Q(report) + "}";
        private static bool AdminOk(out string why)
        {
            why = null;
            if (ZNetScene.instance == null) { why = "no world"; return false; }
            // 1.1.336 (Studio God client with no game client): on the SERVER (dedi / host) with no local player it runs as the server -
            // the in-game editors' own paths write the ZDO and broadcast from there (FGC on the dedi is already admin-gated).
            if (Player.m_localPlayer == null) { if (ZNet.instance != null && ZNet.instance.IsServer()) return true; why = "no local player (call it on the admin's own client, or on the server)"; return false; }
            if (!Managers.VAVanillaPieceManager.IsAdminGateOpen()) { why = "admins only"; return false; }
            return true;
        }
        private static ZNetView Piece(string zdoid, out string why)
        {
            why = null;
            var p = (zdoid ?? "").Split(':');
            if (p.Length != 2 || !long.TryParse(p[0], NumberStyles.Integer, Inv, out long user) || !uint.TryParse(p[1], NumberStyles.Integer, Inv, out uint id)) { why = "zdoid must be \"userid:id\""; return null; }
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(new ZDOID(user, id)) : null;
            var nv = go != null ? go.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid()) { why = "that piece is not loaded here"; return null; }
            return nv;
        }

        /// <summary>The preset kinds FAP offers for a prefab, as the in-game menus decide them: ["light","sound","data"].</summary>
        public static string Kinds(string prefab)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab ?? "") : null;
            if (go == null) return "[]";
            var k = new List<string>();
            if (VALightApplier.HasLight(go)) k.Add(Q("light"));
            if (VASoundPresets.HasSound(prefab)) k.Add(Q("sound"));
            if (VAExtraFieldCatalogue.Build(go).Any(f => f.Settable)) k.Add(Q("data"));
            return "[" + string.Join(",", k) + "]";
        }

        /// <summary>The editable fields of a kind for a prefab, in the editor's order: [{key,label,type,min,max,options}].</summary>
        public static string Fields(string kind, string prefab)
        {
            if (TryExt(kind, "Fields", out var ext, prefab)) return ext;
            return FieldsOwn(kind, prefab);
        }
        private static string FieldsOwn(string kind, string prefab)
        {
            var sb = new StringBuilder("[");
            if (kind == "light")
            {
                // VALightPreset's fields ARE the light editor's fields, in its order
                foreach (var f in typeof(VALightPreset).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.Name == "name" || f.Name == "Note") continue;
                    string type = f.FieldType == typeof(bool) ? "bool" : f.FieldType == typeof(float) ? "float" : (f.Name == "Color" || f.Name == "FlameTint" || f.Name == "Emissive") ? "color" : "text";
                    if (sb.Length > 1) sb.Append(',');
                    sb.Append("{\"key\":").Append(Q(f.Name)).Append(",\"label\":").Append(Q(f.Name)).Append(",\"type\":").Append(Q(type)).Append('}');
                }
            }
            else if (kind == "sound")
            {
                foreach (var s in VASoundPresets.Sources(prefab ?? ""))
                {
                    if (sb.Length > 1) sb.Append(',');
                    sb.Append("{\"key\":").Append(Q(s.Key)).Append(",\"label\":").Append(Q(s.Label)).Append(",\"type\":\"sound\",\"vanilla\":").Append(JsonUtility.ToJson(s.Vanilla)).Append('}');
                }
            }
            else if (kind == "data")
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab ?? "") : null;
                if (go != null)
                    foreach (var f in VAExtraFieldCatalogue.Build(go).Where(f => f.Settable))
                    {
                        if (sb.Length > 1) sb.Append(',');
                        sb.Append("{\"key\":").Append(Q(f.Key)).Append(",\"label\":").Append(Q(f.Key)).Append(",\"type\":").Append(Q(f.Kind.ToString().ToLowerInvariant()))
                          .Append(",\"default\":").Append(Q(f.PrefabValue)).Append(",\"hint\":").Append(Q(f.Hint)).Append('}');
                    }
            }
            return sb.Append(']').ToString();
        }

        /// <summary>The saved presets of a kind (for sound / data: the ones for that prefab): [{name, values}].</summary>
        public static string List(string kind, string prefab)
        {
            if (TryExt(kind, "List", out var ext, prefab)) return ext;
            return ListOwn(kind, prefab);
        }
        private static string ListOwn(string kind, string prefab)
        {
            var sb = new StringBuilder("[");
            if (kind == "light")
                foreach (var p in VALightPresetStore.All) { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(p.name)).Append(",\"note\":").Append(Q(p.Note)).Append(",\"values\":").Append(JsonUtility.ToJson(p)).Append('}'); }
            else if (kind == "sound")
            {
                var src = VASoundPresets.Sources(prefab ?? "");
                foreach (var n in VASoundPresets.TemplatesFor(prefab ?? "")) { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(n)).Append(",\"values\":").Append(SoundJson(VASoundPresets.TemplateFor(n, src))).Append('}'); }
            }
            else if (kind == "data")
                foreach (var p in VADataPresetStore.For(prefab ?? "")) { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(p.name)).Append(",\"values\":").Append(DataJson(p)).Append('}'); }
            return sb.Append(']').ToString();
        }

        /// <summary>A placed piece's current values for a kind (light: its ZDO; sound: its override over vanilla). data: refused
        /// (data presets preset the next placement).</summary>
        public static string Read(string kind, string zdoid)
        {
            if (TryExt(kind, "Read", out var ext, zdoid)) return ext;
            return ReadOwn(kind, zdoid);
        }
        private static string ReadOwn(string kind, string zdoid)
        {
            var nv = Piece(zdoid, out string why);
            if (nv == null) return No(why);
            if (kind == "light") { var p = VALightApplier.Read(nv.GetZDO()); return p != null ? "{\"ok\":true,\"values\":" + JsonUtility.ToJson(p) + "}" : "{\"ok\":true,\"values\":null,\"report\":\"the prefab's own light (no FAP values on this piece)\"}"; }
            if (kind == "sound")
            {
                string prefab = VASoundPresets.PrefabNameOf(nv);
                var src = VASoundPresets.Sources(prefab);
                var own = VASoundPresets.ReadOverride(nv, src);
                return "{\"ok\":true,\"own\":" + (VASoundPresets.ReadOverrideJson(nv).Length > 0 ? "true" : "false") + ",\"values\":" + SoundJson(own) + "}";
            }
            if (kind == "data") return ReadData(nv);   // 1.1.341: a placed piece's current data fields
            return No("kind must be light / sound / data");
        }

        /// <summary>Apply a saved preset: light / sound to a placed piece (the editors' own apply), data = arm it for the next placement.</summary>
        public static string Apply(string kind, string zdoidOrPrefab, string presetName)
        {
            if (TryExt(kind, "Apply", out var ext, zdoidOrPrefab, presetName)) return ext;
            if (kind == "data" && (zdoidOrPrefab ?? "").IndexOf(':') > 0) return ApplyDataTo(zdoidOrPrefab, presetName);   // 1.1.341: a placed piece
            return ApplyOwn(kind, zdoidOrPrefab, presetName);
        }
        private static string ApplyOwn(string kind, string zdoidOrPrefab, string presetName)
        {
            if (!AdminOk(out string why)) return No(why);
            if (kind == "data")
            {
                if (!VADataPresetStore.TryGet(presetName, out var dp)) return No($"no data preset '{presetName}'");
                string err = VADataPresetStore.Arm(dp);
                return err == null ? Ok($"data preset '{presetName}' armed for the next {dp.Prefab} placement") : No(err);
            }
            var nv = Piece(zdoidOrPrefab, out why);
            if (nv == null) return No(why);
            if (kind == "light")
            {
                if (!VALightPresetStore.TryGet(presetName, out var lp)) return No($"no light preset '{presetName}'");
                VALightApplier.SetLive(nv, lp);
                return Ok($"light preset '{presetName}' applied to the piece");
            }
            if (kind == "sound")
            {
                var src = VASoundPresets.Sources(VASoundPresets.PrefabNameOf(nv));
                var values = VASoundPresets.TemplateFor(presetName, src);
                if (values == null) return No($"no sound preset '{presetName}'");
                return VASoundPresets.SaveToPiece(nv, values, out string msg, presetName) ? Ok(msg) : No(msg);
            }
            return No("kind must be light / sound / data");
        }

        /// <summary>Write values to a placed piece (the editors' own apply). light: a VALightPreset JSON (null / "" = reset to the
        /// prefab's own light); sound: {"items":[{"key":..., "values":{SoundValues}}]}.</summary>
        public static string Set(string kind, string zdoid, string valuesJson)
        {
            if (TryExt(kind, "Set", out var ext, zdoid, valuesJson)) return ext;
            return SetOwn(kind, zdoid, valuesJson);
        }
        private static string SetOwn(string kind, string zdoid, string valuesJson)
        {
            if (!AdminOk(out string why)) return No(why);
            var nv = Piece(zdoid, out why);
            if (nv == null) return No(why);
            if (kind == "light")
            {
                if (string.IsNullOrEmpty(valuesJson)) { VALightApplier.SetLive(nv, null); return Ok("light reset to the prefab's own"); }
                var p = new VALightPreset(); JsonUtility.FromJsonOverwrite(valuesJson, p);
                VALightApplier.SetLive(nv, p);
                return Ok("light values applied to the piece");
            }
            if (kind == "sound")
            {
                var src = VASoundPresets.Sources(VASoundPresets.PrefabNameOf(nv));
                var values = ParseSound(valuesJson, src, out string bad);
                if (values == null) return No(bad);
                return VASoundPresets.SaveToPiece(nv, values, out string msg) ? Ok(msg) : No(msg);
            }
            if (kind == "data") return SetData(nv, valuesJson);   // 1.1.341: straight to the placed piece
            return No("kind must be light / sound / data");
        }

        /// <summary>Save values as a named preset in FAP's own store. light: VALightPreset JSON; sound: as Set; data: {"items":[{"key","text"}]}.</summary>
        public static string Save(string kind, string name, string prefab, string valuesJson)
        {
            if (TryExt(kind, "Save", out var ext, name, prefab, valuesJson)) return ext;
            return SaveOwn(kind, name, prefab, valuesJson);
        }
        private static string SaveOwn(string kind, string name, string prefab, string valuesJson)
        {
            if (!AdminOk(out string why)) return No(why);
            if (string.IsNullOrWhiteSpace(name)) return No("a name is needed");
            if (kind == "light")
            {
                var p = new VALightPreset(); JsonUtility.FromJsonOverwrite(valuesJson ?? "{}", p); p.name = name;
                return VALightPresetStore.Save(p) ? Ok($"light preset '{name}' saved") : No("could not save (see the log)");
            }
            if (kind == "sound")
            {
                var values = ParseSound(valuesJson, VASoundPresets.Sources(prefab ?? ""), out string bad);
                if (values == null) return No(bad);
                return VASoundPresets.SaveTemplate(name, values, out string msg) ? Ok(msg) : No(msg);
            }
            if (kind == "data")
            {
                var w = JsonUtility.FromJson<DataItems>(valuesJson ?? "{}");
                var dp = new VADataPreset { name = name, Prefab = prefab ?? "" };
                if (w?.items != null) foreach (var it in w.items) dp.Values.Add(new VADataPresetValue { Key = it.key, Text = it.text });
                return VADataPresetStore.Save(dp) ? Ok($"data preset '{name}' saved for {prefab}") : No("could not save (see the log)");
            }
            return No("kind must be light / sound / data");
        }

        /// <summary>Delete a named preset from FAP's store (light / data). Sound templates have no delete in game, so none here.</summary>
        public static string Delete(string kind, string name)
        {
            if (TryExt(kind, "Delete", out var ext, name)) return ext;
            return DeleteOwn(kind, name);
        }
        private static string DeleteOwn(string kind, string name)
        {
            if (!AdminOk(out string why)) return No(why);
            if (kind == "light") return VALightPresetStore.Delete(name) ? Ok($"light preset '{name}' deleted") : No($"no light preset '{name}'");
            if (kind == "data") return VADataPresetStore.Delete(name) ? Ok($"data preset '{name}' deleted") : No($"no data preset '{name}'");
            if (kind == "sound") return No("sound presets have no delete in game (FAP's sound window has none either)");
            return No("kind must be light / sound / data");
        }

        /// <summary>1.1.337 ([lead]: the Studio places pieces through FGC's place ops, not the hammer): write a data preset onto a piece
        /// that was JUST placed - the in-game armed placement's own code (VADataPresetStore.Parse + VAExtraDataStamp.WriteValues, the
        /// ZNetView.LoadFields format). Every peer that loads the piece from now on creates it with the fields; a copy already loaded
        /// (e.g. the placer's) shows them after its next reload, as with the in-game path's ZDO side. Client or server.</summary>
        public static string ApplyDataTo(string zdoid, string presetName)
        {
            if (!AdminOk(out string why)) return No(why);
            var nv = Piece(zdoid, out why);
            if (nv == null) return No(why);
            if (!VADataPresetStore.TryGet(presetName, out var dp)) return No($"no data preset '{presetName}'");
            string prefab = global::Utils.GetPrefabName(nv.gameObject);
            if (!string.Equals(dp.Prefab, prefab, StringComparison.Ordinal)) return No($"data preset '{presetName}' is for {dp.Prefab}, this piece is {prefab}");
            string err = VADataPresetStore.Parse(dp, out var values);
            if (err != null) return No(err);
            if (!nv.IsOwner()) nv.ClaimOwnership();
            VAExtraDataStamp.WriteValues(nv.GetZDO(), values);
            return Ok($"data preset '{presetName}' written to the piece ({values.Count} field(s))");
        }

        /// <summary>1.1.336: make / remove a hopper on a placed container - FAP's "Make hopper" / "Remove hopper" row action (Core's
        /// HopperChest flag + attach), the same code the in-game row runs. Read side: Kinds-style probe via IsHopper.</summary>
        public static string Hopper(string zdoid, bool hopper)
        {
            if (!AdminOk(out string why)) return No(why);
            var nv = Piece(zdoid, out why);
            if (nv == null) return No(why);
            var c = nv.GetComponent<Container>();
            if (c == null) return No("not a container");
            Patches.VAHopperContextProvider.SetHopperFor(c, hopper);
            return Patches.VAHopperContextProvider.IsHopper(c) == hopper ? Ok(hopper ? "made a hopper" : "no longer a hopper") : No("the flag did not change (see the log)");
        }

        /// <summary>1.1.336: is that placed container a hopper? {ok, hopper} (container only).</summary>
        public static string IsHopper(string zdoid)
        {
            var nv = Piece(zdoid, out string why);
            if (nv == null) return No(why);
            var c = nv.GetComponent<Container>();
            if (c == null) return No("not a container");
            return "{\"ok\":true,\"hopper\":" + (Patches.VAHopperContextProvider.IsHopper(c) ? "true" : "false") + "}";
        }

        // ── JSON helpers (JsonUtility cannot do dictionaries) ──
        [Serializable] private sealed class SoundItem { public string key; public VASoundPresets.SoundValues values; }
        [Serializable] private sealed class SoundItems { public SoundItem[] items; }
        [Serializable] private sealed class DataItem { public string key; public string text; }
        [Serializable] private sealed class DataItems { public DataItem[] items; }
        private static string SoundJson(Dictionary<string, VASoundPresets.SoundValues> v)
        {
            var w = new SoundItems { items = v == null ? new SoundItem[0] : v.Select(kv => new SoundItem { key = kv.Key, values = kv.Value }).ToArray() };
            return JsonUtility.ToJson(w);
        }
        private static string DataJson(VADataPreset p) => JsonUtility.ToJson(new DataItems { items = p.Values.Select(v => new DataItem { key = v.Key, text = v.Text }).ToArray() });
        private static Dictionary<string, VASoundPresets.SoundValues> ParseSound(string json, List<VASoundPresets.Source> sources, out string bad)
        {
            bad = null;
            SoundItems w;
            try { w = JsonUtility.FromJson<SoundItems>(json ?? ""); } catch (Exception ex) { bad = "sound values JSON: " + ex.Message; return null; }
            if (w?.items == null || w.items.Length == 0) { bad = "no sound items"; return null; }
            var keys = new HashSet<string>(sources.Select(s => s.Key));
            var d = new Dictionary<string, VASoundPresets.SoundValues>(StringComparer.Ordinal);
            foreach (var it in w.items)
            {
                if (!keys.Contains(it.key ?? "")) { bad = $"'{it.key}' is not a sound source of this prefab"; return null; }
                d[it.key] = it.values ?? new VASoundPresets.SoundValues();
            }
            return d;
        }
    }
}
