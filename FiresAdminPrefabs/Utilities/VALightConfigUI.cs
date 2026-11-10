using System;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.UI;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    [HarmonyPatch(typeof(Player), "TakeInput")]
    public static class Player_TakeInput_VALightConfigUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result) { if (VALightConfigUI.IsOpen) __result = false; }
    }

    // 1.1.215 (Fire 10-12): the light preset editor (VALightPresets.cs). Two ways in:
    //   OpenForPrefab(prefab)  from the Hammer right-click menu: Apply queues the preset for the next placements of that piece.
    //   OpenForPlaced(view)    from Shift+RMB on a placed light (admins): Apply writes that light's ZDO; every peer re-applies.
    // Two groups: "Vanilla clients see these" (flicker strength / speed / jitter, light + shadow distance: vanilla's own
    // LoadFields applies them) and "FAP clients only" (colour, intensity, range, shadows, flicker on a steady light, flame tint,
    // glow). With VanillaFriendly ON the FAP-only group is hidden and kept at the light's own values (Fire 10-12).
    // Drawn in Core's rounded skin like the item stand editor; layout + opacity saved in [LightPresetUI].
    public class VALightConfigUI : MonoBehaviour
    {
        private const int WindowId = 0x7A52;
        private const float MinW = 480f, MaxW = 1200f, MinH = 560f, MaxH = 1100f, Grip = 18f;

        private static VALightConfigUI _instance;
        private static bool s_p7ClosedLogged;   // 1.1.325 (UI audit P7)
        private static ConfigEntry<float> s_opacity, s_w, s_h, s_x, s_y;

        public static bool IsOpen => _instance != null && _instance._open;

        public static void OpenForPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            Ensure().OpenInternal(prefabName, null, FromPrefab(prefab));
        }

        public static void OpenForPlaced(ZNetView view)
        {
            if (view == null || !view.IsValid()) return;
            var state = view.GetComponent<VALightState>() ?? view.gameObject.AddComponent<VALightState>();
            Ensure().OpenInternal(Utils.GetPrefabName(view.gameObject), view, state.Current());
        }

        private static VALightConfigUI Ensure()
        {
            if (_instance == null)
            {
                var go = new GameObject("FAP_LightConfigUI");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<VALightConfigUI>();
            }
            return _instance;
        }

        // The prefab's own values (the Hammer path has no placed piece to read).
        private static VALightPreset FromPrefab(GameObject prefab)
        {
            var p = new VALightPreset();
            if (prefab == null) return p;
            var l = prefab.GetComponentInChildren<Light>(true);
            var f = prefab.GetComponentInChildren<LightFlicker>(true);
            var lod = prefab.GetComponentInChildren<LightLod>(true);
            if (l != null) { p.Color = ColorUtility.ToHtmlStringRGB(l.color); p.Intensity = l.intensity; p.Range = l.range; p.Shadows = l.shadows != LightShadows.None; }
            p.Flicker = f != null;
            if (f != null) { p.FlickerAmount = f.m_flickerIntensity; p.FlickerSpeed = f.m_flickerSpeed; p.FlickerMovement = f.m_movement; }
            if (lod != null) { p.LightDistance = lod.m_lightDistance; p.ShadowDistance = lod.m_shadowDistance; }
            var fire = prefab.GetComponent<Fireplace>();
            if (fire != null) { p.HasFire = true; p.InfiniteFuel = fire.m_infiniteFuel; p.MaxFuel = fire.m_maxFuel; p.SecPerFuel = fire.m_secPerFuel; p.CanTurnOff = fire.m_canTurnOff; }
            return p;
        }

        private bool _open, _vanillaFriendly;
        private string _prefab;
        private ZNetView _target;
        private VALightPreset _own;   // the light's own values: what the hidden FAP-only group keeps with VanillaFriendly ON
        private string _name = "", _color = "", _intensity = "", _range = "", _amount = "", _speed = "", _movement = "", _lightDist = "", _shadowDist = "", _fade = "", _fadeIn = "", _ttl = "", _note = "", _flame = "", _emissive = "";
        private bool _lodOn = true, _shadowLodOn = true;   // 1.1.262
        private bool _shadows, _flicker, _infinite, _turnOff;
        private string _maxFuel = "", _secPerFuel = "";
        private string _status = "";
        private bool _statusError;
        private Rect _rect;
        private Vector2 _scroll;
        private bool _resizing;

        internal static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("LightPresetUI", "Opacity", 0.96f, new ConfigDescription("The light preset window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_w = cfg.Bind("LightPresetUI", "Width", 600f, "The light preset window's width (drag the corner grip).");
            s_h = cfg.Bind("LightPresetUI", "Height", 760f, "The light preset window's height.");
            s_x = cfg.Bind("LightPresetUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("LightPresetUI", "Y", -1f, "The window's top edge (-1 = centred).");
            VerdantsAscent.Managers.FapConfigUi.Apply(cfg);   // 1.1.279: window geometry is saved, never shown
        }

        private void OpenInternal(string prefab, ZNetView target, VALightPreset start)
        {
            BindLayout();
            _prefab = prefab; _target = target; _own = Copy(start);
            _vanillaFriendly = ConfigManager.Instance?.configVanillaFriendly?.Value ?? false;
            float w = Mathf.Clamp(s_w.Value, MinW, MaxW), h = Mathf.Clamp(s_h.Value, MinH, MaxH);
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
            Load(start);
            Status(target != null ? $"Editing this placed {prefab} live (everyone sees it)." : $"Apply queues the preset for your next {prefab} placements.");
            _open = true;
            enabled = true;   // 1.1.325 (UI audit P7)
            FiresCore.Input.FiresInputBlock.Acquire(this); VAImguiGuard.Open(this, () => _rect, "LightPreset");
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            FiresCore.Input.FiresInputBlock.Release(this); VAImguiGuard.Close(this);
            VAScreenHold.Release(this);   // 1.1.301
            if (s_w != null) { s_w.Value = _rect.width; s_h.Value = _rect.height; s_x.Value = _rect.x; s_y.Value = _rect.y; }
            if (_switchToSound == null) enabled = false;   // 1.1.325 (UI audit P7): off while closed (stays on while a Sound switch is pending)
            if (!s_p7ClosedLogged) { s_p7ClosedLogged = true; VaPieces.Log?.LogDebug("[FAP] VALightConfigUI closed: OnGUI off until reopened"); }
        }

        private ZNetView _switchToSound;   // 1.1.300: set by the Sound button, acted on here (never mid-OnGUI)

        private void Update()
        {
            if (_switchToSound != null) { var t = _switchToSound; _switchToSound = null; Close(); VASoundPresetUi.Open(t); return; }
            if (!_open) return;
            VAScreenHold.Hold(this);   // 1.1.301: free cursor + pinned camera while open (Core InputBlock, as hold-Shift+Alt)
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (ZInput.GetKeyDown(KeyCode.Escape)) Close();
        }

        private void OnGUI()
        {
            if (!_open) return;
            FiresRoundedSkin.Ensure(1f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, s_opacity.Value);
            _rect = GUI.Window(WindowId, _rect, DrawWindow, "", FiresRoundedSkin.Window);
            GUI.color = prev;
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - 80f));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - 60f));
            FiresRoundedSkin.DrawPendingTooltip();
        }

        private string _title, _titleOf; private bool _titleTarget;   // 1.1.321 (UI audit P3/P6)
        private System.Collections.Generic.List<string> _saved; private float _savedAt;

        private void DrawWindow(int id)
        {
            FiresRoundedSkin.ResetTooltip();
            GUILayout.BeginHorizontal();
            if (_title == null || _titleOf != _prefab || _titleTarget != (_target != null)) { _titleOf = _prefab; _titleTarget = _target != null; _title = _titleTarget ? $"Light: this {_prefab}" : $"Light preset: {_prefab}"; }   // 1.1.321 (UI audit P6)
            GUILayout.Label(_title, FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            // 1.1.300 (Fire 10-20): a placed piece with light AND sound switches to its sound editor
            if (_target != null && _target.IsValid() && VASoundPresets.HasSound(VASoundPresets.PrefabNameOf(_target))
                && GUILayout.Button("Sound", FiresRoundedSkin.ButtonSmall, GUILayout.Width(70f)))
                _switchToSound = _target;
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = CoreImguiWidgets.OpacitySlider(s_opacity.Value, 0.4f, 1f, 110f);   // 1.1.293: Core slim slider, old slider on older Core
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) Close();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_rect.height - 190f));
            GUILayout.Label("Preset", FiresRoundedSkin.SectionBar);
            _name = Row("Preset name", _name, "A name to save this look under and reuse it.");
            if (_saved == null || Time.unscaledTime >= _savedAt) { _savedAt = Time.unscaledTime + 0.5f; _saved = VALightPresetStore.All.Select(p => p.name).OrderBy(n => n).ToList(); }   // 1.1.321 (UI audit P3): twice a second
            var saved = _saved;
            if (saved.Count > 0)
            {
                GUILayout.Label("Saved presets (click to load):", FiresRoundedSkin.Desc);
                foreach (var n in saved)
                    if (GUILayout.Button(n, n == _name ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(26f)) && VALightPresetStore.TryGet(n, out var sp)) Load(sp);
            }
            if (!string.IsNullOrEmpty(_note)) GUILayout.Label(_note, FiresRoundedSkin.Desc);   // 1.1.262: built-ins explain themselves

            GUILayout.Space(6f);
            GUILayout.Label("Vanilla clients see these", FiresRoundedSkin.SectionBar);
            _amount = Row("Flicker strength", _amount, "How much the light's brightness flickers (vanilla torches: about 0.1-0.3). Lights that already flicker.");
            _speed = Row("Flicker speed", _speed, "How fast it flickers (vanilla: about 10).");
            _movement = Row("Flicker jitter", _movement, "How far the light jumps around while flickering (0 = still).");
            // 1.1.262: vanilla freezes the flicker at a RANDOM point per lantern per load when speed is 0
            if (Flt(_speed) <= 0f && Flt(_amount) > 0f)
                GUILayout.Label("<color=#F0C060>Speed 0 with strength above 0: vanilla freezes the flicker at a RANDOM point for each lantern, every load. "
                    + "Brightness then ranges from dark to about 2.2x and changes on reload. Use strength 0 for a steady light.</color>", FiresRoundedSkin.Desc);
            _lightDist = Row("Light visible up to (m)", _lightDist, "Past this distance the light switches off (vanilla: 40; 200+ for a far-visible light).");
            _shadowDist = Row("Shadows up to (m)", _shadowDist, "Past this distance it stops casting shadows (vanilla: 20; raise it to see shadows from far).");
            if (GUILayout.Button(_lodOn ? "Switch off by distance: ON (vanilla)" : "Switch off by distance: OFF (always on)", _lodOn ? FiresRoundedSkin.NavItem : FiresRoundedSkin.NavSel, GUILayout.Height(26f))) _lodOn = !_lodOn;
            if (GUILayout.Button(_shadowLodOn ? "Drop shadows by distance: ON (vanilla)" : "Drop shadows by distance: OFF (always)", _shadowLodOn ? FiresRoundedSkin.NavItem : FiresRoundedSkin.NavSel, GUILayout.Height(26f))) _shadowLodOn = !_shadowLodOn;
            _fade = Row("Fade-out time (s)", _fade, "LightFlicker.m_fadeDuration; blank = the light's own. Only matters with a lifetime (ttl).");
            _fadeIn = Row("Fade-in time (s)", _fadeIn, "Brightens over this long after loading; 0 = full at once; blank = the light's own.");
            _ttl = Row("Lifetime (s)", _ttl, "The light destroys itself after this long; 0 = never; blank = the light's own.");
            if (_own.HasFire)
            {
                if (GUILayout.Button(_infinite ? "Infinite fuel: ON (never goes out)" : "Infinite fuel: OFF (burns its fuel)", _infinite ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(28f))) _infinite = !_infinite;
                if (!_infinite)
                {
                    _maxFuel = Row("Max fuel", _maxFuel, "How much fuel it holds.");
                    _secPerFuel = Row("Burn rate (s per fuel)", _secPerFuel, "Seconds one fuel lasts; higher = burns longer.");
                }
                if (GUILayout.Button(_turnOff ? "Can be turned off: YES" : "Can be turned off: NO", _turnOff ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(28f))) _turnOff = !_turnOff;
            }

            if (!_vanillaFriendly)
            {
                GUILayout.Space(6f);
                GUILayout.Label("FAP clients only (players without FAP see the vanilla light)", FiresRoundedSkin.SectionBar);
                _color = Row("Light colour (hex)", _color, "RRGGBB, e.g. FFB35C warm, 7FB8FF cold blue, 9CFF7A green.");
                if (VALightApplier.TryColor(_color, out var c))
                {
                    Color.RGBToHSV(c, out float hue, out float sat, out float val);
                    float nh = Slider("Hue", hue, 0f, 1f), ns = Slider("Saturation", sat, 0f, 1f), nv = Slider("Value", val, 0f, 1f);
                    if (!Mathf.Approximately(nh, hue) || !Mathf.Approximately(ns, sat) || !Mathf.Approximately(nv, val))
                        _color = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(nh, ns, nv));
                }
                _intensity = Row("Intensity", _intensity, "Brightness (vanilla torch about 1-2).");
                _range = Row("Range (m)", _range, "How far the light reaches.");
                if (GUILayout.Button(_shadows ? "Shadows: ON" : "Shadows: OFF", _shadows ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(28f))) _shadows = !_shadows;
                if (GUILayout.Button(_flicker ? "Flicker: ON (added on a steady light)" : "Flicker: OFF", _flicker ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(28f))) _flicker = !_flicker;
                _flame = Row("Flame / particle tint (hex)", _flame, "Blank = the prefab's own flame colours.");
                _emissive = Row("Glow colour (hex)", _emissive, "The glowing material's colour. Blank = the prefab's own.");
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_statusError ? $"<color=#F08080>{_status}</color>" : _status, FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save preset", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnSave();
            if (GUILayout.Button(_target != null ? "Apply to this light" : "Apply (queue)", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnApply();
            if (_target != null && GUILayout.Button("Reset to vanilla", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnReset();
            if (GUILayout.Button("<color=#E8A0A0>Delete preset</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnDelete();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) Close();
            GUILayout.EndHorizontal();
            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 200f, 30f));
        }

        private static string Row(string label, string value, string hint)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(210f));
            value = GUILayout.TextField(value ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            GUILayout.Label(hint, FiresRoundedSkin.Desc);
            return value;
        }

        private static float Slider(string label, float v, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(210f));
            if (CoreImguiWidgets.HasSlider) v = CoreImguiWidgets.Slider(v, min, max, VASoundPresetWindow.Fmt(v, "0.00"), 220f);   // 1.1.293: Core slim slider
            else v = GUILayout.HorizontalSlider(v, min, max, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb);
            GUILayout.EndHorizontal();
            return v;
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

        private void Load(VALightPreset p)
        {
            p = p ?? new VALightPreset();
            var inv = CultureInfo.InvariantCulture;
            _name = p.name ?? "";
            _color = p.Color ?? ""; _intensity = p.Intensity.ToString("0.##", inv); _range = p.Range.ToString("0.##", inv);
            _shadows = p.Shadows; _flicker = p.Flicker;
            _amount = p.FlickerAmount.ToString("0.###", inv); _speed = p.FlickerSpeed.ToString("0.##", inv); _movement = p.FlickerMovement.ToString("0.###", inv);
            _lightDist = p.LightDistance.ToString("0.#", inv); _shadowDist = p.ShadowDistance.ToString("0.#", inv);
            _fade = p.FadeDuration >= 0f ? p.FadeDuration.ToString("G6", inv) : ""; _fadeIn = p.FadeInDuration >= 0f ? p.FadeInDuration.ToString("0.##", inv) : "";
            _ttl = p.Ttl >= 0f ? p.Ttl.ToString("0.##", inv) : ""; _lodOn = p.LightLodOn; _shadowLodOn = p.ShadowLodOn; _note = p.Note ?? "";
            _flame = p.FlameTint ?? ""; _emissive = p.Emissive ?? "";
            _infinite = p.InfiniteFuel; _turnOff = p.CanTurnOff; _maxFuel = p.MaxFuel.ToString("0.##", inv); _secPerFuel = p.SecPerFuel.ToString("0.##", inv);
        }

        private VALightPreset FromForm(out string problem)
        {
            problem = null;
            var p = new VALightPreset
            {
                name = (_name ?? "").Trim(), FlickerAmount = Math.Max(0f, Flt(_amount)), FlickerSpeed = Math.Max(0f, Flt(_speed)),
                FlickerMovement = Math.Max(0f, Flt(_movement)), LightDistance = Math.Max(1f, Flt(_lightDist)), ShadowDistance = Math.Max(1f, Flt(_shadowDist)),
                FadeDuration = OptFlt(_fade), FadeInDuration = OptFlt(_fadeIn), Ttl = OptFlt(_ttl), LightLodOn = _lodOn, ShadowLodOn = _shadowLodOn, Note = _note,
                HasFire = _own.HasFire, InfiniteFuel = _infinite, CanTurnOff = _turnOff, MaxFuel = Math.Max(1f, Flt(_maxFuel)), SecPerFuel = Math.Max(0.1f, Flt(_secPerFuel)),
            };
            if (_vanillaFriendly)
            {
                // The FAP-only group stays at the light's own values: nothing changes that a vanilla client can't see.
                p.Color = _own.Color; p.Intensity = _own.Intensity; p.Range = _own.Range; p.Shadows = _own.Shadows; p.Flicker = _own.Flicker;
                p.FlameTint = ""; p.Emissive = "";
                return p;
            }
            if (!VALightApplier.TryColor(_color, out _)) { problem = "Light colour: six hex digits, e.g. FFB35C."; return null; }
            if (!string.IsNullOrWhiteSpace(_flame) && !VALightApplier.TryColor(_flame, out _)) { problem = "Flame tint: six hex digits or blank."; return null; }
            if (!string.IsNullOrWhiteSpace(_emissive) && !VALightApplier.TryColor(_emissive, out _)) { problem = "Glow colour: six hex digits or blank."; return null; }
            p.Color = VALightApplier.Hex(_color); p.Intensity = Math.Max(0f, Flt(_intensity)); p.Range = Math.Max(0.1f, Flt(_range));
            p.Shadows = _shadows; p.Flicker = _flicker; p.FlameTint = VALightApplier.Hex(_flame); p.Emissive = VALightApplier.Hex(_emissive);
            return p;
        }

        private void OnSave()
        {
            var p = FromForm(out string problem);
            if (p == null) { Status(problem, true); return; }
            if (p.name.Length == 0) { Status("A preset name is required to save.", true); return; }
            Status(VALightPresetStore.Save(p) ? $"Saved '{p.name}'." : "Save failed - see the log.", !VALightPresetStore.TryGet(p.name, out _));
        }

        private void OnApply()
        {
            if (!AdminOk()) return;
            var p = FromForm(out string problem);
            if (p == null) { Status(problem, true); return; }
            if (_target != null)
            {
                if (!_target.IsValid()) { Status("That light is gone (unloaded or removed).", true); return; }
                VALightApplier.SetLive(_target, p);
                Status($"Applied to this {_prefab}{(p.name.Length > 0 ? $" ('{p.name}')" : "")}.");
                return;
            }
            if (p.name.Length == 0) { Status("Name the preset to queue it.", true); return; }
            if (!VALightPresetStore.Save(p)) { Status("Save failed - apply aborted.", true); return; }
            VALightQueue.Enqueue(_prefab, p.name);
            Close();
            VAHammerArmHelper.ArmAndClose(_prefab);
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Queued light preset '{p.name}' for every {_prefab} placement (until changed)");
        }

        private void OnReset()
        {
            if (!AdminOk() || _target == null || !_target.IsValid()) return;
            VALightApplier.SetLive(_target, null);
            Load(_target.GetComponent<VALightState>()?.Current());
            Status("Reset: this light is back to its own look.");
        }

        private void OnDelete()
        {
            string name = (_name ?? "").Trim();
            if (!VALightPresetStore.Delete(name)) { Status($"No saved preset '{name}'.", true); return; }
            if (VALightQueue.TryGet(_prefab, out string q) && q == name) VALightQueue.Clear(_prefab);
            Status($"Deleted preset '{name}' (lights already set keep their look until reset).");
        }

        private bool AdminOk()
        {
            bool admin;
            // 1.1.306 (Fire 10-21): a placed light = own pieces only on a server without FAP (VAFapServerPresence.MayEditPiece);
            // queuing a preset for your own placements follows the 1.1.305 rule
            try { admin = _target != null ? VerdantsAscent.Managers.VAFapServerPresence.MayEditPiece(_target.gameObject) : VerdantsAscent.Managers.VAFapServerPresence.LocalMayUseAdminFeatures(); } catch { admin = false; }
            if (!admin) Status(_target != null && VerdantsAscent.Managers.VAFapServerPresence.IsClientOnly ? "Admins only: on a server without FAP turn debugmode on (Fire 10-23)." : "Admins only (Fire 10-12).", true);
            return admin;
        }

        private static VALightPreset Copy(VALightPreset p) => p == null ? new VALightPreset() : (VALightPreset)p.GetType().GetMethod("MemberwiseClone", AccessTools.all).Invoke(p, null);
        private void Status(string msg, bool error = false) { _status = msg ?? ""; _statusError = error; }
        private static float Flt(string s) => float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        // 1.1.262: blank = -1 = "leave the light's own value" (fade / fade-in / ttl)
        private static float OptFlt(string s) => string.IsNullOrWhiteSpace(s) ? -1f : Math.Max(0f, Flt(s));
    }

    // 1.1.300 (Fire 10-20): the Shift+RMB light pick is gone; placed lights open from Core's context menu (VASoundContextProvider: "Edit light...").
}
