using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace VerdantsAscent.Utilities
{
    // 1.1.405 ([visual] + [lead] 11-08): Core's far systems (FiresCore.Far.ImpostorEngine / FarCache / FarLights / FarBuildings) bind
    // their [Far ...] keys only when an OWNER calls Initialize(ConfigFile). FAT's FarBridge.InitCoreFar was the only caller, so with
    // FAT absent (parked for the BlumpkinPre tests) Fire's far buildings never started although FAP co-owns FarBuildings. When FAT is
    // not loaded, FAP makes the same guarded calls with its own config. Core's Initialize is idempotent, so a FAT loaded later changes
    // nothing. By presence: an older Core has none of these types and nothing happens.
    internal static class FapCoreFar
    {
        private const string FatGuid = "com.Fire.FiresAdminTerrain";

        internal static void InitIfFatAbsent(ConfigFile cfg)
        {
            if (cfg == null) return;
            if (Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(FatGuid)) return;   // FAT initializes them itself
            var done = new List<string>();
            // 1.1.411 ([generator] M22: Core's Far\ moves into EBM 1.2.210 and Core deletes it): look in Core's own assembly with
            // GetType(name, false). AccessTools.TypeByName warned "Could not find type named ..." for each missing type.
            System.Reflection.Assembly core = null;
            try { core = typeof(FiresCore.Lifecycle.FiresMod).Assembly; } catch { }
            foreach (var name in new[] { "FiresCore.Far.ImpostorEngine", "FiresCore.Far.FarCache", "FiresCore.Far.FarLights", "FiresCore.Far.FarBuildings" })
            {
                Type t = null;
                try { t = core?.GetType(name, false); } catch { }
                var m = t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ConfigFile) }, null);
                if (m == null) continue;
                try { m.Invoke(null, new object[] { cfg }); done.Add(t.Name); }
                catch (Exception e) { VerdantsAscent.VaPieces.Log?.LogWarning($"[FarForest] {name}.Initialize threw: {e.GetBaseException().Message}"); }
            }
            VerdantsAscent.VaPieces.Log?.LogInfo(done.Count > 0
                ? $"[FarForest] Core far systems initialized for FAP (FAT absent): {string.Join(", ", done)}."
                : "[FarForest] FAT absent and this Core has no far systems (EBM 1.2.210+ starts its own): nothing to initialize.");
        }
    }
}
