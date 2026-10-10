using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.201 (Fire 10-09: "the resize corner shows a plain square"): an Image with no sprite draws a solid square. This builds
    // the classic corner grip (three diagonal ridges in the corner's triangle, transparent elsewhere) as a white sprite the
    // Image tints, one per corner, cached. Needs no font glyph and no asset.
    internal static class VAGripSprite
    {
        private const int Size = 32;
        private static readonly Dictionary<int, Sprite> s_cache = new Dictionary<int, Sprite>();

        // corner: (1,0) bottom-right, (0,0) bottom-left, (1,1) top-right, (0,1) top-left (the same values as an anchor).
        internal static Sprite Get(Vector2 corner)
        {
            int key = (corner.x > 0.5f ? 1 : 0) | (corner.y > 0.5f ? 2 : 0);
            if (s_cache.TryGetValue(key, out var cached) && cached != null) return cached;

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    // Distance from the grip's own corner, in "diagonal" units: 0 at the corner, 2*Size at the far one.
                    int dx = (key & 1) != 0 ? (Size - 1 - x) : x;
                    int dy = (key & 2) != 0 ? (Size - 1 - y) : y;
                    int d = dx + dy;
                    // 1.1.202: four bolder ridges filling the corner triangle (3 of every 7 px, d < 30), readable at 18 px on screen.
                    bool ridge = d >= 2 && d < 30 && ((d - 2) % 7) < 3;
                    px[y * Size + x] = ridge ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            var sprite = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "VAGripSprite_" + key;
            s_cache[key] = sprite;
            VaPieces.Log?.LogDebug($"[FAP] resize grip sprite built: {sprite.name} (procedural, corner {corner})");
            return sprite;
        }
    }
}
