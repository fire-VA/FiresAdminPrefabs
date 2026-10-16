using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // 1.1.261 (Fire 10-15: "with the use vanilla friendly toggle we should not display scaled pieces as they were made since vanilla
    // clients wont see that"). FAP's two load-time scale readbacks (VAScaleReadback, VAAreaScaleAwakePatch) apply a stored ZDO scale
    // to prefabs vanilla never rescales (m_syncInitialScale off). With [General] VanillaFriendly on they step aside, so a FAP client
    // renders those pieces at prefab scale, as a vanilla client does. Display only: nothing is written to the ZDO. VanillaFriendly
    // off = today's behaviour. One Debug line per prefab: "VF: ignored stored scale x.xx on <prefab>".
    internal static class VAVanillaFriendlyScale
    {
        private static readonly HashSet<string> s_logged = new HashSet<string>();

        internal static bool Skip(ZNetView view, ZDO zdo)
        {
            if (ConfigManager.Instance?.configVanillaFriendly?.Value != true) return false;
            Vector3 v = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            float s = v != Vector3.zero ? v.x : zdo.GetFloat(ZDOVars.s_scaleScalarHash, 1f);
            if (Mathf.Approximately(s, 1f) && (v == Vector3.zero || v == Vector3.one)) return true;   // nothing to ignore
            string prefab = Utils.GetPrefabName(view.gameObject);
            if (s_logged.Add(prefab))
                VaPieces.Log?.LogDebug($"VF: ignored stored scale {(v != Vector3.zero ? v.ToString("F2") : s.ToString("0.00"))} on {prefab} (vanilla clients see it at prefab scale)");
            return true;
        }
    }
}
