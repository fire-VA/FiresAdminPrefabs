using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // ClutterSystem.instance is null at plugin load; its Awake is the first point
    // where m_clutter holds vanilla's and every other mod's entries, so the active
    // presets are overlaid here. The yaml hot-reload path calls the same apply.
    [HarmonyPatch(typeof(ClutterSystem), "Awake")]
    public static class ClutterSystem_Awake_VAAutoApply_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ClutterSystem __instance)
        {
            if (__instance == null) return;
            try
            {
                int applied = VAClutterConfigApplier.ApplyAllActive();
                if (applied > 0)
                    Debug.Log($"{VaPieces.PluginName}: VAClutterAutoApplyPatch — applied {applied} active clutter preset(s) at world load.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterAutoApplyPatch — ApplyAllActive threw: {ex.Message}");
            }
        }
    }
}
