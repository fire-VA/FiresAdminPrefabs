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
    // Player.TakeInput postfix — when our config UI is open, force the
    // result to false so vanilla input handlers (movement, build
    // hotkeys, hotbar) ignore the keyboard + mouse, matching the gate
    // Chat/Console/InventoryGui/Menu participate in.
    [HarmonyPatch(typeof(Player), "TakeInput")]
    public static class Player_TakeInput_VAMonsterConfigUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (VAMonsterConfigUI.IsOpen) __result = false;
        }
    }

    // VAMonsterConfigUI — form-style editor for monster presets, opened
    // from the right-click "Configure monster" menu item on a top-level
    // overlay canvas. Sections: Identity, Spawn, Combat, Drops, Advanced.
    //
    // Save writes the preset to VAMonsterPresetStore (yaml on disk).
    // Apply queues the preset for this creature's next placement via
    // VAMonsterConfigQueue.
    //
    // Lifecycle: singleton MonoBehaviour on a DontDestroyOnLoad GO.
    // The Canvas + panels are torn down on Close, re-created on Open.
    public class VAMonsterConfigUI : MonoBehaviour
    {
        // ── Singleton ────────────────────────────────────────────────

        private static VAMonsterConfigUI _instance;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("FAP_MonsterConfigUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VAMonsterConfigUI>();
        }

        public static void Open(string creaturePrefabName)
        {
            EnsureCreated();
            _instance.OpenInternal(creaturePrefabName);
        }

        public static bool IsOpen => _instance != null && _instance._panelRoot != null;

        // While the UI is open: hold the cursor free + visible, and
        // listen for ESC to close (the pause menu is gated by IsOpen
        // via Player.TakeInput so it won't fire while we're up).
        private void Update()
        {
            if (_panelRoot == null) return;

            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;

            // While the VCS bone editor is open for our preview creature, let VCS own Esc and harvest the
            // edited bones the moment it closes.
            if (_editingBones)
            {
                if (!FiresCore.Bridge.BoneEditingBridge.IsEditorOpen()) HarvestBoneEdit();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CloseInternal();
            }
        }

        // ── State ────────────────────────────────────────────────────

        private string _creature;                       // creature prefab the UI is editing
        private string _selectedPresetName;             // currently-loaded preset (empty = new)

        // VCS bone integration (only active when FiresCustomizationStudio is present).
        private string _pendingBones;                   // bone blob captured from the VCS editor, written on Save
        private GameObject _previewCreature;            // transient creature spawned for the VCS bone-edit session
        private bool _editingBones;                     // true while the VCS bone editor is open for our preview

        // ── UI references ────────────────────────────────────────────

        private GameObject _canvasRoot;
        private Canvas _canvas;
        private GameObject _panelRoot;                  // the form panel
        private GameObject _backdrop;                   // full-screen dim layer behind the panel
        private GameObject _presetDropdownRoot;         // built-on-demand dropdown for the header preset picker

        // Field inputs — populated during BuildForm, read back on Save
        private InputField _inputPresetName;
        private InputField _inputSharedName;
        private InputField _inputGroupMin;
        private InputField _inputGroupMax;
        private Toggle    _toggleRespawn;
        private InputField _inputRespawnSeconds;
        private InputField _inputStackingRadius;
        private InputField _inputHealth;
        private InputField _inputStarLevel;
        private Toggle    _toggleBoss;
        private InputField _inputDefeatKey;
        private InputField _inputDrops;                 // multiline, EWP-native items[] format
        private InputField _inputDropsAmount;           // pick-N-from-M itemAmount
        private InputField _inputScaleX, _inputScaleY, _inputScaleZ;
        private Text      _headerLabel;
        private Text      _statusLine;

        // ── Open / Close ─────────────────────────────────────────────

        private void OpenInternal(string creaturePrefabName)
        {
            if (string.IsNullOrEmpty(creaturePrefabName)) return;
            _creature = creaturePrefabName;

            EnsureCanvas();
            if (_panelRoot != null) Destroy(_panelRoot);
            BuildForm();
            // Capture input (vanilla + other-mod hotkeys) for as long as the window is open.
            FiresCore.Input.FiresInputBlock.Acquire(this);

            // Always open on a blank form. Auto-loading an existing preset
            // caused save-leakage: a new name typed over a populated form
            // would create a preset that inherited every untouched field.
            LoadPresetIntoForm(null);
        }

        private void CloseInternal()
        {
            CleanupPreview();
            FiresCore.Input.FiresInputBlock.Release(this);
            if (_presetDropdownRoot != null) Destroy(_presetDropdownRoot);
            if (_panelRoot != null) Destroy(_panelRoot);
            if (_backdrop != null) Destroy(_backdrop);
            _panelRoot = null;
            _backdrop = null;
            _presetDropdownRoot = null;
        }

        private void EnsureCanvas()
        {
            if (_canvasRoot != null) return;
            _canvasRoot = new GameObject("FAP_MonsterConfigCanvas");
            _canvasRoot.transform.SetParent(transform, false);
            _canvas = _canvasRoot.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Below VARightClickMenu (32767) so the right-click menu can
            // still appear above this form.
            _canvas.sortingOrder = CanvasSortingOrder;
            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 100f;
            _canvasRoot.AddComponent<GraphicRaycaster>();
        }

        // ── Form construction ────────────────────────────────────────

        // Layout constants
        private const float PanelWidth  = 540f;
        private const float PanelHeight = 720f;
        private const float SectionHeaderHeight = 26f;
        private const float FieldRowHeight = 36f;
        private const float FieldRowHintHeight = 18f;
        private const float HorizontalPadding = 14f;
        private const float HeaderBarHeight = 40f;
        private const float BottomBarHeight = 70f;
        private const float StatusLineHeight = 14f;
        private const float RowGap = 4f;
        private const float SectionGap = 6f;
        private const float ActionButtonWidth = 100f;
        private const float ButtonGap = 6f;
        private const float ScrollbarWidth = 14f;
        private const float ScrollSensitivity = 300f;
        private const int HintFontSize = 11;
        private const int LabelFontSize = 13;
        private const int NumberFontSize = 12;
        private const int CanvasSortingOrder = 32766;
        private const float InputRowHeight = 26f;
        private const float InputFieldWidth = 220f;
        private const float LabelWidth = 280f;
        private const float PickerLabelWidth = 160f;
        private const float PickButtonWidth = 150f;
        private const float PickButtonRightInset = 110f;
        private const float ButtonHeight = 30f;
        private const float TextInsetX = 6f;
        private const float TextInsetY = 2f;
        private const float MultilineLineHeight = 18f;
        private const float Vec3ComponentWidth = 56f;
        private const float Vec3ComponentGap = 6f;

        // Nudge so current-health never equals max-health (a vanilla edge
        // case breaks GetMaxHealth's fallback chain when they're equal).
        private const float MaxHealthEpsilon = 0.01f;
        // Scale is only written when it differs from identity by more than this.
        private const float ScaleChangeEpsilon = 0.0001f;

        // Warm-brown / parchment theme — single source in VAConfigTheme so the
        // config UIs match the RPG Maker panels + tooltips and never drift.
        private static readonly Color PanelBg = VAConfigTheme.PanelBg;
        private static readonly Color SectionBg = VAConfigTheme.SectionBg;
        private static readonly Color FieldBg = VAConfigTheme.FieldBg;
        private static readonly Color LabelColor = VAConfigTheme.Label;
        private static readonly Color HintColor = VAConfigTheme.Hint;
        private static readonly Color ButtonBg = VAConfigTheme.ButtonPrimary;
        private static readonly Color ButtonDangerBg = VAConfigTheme.ButtonDanger;
        private static readonly Color ButtonNeutralBg = VAConfigTheme.ButtonNeutral;
        private static readonly Color BackdropColor = VAConfigTheme.Backdrop;
        private static readonly Color BarColor = VAConfigTheme.BarBg;
        private static readonly Color BodyBg = VAConfigTheme.BodyBg;
        private static readonly Color NearTransparent = new Color(0f, 0f, 0f, 0.001f);
        private static readonly Color FieldTextColor = VAConfigTheme.FieldInk;
        private static readonly Color PlaceholderColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Color ScrollHandleColor = VAConfigTheme.ScrollHandle;
        private static readonly Color GripColor = VAConfigTheme.EdgeColor;
        private static readonly Color CloseButtonColor = new Color(0.9f, 0.5f, 0.5f, 1f);
        private static readonly Color SectionLabelColor = VAConfigTheme.SectionLabel;
        private static readonly Color CheckmarkColor = new Color(0.4f, 0.85f, 0.55f, 1f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.5f, 0.5f, 1f);
        private const float ScrollHandleInset = 2f;
        private const float GripSize = 16f;

        private void BuildForm()
        {
            // Modal backdrop — full-screen dim layer behind the panel.
            // Hides Hud elements that would otherwise show in the gap
            // around the panel, and catches clicks so the player can't
            // interact with the hammer / world / minimap while up.
            _backdrop = new GameObject("Backdrop");
            _backdrop.transform.SetParent(_canvasRoot.transform, false);
            var bdImg = _backdrop.AddComponent<UnityEngine.UI.Image>();
            bdImg.color = BackdropColor;
            bdImg.raycastTarget = true;
            var bdRt = bdImg.rectTransform;
            bdRt.anchorMin = Vector2.zero;
            bdRt.anchorMax = Vector2.one;
            bdRt.offsetMin = Vector2.zero;
            bdRt.offsetMax = Vector2.zero;

            // Outer panel — centered on screen.
            _panelRoot = new GameObject("Panel");
            _panelRoot.transform.SetParent(_canvasRoot.transform, false);
            var panelImg = _panelRoot.AddComponent<Image>();
            panelImg.color = PanelBg;
            panelImg.raycastTarget = true;
            VAConfigTheme.AddBorder(_panelRoot, VAConfigTheme.BorderColor, VAConfigTheme.BorderDist);
            var panelRt = panelImg.rectTransform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            panelRt.anchoredPosition = Vector2.zero;

            // Header bar — title + close X
            var header = new GameObject("Header");
            header.transform.SetParent(_panelRoot.transform, false);
            var headerImg = header.AddComponent<Image>();
            headerImg.color = BarColor;
            var headerRt = headerImg.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.anchoredPosition = Vector2.zero;
            headerRt.sizeDelta = new Vector2(0f, HeaderBarHeight);

            _headerLabel = MakeText(header.transform, $"Configure monster: {_creature}", 16, TextAnchor.MiddleLeft);
            var labelRt = _headerLabel.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(HorizontalPadding, 0f);
            labelRt.offsetMax = new Vector2(-50f, 0f);

            var closeBtn = MakeButton(header.transform, "×", CloseButtonColor, CloseInternal);
            var closeRt = ((RectTransform)closeBtn.transform);
            closeRt.anchorMin = new Vector2(1f, 0.5f);
            closeRt.anchorMax = new Vector2(1f, 0.5f);
            closeRt.pivot = new Vector2(1f, 0.5f);
            closeRt.anchoredPosition = new Vector2(-6f, 0f);
            closeRt.sizeDelta = new Vector2(34f, 28f);

            // Dragging the header strip moves the panel. The handler
            // attaches to the header's Image (raycastTarget=true so it
            // catches pointer events) and updates the panel position.
            var headerDrag = header.AddComponent<PanelDragHandler>();
            headerDrag.Panel = (RectTransform)_panelRoot.transform;
            headerImg.raycastTarget = true;

            // Bottom action bar (Save / Apply / Delete / Cancel)
            var bottomBar = new GameObject("BottomBar");
            bottomBar.transform.SetParent(_panelRoot.transform, false);
            var bottomImg = bottomBar.AddComponent<Image>();
            bottomImg.color = BarColor;
            var bottomRt = bottomImg.rectTransform;
            bottomRt.anchorMin = new Vector2(0f, 0f);
            bottomRt.anchorMax = new Vector2(1f, 0f);
            bottomRt.pivot = new Vector2(0.5f, 0f);
            bottomRt.anchoredPosition = Vector2.zero;
            // Tall enough that the status line and button row don't overlap.
            bottomRt.sizeDelta = new Vector2(0f, BottomBarHeight);

            _statusLine = MakeText(bottomBar.transform, "", 12, TextAnchor.MiddleLeft);
            _statusLine.color = HintColor;
            var statusRt = _statusLine.rectTransform;
            statusRt.anchorMin = new Vector2(0f, 1f);
            statusRt.anchorMax = new Vector2(1f, 1f);
            statusRt.pivot = new Vector2(0.5f, 1f);
            statusRt.anchoredPosition = new Vector2(0f, -RowGap);
            statusRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, StatusLineHeight);

            float buttonY = 12f;
            float buttonW = ActionButtonWidth;
            float buttonGap = ButtonGap;

            var saveBtn = MakeButton(bottomBar.transform, "Save", ButtonBg, OnSaveClicked);
            PositionButton(saveBtn, 0, buttonW, buttonY, buttonGap, 4);

            var applyBtn = MakeButton(bottomBar.transform, "Apply (queue)", ButtonBg, OnApplyClicked);
            PositionButton(applyBtn, 1, buttonW, buttonY, buttonGap, 4);

            var deleteBtn = MakeButton(bottomBar.transform, "Delete", ButtonDangerBg, OnDeleteClicked);
            PositionButton(deleteBtn, 2, buttonW, buttonY, buttonGap, 4);

            var cancelBtn = MakeButton(bottomBar.transform, "Close", ButtonNeutralBg, CloseInternal);
            PositionButton(cancelBtn, 3, buttonW, buttonY, buttonGap, 4);

            // Scrollable body between the header and bottom bar.
            var bodyContainer = new GameObject("BodyScroll");
            bodyContainer.transform.SetParent(_panelRoot.transform, false);
            var bodyImg = bodyContainer.AddComponent<Image>();
            bodyImg.color = BodyBg;
            bodyImg.raycastTarget = false;
            var bodyRt = bodyImg.rectTransform;
            bodyRt.anchorMin = new Vector2(0f, 0f);
            bodyRt.anchorMax = new Vector2(1f, 1f);
            bodyRt.offsetMin = new Vector2(0f, BottomBarHeight);
            bodyRt.offsetMax = new Vector2(0f, -HeaderBarHeight);

            // ScrollRect — vertical-only scroll for the form body.
            // High sensitivity so a wheel notch traverses ~one section.
            var scrollRect = bodyContainer.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = ScrollSensitivity;

            // Viewport, trimmed on the right by the scrollbar width.
            // raycastTarget MUST be true here — ScrollRect only receives
            // OnScroll when a raycastable graphic under the cursor bubbles
            // the event up. The image stays near-transparent so this is a
            // raycast-only catch, not a visual change.
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(bodyContainer.transform, false);
            var vImg = viewport.AddComponent<Image>();
            vImg.color = NearTransparent;
            vImg.raycastTarget = true;
            viewport.AddComponent<RectMask2D>();
            var vRt = vImg.rectTransform;
            vRt.anchorMin = Vector2.zero;
            vRt.anchorMax = Vector2.one;
            vRt.offsetMin = Vector2.zero;
            vRt.offsetMax = new Vector2(-ScrollbarWidth, 0f);
            scrollRect.viewport = vRt;

            // Content — sized as we add rows.
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var cImg = content.AddComponent<Image>();
            cImg.color = NearTransparent;
            cImg.raycastTarget = false;
            var cRt = cImg.rectTransform;
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.anchoredPosition = Vector2.zero;
            scrollRect.content = cRt;

            // Visible scrollbar on the right edge, built by hand (no
            // Resources.Load on vanilla guistyle assets, which may not be
            // loaded yet). Image + Scrollbar + handle child is sufficient.
            var scrollbarGo = new GameObject("VerticalScrollbar");
            scrollbarGo.transform.SetParent(bodyContainer.transform, false);
            var sbBg = scrollbarGo.AddComponent<Image>();
            sbBg.color = Color.clear;
            sbBg.raycastTarget = true;
            var sbRt = sbBg.rectTransform;
            sbRt.anchorMin = new Vector2(1f, 0f);
            sbRt.anchorMax = new Vector2(1f, 1f);
            sbRt.pivot = new Vector2(1f, 0.5f);
            sbRt.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            sbRt.anchoredPosition = Vector2.zero;

            // Sliding area (full bar bounds) with a Handle child that the
            // Scrollbar resizes + repositions from the viewport/content
            // size ratio.
            var slidingAreaGo = new GameObject("Sliding Area");
            slidingAreaGo.transform.SetParent(scrollbarGo.transform, false);
            var saRt = slidingAreaGo.AddComponent<RectTransform>();
            saRt.anchorMin = Vector2.zero;
            saRt.anchorMax = Vector2.one;
            saRt.offsetMin = new Vector2(ScrollHandleInset, ScrollHandleInset);
            saRt.offsetMax = new Vector2(-ScrollHandleInset, -ScrollHandleInset);

            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(slidingAreaGo.transform, false);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = ScrollHandleColor;
            var handleRt = handleImg.rectTransform;
            handleRt.anchorMin = Vector2.zero;
            handleRt.anchorMax = Vector2.one;
            handleRt.offsetMin = Vector2.zero;
            handleRt.offsetMax = Vector2.zero;

            var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.targetGraphic = handleImg;
            scrollbar.handleRect = handleRt;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollRect.verticalScrollbar = scrollbar;
            // AutoHide keeps layout stable (the ExpandViewport variant
            // changes viewport width on hide).
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            // RowLayout tracks the running Y offset as we add rows.
            var layout = new RowLayout { Y = 0f };

            // Preset picker (in the header strip)
            BuildPresetPickerRow(content.transform, layout);

            BuildSectionHeader(content.transform, layout, "Identity");
            _inputPresetName = BuildTextRow(content.transform, layout,
                "Preset name",
                "Used as Humanoid.m_name on placed creatures AND as the EWP rule filter. " +
                "Keep unique — naming a preset 'Boar' will match every vanilla Boar in the world.");
            _inputSharedName = BuildTextRow(content.transform, layout,
                "Shared display name (Humanoid.m_name override)",
                "Shown in the creature's hover tooltip. Leave blank to match the preset name.");

            BuildSectionHeader(content.transform, layout, "Spawn");
            _inputGroupMin = BuildNumberRow(content.transform, layout,
                "Group size — min",
                "How many copies to spawn at the click point. Set both to 1 for single spawns.");
            _inputGroupMax = BuildNumberRow(content.transform, layout,
                "Group size — max",
                "Random count between min and max (inclusive) per click.");
            _toggleRespawn = BuildToggleRow(content.transform, layout,
                "Respawn on death",
                "When ON, the creature respawns after the configured delay. " +
                "Uses EWP's destroy-rule pipeline when EWP is installed; falls back to a FAP-side coroutine otherwise.");
            _inputRespawnSeconds = BuildNumberRow(content.transform, layout,
                "Respawn delay (seconds)",
                "Time between death and respawn. Only used when 'Respawn on death' is ON.");
            _inputStackingRadius = BuildNumberRow(content.transform, layout,
                "Prevent stacking — radius (m)",
                "When > 0, respawn is SKIPPED if another creature with the same preset name " +
                "is still alive within this radius. Guarantees 'one in, one out' for boss-class " +
                "creatures. Set to 0 to disable the check (useful when using this rule to " +
                "maintain ambient density of common creatures).");

            BuildSectionHeader(content.transform, layout, "Combat");
            _inputHealth = BuildNumberRow(content.transform, layout,
                "Health",
                "Both 'health' AND 'max_health' are written to the ZDO. max_health is set " +
                "to health + 0.01 to avoid a vanilla edge case where current == max breaks " +
                "GetMaxHealth's fallback chain.");
            _inputStarLevel = BuildNumberRow(content.transform, layout,
                "Star level (1-3)",
                "1 = no stars, 2 = one star, 3 = two stars. Vanilla ZDO key 'level'. " +
                "Multiplies base health and damage according to vanilla scaling.");
            _toggleBoss = BuildToggleRow(content.transform, layout,
                "Boss flag",
                "Sets Humanoid.m_boss = 1 — Valheim shows the boss-health bar UI when " +
                "players are near. Also affects some AI behaviours.");
            _inputDefeatKey = BuildTextRow(content.transform, layout,
                "Set global key on death",
                "Vanilla Humanoid.m_defeatSetGlobalKey override. When this creature dies, " +
                "the named key is added to the world's global-key list. Useful for gating " +
                "raids, biome unlocks, and quest progress (e.g. 'defeated_eikthyr').");

            BuildSectionHeader(content.transform, layout, "Drops");
            _inputDrops = BuildMultilineTextRow(content.transform, layout,
                "Drop table (one item per line)",
                VALootTextFormat.HintText +
                "\nOverrides vanilla CharacterDrop entirely — defining drops here " +
                "suppresses the prefab's baked drop list.",
                lines: 6);
            _inputDropsAmount = BuildTextRow(content.transform, layout,
                "Pick N from pool (optional)",
                "Empty = each item rolls independently against its own chance. " +
                "Single number ('3') = always drop exactly 3 from the pool. Range " +
                "('2;4') = drop a random count in that window per death. Higher " +
                "chance values bias selection toward those items.");

            BuildSectionHeader(content.transform, layout, "Advanced");
            BuildVec3Row(content.transform, layout,
                "Scale (x, y, z)",
                "Visual scale of the creature. Vanilla ZDO 'scale' vec3. Use values like " +
                "1,1,1 for default size; 0.5,0.5,0.5 for mini bosses.",
                out _inputScaleX, out _inputScaleY, out _inputScaleZ);

            // VCS bone editor — only when FiresCustomizationStudio is loaded (Core bridge flag). Opens VCS's
            // bone editor on a transient preview creature; the edited bone positions are saved into this preset.
            if (FiresCore.Bridge.BoneEditingBridge.IsAvailable)
                BuildButtonRow(content.transform, layout, "Edit bones (VCS)…", OnEditBonesClicked);

            // Final content height = accumulated Y offset.
            cRt.sizeDelta = new Vector2(0f, layout.Y);

            // Flush pending layout so the scrollbar handle is sized from
            // real content height this frame, avoiding a full-bar handle
            // on first paint that snaps to size on the first scroll.
            Canvas.ForceUpdateCanvases();

            // Four corner scale grips, drawn after all other panel children so
            // they sit on top. Dragging ANY corner scales the WHOLE window
            // (box + fonts + section spacing) uniformly, so the text grows with
            // the window instead of the box just gaining empty space.
            VAWindowScaleGrip.AddScaleGrips((RectTransform)_panelRoot.transform, GripColor, GripSize);
        }

        // Tracks the running Y offset (downwards from content top) as
        // we stack section headers + field rows.
        private class RowLayout
        {
            public float Y;
        }

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
            txt.color = SectionLabelColor;
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(HorizontalPadding, 0f);
            trt.offsetMax = new Vector2(-HorizontalPadding, 0f);

            layout.Y += SectionHeaderHeight + SectionGap;
        }

        // Adds [Label  ] [InputField] / hint text row. Returns the
        // InputField so the caller can read/write its text.
        private InputField BuildTextRow(Transform parent, RowLayout layout, string label, string hint)
        {
            var input = MakeFieldRowSkeleton(parent, layout, label, hint, out var inputArea);
            input.lineType = InputField.LineType.SingleLine;
            return input;
        }

        private InputField BuildNumberRow(Transform parent, RowLayout layout, string label, string hint)
        {
            var input = MakeFieldRowSkeleton(parent, layout, label, hint, out var inputArea);
            input.lineType = InputField.LineType.SingleLine;
            input.contentType = InputField.ContentType.DecimalNumber;
            return input;
        }

        private InputField BuildMultilineTextRow(Transform parent, RowLayout layout, string label, string hint, int lines)
        {
            float extraHeight = (lines - 1) * MultilineLineHeight;
            var input = MakeFieldRowSkeleton(parent, layout, label, hint, out var inputArea, extraInputHeight: extraHeight);
            input.lineType = InputField.LineType.MultiLineNewline;
            ((RectTransform)inputArea.transform).sizeDelta += new Vector2(0f, extraHeight);
            input.textComponent.alignment = TextAnchor.UpperLeft;
            return input;
        }

        private Toggle BuildToggleRow(Transform parent, RowLayout layout, string label, string hint)
        {
            // Wrap-aware hint height so long hints don't bleed into the
            // next row. See VAHintHeightEstimator for the metrics.
            float hintAvailableWidth = PanelWidth - HorizontalPadding * 2f - ScrollbarWidth;
            float hintHeight = Mathf.Max(
                FieldRowHintHeight,
                VAHintHeightEstimator.Estimate(hint, hintAvailableWidth, HintFontSize));
            float rowHeight = FieldRowHeight + hintHeight;
            var row = new GameObject($"Row_{label}");
            row.transform.SetParent(parent, false);
            var rt = row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(0f, rowHeight);

            // Label (left)
            var lbl = MakeText(row.transform, label, LabelFontSize, TextAnchor.MiddleLeft);
            lbl.color = LabelColor;
            var lblRt = lbl.rectTransform;
            lblRt.anchorMin = new Vector2(0f, 1f);
            lblRt.anchorMax = new Vector2(0f, 1f);
            lblRt.pivot = new Vector2(0f, 1f);
            lblRt.anchoredPosition = new Vector2(HorizontalPadding, 0f);
            lblRt.sizeDelta = new Vector2(LabelWidth, FieldRowHeight);

            // Toggle (right)
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
            checkmarkImg.color = CheckmarkColor;
            var checkmarkRt = checkmarkImg.rectTransform;
            checkmarkRt.anchorMin = Vector2.zero;
            checkmarkRt.anchorMax = Vector2.one;
            checkmarkRt.offsetMin = new Vector2(4f, 4f);
            checkmarkRt.offsetMax = new Vector2(-4f, -4f);

            var toggle = toggleGo.AddComponent<Toggle>();
            toggle.targetGraphic = toggleBg;
            toggle.graphic = checkmarkImg;

            // Hint
            var hintTxt = MakeText(row.transform, hint, HintFontSize, TextAnchor.UpperLeft);
            hintTxt.color = HintColor;
            var hintRt = hintTxt.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = Vector2.zero;
            hintRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, hintHeight);
            hintTxt.horizontalOverflow = HorizontalWrapMode.Wrap;

            layout.Y += rowHeight + RowGap;
            return toggle;
        }

        private void BuildVec3Row(Transform parent, RowLayout layout, string label, string hint,
            out InputField x, out InputField y, out InputField z)
        {
            float hintAvailableWidth = PanelWidth - HorizontalPadding * 2f - ScrollbarWidth;
            float hintHeight = Mathf.Max(
                FieldRowHintHeight,
                VAHintHeightEstimator.Estimate(hint, hintAvailableWidth, HintFontSize));
            float rowHeight = FieldRowHeight + hintHeight;
            var row = new GameObject($"Row_{label}");
            row.transform.SetParent(parent, false);
            var rt = row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(0f, rowHeight);

            var lbl = MakeText(row.transform, label, LabelFontSize, TextAnchor.MiddleLeft);
            lbl.color = LabelColor;
            var lblRt = lbl.rectTransform;
            lblRt.anchorMin = new Vector2(0f, 1f);
            lblRt.anchorMax = new Vector2(0f, 1f);
            lblRt.pivot = new Vector2(0f, 1f);
            lblRt.anchoredPosition = new Vector2(HorizontalPadding, 0f);
            lblRt.sizeDelta = new Vector2(LabelWidth, FieldRowHeight);

            float componentWidth = Vec3ComponentWidth;
            float componentGap = Vec3ComponentGap;
            float totalComponentWidth = componentWidth * 3f + componentGap * 2f;
            float startX = -HorizontalPadding - totalComponentWidth;
            x = MakeNumberInput(row.transform);
            PositionInRow(x.gameObject, startX, RowGap, componentWidth);
            y = MakeNumberInput(row.transform);
            PositionInRow(y.gameObject, startX + componentWidth + componentGap, RowGap, componentWidth);
            z = MakeNumberInput(row.transform);
            PositionInRow(z.gameObject, startX + (componentWidth + componentGap) * 2f, RowGap, componentWidth);

            var hintTxt = MakeText(row.transform, hint, HintFontSize, TextAnchor.UpperLeft);
            hintTxt.color = HintColor;
            var hintRt = hintTxt.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = Vector2.zero;
            hintRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, hintHeight);
            hintTxt.horizontalOverflow = HorizontalWrapMode.Wrap;

            layout.Y += rowHeight + RowGap;
        }

        private static void PositionInRow(GameObject go, float xFromRight, float yFromTop, float width)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(xFromRight, -yFromTop);
            rt.sizeDelta = new Vector2(width, InputRowHeight);
        }

        // Shared row skeleton — label on left, input on right, hint
        // below spanning the row. Returns the InputField.
        private InputField MakeFieldRowSkeleton(Transform parent, RowLayout layout, string label, string hint, out GameObject inputArea, float extraInputHeight = 0f)
        {
            float hintAvailableWidth = PanelWidth - HorizontalPadding * 2f - ScrollbarWidth;
            float hintHeight = Mathf.Max(
                FieldRowHintHeight,
                VAHintHeightEstimator.Estimate(hint, hintAvailableWidth, HintFontSize));
            float rowHeight = FieldRowHeight + hintHeight + extraInputHeight + RowGap;
            var row = new GameObject($"Row_{label}");
            row.transform.SetParent(parent, false);
            var rt = row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(0f, rowHeight);

            // Label
            var lbl = MakeText(row.transform, label, LabelFontSize, TextAnchor.MiddleLeft);
            lbl.color = LabelColor;
            var lblRt = lbl.rectTransform;
            lblRt.anchorMin = new Vector2(0f, 1f);
            lblRt.anchorMax = new Vector2(0f, 1f);
            lblRt.pivot = new Vector2(0f, 1f);
            lblRt.anchoredPosition = new Vector2(HorizontalPadding, 0f);
            lblRt.sizeDelta = new Vector2(LabelWidth, FieldRowHeight);

            // Input area (right-aligned)
            inputArea = new GameObject("Input");
            inputArea.transform.SetParent(row.transform, false);
            var inputBg = inputArea.AddComponent<Image>();
            inputBg.color = FieldBg;
            var inputRt = inputBg.rectTransform;
            inputRt.anchorMin = new Vector2(1f, 1f);
            inputRt.anchorMax = new Vector2(1f, 1f);
            inputRt.pivot = new Vector2(1f, 1f);
            inputRt.anchoredPosition = new Vector2(-HorizontalPadding, -RowGap);
            inputRt.sizeDelta = new Vector2(InputFieldWidth, InputRowHeight);

            // Text
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(inputArea.transform, false);
            var txt = textGo.AddComponent<Text>();
            txt.font = GetFont();
            txt.fontSize = LabelFontSize;
            txt.color = FieldTextColor;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.supportRichText = false;
            var txtRt = txt.rectTransform;
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(TextInsetX, TextInsetY);
            txtRt.offsetMax = new Vector2(-TextInsetX, -TextInsetY);

            // Placeholder
            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(inputArea.transform, false);
            var ph = phGo.AddComponent<Text>();
            ph.font = GetFont();
            ph.fontSize = LabelFontSize;
            ph.color = PlaceholderColor;
            ph.text = "";
            ph.alignment = TextAnchor.MiddleLeft;
            var phRt = ph.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(TextInsetX, TextInsetY);
            phRt.offsetMax = new Vector2(-TextInsetX, -TextInsetY);

            var input = inputArea.AddComponent<InputField>();
            input.targetGraphic = inputBg;
            input.textComponent = txt;
            input.placeholder = ph;

            // Hint
            var hintTxt = MakeText(row.transform, hint, HintFontSize, TextAnchor.UpperLeft);
            hintTxt.color = HintColor;
            var hintRt = hintTxt.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = Vector2.zero;
            hintRt.sizeDelta = new Vector2(-HorizontalPadding * 2f, hintHeight);
            hintTxt.horizontalOverflow = HorizontalWrapMode.Wrap;

            layout.Y += rowHeight + RowGap;
            return input;
        }

        private InputField MakeNumberInput(Transform parent)
        {
            var go = new GameObject("Num");
            go.transform.SetParent(parent, false);
            var bg = go.AddComponent<Image>();
            bg.color = FieldBg;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var txt = textGo.AddComponent<Text>();
            txt.font = GetFont();
            txt.fontSize = NumberFontSize;
            txt.color = FieldTextColor;
            txt.alignment = TextAnchor.MiddleCenter;
            var tr = txt.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var ph = phGo.AddComponent<Text>();
            ph.font = GetFont();
            ph.fontSize = NumberFontSize;
            ph.color = PlaceholderColor;
            ph.text = "0";
            ph.alignment = TextAnchor.MiddleCenter;
            var pr = ph.rectTransform;
            pr.anchorMin = Vector2.zero;
            pr.anchorMax = Vector2.one;
            pr.offsetMin = Vector2.zero;
            pr.offsetMax = Vector2.zero;

            var input = go.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.textComponent = txt;
            input.placeholder = ph;
            input.contentType = InputField.ContentType.DecimalNumber;
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

            var lbl = MakeText(row.transform, "Preset (existing):", LabelFontSize, TextAnchor.MiddleLeft);
            lbl.color = LabelColor;
            var lblRt = lbl.rectTransform;
            lblRt.anchorMin = new Vector2(0f, 0.5f);
            lblRt.anchorMax = new Vector2(0f, 0.5f);
            lblRt.pivot = new Vector2(0f, 0.5f);
            lblRt.anchoredPosition = new Vector2(HorizontalPadding, 0f);
            lblRt.sizeDelta = new Vector2(PickerLabelWidth, FieldRowHeight);

            // "Pick existing..." opens the preset submenu; "New" clears
            // the form for a fresh preset.
            var pickBtn = MakeButton(row.transform, "Pick existing...", ButtonNeutralBg, () =>
            {
                var items = new List<VARightClickMenu.Item>();
                int matched = 0;
                foreach (var entry in VAMonsterPresetStore.All)
                {
                    var preset = entry.Value;
                    if (preset == null) continue;
                    if (!string.Equals(preset.Creature, _creature, StringComparison.Ordinal)) continue;
                    matched++;
                    string captured = preset.name;
                    items.Add(new VARightClickMenu.Item("→ " + preset.name, () => LoadPresetIntoForm(captured)));
                }
                if (matched == 0)
                {
                    items.Add(new VARightClickMenu.Item("(no presets saved for this creature)", null, disabled: true));
                }
                VARightClickMenu.Show(items);
            });
            var pickRt = (RectTransform)pickBtn.transform;
            pickRt.anchorMin = new Vector2(1f, 0.5f);
            pickRt.anchorMax = new Vector2(1f, 0.5f);
            pickRt.pivot = new Vector2(1f, 0.5f);
            pickRt.anchoredPosition = new Vector2(-HorizontalPadding - PickButtonRightInset, 0f);
            pickRt.sizeDelta = new Vector2(PickButtonWidth, InputRowHeight);

            var newBtn = MakeButton(row.transform, "New", ButtonBg, () => LoadPresetIntoForm(null));
            var newRt = (RectTransform)newBtn.transform;
            newRt.anchorMin = new Vector2(1f, 0.5f);
            newRt.anchorMax = new Vector2(1f, 0.5f);
            newRt.pivot = new Vector2(1f, 0.5f);
            newRt.anchoredPosition = new Vector2(-HorizontalPadding, 0f);
            newRt.sizeDelta = new Vector2(ActionButtonWidth, InputRowHeight);

            layout.Y += rowHeight + SectionGap;
        }

        // ── Form ↔ Preset round-trip ─────────────────────────────────

        private void LoadPresetIntoForm(string presetName)
        {
            VAMonsterPreset preset = null;
            if (!string.IsNullOrEmpty(presetName))
            {
                VAMonsterPresetStore.TryGet(presetName, out preset);
            }

            if (preset == null)
            {
                _selectedPresetName = null;
                _inputPresetName.text = string.Empty;
                _inputSharedName.text = string.Empty;
                _inputGroupMin.text = "1";
                _inputGroupMax.text = "1";
                _toggleRespawn.isOn = false;
                _inputRespawnSeconds.text = "60";
                _inputStackingRadius.text = "100";
                _inputHealth.text = "";
                _inputStarLevel.text = "1";
                _toggleBoss.isOn = false;
                _inputDefeatKey.text = string.Empty;
                _inputDrops.text = string.Empty;
                _inputDropsAmount.text = string.Empty;
                _inputScaleX.text = "1";
                _inputScaleY.text = "1";
                _inputScaleZ.text = "1";
                _pendingBones = string.Empty;
                if (_statusLine != null) _statusLine.text = "New preset — fill in the form and click Save, or click 'Pick existing...' to edit one.";
                return;
            }

            _selectedPresetName = preset.name;
            _pendingBones = preset.Bones; // seed prior bone edits so the editor opens showing them
            _inputPresetName.text = preset.name ?? string.Empty;
            _inputSharedName.text = preset.TryGetString("Humanoid.m_name") ?? string.Empty;
            _inputGroupMin.text = preset.TryGetIntStr("va_group_min") ?? "1";
            _inputGroupMax.text = preset.TryGetIntStr("va_group_max") ?? "1";

            string respawn = preset.TryGetFloatStr("va_respawn_seconds");
            float respawnVal = ParseFloat(respawn, 0f);
            _toggleRespawn.isOn = respawnVal > 0f;
            _inputRespawnSeconds.text = respawnVal > 0f
                ? respawnVal.ToString("F1", CultureInfo.InvariantCulture)
                : "60";

            _inputStackingRadius.text = preset.TryGetFloatStr("va_prevent_stacking_radius") ?? "100";
            _inputHealth.text = preset.TryGetFloatStr("health") ?? "";
            _inputStarLevel.text = preset.TryGetIntStr("level") ?? "1";
            _toggleBoss.isOn = preset.TryGetIntStr("override_boss") == "1"
                || preset.TryGetIntStr("Humanoid.m_boss") == "1";
            _inputDefeatKey.text = preset.TryGetString("Humanoid.m_defeatSetGlobalKey") ?? string.Empty;
            _inputDrops.text = VALootTextFormat.Serialize(preset.items);
            _inputDropsAmount.text = preset.itemAmount ?? string.Empty;

            string scaleStr = preset.TryGetVecStr("scale");
            if (!string.IsNullOrEmpty(scaleStr))
            {
                var parts = scaleStr.Split(',');
                if (parts.Length >= 3)
                {
                    _inputScaleX.text = parts[0].Trim();
                    _inputScaleY.text = parts[1].Trim();
                    _inputScaleZ.text = parts[2].Trim();
                }
            }
            else
            {
                _inputScaleX.text = "1";
                _inputScaleY.text = "1";
                _inputScaleZ.text = "1";
            }

            if (_statusLine != null) _statusLine.text = $"Loaded preset '{preset.name}'.";
        }

        private VAMonsterPreset BuildPresetFromForm()
        {
            var preset = new VAMonsterPreset
            {
                name = (_inputPresetName.text ?? string.Empty).Trim(),
            };
            preset.Creature = _creature;  // stored inside strings as va_creature

            if (string.IsNullOrEmpty(preset.name)) return null;

            // ints
            int groupMin = ParseInt(_inputGroupMin.text, 1);
            int groupMax = ParseInt(_inputGroupMax.text, groupMin);
            if (groupMax < groupMin) groupMax = groupMin;
            preset.ints.Add($"va_group_min, {groupMin}");
            preset.ints.Add($"va_group_max, {groupMax}");

            int level = ParseInt(_inputStarLevel.text, 1);
            if (level < 1) level = 1;
            preset.ints.Add($"level, {level}");

            // Boss flag — emit both the WEC key and the vanilla
            // LoadFields key so both readers see it.
            int boss = _toggleBoss.isOn ? 1 : 0;
            preset.ints.Add($"override_boss, {boss}");
            preset.ints.Add($"Humanoid.m_boss, {boss}");

            // floats
            float health = ParseFloat(_inputHealth.text, 0f);
            if (health > 0f)
            {
                preset.floats.Add($"health, {health.ToString("F2", CultureInfo.InvariantCulture)}");
                preset.floats.Add($"max_health, {(health + MaxHealthEpsilon).ToString("F2", CultureInfo.InvariantCulture)}");
            }

            if (_toggleRespawn.isOn)
            {
                float seconds = ParseFloat(_inputRespawnSeconds.text, 60f);
                if (seconds > 0f)
                    preset.floats.Add($"va_respawn_seconds, {seconds.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            float stackingRadius = ParseFloat(_inputStackingRadius.text, 100f);
            preset.floats.Add($"va_prevent_stacking_radius, {stackingRadius.ToString("F1", CultureInfo.InvariantCulture)}");

            // strings
            string sharedName = (_inputSharedName.text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(sharedName)) sharedName = preset.name;
            preset.strings.Add($"Humanoid.m_name, {sharedName}");

            string defeatKey = (_inputDefeatKey.text ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(defeatKey))
                preset.strings.Add($"Humanoid.m_defeatSetGlobalKey, {defeatKey}");

            // EWP-native items list — replaces the legacy va_drops
            // pipe-string. Vanilla CharacterDrop suppression for
            // preset-stamped creatures happens in VAMonsterConfigApplier.
            preset.items = VALootTextFormat.Parse(_inputDrops.text);
            var dropsAmount = (_inputDropsAmount.text ?? string.Empty).Trim();
            preset.itemAmount = string.IsNullOrEmpty(dropsAmount) ? null : dropsAmount;

            // vecs
            float sx = ParseFloat(_inputScaleX.text, 1f);
            float sy = ParseFloat(_inputScaleY.text, 1f);
            float sz = ParseFloat(_inputScaleZ.text, 1f);
            if (Math.Abs(sx - 1f) > ScaleChangeEpsilon
                || Math.Abs(sy - 1f) > ScaleChangeEpsilon
                || Math.Abs(sz - 1f) > ScaleChangeEpsilon)
            {
                preset.vecs.Add($"scale, {sx.ToString("F3", CultureInfo.InvariantCulture)},{sy.ToString("F3", CultureInfo.InvariantCulture)},{sz.ToString("F3", CultureInfo.InvariantCulture)}");
            }

            // VCS bone blob (captured from the editor). Stored as a "va_bones, <blob>" strings[] line.
            if (!string.IsNullOrEmpty(_pendingBones))
                preset.Bones = _pendingBones;

            return preset;
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

            // Saving under a new name CREATES a new preset; it never
            // deletes the loaded one. This avoids the form-state-leakage
            // bug where a new name typed over a loaded preset wiped it.
            if (!VAMonsterPresetStore.Save(preset))
            {
                Status("Save failed — see log.", isError: true);
                return;
            }

            // Save writes ONLY the data yaml — placement is the trigger
            // that materializes rules. Rules reference data by name, so
            // edited fields take effect on the next creature load.
            _selectedPresetName = preset.name;
            Status($"Saved preset '{preset.name}'.");

            // UX tail — close the menus and arm the hammer with the
            // monster stand-in so the placement ghost is up immediately.
            CloseInternal();
            VAHammerArmHelper.ArmAndClose(
                VAMonsterStandinFactory.NamePrefix + _creature);
        }

        private void OnApplyClicked()
        {
            // Save first so the queued reference resolves to fresh state.
            var preset = BuildPresetFromForm();
            if (preset == null)
            {
                Status("Preset name is required to apply.", isError: true);
                return;
            }
            if (!VAMonsterPresetStore.Save(preset))
            {
                Status("Save failed — apply aborted.", isError: true);
                return;
            }
            _selectedPresetName = preset.name;
            VAMonsterConfigQueue.EnqueueForNextPlacement(_creature, preset.name);
            // No EWP rule write here — the placement that consumes
            // this queue entry will regenerate the rule yaml.
            Status($"Saved + queued '{preset.name}' for next {_creature} placement.");

            // Same UX tail as Save — close menus + arm the hammer with
            // the monster stand-in so the player can place immediately.
            CloseInternal();
            VAHammerArmHelper.ArmAndClose(
                VAMonsterStandinFactory.NamePrefix + _creature);
        }

        private void OnDeleteClicked()
        {
            if (string.IsNullOrEmpty(_selectedPresetName))
            {
                Status("Nothing to delete — preset is unsaved.", isError: true);
                return;
            }
            if (VAMonsterPresetStore.Delete(_selectedPresetName))
            {
                VAMonsterEwpRuleWriter.RegenerateRules();
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
            _statusLine.color = isError ? ErrorColor : HintColor;
        }

        // ── UI building helpers ──────────────────────────────────────

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
            var text = go.AddComponent<Text>();
            text.font = GetFont();
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = LabelColor;
            text.text = content;
            text.raycastTarget = false;
            text.supportRichText = true;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static GameObject MakeButton(Transform parent, string label, Color bg, Action onClick)
        {
            var go = new GameObject($"Button_{label}");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = bg;
            var txt = MakeText(go.transform, label, LabelFontSize, TextAnchor.MiddleCenter);
            txt.color = Color.white;
            var trt = txt.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null)
            {
                btn.onClick.AddListener(() => onClick());
            }
            return go;
        }

        private static void PositionButton(GameObject btn, int index, float buttonWidth, float yFromBottom, float gap, int totalButtons)
        {
            float totalWidth = buttonWidth * totalButtons + gap * (totalButtons - 1);
            // Right-align the button strip.
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            float xFromRight = -HorizontalPadding - (totalButtons - 1 - index) * (buttonWidth + gap);
            rt.anchoredPosition = new Vector2(xFromRight, yFromBottom);
            rt.sizeDelta = new Vector2(buttonWidth, ButtonHeight);
        }

        // ── VCS bone-edit integration ────────────────────────────────

        // Full-width button row in the scrolling form (advances the layout cursor like the other rows).
        private GameObject BuildButtonRow(Transform parent, RowLayout layout, string label, Action onClick)
        {
            var btn = MakeButton(parent, label, ButtonBg, onClick);
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -layout.Y);
            rt.sizeDelta = new Vector2(-(HorizontalPadding * 2f), ButtonHeight);
            layout.Y += ButtonHeight + SectionGap;
            return btn;
        }

        // Spawns a transient preview creature, seeds prior bone edits, and opens the VCS bone editor on it.
        private void OnEditBonesClicked()
        {
            if (!FiresCore.Bridge.BoneEditingBridge.IsAvailable || _editingBones) return;
            var player = Player.m_localPlayer;
            if (player == null) { Status("Must be in-world to edit bones.", isError: true); return; }
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(_creature) : null;
            if (prefab == null) { Status("Could not find the creature prefab.", isError: true); return; }

            Vector3 pos = player.transform.position + player.transform.forward * 3f + Vector3.up * 0.4f;
            _previewCreature = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            var ai = _previewCreature.GetComponent<BaseAI>();
            if (ai != null) ai.enabled = false; // freeze it so it doesn't wander/attack while we edit

            var character = _previewCreature.GetComponent<Character>();
            if (character == null) { CleanupPreview(); Status("That prefab has no Character to edit.", isError: true); return; }

            if (!string.IsNullOrEmpty(_pendingBones))
                FiresCore.Bridge.BoneEditingBridge.SetBoneData(character, _pendingBones);

            if (FiresCore.Bridge.BoneEditingBridge.OpenEditor(character))
            {
                _editingBones = true;
                Status("Bone editor open — close it (Esc / X) when done to capture the edit.");
            }
            else
            {
                CleanupPreview();
                Status("Could not open the bone editor.", isError: true);
            }
        }

        // Called when the VCS editor closes: capture the edited bones, then destroy the preview creature.
        private void HarvestBoneEdit()
        {
            var character = _previewCreature != null ? _previewCreature.GetComponent<Character>() : null;
            if (character != null)
            {
                string captured = FiresCore.Bridge.BoneEditingBridge.GetBoneData(character);
                if (!string.IsNullOrEmpty(captured)) _pendingBones = captured;
            }
            CleanupPreview();
            Status(string.IsNullOrEmpty(_pendingBones)
                ? "No bone changes captured."
                : "Bone edits captured — click Save (or Apply) to store them in the preset.");
        }

        private void CleanupPreview()
        {
            _editingBones = false;
            if (_previewCreature != null) { UnityEngine.Object.Destroy(_previewCreature); _previewCreature = null; }
        }

        // ── Drag + resize helpers ────────────────────────────────────

        // Header bar drag — moves the panel's anchoredPosition by the
        // pointer delta. The panel keeps its screen-center pivot so
        // resize math stays simple (origin always at panel center).
        private sealed class PanelDragHandler : MonoBehaviour,
            IBeginDragHandler, IDragHandler
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

        // ── Parsing helpers ──────────────────────────────────────────

        private static int ParseInt(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }
    }
}
