using System.Collections.Generic;

namespace VerdantsAscent.Managers
{
    // Holder for the per-prefab opt-in sets that the distance-rendering and
    // collider tuning passes reference. Empty at build time — populated by
    // config reload or future code paths.
    public static class VAPieceManager
    {
        public static readonly HashSet<string> DistantRenderPrefabs = new HashSet<string>();
    }
}
