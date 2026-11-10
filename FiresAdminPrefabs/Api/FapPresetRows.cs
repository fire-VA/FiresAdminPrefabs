using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Api
{
    // 1.1.341 ([lead], Fire: hovering a piece + E "should pop up the context menus relevant to that piece like lighting presets, data
    // presets, sound presets etc."): the Studio's E window mirrors the in-game menus.
    //   Rows(zdoid): the rows FAP's in-game menus show for that placed piece, in their order - Core context-menu providers (hopper,
    //                sound, light: VAHopperContextProvider / VASoundContextProvider's own tests), then data (the build menu's data
    //                preset, now also on a placed piece), other mods' FapPieceMenu entries (has "external"), registered kinds.
    //   data on a placed piece: Read / Set / Apply(zdoid) - the armed placement's own code (VADataPresetStore.Parse +
    //                VAExtraDataStamp.WriteValues), read back from the ZDO keys ZNetView.LoadFields uses.
    //   RegisterKind: another mod's preset kind ([ghost]'s NPC templates) - a handler object found by reflection, routed by every
    //                FapPresets call. Headless on the dedi like the rest. Add-only.
    public static partial class FapPresets
    {
        private sealed class ExtKind { public string Kind, Label; public object H; }
        private static readonly List<ExtKind> s_ext = new List<ExtKind>();

        /// <summary>Register another mod's preset kind. handler: public methods bool Has(string zdoid), string Fields(string prefab),
        /// string List(string prefab), string Read(string zdoid), string Apply(string zdoid, string preset), string Set(string zdoid,
        /// string valuesJson), string Save(string name, string prefab, string valuesJson), string Delete(string name). Same id = replaced.</summary>
        public static void RegisterKind(string kind, string label, object handler)
        {
            if (string.IsNullOrEmpty(kind) || handler == null || kind == "light" || kind == "sound" || kind == "data" || kind == "hopper") return;
            s_ext.RemoveAll(e => e.Kind == kind);
            s_ext.Add(new ExtKind { Kind = kind, Label = string.IsNullOrEmpty(label) ? kind : label, H = handler });
            VaPieces.Log?.LogDebug($"[FapPresets] preset kind '{kind}' registered by {handler.GetType().FullName} (1.1.341)");
        }

        // a registered kind's method; false = not a registered kind (FAP's own branches run)
        private static bool TryExt(string kind, string method, out string json, params object[] args)
        {
            json = null;
            var e = s_ext.FirstOrDefault(x => x.Kind == kind);
            if (e == null) return false;
            var m = e.H.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, args.Select(_ => typeof(string)).ToArray(), null);
            if (m == null) { json = No($"kind '{kind}' has no {method}"); return true; }
            try { json = m.Invoke(e.H, args) as string ?? No($"{kind}.{method} returned nothing"); }
            catch (Exception ex) { var r = ex.InnerException ?? ex; VaPieces.Log?.LogWarning($"[FapPresets] {kind}.{method} threw: {r.GetType().Name}: {r.Message} (1.1.341)"); json = No($"{kind}.{method} threw: {r.Message}"); }
            return true;
        }

        /// <summary>The preset rows the in-game menus show for that placed piece, in their order:
        /// {ok, prefab, rows:[{kind, label, has:"edit"|"toggle"|"external", on?}]}.</summary>
        public static string Rows(string zdoid)
        {
            var nv = Piece(zdoid, out string why);
            if (nv == null) return No(why);
            string prefab = global::Utils.GetPrefabName(nv.gameObject);
            var rows = new List<string>();
            void Row(string kind, string label, string has, bool? on = null) =>
                rows.Add("{\"kind\":" + Q(kind) + ",\"label\":" + Q(label) + ",\"has\":" + Q(has) + (on.HasValue ? ",\"on\":" + (on.Value ? "true" : "false") : "") + "}");
            var c = nv.GetComponent<Container>();
            if (c != null) { bool h = Patches.VAHopperContextProvider.IsHopper(c); Row("hopper", h ? "Remove hopper" : "Make hopper", "toggle", h); }
            if (VASoundPresets.HasSound(VASoundPresets.PrefabNameOf(nv))) Row("sound", VASoundPresets.ReadOverrideJson(nv).Length > 0 ? "Edit sound... (own sound)" : "Edit sound...", "edit");
            if (VALightApplier.HasLight(nv.gameObject)) Row("light", "Edit light...", "edit");
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go != null && VAExtraFieldCatalogue.Build(go).Any(f => f.Settable)) Row("data", "Data preset", "edit");
            var piece = nv.GetComponent<Piece>();
            if (piece != null) foreach (var e in FapPieceMenu.Collect(piece)) Row("ext:" + e.Key, e.Key, "external");
            foreach (var e in s_ext.ToArray())
            {
                bool has = false;
                var m = e.H.GetType().GetMethod("Has", new[] { typeof(string) });
                try { has = m != null && (bool)m.Invoke(e.H, new object[] { zdoid }); } catch (Exception ex) { VaPieces.Log?.LogDebug($"[FapPresets] {e.Kind}.Has threw: {(ex.InnerException ?? ex).Message} (1.1.341)"); }
                if (has) Row(e.Kind, e.Label, "edit");
            }
            VaPieces.Log?.LogDebug($"[FapPresets] Rows {zdoid} ({prefab}): {rows.Count} row(s) (1.1.341)");
            return "{\"ok\":true,\"prefab\":" + Q(prefab) + ",\"rows\":[" + string.Join(",", rows) + "]}";
        }

        // data on a placed piece: the ZDO keys ZNetView.LoadFields reads (what VAExtraDataStamp.WriteValues writes)
        private static string ReadData(ZNetView nv)
        {
            var go = ZNetScene.instance.GetPrefab(global::Utils.GetPrefabName(nv.gameObject));
            var zdo = nv.GetZDO();
            var items = new List<DataItem>();
            if (go != null)
                foreach (var f in VAExtraFieldCatalogue.Build(go).Where(f => f.Settable))
                {
                    string text;
                    switch (f.Kind)
                    {
                        case VAExtraFieldKind.Int: text = zdo.GetInt(f.Key, int.MinValue) is int i && i != int.MinValue ? i.ToString(Inv) : null; break;
                        case VAExtraFieldKind.Float: { float v = zdo.GetFloat(f.Key, float.NaN); text = float.IsNaN(v) ? null : v.ToString("R", Inv); break; }
                        case VAExtraFieldKind.Bool: text = zdo.GetInt(f.Key, -1) is int b && b >= 0 ? (b != 0 ? "true" : "false") : null; break;
                        case VAExtraFieldKind.Vector3: { var v = zdo.GetVec3(f.Key, new Vector3(float.NaN, 0, 0)); text = float.IsNaN(v.x) ? null : $"{v.x.ToString("R", Inv)},{v.y.ToString("R", Inv)},{v.z.ToString("R", Inv)}"; break; }
                        default: { var s = zdo.GetString(f.Key, null); text = s; break; }
                    }
                    if (text != null) items.Add(new DataItem { key = f.Key, text = text });
                }
            return "{\"ok\":true,\"values\":" + JsonUtility.ToJson(new DataItems { items = items.ToArray() }) + "}";
        }
        private static string SetData(ZNetView nv, string valuesJson)
        {
            string prefab = global::Utils.GetPrefabName(nv.gameObject);
            var w = JsonUtility.FromJson<DataItems>(valuesJson ?? "{}");
            var dp = new VADataPreset { name = "(Studio)", Prefab = prefab };
            if (w?.items != null) foreach (var it in w.items) dp.Values.Add(new VADataPresetValue { Key = it.key, Text = it.text });
            if (dp.Values.Count == 0) return No("no values");
            string err = VADataPresetStore.Parse(dp, out var values);
            if (err != null) return No(err);
            if (!nv.IsOwner()) nv.ClaimOwnership();
            VAExtraDataStamp.WriteValues(nv.GetZDO(), values);
            return Ok($"{values.Count} data field(s) written to the piece");
        }
    }
}
