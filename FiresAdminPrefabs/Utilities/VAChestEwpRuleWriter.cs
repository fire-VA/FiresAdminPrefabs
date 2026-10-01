using System;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Emits ONE generic EWP rule that respawns every FAP-stamped chest.
    // Container.m_autoDestroyEmpty (set via the preset's bools list +
    // ZNetView.LoadFields) handles the destroy when a chest empties out;
    // EWP catches the destroy event and fires this rule, reading the
    // preset name + delay from the destroyed ZDO's own fields so one
    // rule covers all presets.
    //
    // Output: BepInEx/config/expand_world/expand_prefabs_FAP_chests.yaml
    // Per-preset data: BepInEx/config/data/data_FAP_chests.yaml
    public static class VAChestEwpRuleWriter
    {
        public static string ExpandWorldDir =>
            Path.Combine(Paths.ConfigPath, "expand_world");
        public static string RuleFilePath =>
            Path.Combine(ExpandWorldDir, "expand_prefabs_FAP_chests.yaml");

        private static readonly object _ioLock = new object();

        // Writes the rule if the file is missing. Existing files are left
        // alone so admins can hand-edit (e.g. add a bannedObjects clause).
        public static void EnsureFileExists()
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(ExpandWorldDir);
                    if (File.Exists(RuleFilePath)) return;
                    File.WriteAllText(RuleFilePath, BuildGenericRuleYaml());
                    VAEwpYamlValidator.Validate(RuleFilePath, VAEwpYamlValidator.FileKind.Rules);
                    Debug.Log($"{VaPieces.PluginName}: VAChestEwpRuleWriter wrote the generic chest-respawn rule to {RuleFilePath}.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{VaPieces.PluginName}: VAChestEwpRuleWriter — could not ensure file exists at {RuleFilePath}: {ex.Message}");
                }
            }
        }

        // Force-rewrite, used by the reset admin tool. Overwrites existing
        // content via a tmp + atomic move.
        public static void RegenerateRules()
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(ExpandWorldDir);
                    string tmp = RuleFilePath + ".tmp";
                    File.WriteAllText(tmp, BuildGenericRuleYaml());
                    if (File.Exists(RuleFilePath)) File.Delete(RuleFilePath);
                    File.Move(tmp, RuleFilePath);
                    VAEwpYamlValidator.Validate(RuleFilePath, VAEwpYamlValidator.FileKind.Rules);
                    Debug.Log($"{VaPieces.PluginName}: VAChestEwpRuleWriter regenerated the generic chest-respawn rule at {RuleFilePath}.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{VaPieces.PluginName}: VAChestEwpRuleWriter — regenerate failed: {ex.Message}");
                }
            }
        }

        private static string BuildGenericRuleYaml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# expand_prefabs_FAP_chests.yaml");
            sb.AppendLine("# Auto-managed by FiresAdminPrefabs.");
            sb.AppendLine("# Single generic destroy rule that respawns every FAP-stamped chest");
            sb.AppendLine("# whose ZDO carries va_chest=1. Vanilla Container.m_autoDestroyEmpty");
            sb.AppendLine("# (set via the preset's bools list + ZNetView.LoadFields) makes the");
            sb.AppendLine("# chest auto-destroy when empty — same mechanism vanilla loot crates");
            sb.AppendLine("# use. EWP catches the destroy event and fires this respawn rule.");
            sb.AppendLine("# Per-preset data lives in BepInEx/config/data/data_FAP_chests.yaml.");
            sb.AppendLine();
            sb.AppendLine("- prefab: \"*\"");
            sb.AppendLine("  type: destroy");
            sb.AppendLine("  filters:");
            sb.AppendLine("  - int, va_chest, 1");
            sb.AppendLine("  spawn:");
            sb.AppendLine("    - prefab: <prefab>");
            sb.AppendLine("      delay: <float_va_respawn_seconds>");
            sb.AppendLine("      data: <string_va_chest_preset>");
            sb.AppendLine("      triggerRules: true");
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
