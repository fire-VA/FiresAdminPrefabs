using UnityEngine;

using FiresCore.Net;

namespace VerdantsAscent.Utilities
{
    // Persistent respawn anchor for monster-preset placements. EWP's destroy
    // rule uses an in-memory `delay:` timer that dies on server restart — if
    // the creature died during the delay window, it never comes back. The
    // anchor is a hidden Pickable_Flint dropped under the creature's feet
    // whose persistent ZDO carries enough state for the recovery rule to
    // resume the respawn on the next player pass.
    //
    // ZDO contract (set at placement):
    //   va_anchor_<presetName> = 1   — per-preset filter for EWP rules
    //   va_anchor              = 1   — generic FAP-anchor marker
    //   va_creature            = <prefab>
    //   picked                 = true
    //
    // ZDO contract (set later):
    //   va_dead_at = <time>   — written by the destroy-rule poke,
    //                           cleared by the recovery-rule spawn.
    public static class VAMonsterAnchor
    {
        public const string AnchorPrefabName = "Pickable_Flint";
        public const string AnchorKeyPrefix = "va_anchor_";
        public const string DeadAtKey = "va_dead_at";
        public const float BuryDepth = 0.5f;
        private const float SuppressedRespawnMinutes = 999999f;

        public static void SpawnAnchorUnder(Vector3 monsterPos, string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;

            var scene = ZNetScene.instance;
            if (scene == null) return;

            var prefab = scene.GetPrefab(AnchorPrefabName);
            if (prefab == null)
            {
                Debug.LogWarning(
                    $"{VaPieces.PluginName}: VAMonsterAnchor — '{AnchorPrefabName}' not in ZNetScene; " +
                    $"cannot place restart-survival anchor for preset '{presetName}'.");
                return;
            }

            var anchorPos = monsterPos + Vector3.down * BuryDepth;
            var anchor = UnityEngine.Object.Instantiate(prefab, anchorPos, Quaternion.identity);
            if (anchor == null) return;

            var nview = anchor.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                NetworkObjectHelper.SafeDestroy(anchor);
                return;
            }

            var zdo = nview.GetZDO();
            if (zdo == null)
            {
                NetworkObjectHelper.SafeDestroy(anchor);
                return;
            }

            zdo.Set(AnchorKeyPrefix + presetName, 1);
            zdo.Set("va_anchor", 1);
            zdo.Set("picked", true);

            var pickable = anchor.GetComponent<Pickable>();
            if (pickable != null)
            {
                pickable.m_picked = true;
                pickable.m_respawnTimeMinutes = SuppressedRespawnMinutes;
            }

            var renderers = anchor.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = false;
            }
            var colliders = anchor.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) colliders[i].enabled = false;
            }

            if (VerdantsAscent.Managers.ConfigManager.Instance != null
                && VerdantsAscent.Managers.ConfigManager.Instance.configVerboseLogging.Value)
            {
                Debug.Log(
                    $"{VaPieces.PluginName}: VAMonsterAnchor placed '{AnchorPrefabName}' at {anchorPos} " +
                    $"for preset '{presetName}'.");
            }
        }
    }
}
