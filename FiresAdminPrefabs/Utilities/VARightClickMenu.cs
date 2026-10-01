using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using FiresCore.Net;

namespace VerdantsAscent.Utilities
{
    // Lightweight UGUI popup for right-click context actions on hammer
    // pieces and tabs. Lives on its own top-level ScreenSpaceOverlay canvas
    // (DontDestroyOnLoad, sortingOrder = max int) so it never gets buried
    // by the hammer panel and survives world reload. Long lists wrap in a
    // ScrollRect; auto-dismisses on outside click / escape / right-click.
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

        private const int MenuWidth = 200;
        private const int ItemHeight = 28;
        private const int MenuPaddingY = 4;
        private const int MaxVisibleHeight = 420;
        private const int LabelInsetPx = 10;
        private const int LabelFontSize = 14;
        private const float ScrollSensitivityPx = 30f;
        private const int CanvasSortingOrder = 32767;

        private static GameObject _canvasRoot;
        private static Canvas _canvas;
        private static GameObject _activeMenu;
        private static MenuDriver _driver;

        public static bool IsOpen => _activeMenu != null;

        public static void Show(IList<Item> items)
        {
            Show(items, Input.mousePosition);
        }

        public static void Show(IList<Item> items, Vector2 screenPosition)
        {
            if (items == null || items.Count == 0) return;
            EnsureCanvas();
            Close();

            int totalContentHeight = items.Count * ItemHeight + MenuPaddingY * 2;
            bool needsScroll = totalContentHeight > MaxVisibleHeight;
            int visibleHeight = needsScroll ? MaxVisibleHeight : totalContentHeight;

            _activeMenu = new GameObject("FAP_RightClickMenu");
            _activeMenu.transform.SetParent(_canvas.transform, false);

            var panel = _activeMenu.AddComponent<Image>();
            panel.color = VerdantsAscent.Utilities.VAConfigTheme.PanelBg;
            panel.raycastTarget = true;
            VerdantsAscent.Utilities.VAConfigTheme.AddBorder(_activeMenu, VerdantsAscent.Utilities.VAConfigTheme.BorderColor, VerdantsAscent.Utilities.VAConfigTheme.BorderDist);

            var rt = panel.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(MenuWidth, visibleHeight);

            float x = Mathf.Clamp(screenPosition.x, 0f, Screen.width - MenuWidth);
            float y = Mathf.Clamp(screenPosition.y, visibleHeight, Screen.height);
            rt.anchoredPosition = new Vector2(x, y);

            GameObject contentGo;
            if (needsScroll)
            {
                var viewportGo = new GameObject("Viewport");
                viewportGo.transform.SetParent(_activeMenu.transform, false);
                var viewportImg = viewportGo.AddComponent<Image>();
                viewportImg.color = new Color(0f, 0f, 0f, 0.001f);
                viewportImg.raycastTarget = false;
                viewportGo.AddComponent<RectMask2D>();
                var viewportRt = viewportImg.rectTransform;
                viewportRt.anchorMin = Vector2.zero;
                viewportRt.anchorMax = Vector2.one;
                viewportRt.offsetMin = Vector2.zero;
                viewportRt.offsetMax = Vector2.zero;

                contentGo = new GameObject("Content");
                contentGo.transform.SetParent(viewportGo.transform, false);
                var contentImg = contentGo.AddComponent<Image>();
                contentImg.color = new Color(0f, 0f, 0f, 0.001f);
                contentImg.raycastTarget = false;
                var contentRt = contentImg.rectTransform;
                contentRt.anchorMin = new Vector2(0f, 1f);
                contentRt.anchorMax = new Vector2(1f, 1f);
                contentRt.pivot = new Vector2(0.5f, 1f);
                contentRt.anchoredPosition = Vector2.zero;
                contentRt.sizeDelta = new Vector2(0f, totalContentHeight);

                var sr = _activeMenu.AddComponent<ScrollRect>();
                sr.viewport = viewportRt;
                sr.content = contentRt;
                sr.horizontal = false;
                sr.vertical = true;
                sr.movementType = ScrollRect.MovementType.Clamped;
                sr.scrollSensitivity = ScrollSensitivityPx;
                sr.inertia = false;
            }
            else
            {
                contentGo = _activeMenu;
            }

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var itemGo = new GameObject($"Item_{i}_{item.Label}");
                itemGo.transform.SetParent(contentGo.transform, false);

                var bg = itemGo.AddComponent<Image>();
                bg.color = ItemSelector.NormalColor;
                bg.raycastTarget = !item.Disabled;

                var irt = bg.rectTransform;
                irt.anchorMin = new Vector2(0f, 1f);
                irt.anchorMax = new Vector2(1f, 1f);
                irt.pivot = new Vector2(0.5f, 1f);
                irt.anchoredPosition = new Vector2(0f, -(MenuPaddingY + i * ItemHeight));
                irt.sizeDelta = new Vector2(0f, ItemHeight);

                var textGo = new GameObject("Label");
                textGo.transform.SetParent(itemGo.transform, false);
                var txt = textGo.AddComponent<Text>();
                txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (txt.font == null) txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                txt.fontSize = LabelFontSize;
                txt.alignment = TextAnchor.MiddleLeft;
                txt.color = item.Disabled
                    ? new Color(0.55f, 0.55f, 0.55f, 1f)
                    : new Color(0.95f, 0.92f, 0.78f, 1f);
                txt.text = item.Label;
                txt.raycastTarget = false;
                txt.supportRichText = true;
                var trt = txt.rectTransform;
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(LabelInsetPx, 0f);
                trt.offsetMax = new Vector2(-LabelInsetPx, 0f);

                if (!item.Disabled)
                {
                    var selector = itemGo.AddComponent<ItemSelector>();
                    selector.Background = bg;
                    selector.Callback = item.OnSelect;
                }
            }

