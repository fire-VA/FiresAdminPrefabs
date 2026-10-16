using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // 1.1.262 (the lead: point-of-use proof for the light presets): after vanilla's own ZNetView.LoadFields - the path a client WITHOUT
    // FAP uses too - one Debug line per light object carrying LightLod / LightFlicker fields: what the components actually resolved to
    // (LOD bools + distances, flicker settings) and the Light's intensity / range at that moment. Read-only.
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.LoadFields))]
    internal static class VALightLoadProbe
    {
        private static readonly HashSet<ZDOID> s_logged = new HashSet<ZDOID>();

        [HarmonyPostfix]
        private static void Postfix(ZNetView __instance)
        {
            try
            {
                var zdo = __instance != null && __instance.IsValid() ? __instance.GetZDO() : null;
                if (zdo == null || !(zdo.GetBool("HasFieldsLightLod") || zdo.GetBool("HasFieldsLightFlicker"))) return;
                if (!s_logged.Add(zdo.m_uid)) return;
                var lod = __instance.GetComponentInChildren<LightLod>(true);
                var fl = __instance.GetComponentInChildren<LightFlicker>(true);
                var light = __instance.GetComponentInChildren<Light>(true);
                VaPieces.Log?.LogDebug($"[LightPreset] vanilla-path load '{Utils.GetPrefabName(__instance.gameObject)}' at {zdo.GetPosition():F1}: "
                    + (lod != null ? $"LightLod lightLod {lod.m_lightLod} {lod.m_lightDistance:0} m, shadowLod {lod.m_shadowLod} {lod.m_shadowDistance:0} m; " : "no LightLod; ")
                    + (fl != null ? $"LightFlicker strength {fl.m_flickerIntensity:0.###} speed {fl.m_flickerSpeed:0.##} movement {fl.m_movement:0.###} fade {fl.m_fadeDuration:G3} fadeIn {fl.m_fadeInDuration:0.##} ttl {fl.m_ttl:0.##}; " : "no LightFlicker; ")
                    + (light != null ? $"Light intensity {light.intensity:0.##} range {light.range:0.#} shadows {light.shadows}" : "no Light"));
            }
            catch { }
        }
    }
}
