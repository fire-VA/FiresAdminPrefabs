using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.168 (Fire 10-06: "item stand presets like containers"): a preset hangs an item on a preloaded item stand when it is placed,
    // and can make the stand decorative (no taking, no hanging). Presets live in config/data/data_FAP_itemstands.json (the game
    // folder); the hammer's right-click on an item stand piece offers them, and a picked preset stays queued for every placement
    // until another (or "No preset") is picked, like chests and monsters (1.1.167). Each placed stand carries its preset on its ZDO,
    // so the stand needs no file on other peers.
    public sealed class VAItemStandPreset
    {
        public string name = "";
        public string StandPrefab = "";   // the item stand piece it is for (itemstand, itemstandh, ...)
        public string Item = "";          // the item prefab hung on it
        public int Quality = 1;
        public int Variant;
        public bool NoInteract;           // decorative: E does nothing, no item can be taken or hung
        public float RespawnSeconds;      // refill after the item is taken, in seconds (0 = never; the editor makes that an explicit "Refills: OFF")
        public float RespawnSecondsMax;
        public float RefillClearMetres;   // 1.1.178 (Fire 10-07): refill only when no player is within this many metres (0 = refill with players right there)
    }

    public static class VAItemStandPresetStore
    {
        public static string DataFilePath => Path.Combine(VAChestPresetStore.DataDir, "data_FAP_itemstands.json");

        private static readonly Dictionary<string, VAItemStandPreset> _byName = new Dictionary<string, VAItemStandPreset>(StringComparer.Ordinal);
        private static bool _loaded;

        public static IReadOnlyDictionary<string, VAItemStandPreset> All { get { EnsureLoaded(); return _byName; } }

        public static bool TryGet(string name, out VAItemStandPreset preset)
        {
            EnsureLoaded();
            preset = null;
            return !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out preset);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(DataFilePath)) return;
                var list = JsonConvert.DeserializeObject<List<VAItemStandPreset>>(File.ReadAllText(DataFilePath)) ?? new List<VAItemStandPreset>();
                foreach (var p in list) if (p != null && !string.IsNullOrEmpty(p.name)) _byName[p.name] = p;
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: item stand presets could not be read: {ex.Message}"); }
        }

        public static bool Save(VAItemStandPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.name)) return false;
            EnsureLoaded();
            _byName[preset.name] = preset;
            return Write();
        }

        public static bool Delete(string name)
        {
            EnsureLoaded();
            return _byName.Remove(name ?? "") && Write();
        }

        private static bool Write()
        {
            try
            {
                Directory.CreateDirectory(VAChestPresetStore.DataDir);
                string tmp = DataFilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_byName.Values.OrderBy(p => p.name).ToList(), Formatting.Indented));
                if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
                File.Move(tmp, DataFilePath);
                VAItemStandEwp.Write();   // the EWP data entries follow the presets (when that refill is on)
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: item stand presets could not be saved: {ex.Message}"); return false; }
        }
    }

    // The preset queued per stand prefab: kept for every placement until changed (1.1.167's rule).
    public static class VAItemStandQueue
    {
        private static readonly Dictionary<string, string> _queued = new Dictionary<string, string>(StringComparer.Ordinal);

        public static void Enqueue(string standPrefab, string presetName)
        {
            if (string.IsNullOrEmpty(standPrefab)) return;
            if (string.IsNullOrEmpty(presetName)) _queued.Remove(standPrefab); else _queued[standPrefab] = presetName;
        }

        public static void Clear(string standPrefab) { if (!string.IsNullOrEmpty(standPrefab)) _queued.Remove(standPrefab); }

        public static bool TryGetQueuedPresetName(string standPrefab, out string presetName)
        {
            presetName = null;
            return !string.IsNullOrEmpty(standPrefab) && _queued.TryGetValue(standPrefab, out presetName) && !string.IsNullOrEmpty(presetName);
        }

        public static VAItemStandPreset Current(string standPrefab)
        {
            if (!TryGetQueuedPresetName(standPrefab, out var name)) return null;
            if (VAItemStandPresetStore.TryGet(name, out var preset)) return preset;
            _queued.Remove(standPrefab);
            return null;
        }
    }

    // Placement: the stand's placer stamps the preset and hangs the item, as its owner (vanilla's own ZDO fields + SetVisualItem).
    // A stand marked decorative refuses E and hanging, for everyone.
    public static class VAItemStandApplier
    {
        public const string KeyPreset = "va_stand_preset", KeyNoInteract = "va_stand_nointeract";
        public const string KeyRespawn = "va_stand_respawn_seconds", KeyRespawnMax = "va_stand_respawn_seconds_max";

        public static bool Apply(VAItemStandPreset preset, ItemStand stand)
        {
            var view = stand != null ? stand.GetComponent<ZNetView>() : null;
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || preset == null) return false;
            zdo.Set(KeyPreset, preset.name);
            zdo.Set(KeyNoInteract, preset.NoInteract);
            zdo.Set(KeyRespawn, preset.RespawnSeconds);
            zdo.Set(KeyRespawnMax, preset.RespawnSecondsMax);
            // What a refill hangs, on the stand itself (any peer that owns it later can refill it without the preset file).
            zdo.Set(KeyItem, preset.Item ?? "");
            zdo.Set(KeyQuality, Mathf.Max(1, preset.Quality));
            zdo.Set(KeyVariant, Mathf.Max(0, preset.Variant));
            // 1.1.178 ([generator]): hang first, then track, so Core's first tick sees the stand full (no false "emptied" line).
            bool hung = HangItem(preset, stand, view);
            // 1.1.181 (Fire 10-07): [Item stands] UseEwpForRefill ON (with EWP installed) hands the refill to EWP even when Core is
            // there - the stand gets the int key EWP's rule filters on. Else Core's loot respawn; else FAP's own watcher.
            // 1.1.180 ([generator] 10-07): a stand whose item did not hang is never tracked (Core would raise RefillDue for it forever).
            bool ewp = hung && preset.RespawnSeconds > 0f && VAItemStandEwp.Active;
            zdo.Set(KeyEwp, ewp);
            zdo.Set(VAItemStandEwp.KeyStand, ewp ? VAItemStandEwp.StandId(preset) : 0);
            if (ewp) zdo.Set(KeyEwpData, VAItemStandEwp.DataName(preset));
            bool core = hung && !ewp && VACoreLoot.TrackStand(zdo, preset.RespawnSeconds, preset.RespawnSecondsMax, Mathf.Max(0f, preset.RefillClearMetres));
            zdo.Set(KeyCore, core);
            if (!hung)
            {
                // Not a preset stand after all (e.g. a preset made with VanillaFriendly off, placed with it on): no preset key, so
                // FAP's own refill watcher leaves it alone too - never an empty stand that keeps trying to refill.
                zdo.Set(KeyPreset, "");
                try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"Item stand preset '{preset.name}': {preset.Item} can't hang on this stand"); } catch { }
            }
            return hung;
        }

        public const string KeyItem = "va_stand_item", KeyQuality = "va_stand_quality", KeyVariant = "va_stand_variant";
        public const string KeyEwp = "va_stand_ewp", KeyEwpData = "va_stand_data", KeyRefillAt = "va_stand_refill_at", KeyCore = "va_stand_core";

        // The preset as the stand's ZDO carries it (for a refill).
        public static VAItemStandPreset FromZdo(ZDO zdo) => new VAItemStandPreset
        {
            name = zdo.GetString(KeyPreset), Item = zdo.GetString(KeyItem), Quality = zdo.GetInt(KeyQuality, 1), Variant = zdo.GetInt(KeyVariant),
            NoInteract = zdo.GetBool(KeyNoInteract), RespawnSeconds = zdo.GetFloat(KeyRespawn), RespawnSecondsMax = zdo.GetFloat(KeyRespawnMax),
        };

        public static bool HangItem(VAItemStandPreset preset, ItemStand stand, ZNetView view)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(preset.Item) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) { Debug.LogWarning($"{VaPieces.PluginName}: item stand preset '{preset.name}': no item prefab '{preset.Item}'"); return false; }
            // 1.1.172 (the lead 15:3x: NullReferenceException in ItemStand.GetAttachPrefab <- CanAttach): a prefab's own m_itemData has
            // no m_dropPrefab (vanilla sets it on a spawned drop), and CanAttach looks the "attach" child up on it. The clone carries it.
            var data = drop.m_itemData.Clone();
            data.m_dropPrefab = prefab; data.m_stack = 1;
            // 1.1.180 (Fire 10-07: 'blobstand' BlobVial NOT hung): FAP's stand-ins hang anything with an "attach" model (VAItemStandSpawner
            // writes the ZDO the same way); vanilla's CanAttach adds the stand's item-type list, which refused BlobVial on itemstandh. Same rule here.
            if (!CanHold(stand, data, out string why)) { Debug.LogWarning($"{VaPieces.PluginName}: item stand preset '{preset.name}': {stand.name} can't hold {preset.Item} ({why})"); return false; }
            data.m_quality = Mathf.Max(1, preset.Quality); data.m_variant = Mathf.Max(0, preset.Variant);
            data.m_durability = data.GetMaxDurability();
            int hash = prefab.name.GetStableHashCode();
            var zdo = view.GetZDO();
            zdo.Set(ZDOVars.s_item, hash);
            ItemDrop.SaveToZDO(data, zdo);
            view.InvokeRPC(ZNetView.Everybody, "SetVisualItem", hash, data.m_variant, data.m_quality, zdo.GetInt(ZDOVars.s_type));
            return true;
        }

        // 1.1.180 (Fire 10-07): with [General] VanillaFriendly ON, vanilla's own rule (ItemStand.CanAttach: an attach model + the stand's
        // supported / unsupported items and item types), so a vanilla client could hang it too; OFF, FAP's stand-in rule (an attach model).
        public static bool CanHold(ItemStand stand, ItemDrop.ItemData data, out string why)
        {
            why = null;
            bool vanillaFriendly = VerdantsAscent.Managers.ConfigManager.Instance.configVanillaFriendly?.Value ?? false;
            if (data?.m_dropPrefab == null) { why = "it is not an item"; return false; }
            // 1.1.183: FAP draws an item without a stand model from its world model; a vanilla client can't, so VanillaFriendly needs a real one.
            bool standModel = VAItemStandRegistry.HasStandModel(data.m_dropPrefab);
            if (!standModel && (vanillaFriendly || !VAItemStandFallbackModel.HasWorldModel(data.m_dropPrefab)))
            { why = vanillaFriendly ? "VanillaFriendly is on and it has no stand model a vanilla client can draw" : "it has nothing to draw"; return false; }
            if (!vanillaFriendly || stand == null || s_canAttach == null) return true;
            if ((bool)s_canAttach.Invoke(stand, new object[] { data })) return true;
            why = "VanillaFriendly is on and vanilla's " + global::Utils.GetPrefabName(stand.gameObject) + " doesn't take that item";
            return false;
        }

        private static readonly System.Reflection.MethodInfo s_canAttach = AccessTools.Method(typeof(ItemStand), "CanAttach", new[] { typeof(ItemDrop.ItemData) });

        public static bool IsDecorative(ItemStand stand)
        {
            var view = stand != null ? stand.GetComponent<ZNetView>() : null;
            return view != null && view.IsValid() && view.GetZDO().GetBool(KeyNoInteract);
        }
    }

    [HarmonyPatch]
    public static class VAItemStandPatches
    {
        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator)), HarmonyPostfix]
        private static void Piece_SetCreator_Postfix(Piece __instance, long uid)
        {
            if (__instance == null || uid == 0) return;   // 0 = world generation
            var stand = __instance.GetComponent<ItemStand>();
            if (stand == null) return;
            var preset = VAItemStandQueue.Current(global::Utils.GetPrefabName(__instance.gameObject));
            if (preset == null) return;
            bool hung = VAItemStandApplier.Apply(preset, stand);
            Debug.Log($"{VaPieces.PluginName}: item stand preset '{preset.name}' stamped on {__instance.name}: {preset.Item} {(hung ? "hung" : "NOT hung")}{(preset.NoInteract ? ", decorative" : "")}");
        }

        // A preset stand gets the refill watcher (FAP's own refill; idle when the stand is on EWP's rule or has no timer).
        [HarmonyPatch(typeof(ItemStand), "Awake"), HarmonyPostfix]
        private static void ItemStand_Awake_Postfix(ItemStand __instance)
        {
            if (__instance != null && __instance.GetComponent<VAItemStandRefill>() == null) __instance.gameObject.AddComponent<VAItemStandRefill>();
        }

        [HarmonyPatch(typeof(ItemStand), nameof(ItemStand.Interact)), HarmonyPrefix]
        private static bool ItemStand_Interact_Prefix(ItemStand __instance, ref bool __result)
        {
            if (!VAItemStandApplier.IsDecorative(__instance)) return true;
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(ItemStand), nameof(ItemStand.UseItem)), HarmonyPrefix]
        private static bool ItemStand_UseItem_Prefix(ItemStand __instance, ref bool __result)
        {
            if (!VAItemStandApplier.IsDecorative(__instance)) return true;
            __result = false;
            return false;
        }
    }

    // FAP's own refill (Fire 10-06 Q1: the default, and whenever EWP is off or absent): the stand's owner notes when its preset item
    // was taken (ZNet time on the ZDO, so an ownership change keeps the clock) and hangs it back once the timer (min..max) is up.
    // Only peers that have the stand loaded can own it, so a refill lands when someone is near, like any vanilla object.
    public sealed class VAItemStandRefill : MonoBehaviour
    {
        private const float CheckSeconds = 0.25f;   // 1.1.184 (Fire 10-07: "3 seconds is more like 15"): within a quarter second of the set time (was 2 s)
        private ItemStand _stand;
        private ZNetView _view;

        private void Awake()
        {
            _stand = GetComponent<ItemStand>();
            _view = GetComponent<ZNetView>();
            InvokeRepeating(nameof(Check), CheckSeconds, CheckSeconds);
        }

        private void Check()
        {
            try
            {
                if (_view == null || !_view.IsValid() || !_view.IsOwner() || ZNet.instance == null) return;
                var zdo = _view.GetZDO();
                if (string.IsNullOrEmpty(zdo.GetString(VAItemStandApplier.KeyPreset)) || zdo.GetBool(VAItemStandApplier.KeyEwp) || zdo.GetBool(VAItemStandApplier.KeyCore)) return;
                float min = zdo.GetFloat(VAItemStandApplier.KeyRespawn), max = zdo.GetFloat(VAItemStandApplier.KeyRespawnMax);
                if (min <= 0f) return;
                long now = ZNet.instance.GetTime().Ticks;
                long at = zdo.GetLong(VAItemStandApplier.KeyRefillAt);
                if (zdo.GetInt(ZDOVars.s_item) != 0) { if (at != 0) zdo.Set(VAItemStandApplier.KeyRefillAt, 0L); return; }
                if (at == 0)
                {
                    float wait = max > min ? UnityEngine.Random.Range(min, max) : min;
                    zdo.Set(VAItemStandApplier.KeyRefillAt, now + TimeSpan.FromSeconds(wait).Ticks);
                    return;
                }
                if (now < at) return;
                // 1.1.178: wait while a player is within the preset's distance (0 = never wait).
                float clear = VAItemStandPresetStore.TryGet(zdo.GetString(VAItemStandApplier.KeyPreset), out var own) ? own.RefillClearMetres : 0f;
                if (clear > 0f && Player.IsPlayerInRange(transform.position, clear)) return;
                zdo.Set(VAItemStandApplier.KeyRefillAt, 0L);
                bool hung = VAItemStandApplier.HangItem(VAItemStandApplier.FromZdo(zdo), _stand, _view);
                Debug.Log($"{VaPieces.PluginName}: item stand '{zdo.GetString(VAItemStandApplier.KeyPreset)}' refilled with {zdo.GetString(VAItemStandApplier.KeyItem)}: {(hung ? "hung" : "NOT hung")}");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: item stand refill: {ex.Message}"); }
        }
    }

    // EWP refill (the "Item stands / UseEwpForRefill" toggle, with Expand World Prefabs loaded): one generic change rule (the stand's
    // item field going to 0 on a stand marked va_stand_ewp pokes it after its own timer) and a data entry per preset (the item, its
    // variant and quality, injected into the stand). Same folders as the chest rule (BepInEx/config/expand_world, config/data).
    public static class VAItemStandEwp
    {
        public static string RuleFilePath => Path.Combine(VAChestEwpRuleWriter.ExpandWorldDir, "expand_prefabs_FAP_itemstands.yaml");
        public static string DataFilePath => Path.Combine(VAChestPresetStore.DataDir, "data_FAP_itemstands.yaml");

        public static bool Installed => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ExpandWorldPrefabs");
        public static bool Active => Installed && (VerdantsAscent.Managers.ConfigManager.Instance.configItemStandUseEwp?.Value ?? false);

        public static string DataName(VAItemStandPreset p) => "FAP_stand_" + (p?.name ?? "");

        // 1.1.181 (Fire 10-07: "every item stand we place that's refillable gets an int key, and when that item stand changes state
        // ..."): each refillable preset stamps its stand with int fap_stand = StandId(preset). Per preset, two plain rules, no
        // placeholders: the stand's item-taken state (ItemStand broadcasts "item <none> 0 0") pokes the stand itself after the
        // preset's time; the poke injects the preset's item / variant / quality, which the stand shows on its next UpdateVisual
        // (every 4 s). EWP runs this on the server; FAP's own watcher and Core stay off for these stands (va_stand_ewp).
        public const string KeyStand = "fap_stand";

        public static int StandId(VAItemStandPreset p)
        {
            int id = ("FAP_stand_" + (p?.name ?? "")).GetStableHashCode();
            return id != 0 ? id : 1;
        }

        public static void Write()
        {
            try
            {
                if (!Active)
                {
                    // Off: no rule left behind, or EWP would refill alongside Core / FAP's own watcher.
                    if (File.Exists(RuleFilePath)) { File.Delete(RuleFilePath); Debug.Log($"{VaPieces.PluginName}: item stand EWP refill off - {Path.GetFileName(RuleFilePath)} removed"); }
                    return;
                }
                Directory.CreateDirectory(VAChestEwpRuleWriter.ExpandWorldDir);
                var rule = new System.Text.StringBuilder();
                rule.AppendLine("# expand_prefabs_FAP_itemstands.yaml - auto-managed by FiresAdminPrefabs ([Item stands] UseEwpForRefill).");
                rule.AppendLine("# Per refillable item stand preset: when the stand's item is taken, poke the stand after the preset's time;");
                rule.AppendLine("# the poke puts the preset's item back (data from data_FAP_itemstands.yaml). Stands carry int fap_stand = the preset's id.");
                int rules = 0;
                foreach (var p in VAItemStandPresetStore.All.Values.Where(x => x != null && x.RespawnSeconds > 0f).OrderBy(x => x.name))
                {
                    string stand = string.IsNullOrEmpty(p.StandPrefab) ? "itemstand, itemstandh" : p.StandPrefab;
                    string id = StandId(p).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string delay = p.RespawnSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    rule.AppendLine();
                    rule.AppendLine($"# preset '{p.name}': {p.Item} back {delay} s after it is taken");
                    rule.AppendLine($"- prefab: {stand}");
                    rule.AppendLine("  type: state, item <none>");
                    rule.AppendLine($"  filter: int, {KeyStand}, {id}");
                    rule.AppendLine("  poke:");
                    rule.AppendLine("  - self: true");
                    rule.AppendLine("    pars: fap_stand_refill");
                    rule.AppendLine($"    delay: {delay}");
                    rule.AppendLine($"- prefab: {stand}");
                    rule.AppendLine("  type: poke, fap_stand_refill");
                    rule.AppendLine($"  filter: int, {KeyStand}, {id}");
                    rule.AppendLine($"  data: {DataName(p)}");
                    rule.AppendLine("  injectData: true");
                    rules++;
                }
                File.WriteAllText(RuleFilePath, rule.ToString());

                var data = new System.Text.StringBuilder();
                data.AppendLine("# data_FAP_itemstands.yaml - auto-managed by FiresAdminPrefabs: one entry per item stand preset.");
                foreach (var p in VAItemStandPresetStore.All.Values.OrderBy(x => x.name))
                {
                    data.AppendLine($"- name: {DataName(p)}");
                    data.AppendLine("  ints:");
                    data.AppendLine($"  - item, {(p.Item ?? "").GetStableHashCode()}");
                    data.AppendLine($"  - variant, {Mathf.Max(0, p.Variant)}");
                    data.AppendLine($"  - quality, {Mathf.Max(1, p.Quality)}");
                }
                File.WriteAllText(DataFilePath, data.ToString());
                Debug.Log($"{VaPieces.PluginName}: item stand EWP refill: {rules} preset rule pair(s) + {VAItemStandPresetStore.All.Count} data entr(ies) written");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: item stand EWP files: {ex.Message}"); }
        }
    }
}
