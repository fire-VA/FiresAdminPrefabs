using System;
using System.Reflection;
using FiresCore.UI.Shell;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.EventSystems;

namespace VerdantsAscent.Utilities
{
    // 1.1.295 (Fire: Sound Presets, one widget style): cached-REFLECTION adapter over Core 0.2.642
    // FiresCore.UI.Shell.Kit.KitControls (Toggle / Slider), reused from FiresGuilds 1.0.23 UI\KitControlsAdapter.cs.
    // Core older than 0.2.642: a kit-drawn fallback (Widgets.Pill / Widgets.Slider visuals made interactive here). Logs once which path runs.
    internal static class VAKitControls
    {
        private static bool s_resolved, s_logged;
        private static MethodInfo s_toggle, s_slider;

        private static void Resolve()
        {
            if (s_resolved) return; s_resolved = true;
            try
            {
                Type kt = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { try { kt = a.GetType("FiresCore.UI.Shell.Kit.KitControls", false); } catch { } if (kt != null) break; }
                if (kt == null) return;
                foreach (var m in kt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var n = m.GetParameters().Length;
                    if (m.Name == "Toggle" && n == 10) s_toggle = m;
                    else if (m.Name == "Slider" && n == 13) s_slider = m;
                }
            }
            catch { }
        }

        private static void LogOnce()
        {
            if (s_logged) return; s_logged = true;
            Debug.Log("[FiresAdminPrefabs] [CoreUi] sound preset toggles/sliders: " + (s_toggle != null && s_slider != null
                ? "Core KitControls (reflection)" : "KitControls absent (Core < 0.2.642) -> FAP kit fallback"));
        }

        internal static void Toggle(RectTransform parent, string id, bool on, float x, float y, Action<bool> onChanged, float height = 26f)
        {
            Resolve(); LogOnce();
            if (s_toggle != null && parent != null)
            {
                try { if (s_toggle.Invoke(null, new object[] { parent, id, on, x, y, onChanged, height, false, "ON", "OFF" }) != null) return; }
                catch (Exception ex) { Debug.LogWarning("[FiresAdminPrefabs] KitControls.Toggle failed: " + ex.Message); }
            }
            FallbackToggle(parent, id, on, x, y, onChanged);
        }

        internal static void Slider(RectTransform parent, string id, float min, float max, float value, float x, float y, float width,
                                    Action<float> onChanged, float step = 0f, string format = "0.##", float height = 26f)
        {
            Resolve(); LogOnce();
            if (s_slider != null && parent != null)
            {
                try { if (s_slider.Invoke(null, new object[] { parent, id, min, max, value, x, y, width, onChanged, null, step, format, height }) != null) return; }
                catch (Exception ex) { Debug.LogWarning("[FiresAdminPrefabs] KitControls.Slider failed: " + ex.Message); }
            }
            FallbackSlider(parent, id, min, max, value, x, y, width, onChanged, step, format, height);
        }

        // ---- fallback (Core < 0.2.642): the kit visuals, rebuilt on change ----

        private static void FallbackToggle(RectTransform parent, string id, bool on, float x, float y, Action<bool> onChanged)
        {
            var host = Widgets.Node(parent, "Toggle_" + id);
            Widgets.Place(host, x, y, 90f, 26f);
            bool state = on;
            var vis = Widgets.Node(host, "Vis");
            Widgets.Stretch(vis, 0, 0, 0, 0);
            void Draw()
            {
                for (int i = vis.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(vis.GetChild(i).gameObject);
                var p = Widgets.Pill(vis, id, state ? "ON" : "OFF", state, 0f, 0f);
                foreach (var g in p.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.raycastTarget = false;
            }
            Draw();
            var hit = Widgets.Fill(host, "Hit", new Color(0f, 0f, 0f, 0f));
            Widgets.Stretch(hit.rectTransform, 0, 0, 0, 0);
            hit.raycastTarget = true;
            ShellWidgets.Clickable(host, () => { state = !state; Draw(); try { onChanged?.Invoke(state); } catch { } });
        }

        private static void FallbackSlider(RectTransform parent, string id, float min, float max, float value, float x, float y, float width,
                                           Action<float> onChanged, float step, string format, float height)
        {
            var host = Widgets.Node(parent, "Slider_" + id);
            Widgets.Place(host, x, y, width, height);
            var drag = host.gameObject.AddComponent<FallbackSliderDrag>();
            drag.Init(host, id, min, max, value, width, height, step, format, onChanged);
        }

        private sealed class FallbackSliderDrag : MonoBehaviour, IPointerDownHandler, IDragHandler
        {
            private RectTransform _host, _visual;
            private string _id, _format;
            private float _min, _max, _value, _width, _height, _step;
            private Action<float> _onChanged;

            internal void Init(RectTransform host, string id, float min, float max, float value, float width, float height, float step, string format, Action<float> onChanged)
            {
                _host = host; _id = id; _min = min; _max = max; _value = value; _width = width; _height = height; _step = step; _format = format; _onChanged = onChanged;
                var hit = Widgets.Fill(host, "Hit", new Color(0f, 0f, 0f, 0f));
                Widgets.Stretch(hit.rectTransform, 0, 0, 0, 0);
                hit.raycastTarget = true;
                Draw();
            }

            private void Draw()
            {
                if (_visual != null) Destroy(_visual.gameObject);
                float t = _max > _min ? (_value - _min) / (_max - _min) : 0f;
                _visual = Widgets.Slider(_host, _id, t, _value.ToString(_format), 0f, 0f, _width, _height);
                foreach (var g in _visual.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.raycastTarget = false;
                _visual.SetAsFirstSibling();
            }

            public void OnPointerDown(PointerEventData e) => Move(e);
            public void OnDrag(PointerEventData e) => Move(e);

            private void Move(PointerEventData e)
            {
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_host, e.position, e.pressEventCamera, out var local)) return;
                float trackW = Mathf.Max(1f, _width - 52f - FiresTokens.S12);
                float t = Mathf.Clamp01((local.x - _host.rect.xMin) / trackW);
                float v = Mathf.Lerp(_min, _max, t);
                if (_step > 0f) v = Mathf.Round(v / _step) * _step;
                if (Mathf.Approximately(v, _value)) return;
                _value = v;
                Draw();
                try { _onChanged?.Invoke(v); } catch { }
            }
        }
    }
}
