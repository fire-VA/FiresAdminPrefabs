using System.Collections.Generic;
using FiresCore.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Utilities
{
    // "Spawn with extra data" - every field Valheim's per-object loader can apply on this prefab, one row each.
    // Built through Core's UIBuilderHelper (never hand-rolled uGUI) and its CreateScrollArea for the list.
    // Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    internal class VAExtraDataUI : MonoBehaviour
    {
        private const string RootName = "FAP_ExtraDataUI";
        private const int SortOrder = 5000;
        private const float RowHeight = 46f;
        private const float RowGap = 4f;
        private const float LabelWidthFraction = 0.42f;
        private const float HeaderHeight = 52f;
        private const float FooterHeight = 44f;
        private const float SearchHeight = 30f;
        private const int LabelFontSize = 13;
        private const int HintFontSize = 11;
        private const int TitleFontSize = 18;

        private static VAExtraDataUI _open;

        private string _prefabName;
        private List<VAExtraField> _fields;
        private readonly Dictionary<string, TMP_InputField> _inputs = new Dictionary<string, TMP_InputField>();
        private GameObject _canvasRoot;
        private RectTransform _listContent;
        private TextMeshProUGUI _status;
        private string _filter = string.Empty;

        public static void Open(string prefabName)
        {
            Close();
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: extra data - no prefab named {prefabName}");
                return;
            }

            var host = new GameObject(RootName);
            _open = host.AddComponent<VAExtraDataUI>();
            _open._prefabName = prefabName;
            _open._fields = VAExtraFieldCatalogue.Build(prefab);
            _open.Build();
        }

        public static void Close()
        {
            if (_open == null) return;
            if (_open._canvasRoot != null) Destroy(_open._canvasRoot);
            Destroy(_open.gameObject);
            _open = null;
        }

        private void Build()
        {
            _canvasRoot = new GameObject(RootName + "_Canvas");
            Canvas canvas = _canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortOrder;
            CanvasScaler scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _canvasRoot.AddComponent<GraphicRaycaster>();

            GameObject panel = UIBuilderHelper.CreatePanel(_canvasRoot.transform, "Panel",
                new Vector2(0.24f, 0.12f), new Vector2(0.76f, 0.9f), VAConfigTheme.PanelBg);
            var panelRect = panel.GetComponent<RectTransform>();

            UIBuilderHelper.CreateLabel(panelRect, "Title",
                $"Spawn with extra data - {_prefabName}", TitleFontSize, VAConfigTheme.SectionLabel,
                new Vector2(0f, 1f), new Vector2(1f, 1f), TextAlignmentOptions.Center)
                .rectTransform.sizeDelta = new Vector2(0f, HeaderHeight);

            TMP_InputField search = UIBuilderHelper.CreateInputField(panelRect, "Search", "search fields...",
                new Vector2(0.02f, 0.9f), new Vector2(0.5f, 0.9f));
            search.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, SearchHeight);
            search.onValueChanged.AddListener(text => { _filter = text ?? string.Empty; Populate(); });

            UIBuilderHelper.ScrollAreaResult list = UIBuilderHelper.CreateScrollArea(panelRect, "Fields",
                new Vector2(0.02f, 0.08f), new Vector2(0.98f, 0.88f), VAConfigTheme.BodyBg);
            _listContent = list.Content;

            _status = UIBuilderHelper.CreateLabel(panelRect, "Status", StatusLine(), HintFontSize,
                VAConfigTheme.Hint, new Vector2(0.02f, 0.045f), new Vector2(0.98f, 0.08f),
                TextAlignmentOptions.Left);

            UIBuilderHelper.CreateButton(panelRect, "Apply", new Vector2(0.02f, 0.005f), new Vector2(0.22f, 0.045f), OnApply);
            UIBuilderHelper.CreateButton(panelRect, "Clear extra data", new Vector2(0.24f, 0.005f), new Vector2(0.52f, 0.045f), OnClear);
            UIBuilderHelper.CreateButton(panelRect, "Close", new Vector2(0.78f, 0.005f), new Vector2(0.98f, 0.045f), Close);

            Populate();
        }

        private void Populate()
        {
            _inputs.Clear();
            for (int index = _listContent.childCount - 1; index >= 0; index--)
                Destroy(_listContent.GetChild(index).gameObject);

            VAExtraDataQueue.TryGetFor(_prefabName, out List<VAExtraValue> armed);
            float y = 0f;
            int shown = 0;
            foreach (VAExtraField field in _fields)
            {
                if (!Matches(field)) continue;
                BuildRow(field, armed, y);
                y -= RowHeight + RowGap;
                shown++;
            }
            _listContent.sizeDelta = new Vector2(0f, shown * (RowHeight + RowGap));
            if (_status != null) _status.text = StatusLine();
        }

        private void BuildRow(VAExtraField field, List<VAExtraValue> armed, float y)
        {
            GameObject row = UIBuilderHelper.CreatePanel(_listContent, "Row_" + field.Key,
                new Vector2(0f, 1f), new Vector2(1f, 1f), VAConfigTheme.FieldBg);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, y);
            rowRect.sizeDelta = new Vector2(0f, RowHeight);

            Color labelColor = field.Settable ? VAConfigTheme.Label : VAConfigTheme.Hint;
            UIBuilderHelper.CreateLabel(rowRect, "Label", field.Key, LabelFontSize, labelColor,
                new Vector2(0.01f, 0.5f), new Vector2(LabelWidthFraction, 1f), TextAlignmentOptions.Left);
            UIBuilderHelper.CreateLabel(rowRect, "Hint",
                field.Settable ? field.Hint : field.UnsettableReason, HintFontSize, VAConfigTheme.Hint,
                new Vector2(0.01f, 0f), new Vector2(0.99f, 0.5f), TextAlignmentOptions.Left);

            if (!field.Settable) return;

            TMP_InputField input = UIBuilderHelper.CreateInputField(rowRect, "Value", field.PrefabValue,
                new Vector2(LabelWidthFraction + 0.01f, 0.5f), new Vector2(0.99f, 1f));
            input.text = ArmedValueOf(armed, field.Key);
            _inputs[field.Key] = input;
        }

        private static string ArmedValueOf(List<VAExtraValue> armed, string key)
        {
            if (armed == null) return string.Empty;
            foreach (VAExtraValue value in armed)
                if (value.Key == key) return VAExtraFieldCatalogue.Format(value.Kind, value.Value);
            return string.Empty;
        }

        private bool Matches(VAExtraField field) =>
            _filter.Length == 0 || field.Key.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0;

        private void OnApply()
        {
            var values = new List<VAExtraValue>();
            foreach (VAExtraField field in _fields)
            {
                if (!field.Settable) continue;
                if (!_inputs.TryGetValue(field.Key, out TMP_InputField input)) continue;
                string typed = (input.text ?? string.Empty).Trim();
                if (typed.Length == 0) continue;

                if (!VAExtraFieldCatalogue.TryParse(field.Kind, typed, out object parsed, out string problem))
                {
                    _status.text = $"{field.Key}: {problem}. Nothing applied.";
                    return;
                }
                values.Add(new VAExtraValue { Key = field.Key, Kind = field.Kind, Value = parsed });
            }

            if (values.Count == 0)
            {
                _status.text = "No fields filled in - nothing to arm.";
                return;
            }

            VAExtraDataQueue.Arm(_prefabName, values);
            VAHammerArmHelper.ArmAndClose(_prefabName);
            Tell($"Extra data armed: {values.Count} field(s) on {_prefabName}");
            Close();
        }

        private void OnClear()
        {
            VAExtraDataQueue.Clear();
            Tell("Extra data cleared");
            Populate();
        }

        private string StatusLine()
        {
            int settable = 0;
            foreach (VAExtraField field in _fields) if (field.Settable) settable++;
            string armed = VAExtraDataQueue.ArmedPrefab == _prefabName ? VAExtraDataQueue.Describe() : "nothing armed";
            return $"{settable} settable field(s) of {_fields.Count} on {_prefabName}. {armed}. "
                + "Values stay armed until the hammer moves to another piece.";
        }

        private static void Tell(string message)
        {
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            Debug.Log($"{VaPieces.PluginName}: {message}");
        }
    }
}
