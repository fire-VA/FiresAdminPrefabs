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
            // 1.1.318 (audit M8): ObjectDB.Awake / CopyOtherDB postfix body; one bad item no longer throws out of the patch
            foreach (var item in db.m_items)
            {
                try { Check(item); }
                catch (System.Exception e) { UnityEngine.Debug.LogError($"[FAP] armor material guard: {(item != null ? item.name : "?")} check failed: {e.Message}"); }
            }
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

        // 1.1.329 ([wishbone]'s proof via the lead 10-29: ShadowPerson spawns log 5 errors each - "Material 'ShadowPerson 1' with Shader
        // 'Standard' doesn't have a texture property '_SkinBumpMap'" ...): vanilla VisEquipment.UpdateBaseModel (VisEquipment.cs:479-489)
        // reads _SkinBumpMap / _ChestTex / _ChestBumpMap / _LegsTex / _LegsBumpMap from m_models[i].m_baseMaterial with no HasProperty
        // check. A non-player whose every model material lacks them gets vanilla's own mesh + _MainTex swap here, once, and its
        // m_models emptied on that instance only (the prefab keeps them), so UpdateBaseModel returns at its first line. Same look.
        private static readonly int s_mainTex = Shader.PropertyToID("_MainTex");
        private static readonly string[] s_baseProps = { "_SkinBumpMap", "_ChestTex", "_ChestBumpMap", "_LegsTex", "_LegsBumpMap" };
        private static readonly HashSet<string> _baseHealed = new HashSet<string>();

        [HarmonyPatch(typeof(VisEquipment), "Awake"), HarmonyPostfix]
        private static void VisEquipment_Awake_Postfix(VisEquipment __instance, ZNetView ___m_nview, int ___m_modelIndex)
        {
            try
            {
                var models = __instance.m_models;
                var body = __instance.m_bodyModel;
                if (__instance.m_isPlayer || models == null || models.Length == 0 || body == null) return;
                foreach (var m in models)
                {
                    if (m?.m_baseMaterial == null) return;
                    foreach (var p in s_baseProps) if (m.m_baseMaterial.HasProperty(p)) return;   // vanilla can read it: leave as is
                }
                int idx = ___m_modelIndex;
                if (___m_nview != null && ___m_nview.GetZDO() != null) idx = ___m_nview.GetZDO().GetInt(ZDOVars.s_modelIndex, idx);
                idx = Mathf.Clamp(idx, 0, models.Length - 1);
                var model = models[idx];
                if (model.m_mesh != null) body.sharedMesh = model.m_mesh;
                if (model.m_baseMaterial.HasProperty(s_mainTex)) body.materials[0].SetTexture(s_mainTex, model.m_baseMaterial.GetTexture(s_mainTex));
                __instance.m_models = System.Array.Empty<VisEquipment.PlayerModel>();
                string name = Utils.GetPrefabName(__instance.gameObject);
                if (_baseHealed.Add(name))
                    VaPieces.Log?.LogDebug($"[ArmorMaterialGuard] {name}: its body model material '{model.m_baseMaterial.name}' (shader '{(model.m_baseMaterial.shader != null ? model.m_baseMaterial.shader.name : "none")}') has none of {string.Join("/", s_baseProps)}; applied model {idx}'s mesh + _MainTex once and skipped vanilla's base-model update, so it no longer logs 5 errors per spawn");
            }
            catch (System.Exception e) { UnityEngine.Debug.LogError($"[FAP] body model guard: {(__instance != null ? __instance.name : "?")} failed: {e.Message}"); }
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
