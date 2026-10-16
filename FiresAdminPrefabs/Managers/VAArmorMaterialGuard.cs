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

        // 1.1.250 (ITTO VcpPerf, R3 10-14: 285 calls/s, 5.24 ms/s, the biggest VisEquipment cost). Vanilla calls SetChestEquipped /
        // SetLegEquipped for every character EVERY frame and returns early itself when the hash is unchanged; this prefix ran before
        // that. Two early-outs, same result: (1) the character already wears that hash (vanilla's own no-change test), (2) a hash
        // already checked (one HashSet lookup instead of GetItemPrefab + GetInstanceID). Cleared when ObjectDB wakes.
        private static readonly HashSet<int> _checkedHashes = new HashSet<int>();

        [HarmonyPatch(typeof(ObjectDB), "Awake"), HarmonyPostfix]
        private static void ObjectDB_Awake_Postfix(ObjectDB __instance)
        {
            _checkedHashes.Clear();
            CheckAll(__instance);
            VaPieces.Log?.LogDebug($"[ArmorMaterialGuard] attached: {_checked.Count} item prefab(s) checked; equip prefixes early-out on an unchanged or already-checked hash");
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB)), HarmonyPostfix]
        private static void ObjectDB_CopyOtherDB_Postfix(ObjectDB __instance) => CheckAll(__instance);

        private static void CheckAll(ObjectDB db)
        {
            if (db?.m_items == null) return;
            foreach (var item in db.m_items) Check(item);
        }

        [HarmonyPatch(typeof(VisEquipment), "SetChestEquipped"), HarmonyPrefix]
        private static void VisEquipment_SetChestEquipped_Prefix(int hash, int ___m_currentChestItemHash)
        {
            if (hash == ___m_currentChestItemHash) return;   // no change: vanilla returns at once too
            CheckHash(hash);
        }

        [HarmonyPatch(typeof(VisEquipment), "SetLegEquipped"), HarmonyPrefix]
        private static void VisEquipment_SetLegEquipped_Prefix(int hash, int ___m_currentLegItemHash)
        {
            if (hash == ___m_currentLegItemHash) return;
            CheckHash(hash);
        }

        private static void CheckHash(int hash)
        {
            if (hash == 0 || ObjectDB.instance == null || !_checkedHashes.Add(hash)) return;
            var prefab = ObjectDB.instance.GetItemPrefab(hash);
            VaPieces.Log?.LogDebug($"[ArmorMaterialGuard] new equip hash {hash} -> '{(prefab != null ? prefab.name : "no prefab")}' ({_checkedHashes.Count} seen)");
            Check(prefab);
        }
    }
}
