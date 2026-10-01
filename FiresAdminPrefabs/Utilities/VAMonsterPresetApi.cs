using System;
using System.Collections.Generic;
using System.Globalization;
using FiresCore.Bridge;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // FAP's side of Core's MonsterPresetBridge, so another mod's creature editor
    // (FiresAllTheItems) can save a preset without referencing FAP.
    //
    // Deliberately thin: Save already writes the yaml, which triggers the debounced
    // admin push AND, on whichever side receives it, the store's hot reload calling
    // VAMonsterEwpRuleWriter.RegenerateRules. So the whole chain doc 25 describes is
    // one Save call, and duplicating any of it here would double-write.
    //
    // Rules are NOT force-regenerated on this path, matching FAP's own Save button:
    // rules reference data entries by name, so an edit propagates without rule churn,
    // and placement is what materialises a new preset's rules.
    public static class VAMonsterPresetApi
    {
        private const string RespawnSecondsKey = VerdantsAscent.Patches.VAMonsterConfigApplier.KeyRespawnSeconds;

        public static void Register()
        {
            MonsterPresetBridge.SaveHandler = Save;
            MonsterPresetBridge.DeleteHandler = Delete;
            MonsterPresetBridge.ListNamesHandler = ListNames;
            MonsterPresetBridge.GetHandler = Get;
            MonsterPresetBridge.IsPresetInstanceHandler = IsPresetInstance;

            // Preset drops go through Core's single GenerateDropList hook. FAP used to prefix
            // that method itself, which raced Core's own prefix for the same field.
            MonsterPresetBridge.ApplyInstanceDropsHandler =
                VerdantsAscent.Patches.VAMonsterConfigApplier.TryApplyPresetDrops;
        }

        private static bool Save(MonsterPresetRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Name))
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterPresetApi — save refused, a preset name is required.");
                return false;
            }
            if (!Permitted($"save preset '{request.Name}'")) return false;

            var preset = ToPreset(request);
            if (!VAMonsterPresetStore.Save(preset))
            {
                Debug.LogError($"{VaPieces.PluginName}: VAMonsterPresetApi — the store refused to write preset '{request.Name}'.");
                return false;
            }

            Debug.Log($"{VaPieces.PluginName}: VAMonsterPresetApi saved preset '{preset.name}' ({preset.items.Count} drop row(s)) via Core's MonsterPresetBridge.");
            return true;
        }

        private static bool Delete(string name)
        {
            if (!Permitted($"delete preset '{name}'")) return false;
            if (!VAMonsterPresetStore.Delete(name)) return false;

            // Delete DOES regenerate: a rule filtering on a preset key that no longer
            // exists would keep firing against the stale key.
            VAMonsterEwpRuleWriter.RegenerateRules();
            Debug.Log($"{VaPieces.PluginName}: VAMonsterPresetApi deleted preset '{name}' via Core's MonsterPresetBridge.");
            return true;
        }

        // Read is NOT admin-gated: it writes nothing, and a non-admin editor still needs to
        // show what is configured. Save and Delete carry the gate.
        //
        // Round-trips on purpose. Copies of the lists, so a caller editing what it got back
        // cannot mutate the live store behind the store's own lock. RespawnSeconds is left in
        // Floats as the stored `va_respawn_seconds` line rather than lifted into the typed
        // field: Save only adds that line when it is absent, so handing this straight back
        // preserves the preset exactly instead of writing the value twice.
        private static MonsterPresetRequest Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!VAMonsterPresetStore.TryGet(name, out var preset) || preset == null) return null;

            var request = new MonsterPresetRequest
            {
                Name = preset.name,
                Creature = preset.Creature,
                ItemAmount = preset.itemAmount,
            };
            CopyLines(preset.ints, request.Ints);
            CopyLines(preset.floats, request.Floats);
            CopyLines(preset.strings, request.Strings);
            CopyLines(preset.vecs, request.Vecs);

            if (preset.items != null)
            {
                foreach (var item in preset.items)
                {
                    if (item == null) continue;
                    request.Items.Add(new MonsterPresetItem
                    {
                        Prefab = item.prefab,
                        Chance = item.chance,
                        Stack = item.stack,
                        Quality = item.quality,
                        Variant = item.variant,
                    });
                }
            }

            return request;
        }

        // Core asks this before applying any per-species rule. A preset is authored per instance
        // and IS the whole answer for that creature, so a species rule for Draugr must not touch
        // a preset Draugr — it was re-rolling stars the preset had set explicitly.
        //
        // Reads the ZDO directly rather than the store: the question is "was this spawned from a
        // preset", which stays true even if the named preset has since been deleted.
        private static bool IsPresetInstance(ZDO zdo)
        {
            if (zdo == null) return false;
            return !string.IsNullOrEmpty(
                zdo.GetString(VerdantsAscent.Patches.VAMonsterConfigApplier.KeyAppliedPresetName, ""));
        }

        private static List<string> ListNames()
        {
            var names = new List<string>(VAMonsterPresetStore.Count);
            foreach (var name in VAMonsterPresetStore.All.Keys) names.Add(name);
            return names;
        }

        // Fail closed. A preset write reaches the server through the asset push, so this is
        // the same gate FAP's own editor sits behind rather than a courtesy check.
        private static bool Permitted(string what)
        {
            bool open;
            try { open = VerdantsAscent.Managers.VAVanillaPieceManager.IsAdminGateOpen(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterPresetApi — admin check threw, refusing to {what}: {ex.Message}");
                return false;
            }
            if (!open)
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterPresetApi — not admin, refusing to {what}.");
            return open;
        }

        private static VAMonsterPreset ToPreset(MonsterPresetRequest request)
        {
            var preset = new VAMonsterPreset { name = request.Name };

            CopyLines(request.Ints, preset.ints);
            CopyLines(request.Floats, preset.floats);
            CopyLines(request.Strings, preset.strings);
            CopyLines(request.Vecs, preset.vecs);

            // After the strings copy: the setter rewrites the va_creature line, so a caller
            // that put one in Strings as well does not end up with two.
            if (!string.IsNullOrEmpty(request.Creature)) preset.Creature = request.Creature;

            if (request.RespawnSeconds > 0f && preset.TryGetFloatStr(RespawnSecondsKey) == null)
                preset.floats.Add($"{RespawnSecondsKey}, {request.RespawnSeconds.ToString("0.##", CultureInfo.InvariantCulture)}");

            preset.itemAmount = string.IsNullOrEmpty(request.ItemAmount) ? null : request.ItemAmount;

            if (request.Items != null)
            {
                foreach (var item in request.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Prefab)) continue;
                    preset.items.Add(new VAItemEntry
                    {
                        prefab = item.Prefab,
                        chance = item.Chance,
                        stack = item.Stack,
                        quality = item.Quality,
                        variant = item.Variant,
                    });
                }
            }

            return preset;
        }

        private static void CopyLines(List<string> from, List<string> to)
        {
            if (from == null) return;
            foreach (var line in from)
                if (!string.IsNullOrEmpty(line)) to.Add(line);
        }
    }
}
