using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FiresCore.UI.Shell;
using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.291 (Fire: Radius Swapper): the picking flow, the kit confirm card and the kit preset window.
    //
    // Flow: a left click on the swapper in the build menu (BuildUi.OnSelectPiece, prefix below) does not select it; the menu stays
    // open and "Select the prefab to be swapped" shows. The next piece click is FROM ("Select the prefab to swap it to"), the next
    // is TO; then the kit card "Queue tool to swap <FROM> for <TO>?" Yes / No. Yes closes the build menu and selects the swapper
    // with that pair; No cancels the picking and leaves the build menu open. Esc while picking or on the card cancels.
    // While picking, a popup or the preset window is up, vanilla's build-menu input (NavigationUpdate: right click / Esc close the
    // menu) is skipped so the menu stays open.
    // The kit card is reused from FiresCompanions CompanionKitBits.Confirm; the text field from Core ShellConfigModule.MakeInput;
    // everything is Core kit (Widgets / KitText / KitSprites / FiresTokens / ShellWidgets.Clickable) on FAP's own overlay canvas.
    public sealed class VASwapFlow : MonoBehaviour
    {
        internal enum Stage { None, PickFrom, PickTo, Confirm }

        internal static Stage Current { get; private set; }
        internal static bool Busy => Current != Stage.None;

        private static VASwapFlow _instance;
        private static bool s_p7ClosedLogged;   // 1.1.325 (UI audit P7)
        private static SwapPick _from;
        private static string _prompt;
        private static RectTransform _card;
        private static bool _pickerOpen;   // 1.1.303: the preset picker (VARightClickMenu) is up: Esc closes it, not the picking
        private GUIStyle _promptStyle;

        // 1.1.303: picking FROM or TO (the preset picker may open on a build-menu piece).
        internal static bool InPick => Current == Stage.PickFrom || Current == Stage.PickTo;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("FAP_SwapFlow");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VASwapFlow>();
        }

        // BuildUi.OnSelectPiece: true = the click is the swapper's (vanilla must not select or close).
        internal static bool InterceptSelect(Piece piece)
        {
            if (piece == null) return false;
            if (!VAVanillaPieceManager.IsAdminGateOpen()) { if (Busy) Cancel("admin gate closed"); return false; }
            string name = piece.gameObject != null ? piece.gameObject.name : null;
            switch (Current)
            {
                case Stage.None:
                    if (!VARadiusSwapTool.IsStandinName(name)) return false;
                    Current = Stage.PickFrom;
                    _prompt = "Select the prefab to be swapped";
                    VerdantsAscent.VaPieces.Log?.LogDebug("[Swapper] picking started: FROM");
                    return true;
                case Stage.PickFrom:
                case Stage.PickTo:
                    if (!IsSwappable(name))
                    {
                        Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, $"{Localize(piece.m_name)} can't be swapped (not a world prefab)");
                        return true;
                    }
                    Choose(new SwapPick(name));   // a plain left click: FROM = every copy, TO = no preset
                    return true;
                default:
                    return true;   // the card is up: a click behind it does nothing
            }
        }

        // 1.1.303: FROM or TO chosen, by a left click (no preset) or a preset picker row (prefab + preset).
        private static void Choose(SwapPick pick)
        {
            if (!InPick || pick == null) return;
            if (Current == Stage.PickFrom)
            {
                _from = pick;
                Current = Stage.PickTo;
                _prompt = $"Swap {pick.Text(false)}{(pick.HasPreset ? "" : " (any preset)")}\nSelect the prefab to swap it to";
                VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] FROM = {pick.LogText}; picking TO");
                return;
            }
            var from = _from;
            var to = pick;
            if (from == null) { Cancel("no FROM"); return; }
            if (from.SameAs(to))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, from.HasPreset
                    ? "That is the same prefab and preset: pick another prefab or preset (Shift + right click)"
                    : "That is the same prefab: pick another prefab, or Shift + right click it for a preset");
                return;
            }
            Current = Stage.Confirm;
            _prompt = null;
            VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] TO = {to.LogText}; confirm card");
            bool presets = from.HasPreset || to.HasPreset;
            string body = presets
                ? $"Swap {VARadiusSwapTool.FromText(from, to)} for {VARadiusSwapTool.ToText(from, to)}?"
                  + (string.Equals(from.Prefab, to.Prefab, StringComparison.Ordinal) ? "\nSame prefab: the preset is changed in place." : "")
                : $"Queue tool to swap {VARadiusSwapTool.DisplayName(from.Prefab)} for {VARadiusSwapTool.DisplayName(to.Prefab)}?";
            _card = VAKitOverlay.Confirm("Radius Swapper", body,
                () =>
                {
                    Reset();
                    VARadiusSwapTool.Queue(from, to);
                    VARadiusSwapTool.CloseMenuAndSelect();
                },
                () => Cancel("No"));
        }

        // 1.1.303 (Fire: "shift right click should still open the context menu on those prefabs"): while picking, FAP's build-menu
        // context menu on a piece (VAHammerRightClickPatches.ShowPieceMenu, any of its openers) shows this picker instead: the saved
        // presets that apply to that prefab (VASwapPresets.For), each "Sound: <name>" etc. A row locks (prefab, kind, preset) and
        // moves on exactly like a left click. True = the picker handled it.
        internal static bool TryShowPresetPicker(Piece piece)
        {
            if (!InPick || piece == null || piece.gameObject == null) return false;
            string name = piece.gameObject.name;
            if (VARadiusSwapTool.IsStandinName(name)) return false;   // the swapper itself: its own menu
            if (!VAVanillaPieceManager.IsAdminGateOpen()) return false;
            if (!IsSwappable(name))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, $"{Localize(piece.m_name)} can't be swapped (not a world prefab)");
                return true;
            }
            bool picking = Current == Stage.PickFrom;
            var presets = VASwapPresets.For(name);
            var items = new List<VARightClickMenu.Item>();
            string display = VARadiusSwapTool.DisplayName(name);
            items.Add(new VARightClickMenu.Item(picking ? $"{display} (any preset)" : $"{display} (no preset)", () => { _pickerOpen = false; Choose(new SwapPick(name)); }));
            if (presets.Count == 0)
                items.Add(new VARightClickMenu.Item("<color=#999>No saved presets for this prefab</color>", null, disabled: true));
            foreach (var p in presets)
            {
                var pick = p;
                bool same = !picking && _from != null && _from.SameAs(pick);
                items.Add(new VARightClickMenu.Item(pick.PresetLabel + (same ? "   <color=#999>(the FROM choice)</color>" : ""),
                    () => { _pickerOpen = false; Choose(pick); }, disabled: same));
            }
            VARightClickMenu.Show(items);
            _pickerOpen = VARightClickMenu.IsOpen;
            VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] preset picker on {name} ({(picking ? "FROM" : "TO")}): {presets.Count} preset(s)"
                + (presets.Count > 0 ? ": " + string.Join(", ", presets.Select(x => x.PresetLabel).ToArray()) : ""));
            return true;
        }

        internal static bool IsSwappable(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && ZNetScene.instance != null && ZNetScene.instance.GetPrefab(prefabName) != null;

        internal static void Cancel(string why)
        {
            if (!Busy) return;
            VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] picking cancelled ({why})");
            Reset();
        }

        private static void Reset()
        {
            Current = Stage.None;
            _from = null;
            if (_prompt != null) if (!s_p7ClosedLogged) { s_p7ClosedLogged = true; VaPieces.Log?.LogDebug("[FAP] VASwapFlow closed: OnGUI off until reopened"); }   // 1.1.325 (UI audit P7): OnGUI early-out (Update must keep ticking Swap Presets + picking)
            _prompt = null;
            if (_pickerOpen && VARightClickMenu.IsOpen) VARightClickMenu.Close();
            _pickerOpen = false;
            if (_card != null) { _card.gameObject.SetActive(false); Destroy(_card.gameObject); }
            _card = null;
        }

        private static string Localize(string s) => Localization.instance != null ? Localization.instance.Localize(s) : s;

        private void Update()
        {
            VASwapPresetUi.Tick();
            VASoundPresetUi.Tick();   // 1.1.295 (Sound Presets window: Esc closes)
            if (!Busy) { _pickerOpen = false; return; }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // 1.1.303: Esc with the preset picker up closes the picker only (VARightClickMenu may have closed it already this frame).
                if (_pickerOpen) { if (VARightClickMenu.IsOpen) VARightClickMenu.Close(); _pickerOpen = false; return; }
                Cancel("Esc");
                return;
            }
            if (_pickerOpen && !VARightClickMenu.IsOpen) _pickerOpen = false;
            if (!Hud.IsPieceSelectionVisible()) Cancel("build menu closed");
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_prompt) || Event.current.type != EventType.Repaint) return;
            if (_promptStyle == null)
                _promptStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 28, fontStyle = FontStyle.Bold };
            var rect = new Rect(0f, Screen.height * 0.5f - 60f, Screen.width, 120f);   // 1.1.303: two lines (the FROM choice + the ask)
            _promptStyle.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), _prompt, _promptStyle);
            _promptStyle.normal.textColor = new Color(0.94f, 0.71f, 0.30f, 1f);   // FiresTokens.Gold
            GUI.Label(rect, _prompt, _promptStyle);
        }
    }

    // FAP's own screen-space canvas for the kit popups, above the build menu (its GraphicRaycaster wins by sorting order).
    internal static class VAKitOverlay
    {
        private static RectTransform _root;

        internal static RectTransform Root
        {
            get
            {
                if (_root != null) return _root;
                var go = new GameObject("FAP_KitOverlay", typeof(RectTransform));
                UnityEngine.Object.DontDestroyOnLoad(go);
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 31000;
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
                go.AddComponent<GraphicRaycaster>();
                _root = (RectTransform)go.transform;
                return _root;
            }
        }

        // Kit confirm card (reused from FiresCompanions CompanionKitBits.Confirm): a scrim blocks the build menu behind it;
        // Yes = Primary, No / the scrim = cancel.
        internal static RectTransform Confirm(string title, string body, Action onYes, Action onNo)
        {
            var overlay = Root;
            var root = Widgets.Node(overlay, "FapSwapConfirm");
            Widgets.Stretch(root, 0, 0, 0, 0);
            root.SetAsLastSibling();
            bool done = false;
            void Close() { if (root != null) { root.gameObject.SetActive(false); UnityEngine.Object.Destroy(root.gameObject); } }
            void Yes() { if (done) return; done = true; Close(); try { onYes?.Invoke(); } catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: swap confirm threw: {ex.Message}"); } }
            void No() { if (done) return; done = true; Close(); try { onNo?.Invoke(); } catch { } }

            var scrim = Widgets.Fill(root, "Scrim", FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.6f));
            Widgets.Stretch(scrim.rectTransform, 0, 0, 0, 0);
            ShellWidgets.Clickable(scrim.rectTransform, No);

            const float w = 520f;
            float pad = FiresTokens.S24, inner = w - pad * 2f;
            var card = Widgets.Node(root, "Card");
            var fill = Widgets.Box(card, "Fill", KitSprites.RoundPill(10f, false), FiresTokens.Panel);
            var frame = Widgets.Box(card, "Frame", KitSprites.RoundPill(10f, true), FiresTokens.Edge);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0);
            Widgets.Stretch(frame.rectTransform, 0, 0, 0, 0);
            fill.raycastTarget = true;
            VAPointerSink.On(fill.gameObject);   // 1.1.306: a press on the card never drags the build menu (the scrim is Clickable already)
            frame.raycastTarget = false;

            float cy = pad;
            var t = KitText.Create(card, "Title", title, FontRole.SerifBold, 22f, FiresTokens.Gold, KitAlign.Center);
            Widgets.Place(t.Rect, pad, cy, inner, 30f); cy += 38f;
            var b = KitText.Create(card, "Body", body, FontRole.SerifRegular, FiresTokens.BodySize, FiresTokens.Text, KitAlign.TopCenter, 0f, false, true);
            Widgets.Place(b.Rect, pad, cy, inner, 0f);
            float bh = Mathf.Ceil(b.PreferredHeight(inner));
            b.Rect.sizeDelta = new Vector2(inner, bh); cy += bh + FiresTokens.S24;

            const float bw = 150f, bhgt = 32f, gap = 16f;
            float bx = (w - bw * 2f - gap) * 0.5f;
            var yes = Widgets.Button(card, "yes", "Yes", ButtonKind.Primary, bx, cy, bw, bhgt);
            ShellWidgets.Clickable(yes, Yes);
            var no = Widgets.Button(card, "no", "No", ButtonKind.Primary, bx + bw + gap, cy, bw, bhgt);
            ShellWidgets.Clickable(no, No);
            cy += bhgt + pad;

            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
            card.anchoredPosition = Vector2.zero;
            card.sizeDelta = new Vector2(w, cy);
            return root;
        }

        // A kit text field (reused from Core ShellConfigModule.MakeInput, which is internal to Core).
        internal static TMP_InputField Input(RectTransform parent, string placeholder, float x, float y, float w, float h, Action<string> onChange)
        {
            var root = Widgets.Well(parent, "Input", x, y, w, h);
            var area = Widgets.Node(root, "TextArea");
            Widgets.Stretch(area, 10f, 4f, 10f, 4f);
            area.gameObject.AddComponent<RectMask2D>();
            var text = KitText.Create(area, "Text", "", FontRole.SansRegular, 14f, FiresTokens.Text, KitAlign.Left, 0f, false, false);
            Widgets.Stretch(text.Rect, 0, 0, 0, 0);
            var ph = KitText.Create(area, "Placeholder", placeholder, FontRole.SansRegular, 14f, FiresTokens.TextFaint, KitAlign.Left, 0f, false, false);
            Widgets.Stretch(ph.Rect, 0, 0, 0, 0);
            var input = root.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = area;
            input.textComponent = text.Tmp;
            input.placeholder = ph.Graphic;
            input.fontAsset = text.Tmp != null ? text.Tmp.font : null;
            input.caretColor = FiresTokens.Gold;
            input.customCaretColor = true;
            input.caretWidth = 2;
            input.caretBlinkRate = 0.85f;
            input.enabled = false;
            input.enabled = true;
            var bg = root.GetComponentInChildren<Image>();
            if (bg != null) { input.targetGraphic = bg; bg.raycastTarget = true; }
            if (onChange != null) input.onValueChanged.AddListener(s => onChange(s));
            return input;
        }

        // A clipped, wheel-scrolled list in a kit Well; returns the content node rows are placed into (top-left, y down).
        internal static RectTransform List(RectTransform parent, string name, float x, float y, float w, float h)
        {
            var well = Widgets.Well(parent, name, x, y, w, h);
            var bg = well.GetComponentInChildren<Image>();
            if (bg != null) bg.raycastTarget = true;
            var viewport = Widgets.Node(well, "Viewport");
            Widgets.Stretch(viewport, 4f, 4f, 4f, 4f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0f);
            var content = Widgets.Node(viewport, "Content");
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = new Vector2(0f, 0f);
            var scroll = well.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = FiresCore.UI.FiresUi.ScrollSensitivity;   // 1.1.294 (Fire: preset lists scrolled dead slow at 30): Core's shared Fires-list speed (vanilla Skills list x [UI] UI scroll speed)
            return content;
        }

        internal const float RowH = 26f;

        internal static void Rows(RectTransform content, float width, IList<(string label, bool selected, Action click)> rows)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var c = content.GetChild(i);
                c.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(c.gameObject);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var (label, selected, click) = rows[i];
                var row = Widgets.Node(content, "Row" + i);
                Widgets.Place(row, 0f, i * RowH, width, RowH - 2f);
                var bg = Widgets.Box(row, "Fill", KitSprites.RoundedFill(FiresTokens.RadiusSmall), selected ? FiresTokens.Selected : FiresTokens.WithAlpha(FiresTokens.PanelRaised, 0.35f));
                Widgets.Stretch(bg.rectTransform, 0, 0, 0, 0);
                bg.raycastTarget = true;
                var l = KitText.Create(row, "Label", label, FontRole.SansRegular, 14f, selected ? FiresTokens.GoldBright : FiresTokens.Text, KitAlign.Left, 0f, false, false);
                Widgets.Stretch(l.Rect, 8f, 0f, 6f, 0f);
                var act = click;
                ShellWidgets.Clickable(row, () => { try { act?.Invoke(); } catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: swap list row threw: {ex.Message}"); } });
            }
            content.sizeDelta = new Vector2(0f, rows.Count * RowH);
        }
    }

    // swap_presets.json in FAP's cache folder (<BepInEx>\cache\FiresAdminPrefabs, the team cache convention; never config).
    internal static class VASwapPresetStore
    {
        internal sealed class Preset { public string Name; public string From; public string To; }

        private static List<Preset> _presets;

        internal static string FilePath => Path.Combine(Path.GetFullPath(VAFapCachePaths.Root), "swap_presets.json");

        internal static List<Preset> All
        {
            get
            {
                if (_presets != null) return _presets;
                _presets = new List<Preset>();
                try
                {
                    string path = FilePath;
                    if (File.Exists(path))
                        _presets = JsonConvert.DeserializeObject<List<Preset>>(File.ReadAllText(path)) ?? new List<Preset>();
                    _presets.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Name));
                }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: swap presets did not load ({ex.Message}); starting empty"); }
                return _presets;
            }
        }

        internal static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(All, Formatting.Indented));
                VaPieces.Log?.LogInfo($"[Swapper] {All.Count} swap preset(s) saved to {path}");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: swap presets did not save: {ex.Message}"); }
        }

        internal static Preset Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // The kit preset window: FROM / TO fields (paste IDs) each with a searchable list of piece prefabs, the saved presets
    // (click = select / edit, New, Save under the typed name, Delete), Apply (close build menu, swapper in hand with the pair),
    // Close / the x / Esc.
    public static class VASwapPresetUi
    {
        private const float W = 960f, H = 620f, Pad = 24f, ColW = 290f, Gap = 21f, MaxRows = 150;

        private static RectTransform _root;
        private static TMP_InputField _from, _to, _name;
        private static RectTransform _fromList, _toList, _presetList;
        private static KitLabel _status;
        private static string _selected;
        private static List<(string id, string label, string search)> _prefabs;
        private static readonly object InputToken = new object();

        public static bool IsOpen => _root != null;

        public static void Open()
        {
            if (!VAVanillaPieceManager.IsAdminGateOpen()) return;
            if (IsOpen) Close();
            VASwapFlow.Cancel("preset window opened");
            BuildPrefabCache();
            var overlay = VAKitOverlay.Root;

            _root = Widgets.Node(overlay, "FapSwapPresets");
            Widgets.Stretch(_root, 0, 0, 0, 0);
            _root.SetAsLastSibling();
            var scrim = Widgets.Fill(_root, "Scrim", FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.5f));
            Widgets.Stretch(scrim.rectTransform, 0, 0, 0, 0);
            scrim.raycastTarget = true;   // blocks the build menu; a click outside the window does nothing
            VAPointerSink.On(scrim.gameObject);   // 1.1.306: Core's dock drag stands down for it (it was a plain Image)

            var win = Widgets.Node(_root, "Window");
            win.anchorMin = win.anchorMax = win.pivot = new Vector2(0.5f, 0.5f);
            win.anchoredPosition = Vector2.zero;
            win.sizeDelta = new Vector2(W, H);
            var fill = Widgets.Box(win, "Fill", KitSprites.RoundPill(10f, false), FiresTokens.Panel);
            VAPointerSink.On(fill.gameObject);   // 1.1.306: a press on the window's own background never drags the build menu
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0);
            fill.raycastTarget = true;
            var frame = Widgets.Box(win, "Frame", KitSprites.RoundPill(10f, true), FiresTokens.Edge);
            Widgets.Stretch(frame.rectTransform, 0, 0, 0, 0);
            frame.raycastTarget = false;

            var title = KitText.Create(win, "Title", "SWAP PRESETS", FontRole.SerifBold, 22f, FiresTokens.Gold, KitAlign.Center);
            Widgets.Place(title.Rect, Pad, 18f, W - Pad * 2f, 30f);
            var x = Widgets.Close(win);
            ShellWidgets.Clickable(x.Rect, Close);
            x.Graphic.raycastTarget = true;

            float c1 = Pad, c2 = Pad + ColW + Gap, c3 = Pad + (ColW + Gap) * 2f;
            Caption(win, "From (prefab ID)", c1, 64f);
            _from = VAKitOverlay.Input(win, "Paste or search an ID...", c1, 88f, ColW, 32f, _ => RefreshPrefabList(_from, _fromList));
            _fromList = VAKitOverlay.List(win, "FromList", c1, 128f, ColW, 380f);
            Caption(win, "To (prefab ID)", c2, 64f);
            _to = VAKitOverlay.Input(win, "Paste or search an ID...", c2, 88f, ColW, 32f, _ => RefreshPrefabList(_to, _toList));
            _toList = VAKitOverlay.List(win, "ToList", c2, 128f, ColW, 380f);

            Caption(win, "Saved presets (click to edit)", c3, 64f);
            _presetList = VAKitOverlay.List(win, "PresetList", c3, 88f, ColW, 292f);
            Caption(win, "Preset name", c3, 390f);
            _name = VAKitOverlay.Input(win, "Name...", c3, 414f, ColW, 32f, null);
            float bw = (ColW - 20f) / 3f;
            Clickable(Widgets.Button(win, "new", "New", ButtonKind.Primary, c3, 456f, bw, 30f), NewPreset);
            Clickable(Widgets.Button(win, "save", "Save", ButtonKind.Primary, c3 + bw + 10f, 456f, bw, 30f), SavePreset);
            Clickable(Widgets.Button(win, "delete", "Delete", ButtonKind.Destructive, c3 + (bw + 10f) * 2f, 456f, bw, 30f), DeletePreset);

            _status = KitText.Create(win, "Status", "", FontRole.SansRegular, 14f, FiresTokens.TextMuted, KitAlign.Left, 0f, false, true);
            Widgets.Place(_status.Rect, Pad, 526f, W - Pad * 2f - 340f, 40f);
            Clickable(Widgets.Button(win, "close", "Close", ButtonKind.Primary, W - Pad - 150f, H - Pad - 34f, 150f, 34f), Close);
            Clickable(Widgets.Button(win, "apply", "Apply", ButtonKind.Primary, W - Pad - 150f - 16f - 150f, H - Pad - 34f, 150f, 34f), Apply);

            if (VARadiusSwapTool.HasPair) { _from.text = VARadiusSwapTool.FromName; _to.text = VARadiusSwapTool.ToName; }
            RefreshPrefabList(_from, _fromList);
            RefreshPrefabList(_to, _toList);
            RefreshPresets();
            FiresCore.Input.FiresInputBlock.Acquire(InputToken);
            VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] preset window opened ({VASwapPresetStore.All.Count} preset(s), {_prefabs.Count} piece prefabs)");
        }

        public static void Close()
        {
            if (_root == null) return;
            _root.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
            _from = _to = _name = null;
            FiresCore.Input.FiresInputBlock.Release(InputToken);
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (events != null) events.SetSelectedGameObject(null);
            VerdantsAscent.VaPieces.Log?.LogDebug("[Swapper] preset window closed");
        }

        internal static void Tick()
        {
            if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        private static void Clickable(RectTransform rt, Action a) => ShellWidgets.Clickable(rt, a);

        private static void Caption(RectTransform parent, string text, float x, float y)
        {
            var l = KitText.Create(parent, "Caption", text, FontRole.SansRegular, 14f, FiresTokens.TextMuted, KitAlign.Left);
            Widgets.Place(l.Rect, x, y, ColW, 20f);
        }

        private static void Status(string text, bool bad = false)
        {
            if (_status == null) return;
            _status.Value = text;
            _status.Color = bad ? FiresTokens.Danger : FiresTokens.TextMuted;
        }

        private static void BuildPrefabCache()
        {
            _prefabs = new List<(string, string, string)>();
            var scene = ZNetScene.instance;
            if (scene == null) return;
            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                var piece = go.GetComponent<Piece>();
                if (piece == null) continue;
                string loc = Localization.instance != null ? Localization.instance.Localize(piece.m_name) : piece.m_name;
                string label = string.IsNullOrEmpty(loc) || loc == go.name ? go.name : $"{loc}  ({go.name})";
                _prefabs.Add((go.name, label, (go.name + " " + loc).ToLowerInvariant()));
            }
            _prefabs.Sort((a, b) => string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase));
        }

        private static void RefreshPrefabList(TMP_InputField field, RectTransform list)
        {
            if (field == null || list == null || _prefabs == null) return;
            string q = (field.text ?? "").Trim().ToLowerInvariant();
            var rows = new List<(string, bool, Action)>();
            foreach (var p in _prefabs)
            {
                if (q.Length > 0 && !p.search.Contains(q)) continue;
                string id = p.id;
                rows.Add((p.label, string.Equals(id, field.text, StringComparison.Ordinal), () => { field.text = id; }));
                if (rows.Count >= MaxRows) break;
            }
            VAKitOverlay.Rows(list, ColW - 8f, rows);
        }

        private static void RefreshPresets()
        {
            var rows = new List<(string, bool, Action)>();
            foreach (var p in VASwapPresetStore.All)
            {
                var preset = p;
                rows.Add(($"{p.Name}:  {p.From} → {p.To}", string.Equals(p.Name, _selected, StringComparison.OrdinalIgnoreCase), () => Select(preset)));
            }
            VAKitOverlay.Rows(_presetList, ColW - 8f, rows);
        }

        private static void Select(VASwapPresetStore.Preset p)
        {
            _selected = p.Name;
            _name.text = p.Name;
            _from.text = p.From;
            _to.text = p.To;
            RefreshPresets();
            Status($"Editing {p.Name}: change the fields and Save, or Apply.");
        }

        private static void NewPreset()
        {
            _selected = null;
            _name.text = "";
            _from.text = "";
            _to.text = "";
            RefreshPresets();
            Status("New preset: fill From, To and a name, then Save.");
        }

        private static bool ReadPair(out string from, out string to)
        {
            from = (_from.text ?? "").Trim();
            to = (_to.text ?? "").Trim();
            if (from.Length == 0 || to.Length == 0) { Status("Fill both From and To.", true); return false; }
            if (!VASwapFlow.IsSwappable(from)) { Status($"There is no prefab named {from}.", true); return false; }
            if (!VASwapFlow.IsSwappable(to)) { Status($"There is no prefab named {to}.", true); return false; }
            if (ZNetScene.instance.GetPrefab(to).GetComponent<Piece>() == null) { Status($"{to} is not a build piece.", true); return false; }
            if (from == to) { Status("From and To are the same prefab.", true); return false; }
            return true;
        }

        private static void SavePreset()
        {
            string name = (_name.text ?? "").Trim();
            if (name.Length == 0) { Status("Type a preset name first.", true); return; }
            if (!ReadPair(out string from, out string to)) return;
            var existing = VASwapPresetStore.Find(_selected ?? name) ?? VASwapPresetStore.Find(name);
            if (existing == null) { existing = new VASwapPresetStore.Preset(); VASwapPresetStore.All.Add(existing); }
            existing.Name = name; existing.From = from; existing.To = to;
            VASwapPresetStore.Save();
            _selected = name;
            RefreshPresets();
            Status($"Saved {name}.");
        }

        private static void DeletePreset()
        {
            var p = _selected != null ? VASwapPresetStore.Find(_selected) : null;
            if (p == null) { Status("Click a preset to delete first.", true); return; }
            VASwapPresetStore.All.Remove(p);
            VASwapPresetStore.Save();
            NewPreset();
            Status($"Deleted {p.Name}.");
        }

        private static void Apply()
        {
            if (!ReadPair(out string from, out string to)) return;
            Close();
            VARadiusSwapTool.Queue(from, to);
            VARadiusSwapTool.CloseMenuAndSelect();
        }
    }

    // 1.1.291 hooks for the flow above.
    [HarmonyPatch]
    public static class VASwapUiPatches
    {
        // A build-menu piece click while the swapper owns it: no selection, the menu stays open.
        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OnSelectPiece)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static bool OnSelectPiecePrefix(Piece piece)
        {
            if (VASwapPresetUi.IsOpen || VASoundPresetUi.IsOpen) return false;   // 1.1.295: + Sound Presets window
            return !VASwapFlow.InterceptSelect(piece);
        }

        // Vanilla's build-menu input (right click / Esc close the menu) stands down while picking, the card or the preset
        // window is up; Esc is FAP's then (VASwapFlow.Update / VASwapPresetUi.Tick).
        [HarmonyPatch(typeof(BuildUi), "NavigationUpdate"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static bool NavigationUpdatePrefix()
        {
            // 1.1.303 (Fire: "shift right click should still open the context menu on those prefabs"): vanilla's input stands down
            // while picking, so FAP's [BuildMenu] ContextMenuKey path (VABuildUiContextMenu, also a NavigationUpdate prefix) is run
            // from here too; once per frame (its own guard), and ShowPieceMenu turns it into the swapper's preset picker.
            if (VASwapFlow.InPick && !VASwapPresetUi.IsOpen && !VASoundPresetUi.IsOpen && !FiresCore.Input.FiresInputBlock.IsCapturing)
            {
                try { VerdantsAscent.Patches.VABuildUiContextMenu.TryOpenForButtonUnderPointer(); }
                catch (Exception ex) { VaPieces.Log?.LogDebug($"[Swapper] context key while picking: {ex.Message}"); }
            }
            return !(VASwapFlow.Busy || VASwapPresetUi.IsOpen || VASoundPresetUi.IsOpen);   // 1.1.295: + Sound Presets window
        }

        // The build menu closing under the preset window closes it too (its Apply needs the menu's selection path).
        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.Close)), HarmonyPostfix]
        public static void ClosePostfix()
        {
            if (VASwapPresetUi.IsOpen && !Hud.IsPieceSelectionVisible()) VASwapPresetUi.Close();
            // 1.1.297: the Sound Presets window edits a placed piece now (world context menu), so the build menu closing no longer closes it.
        }

        // Camera zoom / ghost rotation stand down while a FAP radius tool is held - the wheel is radius / height
        // (the same patch as FiresAdminTerrain VoxelToolPieces Patch_ZInput_Scroll_VoxelTools / ZoneModeAreaTool.WantsWheel).
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel)), HarmonyPostfix]
        public static void ScrollPostfix(ref float __result)
        {
            if (__result == 0f || VARadiusWheel.ReadingRaw) return;
            if (VAClutterBrushIndicator.HeldTool() != VAClutterBrushIndicator.Tool.None) __result = 0f;
        }
    }
}
