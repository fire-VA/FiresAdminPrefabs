using System;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.UI;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    [HarmonyPatch(typeof(Player), "TakeInput")]
    public static class Player_TakeInput_VAItemStandConfigUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (VAItemStandConfigUI.IsOpen) __result = false;
        }
    }

    // The item stand preset editor: preset name, item prefab, quality, variant, interactable, refill (an explicit toggle, a time
    // when on), the "no player within" distance. Save writes the preset; Apply also queues it on the hammer (kept until changed)
    // and arms the stand; Delete removes it.
    // 1.1.179 (Fire 10-07): drawn in Core's rounded skin (FiresRoundedSkin, IMGUI - the FDT / territory palette look): a window
    // dragged by its title row, resized by the corner grip, an opacity slider; layout + opacity saved in [ItemStandPresetUI].
    // Refill is a toggle: ON needs a time (300 s prefilled), OFF says the item never comes back - a blank time no longer means
    // "never" without saying so.
    public class VAItemStandConfigUI : MonoBehaviour
    {
        private const int WindowId = 0x7A51;
        private const float DefaultRefillSeconds = 300f;
        private const float MinW = 460f, MaxW = 1200f, MinH = 560f, MaxH = 1000f, Grip = 18f;

        private static VAItemStandConfigUI _instance;
        private static ConfigEntry<float> s_opacity, s_w, s_h, s_x, s_y;

        public static void Open(string standPrefabName, string presetName) => Open(standPrefabName, presetName, null, null);

        // From an Item Stands (H)/(V) tab entry (a standin): the item is pre-filled and Apply arms that entry.
        public static void Open(string standPrefabName, string presetName, string prefillItem, string armPiece)
        {
            if (string.IsNullOrEmpty(standPrefabName)) return;
            if (_instance == null)
            {
                var go = new GameObject("FAP_ItemStandConfigUI");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<VAItemStandConfigUI>();
            }
            _instance.OpenInternal(standPrefabName, presetName, prefillItem, armPiece);
        }

        public static bool IsOpen => _instance != null && _instance._open;

        private bool _open;
        private string _stand, _armPiece;
        private string _name = "", _item = "", _quality = "1", _variant = "0", _respawnMin = "", _respawnMax = "", _clear = "";
        private bool _noInteract, _refills = true;
        private string _status = "";
        private bool _statusError;
        private Rect _rect;
        private Vector2 _scroll;
        private bool _resizing;

        private static void BindLayout()
        {
            if (s_opacity != null) return;
            var cfg = VaPieces.Instance.Config;
            s_opacity = cfg.Bind("ItemStandPresetUI", "Opacity", 0.96f, new ConfigDescription("The item stand preset window's opacity.", new AcceptableValueRange<float>(0.4f, 1f)));
            s_w = cfg.Bind("ItemStandPresetUI", "Width", 560f, "The item stand preset window's width (drag the corner grip).");
            s_h = cfg.Bind("ItemStandPresetUI", "Height", 720f, "The item stand preset window's height.");
            s_x = cfg.Bind("ItemStandPresetUI", "X", -1f, "The window's left edge (-1 = centred; drag the title row).");
            s_y = cfg.Bind("ItemStandPresetUI", "Y", -1f, "The window's top edge (-1 = centred).");
        }

        private void OpenInternal(string standPrefab, string presetName, string prefillItem, string armPiece)
        {
            BindLayout();
            _stand = standPrefab;
            _armPiece = string.IsNullOrEmpty(armPiece) ? standPrefab : armPiece;
            float w = Mathf.Clamp(s_w.Value, MinW, MaxW), h = Mathf.Clamp(s_h.Value, MinH, MaxH);
            float x = s_x.Value >= 0f ? s_x.Value : (Screen.width - w) / 2f, y = s_y.Value >= 0f ? s_y.Value : (Screen.height - h) / 2f;
            _rect = new Rect(Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 80f)), Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 60f)), w, h);
            VAItemStandPreset p = null;
            if (!string.IsNullOrEmpty(presetName)) VAItemStandPresetStore.TryGet(presetName, out p);
            Load(p);
            if (!string.IsNullOrEmpty(prefillItem) && (p == null || string.IsNullOrEmpty(_item))) _item = prefillItem;
            _open = true;
            FiresCore.Input.FiresInputBlock.Acquire(this); VAImguiGuard.Open(this, () => _rect, "ItemStandPreset");
        }

        private void Close()
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

        private void Update()
        {
            if (!_open) return;
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

        private void DrawWindow(int id)
        {
            FiresRoundedSkin.ResetTooltip();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Item stand preset: {_stand}", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Opacity", FiresRoundedSkin.Label, GUILayout.Width(62f));
            s_opacity.Value = GUILayout.HorizontalSlider(s_opacity.Value, 0.4f, 1f, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(110f));
            if (GUILayout.Button("x", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) Close();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_rect.height - 150f));

            GUILayout.Label("Preset", FiresRoundedSkin.SectionBar);
            _name = Row("Preset name", _name, "A unique name; the hammer lists it under 'Item stand presets'.");
            _item = Row("Item prefab", _item, "The item hung on the stand, e.g. SwordIron, TrophyDeer.");
            _quality = Row("Quality", _quality, "1 and up.");
            _variant = Row("Variant", _variant, "0 unless the item has styles.");

            GUILayout.Space(6f);
            GUILayout.Label("Interaction", FiresRoundedSkin.SectionBar);
            if (GUILayout.Button(_noInteract ? "Interactable: OFF - decorative (E does nothing; the hammer can still remove it)" : "Interactable: ON - players can take / hang items",
                    _noInteract ? FiresRoundedSkin.NavItem : FiresRoundedSkin.NavSel, GUILayout.Height(30f)))
                _noInteract = !_noInteract;

            GUILayout.Space(6f);
            GUILayout.Label("Refill", FiresRoundedSkin.SectionBar);
            if (GUILayout.Button(_refills ? "Refills: ON - a taken item comes back" : "Refills: OFF - a taken item never comes back",
                    _refills ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem, GUILayout.Height(30f)))
            {
                _refills = !_refills;
                if (_refills && string.IsNullOrWhiteSpace(_respawnMin)) _respawnMin = DefaultRefillSeconds.ToString(CultureInfo.InvariantCulture);
            }
            if (_refills)
            {
                _respawnMin = Row("Respawn time (s)", _respawnMin, "Required: seconds after the item is taken.");
                _respawnMax = Row("Up to (s, optional)", _respawnMax, "A random time between the two; blank = exactly the time above.");
                _clear = Row("Only when no player within (m)", _clear, "Blank or 0 = it comes back even with players right at the stand.");
            }

            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_statusError ? $"<color=#F08080>{_status}</color>" : _status, FiresRoundedSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnSave();
            if (GUILayout.Button("Apply (queue)", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnApply();
            if (GUILayout.Button("<color=#E8A0A0>Delete</color>", FiresRoundedSkin.Button, GUILayout.Height(32f))) OnDelete();
            if (GUILayout.Button("Close", FiresRoundedSkin.Button, GUILayout.Height(32f))) Close();
            GUILayout.EndHorizontal();

            HandleResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 200f, 30f));
        }

        private static string Row(string label, string value, string hint)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, FiresRoundedSkin.Label, GUILayout.Width(190f));
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

        private void Load(VAItemStandPreset p)
        {
            _name = p?.name ?? "";
            _item = p?.Item ?? "";
            _quality = (p?.Quality ?? 1).ToString(CultureInfo.InvariantCulture);
            _variant = (p?.Variant ?? 0).ToString(CultureInfo.InvariantCulture);
            // A saved preset with no time never refilled: show it as OFF. A new form starts ON with the default time.
            _refills = p == null || p.RespawnSeconds > 0f;
            _respawnMin = p != null && p.RespawnSeconds > 0f ? p.RespawnSeconds.ToString(CultureInfo.InvariantCulture)
                        : p == null ? DefaultRefillSeconds.ToString(CultureInfo.InvariantCulture) : "";
            _respawnMax = p != null && p.RespawnSecondsMax > 0f ? p.RespawnSecondsMax.ToString(CultureInfo.InvariantCulture) : "";
            _clear = p != null && p.RefillClearMetres > 0f ? p.RefillClearMetres.ToString(CultureInfo.InvariantCulture) : "";
            _noInteract = p?.NoInteract ?? false;
            Status(VACoreLoot.Available ? "Refill: Core's loot respawn." : VAItemStandEwp.Active ? "Refill: Expand World Prefabs rule (Item stands / UseEwpForRefill on)." : "Refill: FiresAdminPrefabs' own timer.");
        }

        private VAItemStandPreset FromForm(out string problem)
        {
            problem = null;
            string name = (_name ?? "").Trim(), item = (_item ?? "").Trim();
            if (name.Length == 0) { problem = "A preset name is required."; return null; }
            if (item.Length == 0) { problem = "Type the item hung on the stand (an item prefab, e.g. TrophyDeer)."; return null; }
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) { problem = $"'{item}' is not an item prefab."; return null; }
            // 1.1.180: the same rule the hang uses (VanillaFriendly ON = vanilla's CanAttach for this stand; OFF = an attach model).
            var standPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(_stand) : null;
            var data = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); data.m_dropPrefab = prefab;
            if (!VAItemStandApplier.CanHold(standPrefab != null ? standPrefab.GetComponent<ItemStand>() : null, data, out string why)) { problem = $"'{prefab.name}' can't hang on {_stand}: {why}."; return null; }
            float min = 0f, max = 0f, clear = 0f;
            if (_refills)
            {
                min = Flt(_respawnMin);
                if (min <= 0f) { problem = "Refills is ON: set a respawn time in seconds (or turn Refills OFF)."; return null; }
                max = Math.Max(0f, Flt(_respawnMax));
                if (max > 0f && max < min) { problem = "'Up to' must be at least the respawn time."; return null; }
                clear = Math.Max(0f, Flt(_clear));
            }
            return new VAItemStandPreset
            {
                name = name, StandPrefab = _stand, Item = prefab.name,
                Quality = Math.Max(1, Int(_quality, 1)), Variant = Math.Max(0, Int(_variant, 0)), NoInteract = _noInteract,
                RespawnSeconds = min, RespawnSecondsMax = max, RefillClearMetres = clear,
            };
        }

        private void OnSave()
        {
            var p = FromForm(out string problem);
            if (p == null) { Status(problem, true); return; }
            if (!VAItemStandPresetStore.Save(p)) { Status("Save failed - see the log.", true); return; }
            Status($"Saved '{p.name}'" + (p.RespawnSeconds > 0f ? $" (refills after {p.RespawnSeconds:0.#} s)." : " (never refills)."));
        }

        private void OnApply()
        {
            var p = FromForm(out string problem);
            if (p == null) { Status(problem, true); return; }
            if (!VAItemStandPresetStore.Save(p)) { Status("Save failed - apply aborted.", true); return; }
            VAItemStandQueue.Enqueue(_stand, p.name);
            Close();
            VAHammerArmHelper.ArmAndClose(string.IsNullOrEmpty(_armPiece) ? _stand : _armPiece);
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Queued item stand preset '{p.name}' for every {_stand} placement (until changed)");
        }

        private void OnDelete()
        {
            string name = (_name ?? "").Trim();
            if (!VAItemStandPresetStore.Delete(name)) { Status($"No saved preset '{name}'.", true); return; }
            VAItemStandQueue.TryGetQueuedPresetName(_stand, out string q);
            if (q == name) VAItemStandQueue.Clear(_stand);
            Load(null);
            Status($"Deleted '{name}'.");
        }

        private void Status(string msg, bool error = false) { _status = msg ?? ""; _statusError = error; }

        private static int Int(string s, int fallback) => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        private static float Flt(string s) => float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}
