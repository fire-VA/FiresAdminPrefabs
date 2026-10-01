using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Flat ring drawn on the ground at the placement-ghost position when a
    // brush tool is selected. Five tools share the indicator, distinguished
    // by ring color: clutter paint (yellow), FX clear (red), clutter clear
    // (green), spawner/monster clear (teal), container clear (purple).
    //
    // Scroll wheel: normal scroll always drives radius. Shift+scroll varies
    // the clutter-paint brush scale; the other brushes ignore shift.
    public sealed class VAClutterBrushIndicator : MonoBehaviour
    {
        private const int Segments = 64;
        private const float LineWidth = 0.15f;
        private const float MinRadius = 0.5f;
        private const float MaxRadius = 30f;
        private const float ScrollStepRadius = 0.5f;
        private const float ScrollStepBrushScale = 0.1f;
        private const float WheelDeadzone = 0.01f;
        private const float RingHeightOffset = 0.05f;

        private static VAClutterBrushIndicator _instance;

        private LineRenderer _line;
        private Vector3[] _vertexBuf;
        private Color _lastRingColor = Color.clear;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("VA_ClutterBrushIndicator");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<VAClutterBrushIndicator>();
        }

        private void Awake()
        {
            _line = gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.loop = true;
            _line.positionCount = Segments;
            _line.startWidth = LineWidth;
            _line.endWidth = LineWidth;

            Shader sh = Shader.Find("Particles/Standard Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var mat = new Material(sh);
            mat.color = new Color(1f, 0.92f, 0.2f, 0.95f);
            _line.material = mat;
            _line.startColor = mat.color;
            _line.endColor = mat.color;

            _vertexBuf = new Vector3[Segments];
            _line.enabled = false;
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null) { Hide(); return; }
            var ghost = player.m_placementGhost;
            if (ghost == null) { Hide(); return; }

            var piece = ghost.GetComponent<Piece>();
            if (piece == null) { Hide(); return; }
            string ghostName = piece.gameObject.name;
            bool isClutterStandin = VAClutterStandinFactory.IsStandinName(ghostName);
            bool isFxClearStandin = VAFapEwpClearTool.IsStandinName(ghostName);
            bool isClutterClearStandin = VAClutterClearTool.IsStandinName(ghostName);
            bool isMonsterClearStandin = VAFapMonsterClearTool.IsStandinName(ghostName);
            bool isContainerClearStandin = VAFapContainerClearTool.IsStandinName(ghostName);
            bool isRawClutter = !isClutterStandin && !isFxClearStandin && !isClutterClearStandin
                                && !isMonsterClearStandin && !isContainerClearStandin
                                && VAVanillaPieceManager.IsClutterPrefab_Public(piece.gameObject);
            if (!isClutterStandin && !isFxClearStandin && !isClutterClearStandin
                && !isMonsterClearStandin && !isContainerClearStandin && !isRawClutter)
            { Hide(); return; }

            bool shiftDown = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            bool wheelActive = wheel > WheelDeadzone || wheel < -WheelDeadzone;

            float radius;
            string label;
            Color ringColor;

            if (isClutterClearStandin)
            {
                label = "Clutter clear brush";
                ringColor = new Color(0.4f, 0.85f, 0.55f, 0.95f);
                if (wheelActive && !shiftDown)
                {
                    float newR = Mathf.Clamp(
                        VAClutterClearTool.Radius + Mathf.Sign(wheel) * ScrollStepRadius,
                        VAClutterClearTool.MinRadius,
                        VAClutterClearTool.MaxRadius);
                    if (!Mathf.Approximately(newR, VAClutterClearTool.Radius))
                    {
                        VAClutterClearTool.Radius = newR;
                        player.Message(MessageHud.MessageType.TopLeft, $"{label} radius: {newR:F1}m");
                    }
                }
                radius = VAClutterClearTool.Radius;
            }
            else if (isFxClearStandin)
            {
                var radiusCfg = ConfigManager.Instance.configFxClearRadius;
                label = "FX clear brush";
                ringColor = new Color(0.95f, 0.25f, 0.25f, 0.95f);
                if (wheelActive && !shiftDown)
                {
                    float newR = Mathf.Clamp(radiusCfg.Value + Mathf.Sign(wheel) * ScrollStepRadius,
                        MinRadius, MaxRadius);
                    if (!Mathf.Approximately(newR, radiusCfg.Value))
                    {
                        radiusCfg.Value = newR;
                        player.Message(MessageHud.MessageType.TopLeft, $"{label} radius: {newR:F1}m");
                    }
                }
                radius = radiusCfg.Value;
            }
            else if (isMonsterClearStandin)
            {
                var radiusCfg = ConfigManager.Instance.configFxClearRadius;
                label = "Spawner clear brush";
                ringColor = new Color(0.16f, 0.67f, 0.78f, 0.95f);
                if (wheelActive && !shiftDown)
                {
                    float newR = Mathf.Clamp(radiusCfg.Value + Mathf.Sign(wheel) * ScrollStepRadius,
                        MinRadius, MaxRadius);
                    if (!Mathf.Approximately(newR, radiusCfg.Value))
                    {
                        radiusCfg.Value = newR;
                        player.Message(MessageHud.MessageType.TopLeft, $"{label} radius: {newR:F1}m");
                    }
                }
                radius = radiusCfg.Value;
            }
            else if (isContainerClearStandin)
            {
                var radiusCfg = ConfigManager.Instance.configFxClearRadius;
                label = "Container clear brush";
                ringColor = new Color(0.66f, 0.43f, 0.90f, 0.95f);
                if (wheelActive && !shiftDown)
                {
                    float newR = Mathf.Clamp(radiusCfg.Value + Mathf.Sign(wheel) * ScrollStepRadius,
                        MinRadius, MaxRadius);
                    if (!Mathf.Approximately(newR, radiusCfg.Value))
                    {
                        radiusCfg.Value = newR;
                        player.Message(MessageHud.MessageType.TopLeft, $"{label} radius: {newR:F1}m");
                    }
                }
                radius = radiusCfg.Value;
            }
            else
            {
                var radiusCfg = ConfigManager.Instance.configClutterPaintRadius;
                label = "Clutter brush";
                ringColor = new Color(1f, 0.92f, 0.2f, 0.95f);
                if (wheelActive)
                {
                    if (shiftDown)
                    {
                        float newS = Mathf.Clamp(
                            VAClutterPaintTool.BrushScale + Mathf.Sign(wheel) * ScrollStepBrushScale,
                            VAClutterPaintTool.MinBrushScale,
                            VAClutterPaintTool.MaxBrushScale);
                        if (!Mathf.Approximately(newS, VAClutterPaintTool.BrushScale))
                        {
                            VAClutterPaintTool.BrushScale = newS;
                            player.Message(MessageHud.MessageType.TopLeft, $"{label} scale: ×{newS:F2}");
                        }
                    }
                    else
                    {
                        float newR = Mathf.Clamp(radiusCfg.Value + Mathf.Sign(wheel) * ScrollStepRadius,
                            MinRadius, MaxRadius);
                        if (!Mathf.Approximately(newR, radiusCfg.Value))
                        {
                            radiusCfg.Value = newR;
                            player.Message(MessageHud.MessageType.TopLeft, $"{label} radius: {newR:F1}m");
                        }
                    }
                }
                radius = radiusCfg.Value;
            }

            ApplyRingColor(ringColor);

            Vector3 center = ghost.transform.position;
            for (int i = 0; i < Segments; i++)
            {
                float t = (float)i / Segments * Mathf.PI * 2f;
                _vertexBuf[i] = new Vector3(
                    center.x + Mathf.Cos(t) * radius,
                    center.y + RingHeightOffset,
                    center.z + Mathf.Sin(t) * radius);
            }
            _line.SetPositions(_vertexBuf);
            if (!_line.enabled) _line.enabled = true;
        }

        private void Hide()
        {
            if (_line != null && _line.enabled) _line.enabled = false;
        }

        private void ApplyRingColor(Color c)
        {
            if (_line == null) return;
            if (_lastRingColor == c) return;
            _lastRingColor = c;
            _line.startColor = c;
            _line.endColor = c;
            if (_line.material != null && _line.material.HasProperty("_Color"))
                _line.material.color = c;
        }
    }
}
