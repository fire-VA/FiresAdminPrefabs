using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Paints a small "cached" badge into the corner of a blueprint stub's hammer icon,
    // so an admin can see at a glance which blueprints are pre-baked.
    //
    // The badge is composited straight into the icon Texture2D rather than overlaid as
    // UI. An overlay would mean either tracking grid-slot GameObjects, which vanilla
    // rebuilds on every tab change, or patching Hud's grid build. Painting the texture
    // needs neither: it rides on Piece.m_icon wherever the icon appears, for a
    // one-time pass over a few dozen pixels.
    //
    // Call ApplyBadge when a bake completes and when the disk preload finishes. A
    // badge going stale isn't handled: the cache only clears on ZNetScene.Shutdown,
    // by which point there's no hammer UI left to show it.
    public static class VABakeCacheIconBadge
    {
        // Track which icons we've already badged so we don't paint
        // twice (keyed by Sprite instance ID — robust against icon
        // re-renders).
        private static readonly HashSet<int> _badgedSprites = new HashSet<int>();

        // Cannabis-leaf palette. Three greens give the leaf depth:
        // bright top for highlights, mid green for the body, dark
        // green for shadows + outline. Drawing pattern lifted from
        // FiresNPCs CompanionRadialMenu's procedural-icon helpers
        // (DrawLine/DrawCircle on quantized RGBA32 pixel array).
        private static readonly Color32 LeafBright = new Color32(120, 240, 110, 255);   // top leaflets, lit
        private static readonly Color32 LeafMid    = new Color32(58, 180, 70, 255);     // body of each leaflet
        private static readonly Color32 LeafDark   = new Color32(20, 80, 30, 255);      // outline + shadow
        // Soft white halo behind the leaf — keeps it readable against
        // dark-textured icons (stone, marble, etc.).
        private static readonly Color32 LeafHalo   = new Color32(255, 255, 255, 70);

        // Badge size as a fraction of icon edge. 0.32 = top-right
        // ~third is the badge area; the leaf inside has padding so it
        // doesn't bleed to the corner. At 64px icons → 20px badge area,
        // ~16px leaf — clearly readable without obscuring the blueprint
        // preview underneath.
        private const float BadgeSizeFraction = 0.32f;

        // Applies the badge to a Piece's icon if the cache has an
        // entry for the cache key. No-op if cache miss OR if we've
        // already badged this icon. Returns true if the badge was
        // applied this call.
        public static bool ApplyBadgeIfCached(Piece piece, string cacheKey)
        {
            if (piece == null || piece.m_icon == null) return false;
            if (VaPieces.IsHeadless) return false;   // the badge is drawn through a GPU blit
            if (string.IsNullOrEmpty(cacheKey)) return false;
            if (!VAFapBlueprintBakeCache.Has(cacheKey)) return false;

            // Skip if already badged.
            int spriteId = piece.m_icon.GetInstanceID();
            if (_badgedSprites.Contains(spriteId)) return false;

            try
            {
                var srcTex = piece.m_icon.texture;
                if (srcTex == null) return false;

                // Some icon textures aren't readable (shader-rendered
                // RenderTextures, asset-bundle textures with R/W off).
                // Detect by trying GetPixels32 and catching the throw.
                Color32[] pixels;
                try { pixels = srcTex.GetPixels32(); }
                catch (UnityException)
                {
                    // Non-readable — fall back to a Blit copy via
                    // RenderTexture so we get a readable working copy.
                    pixels = ReadPixelsViaBlit(srcTex);
                    if (pixels == null) return false;
                }
                if (pixels == null || pixels.Length == 0) return false;

                int w = srcTex.width;
                int h = srcTex.height;

                int badgeW = Mathf.Max(8, Mathf.RoundToInt(w * BadgeSizeFraction));
                int badgeH = Mathf.Max(8, Mathf.RoundToInt(h * BadgeSizeFraction));

                // Top-right corner of the icon. Unity's GetPixels32
                // reads bottom-up (y=0 is the bottom row), so "top"
                // corresponds to high y indices.
                int x0 = w - badgeW;
                int y0 = h - badgeH;
                float cx = x0 + badgeW * 0.5f;
                float cy = y0 + badgeH * 0.5f;
                // Leaf reference radius — bumped to nearly fill the
                // badge circle. Each leaflet length is scaled
                // relative to this; halo + fan-origin are also
                // computed from r in PaintCannabisLeaf.
                float r = badgeW * 0.48f;

                PaintCannabisLeaf(pixels, w, h, cx, cy, r);

                // Write the modified pixels into a NEW Texture2D so
                // we don't mutate the source asset (which may be
                // shared across multiple icons in some configurations).
                var dst = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = srcTex.name + "_badged",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                dst.SetPixels32(pixels);
                dst.Apply(false, false);

                // Build a Sprite around the new texture (same rect
                // and pivot as the source — keeps UI sizing consistent).
                var newSprite = Sprite.Create(
                    dst,
                    new Rect(0, 0, w, h),
                    new Vector2(0.5f, 0.5f),
                    piece.m_icon.pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect);
                newSprite.name = piece.m_icon.name + "_badged";

                piece.m_icon = newSprite;
                _badgedSprites.Add(newSprite.GetInstanceID());
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeCacheIconBadge.ApplyBadgeIfCached on piece '{piece.name}' threw: {ex.Message}");
                return false;
            }
        }

        // Paints a seven-leaflet silhouette with the same procedural line/circle/arc
        // primitives the companion radial menu uses, onto a quantized RGBA buffer.
        //
        // Drawn back to front: a soft white halo for contrast on dark stone icons, the
        // seven leaflets fanned out (centre longest, base pair shortest and angled
        // down), each as a thick body line with a darker outline and a bright tip,
        // then veins from the base and a small stem cap. Angles are degrees CCW from
        // 12 o'clock.
        private static void PaintCannabisLeaf(Color32[] pixels, int w, int h, float cx, float cy, float r)
        {
            // A cannabis leaf naturally has more mass ABOVE its base
            // than below (leaflets fan upward from a stem). To make
            // the leaf VISUALLY CENTERED in the badge, we shift the
            // fan origin DOWN within the badge, so the topmost
            // leaflet tips reach near the top of the circle and the
            // base leaflets reach near the bottom.
            //
            // Empirically: shifting the fan origin by r*0.45 down
            // and using a top-leaflet length of r*1.45 puts the top
            // tip at cy + r and the lowest leaflet tip at cy - r,
            // visually centered.
            float fanCy = cy - r * 0.45f;

            // 1. Halo — centered on the BADGE center (cx, cy), sized
            //    so the leaf fills it with a thin margin. Halo
            //    radius = r ensures the leaf reads as filling the
            //    full badge circle.
            DrawSoftCircle(pixels, w, h, cx, cy, r * 1.05f, LeafHalo);

            // 2. Leaflets fanned around the FAN ORIGIN. (Angle,
            //    length-scale, thickness-scale). Center leaflet is
            //    the longest + thickest; outer/base leaflets get
            //    progressively shorter to match a real cannabis
            //    silhouette.
            //
            //    Angles measured from straight up (0°), positive =
            //    clockwise (right). Bottom leaflets at ±125° point
            //    down-and-outward — gives the leaf a 9-pointed
            //    silhouette that fills the circle.
            var leaflets = new (float angleDeg, float lenScale, float thicknessScale)[]
            {
                (    0f, 1.45f, 0.24f),   // center top — tallest
                (  30f, 1.25f, 0.22f),   // upper-right
                ( -30f, 1.25f, 0.22f),   // upper-left
                (  60f, 1.00f, 0.18f),   // right-side
                ( -60f, 1.00f, 0.18f),   // left-side
                (  95f, 0.75f, 0.15f),   // right-mid (just past horizontal)
                ( -95f, 0.75f, 0.15f),   // left-mid
                ( 130f, 0.55f, 0.13f),   // right-bottom (down + out)
                (-130f, 0.55f, 0.13f),   // left-bottom
            };

            // 3. Body pass — paint each leaflet's filled body from
            //    fanCy outward.
            for (int i = 0; i < leaflets.Length; i++)
            {
                var (angDeg, len, thick) = leaflets[i];
                float rad = angDeg * Mathf.Deg2Rad;
                // 0° = up = +y in pixel space (we read bottom-up).
                // Rotation positive = clockwise → +x, -y components.
                float dx = Mathf.Sin(rad);
                float dy = Mathf.Cos(rad);
                float tipX = cx + dx * (r * len);
                float tipY = fanCy + dy * (r * len);
                // Slight offset from fanCy so leaflets don't all
                // collapse to a single pixel at their base.
                float baseX = cx + dx * (r * 0.04f);
                float baseY = fanCy + dy * (r * 0.04f);
                DrawLine(pixels, w, h, baseX, baseY, tipX, tipY, LeafMid, r * thick);
            }

            // 4. Tip highlights — bright dots at each leaflet tip
            //    to give the leaf a lit, fresh look. Drawn AFTER
            //    bodies so they sit on top.
            for (int i = 0; i < leaflets.Length; i++)
            {
                var (angDeg, len, _) = leaflets[i];
                float rad = angDeg * Mathf.Deg2Rad;
                float dx = Mathf.Sin(rad);
                float dy = Mathf.Cos(rad);
                float tipX = cx + dx * (r * len);
                float tipY = fanCy + dy * (r * len);
                DrawSoftCircle(pixels, w, h, tipX, tipY, Mathf.Max(1f, r * 0.07f), LeafBright);
            }

            // 5. Central vein cluster — short dark lines radiating
            //    from fan origin, giving the leaf its iconic veiny
            //    look.
            for (int i = 0; i < leaflets.Length; i++)
            {
                var (angDeg, len, _) = leaflets[i];
                float rad = angDeg * Mathf.Deg2Rad;
                float dx = Mathf.Sin(rad);
                float dy = Mathf.Cos(rad);
                float midX = cx + dx * (r * len * 0.55f);
                float midY = fanCy + dy * (r * len * 0.55f);
                DrawLine(pixels, w, h, cx, fanCy, midX, midY, LeafDark, Mathf.Max(0.8f, r * 0.025f));
            }
        }

        // Anti-aliased filled circle. Mirrors CompanionRadialMenu's
        // DrawCircle but operating on Color32 + handles clipping at
        // image bounds.
        private static void DrawSoftCircle(Color32[] pixels, int w, int h, float cx, float cy, float radius, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius - 1f));
            int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(cx + radius + 1f));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius - 1f));
            int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(cy + radius + 1f));
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist <= radius - 0.5f)
                    {
                        BlendPixel(pixels, w, h, x, y, color, 1f);
                    }
                    else if (dist <= radius + 0.5f)
                    {
                        float blend = Mathf.Clamp01(radius + 0.5f - dist);
                        BlendPixel(pixels, w, h, x, y, color, blend);
                    }
                }
            }
        }

        // Anti-aliased thick line, sampled along its length and
        // capsule-stamped at each sample. Mirrors
        // CompanionRadialMenu.DrawLine.
        private static void DrawLine(Color32[] pixels, int w, int h, float x0, float y0, float x1, float y1, Color32 color, float thickness)
        {
            float steps = Mathf.Max(1f, Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) * 2f);
            float halfThickness = thickness * 0.5f;
            int stepCount = Mathf.CeilToInt(steps);
            for (int step = 0; step <= stepCount; step++)
            {
                float t = (float)step / stepCount;
                float px = Mathf.Lerp(x0, x1, t);
                float py = Mathf.Lerp(y0, y1, t);
                int minX = Mathf.Max(0, Mathf.FloorToInt(px - halfThickness - 1f));
                int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(px + halfThickness + 1f));
                int minY = Mathf.Max(0, Mathf.FloorToInt(py - halfThickness - 1f));
                int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(py + halfThickness + 1f));
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        float dx = x - px;
                        float dy = y - py;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist <= halfThickness - 0.5f)
                        {
                            BlendPixel(pixels, w, h, x, y, color, 1f);
                        }
                        else if (dist <= halfThickness + 0.5f)
                        {
                            float blend = Mathf.Clamp01(halfThickness + 0.5f - dist);
                            BlendPixel(pixels, w, h, x, y, color, blend);
                        }
                    }
                }
            }
        }

        // Source-over alpha blend onto the existing pixel. `alpha`
        // multiplies the source color's alpha (used by the AA edge
        // computation in DrawLine/DrawSoftCircle).
        private static void BlendPixel(Color32[] pixels, int w, int h, int x, int y, Color32 src, float alpha)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int idx = y * w + x;
            var dst = pixels[idx];
            float sa = (src.a / 255f) * alpha;
            if (sa <= 0f) return;
            if (sa >= 1f && dst.a < 1) { pixels[idx] = src; return; }
            float da = dst.a / 255f;
            float outA = sa + da * (1f - sa);
            if (outA <= 0f) return;
            byte outR = (byte)Mathf.Clamp(Mathf.RoundToInt(((src.r / 255f) * sa + (dst.r / 255f) * da * (1f - sa)) / outA * 255f), 0, 255);
            byte outG = (byte)Mathf.Clamp(Mathf.RoundToInt(((src.g / 255f) * sa + (dst.g / 255f) * da * (1f - sa)) / outA * 255f), 0, 255);
            byte outB = (byte)Mathf.Clamp(Mathf.RoundToInt(((src.b / 255f) * sa + (dst.b / 255f) * da * (1f - sa)) / outA * 255f), 0, 255);
            byte outAByte = (byte)Mathf.Clamp(Mathf.RoundToInt(outA * 255f), 0, 255);
            pixels[idx] = new Color32(outR, outG, outB, outAByte);
        }

        // Forgets the badged-sprite tracking for a specific stub so
        // the NEXT bake completion will re-paint the badge fresh.
        // Used by the right-click "Re-bake mesh" flow: we want the
        // stub's icon to lose its badge while the cache entry is
        // gone, then get the badge back when re-bake completes.
        public static void ForgetStub(Piece piece)
        {
            if (piece == null || piece.m_icon == null) return;
            try { _badgedSprites.Remove(piece.m_icon.GetInstanceID()); } catch { }
        }

        // Bulk refresh — walks every registered blueprint stub in
        // VABlueprintHammerIntegration and applies the badge where
        // the cache has an entry. Called after disk preload completes
        // + after a new bake finishes.
        public static int RefreshAllStubs()
        {
            int painted = 0;
            try
            {
                var scene = ZNetScene.instance;
                if (scene == null || scene.m_prefabs == null) return 0;

                foreach (var stubName in VerdantsAscent.Managers.VABlueprintHammerIntegration.RegisteredBlueprintPrefabNames)
                {
                    if (!VerdantsAscent.Managers.VABlueprintHammerIntegration.TryGetStubPrefab(stubName, out var stub) || stub == null) continue;
                    var piece = stub.GetComponent<Piece>();
                    if (piece == null) continue;

                    if (!VerdantsAscent.Managers.VABlueprintHammerIntegration.TryGetBlueprint(stubName, out var bp) || bp == null) continue;
                    string cacheKey = VerdantsAscent.Managers.VABlueprintHammerIntegration.GetCacheKey(bp);

                    if (ApplyBadgeIfCached(piece, cacheKey)) painted++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VABakeCacheIconBadge.RefreshAllStubs threw: {ex.Message}");
            }
            return painted;
        }

        // Fallback path for non-readable textures: blit through a
        // temporary RenderTexture into a fresh readable Texture2D,
        // then read pixels from THAT. Slower than direct GetPixels32
        // but works on every texture format/import setting.
        private static Color32[] ReadPixelsViaBlit(Texture src)
        {
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D readable = null;
            try
            {
                rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                readable.Apply(false, false);
                var pixels = readable.GetPixels32();
                return pixels;
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
    }
}
