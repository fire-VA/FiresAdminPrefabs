using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Api
{
    // 1.1.342 ([lead] steps 4: render / chest / itemstand, 5: shader): the build menu's other right-click rows as FapPresets kinds,
    // each one a handler on 1.1.341's RegisterKind path (one router, the same shape [ghost]'s kinds use). Every call runs the in-game
    // menu's own code: VAFapPieceOverrides.SetDistance / SetShader (per prefab, live instances re-applied), VAChestPresetStore +
    // VAChestConfigApplier.ApplyToPlaced / VAChestConfigQueue (chest), VAItemStandPresetStore + VAItemStandQueue.Apply / FromZdo
    // (item stand). Headless on the dedi. Monster / clutter = 1.1.343. Add-only.
    public static partial class FapPresets
    {
        static FapPresets()
        {
            RegisterKind("render", "Distance render override...", new RenderKind());
            RegisterKind("shader", "Shader override...", new ShaderKind());
            RegisterKind("chest", "Configure container...", new ChestKind());
            RegisterKind("itemstand", "Configure item stand...", new StandKind());
            RegisterMore();   // 1.1.343: monster, clutter
        }

        private static ZNetView PieceOf(string zdoid) => Piece(zdoid, out _);
        private static string PrefabOf(ZNetView nv) => global::Utils.GetPrefabName(nv.gameObject);

        // ---- render: the per-prefab distance override (the menu's preset distances; 0 = off, vanilla streaming) ----
        public sealed class RenderKind
        {
            private static readonly float[] Presets = { 0f, 250f, 500f, 750f, 1000f, 1500f };
            public bool Has(string zdoid) => PieceOf(zdoid)?.GetComponent<global::Piece>() != null;
            public string Fields(string prefab) => "[{\"key\":\"distance\",\"label\":\"Render up to (m), 0 = off\",\"type\":\"float\",\"options\":[" + string.Join(",", Presets.Select(p => p.ToString(Inv))) + "]}]";
            public string List(string prefab) => "[" + string.Join(",", Presets.Select(p => "{\"name\":" + Q(p <= 0 ? "Off" : ((int)p) + "m") + ",\"values\":{\"distance\":" + p.ToString(Inv) + "}}")) + "]";
            public string Read(string zdoid)
            {
                var nv = PieceOf(zdoid); if (nv == null) return No("that piece is not loaded here");
                return "{\"ok\":true,\"values\":{\"distance\":" + (VAFapPieceOverrides.TryGetDistance(PrefabOf(nv), out float d) ? d : 0f).ToString(Inv) + "}}";
            }
            public string Apply(string zdoid, string preset) => Set(zdoid, "{\"distance\":" + (preset == "Off" ? "0" : preset.TrimEnd('m')) + "}");
            public string Set(string zdoid, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                var nv = PieceOf(zdoid); if (nv == null) return No("that piece is not loaded here");
                var v = JsonUtility.FromJson<RenderValues>(valuesJson ?? "{}");
                string prefab = PrefabOf(nv);
                VAFapPieceOverrides.SetDistance(prefab, Mathf.Max(0f, v.distance));
                return Ok(v.distance <= 0f ? $"cleared the distance override for {prefab}" : $"{prefab} renders up to {(int)v.distance}m (every {prefab})");
            }
            public string Save(string name, string prefab, string valuesJson) => No("render distances are a fixed list (as in game)");
            public string Delete(string name) => No("render distances are a fixed list (as in game)");
        }
        [Serializable] private sealed class RenderValues { public float distance; }

        // ---- shader: the per-prefab shader override (every live instance) ----
        public sealed class ShaderKind
        {
            public bool Has(string zdoid) => PieceOf(zdoid)?.GetComponent<global::Piece>() != null;
            public string Fields(string prefab) => "[{\"key\":\"shader\",\"label\":\"Shader (empty = the prefab's own)\",\"type\":\"text\",\"options\":[" + string.Join(",", VAFapPieceShaderOverrider.ListAvailableShaders().Select(Q)) + "]}]";
            public string List(string prefab) => "[" + string.Join(",", VAFapPieceShaderOverrider.ListAvailableShaders().Select(s => "{\"name\":" + Q(s) + ",\"values\":{\"shader\":" + Q(s) + "}}")) + "]";
            public string Read(string zdoid)
            {
                var nv = PieceOf(zdoid); if (nv == null) return No("that piece is not loaded here");
                return "{\"ok\":true,\"values\":{\"shader\":" + Q(VAFapPieceOverrides.TryGetShader(PrefabOf(nv), out string s) ? s : "") + "}}";
            }
            public string Apply(string zdoid, string preset) => Set(zdoid, "{\"shader\":" + Q(preset) + "}");
            public string Set(string zdoid, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                var nv = PieceOf(zdoid); if (nv == null) return No("that piece is not loaded here");
                var v = JsonUtility.FromJson<ShaderValues>(valuesJson ?? "{}");
                string prefab = PrefabOf(nv), sh = string.IsNullOrEmpty(v.shader) ? null : v.shader;
                if (sh != null && !VAFapPieceShaderOverrider.ListAvailableShaders().Contains(sh)) return No($"no shader '{sh}'");
                VAFapPieceOverrides.SetShader(prefab, sh);
                return Ok(sh == null ? $"cleared the shader override for {prefab}" : $"{prefab} shader -> {sh} (every {prefab})");
            }
            public string Save(string name, string prefab, string valuesJson) => No("shaders are the game's list");
            public string Delete(string name) => No("shaders are the game's list");
        }
        [Serializable] private sealed class ShaderValues { public string shader; }

        // ---- chest: VAChestPreset (the Configure container window's preset) ----
        [Serializable] private sealed class ChestItem { public string prefab; public float chance = 1f; public string stack, quality, variant; }
        [Serializable] private sealed class ChestValues { public string itemAmount, containerSize; public string[] ints, floats, strings, bools; public ChestItem[] items; }
        private static string ChestJson(VAChestPreset p) => JsonUtility.ToJson(new ChestValues
        {
            itemAmount = p.itemAmount, containerSize = p.containerSize, ints = p.ints.ToArray(), floats = p.floats.ToArray(), strings = p.strings.ToArray(), bools = p.bools.ToArray(),
            items = p.items.Select(i => new ChestItem { prefab = i.prefab, chance = i.chance, stack = i.stack, quality = i.quality, variant = i.variant }).ToArray(),
        });
        private static VAChestPreset ChestFrom(string name, string prefab, string valuesJson)
        {
            var v = JsonUtility.FromJson<ChestValues>(valuesJson ?? "{}") ?? new ChestValues();
            var p = new VAChestPreset { name = name ?? "", itemAmount = v.itemAmount, containerSize = v.containerSize };
            if (v.ints != null) p.ints.AddRange(v.ints); if (v.floats != null) p.floats.AddRange(v.floats); if (v.strings != null) p.strings.AddRange(v.strings); if (v.bools != null) p.bools.AddRange(v.bools);
            if (v.items != null) foreach (var i in v.items) p.items.Add(new VAItemEntry { prefab = i.prefab ?? "", chance = i.chance, stack = i.stack, quality = i.quality, variant = i.variant });
            if (!string.IsNullOrEmpty(prefab)) p.ContainerPrefab = prefab;
            return p;
        }
        public sealed class ChestKind
        {
            public bool Has(string zdoid) => PieceOf(zdoid)?.GetComponent<Container>() != null;
            public string Fields(string prefab) => "[{\"key\":\"items\",\"label\":\"Items\",\"type\":\"items\"},{\"key\":\"itemAmount\",\"label\":\"Item amount\",\"type\":\"text\"},{\"key\":\"containerSize\",\"label\":\"Container size\",\"type\":\"text\"},"
                + "{\"key\":\"ints\",\"label\":\"ints (key, value)\",\"type\":\"lines\"},{\"key\":\"floats\",\"label\":\"floats (key, value)\",\"type\":\"lines\"},{\"key\":\"strings\",\"label\":\"strings (key, value)\",\"type\":\"lines\"},{\"key\":\"bools\",\"label\":\"bools (key, value)\",\"type\":\"lines\"}]";
            public string List(string prefab)
            {
                var sb = new StringBuilder("[");
                foreach (var p in VAChestPresetStore.All.Values.Where(p => p != null && (string.IsNullOrEmpty(prefab) || p.ContainerPrefab == prefab || string.IsNullOrEmpty(p.ContainerPrefab))).OrderBy(p => p.name, StringComparer.Ordinal))
                { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(p.name)).Append(",\"values\":").Append(ChestJson(p)).Append('}'); }
                return sb.Append(']').ToString();
            }
            public string Read(string zdoid)
            {
                var nv = PieceOf(zdoid); if (nv == null) return No("that piece is not loaded here");
                string name = nv.GetZDO().GetString("va_chest_preset", "");
                return VAChestPresetStore.TryGet(name, out var p) ? "{\"ok\":true,\"preset\":" + Q(name) + ",\"values\":" + ChestJson(p) + "}" : "{\"ok\":true,\"preset\":\"\",\"values\":null,\"report\":\"no FAP chest preset on this container\"}";
            }
            // a placed chest: the placement's own apply (contents filled as at placement); a prefab: queued for the next placement
            public string Apply(string zdoidOrPrefab, string preset)
            {
                if (!AdminOk(out string why)) return No(why);
                if (!VAChestPresetStore.TryGet(preset, out var p)) return No($"no chest preset '{preset}'");
                if ((zdoidOrPrefab ?? "").IndexOf(':') < 0) { VAChestConfigQueue.EnqueueForNextPlacement(zdoidOrPrefab, preset); return Ok($"chest preset '{preset}' queued for the next {zdoidOrPrefab}"); }
                var nv = PieceOf(zdoidOrPrefab); var c = nv != null ? nv.GetComponent<Container>() : null; if (c == null) return No("not a loaded container");
                if (!nv.IsOwner()) nv.ClaimOwnership();
                Patches.VAChestConfigApplier.ApplyToPlaced(p, c, fillInventory: true);
                return Ok($"chest preset '{preset}' applied to the container");
            }
            public string Set(string zdoid, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                var nv = PieceOf(zdoid); var c = nv != null ? nv.GetComponent<Container>() : null; if (c == null) return No("not a loaded container");
                if (!nv.IsOwner()) nv.ClaimOwnership();
                Patches.VAChestConfigApplier.ApplyToPlaced(ChestFrom("", PrefabOf(nv), valuesJson), c, fillInventory: true);
                return Ok("chest values applied to the container");
            }
            public string Save(string name, string prefab, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                if (string.IsNullOrWhiteSpace(name)) return No("a name is needed");
                return VAChestPresetStore.Save(ChestFrom(name, prefab, valuesJson)) ? Ok($"chest preset '{name}' saved") : No("could not save (see the log)");
            }
            public string Delete(string name) { if (!AdminOk(out string why)) return No(why); return VAChestPresetStore.Delete(name) ? Ok($"chest preset '{name}' deleted") : No($"no chest preset '{name}'"); }
        }

        // ---- itemstand: VAItemStandPreset (item hung, decorative, refill) ----
        [Serializable] private sealed class StandValues { public string Item = ""; public int Quality = 1, Variant; public bool NoInteract; public float RespawnSeconds, RespawnSecondsMax, RefillClearMetres; }
        private static string StandJson(VAItemStandPreset p) => JsonUtility.ToJson(new StandValues { Item = p.Item, Quality = p.Quality, Variant = p.Variant, NoInteract = p.NoInteract, RespawnSeconds = p.RespawnSeconds, RespawnSecondsMax = p.RespawnSecondsMax, RefillClearMetres = p.RefillClearMetres });
        private static VAItemStandPreset StandFrom(string name, string stand, string valuesJson)
        {
            var v = JsonUtility.FromJson<StandValues>(valuesJson ?? "{}") ?? new StandValues();
            return new VAItemStandPreset { name = name ?? "", StandPrefab = stand ?? "", Item = v.Item ?? "", Quality = Math.Max(1, v.Quality), Variant = v.Variant, NoInteract = v.NoInteract, RespawnSeconds = v.RespawnSeconds, RespawnSecondsMax = v.RespawnSecondsMax, RefillClearMetres = v.RefillClearMetres };
        }
        public sealed class StandKind
        {
            public bool Has(string zdoid) => PieceOf(zdoid)?.GetComponent<ItemStand>() != null;
            public string Fields(string prefab) => "[" + string.Join(",", typeof(StandValues).GetFields().Select(f => "{\"key\":" + Q(f.Name) + ",\"label\":" + Q(f.Name) + ",\"type\":" + Q(f.FieldType == typeof(bool) ? "bool" : f.FieldType == typeof(string) ? "text" : "float") + "}")) + "]";
            public string List(string prefab)
            {
                var sb = new StringBuilder("[");
                foreach (var p in VAItemStandPresetStore.All.Values.Where(p => p != null && (string.IsNullOrEmpty(prefab) || p.StandPrefab == prefab)).OrderBy(p => p.name, StringComparer.Ordinal))
                { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(p.name)).Append(",\"values\":").Append(StandJson(p)).Append('}'); }
                return sb.Append(']').ToString();
            }
            public string Read(string zdoid)
            {
                var nv = PieceOf(zdoid); if (nv == null || nv.GetComponent<ItemStand>() == null) return No("not a loaded item stand");
                var p = VAItemStandApplier.FromZdo(nv.GetZDO());
                return "{\"ok\":true,\"preset\":" + Q(p.name) + ",\"values\":" + StandJson(p) + "}";
            }
            public string Apply(string zdoidOrPrefab, string preset)
            {
                if (!AdminOk(out string why)) return No(why);
                if (!VAItemStandPresetStore.TryGet(preset, out var p)) return No($"no item stand preset '{preset}'");
                if ((zdoidOrPrefab ?? "").IndexOf(':') < 0) { VAItemStandQueue.Enqueue(zdoidOrPrefab, preset); return Ok($"item stand preset '{preset}' queued for the next {zdoidOrPrefab}"); }
                return ApplyTo(zdoidOrPrefab, p) ?? Ok($"item stand preset '{preset}' applied");
            }
            public string Set(string zdoid, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                var nv = PieceOf(zdoid);
                return ApplyTo(zdoid, StandFrom(nv != null ? nv.GetZDO().GetString(VAItemStandApplier.KeyPreset, "") : "", nv != null ? PrefabOf(nv) : "", valuesJson)) ?? Ok("item stand values applied");
            }
            // the editor's own apply on a placed stand: the ZDO keys, then the item hung (HangItem)
            private static string ApplyTo(string zdoid, VAItemStandPreset p)
            {
                var nv = PieceOf(zdoid); var s = nv != null ? nv.GetComponent<ItemStand>() : null; if (s == null) return No("not a loaded item stand");
                if (!nv.IsOwner()) nv.ClaimOwnership();
                if (!VAItemStandApplier.Apply(p, s)) return No("the stand did not take the values (see the log)");
                if (!string.IsNullOrEmpty(p.Item)) VAItemStandApplier.HangItem(p, s, nv);
                return null;
            }
            public string Save(string name, string prefab, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                if (string.IsNullOrWhiteSpace(name)) return No("a name is needed");
                return VAItemStandPresetStore.Save(StandFrom(name, prefab, valuesJson)) ? Ok($"item stand preset '{name}' saved") : No("could not save (see the log)");
            }
            public string Delete(string name) { if (!AdminOk(out string why)) return No(why); return VAItemStandPresetStore.Delete(name) ? Ok($"item stand preset '{name}' deleted") : No($"no item stand preset '{name}'"); }
        }
    }
}
