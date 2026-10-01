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
}
