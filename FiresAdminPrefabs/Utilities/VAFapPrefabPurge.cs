using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Deleting ZDOs of a prefab that DOES resolve - the case VAFapOrphanZdoScan cannot cover, because it only
    // finds ZDOs whose prefab is missing. A blueprint whose parent piece is gone leaves its child objects behind
    // as ordinary, perfectly valid ZDOs: they instantiate, they tick, they upload, and nothing reports them as
    // wrong. Removing those is a world edit with no undo, so the command dry-runs unless the caller says confirm.
    public static class VAFapPrefabPurge
    {
        public sealed class Match
        {
            public int PrefabHash;
            public string PrefabName;
            public int TotalHeld;
            public int WithinRadius;
            public float NearestDistance = float.MaxValue;
            public float FarthestDistance;
            public readonly List<Vector3> Samples = new List<Vector3>();
            public readonly Dictionary<Vector2i, int> ByZone = new Dictionary<Vector2i, int>();
        }

        public struct Vector2i : IEquatable<Vector2i>
        {
            public readonly int X, Y;
            public Vector2i(int x, int y) { X = x; Y = y; }
            public bool Equals(Vector2i other) => X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is Vector2i other && Equals(other);
            public override int GetHashCode() => (X * 397) ^ Y;
            public override string ToString() => X + "," + Y;
        }

        private const int MaxSamples = 5;

        /// <summary>
        /// Resolves a prefab by name or by raw stable hash. Returns 0 when the text names nothing this machine
        /// knows, so a typo can never be mistaken for a prefab with no copies placed.
        /// </summary>
        public static int ResolveHash(string prefabOrHash, out string resolvedName)
        {
            resolvedName = null;
            if (string.IsNullOrEmpty(prefabOrHash)) return 0;

            var scene = ZNetScene.instance;
            if (scene == null) return 0;

            int hash = prefabOrHash.GetStableHashCode();
            GameObject prefab = scene.GetPrefab(hash);
            if (prefab != null)
            {
                resolvedName = prefab.name;
                return hash;
            }

            if (int.TryParse(prefabOrHash, out int literalHash))
            {
                prefab = scene.GetPrefab(literalHash);
                if (prefab != null)
                {
                    resolvedName = prefab.name;
                    return literalHash;
                }
            }
            return 0;
        }

        /// <summary>
        /// What a purge of this prefab would touch. Null when the ZDO table is not readable. `radius` below zero
        /// means every copy this machine holds, whatever the distance.
        /// </summary>
        public static Match Survey(int prefabHash, string prefabName, Vector3 center, float radius)
        {
            var allZdos = VAZdoManAccess.SnapshotAllZdos();
            if (allZdos == null) return null;

            var match = new Match { PrefabHash = prefabHash, PrefabName = prefabName };
            bool unlimited = radius < 0f;
            float radiusSq = radius * radius;

            for (int i = 0; i < allZdos.Count; i++)
            {
                ZDO zdo = allZdos[i];
                if (zdo == null || zdo.GetPrefab() != prefabHash) continue;
                match.TotalHeld++;

                Vector3 position = zdo.GetPosition();
                float distance = Vector3.Distance(center, position);
                if (distance < match.NearestDistance) match.NearestDistance = distance;
                if (distance > match.FarthestDistance) match.FarthestDistance = distance;

                if (!unlimited && (position - center).sqrMagnitude > radiusSq) continue;
                match.WithinRadius++;
                if (match.Samples.Count < MaxSamples) match.Samples.Add(position);

                Vector2s zone = ZoneSystem.GetZone(position);
                var key = new Vector2i(zone.x, zone.y);
                match.ByZone.TryGetValue(key, out int count);
                match.ByZone[key] = count + 1;
            }

            if (match.TotalHeld == 0) match.NearestDistance = 0f;
            return match;
        }

        /// <summary>
        /// Destroys every held ZDO of this prefab inside the radius. Each is claimed first: DestroyZDO early-returns
        /// for a non-owner, and these belong to whichever peer placed them.
        /// </summary>
        public static int Purge(int prefabHash, Vector3 center, float radius)
        {
            var allZdos = VAZdoManAccess.SnapshotAllZdos();
            if (allZdos == null) return 0;

            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) return 0;

            bool unlimited = radius < 0f;
            float radiusSq = radius * radius;
            long sessionId = ZDOMan.GetSessionID();
            int destroyed = 0;

            for (int i = 0; i < allZdos.Count; i++)
            {
                ZDO zdo = allZdos[i];
                if (zdo == null || zdo.GetPrefab() != prefabHash) continue;
                if (!unlimited && (zdo.GetPosition() - center).sqrMagnitude > radiusSq) continue;

                try
                {
                    if (!zdo.IsOwner()) zdo.SetOwner(sessionId);
                    zdoMan.DestroyZDO(zdo);
                    destroyed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapPrefabPurge — destroying ZDO {zdo.m_uid} threw: {ex.Message}");
                }
            }
            return destroyed;
        }
    }

    // 1.1.380 (lead 11-03: fap_purge_prefab said "Destroyed N" while all 9 towers stayed in the save): a client only holds the ZDOs near it and only
    // queues the destroy, so the count meant nothing. The server holds every ZDO: an admin's purge runs there and the real result comes back.
    public static class VAFapPrefabPurgeRpc
    {
        private const string RequestRpc = "FAP_PurgePrefab";
        private const string ResultRpc = "FAP_PurgePrefabResult";
        private const float MaxRadius = 20000f;
        private static ZRoutedRpc s_registeredOn;
        private static Terminal s_waiting;

        public static void Register()
        {
            if (ZRoutedRpc.instance == null || ReferenceEquals(s_registeredOn, ZRoutedRpc.instance)) return;
            s_registeredOn = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register<int, Vector3, float, bool>(RequestRpc, OnRequest);
            ZRoutedRpc.instance.Register<string>(ResultRpc, OnResult);
        }

        public static void Send(Terminal context, int prefabHash, Vector3 center, float radius, bool confirm)
        {
            Register();
            s_waiting = context;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, prefabHash, center, radius, confirm);
        }

        private static void OnRequest(long sender, int prefabHash, Vector3 center, float radius, bool confirm)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string result;
            if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender)) result = "refused: admin on the server only";
            else if (float.IsNaN(center.x) || float.IsNaN(center.y) || float.IsNaN(center.z) || float.IsNaN(radius) || radius > MaxRadius || (radius <= 0f && radius != -1f)) result = "refused: bad position or radius";
            else
            {
                string name = ZNetScene.instance?.GetPrefab(prefabHash)?.name ?? prefabHash.ToString();
                var match = VAFapPrefabPurge.Survey(prefabHash, name, center, radius);
                if (match == null) result = "refused: ZDO table not readable";
                else if (!confirm) result = $"server holds {match.TotalHeld} '{name}', {match.WithinRadius} would go ({match.ByZone.Count} zone(s), {match.NearestDistance:0} to {match.FarthestDistance:0} m). Nothing deleted.";
                else
                {
                    int removed = VAFapPrefabPurge.Purge(prefabHash, center, radius);
                    result = $"server removed {removed} '{name}'; {match.TotalHeld - removed} left in the world.";
                }
            }
            Debug.Log($"{VaPieces.PluginName}: fap_purge_prefab from {sender}: {result}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ResultRpc, result);
        }

        private static void OnResult(long sender, string result)
        {
            if (sender != ZRoutedRpc.instance.GetServerPeerID() || string.IsNullOrEmpty(result) || result.Length > 300) return;
            Debug.Log($"{VaPieces.PluginName}: fap_purge_prefab: {result}");
            if (s_waiting != null) s_waiting.AddString("[FAP] " + result);
        }
    }
}