            _driver = _activeMenu.AddComponent<MenuDriver>();
        }

        public static void Close()
        {
            if (_activeMenu == null) return;
            NetworkObjectHelper.SafeDestroy(_activeMenu);
            _activeMenu = null;
            _driver = null;
        }

        private static void EnsureCanvas()
        {
            if (_canvasRoot != null) return;
            _canvasRoot = new GameObject("FAP_RightClickMenuCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_canvasRoot);

            _canvas = _canvasRoot.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CanvasSortingOrder;

            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 100f;

            _canvasRoot.AddComponent<GraphicRaycaster>();
        }

        private sealed class ItemSelector :
            MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            public static readonly Color NormalColor = new Color(0.10f, 0.10f, 0.12f, 0.0f);
            public static readonly Color HoverColor  = new Color(0.30f, 0.30f, 0.36f, 0.95f);

            public Image Background;
            public Action Callback;

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (Background != null) Background.color = HoverColor;
            }
            public void OnPointerExit(PointerEventData eventData)
            {
                if (Background != null) Background.color = NormalColor;
            }
            public void OnPointerClick(PointerEventData eventData)
            {
                var cb = Callback;
                Close();
                try { cb?.Invoke(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VARightClickMenu item callback threw: {ex.Message}");
                }
            }
        }

        private sealed class MenuDriver : MonoBehaviour
        {
            private void Update()
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }

                if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
                {
                    var rt = (RectTransform)transform;
                    Vector2 mouse = Input.mousePosition;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(rt, mouse, null))
                        Close();
                }
            }
        }

        // ── Text-entry prompt ─────────────────────────────────────────────
        // A small centred modal (themed to match the menu) that collects one
        // line of text. Enter / OK submit, Esc / Cancel / outside-click dismiss.
        // While open it holds the shared Core text-capture gate so typing the
        // name never leaks vanilla or other-mod hotkeys.
        private static GameObject _activePrompt;
        private static readonly object PromptInputToken = new object();

        public static void ShowTextPrompt(string title, string initial, Action<string> onSubmit)
        {
            EnsureCanvas();
            Close();
            ClosePrompt();

            const int W = 380, H = 150;

            _activePrompt = new GameObject("FAP_TextPrompt");
            _activePrompt.transform.SetParent(_canvas.transform, false);
            var panel = _activePrompt.AddComponent<Image>();
            panel.color = VAConfigTheme.PanelBg;
            panel.raycastTarget = true;
            VAConfigTheme.AddBorder(_activePrompt, VAConfigTheme.BorderColor, VAConfigTheme.BorderDist);
            var prt = panel.rectTransform;
            prt.anchorMin = new Vector2(0.5f, 0.5f);
            prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(W, H);
            prt.anchoredPosition = Vector2.zero;

            var titleTxt = MakeLabel(_activePrompt.transform, title ?? string.Empty, VAConfigTheme.SectionLabel, FontStyle.Bold);
            titleTxt.alignment = TextAnchor.MiddleCenter;
            var ttrt = titleTxt.rectTransform;
            ttrt.anchorMin = new Vector2(0f, 1f); ttrt.anchorMax = new Vector2(1f, 1f); ttrt.pivot = new Vector2(0.5f, 1f);
            ttrt.sizeDelta = new Vector2(-24f, 28f); ttrt.anchoredPosition = new Vector2(0f, -12f);

            var inputGo = new GameObject("Input");
            inputGo.transform.SetParent(_activePrompt.transform, false);
            var inputImg = inputGo.AddComponent<Image>();
            inputImg.color = VAConfigTheme.FieldBg;
            VAConfigTheme.AddBorder(inputGo, VAConfigTheme.EdgeColor, VAConfigTheme.EdgeDist);
            var inputRt = inputImg.rectTransform;
            inputRt.anchorMin = new Vector2(0f, 1f); inputRt.anchorMax = new Vector2(1f, 1f); inputRt.pivot = new Vector2(0.5f, 1f);
            inputRt.sizeDelta = new Vector2(-24f, 30f); inputRt.anchoredPosition = new Vector2(0f, -52f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(inputGo.transform, false);
            var fieldText = textGo.AddComponent<Text>();
            fieldText.font = GetFont();
            fieldText.fontSize = LabelFontSize;
            fieldText.color = VAConfigTheme.FieldInk;
            fieldText.alignment = TextAnchor.MiddleLeft;
            fieldText.supportRichText = false;
            var ftrt = fieldText.rectTransform;
            ftrt.anchorMin = Vector2.zero; ftrt.anchorMax = Vector2.one;
            ftrt.offsetMin = new Vector2(8f, 0f); ftrt.offsetMax = new Vector2(-8f, 0f);

            var field = inputGo.AddComponent<InputField>();
            field.textComponent = fieldText;
            field.targetGraphic = inputImg;
            field.lineType = InputField.LineType.SingleLine;
            field.caretColor = Color.white;
            field.selectionColor = new Color(0.50f, 0.40f, 0.25f, 0.5f);
            field.text = initial ?? string.Empty;

            Action submit = () =>
            {
                string val = (field.text ?? string.Empty).Trim();
                ClosePrompt();
                try { onSubmit?.Invoke(val); }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: VARightClickMenu text-prompt submit threw: {ex.Message}"); }
            };

            MakePromptButton(_activePrompt.transform, "OK", VAConfigTheme.ButtonPrimary, new Vector2(-62f, 14f), submit);
            MakePromptButton(_activePrompt.transform, "Cancel", VAConfigTheme.ButtonNeutral, new Vector2(62f, 14f), ClosePrompt);

            FiresCore.Input.FiresInputBlock.Acquire(PromptInputToken);

            var driver = _activePrompt.AddComponent<PromptDriver>();
            driver.OnSubmit = submit;
            driver.OnCancel = ClosePrompt;

            field.Select();
            field.ActivateInputField();
        }

        public static void ClosePrompt()
        {
            if (_activePrompt == null) return;
            FiresCore.Input.FiresInputBlock.Release(PromptInputToken);
            NetworkObjectHelper.SafeDestroy(_activePrompt);
            _activePrompt = null;
        }

        private static Font GetFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }

        private static Text MakeLabel(Transform parent, string text, Color color, FontStyle style)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = GetFont();
            t.fontSize = LabelFontSize;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleLeft;
            t.color = color;
            t.text = text;
            t.raycastTarget = false;
            return t;
        }

        private static void MakePromptButton(Transform parent, string label, Color color, Vector2 anchoredPos, Action onClick)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            VAConfigTheme.AddBorder(go, VAConfigTheme.EdgeColor, VAConfigTheme.EdgeDist);
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0f); rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(108f, 28f); rt.anchoredPosition = anchoredPos;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var txt = MakeLabel(go.transform, label, VAConfigTheme.ButtonInk, FontStyle.Normal);
            txt.alignment = TextAnchor.MiddleCenter;
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        }

        private sealed class PromptDriver : MonoBehaviour
        {
            public Action OnSubmit;
            public Action OnCancel;

            private void Update()
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { OnCancel?.Invoke(); return; }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { OnSubmit?.Invoke(); return; }

                if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
                {
                    var rt = (RectTransform)transform;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null))
                        OnCancel?.Invoke();
                }
            }
        }
    }
}
