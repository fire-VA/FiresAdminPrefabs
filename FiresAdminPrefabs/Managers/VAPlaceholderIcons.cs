using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.1.328 (public report via Fire: PlanBuild "CustomPiece 'sfx_serpent_attack_planned' has no icon" / "is not valid"): a sound
    // prefab has nothing to draw, so FAP's sfx_ pieces stayed iconless and PlanBuild's "<name>_planned" mirror of them was rejected by
    // Jotunn. Every sfx_ piece FAP creates now gets this small speaker icon at creation (drawn once, shared), so no FAP piece is ever
    // iconless and FAP's own menu shows what it is.
    internal static class VAPlaceholderIcons
    {
        private const int Size = 64;
        private static Sprite s_sound;

        internal static Sprite Sound()
        {
            if (s_sound != null) return s_sound;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "FAP_SoundIcon", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var clear = new Color(0f, 0f, 0f, 0f);
            var ink = new Color(0.92f, 0.88f, 0.78f, 1f);
            var px = new Color[Size * Size];
            for (int i = 0; i < px.Length; i++) px[i] = clear;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float fx = x - 10f, fy = y - 32f;
                    bool box = x >= 10 && x < 20 && y >= 24 && y < 40;                              // speaker body
                    bool cone = x >= 20 && x < 32 && Mathf.Abs(fy) <= 8f + (x - 20) * 0.9f;          // speaker cone
                    float r = Mathf.Sqrt(fx * fx + fy * fy);
                    bool front = x > 34 && Mathf.Abs(fy) < (x - 18) * 0.85f;
                    bool wave = front && ((r > 27f && r < 30.5f) || (r > 35f && r < 38.5f) || (r > 43f && r < 46.5f));   // sound waves
                    if (box || cone || wave) px[y * Size + x] = ink;
                }
            tex.SetPixels(px);
            tex.Apply(false, true);
            s_sound = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
            s_sound.name = "FAP_SoundIcon";
            return s_sound;
        }
    }
}
