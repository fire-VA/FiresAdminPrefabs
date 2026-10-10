using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.1.187 (the lead 10-07: 1.1.186 cleared the _bal armours but ArmorLoxChest kept logging "Material 'LoxChest' ... doesn't have a
    // texture property '_ChestTex'" - it doesn't come through FAP's bundle injection, NPC 'Ragnar the Black' wears it): the same heal
    // (VAExternalBundleLoader.HealArmorMaterialWithoutBodyTextures) for every item whatever registered it - once over ObjectDB after it
    // loads, and right before vanilla reads an armour material on equip (VisEquipment.SetChestEquipped / SetLegEquipped), which also
    // catches items another mod adds later. Each item is checked once.
    [HarmonyPatch]
    internal static class VAArmorMaterialGuard
    {
        private static readonly HashSet<int> _checked = new HashSet<int>();

        private static void Check(GameObject itemPrefab)
        {
            if (itemPrefab == null || !_checked.Add(itemPrefab.GetInstanceID())) return;
            VAExternalBundleLoader.HealArmorMaterialWithoutBodyTextures(itemPrefab);
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake"), HarmonyPostfix]
        private static void ObjectDB_Awake_Postfix(ObjectDB __instance) => CheckAll(__instance);

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB)), HarmonyPostfix]
        private static void ObjectDB_CopyOtherDB_Postfix(ObjectDB __instance) => CheckAll(__instance);

        private static void CheckAll(ObjectDB db)
        {
            if (db?.m_items == null) return;
            foreach (var item in db.m_items) Check(item);
        }

        [HarmonyPatch(typeof(VisEquipment), "SetChestEquipped"), HarmonyPrefix]
        private static void VisEquipment_SetChestEquipped_Prefix(int hash) => CheckHash(hash);

        [HarmonyPatch(typeof(VisEquipment), "SetLegEquipped"), HarmonyPrefix]
        private static void VisEquipment_SetLegEquipped_Prefix(int hash) => CheckHash(hash);

        private static void CheckHash(int hash)
        {
            if (hash == 0 || ObjectDB.instance == null) return;
            Check(ObjectDB.instance.GetItemPrefab(hash));
        }
    }
}
