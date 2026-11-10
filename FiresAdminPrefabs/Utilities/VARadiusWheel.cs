using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.291 (Fire, TEAM RULE: every FAP radius tool uses wheel = radius and Shift+wheel = height): THE one wheel read for every
    // FAP radius tool (the swapper and every brush keyed by VAClutterBrushIndicator). Core RadiusWheel.Read (Core 0.2.640+) when
    // that method exists, found once by reflection; otherwise the same bindings mirrored here: Input.GetAxis("Mouse ScrollWheel"),
    // plain = radius, Shift = height, nothing while Ctrl or Alt is held. Callers multiply the +-1 steps by their own step.
    // One height value is shared by every tool: the cylinder is centred on the aim, aim.y - Height/2 .. aim.y + Height/2.
    public static class VARadiusWheel
    {
        public const float DefaultHeight = 8f;
        public const float HeightStep = 1f;
        public const float MinHeight = 1f;
        public const float MaxHeight = 64f;
        private const float WheelDeadzone = 0.01f;

        public static float Height = DefaultHeight;

        // True while Core's reader runs, so FAP's own ZInput scroll suppression (VASwapUi) never hides the wheel from it.
        internal static bool ReadingRaw;

        private static bool _resolved;
        private static MethodInfo _coreRead;

        public static bool Read(out float dRadius, out float dHeight)
        {
            dRadius = 0f;
            dHeight = 0f;
            // 1.1.306 (Fire 10-21: preset UIs must block inputs): the wheel is read raw (Unity's Input can't be patched), so a scroll
            // over a preset list resized the radius tool behind it. While a FAP window or Core's text capture is up, the wheel is the window's.
            if (VAImguiGuard.AnyOpen || VASwapPresetUi.IsOpen || VARightClickMenu.PromptOpen || FiresCore.Input.FiresInputBlock.IsCapturing) return false;
            Resolve();
            if (_coreRead != null)
            {
                var args = new object[] { 0f, 0f };
                try
                {
                    ReadingRaw = true;
                    bool ok = (bool)_coreRead.Invoke(null, args);
                    dRadius = (float)args[0];
                    dHeight = (float)args[1];
                    return ok;
                }
                catch (Exception ex)
                {
                    _coreRead = null;
                    VaPieces.Log?.LogWarning($"[RadiusWheel] Core RadiusWheel.Read threw ({ex.GetType().Name}: {ex.Message}); FAP's mirror of the same bindings is used from now on");
                }
                finally { ReadingRaw = false; }
            }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (ctrl || alt) return false;
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (wheel > -WheelDeadzone && wheel < WheelDeadzone) return false;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float sign = Mathf.Sign(wheel);
            if (shift) dHeight = sign; else dRadius = sign;
            return true;
        }

        public static void StepHeight(float dHeight)
        {
            if (dHeight == 0f) return;
            Height = Mathf.Clamp(Height + Mathf.Sign(dHeight) * HeightStep, MinHeight, MaxHeight);
        }

        public static void Bounds(float aimY, out float yMin, out float yMax)
        {
            yMin = aimY - Height * 0.5f;
            yMax = aimY + Height * 0.5f;
        }

        public static string Hint(float radius) => $"Radius {radius:0.#} m · Height {Height:0} m";

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType("FiresCore.Input.RadiusWheel", false); } catch { }
                    if (t != null) break;
                }
                var byRef = typeof(float).MakeByRefType();
                _coreRead = t?.GetMethod("Read", BindingFlags.Public | BindingFlags.Static, null, new[] { byRef, byRef }, null);
                if (_coreRead != null && _coreRead.ReturnType != typeof(bool)) _coreRead = null;
            }
            catch { _coreRead = null; }
            VaPieces.Log?.LogDebug(_coreRead != null
                ? "[RadiusWheel] using Core RadiusWheel.Read"
                : "[RadiusWheel] Core RadiusWheel.Read not present (Core older than 0.2.640); FAP mirrors its bindings (wheel = radius, Shift = height, Ctrl/Alt = nothing)");
        }
    }
}
