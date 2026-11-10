using System.Collections.Generic;
using System.Globalization;
using GUIFramework;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // 1.1.384 (Fire 11-03/04, Tools\PLAN_fap_spawner_presets_20261103.md): a spawner per enemy on the Monsters tab. The hammer entry is a UI stand-in from VAMonsterStandinFactory;
    // what lands in the world is a vanilla Spawner_* with vanilla ZDO field overrides (ZNetView.LoadFields), so it needs no prefab of ours to load. Values come from the monster's queued preset.
    [HarmonyPatch]
    public static class VAEnemySpawners
    {
        public const string NamePrefix = "FAP_SpawnerPick_";
        public const string RowLabel = "Spawners";
        private const string BasePrefab = "Spawner_Skeleton";
        private const string KillKeySet = "va_kill_key_set", KillKey = "va_kill_key";
        private const string Fields = "HasFields", SpawnerFields = "HasFieldsCreatureSpawner", Prefix = "CreatureSpawner.";

        public static bool Wanted(VAMonsterRegistry.Entry entry)
        {
            var cfg = ConfigManager.Instance;
            if (cfg?.configEnemySpawnersOnHammer == null || !cfg.configEnemySpawnersOnHammer.Value || entry == null) return false;
            return !entry.IsBoss || cfg.configIncludeBossesInEnemySpawners.Value;
        }

        public static void AddRow() => VACategoryRegistry.AddSubFilter(VACategoryRegistry.CatMonsters, RowLabel, p => p != null && p.name.StartsWith(NamePrefix, System.StringComparison.Ordinal));

        public static int PlaceAt(string creature, Vector3 pos, Quaternion rot, Player player)
        {
            var scene = ZNetScene.instance;
            var template = scene != null ? scene.GetPrefab(BasePrefab) : null;
            if (template == null || scene.GetPrefab(creature) == null) { player?.Message(MessageHud.MessageType.Center, $"No spawner for '{creature}': {(template == null ? BasePrefab + " is missing" : "creature is missing")}"); return 0; }
            VAMonsterPreset preset = null;
            if (VAMonsterConfigQueue.TryGetQueuedPresetName(creature, out string presetName)) VAMonsterPresetStore.TryGet(presetName, out preset);
            int count = Mathf.Clamp(IntOf(preset, "va_group_max", 1), 1, 16);
            int groupId = count > 1 ? Random.Range(1000000, int.MaxValue) : 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 at = pos;
                if (count > 1) { var ring = Random.insideUnitCircle * 1.5f; at.x += ring.x; at.z += ring.y; }
                var go = Object.Instantiate(template, at, rot);
                var nv = go.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                var zdo = nv.GetZDO();
                zdo.Set(Fields, true); zdo.Set(SpawnerFields, true);
                zdo.Set(Prefix + "m_creaturePrefab", creature);
                zdo.Set(Prefix + "m_spawnInPlayerBase", true);
                if (preset != null)
                {
                    SetFloat(zdo, preset, "va_spawn_delay_min", "m_respawnTimeMinuts");
                    SetFloat(zdo, preset, "va_levelup_chance", "m_levelupChance");
                    SetFloat(zdo, preset, "va_trigger_distance", "m_triggerDistance");
                    if (preset.TryGetIntStr("va_level_min") != null) zdo.Set(Prefix + "m_minLevel", IntOf(preset, "va_level_min", 1));
                    if (preset.TryGetIntStr("va_level_max") != null) zdo.Set(Prefix + "m_maxLevel", IntOf(preset, "va_level_max", 1));
                    string key = preset.TryGetString("Humanoid.m_defeatSetGlobalKey");   // the editor's "Set global key on death"; "none" clears the key
                    if (key != null) { zdo.Set(KillKeySet, true); zdo.Set(KillKey, key.Trim().Length == 0 || key.Trim().Equals("none", System.StringComparison.OrdinalIgnoreCase) ? "" : key.Trim()); }
                }
                if (count > 1)
                {
                    zdo.Set(Prefix + "m_spawnGroupID", groupId);
                    zdo.Set(Prefix + "m_maxGroupSpawned", count);
                    zdo.Set(Prefix + "m_spawnGroupRadius", 8f);
                }
                zdo.Set("WearNTear.m_health", -1f);   // what wsr protect and fires_bc_regen read as a build
                nv.LoadFields();
                VAPlacedSpawnerBoxes.OnPlaced(go);
            }
            VaPieces.Log?.LogDebug($"[FAP] enemy spawner: {count} x {creature}, preset '{presetName ?? "none"}'");
            return count;
        }

        private static void SetFloat(ZDO zdo, VAMonsterPreset p, string key, string field)
        {
            string s = p.TryGetFloatStr(key);
            if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) zdo.Set(Prefix + field, v);
        }

        private static int IntOf(VAMonsterPreset p, string key, int fallback)
        {
            string s = p?.TryGetIntStr(key) ?? p?.TryGetFloatStr(key);
            return s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? Mathf.RoundToInt(v) : fallback;
        }

        // The kill key is on the creature (Character.m_defeatSetGlobalKey), and vanilla's Spawn copies nothing over.
        [HarmonyPatch(typeof(CreatureSpawner), "Spawn"), HarmonyPostfix]
        private static void AfterSpawn(CreatureSpawner __instance, ZNetView __result)
        {
            if (__result == null || !__result.IsValid()) return;
            var nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.GetZDO().GetBool(KillKeySet)) return;
            string key = nv.GetZDO().GetString(KillKey);
            var character = __result.GetComponent<Character>();
            if (character == null) return;
            var zdo = __result.GetZDO();
            string type = character.GetType().Name;   // LoadFields keys by the component's own type
            zdo.Set(Fields, true); zdo.Set("HasFields" + type, true);
            zdo.Set(type + ".m_defeatSetGlobalKey", key);
            character.m_defeatSetGlobalKey = key;
            VaPieces.Log?.LogDebug($"[FAP] enemy spawner: {character.name} kill key '{key}'");
        }

        // Show All lists without them; a search still finds them, and so does their own tab.
        private static bool HiddenFromShowAll(Piece piece)
        {
            var cfg = ConfigManager.Instance;
            if (piece == null || cfg?.configHideSpawnersFromShowAll == null) return false;
            if (cfg.configHideSpawnersFromShowAll.Value && (piece.name.StartsWith(NamePrefix, System.StringComparison.Ordinal) || VAVanillaPieceManager.IsSpawnerPrefab(piece.gameObject))) return true;
            string hidden = cfg.configShowAllHiddenCategories.Value;
            if (string.IsNullOrWhiteSpace(hidden) || !VACategoryRegistry.TryGetName(piece.m_category, out string tab)) return false;
            foreach (string name in hidden.Split((char)44)) if (name.Trim().Equals(tab, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void FilterShowAll(List<BuildUiPieceButton> buttons, int tag, string term)
        {
            if (tag != -1 || !string.IsNullOrEmpty(term) || buttons == null) return;
            foreach (var button in buttons) if (button != null && HiddenFromShowAll(button.Piece)) button.gameObject.SetActive(false);
        }

        [HarmonyPatch(typeof(BuildUi), "UpdateSearch"), HarmonyPostfix]
        private static void AfterSearch(List<BuildUiPieceButton> ___m_pieceButtons, int ___m_currentTagId, string term) => FilterShowAll(___m_pieceButtons, ___m_currentTagId, term?.Replace(" ", ""));

        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons"), HarmonyPostfix]
        private static void AfterButtons(List<BuildUiPieceButton> ___m_pieceButtons, int ___m_currentTagId, GuiInputField ___m_searchField) => FilterShowAll(___m_pieceButtons, ___m_currentTagId, ___m_searchField != null ? ___m_searchField.text?.Replace(" ", "") : null);

        // Admins only remove a placed spawner with the hammer.
        [HarmonyPatch(typeof(Piece), nameof(Piece.CanBeRemoved)), HarmonyPostfix]
        private static void CanBeRemoved(Piece __instance, ref bool __result)
        {
            if (!__result) return;
            var nv = __instance.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.GetZDO().GetBool(VAPlacedSpawnerBoxes.ZdoKey)) return;
            if (!VAFapServerPresence.LocalMayUseAdminFeatures()) __result = false;
        }
    }
}
