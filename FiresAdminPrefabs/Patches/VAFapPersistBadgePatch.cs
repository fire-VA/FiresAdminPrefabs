using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Badges build menu buttons whose piece is opted in for FAP persistence. BuildUi pools its buttons
    // and runs Setup() again on every refresh, but the first open of a session instantiates one button
    // per piece (8,363 in the admin view), so a badge is only built once a button's piece is opted in:
    // eagerly badging every fresh button cost ~0.7 s of that open. A button keeps its badge once it
    // has one and hides it again when reused for a piece that isn't opted in.
    [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup))]
    public static class BuildUiPieceButton_Setup_VAPersistBadge_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(BuildUiPieceButton __instance) => VAPieceButtonPersistBadge.Apply(__instance);
    }

    internal sealed class VAPieceButtonPersistBadge : MonoBehaviour
    {
        private const string BadgeObjectName = "fap_persist_badge";
        private const string BadgeSpriteName = "FAP_PersistBadge";
        private const float BadgeSizePx = 14f;
        private const float BadgeInsetPx = 2f;
        private const int BadgeTextureSize = 32;
        private const float BadgeOuterRadiusFraction = 0.46f;
        private const float BadgeInnerRadiusFraction = 0.36f;
        private const float BadgePixelsPerUnit = 100f;

        private static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        private static readonly Vector2 SpriteCenterPivot = new Vector2(0.5f, 0.5f);
        private static readonly Color BadgeTint = new Color(1f, 0.55f, 0.1f, 1f);
        private static readonly Color32 BadgeFill = new Color32(255, 255, 255, 255);
        private static readonly Color32 BadgeOutline = new Color32(30, 30, 30, 230);
        private static readonly Color32 Transparent = new Color32(0, 0, 0, 0);

        private static Sprite _badgeSprite;
        private static bool _followingOptIns;
        private static AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> _shownButtons;
        private static bool _shownButtonsResolved;

        private BuildUiPieceButton _button;
        private GameObject _badge;

        internal static void Apply(BuildUiPieceButton button)
        {
            FollowOptInChanges();
            var badge = button.GetComponent<VAPieceButtonPersistBadge>();
            if (badge != null)
            {
                badge.Refresh();
                return;
            }
            if (!IsOptedIn(button)) return;
            badge = button.gameObject.AddComponent<VAPieceButtonPersistBadge>();
            badge.Bind(button);
        }

        // One subscription for every button: an opt-in toggled from the menu badges or un-badges the
        // buttons on screen right away; pooled buttons pick it up on their next Setup.
        private static void FollowOptInChanges()
        {
            if (_followingOptIns) return;
            _followingOptIns = true;
            VAFapPersistentOptIn.Changed += ApplyToShownButtons;
        }

        private static void ApplyToShownButtons()
        {
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            var buttons = buildUi != null ? ShownButtons(buildUi) : null;
            if (buttons == null) return;
            foreach (var button in buttons)
                if (button != null) Apply(button);
        }

        private static List<BuildUiPieceButton> ShownButtons(BuildUi buildUi)
        {
            if (!_shownButtonsResolved)
            {
                _shownButtonsResolved = true;
                try { _shownButtons = AccessTools.FieldRefAccess<BuildUi, List<BuildUiPieceButton>>("m_pieceButtons"); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: BuildUi.m_pieceButtons did not resolve ({ex.GetType().Name}) - " +
                                     "a persistence opt-in shows on the build menu after it is reopened.");
                }
            }
            return _shownButtons != null ? _shownButtons(buildUi) : null;
        }

        private static bool IsOptedIn(BuildUiPieceButton button)
        {
            var piece = button.Piece;
            return piece != null && VAFapPersistentOptIn.IsOptedIn(piece.gameObject.name);
        }

        private void Bind(BuildUiPieceButton button)
        {
            _button = button;
            _badge = CreateBadge(button.transform);
            Refresh();
        }

        private void Refresh() => _badge.SetActive(IsOptedIn(_button));

        private static GameObject CreateBadge(Transform button)
        {
            var badge = new GameObject(BadgeObjectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            badge.transform.SetParent(button, false);

            var image = badge.GetComponent<Image>();
            image.sprite = GetOrCreateBadgeSprite();
            image.color = BadgeTint;
            image.raycastTarget = false;

            var rect = image.rectTransform;
            rect.anchorMin = TopLeft;
            rect.anchorMax = TopLeft;
            rect.pivot = TopLeft;
            rect.anchoredPosition = new Vector2(BadgeInsetPx, -BadgeInsetPx);
            rect.sizeDelta = new Vector2(BadgeSizePx, BadgeSizePx);

            badge.transform.SetAsLastSibling();
            return badge;
        }

        // A white disc with a dark ring, drawn once and shared by every badge.
        private static Sprite GetOrCreateBadgeSprite()
        {
            if (_badgeSprite != null) return _badgeSprite;

            var texture = new Texture2D(BadgeTextureSize, BadgeTextureSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var center = Vector2.one * ((BadgeTextureSize - 1) / 2f);
            float outerRadius = BadgeTextureSize * BadgeOuterRadiusFraction;
            float innerRadius = BadgeTextureSize * BadgeInnerRadiusFraction;
            var pixels = new Color32[BadgeTextureSize * BadgeTextureSize];
            for (int y = 0; y < BadgeTextureSize; y++)
            {
                for (int x = 0; x < BadgeTextureSize; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    Color32 color = Transparent;
                    if (distance <= outerRadius) color = distance >= innerRadius ? BadgeOutline : BadgeFill;
                    pixels[y * BadgeTextureSize + x] = color;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();

            _badgeSprite = Sprite.Create(texture, new Rect(0, 0, BadgeTextureSize, BadgeTextureSize), SpriteCenterPivot, BadgePixelsPerUnit);
            _badgeSprite.name = BadgeSpriteName;
            return _badgeSprite;
        }
    }
}
