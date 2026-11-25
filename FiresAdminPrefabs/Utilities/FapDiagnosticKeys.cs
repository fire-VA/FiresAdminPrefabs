using BepInEx.Configuration;

namespace VerdantsAscent.Utilities
{
    // 1.1.422 ([ghost], lead 11-12; Fire 11:35: "everything in core and fap should be as clean and efficient as possible so that we're not adding too much
    // to frame time"). Three old diagnostic probes hooked hot vanilla paths for every player:
    //  - Destroy watch (Patches\VAFapDestroyWatchPatch.cs): 5 prefixes on ZNetView.OnDestroy / ZNetScene.Destroy / ZNetView.Destroy / WearNTear.Destroy /
    //    TreeBase.RPC_Damage; every unloaded object paid a name string + substring to watch two Norger oaks and monster ghosts.
    //  - Light load probe (Patches\VALightLoadProbe.cs): a ZNetView.LoadFields postfix on every created object, two string-hashed ZDO reads each.
    //  - Asset load window (Patches\AssetLoadWindowProbe.cs): 11 patches on Resources / AssetBundle loads, a 14-frame stack trace per load for up to
    //    60 s after every scene load.
    // Each is now its own [Diagnostics] key, default OFF; off = the patch is never applied (each patch class's Prepare() returns false), so the
    // hooks cost nothing at all, including at PatchAll. Turn one on and restart to use it. Bound inside Awake's settings hold (one file write).
    internal static class FapDiagnosticKeys
    {
        internal static ConfigEntry<bool> DestroyWatch, LightLoadProbe, AssetLoadWindow;

        internal static void Bind(ConfigFile config)
        {
            DestroyWatch = config.Bind("Diagnostics", "Destroy Watch", false,
                "Debug lines naming what destroys a few watched prefabs (Norger oaks, monster placement ghosts). Hooks every object destroy, so OFF unless you are tracing that. Restart to apply.");
            LightLoadProbe = config.Bind("Diagnostics", "Light Load Probe", false,
                "Debug line per light object as it loads (its LightLod / LightFlicker values). Hooks every object load, so OFF unless you are tracing light presets. Restart to apply.");
            AssetLoadWindow = config.Bind("Diagnostics", "Asset Load Window", false,
                "Debug lines naming every asset load (with its caller) for up to 60 s after a scene load. Slows loading, so OFF unless you are tracing an 'UnloadUnusedAssets' error. Restart to apply.");
            var off = new System.Collections.Generic.List<string>();
            if (!DestroyWatch.Value) off.Add("Destroy Watch");
            if (!LightLoadProbe.Value) off.Add("Light Load Probe");
            if (!AssetLoadWindow.Value) off.Add("Asset Load Window");
            if (off.Count > 0)
                VaPieces.Log?.LogInfo($"[Startup] {off.Count} diagnostic probe(s) off, not patched: {string.Join(", ", off.ToArray())} ([Diagnostics] keys; restart after turning one on)");
        }

        // Prepare() answers: never throw at PatchAll (an unbound key = off)
        internal static bool On(ConfigEntry<bool> key) => key != null && key.Value;
    }
}
