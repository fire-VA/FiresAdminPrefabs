using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Runtime side of the chest preset pipeline: stamps a preset's ZDO writes onto a
    // placed container and rolls its loot with the same chance semantics EWP uses on
    // respawn, so first-placement loot matches respawned loot.
    public static class VAChestConfigApplier
    {
        // Also the key the EWP destroy rule filters on, scoping it to stamped chests.
        public const string KeyAppliedPresetName = "va_chest_preset";
        public const string KeyChestMarker = "va_chest";

        private const string CloneSuffix = "(Clone)";
        private const char KeyValueSeparator = ',';
        private const int DefaultQuality = 1;
        private const int DefaultVariant = 0;

        // The bools list carries vanilla's LoadFields gates and Container.m_autoDestroyEmpty,
        // so ZNetView.LoadFields re-applies them on every Awake of this chest.
        public static void StampPresetToZdo(VAChestPreset preset, ZDO zdo)
        {
            if (preset == null || zdo == null) return;

            if (!string.IsNullOrEmpty(preset.name))
                zdo.Set(preset.name, 1);

            foreach (var line in preset.ints ?? new List<string>())
            {
                if (!TrySplitKeyValue(line, out var key, out var value)) continue;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    zdo.Set(key, intValue);
            }

            foreach (var line in preset.floats ?? new List<string>())
            {
                if (!TrySplitKeyValue(line, out var key, out var value)) continue;
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
                    zdo.Set(key, floatValue);
            }

            foreach (var line in preset.strings ?? new List<string>())
            {
                if (!TrySplitKeyValue(line, out var key, out var value)) continue;
                zdo.Set(key, value);
            }

            foreach (var line in preset.bools ?? new List<string>())
            {
                if (!TrySplitKeyValue(line, out var key, out var value)) continue;
                if (bool.TryParse(value, out var boolValue))
                    zdo.Set(key, boolValue);
            }

            zdo.Set(KeyAppliedPresetName, preset.name);
        }

        // Vanilla's AddDefaultItems may already have run, so the inventory is cleared
        // first. Every AddItem fires the container's own change→save path.
        public static void PopulateInventory(VAChestPreset preset, Container container)
        {
            if (preset == null || container == null) return;
            var inventory = container.GetInventory();
            if (inventory == null) return;

            inventory.RemoveAll();

            foreach (var entry in RollItems(preset))
            {
                int stack = VAItemSpec.RollStack(entry.stack);
                if (stack <= 0) continue;

                Vector2i slot = VAChestSlotPlan.TryParseSlot(entry.pos, out Vector2i pinned)
                    ? pinned
                    : new Vector2i(-1, -1);
                inventory.AddItem(entry.prefab, stack, ParseQuality(entry.quality), ParseVariant(entry.variant),
                    0L, string.Empty, slot, false, false, dropIfFullInv: false);
            }
        }

        // Expand World fills a respawned chest from the entry's pos, quality and variant; placement has to
        // produce the same chest. dropIfFullInv must stay false: vanilla's drop path reads
        // Player.m_localPlayer, which is null on a dedicated server.
        private static int ParseQuality(string quality) =>
            int.TryParse(quality, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : DefaultQuality;

        private static int ParseVariant(string variant) =>
            int.TryParse(variant, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : DefaultVariant;

        // No itemAmount: every item rolls independently against its own chance.
        // With itemAmount: N picks from the pool, chance-weighted, without replacement.
        private static List<VAItemEntry> RollItems(VAChestPreset preset)
        {
            var result = new List<VAItemEntry>();
            if (preset.items == null || preset.items.Count == 0) return result;

            int? targetCount = VAItemSpec.RollCount(preset.itemAmount);
            if (!targetCount.HasValue)
            {
                foreach (var item in preset.items)
                {
                    if (item == null || string.IsNullOrEmpty(item.prefab)) continue;
                    if (item.chance >= 1f || UnityEngine.Random.value <= item.chance)
                        result.Add(item);
                }
                return result;
            }

            var pool = new List<VAItemEntry>();
            foreach (var item in preset.items)
            {
                if (item == null || string.IsNullOrEmpty(item.prefab)) continue;
                if (item.chance <= 0f) continue;
                pool.Add(item);
            }

            int picks = Mathf.Min(targetCount.Value, pool.Count);
            for (int i = 0; i < picks; i++)
            {
                float totalWeight = 0f;
                foreach (var item in pool) totalWeight += item.chance;
                if (totalWeight <= 0f) break;

                float roll = UnityEngine.Random.value * totalWeight;
                float accumulated = 0f;
                int pickedIndex = pool.Count - 1;
                for (int j = 0; j < pool.Count; j++)
                {
                    accumulated += pool[j].chance;
                    if (roll <= accumulated)
                    {
                        pickedIndex = j;
                        break;
                    }
                }
                result.Add(pool[pickedIndex]);
                pool.RemoveAt(pickedIndex);
            }
            return result;
        }

        private static bool TrySplitKeyValue(string line, out string key, out string value)
        {
            key = null;
            value = null;
            if (string.IsNullOrEmpty(line)) return false;
            int separator = line.IndexOf(KeyValueSeparator);
            if (separator <= 0) return false;
            key = line.Substring(0, separator).Trim();
            value = line.Substring(separator + 1).Trim();
            return !string.IsNullOrEmpty(key);
        }

        internal static string StripCloneSuffix(string name)
        {
            if (name != null && name.EndsWith(CloneSuffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - CloneSuffix.Length);
            return name;
        }
    }

    // Piece.SetCreator runs only on PlacePiece's success path, on the placing player,
    // after the ZNetView exists — and never on world load — so it is the "player just
    // placed this" signal. The `uid` parameter name is bound by Harmony and must match
    // vanilla's signature.
    [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
    public static class Piece_SetCreator_VAChestApply_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Piece __instance, long uid)
        {
            // uid 0 is a world-generation placement.
            if (__instance == null || uid == 0) return;

            VAExtraDataStamp.StampArmedValues(__instance.gameObject,
                VAChestConfigApplier.StripCloneSuffix(__instance.gameObject.name));

            var container = __instance.GetComponent<Container>();
            if (container == null) return;

            string prefabName = VAChestConfigApplier.StripCloneSuffix(__instance.gameObject.name);
            var preset = VAChestConfigQueue.ConsumeQueuedPreset(prefabName);
            if (preset == null) return;

            var netView = container.m_nview;
            var zdo = netView?.GetZDO();
            if (zdo == null) return;

            VAChestConfigApplier.StampPresetToZdo(preset, zdo);

            // ZNetView.Awake already ran LoadFields before the stamp; re-running it
            // applies m_autoDestroyEmpty to the live container this frame.
            try { netView.LoadFields(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAChestConfigApplier — LoadFields rerun threw: {ex.Message}");
            }

            VAChestConfigApplier.PopulateInventory(preset, container);
            // 1.1.171: Core respawns it when Core has the API (va_chest = 2: the EWP rule leaves it alone)
            VerdantsAscent.Utilities.VACoreLoot.TrackChest(preset, zdo);
        }
    }
}
