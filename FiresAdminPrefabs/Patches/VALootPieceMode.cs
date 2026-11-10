using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.UI.ContextMenu;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // 1.1.394 (Fire 11-05/06): world loot chests + Morkhalla debris on the Hammer, one entry each, with a context-menu
    // "Vanilla function / Decor" switch saved on the piece's ZDO ("fap_loot_mode": "decor" / "vanilla", synced like any ZDO value).
    // - Placed from the Hammer = decor: vanilla Container.Awake skips its loot roll when s_addedDefaultItems is already set, so that
    //   flag is set before Awake (only inside a local Player.PlacePiece, VAPlayerPlacementContext). The chest is plain storage.
    // - Chest to vanilla: vanilla's own AddDefaultItems runs once (its loot table, on top of what's stored). Back to decor: nothing removed.
    // - Debris (Gibber root): decor skips Gibber.Start, so it stays put. To vanilla: Gibber.Start runs = vanilla breaks it apart.
    // Objects without the key (world-spawned chests and gibs) are never touched.
    [HarmonyPatch]
    internal static class VALootPieceMode
    {
        internal const string ModeKey = "fap_loot_mode";
        internal static readonly HashSet<string> Chests = new HashSet<string>(StringComparer.Ordinal)
        {
            "TreasureChest_blackforest", "TreasureChest_heath", "TreasureChest_heath_hildir", "TreasureChest_meadows",
            "TreasureChest_meadows_buried", "TreasureChest_mountains", "TreasureChest_swamp", "shipwreck_karve_chest",
            "loot_chest_wood", "TreasureChest_forestcrypt", "TreasureChest_plains_stone", "TreasureChest_mountaincave_hildir",
            "TreasureChest_plainsfortress_hildir", "TreasureChest_forestcrypt_hildir",
        };
        internal static readonly HashSet<string> Debris = new HashSet<string>(StringComparer.Ordinal)
        {
            "Morkhalla_woodboards_Destroyed", "Morkhalla_Rubble_Destroyed",
        };
        private static readonly int ModeHash = ModeKey.GetStableHashCode();
        private static readonly MethodInfo s_addDefault = AccessTools.Method(typeof(Container), "AddDefaultItems");
        private static readonly MethodInfo s_gibStart = AccessTools.Method(typeof(Gibber), "Start");

        // 1.1.395 (Fire 11-06: "whats with the ones that all just say chest"): each gets its own build-menu name; the vanilla icon stays.
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TreasureChest_meadows"] = "Treasure Chest (Meadows)", ["TreasureChest_meadows_buried"] = "Treasure Chest (Meadows, buried)",
            ["TreasureChest_blackforest"] = "Treasure Chest (Black Forest)", ["TreasureChest_forestcrypt"] = "Treasure Chest (Forest Crypt)",
            ["TreasureChest_forestcrypt_hildir"] = "Treasure Chest (Forest Crypt, Hildir)", ["TreasureChest_swamp"] = "Treasure Chest (Swamp)",
            ["TreasureChest_mountains"] = "Treasure Chest (Mountains)", ["TreasureChest_mountaincave_hildir"] = "Treasure Chest (Mountain Cave, Hildir)",
            ["TreasureChest_heath"] = "Treasure Chest (Heath)", ["TreasureChest_heath_hildir"] = "Treasure Chest (Heath, Hildir)",
            ["TreasureChest_plains_stone"] = "Treasure Chest (Plains, stone)", ["TreasureChest_plainsfortress_hildir"] = "Treasure Chest (Plains Fortress, Hildir)",
            ["shipwreck_karve_chest"] = "Karve Shipwreck Chest", ["loot_chest_wood"] = "Loot Chest (Wood)",
            ["Morkhalla_woodboards_Destroyed"] = "Morkhalla Boards (debris)", ["Morkhalla_Rubble_Destroyed"] = "Morkhalla Rubble (debris)",
        };

        // 1.1.395 (Fire 11-06): the hammer's build-menu right-click picks how the NEXT placement of that piece behaves, remembered per piece
        // in BepInEx\cache\FiresAdminPrefabs\loot_place_modes.txt (one "prefab=vanilla" line each; absent = storage, the default).
        private static HashSet<string> s_placeVanilla;
        private static string PrefPath => System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresAdminPrefabs", "loot_place_modes.txt");
        private static HashSet<string> PlaceVanilla()
        {
            if (s_placeVanilla != null) return s_placeVanilla;
            s_placeVanilla = new HashSet<string>(StringComparer.Ordinal);
            try { if (System.IO.File.Exists(PrefPath)) foreach (var l in System.IO.File.ReadAllLines(PrefPath)) { var t = l.Trim(); if (t.EndsWith("=vanilla")) s_placeVanilla.Add(t.Substring(0, t.Length - 8)); } }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [LootMode] read {PrefPath}: {e.Message}"); }
            return s_placeVanilla;
        }
        internal static bool PlacesVanilla(string prefab) => PlaceVanilla().Contains(prefab);
        private static void SetPlaceVanilla(string prefab, bool vanilla)
        {
            var set = PlaceVanilla();
            if (vanilla) set.Add(prefab); else set.Remove(prefab);
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefPath));
                var lines = new List<string>(); foreach (var n in set) lines.Add(n + "=vanilla");
                System.IO.File.WriteAllLines(PrefPath, lines.ToArray());
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [LootMode] write {PrefPath}: {e.Message}"); }
            Debug.Log($"{VaPieces.PluginName}: [LootMode] next '{prefab}' placement: {(vanilla ? "vanilla (loot roll / breaks)" : "storage (no loot roll / stays whole)")}");
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, vanilla ? "Next placement: vanilla" : "Next placement: storage");
        }

        // Build-menu right-click rows (FAP's own FapPieceMenu hook, the menu with Copy ID / Configure container).
        internal static IEnumerable<KeyValuePair<string, Action>> BuildMenuRows(Piece piece)
        {
            string name = piece != null ? piece.gameObject.name : null;
            if (name == null || !(Chests.Contains(name) || Debris.Contains(name))) return null;
            bool v = PlacesVanilla(name);
            return new[]
            {
                new KeyValuePair<string, Action>((v ? "" : "* ") + "Place as storage" + (Debris.Contains(name) ? " (stays whole)" : " (no loot roll)"), () => SetPlaceVanilla(name, false)),
                new KeyValuePair<string, Action>((v ? "* " : "") + "Place as vanilla" + (Debris.Contains(name) ? " (breaks apart)" : " (rolls its loot)"), () => SetPlaceVanilla(name, true)),
            };
        }

        // After the hammer scan: names + the one Info line that proves they were added.
        internal static void OnHammerScanned(PieceTable table)
        {
            try
            {
                if (table == null) return;
                int found = 0, named = 0; var missing = new List<string>(Names.Keys);
                foreach (var go in table.m_pieces)
                {
                    if (go == null || !Names.TryGetValue(go.name, out var label)) continue;
                    found++; missing.Remove(go.name);
                    var p = go.GetComponent<Piece>();
                    if (p != null && p.m_name != label) { p.m_name = label; named++; }
                }
                Debug.Log($"{VaPieces.PluginName}: [LootMode] loot chests + debris on the Hammer: {found} of {Names.Count}, {named} named" + (missing.Count > 0 ? $"; not on the Hammer: {string.Join(", ", missing.ToArray())}" : ""));
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [LootMode] scan pass: {e.Message}"); }
        }

        internal static string ModeOf(ZNetView nv) => nv != null && nv.IsValid() ? nv.GetZDO().GetString(ModeHash, "") : "";

        [HarmonyPatch(typeof(Container), "Awake"), HarmonyPrefix]
        private static void Container_Awake(Container __instance)
        {
            try
            {
                string pn = Utils.GetPrefabName(__instance.gameObject);
                if (!VAPlayerPlacementContext.IsPlacing || !Chests.Contains(pn)) return;
                var nv = __instance.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || !nv.IsOwner() || nv.GetZDO().GetBool(ZDOVars.s_addedDefaultItems)) return;
                if (PlacesVanilla(pn)) { nv.GetZDO().Set(ModeHash, "vanilla"); VaPieces.Log?.LogDebug($"[LootMode] placed '{__instance.name}' as vanilla: its loot roll runs"); return; }
                nv.GetZDO().Set(ZDOVars.s_addedDefaultItems, true);
                nv.GetZDO().Set(ModeHash, "decor");
                VaPieces.Log?.LogDebug($"[LootMode] placed '{__instance.name}' as decor: loot roll skipped (s_addedDefaultItems set before Awake)");
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [LootMode] chest placement: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Gibber), "Awake"), HarmonyPostfix]
        private static void Gibber_Awake(Gibber __instance)
        {
            try
            {
                if (!VAPlayerPlacementContext.IsPlacing || !Debris.Contains(Utils.GetPrefabName(__instance.gameObject))) return;
                var nv = __instance.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || !nv.IsOwner() || ModeOf(nv) != "") return;
                if (PlacesVanilla(Utils.GetPrefabName(__instance.gameObject))) { nv.GetZDO().Set(ModeHash, "vanilla"); return; }
                nv.GetZDO().Set(ModeHash, "decor");
                VaPieces.Log?.LogDebug($"[LootMode] placed '{__instance.name}' as decor: it stays put");
            }
            catch (Exception e) { Debug.LogWarning($"{VaPieces.PluginName}: [LootMode] debris placement: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Gibber), "Start"), HarmonyPrefix]
        private static bool Gibber_Start(Gibber __instance)
        {
            var nv = __instance.GetComponent<ZNetView>();
            if (ModeOf(nv) != "decor") return true;
            VaPieces.Log?.LogDebug($"[LootMode] '{__instance.name}' is decor: break skipped");
            return false;
        }

        internal static void SetMode(GameObject go, bool vanilla)
        {
            try
            {
                var nv = go != null ? go.GetComponent<ZNetView>() : null;
                if (nv == null || !nv.IsValid()) return;
                nv.ClaimOwnership();
                nv.GetZDO().Set(ModeHash, vanilla ? "vanilla" : "decor");
                string what = vanilla ? "vanilla function" : "decor";
                var c = go.GetComponent<Container>();
                if (c != null && vanilla && s_addDefault != null)
                {
                    s_addDefault.Invoke(c, null);   // vanilla's own loot roll, once per switch to vanilla
                    nv.GetZDO().Set(ZDOVars.s_addedDefaultItems, true);
                    what += ": loot rolled";
                }
                var g = go.GetComponent<Gibber>();
                if (g != null && vanilla && s_gibStart != null) { s_gibStart.Invoke(g, null); what += ": breaking apart"; }
                Debug.Log($"{VaPieces.PluginName}: [LootMode] '{go.name}' set to {what}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Set to " + what);
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [LootMode] set failed: " + (ex.InnerException ?? ex).Message); }
        }
    }

    internal sealed class VALootModeContextProvider : IContextMenuProvider
    {
        public string TitleFor(ContextTarget target) => null;

        public IEnumerable<ContextMenuItem> GetItems(ContextTarget target)
        {
            GameObject go = null;
            var c = target?.Find<Container>();
            if (c != null && VALootPieceMode.Chests.Contains(Utils.GetPrefabName(c.gameObject))) go = c.gameObject;
            if (go == null) { var g = target?.Find<Gibber>(); if (g != null && VALootPieceMode.Debris.Contains(Utils.GetPrefabName(g.gameObject))) go = g.gameObject; }
            if (go == null) return null;
            bool admin = false; try { admin = VerdantsAscent.Managers.VAFapServerPresence.LocalMayUseAdminFeatures(); } catch { }
            if (!admin) return null;
            var nv = go.GetComponent<ZNetView>();
            bool decor = VALootPieceMode.ModeOf(nv) == "decor";
            var items = new List<ContextMenuItem>();
            if (decor) items.Add(ContextMenuItem.Row("Vanilla function", () => VALootPieceMode.SetMode(go, true)));
            else items.Add(ContextMenuItem.Row("Decor", () => VALootPieceMode.SetMode(go, false)));
            return items;
        }
    }
}
