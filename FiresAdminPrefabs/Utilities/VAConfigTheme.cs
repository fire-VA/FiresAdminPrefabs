using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Utilities
{
    // Shared warm-brown / parchment palette for FAP's right-click context menu
    // and the monster / container / clutter configure windows, matching the
    // RPG Maker UI panels and Core's tooltips (the slightly-transparent brown
    // background + gold border + cream text).
    //
    // Values are hard-copied from FiresCore.Help.HelpTheme + the tooltip style
    // override on purpose: those are `internal` in Core and can't be aliased
    // cross-assembly. This is the SINGLE source for all four FAP config UIs so
    // the palette can never drift between them again.
    internal static class VAConfigTheme
    {
        // Surfaces
        public static readonly Color PanelBg      = new Color(0.06f, 0.05f, 0.04f, 0.97f);
        public static readonly Color BarBg        = new Color(0.08f, 0.07f, 0.05f, 0.95f);
        public static readonly Color BodyBg       = new Color(0.07f, 0.06f, 0.04f, 0.95f);
        public static readonly Color SectionBg    = new Color(0.20f, 0.16f, 0.08f, 0.95f);
        public static readonly Color FieldBg      = new Color(0.30f, 0.20f, 0.09f, 0.22f);
        public static readonly Color Backdrop     = new Color(0f, 0f, 0f, 0.85f);

        // Text
        public static readonly Color SectionLabel = new Color(0.95f, 0.80f, 0.45f, 1f);
        public static readonly Color Label        = new Color(0.85f, 0.75f, 0.55f, 1f);
        public static readonly Color Hint         = new Color(0.60f, 0.50f, 0.35f, 0.8f);
        public static readonly Color FieldInk     = new Color(0.95f, 0.92f, 0.78f, 1f);
        public static readonly Color ButtonInk    = new Color(0.97f, 0.92f, 0.78f, 1f);

        // Buttons
        public static readonly Color ButtonPrimary = new Color(0.50f, 0.34f, 0.12f, 0.92f);
        public static readonly Color ButtonDanger  = new Color(0.55f, 0.20f, 0.20f, 1f);
        public static readonly Color ButtonNeutral = new Color(0.15f, 0.12f, 0.08f, 0.9f);

        // Chrome
        public static readonly Color ScrollHandle = new Color(0.40f, 0.30f, 0.15f, 0.7f);
        public static readonly Color Divider      = new Color(0.40f, 0.30f, 0.15f, 0.4f);

        // Border (Outline) — the gold edge that reads as "the RPG Maker look".
        public static readonly Color BorderColor   = new Color(0.50f, 0.40f, 0.25f, 0.8f);
        public static readonly Vector2 BorderDist  = new Vector2(2f, -2f);
        public static readonly Color EdgeColor     = new Color(0.21f, 0.13f, 0.05f, 0.70f);
        public static readonly Vector2 EdgeDist    = new Vector2(1.5f, -1.5f);

        // Adds (or updates) a UnityEngine.UI.Outline. Apply to panel roots, bars,
        // section strips and buttons — NOT to ScrollRect viewports/content.
        public static void AddBorder(GameObject go, Color color, Vector2 dist)
        {
            if (go == null) return;
            var o = go.GetComponent<Outline>() ?? go.AddComponent<Outline>();
            o.effectColor = color;
            o.effectDistance = dist;
            o.enabled = true;
        }
    }
}
