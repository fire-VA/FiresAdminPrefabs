using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Radius-scoped pruning of FAP-painted clutter when a terrain operation
    // clears a chunk of ground. Every hoe/cultivator/TerrainOp/WEC/IH paint
    // funnels through ClutterSystem.ResetGrass(center, radius), so hooking
    // it once covers every caller without per-tool patches.
    //
    // The fallback path inside TerrainComp.Save passes the whole heightmap
    // half-width (~32m on a default 64-tile comp) when m_lastOpPoint is
    // unset. We reject anything that large to avoid wiping the entire
    // clutter patch on every save — the real radius-scoped call from the
    // same op handles the actual cleared area.
    [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.ResetGrass))]
    public static class ClutterSystem_ResetGrass_VAPaintCleanup
    {
        private const float WholeHeightmapRadiusThreshold = 20f;

        private static readonly FieldInfo _instancesField =
            AccessTools.Field(typeof(TerrainComp), "s_instances");

        [HarmonyPostfix]
        public static void Postfix(Vector3 center, float radius)
        {
            if (radius >= WholeHeightmapRadiusThreshold) return;

            var instances = _instancesField?.GetValue(null) as List<TerrainComp>;
            if (instances == null || instances.Count == 0) return;

            for (int i = 0; i < instances.Count; i++)
            {
                var tc = instances[i];
                if (tc == null) continue;

                Vector3 compPos = tc.transform.position;
                float halfSize = tc.m_size * 0.5f;
                float dx = Mathf.Abs(center.x - compPos.x);
                float dz = Mathf.Abs(center.z - compPos.z);
                if (dx > halfSize + radius || dz > halfSize + radius) continue;

                int removed = VAClutterPaintTool.RemoveEntriesInRadius(tc, center, radius);
                if (removed > 0)
                    VAClutterPaintTool.RestoreFromZDO(tc);
            }
        }
    }
}
