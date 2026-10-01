using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Every persistent clone passes through ZNetView.Awake exactly once, whatever
    // spawned it, so the user's shader override is applied to that instance here.
    // The prefab template is never mutated.
    [HarmonyPatch(typeof(ZNetView), "Awake")]
    public static class VAFapPieceShaderOverridePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ZNetView __instance)
        {
            if (__instance == null) return;
            try { VAFapPieceShaderOverrider.ApplyToInstance(__instance.gameObject); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapPieceShaderOverridePatch — Apply threw on '{__instance.gameObject?.name}': {ex.Message}");
            }
        }
    }
}
