using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.UI;
using HarmonyLib;
using UnityEngine;

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
    // 1.1.180 (Fire 10-07): drawn in Core's FiresRoundedSkin (IMGUI, the FDT look) - a window dragged by its title row,
    // resized by the corner grip, an opacity slider; layout + opacity saved in [ChestConfigUI]. "Pick existing..." opens
    // an inline preset list (an IMGUI window draws over any uGUI popup, so the right-click menu would sit behind it).
    public class VAChestConfigUI : MonoBehaviour
    {
        private const int WindowId = 0x7A53;
        private const float MinW = 460f, MaxW = 1200f, MinH = 560f, MaxH = 1000f, Grip = 18f;

        private static VAChestConfigUI _instance;
        private static ConfigEntry<float> s_opacity, s_w, s_h, s_x, s_y;

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

        public static bool IsOpen => _instance != null && _instance._open;

        private void Update()
        {
            if (!_open) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (Input.GetKeyDown(KeyCode.Escape)) CloseInternal();
        }

        private string _containerPrefab;
        private string _selectedPresetName;

        private bool _open;
        private string _inputPresetName = "";
        private string _inputLootTable = "";     // multiline
        private string _inputItemAmount = "";    // "" or "3" or "2;4"
        private string _inputRespawnMin = "";
        private string _inputRespawnMax = "";
        private string _inputStackingRadius = "";
        private string _status = "";
        private bool _statusError;
        private bool _pickerOpen;
        private Rect _rect;
        private Vector2 _scroll;
        private bool _resizing;

        private static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("ChestConfigUI", "Opacity", 0.96f, new ConfigDescription("The chest preset window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_w = cfg.Bind("ChestConfigUI", "Width", 560f, "The chest preset window's width (drag the corner grip).");
            s_h = cfg.Bind("ChestConfigUI", "Height", 720f, "The chest preset window's height.");
            s_x = cfg.Bind("ChestConfigUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("ChestConfigUI", "Y", -1f, "The window's top edge (-1 = centred).");
        }

        private void OpenInternal(string containerPrefabName)
        {
            if (string.IsNullOrEmpty(containerPrefabName)) return;
            _containerPrefab = containerPrefabName;

            BindLayout();
            float w = Mathf.Clamp(s_w.Value, MinW, MaxW), h = Mathf.Clamp(s_h.Value, MinH, MaxH);
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
            _scroll = Vector2.zero;
            _pickerOpen = false;
            // Capture input (vanilla + other-mod hotkeys) for as long as the window is open.
            if (!_open) FiresCore.Input.FiresInputBlock.Acquire(this); VAImguiGuard.Open(this, () => _rect, "ChestConfig");
            _open = true;

            LoadPresetIntoForm(null);
        }

        private void CloseInternal()
        {
            if (!_open) return;
            _open = false;
            _pickerOpen = false;
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
            GUILayout.Label($"Configure container: {_containerPrefab}", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = GUILayout.HorizontalSlider(s_opacity.Value, 0.4f, 1f, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(110f));
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) CloseInternal();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_rect.height - 150f));

            DrawPresetPicker();

            GUILayout.Space(6f);
            GUILayout.Label("Identity", FiresRoundedSkin.SectionBar);
            _inputPresetName = Row("Preset name", _inputPresetName,
                "Used as the ZDO int marker stamped on each placed chest AND as " +
                "the EWP rule's filter key. Keep unique — naming a preset 'piece_chest' " +
                "would collide with every vanilla chest in the world.");

            GUILayout.Space(6f);
            GUILayout.Label("Loot", FiresRoundedSkin.SectionBar);
            GUILayout.Label("Loot table (one item per line)", FiresRoundedSkin.Label);
            _inputLootTable = GUILayout.TextArea(_inputLootTable ?? "", FiresRoundedSkin.TextInput, GUILayout.MinHeight(8 * 18f));
            GUILayout.Label(VALootTextFormat.HintText, FiresRoundedSkin.Desc);
            _inputItemAmount = Row("Pick N from pool (optional)", _inputItemAmount,
                "Empty = each item rolls independently against its own chance. " +
                "Single number ('3') = always pick exactly 3 from the pool. Range " +
                "('2;4') = pick a random count in that window per respawn. Higher " +
                "chance values bias selection toward those items.");

            GUILayout.Space(6f);
            GUILayout.Label("Respawn", FiresRoundedSkin.SectionBar);
            _inputRespawnMin = Row("Respawn delay — min (seconds)", _inputRespawnMin,
                "Time between the chest being emptied and respawning. Required for " +
                "the respawn loop to be active at all (a preset with delay 0 is a " +
                "one-shot loot template — no destroy/respawn rule emitted).");
            _inputRespawnMax = Row("Respawn delay — max (seconds, optional)", _inputRespawnMax,
                "When set AND greater than min, EWP randomizes each respawn within " +
                "[min, max]. Leave empty (or equal to min) for a fixed delay. Useful " +
                "for staggering identical chests so players can't camp the timer.");
            _inputStackingRadius = Row("Prevent stacking — radius (m)", _inputStackingRadius,
                "When > 0, respawn is SKIPPED if another chest of the SAME container " +
                "prefab already exists within this radius. Stops respawn loops from " +
                "creating chest-on-chest stacks when players place dupes. Set to 0 to " +
                "disable the check.");

            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_statusError ? $"<color=#F08080>{_status}</color>" : _status, FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnSaveClicked();
            if (GUILayout.Button("Apply (queue)", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnApplyClicked();
            if (GUILayout.Button("<color=#E8A0A0>Delete</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnDeleteClicked();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) CloseInternal();
            GUILayout.EndHorizontal();

            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 200f, 30f));
        }

        // "Pick existing..." toggles an inline list of this container's saved presets; "New" blanks the form.
        private void DrawPresetPicker()
        {
            GUILayout.Label("Preset", FiresRoundedSkin.SectionBar);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset (existing):", FiresRoundedSkin.Label, GUILayout.Width(160f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Pick existing...", _pickerOpen ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Width(150f), GUILayout.Height(28f)))
                _pickerOpen = !_pickerOpen;
            if (GUILayout.Button("New", FiresRoundedSkin.Button, GUILayout.Width(100f), GUILayout.Height(28f)))
            {
                _pickerOpen = false;
                LoadPresetIntoForm(null);
            }
            GUILayout.EndHorizontal();
            if (!_pickerOpen) return;

            var names = new List<string>();
            foreach (var kvp in VAChestPresetStore.All)
            {
                var preset = kvp.Value;
                if (preset == null) continue;
                if (!string.Equals(preset.ContainerPrefab, _containerPrefab, StringComparison.Ordinal)) continue;
                names.Add(preset.name);
            }
            if (names.Count == 0)
            {
                GUILayout.Label("(no presets saved for this chest)", FiresRoundedSkin.Desc);
                return;
            }
            foreach (var n in names)
            {
                bool sel = string.Equals(n, _selectedPresetName, StringComparison.Ordinal);
                if (GUILayout.Button("→ " + n, sel ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(26f)))
                {
                    _pickerOpen = false;
                    LoadPresetIntoForm(n);
                }
            }
        }

        private static string Row(string label, string value, string hint)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(250f));
            value = GUILayout.TextField(value ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            GUILayout.Label(hint, FiresRoundedSkin.Desc);
            return value;
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
                _inputPresetName = string.Empty;
                _inputLootTable = string.Empty;
                _inputItemAmount = string.Empty;
                _inputRespawnMin = "60";
                _inputRespawnMax = string.Empty;
                _inputStackingRadius = "0";
                Status("New preset — fill in the form and click Save, or click 'Pick existing...' to edit one.");
                return;
            }

            _selectedPresetName = preset.name;
            _inputPresetName = preset.name ?? string.Empty;
            _inputLootTable = VALootTextFormat.Serialize(preset.items);
            _inputItemAmount = preset.itemAmount ?? string.Empty;
            _inputRespawnMin = preset.TryGetFloatStr("va_respawn_seconds") ?? "60";
            _inputRespawnMax = preset.TryGetFloatStr("va_respawn_seconds_max") ?? string.Empty;
            _inputStackingRadius = preset.TryGetFloatStr("va_prevent_stacking_radius") ?? "0";
            Status($"Loaded preset '{preset.name}'.");
        }

        private string _formProblem;

        private VAChestPreset BuildPresetFromForm()
        {
            _formProblem = null;
            var preset = new VAChestPreset
            {
                name = (_inputPresetName ?? string.Empty).Trim(),
            };
            preset.ContainerPrefab = _containerPrefab;
            if (string.IsNullOrEmpty(preset.name)) return null;

            preset.items = VALootTextFormat.Parse(_inputLootTable);
            var itemAmountTxt = (_inputItemAmount ?? string.Empty).Trim();
            preset.itemAmount = string.IsNullOrEmpty(itemAmountTxt) ? null : itemAmountTxt;

            if (!VAChestSlotPlan.TryPlan(preset, out string slotProblem))
            {
                _formProblem = slotProblem;
                return null;
            }

            float respawnMin = ParseFloat(_inputRespawnMin, 60f);
            if (respawnMin > 0f)
                preset.floats.Add($"va_respawn_seconds, {respawnMin.ToString("F2", CultureInfo.InvariantCulture)}");

            float respawnMax = ParseFloat(_inputRespawnMax, 0f);
            if (respawnMax > respawnMin)
                preset.floats.Add($"va_respawn_seconds_max, {respawnMax.ToString("F2", CultureInfo.InvariantCulture)}");

            float stackingRadius = ParseFloat(_inputStackingRadius, 0f);
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

        private void Status(string msg, bool isError = false) { _status = msg ?? ""; _statusError = isError; }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
