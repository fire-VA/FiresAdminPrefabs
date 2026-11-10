using System;
using System.Linq;
using System.Text;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Api
{
    // 1.1.343 ([lead] step 5 rest): the build menu's monster and clutter rows as FapPresets kinds, on the same RegisterKind path as
    // 1.1.342's. monster: VAMonsterPresetStore + VAMonsterConfigApplier.StampPresetAndRefresh (the queued placement's own stamp) on a
    // placed creature, VAMonsterConfigQueue for a prefab. clutter: VAClutterPresetStore + SetActive (per clutter prefab, ClutterSystem
    // re-injected live, as the "Presets for <clutter>" submenu). Headless on the dedi. Add-only.
    public static partial class FapPresets
    {
        private static void RegisterMore()
        {
            RegisterKind("monster", "Monster presets...", new MonsterKind());
            RegisterKind("clutter", "Configure clutter...", new ClutterKind());
        }

        // ---- monster: VAMonsterPreset (drops, fields, the creature it is for) ----
        [Serializable] private sealed class MonsterValues { public string itemAmount; public string[] ints, floats, strings, vecs; public ChestItem[] items; }
        private static string MonsterJson(VAMonsterPreset p) => JsonUtility.ToJson(new MonsterValues
        {
            itemAmount = p.itemAmount, ints = p.ints.ToArray(), floats = p.floats.ToArray(), strings = p.strings.ToArray(), vecs = p.vecs.ToArray(),
            items = p.items.Select(i => new ChestItem { prefab = i.prefab, chance = i.chance, stack = i.stack, quality = i.quality, variant = i.variant }).ToArray(),
        });
        private static VAMonsterPreset MonsterFrom(string name, string creature, string valuesJson)
        {
            var v = JsonUtility.FromJson<MonsterValues>(valuesJson ?? "{}") ?? new MonsterValues();
            var p = new VAMonsterPreset { name = name ?? "", itemAmount = v.itemAmount };
            if (v.ints != null) p.ints.AddRange(v.ints); if (v.floats != null) p.floats.AddRange(v.floats); if (v.strings != null) p.strings.AddRange(v.strings); if (v.vecs != null) p.vecs.AddRange(v.vecs);
            if (v.items != null) foreach (var i in v.items) p.items.Add(new VAItemEntry { prefab = i.prefab ?? "", chance = i.chance, stack = i.stack, quality = i.quality, variant = i.variant });
            if (!string.IsNullOrEmpty(creature)) p.Creature = creature;
            return p;
        }
        public sealed class MonsterKind
        {
            public bool Has(string zdoid) => PieceOf(zdoid)?.GetComponent<Character>() is Character c && !c.IsPlayer();
            public string Fields(string prefab) => "[{\"key\":\"items\",\"label\":\"Drops\",\"type\":\"items\"},{\"key\":\"itemAmount\",\"label\":\"Drop amount\",\"type\":\"text\"},"
                + "{\"key\":\"ints\",\"label\":\"ints (key, value)\",\"type\":\"lines\"},{\"key\":\"floats\",\"label\":\"floats (key, value)\",\"type\":\"lines\"},{\"key\":\"strings\",\"label\":\"strings (key, value)\",\"type\":\"lines\"},{\"key\":\"vecs\",\"label\":\"vecs (key, value)\",\"type\":\"lines\"}]";
            public string List(string prefab)
            {
                var sb = new StringBuilder("[");
                foreach (var p in VAMonsterPresetStore.All.Values.Where(p => p != null && (string.IsNullOrEmpty(prefab) || p.Creature == prefab)).OrderBy(p => p.name, StringComparer.Ordinal))
                { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(p.name)).Append(",\"values\":").Append(MonsterJson(p)).Append('}'); }
                return sb.Append(']').ToString();
            }
            public string Read(string zdoid)
            {
                var nv = PieceOf(zdoid); if (nv == null) return No("that creature is not loaded here");
                VAMonsterConfigQueue.TryGetQueuedPresetName(PrefabOf(nv), out string q);
                return "{\"ok\":true,\"values\":null,\"queued\":" + Q(q ?? "") + ",\"report\":\"a placed creature keeps no preset name; Apply stamps one\"}";
            }
            public string Apply(string zdoidOrPrefab, string preset)
            {
                if (!AdminOk(out string why)) return No(why);
                if (!VAMonsterPresetStore.TryGet(preset, out var p)) return No($"no monster preset '{preset}'");
                if ((zdoidOrPrefab ?? "").IndexOf(':') < 0) { VAMonsterConfigQueue.EnqueueForNextPlacement(zdoidOrPrefab, preset); return Ok($"monster preset '{preset}' queued for the next {zdoidOrPrefab}"); }
                var nv = PieceOf(zdoidOrPrefab); if (nv == null || nv.GetComponent<Character>() == null) return No("not a loaded creature");
                if (!nv.IsOwner()) nv.ClaimOwnership();
                Patches.VAMonsterConfigApplier.StampPresetAndRefresh(p, nv.gameObject);
                return Ok($"monster preset '{preset}' stamped on the creature");
            }
            public string Set(string zdoid, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                var nv = PieceOf(zdoid); if (nv == null || nv.GetComponent<Character>() == null) return No("not a loaded creature");
                if (!nv.IsOwner()) nv.ClaimOwnership();
                Patches.VAMonsterConfigApplier.StampPresetAndRefresh(MonsterFrom("", PrefabOf(nv), valuesJson), nv.gameObject);
                return Ok("monster values stamped on the creature");
            }
            public string Save(string name, string prefab, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                if (string.IsNullOrWhiteSpace(name)) return No("a name is needed");
                return VAMonsterPresetStore.Save(MonsterFrom(name, prefab, valuesJson)) ? Ok($"monster preset '{name}' saved") : No("could not save (see the log)");
            }
            public string Delete(string name) { if (!AdminOk(out string why)) return No(why); return VAMonsterPresetStore.Delete(name) ? Ok($"monster preset '{name}' deleted") : No($"no monster preset '{name}'"); }
        }

        // ---- clutter: VAClutterPreset, ONE active per clutter prefab (world-wide, as in game) ----
        private static string ClutterOf(ZNetView nv)
        {
            string prefab = PrefabOf(nv);
            if (VAClutterStandinFactory.IsStandinName(prefab)) return VAClutterStandinFactory.ResolveRealNameFromStandin(nv.gameObject);
            return VAVanillaPieceManager.IsClutterPrefab_Public(nv.gameObject) ? prefab : null;
        }
        public sealed class ClutterKind
        {
            public bool Has(string zdoid) { var nv = PieceOf(zdoid); return nv != null && !string.IsNullOrEmpty(ClutterOf(nv)); }
            public string Fields(string prefab) => "[" + string.Join(",", typeof(VAClutterPreset).GetFields().Where(f => f.Name != "name" && f.Name != "clutterPrefab").Select(f => "{\"key\":" + Q(f.Name) + ",\"label\":" + Q(f.Name) + ",\"type\":" + Q(f.FieldType == typeof(bool) ? "bool" : f.FieldType == typeof(string) ? "text" : "float") + "}")) + "]";
            public string List(string prefab)
            {
                var sb = new StringBuilder("[");
                foreach (var p in VAClutterPresetStore.All.Values.Where(p => p != null && (string.IsNullOrEmpty(prefab) || p.clutterPrefab == prefab)).OrderBy(p => p.name, StringComparer.Ordinal))
                { if (sb.Length > 1) sb.Append(','); sb.Append("{\"name\":").Append(Q(p.name)).Append(",\"values\":").Append(JsonUtility.ToJson(p)).Append('}'); }
                return sb.Append(']').ToString();
            }
            public string Read(string zdoid)
            {
                var nv = PieceOf(zdoid); string c = nv != null ? ClutterOf(nv) : null; if (c == null) return No("not clutter");
                string active = VAClutterPresetStore.GetActiveNameFor(c);
                return VAClutterPresetStore.TryGet(active ?? "", out var p) ? "{\"ok\":true,\"preset\":" + Q(active) + ",\"values\":" + JsonUtility.ToJson(p) + "}" : "{\"ok\":true,\"preset\":\"\",\"values\":null,\"report\":\"vanilla clutter (no active preset)\"}";
            }
            public string Apply(string zdoidOrPrefab, string preset)
            {
                if (!AdminOk(out string why)) return No(why);
                var nv = (zdoidOrPrefab ?? "").IndexOf(':') > 0 ? PieceOf(zdoidOrPrefab) : null;
                string c = nv != null ? ClutterOf(nv) : zdoidOrPrefab;
                if (string.IsNullOrEmpty(c)) return No("not clutter");
                if (!string.IsNullOrEmpty(preset) && !VAClutterPresetStore.TryGet(preset, out _)) return No($"no clutter preset '{preset}'");
                return VAClutterPresetStore.SetActive(c, preset) ? Ok(string.IsNullOrEmpty(preset) ? $"{c}: back to vanilla clutter" : $"{c}: clutter preset '{preset}' active (world-wide)") : No("could not set (see the log)");
            }
            public string Set(string zdoid, string valuesJson) => No("clutter is per clutter type, world-wide: Save a preset, then Apply it");
            public string Save(string name, string prefab, string valuesJson)
            {
                if (!AdminOk(out string why)) return No(why);
                if (string.IsNullOrWhiteSpace(name)) return No("a name is needed");
                var p = new VAClutterPreset(); JsonUtility.FromJsonOverwrite(valuesJson ?? "{}", p); p.name = name; if (!string.IsNullOrEmpty(prefab)) p.clutterPrefab = prefab;
                return VAClutterPresetStore.Save(p) ? Ok($"clutter preset '{name}' saved") : No("could not save (see the log)");
            }
            public string Delete(string name) { if (!AdminOk(out string why)) return No(why); return VAClutterPresetStore.Delete(name) ? Ok($"clutter preset '{name}' deleted") : No($"no clutter preset '{name}'"); }
        }
    }
}
