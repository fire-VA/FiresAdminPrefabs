using System;
using System.Collections.Generic;
using UnityEngine;

using FiresCore.Net;

namespace VerdantsAscent.Utilities
{
    // Tracks blueprint-group membership so destroying one piece of a placement wipes
    // the whole thing.
    //
    // Every spawned piece carries `va_bp_group = <id>` on its ZDO, and the
    // ZNetView.Awake patch re-registers it on each spawn and zone reload — so the
    // registry rebuilds itself across sessions with no persistence of its own.
    // Vegetation counts as a member even though it never joins the combined mesh:
    // chopping a tree in a blueprint takes the blueprint.
    //
    // Main-thread only (ZNetView / ZNetScene callbacks), so no locking.
    public static class VABlueprintGroupRegistry
    {
        public const string ZdoKey = "va_bp_group";

        private const int InitialGroupCapacity = 8;
        private const int GroupIdHexLength = 12;

        private static readonly Dictionary<string, List<ZNetView>> _byGroup =
            new Dictionary<string, List<ZNetView>>(StringComparer.Ordinal);

        // Set by the mesh baker; holds the combined-mesh visual so it dies with the pieces.
        private static readonly Dictionary<string, GameObject> _bakeParents =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        // Destroying a sibling re-enters the ZNetScene.Destroy prefix for the same
        // group, so the cascade has to short-circuit on itself.
        private static readonly HashSet<string> _destroyingGroups =
            new HashSet<string>(StringComparer.Ordinal);

        // Short enough not to bloat every ZDO, long enough to not collide across
        // thousands of placements.
        public static string NewGroupId()
        {
            return "bp_" + Guid.NewGuid().ToString("N").Substring(0, GroupIdHexLength);
        }

        public static void Register(string groupId, ZNetView netView)
        {
            if (string.IsNullOrEmpty(groupId) || netView == null) return;
            if (!_byGroup.TryGetValue(groupId, out var members))
            {
                members = new List<ZNetView>(InitialGroupCapacity);
                _byGroup[groupId] = members;
            }
            for (int i = 0; i < members.Count; i++)
                if (ReferenceEquals(members[i], netView)) return;
            members.Add(netView);
        }

        public static void AttachBakeParent(string groupId, GameObject bakeParent)
        {
            if (string.IsNullOrEmpty(groupId) || bakeParent == null) return;
            _bakeParents[groupId] = bakeParent;
        }

        // Destroys every member of the group except `triggerNetView`, which the caller
        // is already destroying. Returns the count destroyed, for the cascade log.
        public static int HandleMemberDestroyed(string groupId, ZNetView triggerNetView)
        {
            if (string.IsNullOrEmpty(groupId)) return 0;
            if (_destroyingGroups.Contains(groupId)) return 0;
            _destroyingGroups.Add(groupId);

            int destroyed = 0;
            try
            {
                var scene = ZNetScene.instance;
                if (scene == null) return 0;

                var liveMembers = CollectLiveMembers(groupId, scene);
                var orphanZdoIds = CollectUnloadedMemberZdos(groupId, liveMembers);

                // Cleared first so a cascade that somehow re-enters past the guard
                // finds an empty group and terminates.
                _byGroup.Remove(groupId);

                // The bake parent is registered in BOTH _byGroup and _bakeParents, and
                // destroying a ZNetView twice orphans its ZDO — the second pass sees a
                // torn-down component and skips ZDOMan.DestroyZDO, leaving a ZDO that
                // loops through CreateObject forever. So it is skipped here and
                // destroyed once, below.
                _bakeParents.TryGetValue(groupId, out GameObject bakeParent);

                destroyed += DestroyLiveMembers(liveMembers, triggerNetView, bakeParent);
                destroyed += DestroyUnloadedMembers(orphanZdoIds);
                DestroyBakeParent(groupId, bakeParent, triggerNetView);
            }
            finally
            {
                _destroyingGroups.Remove(groupId);
            }

            return destroyed;
        }

        // Unions the registry with a live scan of ZNetScene.m_instances. The scan is
        // required: pieces whose Awake hadn't validated yet, or that loaded from a
        // previous session's ZDOs, never reached Register — on a Phalanx-scale
        // placement that left thousands of children orphaned.
        private static ZNetView[] CollectLiveMembers(string groupId, ZNetScene scene)
        {
            var members = new HashSet<ZNetView>();

            if (_byGroup.TryGetValue(groupId, out var registered))
            {
                for (int i = 0; i < registered.Count; i++)
                    if (registered[i] != null) members.Add(registered[i]);
            }

            if (scene.m_instances != null)
            {
                foreach (var instance in scene.m_instances)
                {
                    var netView = instance.Value;
                    if (netView == null || members.Contains(netView)) continue;
                    var zdo = netView.GetZDO();
                    if (zdo == null) continue;
                    if (!string.Equals(zdo.GetString(ZdoKey, string.Empty), groupId, StringComparison.Ordinal)) continue;
                    members.Add(netView);
                }
            }

            var snapshot = new ZNetView[members.Count];
            members.CopyTo(snapshot);
            return snapshot;
        }

