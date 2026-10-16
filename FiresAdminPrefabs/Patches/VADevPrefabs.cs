using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // 1.1.252 (Fire 10-14): dev / "questionably safe" prefabs (e.g. SeekerEgg_alwayshatch) get a caution badge in the lower-left
    // of their build-grid cell plus a "Dev prefab - may behave oddly" line in the piece info, and [Build Menu] HideDevPrefabs
    // (default OFF - admins can place anything) takes them out of the menu. Detection is by name only, decided once per prefab.
    // 1.1.270 (Check-HarmonyClassAttributes FAIL since 1.1.251-254): method-level [HarmonyPatch] only, so FAP's per-type
    // CreateClassProcessor never applied this class - the feature was silently off. The class-level attribute turns it on.
    [HarmonyPatch]
    internal static class VADevPrefabs
    {
        internal const string TooltipLine = "Dev prefab - may behave oddly";
        private static readonly Regex DevName = new Regex(
            @"(_alwayshatch|(^|_)dev(_|$)|(^|_)debug(_|$)|(^|_)test(_|$)|_testing$|^dbg_|_placeholder$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, bool> s_cache = new Dictionary<string, bool>();
        private static bool s_logged;

        internal static bool IsDev(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (!s_cache.TryGetValue(prefabName, out bool dev)) s_cache[prefabName] = dev = DevName.IsMatch(prefabName);
            return dev;
        }

        internal static bool IsDev(Piece piece) => piece != null && IsDev(Utils.GetPrefabName(piece.gameObject));

        internal static bool HideOn => ConfigManager.Instance?.configHideDevPrefabs?.Value == true;

        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterUpdateAvailable(PieceTable __instance)
        {
            if (__instance?.m_availablePieces == null) return;
            var dev = __instance.m_availablePieces.Where(IsDev).ToList();
            if (!s_logged && dev.Count > 0)
            {
                s_logged = true;
                VaPieces.Log?.LogInfo($"[FAP] dev prefabs {(HideOn ? "hidden" : "badged")}: {dev.Count} (e.g. {string.Join(", ", dev.Take(5).Select(p => p.name).ToArray())})");
            }
            if (!HideOn || dev.Count == 0) return;
            foreach (var p in dev) __instance.m_availablePieces.Remove(p);
            if (__instance.m_availablePiecesByCategory != null)
                foreach (var list in __instance.m_availablePiecesByCategory) list?.RemoveAll(IsDev);
        }

        [HarmonyPatch(typeof(Hud), "SetupPieceInfo"), HarmonyPostfix]
        private static void AfterPieceInfo(Hud __instance, Piece piece)
        {
            if (!IsDev(piece) || __instance.m_pieceDescription == null) return;
            if (__instance.m_pieceDescription.text.Contains(TooltipLine)) return;
            __instance.m_pieceDescription.text += $"\n<color=#FFB000>{TooltipLine}</color>";
        }

        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup)), HarmonyPostfix]
        private static void AfterButtonSetup(BuildUiPieceButton __instance) => VADevPrefabBadge.Apply(__instance);
    }

    // Lower-left caution triangle, code-drawn once (Fire 10-14: own corner; upper-left = persist disc, upper-right = bake-cache badge).
    internal sealed class VADevPrefabBadge : MonoBehaviour
    {
        private const int Tex = 32;
        private const float SizePx = 15f;
        private static readonly Vector2 BottomLeft = new Vector2(0f, 0f);
        private static Sprite s_sprite;
        private BuildUiPieceButton _button;
        private GameObject _badge;

        internal static void Apply(BuildUiPieceButton button)
        {
            var b = button.GetComponent<VADevPrefabBadge>();
            if (b != null) { b.Refresh(); return; }
            if (!VADevPrefabs.IsDev(button.Piece)) return;
            b = button.gameObject.AddComponent<VADevPrefabBadge>();
            b._button = button;
            b._badge = Create(button.transform);
            b.Refresh();
        }

        private void Refresh() => _badge.SetActive(VADevPrefabs.IsDev(_button.Piece));

        private static GameObject Create(Transform parent)
        {
            var go = new GameObject("fap_dev_badge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = Sprite(); img.raycastTarget = false;
            var r = img.rectTransform;
            r.anchorMin = r.anchorMax = r.pivot = BottomLeft;
            r.anchoredPosition = new Vector2(2f, 2f);
            r.sizeDelta = new Vector2(SizePx, SizePx);
            go.transform.SetAsLastSibling();
            return go;
        }

        // Amber triangle, dark rim, dark "!" - readable at 15 px.
        private static Sprite Sprite()
        {
            if (s_sprite != null) return s_sprite;
            var t = new Texture2D(Tex, Tex, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[Tex * Tex];
            var fill = new Color32(255, 176, 0, 255); var rim = new Color32(25, 25, 25, 235); var clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < Tex; y++)
                for (int x = 0; x < Tex; x++)
                {
                    float fy = y / (float)(Tex - 1);              // 0 bottom .. 1 apex
                    float half = (1f - fy) * 0.5f;                 // half-width at this row
                    float dx = Mathf.Abs(x / (float)(Tex - 1) - 0.5f);
                    Color32 c = clear;
                    if (fy >= 0.02f && dx <= half)
                    {
                        bool edge = fy < 0.12f || dx > half - 0.11f;
                        bool bang = dx < 0.06f && ((fy > 0.38f && fy < 0.72f) || (fy > 0.17f && fy < 0.29f));
                        c = edge || bang ? rim : fill;
                    }
                    px[y * Tex + x] = c;
                }
            t.SetPixels32(px); t.Apply();
            s_sprite = UnityEngine.Sprite.Create(t, new Rect(0, 0, Tex, Tex), new Vector2(0.5f, 0.5f), 100f);
            s_sprite.name = "FAP_DevBadge";
            return s_sprite;
        }
    }
}
