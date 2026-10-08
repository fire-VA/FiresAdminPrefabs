using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.UI;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // "Create data preset" (1.1.169; was "Spawn with extra data") - every field Valheim's per-object loader (ZNetView.LoadFields)
    // can apply on this prefab, one row each, read from the prefab's real components (VAExtraFieldCatalogue). Each row shows the
    // field's current value on the prefab, its hint, and a tooltip on hover (what the field does and what it accepts). A named
    // preset is saved (data_FAP_datapresets.json) and/or queued on the hammer, where it stays until changed.
    // Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    // 1.1.180 (Fire 10-07: "use our proper core rounded UI skin"): drawn in Core's FiresRoundedSkin (IMGUI, the FDT / territory
    // palette look): a window dragged by its title row, resized by the corner grip, an opacity slider in the title row; layout and
    // opacity saved in [DataPresetUI].
    internal class VAExtraDataUI : MonoBehaviour
    {
        private const string RootName = "FAP_ExtraDataUI";
        private const int WindowId = 0x7A52;
        private const float MinW = 640f, MaxW = 1880f, MinH = 420f, MaxH = 1060f, Grip = 18f, LabelW = 0.36f;

        private static VAExtraDataUI _open;
        private static ConfigEntry<float> s_opacity, s_width, s_height, s_x, s_y;

        private string _prefabName;
        private List<VAExtraField> _fields;
        private readonly Dictionary<string, string> _typed = new Dictionary<string, string>();   // survives the search filter
        private string _presetName = string.Empty;
        private string _filter = string.Empty;
        private string _status;
        private Rect _rect;
        private Vector2 _scroll;
        private bool _resizing;
        private readonly object _inputToken = new object();

        public static bool IsOpen => _open != null;

        public static void Open(string prefabName, string presetName)
        {
            Close();
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: data preset - no prefab named {prefabName}");
                return;
            }

            var host = new GameObject(RootName);
            _open = host.AddComponent<VAExtraDataUI>();
            _open._prefabName = prefabName;
            _open._fields = VAExtraFieldCatalogue.Build(prefab);
            _open.LoadStart(presetName);
            _open.PlaceWindow();
            _open._status = _open.StatusLine();
            FiresCore.Input.FiresInputBlock.Acquire(_open._inputToken);
            var inst = _open; VAImguiGuard.Open(inst, () => inst._rect, "DataPreset");
        }

        public static void Close()
        {
            if (_open == null) return;
            FiresCore.Input.FiresInputBlock.Release(_open._inputToken);
            VAImguiGuard.Close(_open);
            _open.SaveLayout();
            Destroy(_open.gameObject);
            _open = null;
        }

        private static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("DataPresetUI", "Opacity", 0.96f, new ConfigDescription("The data preset window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_width = cfg.Bind("DataPresetUI", "Width", 1000f, "The data preset window's width (drag the corner grip).");
            s_height = cfg.Bind("DataPresetUI", "Height", 820f, "The data preset window's height.");
            s_x = cfg.Bind("DataPresetUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("DataPresetUI", "Y", -1f, "The window's top edge (-1 = centred).");
        }

        private void PlaceWindow()
        {
            BindLayout();
            float w = Mathf.Clamp(s_width.Value, MinW, Mathf.Min(MaxW, Screen.width)), h = Mathf.Clamp(s_height.Value, MinH, Mathf.Min(MaxH, Screen.height));
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
        }

        private void SaveLayout()
        {
            if (s_width == null) return;
            s_width.Value = _rect.width; s_height.Value = _rect.height; s_x.Value = _rect.x; s_y.Value = _rect.y;
        }

        // What the rows start with: the named preset, else the values armed on this prefab.
        private void LoadStart(string presetName)
        {
            _typed.Clear();
            if (!string.IsNullOrEmpty(presetName) && VADataPresetStore.TryGet(presetName, out VADataPreset preset))
            {
                _presetName = preset.name;
                foreach (var v in preset.Values) _typed[v.Key] = v.Text;
                return;
            }
            if (VAExtraDataQueue.TryGetFor(_prefabName, out List<VAExtraValue> armed))
                foreach (VAExtraValue value in armed) _typed[value.Key] = VAExtraFieldCatalogue.Format(value.Kind, value.Value);
            _presetName = VAExtraDataQueue.PresetFor(_prefabName) ?? string.Empty;
        }

        private void Update()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (ZInput.GetKeyDown(KeyCode.Escape)) Close();
        }

        private void OnGUI()
        {
            if (_open != this) return;
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
            GUILayout.Label($"Data preset - {_prefabName}", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = GUILayout.HorizontalSlider(s_opacity.Value, 0.4f, 1f, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(120f));
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) { Close(); return; }
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", FiresRoundedSkin.Label, GUILayout.Width(56f));
            _filter = GUILayout.TextField(_filter ?? "", FiresRoundedSkin.TextInput);
            GUILayout.Space(10f);
            GUILayout.Label("Preset name", FiresRoundedSkin.Label, GUILayout.Width(90f));
            _presetName = GUILayout.TextField(_presetName ?? "", FiresRoundedSkin.TextInput);
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            GUILayout.Label("Fields", FiresRoundedSkin.SectionBar);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Max(80f, _rect.height - 210f)));
            float labelW = Mathf.Max(160f, (_rect.width - 60f) * LabelW);
            foreach (VAExtraField field in _fields)
            {
                if (!Matches(field)) continue;
                DrawRow(field, labelW);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_status ?? "", FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save preset", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnSave();
            if (GUILayout.Button("Apply (queue)", FiresRoundedSkin.Button, GUILayout.Height(32f))) { OnApply(); return; }
            if (GUILayout.Button("<color=#E8A0A0>Delete preset</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnDelete();
            if (GUILayout.Button("No data preset", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnClear();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) { Close(); return; }
            GUILayout.EndHorizontal();

            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 230f, 30f));
        }

        // One field: its name, its value box (the prefab's own value shown when empty), its hint; hover = the full tooltip.
        private void DrawRow(VAExtraField field, float labelW)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(field.Settable ? $"<b>{field.Key}</b>" : $"<color=#B0A080>{field.Key}</color>", FiresRoundedSkin.Label, GUILayout.Width(labelW));
            if (field.Settable)
            {
                _typed.TryGetValue(field.Key, out string typed);
                string shown = GUILayout.TextField(typed ?? "", FiresRoundedSkin.TextInput);
                if (shown != (typed ?? "")) _typed[field.Key] = shown;
                if (string.IsNullOrEmpty(shown) && Event.current.type == EventType.Repaint)
                {
                    Rect box = GUILayoutUtility.GetLastRect();
                    var prev = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.55f);
                    GUI.Label(new Rect(box.x + 8f, box.y, box.width - 8f, box.height), string.IsNullOrEmpty(field.PrefabValue) ? "(empty)" : field.PrefabValue, FiresRoundedSkin.Label);
                    GUI.color = prev;
                }
            }
            GUILayout.EndHorizontal();
            string hint = field.Settable ? $"now {(string.IsNullOrEmpty(field.PrefabValue) ? "(empty)" : field.PrefabValue)} - {field.Hint}" : field.UnsettableReason;
            GUILayout.Label(hint, FiresRoundedSkin.Desc);
            FiresRoundedSkin.MarkHint((field.Settable ? field.Hint : field.UnsettableReason)
                + $"\nType: {field.Kind}. Accepts: {Accepts(field.Kind)}."
                + $"\nCurrent value on {_prefabName}: {(string.IsNullOrEmpty(field.PrefabValue) ? "(empty)" : field.PrefabValue)}."
                + "\nLeave empty to keep the prefab's own value.");
            GUILayout.Space(4f);
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

        private static string Accepts(VAExtraFieldKind kind)
        {
            switch (kind)
            {
                case VAExtraFieldKind.Int: return "a whole number, e.g. 3";
                case VAExtraFieldKind.Float: return "a number, e.g. 1.5";
                case VAExtraFieldKind.Bool: return "true or false";
                case VAExtraFieldKind.Vector3: return "x,y,z, e.g. 0,1.5,0";
                case VAExtraFieldKind.Text: return "any text";
                case VAExtraFieldKind.PrefabRef: return "a prefab name, e.g. Boar";
                case VAExtraFieldKind.ItemRef: return "an item prefab name, e.g. Wood";
                default: return kind.ToString();
            }
        }

        private bool Matches(VAExtraField field) =>
            string.IsNullOrEmpty(_filter) || field.Key.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0;

        // The typed values, parsed against the fields. null + a status message when one does not parse.
        private List<VAExtraValue> Collect(out List<VADataPresetValue> texts)
        {
            texts = new List<VADataPresetValue>();
            var values = new List<VAExtraValue>();
            foreach (VAExtraField field in _fields)
            {
                if (!field.Settable) continue;
                if (!_typed.TryGetValue(field.Key, out string typed) || string.IsNullOrEmpty(typed = typed?.Trim())) continue;
                if (!VAExtraFieldCatalogue.TryParse(field.Kind, typed, out object parsed, out string problem))
                {
                    _status = $"<color=#F08080>{field.Key}: {problem}. Nothing saved or applied.</color>";
                    return null;
                }
                values.Add(new VAExtraValue { Key = field.Key, Kind = field.Kind, Value = parsed });
                texts.Add(new VADataPresetValue { Key = field.Key, Text = typed });
            }
            if (values.Count == 0) { _status = "<color=#F08080>No fields filled in.</color>"; return null; }
            return values;
        }

        private VADataPreset SaveNamed(List<VADataPresetValue> texts)
        {
            string name = (_presetName ?? string.Empty).Trim();
            if (name.Length == 0) return null;
            var preset = new VADataPreset { name = name, Prefab = _prefabName, Values = texts };
            return VADataPresetStore.Save(preset) ? preset : null;
        }

        private void OnSave()
        {
            if (Collect(out var texts) == null) return;
            if ((_presetName ?? string.Empty).Trim().Length == 0) { _status = "<color=#F08080>Give the preset a name to save it.</color>"; return; }
            var preset = SaveNamed(texts);
            _status = preset != null ? $"Saved data preset '{preset.name}' ({texts.Count} field(s))." : "<color=#F08080>Save failed - see the log.</color>";
        }

        private void OnApply()
        {
            var values = Collect(out var texts);
            if (values == null) return;
            var preset = (_presetName ?? string.Empty).Trim().Length > 0 ? SaveNamed(texts) : null;
            VAExtraDataQueue.Arm(_prefabName, values, preset?.name);
            VAHammerArmHelper.ArmAndClose(_prefabName);
            Tell($"Data preset {(preset != null ? "'" + preset.name + "' " : "")}queued: {values.Count} field(s) on every {_prefabName} placement (until changed)");
            Close();
        }

        private void OnDelete()
        {
            string name = (_presetName ?? string.Empty).Trim();
            if (!VADataPresetStore.Delete(name)) { _status = $"<color=#F08080>No saved data preset '{name}'.</color>"; return; }
            if (VAExtraDataQueue.PresetFor(_prefabName) == name) VAExtraDataQueue.Clear(_prefabName);
            _status = $"Deleted data preset '{name}'.";
        }

        private void OnClear()
        {
            VAExtraDataQueue.Clear(_prefabName);
            Tell($"No data preset: the next {_prefabName} placements are plain");
            _typed.Clear();
            _status = StatusLine();
        }

        private string StatusLine()
        {
            int settable = _fields.Count(f => f.Settable);
            return $"{settable} settable field(s) of {_fields.Count} on {_prefabName}. {VAExtraDataQueue.Describe(_prefabName)}. "
                + "Queued values stay on this prefab until changed. Hover a row for what it does.";
        }

        private static void Tell(string message)
        {
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            Debug.Log($"{VaPieces.PluginName}: {message}");
        }
    }
}
