using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Expand World writes each chest item at the slot its entry names and never re-derives one, so FAP names
    // every slot itself rather than leaving it to fill order. Plan: Docs/CHEST_PRESET_SLOTS.md
    internal static class VAChestSlotPlan
    {
        private const int FallbackWidth = 4;
        private const int FallbackHeight = 2;
        private const char SlotSeparator = ',';

        public static void ResolveContainerSize(string containerPrefabName, out int width, out int height)
        {
            width = FallbackWidth;
            height = FallbackHeight;
            if (string.IsNullOrEmpty(containerPrefabName)) return;

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(containerPrefabName) : null;
            Container container = prefab != null ? prefab.GetComponent<Container>() : null;
            if (container == null || container.m_width <= 0 || container.m_height <= 0) return;

            width = container.m_width;
            height = container.m_height;
        }

        /// <summary>Pins every item to its own slot in independent-roll mode, and unpins every item in pick-N
        /// mode because Expand World applies itemAmount only to items without a pos. Refuses a table that does
        /// not fit rather than letting the overflow fall off the end.</summary>
        public static bool TryPlan(VAChestPreset preset, out string problem)
        {
            problem = null;
            if (preset == null) return false;

            ResolveContainerSize(preset.ContainerPrefab, out int width, out int height);
            preset.containerSize = FormatSlot(width, height);
            if (preset.items == null || preset.items.Count == 0) return true;

            if (IsPickFromPool(preset))
            {
                foreach (VAItemEntry item in preset.items)
                    if (item != null) item.pos = string.Empty;
                return true;
            }

            int slots = width * height;
            if (preset.items.Count > slots)
            {
                problem = $"{preset.items.Count} items do not fit a {width}x{height} chest ({slots} slots). "
                    + "Remove lines, or pick a bigger container.";
                return false;
            }

            for (int index = 0; index < preset.items.Count; index++)
            {
                VAItemEntry item = preset.items[index];
                if (item == null) continue;
                item.pos = FormatSlot(index % width, index / width);
            }
            return true;
        }

        /// <summary>Brings a stored preset up to the pinned-slot form. Idempotent, and returns true only when
        /// something changed so the store knows to rewrite the file. A table that no longer fits its container
        /// is left alone and named in the log - rewriting it would silently drop items.</summary>
        public static bool MigrateSlots(VAChestPreset preset)
        {
            if (preset?.items == null || preset.items.Count == 0) return false;
            if (!NeedsPlanning(preset)) return false;

            if (TryPlan(preset, out string problem)) return true;

            Debug.LogWarning($"{VaPieces.PluginName}: chest preset '{preset.name}' was not re-slotted: {problem}");
            return false;
        }

        public static bool TryParseSlot(string pos, out Vector2i slot)
        {
            slot = new Vector2i(-1, -1);
            if (string.IsNullOrEmpty(pos)) return false;

            string[] parts = pos.Split(SlotSeparator);
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) return false;
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return false;
            if (x < 0 || y < 0) return false;

            slot = new Vector2i(x, y);
            return true;
        }

        public static bool IsPickFromPool(VAChestPreset preset) =>
            !string.IsNullOrEmpty(preset.itemAmount);

        private static bool NeedsPlanning(VAChestPreset preset)
        {
            if (string.IsNullOrEmpty(preset.containerSize)) return true;

            bool pickFromPool = IsPickFromPool(preset);
            foreach (VAItemEntry item in preset.items)
            {
                if (item == null) continue;
                bool pinned = !string.IsNullOrEmpty(item.pos);
                if (pinned == pickFromPool) return true;
            }
            return false;
        }

        private static string FormatSlot(int x, int y) =>
            x.ToString(CultureInfo.InvariantCulture) + SlotSeparator + y.ToString(CultureInfo.InvariantCulture);
    }
}
