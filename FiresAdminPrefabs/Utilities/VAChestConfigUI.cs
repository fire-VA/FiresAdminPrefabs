using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Utilities
{
    // Gates vanilla keyboard input while the chest config UI is open.
    [HarmonyPatch(typeof(Player), "TakeInput")]
    public static class Player_TakeInput_VAChestConfigUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (VAChestConfigUI.IsOpen) __result = false;
        }
    }

    // Form-style editor for chest presets (loot table, pick-N amount, respawn timer range,
    // prevent-stacking radius). Blank on open to avoid form-state leakage; Save writes the data
    // yaml, Apply also queues for next placement and arms the hammer, Delete removes the entry.
    public class VAChestConfigUI : MonoBehaviour
    {
        private static VAChestConfigUI _instance;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("FAP_ChestConfigUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VAChestConfigUI>();
        }

        public static void Open(string containerPrefabName)
        {
            EnsureCreated();
            _instance.OpenInternal(containerPrefabName);
        }

        public static bool IsOpen => _instance != null && _instance._panelRoot != null;

        private void Update()
        {
            if (_panelRoot == null) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) CloseInternal();
        }

        private string _containerPrefab;
        private string _selectedPresetName;

        private GameObject _canvasRoot;
        private Canvas _canvas;
        private GameObject _panelRoot;
        private GameObject _backdrop;                   // full-screen dim layer behind the panel

        private InputField _inputPresetName;
        private InputField _inputLootTable;     // multiline
        private InputField _inputItemAmount;    // "" or "3" or "2;4"
        private InputField _inputRespawnMin;
        private InputField _inputRespawnMax;
        private InputField _inputStackingRadius;
        private Text       _headerLabel;
        private Text       _statusLine;

        private void OpenInternal(string containerPrefabName)
        {
            if (string.IsNullOrEmpty(containerPrefabName)) return;
            _containerPrefab = containerPrefabName;

            EnsureCanvas();
            if (_panelRoot != null) Destroy(_panelRoot);
            BuildForm();
            // Capture input (vanilla + other-mod hotkeys) for as long as the window is open.
            FiresCore.Input.FiresInputBlock.Acquire(this);

            LoadPresetIntoForm(null);
        }

        private void CloseInternal()
        {
            FiresCore.Input.FiresInputBlock.Release(this);
            if (_panelRoot != null) Destroy(_panelRoot);
            if (_backdrop != null) Destroy(_backdrop);
            _panelRoot = null;
            _backdrop = null;
        }

        private void EnsureCanvas()
        {
            if (_canvasRoot != null) return;
            _canvasRoot = new GameObject("FAP_ChestConfigCanvas");
            _canvasRoot.transform.SetParent(transform, false);
            _canvas = _canvasRoot.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32766;
            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 100f;
            _canvasRoot.AddComponent<GraphicRaycaster>();
        }

        // Layout constants — same palette as the monster UI for visual
        // consistency.
        private const float PanelWidth = 540f;
        private const float PanelHeight = 720f;
        private const float SectionHeaderHeight = 26f;
        private const float FieldRowHeight = 36f;
        private const float FieldRowHintHeight = 18f;
        private const float HorizontalPadding = 14f;

        // Warm-brown / parchment theme — shared VAConfigTheme (matches RPG Maker + tooltips).
        private static readonly Color PanelBg = VAConfigTheme.PanelBg;
        private static readonly Color SectionBg = VAConfigTheme.SectionBg;
        private static readonly Color FieldBg = VAConfigTheme.FieldBg;
        private static readonly Color LabelColor = VAConfigTheme.Label;
        private static readonly Color HintColor = VAConfigTheme.Hint;
        private static readonly Color ButtonBg = VAConfigTheme.ButtonPrimary;
        private static readonly Color ButtonDangerBg = VAConfigTheme.ButtonDanger;
        private static readonly Color ButtonNeutralBg = VAConfigTheme.ButtonNeutral;
        private static readonly Color BackdropColor = VAConfigTheme.Backdrop;
        private static readonly Color ChromeBarColor = VAConfigTheme.BarBg;
        private static readonly Color SectionTitleColor = VAConfigTheme.SectionLabel;
        private static readonly Color ButtonTextColor = VAConfigTheme.ButtonInk;
        private static readonly Color StatusErrorColor = new Color(0.95f, 0.5f, 0.5f, 1f);

        private void BuildForm()
        {
            // Modal backdrop hides the hammer build menu showing through the gap around the panel.
            _backdrop = new GameObject("Backdrop");
            _backdrop.transform.SetParent(_canvasRoot.transform, false);
            var bdImg = _backdrop.AddComponent<Image>();
            bdImg.color = BackdropColor;
            bdImg.raycastTarget = true;
            var bdRt = bdImg.rectTransform;
            bdRt.anchorMin = Vector2.zero;
            bdRt.anchorMax = Vector2.one;
            bdRt.offsetMin = Vector2.zero;
            bdRt.offsetMax = Vector2.zero;

            _panelRoot = new GameObject("Panel");
            _panelRoot.transform.SetParent(_canvasRoot.transform, false);
            var panelImg = _panelRoot.AddComponent<Image>();
            panelImg.color = PanelBg;
            VAConfigTheme.AddBorder(_panelRoot, VAConfigTheme.BorderColor, VAConfigTheme.BorderDist);
            var panelRt = panelImg.rectTransform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            panelRt.anchoredPosition = Vector2.zero;

            // Header
            var header = new GameObject("Header");
            header.transform.SetParent(_panelRoot.transform, false);
            var headerImg = header.AddComponent<Image>();
            headerImg.color = ChromeBarColor;
            var headerRt = headerImg.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(0f, 40f);

            _headerLabel = MakeText(header.transform, $"Configure container: {_containerPrefab}", 16, TextAnchor.MiddleLeft);
            var labelRt = _headerLabel.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(HorizontalPadding, 0f);
            labelRt.offsetMax = new Vector2(-50f, 0f);

            var closeBtn = MakeButton(header.transform, "×", new Color(0.9f, 0.5f, 0.5f, 1f), CloseInternal);
            var closeRt = (RectTransform)closeBtn.transform;
            closeRt.anchorMin = new Vector2(1f, 0.5f);
            closeRt.anchorMax = new Vector2(1f, 0.5f);
            closeRt.pivot = new Vector2(1f, 0.5f);
            closeRt.anchoredPosition = new Vector2(-6f, 0f);
            closeRt.sizeDelta = new Vector2(34f, 28f);

            var headerDrag = header.AddComponent<PanelDragHandler>();
            headerDrag.Panel = (RectTransform)_panelRoot.transform;

            // Bottom bar
            var bottomBar = new GameObject("BottomBar");
            bottomBar.transform.SetParent(_panelRoot.transform, false);
            var bottomImg = bottomBar.AddComponent<Image>();
            bottomImg.color = ChromeBarColor;
            var bottomRt = bottomImg.rectTransform;
            bottomRt.anchorMin = new Vector2(0f, 0f);
            bottomRt.anchorMax = new Vector2(1f, 0f);
            bottomRt.pivot = new Vector2(0.5f, 0f);
            bottomRt.sizeDelta = new Vector2(0f, 70f);

            _statusLine = MakeText(bottomBar.transform, "", 12, TextAnchor.MiddleLeft);
            _statusLine.color = HintColor;
            var statusRt = _statusLine.rectTransform;
            statusRt.anchorMin = new Vector2(0f, 1f);
            statusRt.anchorMax = new Vector2(1f, 1f);
            statusRt.pivot = new Vector2(0.5f, 1f);
            statusRt.anchoredPosition = new Vector2(0f, -4f);
            statusRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, 14f);

            float buttonY = 12f, buttonW = 100f, buttonGap = 6f;
            PositionButton(MakeButton(bottomBar.transform, "Save", ButtonBg, OnSaveClicked), 0, buttonW, buttonY, buttonGap, 4);
            PositionButton(MakeButton(bottomBar.transform, "Apply (queue)", ButtonBg, OnApplyClicked), 1, buttonW, buttonY, buttonGap, 4);
            PositionButton(MakeButton(bottomBar.transform, "Delete", ButtonDangerBg, OnDeleteClicked), 2, buttonW, buttonY, buttonGap, 4);
            PositionButton(MakeButton(bottomBar.transform, "Close", ButtonNeutralBg, CloseInternal), 3, buttonW, buttonY, buttonGap, 4);

            // Body scroll
            var bodyContainer = new GameObject("BodyScroll");
            bodyContainer.transform.SetParent(_panelRoot.transform, false);
            var bodyImg = bodyContainer.AddComponent<Image>();
            bodyImg.color = new Color(0.05f, 0.05f, 0.08f, 1f);
            bodyImg.raycastTarget = false;
            var bodyRt = bodyImg.rectTransform;
            bodyRt.anchorMin = new Vector2(0f, 0f);
            bodyRt.anchorMax = new Vector2(1f, 1f);
            bodyRt.offsetMin = new Vector2(0f, 70f);
            bodyRt.offsetMax = new Vector2(0f, -40f);

            var sr = bodyContainer.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 300f;

            const float ScrollbarWidth = 14f;
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(bodyContainer.transform, false);
            var vImg = viewport.AddComponent<Image>();
            vImg.color = new Color(0f, 0f, 0f, 0.001f);
            vImg.raycastTarget = true;
            viewport.AddComponent<RectMask2D>();
            var vRt = vImg.rectTransform;
            vRt.anchorMin = Vector2.zero;
            vRt.anchorMax = Vector2.one;
            vRt.offsetMin = Vector2.zero;
            vRt.offsetMax = new Vector2(-ScrollbarWidth, 0f);
            sr.viewport = vRt;

            // EXACT copy of VAMonsterConfigUI's scrollbar setup.
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var cImg = content.AddComponent<Image>();
            cImg.color = new Color(0f, 0f, 0f, 0.001f);
            cImg.raycastTarget = false;
            var cRt = cImg.rectTransform;
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.anchoredPosition = Vector2.zero;
            sr.content = cRt;

            var scrollbarGo = new GameObject("VerticalScrollbar");
            scrollbarGo.transform.SetParent(bodyContainer.transform, false);
            var sbBg = scrollbarGo.AddComponent<Image>();
            sbBg.color = new Color(0f, 0f, 0f, 0f);
            sbBg.raycastTarget = true;
            var sbRt = sbBg.rectTransform;
            sbRt.anchorMin = new Vector2(1f, 0f);
            sbRt.anchorMax = new Vector2(1f, 1f);
            sbRt.pivot = new Vector2(1f, 0.5f);
            sbRt.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            sbRt.anchoredPosition = Vector2.zero;

            var slidingAreaGo = new GameObject("Sliding Area");
            slidingAreaGo.transform.SetParent(scrollbarGo.transform, false);
            var saRt = slidingAreaGo.AddComponent<RectTransform>();
            saRt.anchorMin = Vector2.zero;
            saRt.anchorMax = Vector2.one;
            saRt.offsetMin = new Vector2(2f, 2f);
            saRt.offsetMax = new Vector2(-2f, -2f);

            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(slidingAreaGo.transform, false);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = new Color(0.55f, 0.55f, 0.62f, 0.95f);
            var handleRt = handleImg.rectTransform;
            handleRt.anchorMin = Vector2.zero;
            handleRt.anchorMax = Vector2.one;
            handleRt.offsetMin = Vector2.zero;
            handleRt.offsetMax = Vector2.zero;

            var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.targetGraphic = handleImg;
            scrollbar.handleRect = handleRt;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            sr.verticalScrollbar = scrollbar;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var layout = new RowLayout { Y = 0f };

            BuildPresetPickerRow(content.transform, layout);

            BuildSectionHeader(content.transform, layout, "Identity");
            _inputPresetName = BuildTextRow(content.transform, layout,
                "Preset name",
                "Used as the ZDO int marker stamped on each placed chest AND as " +
                "the EWP rule's filter key. Keep unique — naming a preset 'piece_chest' " +
                "would collide with every vanilla chest in the world.");

            BuildSectionHeader(content.transform, layout, "Loot");
            _inputLootTable = BuildMultilineTextRow(content.transform, layout,
                "Loot table (one item per line)",
                VALootTextFormat.HintText,
                lines: 8);
            _inputItemAmount = BuildTextRow(content.transform, layout,
                "Pick N from pool (optional)",
                "Empty = each item rolls independently against its own chance. " +
                "Single number ('3') = always pick exactly 3 from the pool. Range " +
                "('2;4') = pick a random count in that window per respawn. Higher " +
                "chance values bias selection toward those items.");

            BuildSectionHeader(content.transform, layout, "Respawn");
            _inputRespawnMin = BuildNumberRow(content.transform, layout,
                "Respawn delay — min (seconds)",
                "Time between the chest being emptied and respawning. Required for " +
                "the respawn loop to be active at all (a preset with delay 0 is a " +
                "one-shot loot template — no destroy/respawn rule emitted).");
            _inputRespawnMax = BuildNumberRow(content.transform, layout,
                "Respawn delay — max (seconds, optional)",
                "When set AND greater than min, EWP randomizes each respawn within " +
                "[min, max]. Leave empty (or equal to min) for a fixed delay. Useful " +
                "for staggering identical chests so players can't camp the timer.");
            _inputStackingRadius = BuildNumberRow(content.transform, layout,
                "Prevent stacking — radius (m)",
                "When > 0, respawn is SKIPPED if another chest of the SAME container " +
                "prefab already exists within this radius. Stops respawn loops from " +
                "creating chest-on-chest stacks when players place dupes. Set to 0 to " +
                "disable the check.");

            cRt.sizeDelta = new Vector2(0f, layout.Y);

            // Flush pending layout so the scrollbar handle is sized
            // from real content height THIS frame, not size=1.0 default.
            Canvas.ForceUpdateCanvases();

            // Four corner scale grips — drag any corner to scale the whole window
            // (box + fonts + section spacing) uniformly so the text grows with it.
            VAWindowScaleGrip.AddScaleGrips((RectTransform)_panelRoot.transform, VAConfigTheme.EdgeColor);
        }

        private class RowLayout { public float Y; }

        private void BuildSectionHeader(Transform parent, RowLayout layout, string label)
        {
            var header = new GameObject($"Section_{label}");
            header.transform.SetParent(parent, false);
            var img = header.AddComponent<Image>();
            img.color = SectionBg;
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(0f, SectionHeaderHeight);

            var txt = MakeText(header.transform, label, 14, TextAnchor.MiddleLeft);
            txt.color = SectionTitleColor;
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(HorizontalPadding, 0f);
            trt.offsetMax = new Vector2(-HorizontalPadding, 0f);

            layout.Y += SectionHeaderHeight + 6f;
        }

        private InputField BuildTextRow(Transform parent, RowLayout layout, string label, string hint)
        {
            var input = MakeFieldRowSkeleton(parent, layout, label, hint, out _);
            input.lineType = InputField.LineType.SingleLine;
            return input;
        }

        private InputField BuildNumberRow(Transform parent, RowLayout layout, string label, string hint)
        {
            var input = MakeFieldRowSkeleton(parent, layout, label, hint, out _);
            input.lineType = InputField.LineType.SingleLine;
            input.contentType = InputField.ContentType.DecimalNumber;
            return input;
        }

        private InputField BuildMultilineTextRow(Transform parent, RowLayout layout, string label, string hint, int lines)
        {
            float extraHeight = (lines - 1) * 18f;
            var input = MakeFieldRowSkeleton(parent, layout, label, hint, out var inputArea, extraInputHeight: extraHeight);
            input.lineType = InputField.LineType.MultiLineNewline;
            ((RectTransform)inputArea.transform).sizeDelta += new Vector2(0f, extraHeight);
            input.textComponent.alignment = TextAnchor.UpperLeft;
            return input;
        }

        private InputField MakeFieldRowSkeleton(Transform parent, RowLayout layout, string label, string hint, out GameObject inputArea, float extraInputHeight = 0f)
        {
            // Wrap-aware hint height: estimator counts both explicit newlines and visual-wrap
            // lines at the available width, so long single-line hints don't overflow the next row.
            float hintAvailableWidth = PanelWidth - HorizontalPadding * 2f - 14f; // 14 = scrollbar width
            float hintHeight = Mathf.Max(
                FieldRowHintHeight,
                VAHintHeightEstimator.Estimate(hint, hintAvailableWidth, 11));
            float rowHeight = FieldRowHeight + hintHeight + extraInputHeight + 4f;

            var row = new GameObject($"Row_{label}");
            row.transform.SetParent(parent, false);
            var rt = row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(0f, rowHeight);

            var lbl = MakeText(row.transform, label, 13, TextAnchor.MiddleLeft);
            lbl.color = LabelColor;
            var lblRt = lbl.rectTransform;
            lblRt.anchorMin = new Vector2(0f, 1f);
            lblRt.anchorMax = new Vector2(0f, 1f);
            lblRt.pivot = new Vector2(0f, 1f);
            lblRt.anchoredPosition = new Vector2(HorizontalPadding, 0f);
            lblRt.sizeDelta = new Vector2(280f, FieldRowHeight);

            inputArea = new GameObject("Input");
            inputArea.transform.SetParent(row.transform, false);
            var inputBg = inputArea.AddComponent<Image>();
            inputBg.color = FieldBg;
            var inputRt = inputBg.rectTransform;
            inputRt.anchorMin = new Vector2(1f, 1f);
            inputRt.anchorMax = new Vector2(1f, 1f);
            inputRt.pivot = new Vector2(1f, 1f);
            inputRt.anchoredPosition = new Vector2(-HorizontalPadding, -4f);
            inputRt.sizeDelta = new Vector2(220f, 26f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(inputArea.transform, false);
            var txt = textGo.AddComponent<Text>();
            txt.font = GetFont();
            txt.fontSize = 13;
            txt.color = new Color(0.95f, 0.95f, 0.95f, 1f);
            txt.alignment = TextAnchor.MiddleLeft;
            txt.supportRichText = false;
            var txtRt = txt.rectTransform;
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(6f, 2f);
            txtRt.offsetMax = new Vector2(-6f, -2f);

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(inputArea.transform, false);
            var ph = phGo.AddComponent<Text>();
            ph.font = GetFont();
            ph.fontSize = 13;
            ph.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            ph.alignment = TextAnchor.MiddleLeft;
            var phRt = ph.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(6f, 2f);
            phRt.offsetMax = new Vector2(-6f, -2f);

            var input = inputArea.AddComponent<InputField>();
            input.targetGraphic = inputBg;
            input.textComponent = txt;
            input.placeholder = ph;

            var hintTxt = MakeText(row.transform, hint, 11, TextAnchor.UpperLeft);
            hintTxt.color = HintColor;
            var hintRt = hintTxt.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, hintHeight);
            hintTxt.horizontalOverflow = HorizontalWrapMode.Wrap;

            layout.Y += rowHeight + 4f;
            return input;
        }

        private void BuildPresetPickerRow(Transform parent, RowLayout layout)
        {
            float rowHeight = FieldRowHeight;
            var row = new GameObject("PresetPicker");
            row.transform.SetParent(parent, false);
            var rt = row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(0f, rowHeight);

            var lbl = MakeText(row.transform, "Preset (existing):", 13, TextAnchor.MiddleLeft);
            lbl.color = LabelColor;
            var lblRt = lbl.rectTransform;
            lblRt.anchorMin = new Vector2(0f, 0.5f);
            lblRt.anchorMax = new Vector2(0f, 0.5f);
            lblRt.pivot = new Vector2(0f, 0.5f);
            lblRt.anchoredPosition = new Vector2(HorizontalPadding, 0f);
            lblRt.sizeDelta = new Vector2(160f, FieldRowHeight);

            var pickBtn = MakeButton(row.transform, "Pick existing...", ButtonNeutralBg, () =>
            {
                var items = new List<VARightClickMenu.Item>();
                int matched = 0;
                foreach (var kvp in VAChestPresetStore.All)
                {
                    var preset = kvp.Value;
                    if (preset == null) continue;
                    if (!string.Equals(preset.ContainerPrefab, _containerPrefab, StringComparison.Ordinal)) continue;
                    matched++;
                    string captured = preset.name;
                    items.Add(new VARightClickMenu.Item("→ " + preset.name, () => LoadPresetIntoForm(captured)));
                }
                if (matched == 0)
                {
                    items.Add(new VARightClickMenu.Item("(no presets saved for this chest)", null, disabled: true));
                }
                VARightClickMenu.Show(items);
            });
            var pickRt = (RectTransform)pickBtn.transform;
            pickRt.anchorMin = new Vector2(1f, 0.5f);
            pickRt.anchorMax = new Vector2(1f, 0.5f);
            pickRt.pivot = new Vector2(1f, 0.5f);
            pickRt.anchoredPosition = new Vector2(-HorizontalPadding - 110f, 0f);
            pickRt.sizeDelta = new Vector2(150f, 26f);

            var newBtn = MakeButton(row.transform, "New", ButtonBg, () => LoadPresetIntoForm(null));
            var newRt = (RectTransform)newBtn.transform;
            newRt.anchorMin = new Vector2(1f, 0.5f);
            newRt.anchorMax = new Vector2(1f, 0.5f);
            newRt.pivot = new Vector2(1f, 0.5f);
            newRt.anchoredPosition = new Vector2(-HorizontalPadding, 0f);
            newRt.sizeDelta = new Vector2(100f, 26f);

            layout.Y += rowHeight + 6f;
        }

        // ── Form ↔ Preset round-trip ─────────────────────────────────

        private void LoadPresetIntoForm(string presetName)
        {
            VAChestPreset preset = null;
            if (!string.IsNullOrEmpty(presetName))
            {
                VAChestPresetStore.TryGet(presetName, out preset);
            }

            if (preset == null)
            {
                _selectedPresetName = null;
                _inputPresetName.text = string.Empty;
                _inputLootTable.text = string.Empty;
                _inputItemAmount.text = string.Empty;
                _inputRespawnMin.text = "60";
                _inputRespawnMax.text = string.Empty;
                _inputStackingRadius.text = "0";
                Status("New preset — fill in the form and click Save, or click 'Pick existing...' to edit one.");
                return;
            }

            _selectedPresetName = preset.name;
            _inputPresetName.text = preset.name ?? string.Empty;
            _inputLootTable.text = VALootTextFormat.Serialize(preset.items);
            _inputItemAmount.text = preset.itemAmount ?? string.Empty;
            _inputRespawnMin.text = preset.TryGetFloatStr("va_respawn_seconds") ?? "60";
            _inputRespawnMax.text = preset.TryGetFloatStr("va_respawn_seconds_max") ?? string.Empty;
            _inputStackingRadius.text = preset.TryGetFloatStr("va_prevent_stacking_radius") ?? "0";
            Status($"Loaded preset '{preset.name}'.");
        }

        private string _formProblem;

        private VAChestPreset BuildPresetFromForm()
        {
            _formProblem = null;
            var preset = new VAChestPreset
            {
                name = (_inputPresetName.text ?? string.Empty).Trim(),
            };
            preset.ContainerPrefab = _containerPrefab;
            if (string.IsNullOrEmpty(preset.name)) return null;

            preset.items = VALootTextFormat.Parse(_inputLootTable.text);
            var itemAmountTxt = (_inputItemAmount.text ?? string.Empty).Trim();
            preset.itemAmount = string.IsNullOrEmpty(itemAmountTxt) ? null : itemAmountTxt;

            if (!VAChestSlotPlan.TryPlan(preset, out string slotProblem))
            {
                _formProblem = slotProblem;
                return null;
            }

            float respawnMin = ParseFloat(_inputRespawnMin.text, 60f);
            if (respawnMin > 0f)
                preset.floats.Add($"va_respawn_seconds, {respawnMin.ToString("F2", CultureInfo.InvariantCulture)}");

            float respawnMax = ParseFloat(_inputRespawnMax.text, 0f);
            if (respawnMax > respawnMin)
                preset.floats.Add($"va_respawn_seconds_max, {respawnMax.ToString("F2", CultureInfo.InvariantCulture)}");

            float stackingRadius = ParseFloat(_inputStackingRadius.text, 0f);
            if (stackingRadius > 0f)
                preset.floats.Add($"va_prevent_stacking_radius, {stackingRadius.ToString("F1", CultureInfo.InvariantCulture)}");

            return preset;
        }

        // ── Button actions ───────────────────────────────────────────

        private void OnSaveClicked()
        {
            var preset = BuildPresetFromForm();
            if (preset == null)
            {
                Status(_formProblem ?? "Preset name is required.", isError: true);
                return;
            }
            if (!VAChestPresetStore.Save(preset))
            {
                Status("Save failed — see log.", isError: true);
                return;
            }
            _selectedPresetName = preset.name;
            Status($"Saved preset '{preset.name}'.");

            CloseInternal();
            VAHammerArmHelper.ArmAndClose(_containerPrefab);
        }

        private void OnApplyClicked()
        {
            var preset = BuildPresetFromForm();
            if (preset == null)
            {
                Status(_formProblem ?? "Preset name is required to apply.", isError: true);
                return;
            }
            if (!VAChestPresetStore.Save(preset))
            {
                Status("Save failed — apply aborted.", isError: true);
                return;
            }
            _selectedPresetName = preset.name;
            VAChestConfigQueue.EnqueueForNextPlacement(_containerPrefab, preset.name);
            Status($"Saved + queued '{preset.name}' for next {_containerPrefab} placement.");

            CloseInternal();
            VAHammerArmHelper.ArmAndClose(_containerPrefab);
        }

        private void OnDeleteClicked()
        {
            if (string.IsNullOrEmpty(_selectedPresetName))
            {
                Status("Nothing to delete — preset is unsaved.", isError: true);
                return;
            }
            if (VAChestPresetStore.Delete(_selectedPresetName))
            {
                // No rule regen needed — the generic rule is preset-agnostic. Orphan chests
                // carrying the deleted name simply fail their next-respawn lookup, which is correct.
                Status($"Deleted preset '{_selectedPresetName}'.");
                LoadPresetIntoForm(null);
            }
            else
            {
                Status("Delete failed — see log.", isError: true);
            }
        }

        private void Status(string msg, bool isError = false)
        {
            if (_statusLine == null) return;
            _statusLine.text = msg;
            _statusLine.color = isError ? StatusErrorColor : HintColor;
        }

        // ── UI helpers (duplicated from VAMonsterConfigUI) ───────────

        private static Font GetFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static Text MakeText(Transform parent, string content, int fontSize, TextAnchor anchor)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = GetFont();
            t.fontSize = fontSize;
            t.alignment = anchor;
            t.color = LabelColor;
            t.text = content;
            t.raycastTarget = false;
            t.supportRichText = true;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private static GameObject MakeButton(Transform parent, string label, Color bg, Action onClick)
        {
            var go = new GameObject($"Button_{label}");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = bg;
            var txt = MakeText(go.transform, label, 13, TextAnchor.MiddleCenter);
            txt.color = ButtonTextColor;
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return go;
        }

        private static void PositionButton(GameObject btn, int index, float buttonWidth, float yFromBottom, float gap, int totalButtons)
        {
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            float xFromRight = -HorizontalPadding - (totalButtons - 1 - index) * (buttonWidth + gap);
            rt.anchoredPosition = new Vector2(xFromRight, yFromBottom);
            rt.sizeDelta = new Vector2(buttonWidth, 30f);
        }

        private sealed class PanelDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
        {
            public RectTransform Panel;
            private Vector2 _pointerStart;
            private Vector2 _panelStart;
            public void OnBeginDrag(PointerEventData eventData)
            {
                if (Panel == null) return;
                _pointerStart = eventData.position;
                _panelStart = Panel.anchoredPosition;
            }
            public void OnDrag(PointerEventData eventData)
            {
                if (Panel == null) return;
                Vector2 delta = eventData.position - _pointerStart;
                Panel.anchoredPosition = _panelStart + delta;
            }
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
