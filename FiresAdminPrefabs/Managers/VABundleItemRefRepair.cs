using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.1.165 (Fire 10-05, "clean log"): bundle prefabs whose item references did not survive the bundle split get their items
    // back by NAME once ZNetScene knows every item (vanilla + every bundle), instead of only being made safe.
    //   - Pickables (balrondnature 35): the item each bundle pickable gives, from the bundle's own pickable -> item pairs; an item
    //     the bundle doesn't carry falls back to the closest vanilla one, so every pickable yields something.
    //   - Pieces left with NO requirement (vapieces 130 + 3): the VerdantsAscent piece costs by material: grausten -> Grausten,
    //     sandstone -> Stone, marble -> BlackMarble, anything else -> Wood (the original VApieces default "Wood,5").
    // One Debug summary when everything resolved; a Warning only for what genuinely stays unresolved.
    [HarmonyPatch]
    internal static class VABundleItemRefRepair
    {
        // pickable prefab -> the item it gives (the bundle's own pairs)
        private static readonly Dictionary<string, string> PickableItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "BlackberryBush_bal", "Blackberries_bal" }, { "BlackberryBush2_bal", "Blackberries_bal" },
            { "BlueberryBush_bal", "Blueberries" }, { "Pickable_BlueberryBush_bal", "Blueberries" },
            { "RaspberryBush_bal", "Raspberry" }, { "Pickable_RaspberryBush_bal", "Raspberry" },
            { "IceberryBush_bal", "Iceberry_bal" }, { "Lingonberry_shrub_bal", "Lingonberry_bal" },
            { "Pickable_AppleTree_bal", "Apple_bal" }, { "Pickable_Bloodvine_bal", "Bloodfruit_bal" },
            { "Pickable_Cabbage_bal", "Cabbage_bal" }, { "Pickable_Clay_bal", "Clay_bal" },
            { "Pickable_DandelionFarm_bal", "Dandelion" }, { "Pickable_Garlic_bal", "Garlic_bal" },
            { "Pickable_GuckSack_bal", "Guck" }, { "Pickable_Ignicap_bal", "Ignicap_bal" },
            { "Pickable_Kelp_bal", "RedKelp_bal" }, { "Pickable_Lavender_bal", "Lavender_bal" },
            { "Pickable_Lija_bal", "Lija_bal" }, { "Pickable_MagmaStone_bal", "MagmaStone_bal" },
            { "Pickable_MeadowsMeatPile01_bal", "RottenMeat" }, { "Pickable_MeadowsMeatPile02_bal", "RottenMeat" },
            { "Pickable_MeteoriteNew_bal", "FlametalOre" }, { "Pickable_Mint_bal", "Mint_bal" },
            { "Pickable_MountainCaveCrystal_bal", "Crystal" }, { "Pickable_Mugwort_bal", "Mugwort_bal" },
            { "Pickable_MushroomFarm_bal", "Mushroom" }, { "Pickable_Mushroom_Darkbul_bal", "Darkbul_bal" },
            { "Pickable_Mushroom_Random_bal", "Moss_bal" }, { "Pickable_Mushroom_blueFarm_bal", "MushroomBlue" },
            { "Pickable_Mushroom_blueNew_bal", "MushroomBlue" }, { "Pickable_Mushroom_blue_bal", "MushroomBlue" },
            { "Pickable_Mushroom_grayFarm_bal", "GrayMushroom_bal" }, { "Pickable_Mushroom_grayNew_bal", "GrayMushroom_bal" },
            { "Pickable_Mushroom_yellowFarm_bal", "MushroomYellow" }, { "Pickable_Mushroom_yellow_bal", "MushroomYellow" },
            { "Pickable_Mycelium_bal", "Mycelium_bal" }, { "Pickable_Nettle_bal", "Nettle_bal" },
            { "Pickable_Plantain_bal", "Plantain_bal" }, { "Pickable_Potato_bal", "Potato_bal" },
            { "Pickable_Sage_bal", "Sage_bal" }, { "Pickable_Seaberry_bal", "Seaberries_bal" },
            { "Pickable_SeedCabbage_bal", "CabbageSeeds_bal" }, { "Pickable_SeedGarlic_bal", "GarlicSeeds_bal" },
            { "Pickable_Snowleaf_bal", "Snowleaf_bal" }, { "Pickable_StrawPlant_bal", "Straw_bal" },
            { "Pickable_Straw_bal", "Straw_bal" }, { "Pickable_Straw_DeepNorth_bal", "Straw_bal" },
            { "Pickable_ThistleFarm_bal", "Thistle" }, { "Pickable_TinNew_bal", "TinOre" },
            { "Pickable_Yarrow_bal", "Yarrow_bal" },
        };

        // a bundle item that isn't registered -> the closest vanilla item
        private static readonly Dictionary<string, string> VanillaFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Blackberries_bal", "Blueberries" }, { "Iceberry_bal", "Blueberries" }, { "Lingonberry_bal", "Raspberry" },
            { "Apple_bal", "Raspberry" }, { "Bloodfruit_bal", "Raspberry" }, { "Seaberries_bal", "Cloudberry" },
            { "Cabbage_bal", "Turnip" }, { "Garlic_bal", "Onion" }, { "Potato_bal", "Carrot" },
            { "CabbageSeeds_bal", "TurnipSeeds" }, { "GarlicSeeds_bal", "OnionSeeds" },
            { "Clay_bal", "Stone" }, { "MagmaStone_bal", "Grausten" }, { "RedKelp_bal", "Guck" },
            { "Straw_bal", "Flax" }, { "Moss_bal", "Thistle" },
            { "Nettle_bal", "Thistle" }, { "Lavender_bal", "Dandelion" }, { "Lija_bal", "Dandelion" }, { "Mint_bal", "Dandelion" },
            { "Mugwort_bal", "Dandelion" }, { "Plantain_bal", "Dandelion" }, { "Sage_bal", "Dandelion" }, { "Yarrow_bal", "Dandelion" },
            { "Snowleaf_bal", "Thistle" },
            { "Darkbul_bal", "Mushroom" }, { "GrayMushroom_bal", "Mushroom" }, { "Ignicap_bal", "MushroomSmokePuff" }, { "Mycelium_bal", "Mushroom" },
            { "TinOre", "CopperOre" },
        };

        private const int DefaultPieceAmount = 5;

        private static readonly List<(Pickable pickable, string prefab)> s_pickables = new List<(Pickable, string)>();
        private static readonly List<(Piece piece, string prefab, string bundle)> s_pieces = new List<(Piece, string, string)>();

        internal static void NotePickable(Pickable pickable, string prefabName) { if (pickable != null) s_pickables.Add((pickable, prefabName)); }
        internal static void NoteEmptyPiece(Piece piece, string prefabName, string bundle) { if (piece != null) s_pieces.Add((piece, prefabName, bundle)); }

        internal static string PieceMaterial(string prefabName)
        {
            string n = prefabName ?? "";
            if (n.IndexOf("grausten", StringComparison.OrdinalIgnoreCase) >= 0) return "Grausten";
            if (n.IndexOf("sandstone", StringComparison.OrdinalIgnoreCase) >= 0) return "Stone";
            if (n.IndexOf("marble", StringComparison.OrdinalIgnoreCase) >= 0) return "BlackMarble";
            return "Wood";
        }

        // After vanilla built m_namedPrefabs and the bundle prefabs are in; late, so every item is registered.
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
            if (__instance == null) return;
            try { Resolve(__instance); }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: VABundleItemRefRepair failed: {e.Message}"); }
        }

        private static ItemDrop Item(ZNetScene scene, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var go = scene.GetPrefab(name);
            return go != null ? go.GetComponent<ItemDrop>() : null;
        }

        private static void Resolve(ZNetScene scene)
        {
            int pickOk = 0, pickFallback = 0; var pickMissing = new List<string>(); var fallbacks = new List<string>();
            foreach (var (p, prefab) in s_pickables)
            {
                if (p == null) continue;
                if (p.m_itemPrefab != null && p.m_itemPrefab.GetComponent<ItemDrop>() != null) continue;
                string key = p.gameObject.name;
                if (!PickableItems.TryGetValue(key, out string want) && !PickableItems.TryGetValue(prefab ?? "", out want)) { pickMissing.Add(key); continue; }
                var item = Item(scene, want);
                if (item == null && VanillaFallback.TryGetValue(want, out string fb)) { item = Item(scene, fb); if (item != null) { pickFallback++; fallbacks.Add($"{want}->{fb}"); } }
                else if (item != null) pickOk++;
                if (item == null) { pickMissing.Add($"{key} ({want})"); continue; }
                p.m_itemPrefab = item.gameObject;
                if (p.m_overrideName == p.gameObject.name) p.m_overrideName = "";   // the load-time hover guard is no longer needed
            }

            int pieceOk = 0; var pieceMissing = new List<string>(); var byMaterial = new Dictionary<string, int>();
            foreach (var (piece, prefab, bundle) in s_pieces)
            {
                if (piece == null || (piece.m_resources != null && piece.m_resources.Length > 0)) continue;
                string mat = PieceMaterial(prefab);
                var item = Item(scene, mat);
                if (item == null) { pieceMissing.Add($"{prefab} ({mat})"); continue; }
                piece.m_resources = new[] { new Piece.Requirement { m_resItem = item, m_amount = DefaultPieceAmount, m_amountPerLevel = 0, m_recover = true } };
                pieceOk++;
                byMaterial[mat] = byMaterial.TryGetValue(mat, out int c) ? c + 1 : 1;
            }

            if (s_pickables.Count > 0 || s_pieces.Count > 0)
                VaPieces.Log?.LogDebug($"[VABundleItemRefRepair] pickables: {pickOk} given their bundle item, {pickFallback} a vanilla stand-in"
                    + (fallbacks.Count > 0 ? $" ({string.Join(", ", fallbacks.Distinct())})" : "")
                    + $"; pieces with no cost: {pieceOk} given {DefaultPieceAmount} x material ({string.Join(", ", byMaterial.Select(kv => kv.Key + " " + kv.Value))})");
            if (pickMissing.Count > 0)
                Debug.LogWarning($"{VaPieces.PluginName}: VABundleItemRefRepair: {pickMissing.Count} Pickable(s) still have no item (they yield nothing): {string.Join(", ", pickMissing.Distinct().Take(20))}");
            if (pieceMissing.Count > 0)
                Debug.LogWarning($"{VaPieces.PluginName}: VABundleItemRefRepair: {pieceMissing.Count} piece(s) still have no cost: {string.Join(", ", pieceMissing.Take(20))}");
        }
    }
}
