using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Every persistent clone passes through ZNetView.Awake exactly once, whatever
    // spawned it, so the user's shader override is applied to that instance here.
    // The prefab template is never mutated.
    // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
    public static class VAFapPieceShaderOverridePatch
    {
        public static void Run(ZNetView __instance)
        {
            if (__instance == null || !VAFapPieceOverrides.AnyOverrides) return;   // 1.1.402: nothing configured = no name string per create
            try { VAFapPieceShaderOverrider.ApplyToInstance(__instance.gameObject); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapPieceShaderOverridePatch — Apply threw on '{__instance.gameObject?.name}': {ex.Message}");
            }
        }
    }
}
