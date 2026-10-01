using System;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Cell a bake part belongs to, in blueprint-local space. X and Z follow Valheim's 64m zone
    // grid; Y splits the column into slabs of the same height so a 270m tower becomes a stack of
    // cubes rather than one bucket whose LOD and collider state is decided by a point 130m up.
    public struct VABakeZoneKey : IEquatable<VABakeZoneKey>
    {
        private const float SlabHeightMeters = 64f;
        private const int HashCombineMultiplier = 397;

        public int X;
        public int Y;
        public int Z;

        public static VABakeZoneKey FromBlueprintLocalPosition(Vector3 blueprintLocalPosition)
        {
            var zone = ZoneSystem.GetZone(blueprintLocalPosition).ToVector2i();
            return new VABakeZoneKey
            {
                X = zone.x,
                Y = Mathf.FloorToInt(blueprintLocalPosition.y / SlabHeightMeters),
                Z = zone.y,
            };
        }

        public bool Equals(VABakeZoneKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is VABakeZoneKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * HashCombineMultiplier) ^ Y;
                hash = (hash * HashCombineMultiplier) ^ Z;
                return hash;
            }
        }

        public override string ToString() => $"{X},{Y},{Z}";
    }
}
