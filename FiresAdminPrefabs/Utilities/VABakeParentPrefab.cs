using System;
using System.Collections;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Registers VA_BakeParent, the root older FAP builds gave a runtime-baked blueprint placement.
    // New placements use per-blueprint bundle prefabs, but worlds saved with bake parents still
    // hold their ZDOs, and without the template those placements would load as missing prefabs.
    //
    // The template carries:
    //   ZNetView (persistent)   ZDO holds the cache key and group id across a save
    //   Piece                   hammer-targetable; removing it fires the group cascade.
    //                           Category Max keeps it off the build menu.
    //   WearNTear               invulnerable, no support or roof wear
    //   VABakeBatchMarker       group id for HammerRemovalPatch's direct lookup
    //   VABakeParentLoader      attaches the cached mesh on load, or rebakes it on a miss
    //   VABakeColliderToggle    disables the colliders while the player is far away
    public static class VABakeParentPrefab
    {
        internal const string ZdoKeyCacheName = "va_bp_cache_name";
        internal const string ZdoKeyGroupId = VABlueprintGroupRegistry.ZdoKey;

        private const string PrefabName = "VA_BakeParent";
        private const string PieceDisplayName = "Baked Blueprint";
        private const string PieceDescription = "<color=#ffaa00>Combined-mesh blueprint structure</color>";
        private static readonly Vector3 TemplateParkPosition = new Vector3(100000f, 100000f, 100000f);

        private static GameObject _template;

        // Adds the template to ZNetScene on every scene start, creating it the first time. It is
        // registered with VABakeTemplateRegistry before its ZNetView is added, so its own Awake is
        // bypassed, and it stays active so clones run Awake and claim their saved ZDOs.
        public static void EnsureRegistered(ZNetScene scene)
        {
            if (scene == null) return;
            if (_template == null && !TryCreateTemplate()) return;

            int prefabHash = PrefabName.GetStableHashCode();
            if (!scene.m_prefabs.Contains(_template)) scene.m_prefabs.Add(_template);
            if (!scene.m_namedPrefabs.ContainsKey(prefabHash)) scene.m_namedPrefabs[prefabHash] = _template;
        }

        private static bool TryCreateTemplate()
        {
            try
            {
                var template = new GameObject(PrefabName);
                VABakeTemplateRegistry.RegisterTemplate(template);
                template.transform.position = TemplateParkPosition;
                UnityEngine.Object.DontDestroyOnLoad(template);

                var netView = template.AddComponent<ZNetView>();
                netView.m_persistent = true;
                netView.m_type = ZDO.ObjectType.Default;
                netView.m_distant = true;

                var piece = template.AddComponent<Piece>();
                piece.m_name = PieceDisplayName;
                piece.m_description = PieceDescription;
                piece.m_enabled = true;
                piece.m_clipGround = true;
                piece.m_clipEverything = true;
                piece.m_allowRotatedOverlap = true;
                piece.m_canBeRemoved = true;
                piece.m_resources = Array.Empty<Piece.Requirement>();
                piece.m_category = VerdantsAscent.Utilities.VAPieceCategoryRuntime.Hidden;

                var wearNTear = template.AddComponent<WearNTear>();
                wearNTear.m_health = -1f;
                wearNTear.m_noSupportWear = false;
                wearNTear.m_noRoofWear = false;
                wearNTear.m_supports = false;

                template.AddComponent<VABakeBatchMarker>();
                template.AddComponent<VABakeParentLoader>();
                template.AddComponent<VABakeColliderToggle>();

                VAPieceManager.DistantRenderPrefabs.Add(PrefabName);
                _template = template;
                Debug.Log($"{VaPieces.PluginName}: [t={Time.realtimeSinceStartup:F2}s] VABakeParentPrefab registered template '{PrefabName}' (hash={PrefabName.GetStableHashCode()}) + distant-render entry.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeParentPrefab.EnsureRegistered threw: {ex.Message}");
                return false;
            }
        }
    }

    // Runs when a VA_BakeParent or a baked bundle root spawns. Older builds let vanilla write
    // s_support onto these invulnerable roots, which tinted them under the hammer, so an owned ZDO
    // has it cleared. A bake parent then mirrors its group id onto VABakeBatchMarker and attaches
    // the cached mesh; on a cache miss, which is normal after a restart, it rebakes the blueprint
    // from its prefabs and attaches the result. Until then it renders nothing but is otherwise a
    // normal piece.
    public class VABakeParentLoader : MonoBehaviour
    {
        private void Awake()
        {
            var netView = GetComponent<ZNetView>();
            if (netView == null || !netView.IsValid()) return;

            try
            {
                var zdo = netView.GetZDO();
                if (zdo.IsOwner()) zdo.RemoveFloat(ZDOVars.s_support);

                string groupId = zdo.GetString(VABakeParentPrefab.ZdoKeyGroupId, string.Empty);
                var marker = GetComponent<VABakeBatchMarker>();
                if (marker != null && !string.IsNullOrEmpty(groupId)) marker.GroupId = groupId;

                string cacheKey = zdo.GetString(VABakeParentPrefab.ZdoKeyCacheName, string.Empty);
                if (string.IsNullOrEmpty(cacheKey)) return;

                if (!string.IsNullOrEmpty(groupId)) VABlueprintGroupRegistry.AttachBakeParent(groupId, gameObject);

                if (VAFapBlueprintBakeCache.TryGet(cacheKey, out var cached)) AttachCachedParts(cached);
                else QueueRebake(cacheKey);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeParentLoader.Awake threw: {ex.Message}");
            }
        }

        // Builds the same zone and LOD hierarchy a bundle prefab gets, then the live children.
        private void AttachCachedParts(VAFapBlueprintBakeCache.CachedBake cached)
        {
            if (cached?.Parts == null) return;
            var marker = GetComponent<VABakeBatchMarker>();
            string groupId = marker != null ? marker.GroupId : null;
            string blueprintName = cached.BlueprintName ?? gameObject.name;
            VABakeBundle.BuildZoneBucketsForTemplate(gameObject, cached.Parts, blueprintName, groupId);
            VABakeBundle.BuildLiveChildrenForTemplate(gameObject, cached.LiveChildren, blueprintName);
        }

        private void QueueRebake(string cacheKey)
        {
            if (!VABlueprintHammerIntegration.TryGetBlueprintBySourceFileName(cacheKey, out var blueprint) || blueprint == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeParentLoader — cache key '{cacheKey}' not registered as a blueprint; bake parent will be invisible until removed.");
                return;
            }
            if (VaPieces.Instance != null) VaPieces.Instance.StartCoroutine(RebakeAndAttach(cacheKey, blueprint));
        }

        private IEnumerator RebakeAndAttach(string cacheKey, VABlueprintLoader.Blueprint blueprint)
        {
            var scene = ZNetScene.instance;
            if (scene == null) yield break;

            yield return VaPieces.Instance.StartCoroutine(
                VAFapBlueprintMeshBaker.BakeFromBlueprintPrefabsCoroutine(blueprint, scene, cacheKey));

            if (this != null && VAFapBlueprintBakeCache.TryGet(cacheKey, out var cached))
                AttachCachedParts(cached);
        }
    }
}
