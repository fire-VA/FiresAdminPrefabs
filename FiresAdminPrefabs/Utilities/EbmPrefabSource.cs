using System;
using System.Reflection;

namespace VerdantsAscent.Utilities
{
    // 1.1.413 ([ghost]'s EBM 1.2.210, Tools\_port_ebm1210_ghost\HANDOVER_1210b_pushes.md): EBM used to read FAP's FapExternalPrefabs.SourceBundleOf
    // by name to name a bundle prefab's source bundle in its refusal lines. EBM looks nobody up any more, so FAP pushes it once:
    // EbmApi.RegisterPrefabSourceLookup(Func<string, string>). By name, no compile reference; an EBM without it = one Info line, nothing pushed.
    // Without the push EBM's lines name the assembly instead of the bundle (log text only, nothing else changes).
    internal static class EbmPrefabSource
    {
        private static bool s_done;

        internal static void Push()
        {
            if (s_done) return;
            Assembly ebm = null;
            foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
            {
                var inst = info?.Instance; if (inst == null) continue;
                var asm = inst.GetType().Assembly;
                if (asm.GetName().Name == "FiresEasyBakeMeshes") { ebm = asm; break; }
            }
            if (ebm == null) return;   // no EBM: nothing to tell
            s_done = true;
            var m = ebm.GetType("FiresEasyBakeMeshes.EbmApi", false)?.GetMethod("RegisterPrefabSourceLookup", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Func<string, string>) }, null);
            if (m == null) { VaPieces.Log?.LogInfo("[FapEbm] this EBM has no EbmApi.RegisterPrefabSourceLookup (EBM 1.2.210+): bundle names not pushed."); return; }
            m.Invoke(null, new object[] { new Func<string, string>(global::VerdantsAscent.Api.FapExternalPrefabs.SourceBundleOf) });
            VaPieces.Log?.LogInfo("[FapEbm] FAP's bundle-source lookup registered with EBM (EbmApi.RegisterPrefabSourceLookup).");
        }
    }
}
