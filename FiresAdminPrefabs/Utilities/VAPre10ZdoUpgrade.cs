using System;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Valheim 1.0 keeps item prefab references as name hashes, chest
    // inventories as byte arrays and item data as one packed array, but only
    // converts older data while loading a pre-1.0 world
    // (ZDOMan.ConvertInventories + ConvertPrefabStrings). Blueprint data saved
    // before 1.0 therefore lands in a live world in the old shapes, and item
    // stands, armor stands, chests and fermenters spawn empty. Apply runs one
    // ZDO through the same conversions; data already in the 1.0 shape is left
    // untouched.
    public static class VAPre10ZdoUpgrade
    {
        private static readonly int[] PrefabNameKeys =
        {
            ZDOVars.s_content,
            ZDOVars.s_item,
            "0_item".GetStableHashCode(),
            "1_item".GetStableHashCode(),
            "2_item".GetStableHashCode(),
            "3_item".GetStableHashCode(),
            "4_item".GetStableHashCode(),
            "5_item".GetStableHashCode(),
        };

        public static void Apply(ZDO zdo)
        {
            if (zdo == null) return;
            Run(zdo, ConvertItemData);
            Run(zdo, ConvertInventory);
            Run(zdo, ConvertPrefabNames);
        }

        private static void Run(ZDO zdo, Action<ZDO> conversion)
        {
            try { conversion(zdo); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAPre10ZdoUpgrade.{conversion.Method.Name} threw: {ex.Message}");
            }
        }

        private static void ConvertItemData(ZDO zdo)
        {
            if (!zdo.GetInt(ZDOVars.s_stack, out _)) return;

            var itemData = new ItemDrop.ItemData();
            itemData.m_durability = zdo.GetFloat(ZDOVars.s_durability, itemData.m_durability);
            itemData.m_stack = zdo.GetInt(ZDOVars.s_stack, itemData.m_stack);
            itemData.m_quality = zdo.GetInt(ZDOVars.s_quality, itemData.m_quality);
            itemData.m_variant = zdo.GetInt(ZDOVars.s_variant, itemData.m_variant);
            itemData.m_crafterID = zdo.GetLong(ZDOVars.s_crafterID, itemData.m_crafterID);
            itemData.m_crafterName = zdo.GetString(ZDOVars.s_crafterName, itemData.m_crafterName);
            int dataCount = zdo.GetInt(ZDOVars.s_dataCount);
            itemData.m_customData.Clear();
            for (int i = 0; i < dataCount; i++)
                itemData.m_customData[zdo.GetString($"data_{i}")] = zdo.GetString($"data__{i}");
            itemData.m_worldLevel = (byte)zdo.GetInt(ZDOVars.s_worldLevel, itemData.m_worldLevel);
            itemData.m_pickedUp = zdo.GetBool(ZDOVars.s_pickedUp, itemData.m_pickedUp);
            itemData.m_cheated = zdo.GetBool(ZDOVars.s_cheated, itemData.m_cheated);
            ItemDrop.SaveToZDO(itemData, zdo);

            zdo.RemoveFloat(ZDOVars.s_durability);
            zdo.RemoveInt(ZDOVars.s_stack);
            if (itemData.m_quality == 1) zdo.RemoveInt(ZDOVars.s_quality);
            if (itemData.m_variant == 0) zdo.RemoveInt(ZDOVars.s_variant);
            zdo.RemoveLong(ZDOVars.s_crafterID);
            zdo.RemoveString(ZDOVars.s_crafterName);
            zdo.RemoveInt(ZDOVars.s_dataCount);
            for (int i = 0; i < dataCount; i++)
            {
                zdo.RemoveString($"data_{i}".GetStableHashCode());
                zdo.RemoveString($"data__{i}".GetStableHashCode());
            }
            zdo.RemoveInt(ZDOVars.s_worldLevel);
            zdo.RemoveBool(ZDOVars.s_pickedUp);
            zdo.RemoveBool(ZDOVars.s_cheated);
        }

        private static void ConvertInventory(ZDO zdo)
        {
            if (!zdo.GetString(ZDOVars.s_items, out string base64)) return;
            if (!string.IsNullOrEmpty(base64))
            {
                var legacy = new ZPackage(base64);
                if (legacy.Size() == 0) return;
                var inventory = new Inventory(false);
                inventory.Load(legacy);
                var current = new ZPackage();
                inventory.Save(current);
                zdo.Set(ZDOVars.s_items, current.GetArray());
            }
            zdo.RemoveString(ZDOVars.s_items);
        }

        private static void ConvertPrefabNames(ZDO zdo)
        {
            foreach (int key in PrefabNameKeys)
            {
                if (!zdo.GetString(key, out string prefabName)) continue;
                zdo.RemoveString(key);
                if (!string.IsNullOrEmpty(prefabName))
                    zdo.Set(key, prefabName.GetStableHashCode(), true);
            }
        }
    }
}
