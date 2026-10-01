using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Generates the EWP destroy/respawn rules for monster presets into
    // config/expand_world/expand_prefabs_FAP_presets.yaml.
    //
    // Three rules per preset, plus one global sweep:
    //
    //   1. creature destroy  → schedule the respawn AND poke a nearby anchor
    //   2. anchor mark-dead  → anchor records the death time on its own ZDO
    //   3. anchor recover    → the global sweep pokes it; if the cooldown has
    //                          elapsed it respawns the creature
    //
    // Rules 2 and 3 exist because rule 1's scheduled respawn is lost on a server
    // restart. The anchor's ZDO survives, so the sweep restores anything that died
    // while the server was down.
    //
    // The preset name doubles as the ZDO int key that scopes each rule to its own
    // preset — unique by definition (the store is keyed by it) and readable in the
    // yaml. `injectData: true` bakes the named data entry onto the ZDO before the
    // creature's Awake, so vanilla LoadFields can't cache prefab defaults first.
    //
    // Every side that runs FAP writes its own identical local copy; no RPC sync.
    public static class VAMonsterEwpRuleWriter
    {
        // Fires once per in-game hour (~75 real seconds) regardless of player
        // movement. A `body_vel` trigger would scan on every movement tick — hundreds
        // of scans per second per player — for the same result.
        private const string RecoverySweepTrigger = "- type: time, hour 0;23";

        // Time triggers fire from the origin, and EWP's >10000 threshold flips it into
        // the global-iterate path, which is what a world-wide anchor scan needs.
        // The quotes are mandatory, not style. A bare `*` opens a YAML alias node, so
        // `prefab: *` fails to parse and takes the WHOLE rule file down with it — every
        // preset's rules, not just this one. VAChestEwpRuleWriter already quotes it.
        private const string WildcardPrefabYaml = "\"*\"";

        private const int GlobalSweepMaxDistance = 99999;
        private const int AnchorPokeMaxDistance = 5;
        private const float DefaultStackingRadius = 100f;

        public static string ExpandWorldDir =>
            Path.Combine(Paths.ConfigPath, "expand_world");
        public static string RuleFilePath =>
            Path.Combine(ExpandWorldDir, "expand_prefabs_FAP_presets.yaml");

        private static readonly object _ioLock = new object();

        // Written at mod load so EWP's file watcher has something to watch from the
        // start. Never overwrites existing content — placement is what materializes
        // the rules.
        public static void EnsureFileExists()
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(ExpandWorldDir);
                    if (File.Exists(RuleFilePath)) return;
                    File.WriteAllText(RuleFilePath, BuildHeader());
                    Debug.Log($"{VaPieces.PluginName}: VAMonsterEwpRuleWriter created empty rule yaml at {RuleFilePath} (rules will populate on creature placement).");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{VaPieces.PluginName}: VAMonsterEwpRuleWriter — could not ensure file exists at {RuleFilePath}: {ex.Message}");
                }
            }
        }

        private static string BuildHeader()
        {
            var header = new StringBuilder();
            header.AppendLine("# expand_prefabs_FAP_presets.yaml");
            header.AppendLine("# Auto-managed by FiresAdminPrefabs.");
            header.AppendLine("# Rules appear here as you PLACE creatures carrying a configured preset.");
            header.AppendLine("# Saving a preset alone does not write rules — placement is the trigger so");
            header.AppendLine("# the file stays minimal until rules are actually needed.");
            header.AppendLine("# Preset definitions live in BepInEx/config/data/data_FAP_presets.yaml.");
            header.AppendLine();
            return header.ToString();
        }

        // Rebuilt from scratch on creature placement and on preset delete. Saving a
        // preset doesn't call this: rules reference data entries by name, so edits
        // propagate without rule churn.
        //
        // ignoreGate bypasses only the EWP-active check, for the mod-load write.
        public static void RegenerateRules(bool ignoreGate = false)
        {
            if (!ignoreGate && !VAEwpSoftDep.IsActive)
            {
                Debug.Log(
                    $"{VaPieces.PluginName}: VAMonsterEwpRuleWriter — " +
                    $"EWP soft-dep not active; skipping rule regeneration. " +
                    $"FAP OnDeath will handle respawn for preset-stamped monsters.");
                return;
            }

            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(ExpandWorldDir);

                    var yaml = new StringBuilder();
                    yaml.Append(BuildHeader());

                    if (AnyPresetHasRespawn())
                        AppendGlobalRecoverySweep(yaml);

                    int rulesEmitted = 0;
                    foreach (var preset in VAMonsterPresetStore.All.Values)
                    {
                        if (preset == null || string.IsNullOrEmpty(preset.name)) continue;

                        float respawnSeconds = ParseFloat(preset.TryGetFloatStr("va_respawn_seconds"), 0f);
                        if (respawnSeconds <= 0f) continue;

                        AppendPresetRules(yaml, preset, respawnSeconds);
                        rulesEmitted++;
                    }

                    WriteAtomic(yaml.ToString());

                    Debug.Log($"{VaPieces.PluginName}: VAMonsterEwpRuleWriter wrote {rulesEmitted} EWP destroy-rule(s) to {RuleFilePath}.");

                    // Mirror the rules (and the preset data) to the server so they fire
                    // there. Debounced, config-gated, admin + remote-server only.
                    VerdantsAscent.Sync.VAFapAssetSync.RequestAutoPush();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{VaPieces.PluginName}: VAMonsterEwpRuleWriter — write failed: {ex.Message}");
                }
            }
        }

        private static bool AnyPresetHasRespawn()
        {
            foreach (var preset in VAMonsterPresetStore.All.Values)
            {
                if (preset == null) continue;
                if (ParseFloat(preset.TryGetFloatStr("va_respawn_seconds"), 0f) > 0f) return true;
            }
            return false;
        }

        // One sweep for every preset rather than one per preset: a single filter pass
        // over the world's ZDOs instead of N. Each anchor filters itself by its own
        // per-preset key inside its handler, so wrong-preset anchors no-op fast.
        private static void AppendGlobalRecoverySweep(StringBuilder yaml)
        {
            yaml.AppendLine(RecoverySweepTrigger);
            yaml.AppendLine("  poke:");
            yaml.AppendLine($"    - prefab: {VAMonsterAnchor.AnchorPrefabName}");
            yaml.AppendLine("      filters:");
            yaml.AppendLine("      - int, va_anchor, 1");
            yaml.AppendLine($"      - float, {VAMonsterAnchor.DeadAtKey}, 0.001;<time>");
            yaml.AppendLine($"      maxDistance: {GlobalSweepMaxDistance}");
            yaml.AppendLine("      pars: vaRecover");
            yaml.AppendLine();
        }

        private static void AppendPresetRules(StringBuilder yaml, VAMonsterPreset preset, float respawnSeconds)
        {
            string presetKey = preset.name;

            string creaturePrefab = preset.Creature;
            bool hasCreaturePrefab = !string.IsNullOrEmpty(creaturePrefab);
            float stackingRadius = ParseFloat(preset.TryGetFloatStr("va_prevent_stacking_radius"), DefaultStackingRadius);

            // `*` when the preset names no creature, NOT `ZNetView`. EWP resolves a rule's
            // prefab through PrefabHelper.ParsePrefabs, which tries the wildcard forms, then
            // ZNetScene.m_namedPrefabs, then a value group, and otherwise logs
            // "Failed to resolve prefab" and returns null — so the rule registers against
            // ZERO prefab keys and never fires at all. `ZNetView` is a component, never a
            // prefab name, so every such rule FAP wrote was silently dead rather than
            // matching widely as its old comment claimed. `*` expands to every prefab and
            // lets `filters:` do the narrowing, which is what the chest rule already does.
            //
            // Doc 25 suggested the `creature` group for this case, but value groups come
            // from EWD data files and no `creature` group is defined, so it would resolve
            // to null the same way.
            string matchPrefab = hasCreaturePrefab ? creaturePrefab : WildcardPrefabYaml;
            if (!hasCreaturePrefab)
                Debug.LogWarning($"{VaPieces.PluginName}: VAMonsterEwpRuleWriter — preset '{presetKey}' names no creature, so its destroy rule matches every prefab and is narrowed by the preset key alone. Set the preset's creature to make it cheap, and to get restart recovery.");

            // `filters:` matches the destroyed ZDO's data. A top-level `data:` would be
            // a write target instead, and EWP force-clears it on destroy actions.
            // `triggerRules: true` puts the respawned ZDO back into the rule system so
            // its own destroy rule fires next time it dies.
            yaml.AppendLine($"- prefab: {matchPrefab}");
            yaml.AppendLine("  type: destroy");
            yaml.AppendLine("  filters:");
            yaml.AppendLine($"  - int, {presetKey}, 1");

            // No `drops:` here. Preset loot goes through vanilla's CharacterDrop — see
            // CharacterDrop_GenerateDropList_VAPresetDrops — which gets level scaling and
            // one-per-player for free and works with EWP absent.

            yaml.AppendLine("  spawn:");
            yaml.AppendLine("    - prefab: <prefab>");
            yaml.AppendLine($"      delay: {respawnSeconds:F2}");
            yaml.AppendLine($"      data: {preset.name}");
            yaml.AppendLine("      triggerRules: true");
            AppendStackingGuard(yaml, creaturePrefab, stackingRadius, hasCreaturePrefab);

            // The restart-recovery chain spawns the creature BY NAME, so without a creature
            // there is nothing for it to spawn. It used to be emitted anyway with `ZNetView`
            // as the spawn target, which EWP could not resolve — three rules that could
            // never do anything, one of them warning on every load. In-session respawn is
            // unaffected: the destroy rule's own `spawn: <prefab>` template resolves to
            // whatever died.
            if (!hasCreaturePrefab)
            {
                yaml.AppendLine();
                return;
            }

            yaml.AppendLine("  poke:");
            yaml.AppendLine($"    - prefab: {VAMonsterAnchor.AnchorPrefabName}");
            yaml.AppendLine($"      data: int, {VAMonsterAnchor.AnchorKeyPrefix}{presetKey}, 1");
            yaml.AppendLine($"      maxDistance: {AnchorPokeMaxDistance}");
            yaml.AppendLine($"      pars: vaMarkDead_{presetKey}");
            yaml.AppendLine();

            yaml.AppendLine($"- prefab: {VAMonsterAnchor.AnchorPrefabName}");
            yaml.AppendLine($"  type: poke, vaMarkDead_{presetKey}");
            yaml.AppendLine($"  data: float, {VAMonsterAnchor.DeadAtKey}, <time>");
            yaml.AppendLine();

            yaml.AppendLine($"- prefab: {VAMonsterAnchor.AnchorPrefabName}");
            yaml.AppendLine($"  type: poke, vaRecover");
            yaml.AppendLine("  filters:");
            yaml.AppendLine($"  - int, {VAMonsterAnchor.AnchorKeyPrefix}{presetKey}, 1");
            yaml.AppendLine($"  - float, {VAMonsterAnchor.DeadAtKey}, 0.001;<time>-{respawnSeconds:F0}");
            yaml.AppendLine("  spawn:");
            yaml.AppendLine($"    - prefab: {creaturePrefab}");
            yaml.AppendLine($"      data: {preset.name}");
            yaml.AppendLine("      triggerRules: true");
            AppendStackingGuard(yaml, creaturePrefab, stackingRadius, hasCreaturePrefab);
            yaml.AppendLine($"  data: float, {VAMonsterAnchor.DeadAtKey}, 0");
            yaml.AppendLine();
        }

        private static void AppendStackingGuard(StringBuilder yaml, string creaturePrefab, float radius, bool hasCreaturePrefab)
        {
            if (radius <= 0f || !hasCreaturePrefab) return;
            yaml.AppendLine("  bannedObjects:");
            yaml.AppendLine($"    - prefab: {creaturePrefab}");
            yaml.AppendLine($"      maxDistance: {radius:F1}");
        }

        private static void WriteAtomic(string contents)
        {
            string tempPath = RuleFilePath + ".tmp";
            File.WriteAllText(tempPath, contents);
            if (File.Exists(RuleFilePath)) File.Delete(RuleFilePath);
            File.Move(tempPath, RuleFilePath);
            VAEwpYamlValidator.Validate(RuleFilePath, VAEwpYamlValidator.FileKind.Rules);
        }

        private static float ParseFloat(string text, float fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }
    }
}
