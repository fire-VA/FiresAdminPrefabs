using System;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Intercepts hammer placement of a blueprint stub ("BP_<file>") and spawns the
    // blueprint's pieces at the ghost transform instead of vanilla's single stub.
    // Baked bundle pieces ("piece_<file>") are self-contained and never come here.
    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class VABlueprintPlacementPatch
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> _placementGhost =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        // PlacePiece is void, so skipping vanilla is just returning false.
        [HarmonyPriority(Priority.High)]
        [HarmonyPrefix]
        public static bool Prefix(Player __instance, Piece piece)
        {
            if (piece == null) return true;

            string prefabName = piece.gameObject != null ? piece.gameObject.name : null;
            if (string.IsNullOrEmpty(prefabName)) return true;

            // A fully lazy bundle needs its real mesh before vanilla's Instantiate clones it.
            VABakeBundle.EnsureLazyBundleBuilt(prefabName.GetStableHashCode());

            if (!VABlueprintHammerIntegration.IsBlueprintStub(prefabName)) return true;

            if (!VABlueprintHammerIntegration.TryGetBlueprint(prefabName, out var blueprint) || blueprint == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintPlacementPatch — stub '{prefabName}' has no loaded blueprint. Letting vanilla handle (will leave an empty stub in the world).");
                return true;
            }

            try
            {
                Transform ghost = GetPlacementGhostTransform(__instance);
                Vector3 worldPos = ghost != null ? ghost.position : __instance.transform.position;
                Quaternion worldRot = ghost != null ? ghost.rotation : __instance.transform.rotation;

                VABlueprintHammerIntegration.SpawnBlueprintAt(blueprint, worldPos, worldRot, __instance);

                if (piece.m_placeEffect != null)
                    piece.m_placeEffect.Create(worldPos, worldRot, __instance.transform);

                PlayHammerSwing(__instance);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{VaPieces.PluginName}: VABlueprintPlacementPatch threw for stub '{prefabName}': {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static Transform GetPlacementGhostTransform(Player player)
        {
            try
            {
                var ghost = _placementGhost(player);
                return ghost != null ? ghost.transform : null;
            }
            catch { return null; }
        }

        // Vanilla triggers the swing at the end of the PlacePiece body we skipped.
        private static void PlayHammerSwing(Player player)
        {
            try
            {
                var item = player.GetRightItem();
                if (item == null) return;
                player.FaceLookDirection();
                player.m_zanim.SetTrigger(item.m_shared.m_attack.m_attackAnimation);
            }
            catch { }
        }
    }

    // A blueprint stub is a hammer-menu placeholder and is never a valid world object,
    // but an earlier build could save one into the world. Vanilla only purges ZDOs with
    // an unresolvable prefab on the server, and the zone is streamed by the client, so
    // the orphan was retried — and logged as a missing prefab — every frame.
    //
    // Only positively identified stubs are handled. Any other missing prefab is left to
    // vanilla, so a client lacking some mod can never delete that mod's objects.
    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    public static class VABlueprintStubOrphanCleanup
    {
        private static readonly System.Collections.Generic.HashSet<ZDOID> _destroyedThisSession =
            new System.Collections.Generic.HashSet<ZDOID>();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(ZDO zdo, ref GameObject __result)
        {
            if (zdo == null) return true;
            if (!VABlueprintHammerIntegration.TryGetStubNameForHash(zdo.GetPrefab(), out string stubName)) return true;

            __result = null;

            // On the server a null result is enough: vanilla's invalid-prefab branch in
            // the caller claims and destroys it immediately afterwards.
            if (ZNet.instance == null || ZNet.instance.IsServer()) return false;

            // Deleting world state is an admin action. A non-admin still skips the
            // instantiate, which is what stops the log spam.
            if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return false;

            // The caller retries every frame until the destroy is processed.
            if (!_destroyedThisSession.Add(zdo.m_uid)) return false;

            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
            Debug.Log($"{VaPieces.PluginName}: removed orphaned blueprint stub '{stubName}' (ZDO {zdo.m_uid}) — "
                + "a hammer placeholder saved into the world by an earlier FAP build.");
            return false;
        }
    }

    // Blueprint stubs register without ghost visuals; they are built from the disk
    // cache the moment the player selects one, or a bake-cache preview is attached
    // as a fallback so the ghost is never invisible.
    [HarmonyPatch(typeof(Player), nameof(Player.SetupPlacementGhost))]
    public static class VABlueprintGhostVisualLazyAttachPatch
    {
        private const string GhostFallbackChildName = "__VABakeCacheGhostFallback";

        [HarmonyPriority(Priority.High)]
        [HarmonyPrefix]
        public static void Prefix(Player __instance)
        {
            try
            {
                var pieceTable = __instance != null ? __instance.m_buildPieces : null;
                var piece = pieceTable != null ? pieceTable.GetSelectedPiece() : null;
                if (piece == null) return;

                var stub = piece.gameObject;
                if (stub == null) return;
                string stubName = stub.name;
                if (string.IsNullOrEmpty(stubName)) return;
                if (!stubName.StartsWith(VABlueprintHammerIntegration.BlueprintPrefabPrefix, StringComparison.Ordinal))
                    return;

                VABlueprintHammerIntegration.EnsureGhostVisuals(stubName);
                TryAttachBakeCacheAsGhostFallback(stub, stubName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABlueprintGhostVisualLazyAttachPatch threw — {ex.Message}");
            }
        }

        // Visual-only preview built from the bake cache's combined meshes: no colliders,
        // and vanilla swaps in the ghost material afterwards. Idempotent via the marker child.
        private static void TryAttachBakeCacheAsGhostFallback(GameObject stub, string stubName)
        {
            try
            {
                if (stub.transform.Find(GhostFallbackChildName) != null) return;
                if (stub.GetComponentInChildren<MeshFilter>(includeInactive: true) != null) return;

                if (!VABlueprintHammerIntegration.TryGetBlueprint(stubName, out var blueprint) || blueprint == null) return;
                string cacheKey = VABlueprintHammerIntegration.GetCacheKey(blueprint);
                if (!VAFapBlueprintBakeCache.TryGet(cacheKey, out var cached) || cached == null || cached.Parts == null || cached.Parts.Count == 0) return;

                var root = new GameObject(GhostFallbackChildName);
                ParentAtOrigin(root.transform, stub.transform);

                for (int i = 0; i < cached.Parts.Count; i++)
                {
                    var part = cached.Parts[i];
                    if (part == null || part.CombinedMesh == null || part.Materials == null || part.Materials.Length == 0) continue;

                    var child = new GameObject("GhostPart_" + i);
                    ParentAtOrigin(child.transform, root.transform);
                    child.AddComponent<MeshFilter>().sharedMesh = part.CombinedMesh;
                    var meshRenderer = child.AddComponent<MeshRenderer>();
                    meshRenderer.sharedMaterials = part.Materials;
                    meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    meshRenderer.receiveShadows = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: TryAttachBakeCacheAsGhostFallback threw for '{stubName}': {ex.Message}");
            }
        }

        private static void ParentAtOrigin(Transform child, Transform parent)
        {
            child.SetParent(parent, worldPositionStays: false);
            child.localPosition = Vector3.zero;
            child.localRotation = Quaternion.identity;
            child.localScale = Vector3.one;
        }
    }

    // Bundle templates carry only the combined mesh. After vanilla instantiates one,
    // this postfix spawns the bundle's cached live children (doors, chests, signs…)
    // as their own networked objects, a few hundred per frame so a megabase doesn't
    // freeze the placement frame.
    //
    // PlacePiece is void, so the placed root is found by prefab hash + position.
    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class VABundlePlacementSpawn
    {
        private const float PlacedMatchRadiusMeters = 0.5f;

        [HarmonyPostfix]
        public static void Postfix(Piece piece, Vector3 pos, Quaternion rot)
        {
            if (piece == null) return;
            try
            {
                string prefabName = piece.gameObject.name;
                if (string.IsNullOrEmpty(prefabName)) return;

                string cacheKey = VABakeBundle.TryGetCacheKeyForPrefab(prefabName);
                if (string.IsNullOrEmpty(cacheKey)) return;

                Sync.VAFapAssetSync.Client_PushPlacedBakeIfServerLacksIt(prefabName);

                var placed = FindMostRecentlyPlaced(prefabName, pos);
                if (placed == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABundlePlacementSpawn — couldn't locate placed bundle '{prefabName}' at {pos}; live children won't spawn for this placement.");
                    return;
                }

                if (!VAFapBlueprintBakeCache.TryGet(cacheKey, out var cached) || cached == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VABundlePlacementSpawn — cache miss for '{cacheKey}'; bundle '{prefabName}' will place without live children. Re-bake to recover.");
                    return;
                }

                if (cached.LiveChildren == null || cached.LiveChildren.Count == 0)
                    return;

                var spawn = VABakeBundle.SpawnLiveChildrenForPlacedBundleCoroutine(placed, cached, prefabName);
                if (VaPieces.Instance != null)
                    VaPieces.Instance.StartCoroutine(spawn);
                else
                    while (spawn.MoveNext()) { }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABundlePlacementSpawn.Postfix threw — {ex.Message}");
            }
        }

        // Vanilla may snap-adjust placement slightly, hence the tolerance radius.
        private static GameObject FindMostRecentlyPlaced(string prefabName, Vector3 pos)
        {
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_instances == null) return null;

            int prefabHash = prefabName.GetStableHashCode();
            float bestDistanceSq = PlacedMatchRadiusMeters * PlacedMatchRadiusMeters;
            GameObject best = null;
            foreach (var instance in scene.m_instances)
            {
                var netView = instance.Value;
                if (netView == null) continue;
                var zdo = netView.GetZDO();
                if (zdo == null) continue;
                if (zdo.GetPrefab() != prefabHash) continue;

                float distanceSq = (netView.transform.position - pos).sqrMagnitude;
                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    best = netView.gameObject;
                }
            }
            return best;
        }
    }

    // WearNTear.Awake refills m_renderers from every descendant renderer on the placed
    // clone, undoing the template-time prune. Bundle roots are invulnerable and never
    // need wear visuals, so the list is emptied again to keep UpdateWear a no-op.
    [HarmonyPatch(typeof(WearNTear), "Awake")]
    public static class VABundleRootWearNTearAwakePrune
    {
        private static System.Reflection.FieldInfo _renderersField;
        private static bool _resolved;

        [HarmonyPostfix]
        public static void Postfix(WearNTear __instance)
        {
            try
            {
                if (__instance == null) return;
                var root = __instance.gameObject;
                if (root == null) return;

                // Instantiate copies VABakeParentLoader onto an Infinity Hammer clone too,
                // so the component alone does not prove FAP ownership during an IH op.
                if (VAInfinityHammerCompat.IsIhCopyPlacing) return;   // 1.1.206: only an IH copy, not an IH-held hammer
                if (root.GetComponent<VABakeParentLoader>() == null) return;

                if (!_resolved)
                {
                    _renderersField = AccessTools.Field(typeof(WearNTear), "m_renderers");
                    _resolved = true;
                }
                if (_renderersField == null) return;

                var emptyList = Activator.CreateInstance(_renderersField.FieldType);
                _renderersField.SetValue(__instance, emptyList);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABundleRootWearNTearAwakePrune.Postfix threw — {ex.Message}");
            }
        }
    }

    // Soft-dependency bridge into Expand World Data for per-piece blueprint state.
    //
    // Every direct Data.* reference lives in a *Impl method that is only reached
    // after IsEwdLoaded returned true. The JIT compiles a method body on first
    // call, so EWD's types are never resolved when EWD is absent. Inlining those
    // calls into the public methods would reintroduce the TypeLoadException.
    internal static class VABundleDataApplier
    {
        private const string ExpandWorldDataGuid = "expand_world_data";

        private static readonly int SupportFieldHash = "support".GetStableHashCode();
        private static readonly int WearNTearSupportFieldHash = "WearNTear.support".GetStableHashCode();

        private static bool? _ewdLoadedCache;

        public static bool IsEwdLoaded
        {
            get
            {
                if (_ewdLoadedCache.HasValue) return _ewdLoadedCache.Value;
                bool loaded = false;
                try { loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ExpandWorldDataGuid); }
                catch { }
                _ewdLoadedCache = loaded;
                if (loaded)
                    Debug.Log($"{VaPieces.PluginName}: VABundleDataApplier — Expand World Data detected. Bundle live-child per-piece state will be restored at placement (sign text, chest inventory, door state, etc.).");
                else
                    Debug.Log($"{VaPieces.PluginName}: VABundleDataApplier — Expand World Data not loaded. Bundle live-child per-piece state will NOT be restored — pieces place with vanilla defaults. Install JereKuusela.Expand_World_Data to enable.");
                return loaded;
            }
        }

        public static bool TryApply(string base64Data, string prefabName, ZDO zdo)
        {
            if (string.IsNullOrEmpty(base64Data)) return false;
            if (zdo == null) return false;
            if (!IsEwdLoaded) return false;
            return TryApplyImpl(base64Data, prefabName, zdo);
        }

        // Only the two support fields are stripped, so the placed piece computes its
        // support tint from real world conditions instead of the blueprint's stale
        // value. Health is kept: with the field edits it sets the piece's look.
        private static bool TryApplyImpl(string base64Data, string prefabName, ZDO zdo)
        {
            try
            {
                var entry = new Data.DataEntry(base64Data);
                if (entry.Floats != null)
                {
                    entry.Floats.Remove(SupportFieldHash);
                    entry.Floats.Remove(WearNTearSupportFieldHash);
                }

                var parameters = new Data.ObjectParameters(prefabName ?? string.Empty, string.Empty, zdo);
                entry.Write(parameters, zdo);
                VAPre10ZdoUpgrade.Apply(zdo);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABundleDataApplier.TryApplyImpl threw on '{prefabName}': {ex.Message}");
                return false;
            }
        }
    }
}
