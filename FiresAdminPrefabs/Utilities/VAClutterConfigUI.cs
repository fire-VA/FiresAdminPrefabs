using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Utilities
{
    // Gates vanilla movement + hotbar input while the clutter config form is open.
    [HarmonyPatch(typeof(Player), "TakeInput")]
    public static class Player_TakeInput_VAClutterConfigUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (VAClutterConfigUI.IsOpen) __result = false;
        }
    }

    // Form-style editor for clutter presets (28 fields across Identity / Placement /
    // Scale / Fractal / Forest / Shader). Same Save / Apply / Delete pattern as the
    // chest UI; Apply also marks the preset active and pushes it to ClutterSystem live.
    public class VAClutterConfigUI : MonoBehaviour
    {
        private static VAClutterConfigUI _instance;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("FAP_ClutterConfigUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VAClutterConfigUI>();
        }

        public static void Open(string clutterPrefabName)
        {
            EnsureCreated();
            _instance.OpenInternal(clutterPrefabName);
        }

        public static bool IsOpen => _instance != null && _instance._panelRoot != null;

        private void Update()
        {
            if (_panelRoot == null) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) CloseInternal();
        }

        private string _clutterPrefab;
        private string _selectedPresetName;

        private GameObject _canvasRoot;
        private Canvas _canvas;
        private GameObject _panelRoot;
        private GameObject _backdrop;

        // Identity
        private InputField _inputPresetName, _inputBiome;
        private Toggle _toggleEnabled;
        // Placement
        private InputField _inputAmount, _inputRandomOffset;
        private InputField _inputMinAlt, _inputMaxAlt, _inputMinOceanDepth, _inputMaxOceanDepth;
        private InputField _inputMinTilt, _inputMaxTilt, _inputMinVegetation, _inputMaxVegetation;
        private Toggle _toggleSnapToWater, _toggleOnUncleared, _toggleOnCleared, _toggleTerrainTilt, _toggleInstanced;
        // Scale
        private InputField _inputScaleMin, _inputScaleMax;
        // Fractal
        private InputField _inputFractalScale, _inputFractalOffset, _inputFractalThreshMin, _inputFractalThreshMax;
        // Forest
        private Toggle _toggleInForest;
        private InputField _inputForestThreshMin, _inputForestThreshMax;
        // Shader
        private InputField _inputSwaySpeed, _inputSwayDistance, _inputPushDistance;

        private Text _headerLabel, _statusLine;

        private void OpenInternal(string clutterPrefabName)
        {
            if (string.IsNullOrEmpty(clutterPrefabName)) return;
            _clutterPrefab = clutterPrefabName;

            EnsureCanvas();
            if (_panelRoot != null) Destroy(_panelRoot);
            if (_backdrop != null) Destroy(_backdrop);
            BuildForm();
            // Capture input (vanilla + other-mod hotkeys) for as long as the window is open.
            FiresCore.Input.FiresInputBlock.Acquire(this);

            string activeName = VAClutterPresetStore.GetActiveNameFor(_clutterPrefab);
            LoadPresetIntoForm(activeName);
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
            _canvasRoot = new GameObject("FAP_ClutterConfigCanvas");
            _canvasRoot.transform.SetParent(transform, false);
            _canvas = _canvasRoot.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32766;
            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 100f;
            _canvasRoot.AddComponent<GraphicRaycaster>();
        }

        // Layout constants — same palette as monster/chest UIs.
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
            // Modal backdrop hides the hammer panel showing through the gap around the form.
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

            _headerLabel = MakeText(header.transform, $"Configure clutter: {_clutterPrefab}", 16, TextAnchor.MiddleLeft);
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

            header.AddComponent<PanelDragHandler>().Panel = (RectTransform)_panelRoot.transform;

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
            PositionButton(MakeButton(bottomBar.transform, "Apply", ButtonBg, OnApplyClicked), 1, buttonW, buttonY, buttonGap, 4);
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

            // ── Build rows ───────────────────────────────────────────
            var layout = new RowLayout { Y = 0f };

            BuildPresetPickerRow(content.transform, layout);

            BuildSectionHeader(content.transform, layout, "Identity");
            _inputPresetName = BuildTextRow(content.transform, layout,
                "Preset name",
                "Saved as a named snapshot in clutter_presets.yaml. Pick a memorable label like 'LushMeadows' or 'SparseAshlands'.");
            _inputBiome = BuildTextRow(content.transform, layout,
                "Biome (CSV)",
                "Comma-separated list of vanilla biome names: Meadows, BlackForest, Plains, Swamp, " +
                "Mountains, Mistlands, Ashlands, AshLands, DeepNorth, Ocean. Empty = leave the " +
                "clutter's current biome mask untouched.");
            _toggleEnabled = BuildToggleRow(content.transform, layout,
                "Enabled",
                "Master on/off for this clutter entry. Off hides it everywhere.");

            BuildSectionHeader(content.transform, layout, "Placement");
            _inputAmount = BuildNumberRow(content.transform, layout,
                "Amount per patch",
                "How many instances per 8m grass patch (vanilla default 80). Higher = denser.");
            _inputRandomOffset = BuildNumberRow(content.transform, layout,
                "Random offset (m)",
                "Per-instance position jitter around the patch grid. 0 = grid-aligned.");
            _inputMinAlt = BuildNumberRow(content.transform, layout,
                "Min altitude",
                "Spawn only at world Y ≥ this value. Vanilla water level is 27.");
            _inputMaxAlt = BuildNumberRow(content.transform, layout,
                "Max altitude",
                "Spawn only at world Y ≤ this value. Use big values like 1000 for 'no cap'.");
            _inputMinOceanDepth = BuildNumberRow(content.transform, layout,
                "Min ocean depth",
                "Underwater clutter — meters below water level. 0 = ignore.");
            _inputMaxOceanDepth = BuildNumberRow(content.transform, layout,
                "Max ocean depth",
                "Underwater clutter upper limit.");
            _inputMinTilt = BuildNumberRow(content.transform, layout,
                "Min terrain tilt (deg)",
                "Skip instances where local terrain slope is below this angle.");
            _inputMaxTilt = BuildNumberRow(content.transform, layout,
                "Max terrain tilt (deg)",
                "Skip instances where local terrain slope is above this angle. Vanilla default 18.");
            _inputMinVegetation = BuildNumberRow(content.transform, layout,
                "Min vegetation density",
                "Skip patches where vegetation density is below this. 0 = no min filter.");
            _inputMaxVegetation = BuildNumberRow(content.transform, layout,
                "Max vegetation density",
                "Skip patches where vegetation density is above this. 0 = no max filter.");
            _toggleSnapToWater = BuildToggleRow(content.transform, layout,
                "Snap to water",
                "Snap instances to water surface (floating clutter).");
            _toggleOnUncleared = BuildToggleRow(content.transform, layout,
                "Spawn on uncleared terrain",
                "Spawn on terrain that hasn't been hoed/leveled. Vanilla default ON.");
            _toggleOnCleared = BuildToggleRow(content.transform, layout,
                "Spawn on cleared terrain",
                "Spawn on terrain that HAS been hoed/leveled. Vanilla default OFF.");
            _toggleTerrainTilt = BuildToggleRow(content.transform, layout,
                "Tilt with terrain",
                "Rotate instances to align with local terrain slope.");
            _toggleInstanced = BuildToggleRow(content.transform, layout,
                "Instanced rendering",
                "Use Unity's instanced renderer (more efficient at high counts). Most vanilla clutter uses this.");

            BuildSectionHeader(content.transform, layout, "Scale");
            _inputScaleMin = BuildNumberRow(content.transform, layout,
                "Scale min",
                "Lower bound of per-instance random scale.");
            _inputScaleMax = BuildNumberRow(content.transform, layout,
                "Scale max",
                "Upper bound of per-instance random scale. Set min=max for fixed size.");

            BuildSectionHeader(content.transform, layout, "Fractal placement");
            _inputFractalScale = BuildNumberRow(content.transform, layout,
                "Fractal scale",
                "Noise scale for placement filter. > 0 enables fractal placement; 0 disables.");
            _inputFractalOffset = BuildNumberRow(content.transform, layout,
                "Fractal offset",
                "Noise sample offset. Shift to get different placement patterns.");
            _inputFractalThreshMin = BuildNumberRow(content.transform, layout,
                "Fractal threshold min",
                "Skip instances where noise value is below this. Vanilla default 0.5.");
            _inputFractalThreshMax = BuildNumberRow(content.transform, layout,
                "Fractal threshold max",
                "Skip instances where noise value is above this. Vanilla default 1.0.");

            BuildSectionHeader(content.transform, layout, "Forest");
            _toggleInForest = BuildToggleRow(content.transform, layout,
                "Inside forest",
                "Only spawn where the forest fractal is 'inside forest' (between thresholds below).");
            _inputForestThreshMin = BuildNumberRow(content.transform, layout,
                "Forest threshold min",
                "Lower bound of the 'in forest' fractal range. 0 = no lower cutoff.");
            _inputForestThreshMax = BuildNumberRow(content.transform, layout,
                "Forest threshold max",
                "Upper bound of the 'in forest' fractal range. 1 = no upper cutoff.");

            BuildSectionHeader(content.transform, layout, "Shader (sway / push)");
            _inputSwaySpeed = BuildNumberRow(content.transform, layout,
                "Sway speed",
                "Material _SwaySpeed property. Higher = faster wind animation. Applied via " +
                "MaterialPropertyBlock — won't permanently mutate the shared material.");
            _inputSwayDistance = BuildNumberRow(content.transform, layout,
                "Sway distance",
                "Material _SwayDistance property. How far the tops of the grass bend.");
            _inputPushDistance = BuildNumberRow(content.transform, layout,
                "Push distance",
                "Material _PushDistance property. How far players/creatures push the grass aside.");

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

        private Toggle BuildToggleRow(Transform parent, RowLayout layout, string label, string hint)
        {
            float hintAvailableWidth = PanelWidth - HorizontalPadding * 2f - 14f;
            float hintHeight = Mathf.Max(
                FieldRowHintHeight,
                VAHintHeightEstimator.Estimate(hint, hintAvailableWidth, 11));
            float rowHeight = FieldRowHeight + hintHeight;

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

            var toggleGo = new GameObject("Toggle");
            toggleGo.transform.SetParent(row.transform, false);
            var toggleRt = toggleGo.AddComponent<RectTransform>();
            toggleRt.anchorMin = new Vector2(1f, 1f);
            toggleRt.anchorMax = new Vector2(1f, 1f);
            toggleRt.pivot = new Vector2(1f, 1f);
            toggleRt.anchoredPosition = new Vector2(-HorizontalPadding, -8f);
            toggleRt.sizeDelta = new Vector2(22f, 22f);
            var toggleBg = toggleGo.AddComponent<Image>();
            toggleBg.color = FieldBg;

            var checkmarkGo = new GameObject("Checkmark");
            checkmarkGo.transform.SetParent(toggleGo.transform, false);
            var checkmarkImg = checkmarkGo.AddComponent<Image>();
            checkmarkImg.color = new Color(0.4f, 0.85f, 0.55f, 1f);
            var checkmarkRt = checkmarkImg.rectTransform;
            checkmarkRt.anchorMin = Vector2.zero;
            checkmarkRt.anchorMax = Vector2.one;
            checkmarkRt.offsetMin = new Vector2(4f, 4f);
            checkmarkRt.offsetMax = new Vector2(-4f, -4f);

            var toggle = toggleGo.AddComponent<Toggle>();
            toggle.targetGraphic = toggleBg;
            toggle.graphic = checkmarkImg;

            var hintTxt = MakeText(row.transform, hint, 11, TextAnchor.UpperLeft);
            hintTxt.color = HintColor;
            var hintRt = hintTxt.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, hintHeight);
            hintTxt.horizontalOverflow = HorizontalWrapMode.Wrap;

            layout.Y += rowHeight + 4f;
            return toggle;
        }

        private InputField MakeFieldRowSkeleton(Transform parent, RowLayout layout, string label, string hint, out GameObject inputArea)
        {
            float hintAvailableWidth = PanelWidth - HorizontalPadding * 2f - 14f;
            float hintHeight = Mathf.Max(
                FieldRowHintHeight,
                VAHintHeightEstimator.Estimate(hint, hintAvailableWidth, 11));
            float rowHeight = FieldRowHeight + hintHeight + 4f;

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
                foreach (var kvp in VAClutterPresetStore.All)
                {
                    var preset = kvp.Value;
                    if (preset == null) continue;
                    if (!string.Equals(preset.clutterPrefab, _clutterPrefab, StringComparison.Ordinal)) continue;
                    matched++;
                    string captured = preset.name;
                    items.Add(new VARightClickMenu.Item("→ " + preset.name, () => LoadPresetIntoForm(captured)));
                }
                if (matched == 0)
                {
                    items.Add(new VARightClickMenu.Item("(no presets saved for this clutter)", null, disabled: true));
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

        // ── Form ↔ preset round-trip ─────────────────────────────────

        private void LoadPresetIntoForm(string presetName)
        {
            VAClutterPreset preset = null;
            if (!string.IsNullOrEmpty(presetName))
            {
                VAClutterPresetStore.TryGet(presetName, out preset);
            }

            if (preset == null)
            {
                // Blank form snapshots the live ClutterSystem values into a scratch preset so
                // untouched fields round-trip as vanilla truth, keeping Apply a true partial update
                // (hardcoded defaults would overwrite e.g. m_instanced and break grass rendering).
                var scratch = new VAClutterPreset { clutterPrefab = _clutterPrefab };
                bool snapped = VAClutterConfigApplier.SnapshotCurrentValuesTo(_clutterPrefab, scratch);
                _selectedPresetName = null;
                _inputPresetName.text = string.Empty;
                _inputBiome.text = scratch.biome ?? string.Empty;
                _toggleEnabled.isOn = scratch.enabled;
                _inputAmount.text = scratch.amount.ToString(CultureInfo.InvariantCulture);
                _inputRandomOffset.text = scratch.randomOffset.ToString(CultureInfo.InvariantCulture);
                _inputMinAlt.text = scratch.minAlt.ToString(CultureInfo.InvariantCulture);
                _inputMaxAlt.text = scratch.maxAlt.ToString(CultureInfo.InvariantCulture);
                _inputMinOceanDepth.text = scratch.minOceanDepth.ToString(CultureInfo.InvariantCulture);
                _inputMaxOceanDepth.text = scratch.maxOceanDepth.ToString(CultureInfo.InvariantCulture);
                _inputMinTilt.text = scratch.minTilt.ToString(CultureInfo.InvariantCulture);
                _inputMaxTilt.text = scratch.maxTilt.ToString(CultureInfo.InvariantCulture);
                _inputMinVegetation.text = scratch.minVegetation.ToString(CultureInfo.InvariantCulture);
                _inputMaxVegetation.text = scratch.maxVegetation.ToString(CultureInfo.InvariantCulture);
                _toggleSnapToWater.isOn = scratch.snapToWater;
                _toggleOnUncleared.isOn = scratch.onUncleared;
                _toggleOnCleared.isOn = scratch.onCleared;
                _toggleTerrainTilt.isOn = scratch.terrainTilt;
                _toggleInstanced.isOn = scratch.instanced;
                _inputScaleMin.text = scratch.scaleMin.ToString(CultureInfo.InvariantCulture);
                _inputScaleMax.text = scratch.scaleMax.ToString(CultureInfo.InvariantCulture);
                _inputFractalScale.text = scratch.fractalScale.ToString(CultureInfo.InvariantCulture);
                _inputFractalOffset.text = scratch.fractalOffset.ToString(CultureInfo.InvariantCulture);
                _inputFractalThreshMin.text = scratch.fractalTresholdMin.ToString(CultureInfo.InvariantCulture);
                _inputFractalThreshMax.text = scratch.fractalTresholdMax.ToString(CultureInfo.InvariantCulture);
                _toggleInForest.isOn = scratch.inForest;
                _inputForestThreshMin.text = scratch.forestTresholdMin.ToString(CultureInfo.InvariantCulture);
                _inputForestThreshMax.text = scratch.forestTresholdMax.ToString(CultureInfo.InvariantCulture);
                // Shader props default to vanilla grass values; snapshotting a property block is
                // not meaningful when the material may not expose the property.
                _inputSwaySpeed.text = "60";
                _inputSwayDistance.text = "2.3";
                _inputPushDistance.text = "2";

                if (snapped)
                {
                    Status("New preset — pre-filled with current vanilla values. Tweak only what you want, then Save / Apply.");
                }
                else
                {
                    Status("New preset — couldn't snapshot vanilla values (ClutterSystem not ready?). Defaults shown; verify before applying.", isError: true);
                }
                return;
            }

            _selectedPresetName = preset.name;
            _inputPresetName.text = preset.name ?? string.Empty;
            _inputBiome.text = preset.biome ?? string.Empty;
            _toggleEnabled.isOn = preset.enabled;
            _inputAmount.text = preset.amount.ToString(CultureInfo.InvariantCulture);
            _inputRandomOffset.text = preset.randomOffset.ToString(CultureInfo.InvariantCulture);
            _inputMinAlt.text = preset.minAlt.ToString(CultureInfo.InvariantCulture);
            _inputMaxAlt.text = preset.maxAlt.ToString(CultureInfo.InvariantCulture);
            _inputMinOceanDepth.text = preset.minOceanDepth.ToString(CultureInfo.InvariantCulture);
            _inputMaxOceanDepth.text = preset.maxOceanDepth.ToString(CultureInfo.InvariantCulture);
            _inputMinTilt.text = preset.minTilt.ToString(CultureInfo.InvariantCulture);
            _inputMaxTilt.text = preset.maxTilt.ToString(CultureInfo.InvariantCulture);
            _inputMinVegetation.text = preset.minVegetation.ToString(CultureInfo.InvariantCulture);
            _inputMaxVegetation.text = preset.maxVegetation.ToString(CultureInfo.InvariantCulture);
            _toggleSnapToWater.isOn = preset.snapToWater;
            _toggleOnUncleared.isOn = preset.onUncleared;
            _toggleOnCleared.isOn = preset.onCleared;
            _toggleTerrainTilt.isOn = preset.terrainTilt;
            _toggleInstanced.isOn = preset.instanced;
            _inputScaleMin.text = preset.scaleMin.ToString(CultureInfo.InvariantCulture);
            _inputScaleMax.text = preset.scaleMax.ToString(CultureInfo.InvariantCulture);
            _inputFractalScale.text = preset.fractalScale.ToString(CultureInfo.InvariantCulture);
            _inputFractalOffset.text = preset.fractalOffset.ToString(CultureInfo.InvariantCulture);
            _inputFractalThreshMin.text = preset.fractalTresholdMin.ToString(CultureInfo.InvariantCulture);
            _inputFractalThreshMax.text = preset.fractalTresholdMax.ToString(CultureInfo.InvariantCulture);
            _toggleInForest.isOn = preset.inForest;
            _inputForestThreshMin.text = preset.forestTresholdMin.ToString(CultureInfo.InvariantCulture);
            _inputForestThreshMax.text = preset.forestTresholdMax.ToString(CultureInfo.InvariantCulture);
            _inputSwaySpeed.text = preset.swaySpeed.ToString(CultureInfo.InvariantCulture);
            _inputSwayDistance.text = preset.swayDistance.ToString(CultureInfo.InvariantCulture);
            _inputPushDistance.text = preset.pushDistance.ToString(CultureInfo.InvariantCulture);
            Status($"Loaded preset '{preset.name}'.");
        }

        private VAClutterPreset BuildPresetFromForm()
        {
            var preset = new VAClutterPreset
            {
                name = (_inputPresetName.text ?? string.Empty).Trim(),
                clutterPrefab = _clutterPrefab,
                biome = (_inputBiome.text ?? string.Empty).Trim(),
                enabled = _toggleEnabled.isOn,
                amount = ParseInt(_inputAmount.text, 80),
                randomOffset = ParseFloat(_inputRandomOffset.text, 0f),
                minAlt = ParseFloat(_inputMinAlt.text, 27f),
                maxAlt = ParseFloat(_inputMaxAlt.text, 1000f),
                minOceanDepth = ParseFloat(_inputMinOceanDepth.text, 0f),
                maxOceanDepth = ParseFloat(_inputMaxOceanDepth.text, 0f),
                minTilt = ParseFloat(_inputMinTilt.text, 0f),
                maxTilt = ParseFloat(_inputMaxTilt.text, 18f),
                minVegetation = ParseFloat(_inputMinVegetation.text, 0f),
                maxVegetation = ParseFloat(_inputMaxVegetation.text, 0f),
                snapToWater = _toggleSnapToWater.isOn,
                onUncleared = _toggleOnUncleared.isOn,
                onCleared = _toggleOnCleared.isOn,
                terrainTilt = _toggleTerrainTilt.isOn,
                instanced = _toggleInstanced.isOn,
                scaleMin = ParseFloat(_inputScaleMin.text, 1f),
                scaleMax = ParseFloat(_inputScaleMax.text, 1f),
                fractalScale = ParseFloat(_inputFractalScale.text, 0f),
                fractalOffset = ParseFloat(_inputFractalOffset.text, 0f),
                fractalTresholdMin = ParseFloat(_inputFractalThreshMin.text, 0.5f),
                fractalTresholdMax = ParseFloat(_inputFractalThreshMax.text, 1f),
                inForest = _toggleInForest.isOn,
                forestTresholdMin = ParseFloat(_inputForestThreshMin.text, 0f),
                forestTresholdMax = ParseFloat(_inputForestThreshMax.text, 1f),
                swaySpeed = ParseFloat(_inputSwaySpeed.text, 60f),
                swayDistance = ParseFloat(_inputSwayDistance.text, 2.3f),
                pushDistance = ParseFloat(_inputPushDistance.text, 2f),
            };
            return string.IsNullOrEmpty(preset.name) ? null : preset;
        }

        // ── Button actions ───────────────────────────────────────────

        private void OnSaveClicked()
        {
            var preset = BuildPresetFromForm();
            if (preset == null)
            {
                Status("Preset name is required.", isError: true);
                return;
            }
            if (!VAClutterPresetStore.Save(preset))
            {
                Status("Save failed — see log.", isError: true);
                return;
            }
            _selectedPresetName = preset.name;
            Status($"Saved preset '{preset.name}' (not yet applied — click Apply to push live).");
        }

        private void OnApplyClicked()
        {
            var preset = BuildPresetFromForm();
            if (preset == null)
            {
                Status("Preset name is required to apply.", isError: true);
                return;
            }
            if (!VAClutterPresetStore.Save(preset))
            {
                Status("Save failed — apply aborted.", isError: true);
                return;
            }
            _selectedPresetName = preset.name;
            VAClutterPresetStore.SetActive(_clutterPrefab, preset.name);

            // Applier runs the ClearAll → regen cycle so visible clutter updates within a tick.
            bool ok = VAClutterConfigApplier.Apply(preset);
            if (ok)
            {
                Status($"Applied '{preset.name}' to {_clutterPrefab}. Visible clutter will refresh shortly.");
            }
            else
            {
                Status($"Saved '{preset.name}' but live apply failed — see log. " +
                       "Will retry on next world load.", isError: true);
            }
            CloseInternal();
        }

        private void OnDeleteClicked()
        {
            if (string.IsNullOrEmpty(_selectedPresetName))
            {
                Status("Nothing to delete — preset is unsaved.", isError: true);
                return;
            }
            if (VAClutterPresetStore.Delete(_selectedPresetName))
            {
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

        // ── UI helpers (duplicated from chest/monster UIs) ───────────

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
            private Vector2 _pointerStart, _panelStart;
            public void OnBeginDrag(PointerEventData eventData)
            {
                if (Panel == null) return;
                _pointerStart = eventData.position;
                _panelStart = Panel.anchoredPosition;
            }
            public void OnDrag(PointerEventData eventData)
            {
                if (Panel == null) return;
                Panel.anchoredPosition = _panelStart + (eventData.position - _pointerStart);
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
