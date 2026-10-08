using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using FiresCore.UI;

namespace VerdantsAscent.Utilities
{
    // Lightweight popup for right-click context actions on hammer pieces
    // and tabs. Long lists scroll; auto-dismisses on outside click / escape /
    // right-click.
    // 1.1.180 (Fire 10-07): drawn in Core's FiresRoundedSkin (IMGUI, the FDT look)
    // One hidden DontDestroyOnLoad host draws the menu and the prompt as IMGUI
    // windows brought to the front every frame (GUI.depth lowest), so they sit
    // over the hammer UI and over FAP's other IMGUI editor windows. A transparent
    // uGUI blocker (top canvas, sortingOrder = max) under the open rect keeps a
    // click on a row from also reaching the build menu's buttons underneath.
    public static class VARightClickMenu
    {
        public sealed class Item
        {
            public string Label;
            public Action OnSelect;
            public bool Disabled;
            public Item(string label, Action onSelect, bool disabled = false)
            {
                Label = label;
                OnSelect = onSelect;
                Disabled = disabled;
            }
        }

        private const int MenuWidth = 230;
        private const int ItemHeight = 28;
        private const int MenuPaddingY = 6;
        private const int MenuPaddingX = 6;
        private const int MaxVisibleHeight = 420;
        private const int LabelFontSize = 14;
        private const int CanvasSortingOrder = 32767;
        private const int MenuWindowId = 0x7A5C1;
        private const int PromptWindowId = 0x7A5C2;
        private const int HostGuiDepth = -1000;

        private static GameObject _hostGo;
        private static Host _host;
        private static Canvas _canvas;
        private static RectTransform _blocker;

        private static List<Item> _items;
        private static Rect _menuRect;        // IMGUI coords (top-down)
        private static bool _needsScroll;
        private static Vector2 _scroll;
        private static int _openedFrame = -1;

        public static bool IsOpen => _items != null;

        // 1.1.173: a caller that knows where the click was (an EventSystem click) sets this around its Show; else the cursor.
        public static Vector2? NextPosition;

        public static void Show(IList<Item> items)
        {
            Show(items, NextPosition ?? (Vector2)Input.mousePosition);
        }

        // screenPosition is a screen (bottom-up) point, as before: the menu's top-left corner sits there, clamped on screen.
        public static void Show(IList<Item> items, Vector2 screenPosition)
        {
            if (items == null || items.Count == 0) return;
            EnsureHost();
            Close();

            int totalContentHeight = items.Count * ItemHeight + MenuPaddingY * 2;
            _needsScroll = totalContentHeight > MaxVisibleHeight;
            int visibleHeight = _needsScroll ? MaxVisibleHeight : totalContentHeight;

            float x = Mathf.Clamp(screenPosition.x, 0f, Mathf.Max(0f, Screen.width - MenuWidth));
            float yUp = Mathf.Clamp(screenPosition.y, visibleHeight, Screen.height);
            _menuRect = new Rect(x, Screen.height - yUp, MenuWidth, visibleHeight);   // bottom-up -> IMGUI top-down
            _scroll = Vector2.zero;
            _items = new List<Item>(items);
            _openedFrame = Time.frameCount;
            UpdateBlocker();
        }

        public static void Close()
        {
            if (_items == null) return;
            // 1.1.173: who closed it (one frame up), at Debug: a menu closed the frame it opened shows its closer here.
            try { var m = new System.Diagnostics.StackFrame(1).GetMethod(); VaPieces.Log?.LogDebug($"[VARightClickMenu] closed by {m?.DeclaringType?.Name}.{m?.Name} (frame {Time.frameCount})"); } catch { }
            _items = null;
            UpdateBlocker();
        }

