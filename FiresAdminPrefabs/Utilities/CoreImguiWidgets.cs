using System;
using System.Reflection;
using FiresCore.UI;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// 1.1.293: Core 0.2.643 FiresCore.UI.Shell.Kit.FiresImguiWidgets (PillSwitchLayout / SlimSliderLayout) called through
    /// delegates built once by reflection, so FAP still runs on an older Core. Absent = the caller's old control; the path is logged once.
    /// </summary>
    internal static class CoreImguiWidgets
    {
        private delegate bool PillFn(bool on, GUILayoutOption[] options);
        private delegate float SliderFn(float v, float min, float max, string valueText, float width);

        private static bool s_looked;
        private static PillFn s_pill;
        private static SliderFn s_slider;

        private static void Look()
        {
            if (s_looked) return;
            s_looked = true;
            try
            {
                var t = Type.GetType("FiresCore.UI.Shell.Kit.FiresImguiWidgets, FiresUnifiedCore");
                var p = t?.GetMethod("PillSwitchLayout", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(GUILayoutOption[]) }, null);
                var s = t?.GetMethod("SlimSliderLayout", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float), typeof(float), typeof(float), typeof(string), typeof(float) }, null);
                if (p != null && p.ReturnType == typeof(bool)) s_pill = (PillFn)Delegate.CreateDelegate(typeof(PillFn), p);
                if (s != null && s.ReturnType == typeof(float)) s_slider = (SliderFn)Delegate.CreateDelegate(typeof(SliderFn), s);
            }
            catch (Exception ex) { Debug.LogWarning("[FiresAdminPrefabs] [CoreUi] imgui widgets lookup failed: " + ex.Message); }
            Debug.Log($"[FiresAdminPrefabs] [CoreUi] imgui widgets: pill switch {(s_pill != null ? "Core" : "fallback")}, slim slider {(s_slider != null ? "Core" : "fallback")}{(s_pill == null || s_slider == null ? " (Core older than 0.2.643: old FAP controls)" : "")}");
        }

        public static bool HasPill { get { Look(); return s_pill != null; } }
        public static bool HasSlider { get { Look(); return s_slider != null; } }

        public static bool Pill(bool on, params GUILayoutOption[] options) { Look(); return s_pill(on, options); }
        public static float Slider(float v, float min, float max, string valueText, float width) { Look(); return s_slider(v, min, max, valueText, width); }

        /// <summary>Opacity header slider: Core slim slider when present, else the old FAP slider (same range, same width).</summary>
        public static float OpacitySlider(float v, float min, float max, float width)
        {
            if (HasSlider) return Slider(v, min, max, VASoundPresetWindow.Fmt(v, "0.00"), width);   // 1.1.321 (UI audit P6): formatted once per value
            return GUILayout.HorizontalSlider(v, min, max, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(width));
        }
    }
}
