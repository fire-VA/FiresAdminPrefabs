using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.UI;
using HarmonyLib;
using UnityEngine;

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
    // from the right-click "Configure monster" menu item. Sections:
    // Preset, Identity, Spawn, Combat, Drops, Advanced.
    //
    // Save writes the preset to VAMonsterPresetStore (yaml on disk).
    // Apply queues the preset for this creature's next placement via
    // VAMonsterConfigQueue.
    //
    // Lifecycle: singleton MonoBehaviour on a DontDestroyOnLoad GO; the window is drawn while _open.
    // 1.1.180 (Fire 10-07): drawn in Core's FiresRoundedSkin (IMGUI, the FDT look): a window dragged by its title row,
    // resized by the corner grip, an opacity slider; layout + opacity saved in [MonsterConfigUI]. The preset picker is an
    // inline list (was a right-click submenu).
    public class VAMonsterConfigUI : MonoBehaviour
    {
        private const int WindowId = 0x7A54;
        private const float MinW = 480f, MaxW = 1200f, MinH = 560f, MaxH = 1100f, Grip = 18f;
        private const float LabelWidth = 230f;

        // Nudge so current-health never equals max-health (a vanilla edge
        // case breaks GetMaxHealth's fallback chain when they're equal).
        private const float MaxHealthEpsilon = 0.01f;
        // Scale is only written when it differs from identity by more than this.
        private const float ScaleChangeEpsilon = 0.0001f;

        // ── Singleton ────────────────────────────────────────────────

        private static VAMonsterConfigUI _instance;
        private static ConfigEntry<float> s_opacity, s_w, s_h, s_x, s_y;

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

        public static bool IsOpen => _instance != null && _instance._open;

        // While the UI is open: hold the cursor free + visible, and
        // listen for ESC to close (the pause menu is gated by IsOpen
        // via Player.TakeInput so it won't fire while we're up).
        private void Update()
        {
            if (!_open) return;

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

        private bool _open;
        private string _creature;                       // creature prefab the UI is editing
        private string _selectedPresetName;             // currently-loaded preset (empty = new)

        // VCS bone integration (only active when FiresCustomizationStudio is present).
        private string _pendingBones;                   // bone blob captured from the VCS editor, written on Save
        private GameObject _previewCreature;            // transient creature spawned for the VCS bone-edit session
        private bool _editingBones;                     // true while the VCS bone editor is open for our preview

        // Form fields — read back on Save
        private string _presetName = "", _sharedName = "", _groupMin = "1", _groupMax = "1";
        private bool _respawn;
        private string _respawnSeconds = "60", _stackingRadius = "100", _health = "", _starLevel = "1";
        private bool _boss;
        private string _defeatKey = "", _drops = "", _dropsAmount = "";   // _drops: multiline, EWP-native items[] format
        private string _scaleX = "1", _scaleY = "1", _scaleZ = "1";

        private string _status = "";
        private bool _statusError;
        private bool _pickerOpen;                       // inline "Pick existing..." list shown
        private Rect _rect;
        private Vector2 _scroll;
        private bool _resizing;

        private static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("MonsterConfigUI", "Opacity", 0.96f, new ConfigDescription("The monster preset window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_w = cfg.Bind("MonsterConfigUI", "Width", 600f, "The monster preset window's width (drag the corner grip).");
            s_h = cfg.Bind("MonsterConfigUI", "Height", 760f, "The monster preset window's height.");
            s_x = cfg.Bind("MonsterConfigUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("MonsterConfigUI", "Y", -1f, "The window's top edge (-1 = centred).");
        }

        // ── Open / Close ─────────────────────────────────────────────

        private void OpenInternal(string creaturePrefabName)
        {
            if (string.IsNullOrEmpty(creaturePrefabName)) return;
            BindLayout();
            _creature = creaturePrefabName;
            float w = Mathf.Clamp(s_w.Value, MinW, MaxW), h = Mathf.Clamp(s_h.Value, MinH, MaxH);
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
            _scroll = Vector2.zero;
            _pickerOpen = false;
            // Capture input (vanilla + other-mod hotkeys) for as long as the window is open.
            if (!_open) FiresCore.Input.FiresInputBlock.Acquire(this); VAImguiGuard.Open(this, () => _rect, "MonsterConfig");
            _open = true;

            // Always open on a blank form. Auto-loading an existing preset
            // caused save-leakage: a new name typed over a populated form
            // would create a preset that inherited every untouched field.
            LoadPresetIntoForm(null);
        }

        private void CloseInternal()
        {
            CleanupPreview();
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

        // ── Drawing ──────────────────────────────────────────────────

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
            GUILayout.Label($"Configure monster: {_creature}", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = GUILayout.HorizontalSlider(s_opacity.Value, 0.4f, 1f, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(110f));
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) CloseInternal();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_rect.height - 150f));

            // Preset picker: "Pick existing..." lists this creature's saved presets inline; "New" clears the form.
            GUILayout.Label("Preset", FiresRoundedSkin.SectionBar);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset (existing):", FiresRoundedSkin.Label, GUILayout.Width(LabelWidth));
            if (GUILayout.Button("Pick existing...", _pickerOpen ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(28f)))
                _pickerOpen = !_pickerOpen;
            if (GUILayout.Button("New", FiresRoundedSkin.Button, GUILayout.Width(90f), GUILayout.Height(28f)))
            {
                _pickerOpen = false;
                LoadPresetIntoForm(null);
            }
            GUILayout.EndHorizontal();
            if (_pickerOpen) DrawPresetList();

            GUILayout.Space(6f);
            GUILayout.Label("Identity", FiresRoundedSkin.SectionBar);
            _presetName = Row("Preset name", _presetName,
                "Used as Humanoid.m_name on placed creatures AND as the EWP rule filter. " +
                "Keep unique — naming a preset 'Boar' will match every vanilla Boar in the world.");
            _sharedName = Row("Shared display name (Humanoid.m_name override)", _sharedName,
                "Shown in the creature's hover tooltip. Leave blank to match the preset name.");

            GUILayout.Space(6f);
            GUILayout.Label("Spawn", FiresRoundedSkin.SectionBar);
            _groupMin = Row("Group size — min", _groupMin,
                "How many copies to spawn at the click point. Set both to 1 for single spawns.");
            _groupMax = Row("Group size — max", _groupMax,
                "Random count between min and max (inclusive) per click.");
            _respawn = ToggleRow("Respawn on death", _respawn,
                "When ON, the creature respawns after the configured delay. " +
                "Uses EWP's destroy-rule pipeline when EWP is installed; falls back to a FAP-side coroutine otherwise.");
            _respawnSeconds = Row("Respawn delay (seconds)", _respawnSeconds,
                "Time between death and respawn. Only used when 'Respawn on death' is ON.");
            _stackingRadius = Row("Prevent stacking — radius (m)", _stackingRadius,
                "When > 0, respawn is SKIPPED if another creature with the same preset name " +
                "is still alive within this radius. Guarantees 'one in, one out' for boss-class " +
                "creatures. Set to 0 to disable the check (useful when using this rule to " +
                "maintain ambient density of common creatures).");

            GUILayout.Space(6f);
            GUILayout.Label("Combat", FiresRoundedSkin.SectionBar);
            _health = Row("Health", _health,
                "Both 'health' AND 'max_health' are written to the ZDO. max_health is set " +
                "to health + 0.01 to avoid a vanilla edge case where current == max breaks " +
                "GetMaxHealth's fallback chain.");
            _starLevel = Row("Star level (1-3)", _starLevel,
                "1 = no stars, 2 = one star, 3 = two stars. Vanilla ZDO key 'level'. " +
                "Multiplies base health and damage according to vanilla scaling.");
            _boss = ToggleRow("Boss flag", _boss,
                "Sets Humanoid.m_boss = 1 — Valheim shows the boss-health bar UI when " +
                "players are near. Also affects some AI behaviours.");
            _defeatKey = Row("Set global key on death", _defeatKey,
                "Vanilla Humanoid.m_defeatSetGlobalKey override. When this creature dies, " +
                "the named key is added to the world's global-key list. Useful for gating " +
                "raids, biome unlocks, and quest progress (e.g. 'defeated_eikthyr').");

            GUILayout.Space(6f);
            GUILayout.Label("Drops", FiresRoundedSkin.SectionBar);
            GUILayout.Label("Drop table (one item per line)", FiresRoundedSkin.Label);
            _drops = GUILayout.TextArea(_drops ?? "", FiresRoundedSkin.TextInput, GUILayout.MinHeight(120f));
            GUILayout.Label(VALootTextFormat.HintText +
                "\nOverrides vanilla CharacterDrop entirely — defining drops here " +
                "suppresses the prefab's baked drop list.", FiresRoundedSkin.Desc);
            _dropsAmount = Row("Pick N from pool (optional)", _dropsAmount,
                "Empty = each item rolls independently against its own chance. " +
                "Single number ('3') = always drop exactly 3 from the pool. Range " +
                "('2;4') = drop a random count in that window per death. Higher " +
                "chance values bias selection toward those items.");

            GUILayout.Space(6f);
            GUILayout.Label("Advanced", FiresRoundedSkin.SectionBar);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Scale (x, y, z)", FiresRoundedSkin.Label, GUILayout.Width(LabelWidth));
            _scaleX = GUILayout.TextField(_scaleX ?? "", FiresRoundedSkin.TextInput, GUILayout.MinWidth(56f));
            _scaleY = GUILayout.TextField(_scaleY ?? "", FiresRoundedSkin.TextInput, GUILayout.MinWidth(56f));
            _scaleZ = GUILayout.TextField(_scaleZ ?? "", FiresRoundedSkin.TextInput, GUILayout.MinWidth(56f));
            GUILayout.EndHorizontal();
            GUILayout.Label("Visual scale of the creature. Vanilla ZDO 'scale' vec3. Use values like " +
                "1,1,1 for default size; 0.5,0.5,0.5 for mini bosses.", FiresRoundedSkin.Desc);

            // VCS bone editor — only when FiresCustomizationStudio is loaded (Core bridge flag). Opens VCS's
            // bone editor on a transient preview creature; the edited bone positions are saved into this preset.
            if (FiresCore.Bridge.BoneEditingBridge.IsAvailable)
            {
                GUILayout.Space(4f);
                if (GUILayout.Button("Edit bones (VCS)…", FiresRoundedSkin.Button, GUILayout.Height(30f))) OnEditBonesClicked();
            }

            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_statusError ? $"<color=#F08080>{_status}</color>" : _status, FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnSaveClicked();
            if (GUILayout.Button("Apply (queue)", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnApplyClicked();
            if (GUILayout.Button("<color=#E8A0A0>Delete</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnDeleteClicked();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) CloseInternal();
            GUILayout.EndHorizontal();

            if (!_open) return;
            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 200f, 30f));
        }

        // The saved presets for this creature as NavItem buttons (NavSel = the loaded one).
        private void DrawPresetList()
        {
            var names = new List<string>();
            foreach (var entry in VAMonsterPresetStore.All)
            {
                var preset = entry.Value;
                if (preset == null) continue;
                if (!string.Equals(preset.Creature, _creature, StringComparison.Ordinal)) continue;
                names.Add(preset.name);
            }
            if (names.Count == 0)
            {
                GUILayout.Label("(no presets saved for this creature)", FiresRoundedSkin.Desc);
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
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(LabelWidth));
            value = GUILayout.TextField(value ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            GUILayout.Label(hint, FiresRoundedSkin.Desc);
            return value;
        }

        // ON/OFF toggle as a full-width NavSel/NavItem button + its hint.
        private static bool ToggleRow(string label, bool value, string hint)
        {
            if (GUILayout.Button(label + (value ? ": ON" : ": OFF"), value ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(30f)))
                value = !value;
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
            VAMonsterPreset preset = null;
            if (!string.IsNullOrEmpty(presetName))
            {
                VAMonsterPresetStore.TryGet(presetName, out preset);
            }

            if (preset == null)
            {
                _selectedPresetName = null;
                _presetName = string.Empty;
                _sharedName = string.Empty;
                _groupMin = "1";
                _groupMax = "1";
                _respawn = false;
                _respawnSeconds = "60";
                _stackingRadius = "100";
                _health = "";
                _starLevel = "1";
                _boss = false;
                _defeatKey = string.Empty;
                _drops = string.Empty;
                _dropsAmount = string.Empty;
                _scaleX = "1";
                _scaleY = "1";
                _scaleZ = "1";
                _pendingBones = string.Empty;
                Status("New preset — fill in the form and click Save, or click 'Pick existing...' to edit one.");
                return;
            }

            _selectedPresetName = preset.name;
            _pendingBones = preset.Bones; // seed prior bone edits so the editor opens showing them
            _presetName = preset.name ?? string.Empty;
            _sharedName = preset.TryGetString("Humanoid.m_name") ?? string.Empty;
            _groupMin = preset.TryGetIntStr("va_group_min") ?? "1";
            _groupMax = preset.TryGetIntStr("va_group_max") ?? "1";

            string respawn = preset.TryGetFloatStr("va_respawn_seconds");
            float respawnVal = ParseFloat(respawn, 0f);
            _respawn = respawnVal > 0f;
            _respawnSeconds = respawnVal > 0f
                ? respawnVal.ToString("F1", CultureInfo.InvariantCulture)
                : "60";

            _stackingRadius = preset.TryGetFloatStr("va_prevent_stacking_radius") ?? "100";
            _health = preset.TryGetFloatStr("health") ?? "";
            _starLevel = preset.TryGetIntStr("level") ?? "1";
            _boss = preset.TryGetIntStr("override_boss") == "1"
                || preset.TryGetIntStr("Humanoid.m_boss") == "1";
            _defeatKey = preset.TryGetString("Humanoid.m_defeatSetGlobalKey") ?? string.Empty;
            _drops = VALootTextFormat.Serialize(preset.items);
            _dropsAmount = preset.itemAmount ?? string.Empty;

            string scaleStr = preset.TryGetVecStr("scale");
            if (!string.IsNullOrEmpty(scaleStr))
            {
                var parts = scaleStr.Split(',');
                if (parts.Length >= 3)
                {
                    _scaleX = parts[0].Trim();
                    _scaleY = parts[1].Trim();
                    _scaleZ = parts[2].Trim();
                }
            }
            else
            {
                _scaleX = "1";
                _scaleY = "1";
                _scaleZ = "1";
            }

            Status($"Loaded preset '{preset.name}'.");
        }

        private VAMonsterPreset BuildPresetFromForm()
        {
            var preset = new VAMonsterPreset
            {
                name = (_presetName ?? string.Empty).Trim(),
            };
            preset.Creature = _creature;  // stored inside strings as va_creature

            if (string.IsNullOrEmpty(preset.name)) return null;

            // ints
            int groupMin = ParseInt(_groupMin, 1);
            int groupMax = ParseInt(_groupMax, groupMin);
            if (groupMax < groupMin) groupMax = groupMin;
            preset.ints.Add($"va_group_min, {groupMin}");
            preset.ints.Add($"va_group_max, {groupMax}");

            int level = ParseInt(_starLevel, 1);
            if (level < 1) level = 1;
            preset.ints.Add($"level, {level}");

            // Boss flag — emit both the WEC key and the vanilla
            // LoadFields key so both readers see it.
            int boss = _boss ? 1 : 0;
            preset.ints.Add($"override_boss, {boss}");
            preset.ints.Add($"Humanoid.m_boss, {boss}");

            // floats
            float health = ParseFloat(_health, 0f);
            if (health > 0f)
            {
                preset.floats.Add($"health, {health.ToString("F2", CultureInfo.InvariantCulture)}");
                preset.floats.Add($"max_health, {(health + MaxHealthEpsilon).ToString("F2", CultureInfo.InvariantCulture)}");
            }

            if (_respawn)
            {
                float seconds = ParseFloat(_respawnSeconds, 60f);
                if (seconds > 0f)
                    preset.floats.Add($"va_respawn_seconds, {seconds.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            float stackingRadius = ParseFloat(_stackingRadius, 100f);
            preset.floats.Add($"va_prevent_stacking_radius, {stackingRadius.ToString("F1", CultureInfo.InvariantCulture)}");

            // strings
            string sharedName = (_sharedName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(sharedName)) sharedName = preset.name;
            preset.strings.Add($"Humanoid.m_name, {sharedName}");

            string defeatKey = (_defeatKey ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(defeatKey))
                preset.strings.Add($"Humanoid.m_defeatSetGlobalKey, {defeatKey}");

            // EWP-native items list — replaces the legacy va_drops
            // pipe-string. Vanilla CharacterDrop suppression for
            // preset-stamped creatures happens in VAMonsterConfigApplier.
            preset.items = VALootTextFormat.Parse(_drops);
            var dropsAmount = (_dropsAmount ?? string.Empty).Trim();
            preset.itemAmount = string.IsNullOrEmpty(dropsAmount) ? null : dropsAmount;

            // vecs
            float sx = ParseFloat(_scaleX, 1f);
            float sy = ParseFloat(_scaleY, 1f);
            float sz = ParseFloat(_scaleZ, 1f);
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
            _status = msg ?? "";
            _statusError = isError;
        }

        // ── VCS bone-edit integration ────────────────────────────────

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
