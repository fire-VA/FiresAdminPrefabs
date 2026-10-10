using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.182 (Fire 10-07, the item stand editor: clicking into it closed the build menu and the camera spun): FAP's editors draw
    // with IMGUI since 1.1.180, and IMGUI is invisible to uGUI's EventSystem. A click inside an editor that sat over the build
    // menu also reached the piece icon behind it (BuildUi.OnSelectPiece -> Hud.CloseBuildUi), and with the build menu gone
    // GameCamera.UpdateMouseCapture locked the cursor again every frame, so mouse look took over.
    // While any FAP IMGUI window is open: an invisible uGUI blocker sits under each window's rect (top sorting order, so the
    // EventSystem sees UI there and nothing behind it gets the click), and the cursor stays free and visible. Keys are blocked
    // by Core's FiresInputBlock, which each window holds.
    public static class VAImguiGuard
    {
        private sealed class Entry { public object Owner; public Func<Rect> Rect; public string Name; public RectTransform Blocker; }

        private static readonly List<Entry> _open = new List<Entry>();
        private static Canvas _canvas;

        public static bool AnyOpen => _open.Count > 0;

        public static void Open(object owner, Func<Rect> rect, string name)
        {
            if (owner == null || rect == null) return;
            if (_open.Exists(e => ReferenceEquals(e.Owner, owner))) return;
            EnsureCanvas();
            var go = new GameObject("FAP_ImguiBlocker_" + name);
            go.transform.SetParent(_canvas.transform, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = true;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            _open.Add(new Entry { Owner = owner, Rect = rect, Name = name, Blocker = rt });
            Sync();
            Log($"{name} opened");
        }

        public static void Close(object owner)
        {
            int i = _open.FindIndex(e => ReferenceEquals(e.Owner, owner));
            if (i < 0) return;
            var entry = _open[i];
            _open.RemoveAt(i);
            if (entry.Blocker != null) UnityEngine.Object.Destroy(entry.Blocker.gameObject);
            Log($"{entry.Name} closed");
        }

        private static void Log(string what)
        {
            if (ConfigManager.Instance.configVerboseLogging?.Value != true) return;
            Debug.Log($"{VaPieces.PluginName}: [ImguiGuard] {what}; open windows {_open.Count}; cursor {Cursor.lockState}, visible {Cursor.visible}; build menu {(Hud.IsPieceSelectionVisible() ? "open" : "closed")}");
        }

        private static void EnsureCanvas()
        {
            if (_canvas != null) return;
            var go = new GameObject("FAP_ImguiGuardCanvas");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32767;
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<Driver>();
        }

        // IMGUI rects are top-down pixels; the overlay canvas is bottom-up pixels (ConstantPixelSize, scale 1).
        private static void Sync()
        {
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                var e = _open[i];
                if (e.Owner is UnityEngine.Object uo && uo == null) { if (e.Blocker != null) UnityEngine.Object.Destroy(e.Blocker.gameObject); _open.RemoveAt(i); continue; }
                Rect r;
                try { r = e.Rect(); } catch { continue; }
                if (e.Blocker == null) continue;
                e.Blocker.anchoredPosition = new Vector2(r.x, Screen.height - r.y - r.height);
                e.Blocker.sizeDelta = new Vector2(r.width, r.height);
            }
        }

        private static void HoldCursor()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        private sealed class Driver : MonoBehaviour
        {
            private void LateUpdate()
            {
                if (_open.Count == 0) return;
                Sync();
                HoldCursor();
            }
        }

        // Vanilla re-locks the cursor here every frame unless one of its own menus is open.
        [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
        public static class GameCamera_UpdateMouseCapture_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (_open.Count == 0) return true;
                HoldCursor();
                return false;
            }
        }
    }
}