        // A megabase spans more zones than are streamed in, so most of its pieces have
        // no GameObject when the root is hammered — only a ZDO. Without this pass they
        // survive the cascade and re-instantiate the next time the player walks back.
        private static List<ZDOID> CollectUnloadedMemberZdos(string groupId, ZNetView[] liveMembers)
        {
            var orphanIds = new List<ZDOID>();
            try
            {
                var allZdos = VAZdoManAccess.SnapshotAllZdos();
                if (allZdos == null) return orphanIds;

                var liveZdoIds = new HashSet<ZDOID>();
                for (int i = 0; i < liveMembers.Length; i++)
                {
                    var netView = liveMembers[i];
                    if (netView == null || !netView.IsValid()) continue;
                    var zdo = netView.GetZDO();
                    if (zdo != null) liveZdoIds.Add(zdo.m_uid);
                }

                for (int i = 0; i < allZdos.Count; i++)
                {
                    var zdo = allZdos[i];
                    if (zdo == null || liveZdoIds.Contains(zdo.m_uid)) continue;
                    if (!string.Equals(zdo.GetString(ZdoKey, string.Empty), groupId, StringComparison.Ordinal)) continue;
                    orphanIds.Add(zdo.m_uid);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegistry — ZDOMan master-dict walk threw: {ex.Message}");
            }
            return orphanIds;
        }

        // SafeDestroy cancels pending InvokeRepeating and disables per-frame updaters
        // before the destroy, which turns a megabase cascade from a wall of Smelter /
        // Fireplace teardown NREs into a clean mass destroy.
        private static int DestroyLiveMembers(ZNetView[] members, ZNetView triggerNetView, GameObject bakeParent)
        {
            int destroyed = 0;
            for (int i = 0; i < members.Length; i++)
            {
                var netView = members[i];
                if (netView == null) continue;
                if (ReferenceEquals(netView, triggerNetView)) continue;
                if (!netView.IsValid()) continue;

                var go = netView.gameObject;
                if (go == null) continue;
                if (bakeParent != null && ReferenceEquals(go, bakeParent)) continue;

                try
                {
                    // Before the destroy, so Infinity Hammer's undo restores the whole
                    // group rather than just the trigger piece.
                    VAInfinityHammerCompat.RecordCascadeRemoval(netView.GetZDO());
                    NetworkObjectHelper.SafeDestroy(go);
                    destroyed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegistry — SafeDestroy on sibling '{go.name}' threw: {ex.Message}");
                }
            }
            return destroyed;
        }

        // DestroyZDO early-returns for a non-owner, and these were usually owned by
        // whichever peer placed them, so each is claimed immediately before destroying.
        private static int DestroyUnloadedMembers(List<ZDOID> orphanZdoIds)
        {
            if (orphanZdoIds.Count == 0) return 0;

            int destroyed = 0;
            try
            {
                var zdoMan = ZDOMan.instance;
                if (zdoMan == null) return 0;

                long sessionId = ZDOMan.GetSessionID();
                for (int i = 0; i < orphanZdoIds.Count; i++)
                {
                    var zdo = zdoMan.GetZDO(orphanZdoIds[i]);
                    if (zdo == null) continue;
                    try
                    {
                        VAInfinityHammerCompat.RecordCascadeRemoval(zdo);
                        if (!zdo.IsOwner()) zdo.SetOwner(sessionId);
                        zdoMan.DestroyZDO(zdo);
                        destroyed++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegistry — orphan ZDO destroy on {orphanZdoIds[i]} threw: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegistry — orphan-ZDO cleanup loop threw: {ex.Message}");
            }
            return destroyed;
        }

        // When the bake parent IS the trigger, vanilla's ZNetScene.Destroy is already
        // in flight on it — we're inside that prefix — so destroying again would
        // double-fire the pipeline on the same object.
        private static void DestroyBakeParent(string groupId, GameObject bakeParent, ZNetView triggerNetView)
        {
            if (bakeParent == null) return;
            _bakeParents.Remove(groupId);

            if (triggerNetView != null && ReferenceEquals(triggerNetView.gameObject, bakeParent)) return;

            try
            {
                var netView = bakeParent.GetComponent<ZNetView>();
                if (netView != null && netView.IsValid())
                    VAInfinityHammerCompat.RecordCascadeRemoval(netView.GetZDO());
                NetworkObjectHelper.SafeDestroy(bakeParent);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{VaPieces.PluginName}] VABlueprintGroupRegistry — bake-parent SafeDestroy threw: {ex.Message}");
            }
        }

        // Called on world unload so session refs don't leak between worlds;
        // re-registration happens as ZNetViews Awake in the new session.
        public static void ClearAll()
        {
            _byGroup.Clear();
            _bakeParents.Clear();
            _destroyingGroups.Clear();
        }
    }
}
