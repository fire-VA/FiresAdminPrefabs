using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.UI;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.295 (Fire: Sound Presets): the window opened by "Edit sound..." on Core's world context menu for a placed piece.
    // 1.1.302 (Fire 10-21: "sound and light presets should look the same ... lets make the sound preset ui match our others"):
    // rebuilt in the light preset window's style (VALightConfigUI, REUSED: Core's rounded IMGUI skin, title row with Light switch /
    // Opacity / x, section bars, Core slim sliders + pill switches (CoreImguiWidgets), status line, footer buttons, corner grip;
    // layout + opacity saved in [SoundPresetUI]). Same behaviour as 1.1.297-1.1.301: templates fill the form, Save writes THIS piece
    // (ZDO fap_sound), Reset to vanilla fills vanilla values, Preview loops one source.
    public static class VASoundPresetUi
    {
        public static bool IsOpen => VASoundPresetWindow.Instance != null && VASoundPresetWindow.Instance.Open;

        public static void Open(ZNetView piece)
        {
            if (!VAVanillaPieceManager.IsAdminGateOpen()) return;
            if (piece == null || !piece.IsValid()) return;
            if (!VASoundPresets.HasSound(VASoundPresets.PrefabNameOf(piece))) return;
            if (IsOpen) Close();
            VASwapFlow.Cancel("sound preset window opened");
            VASoundPresetWindow.Ensure().OpenFor(piece);
        }

        public static void Close() { if (VASoundPresetWindow.Instance != null) VASoundPresetWindow.Instance.CloseWindow(); }

        internal static void Tick() { }   // 1.1.302: the window ticks itself (VASoundPresetWindow.Update); kept for VASwapUi's call
    }

    internal sealed class VASoundPresetWindow : MonoBehaviour
    {
        private const int WindowId = 0x7A53;
        private const float MinW = 520f, MaxW = 1400f, MinH = 560f, MaxH = 1300f, Grip = 18f;
        private static ConfigEntry<float> s_opacity, s_w, s_h, s_x, s_y;

        internal static VASoundPresetWindow Instance { get; private set; }
        private static bool s_p7ClosedLogged;   // 1.1.325 (UI audit P7)
        internal bool Open { get; private set; }

        private string _prefab, _template;
        private ZNetView _piece, _switchToLight;
        private List<VASoundPresets.Source> _sources;
        private Dictionary<string, VASoundPresets.SoundValues> _edit;
        private string _status = "";
        private bool _statusError, _resizing;
        private Rect _rect;
        private Vector2 _scroll;

        internal static VASoundPresetWindow Ensure()
        {
            if (Instance == null)
            {
                var go = new GameObject("FAP_SoundPresetUI");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<VASoundPresetWindow>();
                Instance.enabled = false;   // 1.1.325 (UI audit P7): starts closed; OpenFor turns it on
            }
            return Instance;
        }

        private static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("SoundPresetUI", "Opacity", 0.96f, new ConfigDescription("The sound preset window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_w = cfg.Bind("SoundPresetUI", "Width", 640f, "The sound preset window's width (drag the corner grip).");
            s_h = cfg.Bind("SoundPresetUI", "Height", 800f, "The sound preset window's height.");
            s_x = cfg.Bind("SoundPresetUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("SoundPresetUI", "Y", -1f, "The window's top edge (-1 = centred).");
            VerdantsAscent.Managers.FapConfigUi.Apply(cfg);   // window geometry is saved, never shown (as [LightPresetUI])
        }

        internal void OpenFor(ZNetView nv)
        {
            BindLayout();
            float w = Mathf.Clamp(s_w.Value, MinW, MaxW), h = Mathf.Clamp(s_h.Value, MinH, MaxH);
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
            _piece = nv;
            _prefab = VASoundPresets.PrefabNameOf(nv);
            VASoundPresets.PreviewAnchor = nv;
            _sources = VASoundPresets.Sources(_prefab);
            var own = VASoundPresets.ReadOverride(nv, _sources);
            _edit = new Dictionary<string, VASoundPresets.SoundValues>(StringComparer.Ordinal);
            foreach (var s in _sources)
                _edit[s.Key] = (own != null && own.TryGetValue(s.Key, out var v) ? v : s.Vanilla).Copy();
            _template = null;
            _clipLists.Clear(); _lightOf = null; _title = null; _names = null;   // 1.1.321: fresh caches per open
            _scroll = Vector2.zero;
            Status(HasOwn() ? OwnText : VanillaText);
            RestartPreview(_sources.Count > 0 ? _sources[0].Key : null);
            Open = true;
            enabled = true;   // 1.1.325 (UI audit P7)
            FiresCore.Input.FiresInputBlock.Acquire(this); VAImguiGuard.Open(this, () => _rect, "SoundPreset");
            VerdantsAscent.VaPieces.Log?.LogDebug($"[SoundPresets] window opened on {_prefab} ({_sources.Count} source(s)) - light-window style (1.1.302)");
        }

        internal void CloseWindow()
        {
            if (!Open) return;
            Open = false;
            VASoundPresets.StopPreview();   // 1.1.296 (SP1): the preview loop ends with the window
            VASoundPresets.PreviewAnchor = null;
            FiresCore.Input.FiresInputBlock.Release(this); VAImguiGuard.Close(this);
            VAScreenHold.Release(this);
            if (s_w != null) { s_w.Value = _rect.width; s_h.Value = _rect.height; s_x.Value = _rect.x; s_y.Value = _rect.y; }
            _prefab = _template = null; _piece = null; _sources = null; _edit = null; _clipLists.Clear(); _lightOf = null; _title = null;   // 1.1.321
            VerdantsAscent.VaPieces.Log?.LogDebug("[SoundPresets] window closed");
            if (_switchToLight == null) enabled = false;   // 1.1.325 (UI audit P7): off while closed (stays on while a Light switch is pending)
            if (!s_p7ClosedLogged) { s_p7ClosedLogged = true; VaPieces.Log?.LogDebug("[FAP] VASoundPresetWindow closed: OnGUI off until reopened"); }
        }

        private void Update()
        {
            if (_switchToLight != null) { var t = _switchToLight; _switchToLight = null; CloseWindow(); VALightConfigUI.OpenForPlaced(t); return; }
            if (!Open) return;
            VAScreenHold.Hold(this);   // free cursor + pinned camera while open (as the light window)
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (_piece == null || !_piece.IsValid()) { CloseWindow(); return; }   // the piece was removed
            if (ZInput.GetKeyDown(KeyCode.Escape)) CloseWindow();
        }

        private void OnGUI()
        {
            if (!Open) return;
            FiresRoundedSkin.Ensure(1f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, s_opacity.Value);
            _rect = GUI.Window(WindowId, _rect, DrawWindow, "", FiresRoundedSkin.Window);
            GUI.color = prev;
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - 80f));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - 60f));
            FiresRoundedSkin.DrawPendingTooltip();
        }

        private void DrawWindow(int id)
        {
            if (_sources == null || _edit == null) return;
            FiresRoundedSkin.ResetTooltip();
            GUILayout.BeginHorizontal();
            bool own = HasOwn();   // 1.1.321 (UI audit P6): the title is rebuilt only when it changes
            if (_title == null || own != _titleOwn) { _titleOwn = own; _title = $"Sound: this {_prefab}{(own ? " (own sound)" : "")}"; }
            GUILayout.Label(_title, FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            // 1.1.300: a piece with sound AND light switches to its light editor (the light window has the matching Sound button)
            // 1.1.321 (UI audit P4): HasLight walks the piece; looked up once per piece
            if (_piece != _lightOf) { _lightOf = _piece; _hasLight = _piece != null && _piece.IsValid() && VALightApplier.HasLight(_piece.gameObject); }
            if (_piece != null && _piece.IsValid() && _hasLight
                && GUILayout.Button("Light", FiresRoundedSkin.ButtonSmall, GUILayout.Width(70f)))
                _switchToLight = _piece;
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = CoreImguiWidgets.OpacitySlider(s_opacity.Value, 0.4f, 1f, 110f);
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) CloseWindow();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            if (!Open) return;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_rect.height - 190f));
            GUILayout.Label("Templates", FiresRoundedSkin.SectionBar);
            // 1.1.321 (UI audit P3): the sorted template list is rebuilt twice a second, not every IMGUI pass
            if (_names == null || Time.unscaledTime >= _namesAt) { _namesAt = Time.unscaledTime + 0.5f; _names = VASoundPresets.TemplateNames().OrderBy(n => n).ToList(); }
            var names = _names;
            if (names.Count > 0)
            {
                GUILayout.Label("Saved templates (click to fill in; Save applies them to this piece):", FiresRoundedSkin.Desc);
                foreach (var n in names)
                    if (GUILayout.Button(n, n == _template ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(26f))) ApplyTemplate(n);
            }
            else GUILayout.Label("No templates yet. Change values and press Save template to keep them.", FiresRoundedSkin.Desc);

            foreach (var s in _sources)
            {
                var v = _edit[s.Key];
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                GUILayout.Label(s.Label, FiresRoundedSkin.SectionBar);
                if (GUILayout.Button("Preview", FiresRoundedSkin.ButtonSmall, GUILayout.Width(80f))) RestartPreview(s.Key);   // 1.1.296 (SP1)
                GUILayout.EndHorizontal();
                // 1.1.296: ZSFX rewrites AudioSource volume / pitch every frame, so a ZSFX source shows its min/max ranges instead
                if (!v.HasZsfx)
                {
                    v.Volume = Slider("Volume", v.Volume, 0f, 1f, "0.00");
                    v.Pitch = Slider("Pitch", v.Pitch, 0f, 3f, "0.00");
                }
                v.SpatialBlend = Slider("Spatial blend (0 = 2D, 1 = 3D)", v.SpatialBlend, 0f, 1f, "0.00");
                v.MinDistance = Slider("Min distance", v.MinDistance, 0f, 100f, "0.0", 0.5f);
                v.MaxDistance = Slider("Max distance", v.MaxDistance, 0f, 500f, "0", 1f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Rolloff mode", FiresRoundedSkin.Label, GUILayout.Width(LabelW));
                int m = Mathf.Clamp(v.RolloffMode, 0, RolloffNames.Length - 1);
                if (GUILayout.Button(RolloffNames[m], FiresRoundedSkin.ButtonSmall, GUILayout.Width(140f))) { v.RolloffMode = (m + 1) % RolloffNames.Length; Edited(); }
                GUILayout.EndHorizontal();
                v.Loop = Toggle("Loop", v.Loop);
                v.Priority = Mathf.RoundToInt(Slider("Priority (0 = highest)", v.Priority, 0f, 256f, "0", 1f));
                v.DopplerLevel = Slider("Doppler level", v.DopplerLevel, 0f, 5f, "0.00");
                v.Spread = Slider("Spread (degrees)", v.Spread, 0f, 360f, "0", 1f);
                if (v.HasZsfx)
                {
                    if (s.ClipNames.Length > 1)
                    {
                        v.Clip = Mathf.RoundToInt(Slider("Clip", v.Clip, -1f, s.ClipNames.Length - 1, "0", 1f));
                        GUILayout.Label(ClipList(s), FiresRoundedSkin.Desc);   // 1.1.321 (P2): built once per source
                    }
                    v.MinPitch = Slider("Min pitch", v.MinPitch, 0f, 3f, "0.00");
                    v.MaxPitch = Slider("Max pitch", v.MaxPitch, 0f, 3f, "0.00");
                    v.MinVol = Slider("Min volume", v.MinVol, 0f, 1.5f, "0.00");
                    v.MaxVol = Slider("Max volume", v.MaxVol, 0f, 1.5f, "0.00");
                    v.RandomPan = Toggle("Random pan", v.RandomPan);
                    v.MinPan = Slider("Min pan", v.MinPan, -1f, 1f, "0.00");
                    v.MaxPan = Slider("Max pan", v.MaxPan, -1f, 1f, "0.00");
                    v.FadeIn = Slider("Fade in (s)", v.FadeIn, 0f, 10f, "0.00");
                    v.FadeOut = Slider("Fade out (s)", v.FadeOut, 0f, 10f, "0.00");
                    v.FadeOutDelay = Slider("Fade out delay (s)", v.FadeOutDelay, 0f, 30f, "0.0");
                    v.FadeOutOnAwake = Toggle("Fade out on awake", v.FadeOutOnAwake);
                    v.MinDelay = Slider("Min delay (s)", v.MinDelay, 0f, 10f, "0.00");
                    v.MaxDelay = Slider("Max delay (s)", v.MaxDelay, 0f, 10f, "0.00");
                    v.MaxConcurrent = Mathf.RoundToInt(Slider("Max concurrent (0 = no limit)", v.MaxConcurrent, 0f, 32f, "0", 1f));
                    v.IgnoreConcurrencyDistance = Toggle("Ignore close-by suppression", v.IgnoreConcurrencyDistance);
                    v.DistanceReverb = Toggle("Distance reverb", v.DistanceReverb);
                    v.PlayOnAwake = Toggle("Play on awake", v.PlayOnAwake);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_statusError ? $"<color=#F08080>{_status}</color>" : _status, FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save template", FiresRoundedSkin.Button, GUILayout.Height(32f))) SaveTemplate();
            if (GUILayout.Button("Apply to this piece", FiresRoundedSkin.Button, GUILayout.Height(32f))) SavePiece();
            if (GUILayout.Button("Reset to vanilla", FiresRoundedSkin.Button, GUILayout.Height(32f))) ResetToVanilla();
            if (GUILayout.Button("<color=#E8A0A0>Delete template</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) DeleteTemplate();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) CloseWindow();
            GUILayout.EndHorizontal();
            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 200f, 30f));
        }

        private const float LabelW = 230f;
        // 1.1.321 (UI audit P2/P3/P4/P6) caches
        private string _title; private bool _titleOwn;
        private ZNetView _lightOf; private bool _hasLight;
        private List<string> _names; private float _namesAt;
        private readonly Dictionary<VASoundPresets.Source, string> _clipLists = new Dictionary<VASoundPresets.Source, string>();
        private static readonly Dictionary<string, Dictionary<float, string>> s_fmt = new Dictionary<string, Dictionary<float, string>>();

        private string ClipList(VASoundPresets.Source s)
        {
            if (!_clipLists.TryGetValue(s, out string t)) _clipLists[s] = t = $"-1 = random of {s.ClipNames.Length}: " + string.Join(", ", s.ClipNames.Select((c, i) => i + " " + c));
            return t;
        }

        // A slider's value text, formatted once per value (bounded: a format's cache is dropped past 4096 values)
        internal static string Fmt(float v, string format)
        {
            if (!s_fmt.TryGetValue(format, out var byValue)) s_fmt[format] = byValue = new Dictionary<float, string>();
            if (byValue.TryGetValue(v, out string t)) return t;
            if (byValue.Count > 4096) byValue.Clear();
            return byValue[v] = v.ToString(format);
        }
        private static readonly string[] RolloffNames = { "Logarithmic", "Linear", "Custom" };

        // Core slim slider (as the light window's Hue / Saturation rows); old FAP slider on an older Core
        private float Slider(string label, float v, float min, float max, string format, float step = 0f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(LabelW));
            float c = Mathf.Clamp(v, min, max), n;
            if (CoreImguiWidgets.HasSlider) n = CoreImguiWidgets.Slider(c, min, max, Fmt(c, format), 260f);
            else n = GUILayout.HorizontalSlider(c, min, max, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(260f));
            GUILayout.EndHorizontal();
            if (step > 0f) n = Mathf.Round(n / step) * step;
            if (!Mathf.Approximately(n, c)) { Edited(); return n; }
            return v;
        }

        // Core pill switch (the config window's toggle); an ON/OFF button on an older Core
        private bool Toggle(string label, bool on)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(LabelW));
            bool n = CoreImguiWidgets.HasPill ? CoreImguiWidgets.Pill(on)
                : GUILayout.Button(on ? "ON" : "OFF", on ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Width(80f), GUILayout.Height(24f)) ? !on : on;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (n != on) Edited();
            return n;
        }

        private void HandleResizeGrip()
        {
            var grip = new Rect(_rect.width - Grip - 4f, _rect.height - Grip - 4f, Grip, Grip);
            GUI.Label(grip, "//", FiresRoundedSkin.Hint);
            var e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { _resizing = true; e.Use(); }
            else if (_resizing && e.type == EventType.MouseDrag)
            {
                _rect.width = Mathf.Clamp(_rect.width + e.delta.x, MinW, MaxW);
                _rect.height = Mathf.Clamp(_rect.height + e.delta.y, MinH, MaxH);
                e.Use();
            }
            else if (_resizing && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)) _resizing = false;
        }

        private const string VanillaText = "Vanilla values. Change them and Apply to this piece.";
        private const string OwnText = "This piece has its own sound. Apply to update, Reset to vanilla to clear.";

        private bool HasOwn() => _piece != null && _piece.IsValid() && VASoundPresets.ReadOverrideJson(_piece).Length > 0;
        private void Status(string msg, bool error = false) { _status = msg ?? ""; _statusError = error; }
        private void Edited() => Status("Edited (not applied). Apply to this piece saves it; Reset to vanilla undoes it.");

        private void ApplyTemplate(string name)
        {
            var t = VASoundPresets.Template(name);
            if (t == null || _sources == null) return;
            int hit = 0;
            foreach (var s in _sources)
            {
                if (t.TryGetValue(s.Key, out var v) && v != null) { _edit[s.Key] = v.Copy(); hit++; }
                else _edit[s.Key] = s.Vanilla.Copy();
            }
            _template = name;
            RestartPreview(VASoundPresets.PreviewKey);
            Status(hit > 0 ? $"Template '{name}' filled in ({hit} source(s)). Apply to this piece to keep it."
                           : $"Template '{name}' has no source of {_prefab}; vanilla values filled in.", hit == 0);
        }

        // 1.1.296 (SP1): one looping preview, the given section's (none = stopped)
        private void RestartPreview(string key)
        {
            var s = key != null && _sources != null ? _sources.FirstOrDefault(x => x.Key == key) : null;
            if (s == null || _edit == null || !_edit.TryGetValue(s.Key, out var v)) { VASoundPresets.StopPreview(); return; }
            VASoundPresets.Preview(_prefab, s, v);
        }

        private void ResetToVanilla()
        {
            foreach (var s in _sources) _edit[s.Key] = s.Vanilla.Copy();
            _template = null;
            RestartPreview(VASoundPresets.PreviewKey);
            Status(HasOwn() ? "Vanilla values filled in. Apply clears this piece's own sound." : VanillaText);
        }

        // Apply = write to THIS piece's ZDO (all vanilla = the key is cleared)
        private void SavePiece()
        {
            if (_piece == null || !_piece.IsValid()) { Status("The piece is gone.", true); return; }
            bool ok = VASoundPresets.SaveToPiece(_piece, _edit, out string msg, _template);   // 1.1.303: + the template name stamp
            Status(ok ? (HasOwn() ? msg + " " + OwnText : msg + " " + VanillaText) : msg, !ok);
        }

        private void SaveTemplate()
        {
            if (_prefab == null) return;
            var changed = _sources.Where(s => JsonDiff(_edit[s.Key], s.Vanilla)).ToDictionary(s => s.Key, s => _edit[s.Key], StringComparer.Ordinal);
            if (changed.Count == 0) { Status("Every value is vanilla: nothing to keep as a template.", true); return; }
            string name = _template ?? VASoundPresets.NextTemplateName(_prefab);
            bool ok = VASoundPresets.SaveTemplate(name, changed, out string msg);
            if (ok) _template = name;
            Status(msg, !ok);
        }

        private void DeleteTemplate()
        {
            if (_template == null || VASoundPresets.Template(_template) == null) { Status("Click a template in the list first.", true); return; }
            bool ok = VASoundPresets.SaveTemplate(_template, null, out string msg);
            if (ok) _template = null;
            Status(msg + " (This piece keeps its values until you Apply.)", !ok);
        }

        private static bool JsonDiff(VASoundPresets.SoundValues a, VASoundPresets.SoundValues b)
            => Newtonsoft.Json.JsonConvert.SerializeObject(a) != Newtonsoft.Json.JsonConvert.SerializeObject(b);
    }

    // 1.1.297 (Fire: per placed piece): "Edit sound..." on FAP's world right-click menu for the placed piece under the cursor.
    // Reused from VAHopperContextProvider (Core FiresContextMenu provider, ContextTarget.Find, admin gate).
    internal sealed class VASoundContextProvider : FiresCore.UI.ContextMenu.IContextMenuProvider
    {
        public string TitleFor(FiresCore.UI.ContextMenu.ContextTarget target) => null;

        public IEnumerable<FiresCore.UI.ContextMenu.ContextMenuItem> GetItems(FiresCore.UI.ContextMenu.ContextTarget target)
        {
            if (!VAVanillaPieceManager.IsAdminGateOpen()) return null;
            var piece = target?.Find<Piece>();
            var nv = piece != null ? piece.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid()) return null;
            // 1.1.300 (Fire 10-20: "both lighting and sound presets should be our core context menu"): the light editor for a placed
            // piece lives here next to the sound one (it was Shift+RMB only); a piece with both shows both, and each window can switch.
            var items = new List<FiresCore.UI.ContextMenu.ContextMenuItem>();
            if (VASoundPresets.HasSound(VASoundPresets.PrefabNameOf(nv)))
            {
                bool own = VASoundPresets.ReadOverrideJson(nv).Length > 0;
                items.Add(FiresCore.UI.ContextMenu.ContextMenuItem.Row(own ? "Edit sound... (own sound)" : "Edit sound...", () => VASoundPresetUi.Open(nv)));
            }
            if (VALightApplier.HasLight(nv.gameObject))
                items.Add(FiresCore.UI.ContextMenu.ContextMenuItem.Row("Edit light...", () => VALightConfigUI.OpenForPlaced(nv)));
            return items.Count > 0 ? items : null;
        }
    }
}