        private static void EnsureHost()
        {
            if (_hostGo != null) return;
            _hostGo = new GameObject("FAP_RightClickMenuHost");
            UnityEngine.Object.DontDestroyOnLoad(_hostGo);

            _canvas = _hostGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CanvasSortingOrder;
            var scaler = _hostGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _hostGo.AddComponent<GraphicRaycaster>();

            var blockerGo = new GameObject("Blocker");
            blockerGo.transform.SetParent(_hostGo.transform, false);
            var img = blockerGo.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = true;
            _blocker = img.rectTransform;
            _blocker.anchorMin = Vector2.zero; _blocker.anchorMax = Vector2.zero; _blocker.pivot = Vector2.zero;
            blockerGo.SetActive(false);

            _host = _hostGo.AddComponent<Host>();
        }

        // Swallows uGUI clicks under whichever of the menu / prompt is open (the prompt wins; it closes the menu).
        private static void UpdateBlocker()
        {
            if (_blocker == null) return;
            Rect? r = _promptOpen ? _promptRect : (IsOpen ? _menuRect : (Rect?)null);
            _blocker.gameObject.SetActive(r.HasValue);
            if (!r.HasValue) return;
            var v = r.Value;
            _blocker.anchoredPosition = new Vector2(v.x, Screen.height - v.yMax);   // IMGUI top-down -> canvas bottom-up
            _blocker.sizeDelta = new Vector2(v.width, v.height);
        }

        private static bool MouseOver(Rect imguiRect)
        {
            Vector2 m = Input.mousePosition;
            return imguiRect.Contains(new Vector2(m.x, Screen.height - m.y));
        }

        private static void Select(Item item)
        {
            var cb = item.OnSelect;
            Close();
            try { cb?.Invoke(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VARightClickMenu item callback threw: {ex.Message}");
            }
        }

        private static GUIStyle _rowStyle, _rowDisabledStyle, _promptTitleStyle, _promptFieldStyle;

        private static void EnsureRowStyles()
        {
            if (_rowStyle != null) return;
            // NavItem gives the hover highlight; the disabled copy has no hover/active and greyed text.
            _rowStyle = new GUIStyle(FiresRoundedSkin.NavItem) { fontSize = LabelFontSize, richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            _rowDisabledStyle = new GUIStyle(_rowStyle);
            _rowDisabledStyle.normal.background = null;
            _rowDisabledStyle.hover = _rowDisabledStyle.normal;
            _rowDisabledStyle.active = _rowDisabledStyle.normal;
            _rowDisabledStyle.focused = _rowDisabledStyle.normal;
            _rowDisabledStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            _promptTitleStyle = new GUIStyle(FiresRoundedSkin.Title) { alignment = TextAnchor.MiddleCenter };
            _promptFieldStyle = new GUIStyle(FiresRoundedSkin.TextInput) { fontSize = LabelFontSize };
        }

        private static void DrawMenu(int id)
        {
            var items = _items;
            if (items == null) return;
            float innerW = MenuWidth - MenuPaddingX * 2;
            int contentH = items.Count * ItemHeight;
            Rect view = new Rect(MenuPaddingX, MenuPaddingY, innerW, _menuRect.height - MenuPaddingY * 2);
            if (_needsScroll)
                _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0f, 0f, innerW - 16f, contentH), false, true, GUIStyle.none, GUI.skin.verticalScrollbar);
            else
                GUI.BeginGroup(view);

            float rowW = _needsScroll ? innerW - 16f : innerW;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var r = new Rect(0f, i * ItemHeight, rowW, ItemHeight);
                if (item.Disabled) { GUI.Label(r, item.Label ?? string.Empty, _rowDisabledStyle); continue; }
                if (GUI.Button(r, item.Label ?? string.Empty, _rowStyle)) { Select(item); break; }
            }

            if (_needsScroll) GUI.EndScrollView(); else GUI.EndGroup();
        }

        private sealed class Host : MonoBehaviour
        {
            private void Update()
            {
                if (_promptOpen) { PromptUpdate(); return; }
                if (!IsOpen) return;
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                // The click that opened the menu (same frame) is not an outside click.
                if (Time.frameCount != _openedFrame && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) && !MouseOver(_menuRect))
                    Close();
            }

