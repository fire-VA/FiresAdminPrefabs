using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Shared parts of the area clear brushes (FX, spawner, container and clutter): the hidden
    // stand-in pieces the hammer places and where they sit in the build menu, their procedural
    // "delete ring" icons, the radius scan over loaded networked objects, and the watchdog that
    // repeats a sweep so objects ExpandWorld respawns while a clear is still syncing go too.
    internal static class VAAreaClearBrush
    {
        // Decides which icon pixels get the brush's white mark; offsets are from the icon centre.
        public delegate bool IconMark(float offsetX, float offsetY);

        private const int IconSize = 128;
        private const float IconPixelsPerUnit = 100f;
        private const float IconOuterRadius = IconSize * 0.46f;
        private const float IconInnerRadius = IconSize * 0.36f;
        private const float DefaultWatchdogIntervalSec = 0.5f;
        private const float MinWatchdogIntervalSec = 0.1f;
        private static readonly float InverseSqrtTwo = 1f / Mathf.Sqrt(2f);
        private static readonly Color32 IconInk = new Color32(245, 245, 245, 255);
        private static readonly Color32 Transparent = new Color32(0, 0, 0, 0);

        private static readonly Dictionary<Piece, string> _brushKindByPiece = new Dictionary<Piece, string>();
        private static GameObject _standinRoot;
        private static CoroutineHost _coroutineHost;

        private sealed class CoroutineHost : MonoBehaviour { }

        // An inactive, non-removable piece with no mesh: the placement ghost follows the camera ray
        // on its own, and VAClutterBrushIndicator draws the radius ring.
        // brushKind groups the stand-ins of one brush that lives in several tabs.
        public static GameObject CreateStandin(string brushKind, string name, string displayName, string description, Piece.PieceCategory category, Sprite icon)
        {
            if (_standinRoot == null)
            {
                _standinRoot = new GameObject("FAP_ClearBrushStandins");
                _standinRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_standinRoot);
            }

            var standin = new GameObject(name);
            standin.transform.SetParent(_standinRoot.transform, worldPositionStays: false);
            standin.SetActive(false);

            var piece = standin.AddComponent<Piece>();
            piece.m_name = displayName;
            piece.m_description = description;
            piece.m_enabled = true;
            piece.m_category = category;
            piece.m_clipGround = true;
            piece.m_clipEverything = true;
            piece.m_allowRotatedOverlap = true;
            piece.m_canBeRemoved = false;
            piece.m_resources = Array.Empty<Piece.Requirement>();
            piece.m_icon = icon;
            _brushKindByPiece[piece] = brushKind;
            return standin;
        }

        // Moves the brushes in a build menu list to just after the icons at its head (repair and
        // remove, and Infinity Hammer's back button and tools). A list spanning several tabs, like
        // Show All, would otherwise show a multi-tab brush once per tab, so each kind appears once.
        public static void PlaceBrushesAfterMenuIcons(IList<Piece> pieces)
        {
            if (pieces == null || _brushKindByPiece.Count == 0) return;

            var brushes = new List<Piece>();
            var seenKinds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = pieces.Count - 1; i >= 0; i--)
            {
                var piece = pieces[i];
                if (piece == null || !_brushKindByPiece.ContainsKey(piece)) continue;
                brushes.Insert(0, piece);
                pieces.RemoveAt(i);
            }
            brushes.RemoveAll(brush => !seenKinds.Add(_brushKindByPiece[brush]));
            if (brushes.Count == 0) return;

            int insertAt = 0;
            while (insertAt < pieces.Count && IsMenuIcon(pieces[insertAt])) insertAt++;
            for (int i = 0; i < brushes.Count; i++) pieces.Insert(insertAt + i, brushes[i]);
        }

        private static bool IsMenuIcon(Piece piece) =>
            piece != null && (piece.m_repairPiece || piece.m_removePiece || VAExternalMenuPieces.IsMenuPiece(piece));

        // A disc with a darker ring in the brush's colours, with the brush's own mark drawn over it.
        public static Sprite CreateIcon(string spriteName, Color32 ringColor, Color32 fillColor, IconMark mark)
        {
            var pixels = new Color32[IconSize * IconSize];
            float center = IconSize * 0.5f;
            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    float offsetX = x - center;
                    float offsetY = y - center;
                    float distanceSqr = offsetX * offsetX + offsetY * offsetY;
                    Color32 color = Transparent;
                    if (distanceSqr <= IconOuterRadius * IconOuterRadius)
                        color = distanceSqr >= IconInnerRadius * IconInnerRadius ? ringColor : fillColor;
                    if (mark(offsetX, offsetY)) color = IconInk;
                    pixels[y * IconSize + x] = color;
                }
            }

            var texture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            var sprite = Sprite.Create(texture, new Rect(0, 0, IconSize, IconSize), new Vector2(0.5f, 0.5f), IconPixelsPerUnit);
            sprite.name = spriteName;
            return sprite;
        }

        public static bool IsInsideIconFill(float offsetX, float offsetY) =>
            offsetX * offsetX + offsetY * offsetY < IconInnerRadius * IconInnerRadius;

        public static float DistanceToIconDiagonal(float offsetX, float offsetY) =>
            Mathf.Abs(offsetX - offsetY) * InverseSqrtTwo;

        public static bool IsNearIconSegment(float offsetX, float offsetY, Vector2 from, Vector2 to, float halfThickness)
        {
            var point = new Vector2(offsetX, offsetY);
            var segment = to - from;
            float along = Mathf.Clamp01(Vector2.Dot(point - from, segment) / segment.sqrMagnitude);
            return (from + segment * along - point).sqrMagnitude <= halfThickness * halfThickness;
        }

        // Valid networked objects within radius of center on the XZ plane.
        public static List<ZNetView> FindNetViewsInRadius(Vector3 center, float radius)
        {
            var found = new List<ZNetView>();
            var scene = ZNetScene.instance;
            if (scene == null) return found;

            float radiusSqr = radius * radius;
            foreach (var netView in scene.m_instances.Values)
            {
                if (netView == null || !netView.IsValid()) continue;
                Vector3 position = netView.transform.position;
                float dx = position.x - center.x;
                float dz = position.z - center.z;
                if (dx * dx + dz * dz <= radiusSqr) found.Add(netView);
            }
            return found;
        }

        // Repeats sweep every FxClear WatchdogTickIntervalSec for durationSec, then calls onFinished.
        public static void RunWatchdog(float durationSec, Action sweep, Action onFinished = null)
        {
            if (durationSec <= 0f) return;
            if (_coroutineHost == null)
            {
                var hostObject = new GameObject("FAP_ClearBrushWatchdog");
                UnityEngine.Object.DontDestroyOnLoad(hostObject);
                _coroutineHost = hostObject.AddComponent<CoroutineHost>();
            }
            _coroutineHost.StartCoroutine(Watchdog(durationSec, sweep, onFinished));
        }

        private static IEnumerator Watchdog(float durationSec, Action sweep, Action onFinished)
        {
            float interval = ConfigManager.Instance?.configFxClearTickIntervalSec?.Value ?? DefaultWatchdogIntervalSec;
            interval = Mathf.Max(interval, MinWatchdogIntervalSec);

            for (float elapsed = 0f; elapsed < durationSec; elapsed += interval)
            {
                yield return new WaitForSeconds(interval);
                try { sweep(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: clear brush watchdog sweep threw: {ex.Message}");
                }
            }
            onFinished?.Invoke();
        }
    }
}
