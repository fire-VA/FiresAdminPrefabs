using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Runtime side of the monster preset pipeline: stamps a preset's ZDO writes onto
    // a spawned creature, swaps in the preset's drop table so vanilla's own drop path
    // rolls it, and schedules respawn itself when EWP is not the persistence backend.
    public static class VAMonsterConfigApplier
    {
        // ZNetView.LoadFields bails unless both the top-level gate and the
        // per-component gate are set.
        public const string KeyHasFields           = "HasFields";
        public const string KeyHasFieldsHumanoid   = "HasFieldsHumanoid";
        public const string KeyHasFieldsCharacter  = "HasFieldsCharacter";
        public const string KeyHasFieldsMonsterAI  = "HasFieldsMonsterAI";
        public const string KeyHasFieldsAnimalAI   = "HasFieldsAnimalAI";

        public const string KeyRespawnSeconds      = "va_respawn_seconds";
        public const string KeyAppliedPresetName   = "va_preset_name";

        private const char KeyValueSeparator = ',';

        // Lines are "key, value", split on the FIRST comma so values keep their own
        // commas. The preset-name int is what the EWP destroy rule filters on.
        public static void StampPresetToZdo(VAMonsterPreset preset, ZDO zdo)
        {
            if (preset == null || zdo == null) return;

            zdo.Set(KeyHasFields, true);
            zdo.Set(KeyHasFieldsHumanoid, true);
            zdo.Set(KeyHasFieldsCharacter, true);
            zdo.Set(KeyHasFieldsMonsterAI, true);
            zdo.Set(KeyHasFieldsAnimalAI, true);

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

            foreach (var line in preset.vecs ?? new List<string>())
            {
                if (!TrySplitKeyValue(line, out var key, out var vectorText)) continue;
                if (TryParseVector3(vectorText, out var vector))
                    zdo.Set(key, vector);
            }

            zdo.Set(KeyAppliedPresetName, preset.name);
        }

        // For a live instance: stamps, re-runs LoadFields, and writes the headline
        // fields directly so the HUD reflects the preset on the same frame.
        public static void StampPresetAndRefresh(VAMonsterPreset preset, GameObject instance)
        {
            if (preset == null || instance == null) return;
            var netView = instance.GetComponent<ZNetView>();
            if (netView == null || !netView.IsValid()) return;
            var zdo = netView.GetZDO();
            if (zdo == null) return;

            StampPresetToZdo(preset, zdo);

            try { netView.LoadFields(); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"{VaPieces.PluginName}: StampPresetAndRefresh — LoadFields rerun threw: {ex.Message}");
            }

            var humanoid = instance.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                string nameOverride = preset.TryGetString("Humanoid.m_name");
                if (!string.IsNullOrEmpty(nameOverride))
                    humanoid.m_name = nameOverride;

                string defeatKey = preset.TryGetString("Humanoid.m_defeatSetGlobalKey");
                if (defeatKey != null)
                    humanoid.m_defeatSetGlobalKey = defeatKey;

                string bossText = preset.TryGetIntStr("Humanoid.m_boss");
                if (string.IsNullOrEmpty(bossText))
                    bossText = preset.TryGetIntStr("override_boss");
                if (!string.IsNullOrEmpty(bossText)
                    && int.TryParse(bossText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bossValue))
                {
                    humanoid.m_boss = bossValue != 0;
                }
            }

            // FiresCustomizationStudio owns bone semantics; the blob already rode the ZDO.
            string bones = preset.Bones;
            if (!string.IsNullOrEmpty(bones))
            {
                var character = instance.GetComponent<Character>();
                if (character != null) FiresCore.Bridge.BoneEditingBridge.SetBoneData(character, bones);
            }
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
            return key.Length > 0;
        }

        private static bool TryParseVector3(string text, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split(KeyValueSeparator);
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
            result = new Vector3(x, y, z);
            return true;
        }

        // A stamped creature drops its preset's table through VANILLA's own drop path: we swap
        // the instance's m_drops for the preset's entries and let CharacterDrop roll them.
        //
        // Answered through Core's MonsterPresetBridge rather than from a Harmony patch here.
        // Core's CreatureOverrides ALREADY prefixes CharacterDrop.GenerateDropList and assigns
        // m_drops for its per-species overrides. Two prefixes assigning the same field, neither
        // declaring a priority, meant a creature with both a preset and an override got whichever
        // mod Harmony happened to run last — silently, and differently per load order. Core now
        // asks the bridge first and stops if we claim the instance, so precedence lives in one
        // place: a per-instance preset beats a per-species override.
        //
        // This also replaced EWP's `drops:` on the destroy rule (which EWP documents as
        // unsupported for `type: destroy`) plus the postfix that cleared vanilla's list to stop
        // the two double-dropping. Presets with no items leave vanilla's list untouched.
        public static bool TryApplyPresetDrops(CharacterDrop characterDrop)
        {
            if (characterDrop == null) return false;
            var netView = characterDrop.GetComponent<ZNetView>();
            if (netView == null || !netView.IsValid()) return false;
            var zdo = netView.GetZDO();
            if (zdo == null) return false;

            string presetName = zdo.GetString(KeyAppliedPresetName, "");
            if (string.IsNullOrEmpty(presetName)) return false;

            if (!VAMonsterPresetStore.TryGet(presetName, out var preset)) return false;
            if (preset.items == null || preset.items.Count == 0) return false;

            var drops = BuildPresetDrops(preset);
            if (drops == null) return false;

            characterDrop.m_drops = drops;
            return true;
        }

        private static readonly HashSet<string> _inexpressibleFieldsWarned =
            new HashSet<string>(StringComparer.Ordinal);

        // vanilla's Drop carries prefab, an amount range and a chance, and nothing else,
        // so an entry's quality or variant cannot survive this path. Warned once per
        // preset rather than dropped silently — the alternative is spawning the items by
        // hand and losing everything else vanilla does for free.
        private static List<CharacterDrop.Drop> BuildPresetDrops(VAMonsterPreset preset)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return null;

            int? pickCount = VAItemSpec.RollCount(preset.itemAmount);
            var selected = SelectEntries(preset, pickCount);
            if (selected == null || selected.Count == 0) return null;

            var drops = new List<CharacterDrop.Drop>(selected.Count);
            bool sawInexpressible = false;

            foreach (var entry in selected)
            {
                if (entry == null || string.IsNullOrEmpty(entry.prefab)) continue;
                var prefab = scene.GetPrefab(entry.prefab);
                if (prefab == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterConfigApplier — preset '{preset.name}' drops unknown prefab '{entry.prefab}'; skipping that entry.");
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.quality) || !string.IsNullOrEmpty(entry.variant))
                    sawInexpressible = true;

                // Rolled here rather than left to vanilla: its m_dontScale path is
                // Random.Range(min, max), which is EXCLUSIVE of max, so a "2;4" stack
                // would never drop 4. Rolling through the same helper the chest path
                // uses also keeps a stack spec meaning one thing in both places.
                int amount = VAItemSpec.RollStack(entry.stack);
                drops.Add(new CharacterDrop.Drop
                {
                    m_prefab = prefab,
                    m_amountMin = amount,
                    m_amountMax = amount,
                    // A pick-N selection has already decided this entry drops, so its own
                    // chance must not roll a second time.
                    m_chance = pickCount.HasValue ? 1f : entry.chance,
                    // The preset's table is the whole answer: a star-2 draugr shouldn't
                    // silently double it, and the stack spec already says how many.
                    m_levelMultiplier = false,
                    m_onePerPlayer = false,
                    m_dontScale = true,
                });
            }

            if (sawInexpressible && _inexpressibleFieldsWarned.Add(preset.name))
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterConfigApplier — preset '{preset.name}' sets item quality or variant, which vanilla's CharacterDrop.Drop has no field for. Those items drop at default quality and variant.");

            return drops.Count > 0 ? drops : null;
        }

        // Mirrors VAChestConfigApplier.RollItems: no itemAmount means every entry rolls
        // against its own chance (which vanilla's own drop loop does, so the table passes
        // straight through), and an itemAmount means N chance-weighted picks without
        // replacement. The pick happens inside GenerateDropList, which runs once per
        // death, so it re-rolls each time instead of freezing one set at spawn.
        private static List<VAItemEntry> SelectEntries(VAMonsterPreset preset, int? pickCount)
        {
            if (!pickCount.HasValue) return preset.items;

            var pool = new List<VAItemEntry>(preset.items.Count);
            foreach (var entry in preset.items)
            {
                if (entry == null || string.IsNullOrEmpty(entry.prefab)) continue;
                if (entry.chance <= 0f) continue;
                pool.Add(entry);
            }

            int picks = Math.Min(pickCount.Value, pool.Count);
            var picked = new List<VAItemEntry>(picks);
            for (int i = 0; i < picks; i++)
            {
                float totalWeight = 0f;
                foreach (var entry in pool) totalWeight += entry.chance;
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
                picked.Add(pool[pickedIndex]);
                pool.RemoveAt(pickedIndex);
            }

            return picked;
        }

        // FAP-mode respawn. Stands down whenever EWP is the active backend (its rule
        // yaml's `delay:` handles respawn), and only the ZDO owner schedules, so a
        // death seen by every peer spawns one creature.
        [HarmonyPatch(typeof(Character), "OnDeath")]
        public static class Character_OnDeath_VARespawn
        {
            [HarmonyPostfix]
            public static void Postfix(Character __instance)
            {
                if (__instance == null) return;

                string prefabName = __instance.gameObject != null ? __instance.gameObject.name : "<null-go>";
                var netView = __instance.GetComponent<ZNetView>();
                bool netViewValid = netView != null && netView.IsValid();
                bool isOwner = netViewValid && netView.IsOwner();
                var zdo = netViewValid ? netView.GetZDO() : null;
                float delay = zdo != null ? zdo.GetFloat(KeyRespawnSeconds, 0f) : 0f;
                string presetName = zdo != null ? zdo.GetString(KeyAppliedPresetName, string.Empty) : string.Empty;
                bool ewpHandlesRespawn = VAEwpSoftDep.IsActive;

                // One line per death answers "why did/didn't this respawn?" from a single grep.
                VaPieces.Log?.LogDebug(   // 1.1.207: per death = Debug (Fire: Message-level defaults)
                    $"[FAP-RespawnGate] OnDeath prefab='{prefabName}' " +
                    $"ewpInstalled={VAEwpDetector.IsEwpInstalled} useEwpCfg={ewpHandlesRespawn} ewpHandles={ewpHandlesRespawn} " +
                    $"nviewValid={netViewValid} isOwner={isOwner} " +
                    $"va_respawn_seconds={delay:F2} appliedPreset='{presetName}'");

                if (ewpHandlesRespawn) return;
                if (!netViewValid || !isOwner) return;
                if (zdo == null) return;
                if (delay <= 0f) return;

                prefabName = VAChestConfigApplier.StripCloneSuffix(prefabName);
                Vector3 position = __instance.transform.position;
                Quaternion rotation = __instance.transform.rotation;

                VaPieces.Log?.LogDebug(
                    $"[FAP-RespawnGate] OnDeath SCHEDULING respawn prefab='{prefabName}' " +
                    $"delay={delay:F2}s preset='{presetName}' pos={position}");

                if (VaPieces.Instance != null)
                    VaPieces.Instance.StartCoroutine(RespawnAfterDelay(prefabName, position, rotation, presetName, delay));
            }

            private static System.Collections.IEnumerator RespawnAfterDelay(
                string prefabName, Vector3 position, Quaternion rotation, string presetName, float delay)
            {
                yield return new WaitForSeconds(delay);

                var scene = ZNetScene.instance;
                if (scene == null) yield break;
                var prefab = scene.GetPrefab(prefabName);
                if (prefab == null) yield break;

                var instance = UnityEngine.Object.Instantiate(prefab, position, rotation);

                if (!string.IsNullOrEmpty(presetName)
                    && VAMonsterPresetStore.TryGet(presetName, out var preset))
                {
                    StampPresetAndRefresh(preset, instance);
                }
            }
        }
    }

    // Any plugin GUID containing "expand_world_prefabs" counts. Resolved once; the
    // BepInEx plugin scan is complete before any patch fires.
    public static class VAEwpDetector
    {
        private const string ExpandWorldPrefabsGuidFragment = "expand_world_prefabs";

        private static bool? _cached;

        public static bool IsEwpInstalled
        {
            get
            {
                if (_cached.HasValue) return _cached.Value;
                bool found = false;
                try
                {
                    var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                    if (infos != null)
                    {
                        foreach (var guid in infos.Keys)
                        {
                            if (string.IsNullOrEmpty(guid)) continue;
                            if (guid.IndexOf(ExpandWorldPrefabsGuidFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    return false;
                }
                _cached = found;
                return found;
            }
        }
    }
}
