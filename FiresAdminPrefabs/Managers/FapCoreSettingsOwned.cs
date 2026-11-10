using System;
using BepInEx.Configuration;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.1.345 (Fire-approved Core config move, plan Tools\CORE_CONFIG_MOVE_PLAN_20261030.md, [wishbone]): Core's "[Building] New piece
    /// faces away from you" is FAP's (Core gated it FAP-only). Bound here under Core's section + key, its value copied once from Core's cfg
    /// on the first load, handed to Core's NewPieceFacesAway code with CoreSettings.Provide (without FAP Core uses its default, off).
    /// Same shape as FAT's FatCoreSettingsOwned / RPGClasses' CoreSettingsOwned; on a Core older than 0.2.1036 it logs once.
    /// </summary>
    internal static class FapCoreSettingsOwned
    {
        private const string Owner = "FiresAdminPrefabs";

        internal static void Bind(ConfigFile config)
        {
            try { BindAll(config); }
            catch (Exception e) when (e is MissingMethodException || e is TypeLoadException || e is MissingMemberException)
            {
                VaPieces.Log?.LogInfo("[CoreSettings] the loaded Core has no CoreSettings (older than 0.2.1036): Core keeps its own 'New piece faces away from you'");
            }
        }

        private static void BindAll(ConfigFile config)
        {
            const string section = "Building", key = "New piece faces away from you";
            var d = new ConfigDefinition(section, key);
            bool hadOwn = HasSaved(config, d);
            var entry = config.Bind(d, false, new ConfigDescription(
                "When the build tool is taken out and whenever a different piece is picked, the piece is turned to face the way the camera looks, "
                + "so forward/back/left/right moves (e.g. Infinity Hammer's arrow keys) go away/toward/left/right. Rotating it yourself is kept while "
                + "you stay on the same piece. Off: vanilla (the last rotation is kept for every piece)."));
            if (!hadOwn && FiresCore.Config.CoreSettings.TryReadCoreSaved(section, key, out string old) && bool.TryParse(old, out bool v))
            {
                entry.Value = v;
                VaPieces.Log?.LogInfo($"[CoreSettings] migrated {section}/{key} from Core: {v}");
            }
            FiresCore.Config.CoreSettings.Provide(section, key, () => entry.Value, Owner);
        }

        private static bool HasSaved(ConfigFile config, ConfigDefinition def)
        {
            try
            {
                var orphans = HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
                return orphans != null && orphans.ContainsKey(def);
            }
            catch { return false; }
        }
    }
}
