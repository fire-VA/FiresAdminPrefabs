using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // 1.1.171 (Fire 10-06: "always Core when present"): FAP's preset chests and item stands respawn / refill through Core's loot
    // respawn (FiresCore.Loot.LootRespawn, Core 0.2.311+) whenever Core has that API, regardless of Core's location-loot toggle; each
    // preset keeps its own seconds timer (min / max) and Core applies its no-player-within-range rule. Without it (no Core, or an
    // older Core) FAP falls back as before: chests through the EWP rule, item stands through FAP's own refill or EWP (its toggle).
    //   Chest: right after the placer stamps a preset chest, Track(zdo, min, max, stacking radius, refillWithDefaults: false,
    //     carry keys). The chest's marker is va_chest = 2 (Core) instead of 1, so the EWP rule (filter va_chest 1) never also
    //     respawns it. The preset's loot travels on the chest as va_chest_loot (JSON); a chest Core respawns is filled once by its
    //     owner from it (Container Awake), so no peer needs FAP's preset file.
    //   Item stand: TrackRefill(zdo, min, max); Core's RefillDue(zdo) hangs the preset item back (VAItemStandApplier.HangItem).
    // Reflection only, so FAP still loads (and falls back) on an older Core.
    public static class VACoreLoot
    {
        public const string KeyLoot = "va_chest_loot", KeyFilled = "va_chest_filled";
        public const int MarkerCore = 2;

        private static bool _resolved;
        private static MethodInfo s_track, s_trackRefill, s_trackRefillRange;
        private static EventInfo s_refillDue;

        public static bool Available { get { Resolve(); return s_track != null; } }

        // 1.1.177 ([generator] 10-07, Fire's log): Core raised RefillDue six times for a stand tracked in an EARLIER session
        // and nothing answered: FAP only subscribed on its first Track call of the session. Subscribe as the world starts.
        [HarmonyPatch(typeof(ZNet), "Awake")]
        public static class ZNet_Awake_VACoreLoot_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                Resolve();
                VAItemStandEwp.Write();   // 1.1.181: the EWP refill script matches the toggle and the presets from world start
                if (s_refillDue != null && ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.Log($"{VaPieces.PluginName}: [FAP] Core LootRespawn.RefillDue subscribed (item stands refill from earlier sessions too)");
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var t = AccessTools.TypeByName("FiresCore.Loot.LootRespawn");
                if (t == null) return;
                s_track = AccessTools.Method(t, "Track");
                s_trackRefill = AccessTools.Method(t, "TrackRefill", new[] { typeof(ZDO), typeof(float), typeof(float) });
                // 1.1.178: Core 0.2.316+ takes the stand's own "no player within" distance (0 = refill with players right there).
                s_trackRefillRange = AccessTools.Method(t, "TrackRefill", new[] { typeof(ZDO), typeof(float), typeof(float), typeof(float) });
                s_refillDue = t.GetEvent("RefillDue", BindingFlags.Public | BindingFlags.Static);
                if (s_refillDue != null && s_refillDue.EventHandlerType == typeof(Action<ZDO>))
                    s_refillDue.AddEventHandler(null, new Action<ZDO>(OnRefillDue));
                Debug.Log($"{VaPieces.PluginName}: Core's loot respawn found: preset chests{(s_trackRefill != null ? " and item stands" : "")} use it");
            }
            catch (Exception ex) { s_track = null; Debug.LogWarning($"{VaPieces.PluginName}: Core's loot respawn: {ex.Message}"); }
        }

        // The chest's FAP fields Core carries onto a respawned chest (it can't list a ZDO's keys by name).
        public static string[] ChestCarryKeys(VAChestPreset preset) => new[]
        {
            VAChestConfigApplier.KeyChestMarker, VAChestConfigApplier.KeyAppliedPresetName, KeyLoot,
            "HasFields", "HasFieldsContainer", "Container.m_autoDestroyEmpty", preset.name,
        };

        /// <summary>After a preset chest is stamped (as its owner). True when Core tracks it.</summary>
        public static bool TrackChest(VAChestPreset preset, ZDO zdo)
        {
            if (preset == null || zdo == null || !Available) return false;
            float min = Float(preset.TryGetFloatStr("va_respawn_seconds")), max = Float(preset.TryGetFloatStr("va_respawn_seconds_max"));
            if (min <= 0f) return false;   // a one-shot loot template: no respawn
            float radius = Float(preset.TryGetFloatStr("va_prevent_stacking_radius"));
            zdo.Set(VAChestConfigApplier.KeyChestMarker, MarkerCore);
            zdo.Set(KeyLoot, JsonConvert.SerializeObject(new LootCopy { items = preset.items, itemAmount = preset.itemAmount }));
            zdo.Set(KeyFilled, true);
            s_track.Invoke(null, new object[] { zdo, min, max, radius > 0f ? radius : 1f, false, ChestCarryKeys(preset) });
            return true;
        }

        /// <summary>Item stand: true when Core runs its refill (FAP's own watcher then stays idle for it).</summary>
        public static bool TrackStand(ZDO zdo, float min, float max, float playerRange)
        {
            Resolve();
            if (zdo == null || s_trackRefill == null || s_refillDue == null || min <= 0f) return false;
            if (s_trackRefillRange != null) s_trackRefillRange.Invoke(null, new object[] { zdo, min, max, playerRange });
            else s_trackRefill.Invoke(null, new object[] { zdo, min, max });   // an older Core: its global [Loot] distance
            return true;
        }

        private static void OnRefillDue(ZDO zdo)
        {
            try
            {
                if (zdo == null || string.IsNullOrEmpty(zdo.GetString(VAItemStandApplier.KeyPreset))) return;
                var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                var stand = go != null ? go.GetComponent<ItemStand>() : null;
                var view = go != null ? go.GetComponent<ZNetView>() : null;
                if (stand == null || view == null || !view.IsValid() || zdo.GetInt(ZDOVars.s_item) != 0) return;
                bool hung = VAItemStandApplier.HangItem(VAItemStandApplier.FromZdo(zdo), stand, view);
                Debug.Log($"{VaPieces.PluginName}: item stand '{zdo.GetString(VAItemStandApplier.KeyPreset)}' refilled by Core's timer: {(hung ? "hung" : "NOT hung")}");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: item stand refill (Core): {ex.Message}"); }
        }

        private sealed class LootCopy
        {
            public List<VAItemEntry> items;
            public string itemAmount;
        }

        // A chest Core respawned carries the marker and the loot but no contents yet: its owner fills it once from va_chest_loot.
        [HarmonyPatch(typeof(Container), "Awake")]
        public static class Container_Awake_VACoreLoot_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Container __instance)
            {
                try
                {
                    var view = __instance != null ? __instance.m_nview : null;
                    var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                    if (zdo == null || !view.IsOwner() || zdo.GetInt(VAChestConfigApplier.KeyChestMarker) != MarkerCore || zdo.GetBool(KeyFilled)) return;
                    string json = zdo.GetString(KeyLoot);
                    if (string.IsNullOrEmpty(json)) return;
                    var copy = JsonConvert.DeserializeObject<LootCopy>(json);
                    var preset = new VAChestPreset { name = zdo.GetString(VAChestConfigApplier.KeyAppliedPresetName), items = copy?.items ?? new List<VAItemEntry>(), itemAmount = copy?.itemAmount };
                    VAChestConfigApplier.PopulateInventory(preset, __instance);
                    zdo.Set(KeyFilled, true);
                    Debug.Log($"{VaPieces.PluginName}: chest preset '{preset.name}' respawned by Core, filled with {__instance.GetInventory().NrOfItems()} item stack(s)");
                }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: Core-respawned chest fill: {ex.Message}"); }
            }
        }

        private static float Float(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}
