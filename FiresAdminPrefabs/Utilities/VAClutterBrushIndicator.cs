using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Cylinder preview drawn at the placement-ghost position when a brush tool is selected. The tools sharing the indicator
    // are told apart by ring color: clutter paint (yellow), FX clear / make persistent (red), clutter clear (green),
    // spawner/monster clear (teal), container clear (purple), Radius Swapper (blue, 1.1.291).
    //
    // 1.1.291 (Fire, TEAM RULE: every FAP radius tool uses wheel = radius and Shift+wheel = height): the wheel is read only through
    // VARadiusWheel (Core RadiusWheel when present). Plain wheel = this tool's radius, Shift+wheel = the one shared height
    // (VARadiusWheel.Height, cylinder centred on the aim), Ctrl/Alt + wheel = nothing. 1.1.292: the clutter paint brush
    // is the exception (no height): Shift+wheel = brush scale again, see below. The preview draws a top ring and a bottom ring at
    // aim.y +- height/2 joined by 4 posts (reused from FiresAdminTerrain ZoneModeAreaTool.Draw / Circle), and the hint
    // "Radius X m · Height Y m" shows at the bottom of the screen while the tool is held.
    public sealed class VAClutterBrushIndicator : MonoBehaviour
    {
        private const int Segments = 64;
        private const int Posts = 4;
        private const float LineWidth = 0.15f;
        private const float MinRadius = 0.5f;
        private const float MaxRadius = 30f;
        private const float ScrollStepRadius = 0.5f;
        private const float ScrollStepBrushScale = 0.1f;   // 1.1.292: restored from 1.1.290

        // 1.1.292 (Fire: Clutter Paint is the exception to the radius rule, it has no height): Shift+wheel = brush SCALE
        // (VAClutterPaintTool.BrushScale, the 1.1.290 step/clamp), one flat ring, and the placement ghost is drawn every frame
        // at VAClutterPaintTool.PreviewScale(prefab) = the mean of the exact range Paint picks each sample from.
        private GameObject _scaledGhost;
        private Vector3 _ghostBaseScale = Vector3.one;
        private GameObject _ghostPaintPrefab;

        internal enum Tool { None, ClutterPaint, FxClear, ClutterClear, SpawnerClear, ContainerClear, Swap }

        private static VAClutterBrushIndicator _instance;
        private static bool s_p7ClosedLogged;   // 1.1.325 (UI audit P7)

        private LineRenderer _top;
        private LineRenderer _bottom;
        private LineRenderer[] _posts;
        private Material _material;
        private Vector3[] _vertexBuf;
        private Color _lastRingColor = Color.clear;
        private string _hint;
        private GUIStyle _hintStyle;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("VA_ClutterBrushIndicator");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<VAClutterBrushIndicator>();
        }

        // Which FAP radius tool a ghost is (None for anything else). VASwapUi's scroll suppression asks the same question.
        internal static Tool Classify(Piece piece)
        {
            if (piece == null) return Tool.None;
            string ghostName = piece.gameObject.name;
            if (VARadiusSwapTool.IsStandinName(ghostName)) return Tool.Swap;
            if (VAClutterStandinFactory.IsStandinName(ghostName)) return Tool.ClutterPaint;
            // 1.1.260: the Make Persistent Brush uses the FX brush radius, so it shows the same ring
            if (VAFapEwpClearTool.IsStandinName(ghostName) || VAFapPersistBrushTool.IsStandinName(ghostName)) return Tool.FxClear;
            if (VAClutterClearTool.IsStandinName(ghostName)) return Tool.ClutterClear;
            if (VAFapMonsterClearTool.IsStandinName(ghostName)) return Tool.SpawnerClear;
            if (VAFapContainerClearTool.IsStandinName(ghostName)) return Tool.ContainerClear;
            if (VAVanillaPieceManager.IsClutterPrefab_Public(piece.gameObject)) return Tool.ClutterPaint;
            return Tool.None;
        }

        internal static Tool HeldTool()
        {
            var ghost = Player.m_localPlayer != null ? Player.m_localPlayer.m_placementGhost : null;
            return ghost != null ? Classify(ghost.GetComponent<Piece>()) : Tool.None;
        }

        private void Awake()
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Particles/Standard Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            _material = new Material(sh);
            _top = MakeLine("top", Segments, true);
            _bottom = MakeLine("bottom", Segments, true);
            _posts = new LineRenderer[Posts];
            for (int i = 0; i < Posts; i++) _posts[i] = MakeLine("post" + i, 2, false);
            _vertexBuf = new Vector3[Segments];
            Hide();
        }

        private LineRenderer MakeLine(string name, int points, bool loop)
        {
            var go = new GameObject("VA_BrushRing_" + name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = loop;
            line.positionCount = points;
            line.startWidth = LineWidth;
            line.endWidth = LineWidth;
            line.material = _material;
            line.enabled = false;
            return line;
        }

        private GameObject _lastGhost; private Tool _lastTool;   // 1.1.321 (P8)

        private void Update()
        {
            var player = Player.m_localPlayer;
            var ghost = player != null ? player.m_placementGhost : null;
            // 1.1.321 (UI audit P8): the ghost is classified once, not every frame
            if (ghost != _lastGhost) { _lastGhost = ghost; _lastTool = ghost != null ? Classify(ghost.GetComponent<Piece>()) : Tool.None; }
            var tool = _lastTool;
            if (tool == Tool.None) { Hide(); return; }

            string label;
            Color ringColor;
            switch (tool)
            {
                case Tool.Swap: label = "Radius Swapper"; ringColor = new Color(0.35f, 0.65f, 1f, 0.95f); break;
                case Tool.ClutterClear: label = "Clutter clear brush"; ringColor = new Color(0.4f, 0.85f, 0.55f, 0.95f); break;
                case Tool.FxClear: label = "FX clear brush"; ringColor = new Color(0.95f, 0.25f, 0.25f, 0.95f); break;
                case Tool.SpawnerClear: label = "Spawner clear brush"; ringColor = new Color(0.16f, 0.67f, 0.78f, 0.95f); break;
                case Tool.ContainerClear: label = "Container clear brush"; ringColor = new Color(0.66f, 0.43f, 0.90f, 0.95f); break;
                default: label = "Clutter brush"; ringColor = new Color(1f, 0.92f, 0.2f, 0.95f); break;
            }

            if (!FiresCore.Input.FiresInputBlock.IsTyping() && VARadiusWheel.Read(out float dRadius, out float dHeight))
            {
                bool changed = false;
                if (dRadius != 0f)
                {
                    float before = GetRadius(tool);
                    SetRadius(tool, before + Mathf.Sign(dRadius) * RadiusStep(tool));
                    changed = !Mathf.Approximately(before, GetRadius(tool));
                }
                if (dHeight != 0f && tool == Tool.ClutterPaint)
                {
                    float before = VAClutterPaintTool.BrushScale;
                    VAClutterPaintTool.BrushScale = Mathf.Clamp(before + Mathf.Sign(dHeight) * ScrollStepBrushScale,
                        VAClutterPaintTool.MinBrushScale, VAClutterPaintTool.MaxBrushScale);
                    changed |= !Mathf.Approximately(before, VAClutterPaintTool.BrushScale);
                }
                else if (dHeight != 0f)
                {
                    float before = VARadiusWheel.Height;
                    VARadiusWheel.StepHeight(dHeight);
                    changed |= !Mathf.Approximately(before, VARadiusWheel.Height);
                }
                if (changed) player.Message(MessageHud.MessageType.TopLeft, $"{label}: {HintFor(tool, GetRadius(tool))}");
            }

            float radius = GetRadius(tool);
            _hint = $"{label}: {HintFor(tool, radius)}";
            if (tool == Tool.Swap) _hint += "\n" + VARadiusSwapTool.PairText();

            ApplyRingColor(ringColor);
            Vector3 center = ghost.transform.position;
            if (tool == Tool.ClutterPaint)
            {
                ApplyGhostScale(ghost);
                Circle(_top, center, radius, center.y);   // 1.1.292: one flat ring, no height cylinder
                if (_bottom.enabled) _bottom.enabled = false;
                foreach (var p in _posts) if (p.enabled) p.enabled = false;
                return;
            }
            RestoreGhostScale();
            VARadiusWheel.Bounds(center.y, out float yMin, out float yMax);
            Circle(_top, center, radius, yMax);
            Circle(_bottom, center, radius, yMin);
            for (int i = 0; i < Posts; i++)
            {
                float angle = i * Mathf.PI * 2f / Posts;
                var foot = new Vector3(center.x + Mathf.Cos(angle) * radius, 0f, center.z + Mathf.Sin(angle) * radius);
                _posts[i].SetPosition(0, new Vector3(foot.x, yMin, foot.z));
                _posts[i].SetPosition(1, new Vector3(foot.x, yMax, foot.z));
                if (!_posts[i].enabled) _posts[i].enabled = true;
            }
        }

        // 1.1.292: "<Tool>: Radius X m · Scale ×Y" for Clutter Paint; every other tool keeps the radius + height hint.
        private static string HintFor(Tool tool, float radius) => tool == Tool.ClutterPaint
            ? $"Radius {radius:0.#} m · Scale ×{VAClutterPaintTool.ClampedBrushScale:0.00}"
            : VARadiusWheel.Hint(radius);

        // 1.1.292: the same prefab HammerClutterPaintPatch hands to Paint (stand-in -> VAClutterRegistry.ResolvePrefab,
        // a raw clutter prefab ghost -> the ghost itself), so PreviewScale reads the identical range.
        private void ApplyGhostScale(GameObject ghost)
        {
            if (_scaledGhost != ghost)
            {
                RestoreGhostScale();
                _scaledGhost = ghost;
                _ghostBaseScale = ghost.transform.localScale;
                string real = VAClutterStandinFactory.ResolveRealNameFromStandin(ghost);
                _ghostPaintPrefab = real != null ? VAClutterRegistry.ResolvePrefab(real) : ghost;
            }
            float s = _ghostPaintPrefab != null ? VAClutterPaintTool.PreviewScale(_ghostPaintPrefab) : VAClutterPaintTool.ClampedBrushScale;
            Vector3 want = _ghostBaseScale * s;
            if (ghost.transform.localScale != want) ghost.transform.localScale = want;
        }

        private void RestoreGhostScale()
        {
            if (_scaledGhost != null) _scaledGhost.transform.localScale = _ghostBaseScale;
            _scaledGhost = null;
            _ghostPaintPrefab = null;
        }

        private static float RadiusStep(Tool tool) => tool == Tool.Swap ? VARadiusSwapTool.RadiusStep : ScrollStepRadius;

        private static float GetRadius(Tool tool)
        {
            switch (tool)
            {
                case Tool.Swap: return VARadiusSwapTool.Radius;
                case Tool.ClutterClear: return VAClutterClearTool.Radius;
                case Tool.ClutterPaint: return ConfigManager.Instance.configClutterPaintRadius.Value;
                default: return ConfigManager.Instance.configFxClearRadius.Value;   // FX clear, make persistent, spawner, container
            }
        }

        private static void SetRadius(Tool tool, float value)
        {
            switch (tool)
            {
                case Tool.Swap:
                    VARadiusSwapTool.Radius = Mathf.Clamp(value, VARadiusSwapTool.MinRadius, VARadiusSwapTool.MaxRadius);
                    break;
                case Tool.ClutterClear:
                    VAClutterClearTool.Radius = Mathf.Clamp(value, VAClutterClearTool.MinRadius, VAClutterClearTool.MaxRadius);
                    break;
                case Tool.ClutterPaint:
                {
                    var cfg = ConfigManager.Instance.configClutterPaintRadius;
                    float v = Mathf.Clamp(value, MinRadius, MaxRadius);
                    if (!Mathf.Approximately(v, cfg.Value)) cfg.Value = v;
                    break;
                }
                default:
                {
                    var cfg = ConfigManager.Instance.configFxClearRadius;
                    float v = Mathf.Clamp(value, MinRadius, MaxRadius);
                    if (!Mathf.Approximately(v, cfg.Value)) cfg.Value = v;
                    break;
                }
            }
        }

        // reused from FiresAdminTerrain ZoneModeAreaTool.Circle
        private void Circle(LineRenderer line, Vector3 center, float radius, float height)
        {
            for (int i = 0; i < Segments; i++)
            {
                float t = (float)i / Segments * Mathf.PI * 2f;
                _vertexBuf[i] = new Vector3(center.x + Mathf.Cos(t) * radius, height, center.z + Mathf.Sin(t) * radius);
            }
            line.SetPositions(_vertexBuf);
            if (!line.enabled) line.enabled = true;
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_hint) || Event.current.type != EventType.Repaint) return;
            if (_hintStyle == null)
                _hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, richText = false };
            var rect = new Rect(0f, Screen.height - 150f, Screen.width, 60f);
            _hintStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), _hint, _hintStyle);
            _hintStyle.normal.textColor = new Color(0.94f, 0.88f, 0.77f, 1f);
            GUI.Label(rect, _hint, _hintStyle);
        }

        private void Hide()
        {
            if (_hint != null) if (!s_p7ClosedLogged) { s_p7ClosedLogged = true; VaPieces.Log?.LogDebug("[FAP] VAClutterBrushIndicator closed: OnGUI off until reopened"); }   // 1.1.325 (UI audit P7): OnGUI early-out (Update must keep polling the held ghost)
            _hint = null;
            RestoreGhostScale();
            if (_top != null && _top.enabled) _top.enabled = false;
            if (_bottom != null && _bottom.enabled) _bottom.enabled = false;
            if (_posts != null)
                foreach (var p in _posts) if (p != null && p.enabled) p.enabled = false;
        }

        private void ApplyRingColor(Color c)
        {
            if (_lastRingColor == c) return;
            _lastRingColor = c;
            foreach (var line in new[] { _top, _bottom })
            {
                line.startColor = c;
                line.endColor = c;
            }
            var dim = new Color(c.r, c.g, c.b, c.a * 0.7f);
            foreach (var p in _posts) { p.startColor = dim; p.endColor = dim; }
        }
    }
}
