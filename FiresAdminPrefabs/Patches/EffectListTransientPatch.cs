using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // FAP marks fx_/vfx_/sfx_ prefabs persistent so a PLACED one survives reload,
    // but vanilla spawns the same prefabs as transient status effects through
    // EffectList.Create. Those instances are flipped back so they unload with
    // their zone instead of orphaning as saved ZDOs. Hammer placement never
    // passes through EffectList.Create.
    [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
    internal static class EffectListTransientPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameObject[] __result)
        {
            if (__result == null) return;
            for (int i = 0; i < __result.Length; i++)
            {
                GameObject effect = __result[i];
                if (effect == null) continue;
                var netView = effect.GetComponent<ZNetView>();
                if (netView == null || !netView.m_persistent) continue;

                netView.m_persistent = false;
                ZDO zdo = netView.GetZDO();
                if (zdo != null && zdo.IsOwner())
                    zdo.Persistent = false;
            }
        }
    }
}
