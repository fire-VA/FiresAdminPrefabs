using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // ZDOs whose prefab hash no longer resolves in ZNetScene. They can never instantiate, so
    // vanilla retries them every time their zone streams in and logs "Missing prefab hash" each
    // pass. ZNetScene only claims and destroys them when it is the server, so on a client they
    // spam for the life of the world.
    //
    // A prefab is missing because its mod is gone, or because the content is local to another
    // machine — a blueprint stub or a baked bundle that only the admin's client has. FAP names
    // its own content from the blueprint files on disk; everything else stays unnamed and is
    // never purged unless the admin names its hash explicitly.
    public static class VAFapOrphanZdoScan
    {
        public sealed class OrphanGroup
        {
            public int PrefabHash;
            public string PrefabName;
            public int ZdoCount;
            public Vector3 SamplePosition;

            public bool IsNamed => !string.IsNullOrEmpty(PrefabName);
        }

        private static Dictionary<int, string> _fapNamesByHash;

        // Null when the ZDO table could not be read; empty when the world is clean.
        public static List<OrphanGroup> Scan()
        {
            var allZdos = VAZdoManAccess.SnapshotAllZdos();
            if (allZdos == null) return null;

            var scene = ZNetScene.instance;
            if (scene == null) return null;

            var byPrefabHash = new Dictionary<int, OrphanGroup>();
            for (int i = 0; i < allZdos.Count; i++)
            {
                var zdo = allZdos[i];
                if (zdo == null) continue;

                int prefabHash = zdo.GetPrefab();
                if (prefabHash == 0) continue;
                if (scene.GetPrefab(prefabHash) != null) continue;

                if (!byPrefabHash.TryGetValue(prefabHash, out var group))
                {
                    group = new OrphanGroup
                    {
                        PrefabHash = prefabHash,
                        PrefabName = TryResolveFapName(prefabHash),
                        SamplePosition = zdo.GetPosition(),
                    };
                    byPrefabHash[prefabHash] = group;
                }
                group.ZdoCount++;
            }

            var groups = new List<OrphanGroup>(byPrefabHash.Values);
            groups.Sort((left, right) => right.ZdoCount.CompareTo(left.ZdoCount));
            return groups;
        }

        // Claims each ZDO before destroying it: DestroyZDO early-returns for a non-owner, and
        // these belong to whichever peer placed them.
        public static int Purge(ICollection<int> prefabHashes)
        {
            if (prefabHashes == null || prefabHashes.Count == 0) return 0;

            var allZdos = VAZdoManAccess.SnapshotAllZdos();
            if (allZdos == null) return 0;

            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return 0;

            long sessionId = ZDOMan.GetSessionID();
            int destroyed = 0;
            for (int i = 0; i < allZdos.Count; i++)
            {
                var zdo = allZdos[i];
                if (zdo == null) continue;
                if (!prefabHashes.Contains(zdo.GetPrefab())) continue;

                try
                {
                    if (!zdo.IsOwner()) zdo.SetOwner(sessionId);
                    zdoMan.DestroyZDO(zdo);
                    destroyed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapOrphanZdoScan — destroying orphan ZDO {zdo.m_uid} threw: {ex.Message}");
                }
            }
            return destroyed;
        }

        // Blueprint files on disk, not the live registries: the registries are empty for a
        // non-admin and before the deferred blueprint scan runs, which is exactly when an
        // orphan's zone streams in.
        private static string TryResolveFapName(int prefabHash)
        {
            if (_fapNamesByHash == null) _fapNamesByHash = BuildFapNameIndex();
            return _fapNamesByHash.TryGetValue(prefabHash, out var name) ? name : null;
        }

        private static Dictionary<int, string> BuildFapNameIndex()
        {
            var index = new Dictionary<int, string>();
            try
            {
                foreach (var path in VABlueprintLoader.EnumerateAllBlueprintFiles())
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    AddName(index, VABlueprintHammerIntegration.MakeStubName(path));
                    AddName(index, VABakeBundle.MakePrefabName(Path.GetFileName(path)));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapOrphanZdoScan — building the blueprint name index threw: {ex.Message}");
            }
            return index;
        }

        private static void AddName(Dictionary<int, string> index, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            index[prefabName.GetStableHashCode()] = prefabName;
        }
    }
}