            private void OnGUI()
            {
                if (!IsOpen && !_promptOpen) return;
                GUI.depth = HostGuiDepth;
                FiresRoundedSkin.Ensure(1f);
                EnsureRowStyles();
                if (IsOpen)
                {
                    GUI.Window(MenuWindowId, _menuRect, DrawMenu, "", FiresRoundedSkin.Window);
                    GUI.BringWindowToFront(MenuWindowId);
                }
                if (_promptOpen)
                {
                    GUI.Window(PromptWindowId, _promptRect, DrawPrompt, "", FiresRoundedSkin.Window);
                    GUI.BringWindowToFront(PromptWindowId);
                    GUI.FocusWindow(PromptWindowId);
                }
            }
        }

        // ── Text-entry prompt ─────────────────────────────────────────────
        // A small centred modal (themed to match the menu) that collects one
        // line of text. Enter / OK submit, Esc / Cancel / outside-click dismiss.
        // While open it holds the shared Core text-capture gate so typing the
        // name never leaks vanilla or other-mod hotkeys.
        private const string PromptFieldName = "FAP_TextPromptField";
        private static readonly object PromptInputToken = new object();
        private static bool _promptOpen;
        private static Rect _promptRect;
        private static string _promptTitle;
        private static string _promptText;
        private static Action<string> _promptOnSubmit;
        private static int _promptOpenedFrame = -1;
        private static bool _promptFocusPending;

        public static void ShowTextPrompt(string title, string initial, Action<string> onSubmit)
        {
            EnsureHost();
            Close();
            ClosePrompt();

            const int W = 380, H = 150;
            _promptRect = new Rect((Screen.width - W) * 0.5f, (Screen.height - H) * 0.5f, W, H);
            _promptTitle = title ?? string.Empty;
            _promptText = initial ?? string.Empty;
            _promptOnSubmit = onSubmit;
            _promptOpenedFrame = Time.frameCount;
            _promptFocusPending = true;
            _promptOpen = true;

            FiresCore.Input.FiresInputBlock.Acquire(PromptInputToken);
            UpdateBlocker();
        }

        public static void ClosePrompt()
        {
            if (!_promptOpen) return;
            FiresCore.Input.FiresInputBlock.Release(PromptInputToken);
            _promptOpen = false;
            _promptOnSubmit = null;
            if (GUIUtility.keyboardControl != 0) GUIUtility.keyboardControl = 0;
            UpdateBlocker();
        }

        private static void SubmitPrompt()
        {
            if (!_promptOpen) return;
            string val = (_promptText ?? string.Empty).Trim();
            var cb = _promptOnSubmit;
            ClosePrompt();
            try { cb?.Invoke(val); }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: VARightClickMenu text-prompt submit threw: {ex.Message}"); }
        }

        private static void PromptUpdate()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) { ClosePrompt(); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { SubmitPrompt(); return; }
            if (Time.frameCount != _promptOpenedFrame && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) && !MouseOver(_promptRect))
                ClosePrompt();
        }

        private static void DrawPrompt(int id)
        {
            // Enter / Escape as IMGUI key events too (the focused TextField would otherwise eat them before Update sees a frame).
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.Use(); SubmitPrompt(); return; }
                if (e.keyCode == KeyCode.Escape) { e.Use(); ClosePrompt(); return; }
            }

            float w = _promptRect.width;
            GUI.Label(new Rect(12f, 12f, w - 24f, 28f), _promptTitle, _promptTitleStyle);

            GUI.SetNextControlName(PromptFieldName);
            _promptText = GUI.TextField(new Rect(12f, 52f, w - 24f, 30f), _promptText ?? string.Empty, _promptFieldStyle);
            if (_promptFocusPending && e.type == EventType.Repaint)
            {
                _promptFocusPending = false;
                GUI.FocusControl(PromptFieldName);
            }

            float by = _promptRect.height - 14f - 28f;
            if (GUI.Button(new Rect(w * 0.5f - 62f - 54f, by, 108f, 28f), "OK", FiresRoundedSkin.Button)) { SubmitPrompt(); return; }
            if (GUI.Button(new Rect(w * 0.5f + 62f - 54f, by, 108f, 28f), "Cancel", FiresRoundedSkin.Button)) { ClosePrompt(); return; }
        }
    }
}
