using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.UI;
using HarmonyLib;
using UnityEngine;

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
    // 1.1.180 (Fire 10-07): drawn in Core's FiresRoundedSkin (IMGUI, the FDT look) - a window dragged by its title row,
    // resized by the corner grip, an opacity slider; layout + opacity saved in [ClutterConfigUI].
    public class VAClutterConfigUI : MonoBehaviour
    {
        private const int WindowId = 0x7A55;
        private const float MinW = 460f, MaxW = 1200f, MinH = 480f, MaxH = 1200f, Grip = 18f;

        private static VAClutterConfigUI _instance;
        private static ConfigEntry<float> s_opacity, s_w, s_h, s_x, s_y;

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

        public static bool IsOpen => _instance != null && _instance._open;

        private void Update()
        {
            if (!_open) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) CloseInternal();
        }

        private string _clutterPrefab;
        private string _selectedPresetName;

        private bool _open;
        private Rect _rect;
        private Vector2 _scroll;
        private bool _resizing;
        private string _status = string.Empty;
        private bool _statusError;

        // Identity
        private string _presetName = "", _biome = "";
        private bool _enabled;
        // Placement
        private string _amount = "", _randomOffset = "";
        private string _minAlt = "", _maxAlt = "", _minOceanDepth = "", _maxOceanDepth = "";
        private string _minTilt = "", _maxTilt = "", _minVegetation = "", _maxVegetation = "";
        private bool _snapToWater, _onUncleared, _onCleared, _terrainTilt, _instanced;
        // Scale
        private string _scaleMin = "", _scaleMax = "";
        // Fractal
        private string _fractalScale = "", _fractalOffset = "", _fractalThreshMin = "", _fractalThreshMax = "";
        // Forest
        private bool _inForest;
        private string _forestThreshMin = "", _forestThreshMax = "";
        // Shader
        private string _swaySpeed = "", _swayDistance = "", _pushDistance = "";

        private static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("ClutterConfigUI", "Opacity", 0.96f, new ConfigDescription("The clutter config window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_w = cfg.Bind("ClutterConfigUI", "Width", 560f, "The clutter config window's width (drag the corner grip).");
            s_h = cfg.Bind("ClutterConfigUI", "Height", 760f, "The clutter config window's height.");
            s_x = cfg.Bind("ClutterConfigUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("ClutterConfigUI", "Y", -1f, "The window's top edge (-1 = centred).");
        }

        private void OpenInternal(string clutterPrefabName)
        {
            if (string.IsNullOrEmpty(clutterPrefabName)) return;
            _clutterPrefab = clutterPrefabName;

            BindLayout();
            float w = Mathf.Clamp(s_w.Value, MinW, MaxW), h = Mathf.Clamp(s_h.Value, MinH, MaxH);
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
            _scroll = Vector2.zero;
            // Capture input (vanilla + other-mod hotkeys) for as long as the window is open.
            FiresCore.Input.FiresInputBlock.Acquire(this); VAImguiGuard.Open(this, () => _rect, "ClutterConfig");
            _open = true;

            string activeName = VAClutterPresetStore.GetActiveNameFor(_clutterPrefab);
            LoadPresetIntoForm(activeName);
        }

        private void CloseInternal()
        {
            if (!_open) return;
            _open = false;
            FiresCore.Input.FiresInputBlock.Release(this); VAImguiGuard.Close(this);
            SaveLayout();
        }

        private void SaveLayout()
        {
            if (s_w == null) return;
            s_w.Value = _rect.width; s_h.Value = _rect.height; s_x.Value = _rect.x; s_y.Value = _rect.y;
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

        private void DrawWindow(int id)
        {
            FiresRoundedSkin.ResetTooltip();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Configure clutter: {_clutterPrefab}", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = GUILayout.HorizontalSlider(s_opacity.Value, 0.4f, 1f, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(110f));
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) CloseInternal();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            // Preset picker: the existing presets still open in the shared right-click menu.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset (existing):", FiresRoundedSkin.Label, GUILayout.Width(160f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Pick existing...", FiresRoundedSkin.Button, GUILayout.Width(150f), GUILayout.Height(28f))) ShowPresetPicker();
            if (GUILayout.Button("New", FiresRoundedSkin.Button, GUILayout.Width(100f), GUILayout.Height(28f))) LoadPresetIntoForm(null);
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Max(80f, _rect.height - 190f)));

            GUILayout.Label("Identity", FiresRoundedSkin.SectionBar);
            _presetName = Row("Preset name", _presetName,
                "Saved as a named snapshot in clutter_presets.yaml. Pick a memorable label like 'LushMeadows' or 'SparseAshlands'.");
            _biome = Row("Biome (CSV)", _biome,
                "Comma-separated list of vanilla biome names: Meadows, BlackForest, Plains, Swamp, " +
                "Mountains, Mistlands, Ashlands, AshLands, DeepNorth, Ocean. Empty = leave the " +
                "clutter's current biome mask untouched.");
            _enabled = Toggle("Enabled", _enabled,
                "Master on/off for this clutter entry. Off hides it everywhere.");

            GUILayout.Space(6f);
            GUILayout.Label("Placement", FiresRoundedSkin.SectionBar);
            _amount = Row("Amount per patch", _amount,
                "How many instances per 8m grass patch (vanilla default 80). Higher = denser.");
            _randomOffset = Row("Random offset (m)", _randomOffset,
                "Per-instance position jitter around the patch grid. 0 = grid-aligned.");
            _minAlt = Row("Min altitude", _minAlt,
                "Spawn only at world Y ≥ this value. Vanilla water level is 27.");
            _maxAlt = Row("Max altitude", _maxAlt,
                "Spawn only at world Y ≤ this value. Use big values like 1000 for 'no cap'.");
            _minOceanDepth = Row("Min ocean depth", _minOceanDepth,
                "Underwater clutter — meters below water level. 0 = ignore.");
            _maxOceanDepth = Row("Max ocean depth", _maxOceanDepth,
                "Underwater clutter upper limit.");
            _minTilt = Row("Min terrain tilt (deg)", _minTilt,
                "Skip instances where local terrain slope is below this angle.");
            _maxTilt = Row("Max terrain tilt (deg)", _maxTilt,
                "Skip instances where local terrain slope is above this angle. Vanilla default 18.");
            _minVegetation = Row("Min vegetation density", _minVegetation,
                "Skip patches where vegetation density is below this. 0 = no min filter.");
            _maxVegetation = Row("Max vegetation density", _maxVegetation,
                "Skip patches where vegetation density is above this. 0 = no max filter.");
            _snapToWater = Toggle("Snap to water", _snapToWater,
                "Snap instances to water surface (floating clutter).");
            _onUncleared = Toggle("Spawn on uncleared terrain", _onUncleared,
                "Spawn on terrain that hasn't been hoed/leveled. Vanilla default ON.");
            _onCleared = Toggle("Spawn on cleared terrain", _onCleared,
                "Spawn on terrain that HAS been hoed/leveled. Vanilla default OFF.");
            _terrainTilt = Toggle("Tilt with terrain", _terrainTilt,
                "Rotate instances to align with local terrain slope.");
            _instanced = Toggle("Instanced rendering", _instanced,
                "Use Unity's instanced renderer (more efficient at high counts). Most vanilla clutter uses this.");

            GUILayout.Space(6f);
            GUILayout.Label("Scale", FiresRoundedSkin.SectionBar);
            _scaleMin = Row("Scale min", _scaleMin,
                "Lower bound of per-instance random scale.");
            _scaleMax = Row("Scale max", _scaleMax,
                "Upper bound of per-instance random scale. Set min=max for fixed size.");

            GUILayout.Space(6f);
            GUILayout.Label("Fractal placement", FiresRoundedSkin.SectionBar);
            _fractalScale = Row("Fractal scale", _fractalScale,
                "Noise scale for placement filter. > 0 enables fractal placement; 0 disables.");
            _fractalOffset = Row("Fractal offset", _fractalOffset,
                "Noise sample offset. Shift to get different placement patterns.");
            _fractalThreshMin = Row("Fractal threshold min", _fractalThreshMin,
                "Skip instances where noise value is below this. Vanilla default 0.5.");
            _fractalThreshMax = Row("Fractal threshold max", _fractalThreshMax,
                "Skip instances where noise value is above this. Vanilla default 1.0.");

            GUILayout.Space(6f);
            GUILayout.Label("Forest", FiresRoundedSkin.SectionBar);
            _inForest = Toggle("Inside forest", _inForest,
                "Only spawn where the forest fractal is 'inside forest' (between thresholds below).");
            _forestThreshMin = Row("Forest threshold min", _forestThreshMin,
                "Lower bound of the 'in forest' fractal range. 0 = no lower cutoff.");
            _forestThreshMax = Row("Forest threshold max", _forestThreshMax,
                "Upper bound of the 'in forest' fractal range. 1 = no upper cutoff.");

            GUILayout.Space(6f);
            GUILayout.Label("Shader (sway / push)", FiresRoundedSkin.SectionBar);
            _swaySpeed = Row("Sway speed", _swaySpeed,
                "Material _SwaySpeed property. Higher = faster wind animation. Applied via " +
                "MaterialPropertyBlock — won't permanently mutate the shared material.");
            _swayDistance = Row("Sway distance", _swayDistance,
                "Material _SwayDistance property. How far the tops of the grass bend.");
            _pushDistance = Row("Push distance", _pushDistance,
                "Material _PushDistance property. How far players/creatures push the grass aside.");

            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_statusError ? $"<color=#F08080>{_status}</color>" : _status, FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnSaveClicked();
            if (GUILayout.Button("Apply", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnApplyClicked();
            if (GUILayout.Button("<color=#E8A0A0>Delete</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnDeleteClicked();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) CloseInternal();
            GUILayout.EndHorizontal();

            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 200f, 30f));
        }

        private static string Row(string label, string value, string hint)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(220f));
            value = GUILayout.TextField(value ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            GUILayout.Label(hint, FiresRoundedSkin.Desc);
            return value;
        }

        // A bool row: one NavSel (ON) / NavItem (OFF) button that flips on click.
        private static bool Toggle(string label, bool value, string hint)
        {
            if (GUILayout.Button($"{label}: {(value ? "ON" : "OFF")}", value ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(28f)))
                value = !value;
            GUILayout.Label(hint, FiresRoundedSkin.Desc);
            return value;
        }

        private void ShowPresetPicker()
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
        }

        // The corner grip: drag to resize (the top-left corner stays put).
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
            else if (_resizing && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)) { _resizing = false; SaveLayout(); }
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
                _presetName = string.Empty;
                _biome = scratch.biome ?? string.Empty;
                _enabled = scratch.enabled;
                _amount = scratch.amount.ToString(CultureInfo.InvariantCulture);
                _randomOffset = scratch.randomOffset.ToString(CultureInfo.InvariantCulture);
                _minAlt = scratch.minAlt.ToString(CultureInfo.InvariantCulture);
                _maxAlt = scratch.maxAlt.ToString(CultureInfo.InvariantCulture);
                _minOceanDepth = scratch.minOceanDepth.ToString(CultureInfo.InvariantCulture);
                _maxOceanDepth = scratch.maxOceanDepth.ToString(CultureInfo.InvariantCulture);
                _minTilt = scratch.minTilt.ToString(CultureInfo.InvariantCulture);
                _maxTilt = scratch.maxTilt.ToString(CultureInfo.InvariantCulture);
                _minVegetation = scratch.minVegetation.ToString(CultureInfo.InvariantCulture);
                _maxVegetation = scratch.maxVegetation.ToString(CultureInfo.InvariantCulture);
                _snapToWater = scratch.snapToWater;
                _onUncleared = scratch.onUncleared;
                _onCleared = scratch.onCleared;
                _terrainTilt = scratch.terrainTilt;
                _instanced = scratch.instanced;
                _scaleMin = scratch.scaleMin.ToString(CultureInfo.InvariantCulture);
                _scaleMax = scratch.scaleMax.ToString(CultureInfo.InvariantCulture);
                _fractalScale = scratch.fractalScale.ToString(CultureInfo.InvariantCulture);
                _fractalOffset = scratch.fractalOffset.ToString(CultureInfo.InvariantCulture);
                _fractalThreshMin = scratch.fractalTresholdMin.ToString(CultureInfo.InvariantCulture);
                _fractalThreshMax = scratch.fractalTresholdMax.ToString(CultureInfo.InvariantCulture);
                _inForest = scratch.inForest;
                _forestThreshMin = scratch.forestTresholdMin.ToString(CultureInfo.InvariantCulture);
                _forestThreshMax = scratch.forestTresholdMax.ToString(CultureInfo.InvariantCulture);
                // Shader props default to vanilla grass values; snapshotting a property block is
                // not meaningful when the material may not expose the property.
                _swaySpeed = "60";
                _swayDistance = "2.3";
                _pushDistance = "2";

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
            _presetName = preset.name ?? string.Empty;
            _biome = preset.biome ?? string.Empty;
            _enabled = preset.enabled;
            _amount = preset.amount.ToString(CultureInfo.InvariantCulture);
            _randomOffset = preset.randomOffset.ToString(CultureInfo.InvariantCulture);
            _minAlt = preset.minAlt.ToString(CultureInfo.InvariantCulture);
            _maxAlt = preset.maxAlt.ToString(CultureInfo.InvariantCulture);
            _minOceanDepth = preset.minOceanDepth.ToString(CultureInfo.InvariantCulture);
            _maxOceanDepth = preset.maxOceanDepth.ToString(CultureInfo.InvariantCulture);
            _minTilt = preset.minTilt.ToString(CultureInfo.InvariantCulture);
            _maxTilt = preset.maxTilt.ToString(CultureInfo.InvariantCulture);
            _minVegetation = preset.minVegetation.ToString(CultureInfo.InvariantCulture);
            _maxVegetation = preset.maxVegetation.ToString(CultureInfo.InvariantCulture);
            _snapToWater = preset.snapToWater;
            _onUncleared = preset.onUncleared;
            _onCleared = preset.onCleared;
            _terrainTilt = preset.terrainTilt;
            _instanced = preset.instanced;
            _scaleMin = preset.scaleMin.ToString(CultureInfo.InvariantCulture);
            _scaleMax = preset.scaleMax.ToString(CultureInfo.InvariantCulture);
            _fractalScale = preset.fractalScale.ToString(CultureInfo.InvariantCulture);
            _fractalOffset = preset.fractalOffset.ToString(CultureInfo.InvariantCulture);
            _fractalThreshMin = preset.fractalTresholdMin.ToString(CultureInfo.InvariantCulture);
            _fractalThreshMax = preset.fractalTresholdMax.ToString(CultureInfo.InvariantCulture);
            _inForest = preset.inForest;
            _forestThreshMin = preset.forestTresholdMin.ToString(CultureInfo.InvariantCulture);
            _forestThreshMax = preset.forestTresholdMax.ToString(CultureInfo.InvariantCulture);
            _swaySpeed = preset.swaySpeed.ToString(CultureInfo.InvariantCulture);
            _swayDistance = preset.swayDistance.ToString(CultureInfo.InvariantCulture);
            _pushDistance = preset.pushDistance.ToString(CultureInfo.InvariantCulture);
            Status($"Loaded preset '{preset.name}'.");
        }

        private VAClutterPreset BuildPresetFromForm()
        {
            var preset = new VAClutterPreset
            {
                name = (_presetName ?? string.Empty).Trim(),
                clutterPrefab = _clutterPrefab,
                biome = (_biome ?? string.Empty).Trim(),
                enabled = _enabled,
                amount = ParseInt(_amount, 80),
                randomOffset = ParseFloat(_randomOffset, 0f),
                minAlt = ParseFloat(_minAlt, 27f),
                maxAlt = ParseFloat(_maxAlt, 1000f),
                minOceanDepth = ParseFloat(_minOceanDepth, 0f),
                maxOceanDepth = ParseFloat(_maxOceanDepth, 0f),
                minTilt = ParseFloat(_minTilt, 0f),
                maxTilt = ParseFloat(_maxTilt, 18f),
                minVegetation = ParseFloat(_minVegetation, 0f),
                maxVegetation = ParseFloat(_maxVegetation, 0f),
                snapToWater = _snapToWater,
                onUncleared = _onUncleared,
                onCleared = _onCleared,
                terrainTilt = _terrainTilt,
                instanced = _instanced,
                scaleMin = ParseFloat(_scaleMin, 1f),
                scaleMax = ParseFloat(_scaleMax, 1f),
                fractalScale = ParseFloat(_fractalScale, 0f),
                fractalOffset = ParseFloat(_fractalOffset, 0f),
                fractalTresholdMin = ParseFloat(_fractalThreshMin, 0.5f),
                fractalTresholdMax = ParseFloat(_fractalThreshMax, 1f),
                inForest = _inForest,
                forestTresholdMin = ParseFloat(_forestThreshMin, 0f),
                forestTresholdMax = ParseFloat(_forestThreshMax, 1f),
                swaySpeed = ParseFloat(_swaySpeed, 60f),
                swayDistance = ParseFloat(_swayDistance, 2.3f),
                pushDistance = ParseFloat(_pushDistance, 2f),
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
            _status = msg ?? string.Empty;
            _statusError = isError;
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
