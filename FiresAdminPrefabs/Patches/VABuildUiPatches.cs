using System.Linq;
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // Valheim 1.0 replaced the hammer build menu wholesale. The old one lived on Hud
    // (m_pieceSelectionWindow + m_pieceIcons + m_pieceCategoryTabs, a fixed 15x6 grid); the new one is a
    // separate BuildUi MonoBehaviour with its own pooled BuildUiPieceButtons, its own scroll rect, its own
    // search field, and a TAG system (BuildUiTagButton) in place of Piece.PieceCategory tabs.
    //
    // The legacy window is not merely unused - it is switched off and never switched back on. In the whole
    // 1.0 assembly m_pieceSelectionWindow appears exactly twice: the field declaration, and
    // SetActive(false) inside Hud.Awake. Hud.TogglePieceSelection now calls m_buildUi.OpenBuildMenu().
    // Hud.UpdatePieceList still runs every frame, painting into that hidden window, which is why FAP's
    // patches on it kept attaching cleanly and produced no errors and no visible effect.
    //
    // So everything FAP layered on the old menu had to move here. Scrolling and search were dropped rather
    // than ported - vanilla now ships both (m_pieceScrollRect, m_searchField/UpdateSearch), and maintaining
    // a parallel implementation of shipped functionality is not worth it. What vanilla still does NOT give
    // us is a movable/resizable window and a right-click menu on a piece, so those are rebuilt below.
    internal static class VABuildUiWindow
    {
        internal const float MinScale = 0.7f;
        internal const float MaxScale = 2.5f;

        // Grid-mode bounds, in canvas units. The stock window is 1085x490; allow it down a little and
        // up to roughly a 4K canvas so the grid can genuinely grow.
        internal const float MinWidth = 700f, MaxWidth = 3000f;
        internal const float MinHeight = 320f, MaxHeight = 1400f;

        // One small corner handle, like the pre-1.0 build menu had - not four big squares.
        private const float GripSize = 22f;   // 1.1.202: was 18

        private static readonly Color GripColor = new Color(1f, 0.78f, 0.28f, 0.75f);

        internal static float LoadScale()
        {
            var e = ConfigManager.Instance?.configBuildMenuScale;
            return e == null ? 1f : Mathf.Clamp(e.Value, MinScale, MaxScale);
        }

        internal static void SaveScale(float v)
        {
            var e = ConfigManager.Instance?.configBuildMenuScale;
            if (e != null) e.Value = Mathf.Clamp(v, MinScale, MaxScale);
        }

        internal static void SaveSize(Vector2 size)
        {
            var cfg = ConfigManager.Instance;
            if (cfg?.configBuildMenuWidth == null || cfg.configBuildMenuHeight == null) return;
            cfg.configBuildMenuWidth.Value  = Mathf.Clamp(size.x, MinWidth, MaxWidth);
            cfg.configBuildMenuHeight.Value = Mathf.Clamp(size.y, MinHeight, MaxHeight);
        }

        internal static void SavePosition(Vector2 screenPos)
        {
            var cfg = ConfigManager.Instance;
            if (cfg?.configBuildMenuPosX == null || cfg.configBuildMenuPosY == null) return;
            cfg.configBuildMenuPosX.Value = screenPos.x;
            cfg.configBuildMenuPosY.Value = screenPos.y;
        }

        // The BuildUi GameObject is what OpenBuildMenu/Close toggle, but it is NOT necessarily the
        // visible window: the panel the player sees can be a child, with the BuildUi object itself a
        // full-screen container. Anchoring the corner grips to a full-screen root puts them in the
        // screen corners - off the window, effectively invisible - which is exactly what "no resize
        // handles" looks like.
        //
        // m_pieceView is the scroll viewport holding the piece grid, so the window is somewhere on its
        // ancestor chain. Walk from it up to the root and take the HIGHEST node that still has a
        // background graphic and is not full-screen: that is the window chrome.
        private static RectTransform ResolveWindowPanel(BuildUi buildUi, RectTransform root)
        {
            var pieceView = AccessTools.Field(typeof(BuildUi), "m_pieceView")?.GetValue(buildUi) as RectTransform;
            if (pieceView == null) return root;

            // Take the OUTERMOST node that is not full-screen, and do NOT require a background graphic:
            // on the real prefab the chain is Piece List Container -> Root -> Main View -> SelectionWindow
            // -> bar -> BuildUIV2, and not one of them carries an Image - the wooden panel art hangs off a
            // child instead. Requiring an Image therefore matched nothing and fell through to the
            // full-screen root, which is what put the grips in the screen corners.
            RectTransform best = null;
            var chain = new System.Text.StringBuilder();
            for (var t = pieceView; t != null; t = t.parent as RectTransform)
            {
                var size = t.rect.size * t.lossyScale.x;
                bool fullScreen = size.x >= Screen.width * 0.98f || size.y >= Screen.height * 0.98f;
                chain.Append("\n    ").Append(t.name)
                     .Append("  rect=").Append(Mathf.RoundToInt(t.rect.width)).Append('x').Append(Mathf.RoundToInt(t.rect.height))
                     .Append("  anchors=").Append(t.anchorMin.x.ToString("0.##")).Append(',').Append(t.anchorMin.y.ToString("0.##"))
                     .Append("..").Append(t.anchorMax.x.ToString("0.##")).Append(',').Append(t.anchorMax.y.ToString("0.##"))
                     .Append(t.GetComponent<Image>() != null ? "  [Image]" : "")
                     .Append(fullScreen ? "  FULLSCREEN" : "");
                if (!fullScreen) best = t;
                if (t == root) break;
            }

            var chosen = best ?? root;

            // Log the root's direct children too: if the tab strip is a SIBLING of the window rather than
            // a child, scaling the window alone would leave the tabs behind, and this is the cheapest way
            // to see that without a live inspector.
            var kids = new System.Text.StringBuilder();
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i) as RectTransform;
                if (c == null) continue;
                kids.Append("\n    ").Append(c.name)
                    .Append("  rect=").Append(Mathf.RoundToInt(c.rect.width)).Append('x').Append(Mathf.RoundToInt(c.rect.height))
                    .Append(c == chosen ? "   <== chosen" : "");
            }

            Debug.Log($"{VaPieces.PluginName}: build-menu window chain (screen {Screen.width}x{Screen.height}):{chain}" +
                      $"\n  root children:{kids}" +
                      $"\n  -> resize/move target: {chosen.name}");
            return chosen;
        }

        // Attach once per BuildUi instance. Idempotent on the marker component so repeated
        // OpenBuildMenu calls don't stack grips or drag handlers.
        internal static void Ensure(BuildUi buildUi)
        {
            if (buildUi == null) return;
            var root = buildUi.transform as RectTransform;
            if (root == null) return;
            if (root.GetComponent<VABuildUiWindowMarker>() != null) return;
            root.gameObject.AddComponent<VABuildUiWindowMarker>();

            try
            {
                var panel = ResolveWindowPanel(buildUi, root);

                // 1.1.272 (UI audit, Tools\UI_HANDLING_AUDIT.md): Core is the ONE implementation of window move / scale / dock.
                // The build menu is a Core HUD element ("fap.buildmenu", FiresShell.RegisterHudElement): Core's layout mode moves
                // it, its corner grip (FiresResizeGrip) scales it, and Core saves the layout. FAP's own bottom-right grip, empty-
                // chrome drag and saved scale / position are retired. The grid's saved width / height stay as the window's size.
                NormalizeLayout(buildUi, panel);
                var vanillaSize = panel.rect.size;   // 1.1.274: what a layout Reset returns to
                RestoreGeometry(panel);
                if (!Utilities.CoreUiBridge.RegisterHud("fap.buildmenu", panel, "Build menu", "FiresAdminPrefabs", resizable: true,
                        sizeChanged: s => VABuildUiWindowMarker.QueueSize(s), minSize: new Vector2(MinWidth, MinHeight),
                        sizeReset: () => ResetSize(panel, vanillaSize)))   // 1.1.273: Core 0.2.574 SIZE mode = the grid width / height resize, as before
                    Debug.Log($"{VaPieces.PluginName}: build menu stays where vanilla puts it (Core older than 0.2.572 has no HUD element API)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: could not attach build-menu window controls: {ex.Message}");
            }
        }

        // Vanilla laid this window out at exactly ONE size, so only the nodes it happened to anchor as
        // stretch follow a resize. The tag list down the left side is not one of them: it keeps its
        // design-time rect while the piece grid grows past it, and the grid slides out underneath - which
        // is the overlap you get after a grid drag (a plain drag since 1.1.189).
        //
        // Nudging each node per drag would mean re-deriving the same offsets every frame. Normalising does
        // it once instead: read each node's CURRENT visual insets, re-express them as stretch anchors, and
        // from then on Unity keeps every child tracking its parent for free, at any size. Because the
        // insets are read from the live layout the conversion is visually a no-op, so it is safe to run on
        // nodes that were already stretched (and on those it is skipped outright).
        private static void NormalizeLayout(BuildUi buildUi, RectTransform panel)
        {
            var pieceView = FieldValue<RectTransform>(buildUi, "m_pieceView");
            if (pieceView != null)
            {
                for (var t = pieceView; t != null && t != panel; t = t.parent as RectTransform)
                    StretchToParent(t);
            }

            var tagList = FieldValue<RectTransform>(buildUi, "m_tagListContainer");
            if (tagList == null) return;
            var column = tagList.parent as RectTransform;
            if (column == null) return;

            // The search row is authored for the design-time column height, so once the tag list grows to
            // the full window it runs straight under the FILTER field and the F hint. Rather than guess
            // which node that row is, walk up from m_searchField to whichever ancestor is a direct child
            // of the tag column (or of the tag list itself) - that node is the row, keybind badge and
            // tool icon included, whatever the prefab calls it.
            //
            // Vanilla's own navigation says where it belongs: GetTagListEdgeNavigation sets
            // selectOnUp = m_searchField for the topmost tag, so pressing Up off the tag list lands on
            // the search field. The search row is ABOVE the tag list, and the tag list starts below it.
            var searchField = FieldValue<Component>(buildUi, "m_searchField");
            var searchRt = searchField != null ? searchField.transform as RectTransform : null;
            RectTransform searchRow = null;
            bool insideTagList = false;
            for (var t = searchRt; t != null && t != panel; t = t.parent as RectTransform)
            {
                if (t == tagList) break;
                if (t.parent == tagList) { searchRow = t; insideTagList = true; break; }
                if (t.parent == column) { searchRow = t; break; }
            }

            var scroll = FieldValue<ScrollRect>(buildUi, "m_tagListScrollRect");
            var scrollRt = scroll != null ? scroll.transform as RectTransform : null;

            LogColumn(column, tagList, searchRow, insideTagList);

            if (searchRow == null)
            {
                PinLeftVertical(tagList, InsetsInParent(tagList, column).y, InsetsInParent(tagList, column).w);
                if (scrollRt != null) { StretchToParent(scrollRt); StretchToParent(scroll.viewport); }
                return;
            }

            float gap = 6f;
            float rowHeight = searchRow.rect.height;

            if (insideTagList)
            {
                // Row and scroll view are both inside the tag list container: row on top, list under it.
                var tagInset = InsetsInParent(tagList, column);
                PinLeftVertical(tagList, tagInset.y, tagInset.w);
                PinTopRow(searchRow, 0f);
                if (scrollRt != null)
                {
                    PinLeftVertical(scrollRt, rowHeight + gap, InsetsInParent(scrollRt, tagList).w);
                    StretchToParent(scroll.viewport);
                }
            }
            else
            {
                // Row is a sibling of the tag list. Put it at the column's top and start the list below.
                var tagInset = InsetsInParent(tagList, column);
                PinTopRow(searchRow, tagInset.y);
                PinLeftVertical(tagList, tagInset.y + rowHeight + gap, tagInset.w);
                if (scrollRt != null) { StretchToParent(scrollRt); StretchToParent(scroll.viewport); }
            }
        }

        // One-shot dump of the tag column. The window is prefab-authored, so the only way to know what
        // actually sits next to the tag list is to print it once from the live hierarchy.
        private static void LogColumn(RectTransform column, RectTransform tagList, RectTransform searchRow, bool insideTagList)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{VaPieces.PluginName}: build-menu tag column '{column.name}' rect=")
              .Append(Mathf.RoundToInt(column.rect.width)).Append('x').Append(Mathf.RoundToInt(column.rect.height));
            AppendChildren(sb, column, tagList, searchRow, "  ");
            sb.Append("\n  search row: ").Append(searchRow != null ? searchRow.name : "<not found>")
              .Append(searchRow != null ? (insideTagList ? "  (inside tag list)" : "  (sibling of tag list)") : "");
            Debug.Log(sb.ToString());
        }

        private static void AppendChildren(System.Text.StringBuilder sb, RectTransform parent, RectTransform tagList, RectTransform searchRow, string indent)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i) as RectTransform;
                if (c == null) continue;
                var inset = InsetsInParent(c, parent);
                sb.Append('\n').Append(indent).Append(c.name)
                  .Append("  rect=").Append(Mathf.RoundToInt(c.rect.width)).Append('x').Append(Mathf.RoundToInt(c.rect.height))
                  .Append("  inset l/t/r/b=").Append(Mathf.RoundToInt(inset.x)).Append('/').Append(Mathf.RoundToInt(inset.y))
                  .Append('/').Append(Mathf.RoundToInt(inset.z)).Append('/').Append(Mathf.RoundToInt(inset.w))
                  .Append("  anchors=").Append(c.anchorMin.x.ToString("0.##")).Append(',').Append(c.anchorMin.y.ToString("0.##"))
                  .Append("..").Append(c.anchorMax.x.ToString("0.##")).Append(',').Append(c.anchorMax.y.ToString("0.##"))
                  .Append(c == tagList ? "   <== TAG LIST" : "")
                  .Append(c == searchRow ? "   <== SEARCH ROW" : "");
                if (c == tagList) AppendChildren(sb, c, tagList, searchRow, indent + "  ");
            }
        }

        private static T FieldValue<T>(object target, string name) where T : class
            => target == null ? null : AccessTools.Field(target.GetType(), name)?.GetValue(target) as T;

        // (left, top, right, bottom) gap between rt's rect and its parent's, in the parent's own space.
        private static Vector4 InsetsInParent(RectTransform rt, RectTransform parent)
        {
            var r = rt.rect;
            var bottomLeft = parent.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            var topRight   = parent.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            var pr = parent.rect;
            return new Vector4(bottomLeft.x - pr.xMin, pr.yMax - topRight.y, pr.xMax - topRight.x, bottomLeft.y - pr.yMin);
        }

        private static void StretchToParent(RectTransform rt)
        {
            var parent = rt != null ? rt.parent as RectTransform : null;
            if (parent == null) return;
            if (rt.anchorMin == Vector2.zero && rt.anchorMax == Vector2.one) return;
            var inset = InsetsInParent(rt, parent);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset.x, inset.w);
            rt.offsetMax = new Vector2(-inset.z, -inset.y);
        }

        // Fixed width, pinned to the parent's LEFT edge, spanning from topInset to bottomInset. Vanilla's
        // SetTagListActive keeps writing m_pieceView.offsetMin.x = m_tagListOpenOffset, so the grid starts
        // a constant distance in from that same edge and the two can no longer cross.
        private static void PinLeftVertical(RectTransform rt, float topInset, float bottomInset)
        {
            var parent = rt != null ? rt.parent as RectTransform : null;
            if (parent == null) return;
            var inset = InsetsInParent(rt, parent);
            float width = rt.rect.width;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(inset.x, bottomInset);
            rt.offsetMax = new Vector2(inset.x + width, -topInset);
        }

        // Fixed size, pinned to the parent's TOP-LEFT at topInset. Keeps its current width and height, so
        // the keybind badge and tool icon inside it keep their own layout.
        private static void PinTopRow(RectTransform rt, float topInset)
        {
            var parent = rt != null ? rt.parent as RectTransform : null;
            if (parent == null) return;
            var inset = InsetsInParent(rt, parent);
            float w = rt.rect.width, h = rt.rect.height;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(inset.x, -topInset);
            rt.sizeDelta = new Vector2(w, h);
        }
        // Zoom and grid size are kept separately: a drag or a Shift+drag never discards the other one.
        // 1.1.274 (Core 0.2.579 SizeReset): the layout Reset clears the saved grid size (NaN = vanilla) and puts the window back to vanilla's size
        private static void ResetSize(RectTransform panel, Vector2 vanillaSize)
        {
            VABuildUiWindowMarker.CancelPending();
            var cfg = ConfigManager.Instance;
            if (cfg?.configBuildMenuWidth != null) cfg.configBuildMenuWidth.Value = float.NaN;
            if (cfg?.configBuildMenuHeight != null) cfg.configBuildMenuHeight.Value = float.NaN;
            if (panel != null)
            {
                panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, vanillaSize.x);
                panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, vanillaSize.y);
            }
            Debug.Log($"{VaPieces.PluginName}: build menu size reset to vanilla {vanillaSize.x:0}x{vanillaSize.y:0} (Core layout Reset)");
        }

        private static void RestoreGeometry(RectTransform panel)
        {
            // 1.1.272: no own scale (Core's HUD dock scales the window); only the saved grid size

            var cfg = ConfigManager.Instance;
            if (cfg?.configBuildMenuWidth == null || cfg.configBuildMenuHeight == null) return;
            float w = cfg.configBuildMenuWidth.Value, h = cfg.configBuildMenuHeight.Value;
            if (float.IsNaN(w) || float.IsNaN(h)) return;
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Clamp(w, MinWidth, MaxWidth));
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,   Mathf.Clamp(h, MinHeight, MaxHeight));
        }

        private static void RestorePosition(RectTransform root)
        {
            var cfg = ConfigManager.Instance;
            if (cfg?.configBuildMenuPosX == null || cfg.configBuildMenuPosY == null) return;
            float x = cfg.configBuildMenuPosX.Value, y = cfg.configBuildMenuPosY.Value;
            if (float.IsNaN(x) || float.IsNaN(y)) return;

            // Clamp back on-screen. A saved position from a larger display (or a since-changed
            // resolution) would otherwise park the window entirely outside the view with no way to
            // drag it back.
            float margin = 40f;
            x = Mathf.Clamp(x, margin, Mathf.Max(margin, Screen.width - margin));
            y = Mathf.Clamp(y, margin, Mathf.Max(margin, Screen.height - margin));
            root.position = new Vector3(x, y, root.position.z);
        }
    }

    // Marks a BuildUi whose window controls are already attached.
    // 1.1.273: also the debounce for Core's SIZE resize - BepInEx rewrites the .cfg on every Value set, so the grid size is
    // saved once the drag has been still for half a second, not per frame.
    internal sealed class VABuildUiWindowMarker : MonoBehaviour
    {
        private static Vector2 s_pending; private static float s_due = -1f;
        internal static void QueueSize(Vector2 size) { s_pending = size; s_due = Time.unscaledTime + 0.5f; }
        internal static void CancelPending() => s_due = -1f;   // 1.1.274: a Reset must not be overwritten by a queued save
        private void Update()
        {
            if (s_due < 0f || Time.unscaledTime < s_due) return;
            s_due = -1f;
            VABuildUiWindow.SaveSize(s_pending);
            Debug.Log($"{VaPieces.PluginName}: build menu size saved {s_pending.x:0}x{s_pending.y:0} (Core grip)");
        }
    }

    // Bridge FAP's own favorites onto Valheim 1.0's Favorites TAB.
    //
    // FAP has had a favorites tab of its own since long before 1.0, backed by VAHammerTabOrganizer and a
    // prefabs.txt on disk. 1.0 shipped its own, backed by FavoritePieceList, and the two know nothing
    // about each other - so right-click -> Add to Favorites filed the piece in FAP's tab, which the new
    // build menu never renders, and the vanilla Favorites tab stayed empty.
    //
    // FavoritePieceList exposes AddPiece/RemovePiece/IsFavorite publicly and BuildUi subscribes to its
    // PieceAdded/PieceRemoved events, so writing through it refreshes the tab with no extra prodding.
    // The list itself is private on BuildUi, hence the one reflection hop.
    internal static class VABuildUiFavorites
    {
        private static System.Reflection.FieldInfo _field;
        private static bool _warned;

        private static FavoritePieceList List()
        {
            var hud = Hud.instance;
            var ui = hud != null ? hud.m_buildUi : null;
            if (ui == null) return null;
            if (_field == null) _field = AccessTools.Field(typeof(BuildUi), "m_favoritePieceList");
            var list = _field?.GetValue(ui) as FavoritePieceList;
            if (list == null && !_warned)
            {
                _warned = true;
                Debug.LogWarning($"{VaPieces.PluginName}: BuildUi.m_favoritePieceList did not resolve - " +
                                 "right-click favorites will not reach the vanilla Favorites tab.");
            }
            return list;
        }

        internal static bool IsFavorite(string prefabName)
        {
            var l = List();
            return l != null && !string.IsNullOrEmpty(prefabName) && l.IsFavorite(prefabName);
        }

        // Mirror FAP's state onto vanilla rather than toggling independently, so the two can never
        // disagree about whether a piece is favorited.
        internal static void Mirror(string prefabName, bool favorite)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            try
            {
                var l = List();
                if (l == null) return;
                bool already = l.IsFavorite(prefabName);
                if (favorite && !already) l.AddPiece(prefabName);
                else if (!favorite && already) l.RemovePiece(prefabName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: could not mirror favorite '{prefabName}' to the vanilla tab: {ex.Message}");
            }
        }
    }

    // Bottom-right resize handle. Which of the two resizes you get is decided per GESTURE, at
    // OnBeginDrag, so it can't change mid-drag:
    //   drag       -> GRID. Grows the window's RECT, so vanilla's layout flows MORE icons across and
    //                 down - the pre-1.0 behaviour, and the reason a resize existed in the first place.
    //   Shift+drag -> ZOOM. Scales the window; same icons, bigger.
    // 1.1.189 (Fire 10-07, asked for by a player): swapped; until 1.1.188 a plain drag zoomed and Shift+drag grew the grid.
    internal sealed class VABuildUiResizeGrip : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform Target;

        // 1.1.202 ([visual]'s ui_corner crop: VAGripSprite_1 resolved but nothing showed): the grip is the panel's FIRST child, so
        // everything vanilla lays out after it draws on top. Keep it the last sibling whenever the menu shows, and say what it is.
        private void OnEnable()
        {
            transform.SetAsLastSibling();
            var img = GetComponent<Image>();
            var rt = transform as RectTransform;
            VaPieces.Log?.LogDebug($"[FAP] build-menu grip: sprite {(img != null && img.sprite != null ? img.sprite.name : "none")}, colour {(img != null ? img.color.ToString() : "-")}, "
                + $"size {(rt != null ? rt.rect.size.ToString() : "-")}, sibling {transform.GetSiblingIndex() + 1}/{(transform.parent != null ? transform.parent.childCount : 0)} (last = on top)");
        }

        private bool _gridMode;
        private Vector2 _startPointer;
        private Vector2 _startSize;
        private float _startScale;
        private float _startDist;
        private Vector2 _center;

        private static bool ShiftHeld =>
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // Pointer deltas are SCREEN pixels; rect sizes are canvas units. Without dividing by the
        // canvas scale factor the window grows at the wrong rate on any non-1.0 UI scale - and this
        // menu reports a 2580x1080 rect on a 3440x1440 screen, so the factor is never 1 here.
        private float CanvasScale
        {
            get
            {
                var c = Target != null ? Target.GetComponentInParent<Canvas>() : null;
                return (c != null && c.scaleFactor > 0.01f) ? c.scaleFactor : 1f;
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            _gridMode = !ShiftHeld;   // 1.1.189 (Fire 10-07): swapped - plain drag = GRID, Shift+drag = ZOOM
            _startPointer = eventData.position;
            _startSize = Target.rect.size;
            _startScale = Target.localScale.x;
            _center = Target.position;
            _startDist = Vector2.Distance(eventData.position, _center);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Target == null) return;

            if (_gridMode)
            {
                var d = (eventData.position - _startPointer) / CanvasScale;
                // Bottom-right grip: dragging right widens, dragging DOWN (negative screen y) heightens.
                float w = Mathf.Clamp(_startSize.x + d.x, VABuildUiWindow.MinWidth,  VABuildUiWindow.MaxWidth);
                float h = Mathf.Clamp(_startSize.y - d.y, VABuildUiWindow.MinHeight, VABuildUiWindow.MaxHeight);
                Target.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
                Target.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,   h);
                return;
            }

            if (_startDist < 1f) return;
            float dist = Vector2.Distance(eventData.position, _center);
            float s = Mathf.Clamp(_startScale * (dist / _startDist), VABuildUiWindow.MinScale, VABuildUiWindow.MaxScale);
            Target.localScale = new Vector3(s, s, 1f);
        }

        // Persist once per gesture, not per frame - BepInEx rewrites the .cfg on every Value set.
        public void OnEndDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            if (_gridMode) VABuildUiWindow.SaveSize(Target.rect.size);
            else VABuildUiWindow.SaveScale(Target.localScale.x);
        }
    }

    internal sealed class VABuildUiMoveHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform Target;

        private Vector2 _grabOffset;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            _grabOffset = (Vector2)Target.position - eventData.position;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            var p = eventData.position + _grabOffset;
            Target.position = new Vector3(p.x, p.y, Target.position.z);
        }

        // Persist once per gesture, not per frame - BepInEx rewrites the .cfg on every Value set.
        public void OnEndDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            VABuildUiWindow.SavePosition(Target.position);
        }
    }

    [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
    public static class BuildUi_OpenBuildMenu_VAWindowControls_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(BuildUi __instance)
        {
            VerdantsAscent.Utilities.FapNativeTrace.Step("FAP window controls begin");
            VABuildUiWindow.Ensure(__instance);
            VABuildUiContextMenu.AttachEventClicks(__instance);   // 1.1.170
            VerdantsAscent.Utilities.FapNativeTrace.Step("FAP window controls: grip + clicks attached");
            // 1.1.207 (Fire 10-10: IH's tool still shows FAP's old tabs; where they come from wasn't provable from code): one
            // evidence line per build-menu open, at Debug.
            try
            {
                var t = Player.m_localPlayer?.m_buildPieces;
                string labels = t?.m_categoryLabels != null ? string.Join(", ", t.m_categoryLabels.Take(14)) : "(none)";
                string cats = t?.m_categories != null ? string.Join(",", t.m_categories.Take(14).Select(c => ((int)c).ToString())) : "(none)";
                VaPieces.Log?.LogDebug($"[FAP] build menu opened: table '{t?.name}' organized={HammerCategoryUI.IsOrganizable(t)} hammer={HammerCategoryUI.IsHammerTable(t)} {HammerCategoryUI.IhStateText()} categories=[{cats}] labels=[{labels}]");
            }
            catch (System.Exception ex) { VaPieces.Log?.LogDebug($"[FAP] build menu opened: (evidence line failed: {ex.Message})"); }
        }
    }

    // 1.1.172 (the lead 15:3x: an EventSystem right click on a piece button opened no menu): vanilla makes piece buttons in
    // UpdatePieceButtons (the list changes, a search is typed), AFTER OpenBuildMenu; those never got the opener. Attached here too.
    [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons")]
    public static class BuildUi_UpdatePieceButtons_VAEventClicks_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(BuildUi __instance) => VABuildUiContextMenu.AttachEventClicks(__instance);
    }

    // FAP's context menu for the build menu button under the cursor: the piece menu on a piece button, the
    // tab menu on a tag button. It opens on [BuildMenu] ContextMenuKey (default Shift + right click); a plain
    // right click is left to vanilla, which closes the menu.
    internal static class VABuildUiContextMenu
    {
        private static readonly List<RaycastResult> PointerHits = new List<RaycastResult>();

        // True when FAP's menu opened, so vanilla must not also read this click as its own.
        internal static bool TryOpenForButtonUnderPointer()
        {
            var key = ConfigManager.Instance?.configBuildMenuContextMenuKey;
            if (key == null || !key.IsPressed() || s_openedFrame == Time.frameCount) return false;

            var target = TopmostUiUnderPointer();
            if (target == null) return false;

            var pieceButton = target.GetComponentInParent<BuildUiPieceButton>();
            if (pieceButton != null)
            {
                if (pieceButton.Piece == null) return false;
                VAHammerRightClickPatches.ShowPieceMenu(pieceButton.Piece);
            }
            else
            {
                var tagButton = target.GetComponentInParent<BuildUiTagButton>();
                if (tagButton == null) return false;
                VAHammerRightClickPatches.ShowTagMenu(tagButton.m_tagId);
            }
            s_openedFrame = Time.frameCount;
            return Utilities.VARightClickMenu.IsOpen;
        }

        // 1.1.170 (Fire 10-02/10-06: tests use the player's input path): the same menu also opens from an EventSystem right
        // click on a piece / tab button (the binding's key must be the right mouse button, its Ctrl/Shift/Alt held, read through
        // FiresKeys like the binding itself). A real right click may reach both this and the key path; whichever runs first
        // this frame opens it.
        private static int s_openedFrame = -1;

        internal static void AttachEventClicks(BuildUi buildUi)
        {
            if (buildUi == null) return;
            // 1.1.203: FAP's piece/tab context menu is for the Hammer; with another tool's table (Infinity Hammer's blueprint
            // menu) the per-rebuild button sweep is skipped entirely.
            if (!HammerCategoryUI.IsOrganizable(Player.m_localPlayer?.m_buildPieces)) return;
            foreach (var b in buildUi.GetComponentsInChildren<BuildUiPieceButton>(true))
                if (b.GetComponent<RightClickOpener>() == null) b.gameObject.AddComponent<RightClickOpener>();
            foreach (var b in buildUi.GetComponentsInChildren<BuildUiTagButton>(true))
                if (b.GetComponent<RightClickOpener>() == null) b.gameObject.AddComponent<RightClickOpener>();
        }

        private static bool ModifiersHeld(FiresCore.Input.KeyBinding key)
        {
            bool ctrl = FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftControl) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightControl);
            bool shift = FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftShift) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightShift);
            bool alt = FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftAlt) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightAlt);
            return ctrl == key.Ctrl.Value && shift == key.Shift.Value && alt == key.Alt.Value;
        }

        private sealed class RightClickOpener : MonoBehaviour, IPointerClickHandler
        {
            public void OnPointerClick(PointerEventData eventData)
            {
                var key = ConfigManager.Instance?.configBuildMenuContextMenuKey;
                // 1.1.172: one Debug line per click on a piece / tab button: the event's button, the modifiers as FiresKeys reads them, the target.
                VaPieces.Log?.LogDebug($"[VABuildUi] event click {eventData.button} on {name}: ctrl {FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftControl) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightControl)}, "
                    + $"shift {FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftShift) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightShift)}, alt {FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftAlt) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightAlt)}; "
                    + $"binding {(key != null ? $"{key.Key.Value} ctrl {key.Ctrl.Value} shift {key.Shift.Value} alt {key.Alt.Value}" : "none")}, already opened this frame {s_openedFrame == Time.frameCount}");
                // 1.1.263 (Arbby via Fire): the binding may be the MIDDLE button. A middle click on a PIECE button is handled by the
                // OnMiddleClick prefix below (it also has to stop vanilla's favourite toggle); here only tag buttons take it.
                bool middleBinding = key != null && key.Key.Value == KeyCode.Mouse2;
                var wanted = middleBinding ? PointerEventData.InputButton.Middle : PointerEventData.InputButton.Right;
                if (eventData.button != wanted || s_openedFrame == Time.frameCount) return;
                if (middleBinding && GetComponent<BuildUiPieceButton>() != null) return;
                // 1.1.173 (the lead 15:5x: the click line fired, still no menu): every way out is logged, and the outcome.
                string skip = key == null ? "no binding" : key.Key.Value != KeyCode.Mouse1 && key.Key.Value != KeyCode.Mouse2 ? $"binding key {key.Key.Value} is not a mouse button (Mouse1 / Mouse2)"
                    : !ModifiersHeld(key) ? "modifiers don't match the binding" : FiresCore.Input.FiresInputBlock.IsCapturing ? "Core's text-capture gate is on (a Fires text field or modal holds input)" : null;
                if (skip != null) { VaPieces.Log?.LogDebug($"[VABuildUi] event click on {name}: no menu, {skip}"); return; }
                Utilities.VARightClickMenu.NextPosition = eventData.position;   // at the click, not wherever the real cursor is
                var piece = GetComponent<BuildUiPieceButton>();
                string what;
                if (piece != null) { what = piece.Piece != null ? piece.Piece.name : "a piece button with no piece"; if (piece.Piece != null) VAHammerRightClickPatches.ShowPieceMenu(piece.Piece); }
                else { var tag = GetComponent<BuildUiTagButton>(); what = tag != null ? "tag " + tag.m_tagId : "neither a piece nor a tag button"; if (tag != null) VAHammerRightClickPatches.ShowTagMenu(tag.m_tagId); }
                Utilities.VARightClickMenu.NextPosition = null;
                s_openedFrame = Time.frameCount;
                VaPieces.Log?.LogDebug($"[VABuildUi] opening menu for {what}: {(Utilities.VARightClickMenu.IsOpen ? "ok" : "NOT open after the call")}");
            }
        }

        // 1.1.263 (Arbby via Fire: "if we rebind the context menue for fap to mmb it should BLOCK the vanilla mmb favorites quick
        // action and just pull up our context menue, keeping the mmb favorites untouched otherwise"). Vanilla's middle click on a piece
        // button is BuildUiPieceButton.OnMiddleClick -> BuildUi.PressedFavoriteButton. Only when [BuildMenu] ContextMenuKey is Mouse2
        // (with its modifiers held) does FAP take the click: its piece menu opens (favourite is one of its entries) and vanilla's
        // toggle is skipped. Any other binding: vanilla runs exactly as before.
        internal static bool MiddleClickIsOurs(out FiresCore.Input.KeyBinding key)
        {
            key = ConfigManager.Instance?.configBuildMenuContextMenuKey;
            return key != null && key.Key.Value == KeyCode.Mouse2 && ModifiersHeld(key) && !FiresCore.Input.FiresInputBlock.IsCapturing
                && HammerCategoryUI.IsOrganizable(Player.m_localPlayer?.m_buildPieces);
        }

        internal static void OpenFromMiddleClick(BuildUiPieceButton button)
        {
            if (s_openedFrame == Time.frameCount || button == null || button.Piece == null) return;
            Utilities.VARightClickMenu.NextPosition = Input.mousePosition;
            VAHammerRightClickPatches.ShowPieceMenu(button.Piece);
            Utilities.VARightClickMenu.NextPosition = null;
            s_openedFrame = Time.frameCount;
            VaPieces.Log?.LogDebug($"[VABuildUi] MMB -> FAP context menu (vanilla favourite skipped) on '{button.Piece.name}': {(Utilities.VARightClickMenu.IsOpen ? "open" : "NOT open after the call")}");
        }

        private static GameObject TopmostUiUnderPointer()
        {
            var events = EventSystem.current;
            if (events == null) return null;
            PointerHits.Clear();
            events.RaycastAll(new PointerEventData(events) { position = Input.mousePosition }, PointerHits);
            return PointerHits.Count > 0 ? PointerHits[0].gameObject : null;
        }
    }

    // 1.1.263: vanilla's middle click on a piece button toggles a favourite. With FAP's context menu bound to the middle button, the
    // click is FAP's instead (the menu opens, the favourite is untouched); with any other binding vanilla's toggle runs unchanged.
    [HarmonyPatch(typeof(BuildUiPieceButton), "OnMiddleClick")]
    public static class BuildUiPieceButton_OnMiddleClick_VAContextMenu_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(BuildUiPieceButton __instance)
        {
            if (!VABuildUiContextMenu.MiddleClickIsOurs(out _)) return true;   // not bound to MMB: vanilla's favourite toggle
            VABuildUiContextMenu.OpenFromMiddleClick(__instance);
            return false;                                                      // one click, one action: no favourite toggle
        }
    }

    // Keeps the build menu open underneath FAP's context menu while the popup is up.
    [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.Close))]
    public static class BuildUi_Close_VAMenuGate_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix() => !Utilities.VARightClickMenu.IsOpen;
    }

    // 1.1.174 (the lead 16:2x: Core's text-capture gate stayed ON for a whole round after the build menu's search was typed in):
    // Core's driver holds the gate while the EventSystem's selection is a focused text field. When the build menu closes or a
    // piece is picked, a text field of the build menu that is still selected is let go (selection cleared), so the gate drops.
    // Typing in the search field itself is untouched: nothing happens while the menu stays open.
    [HarmonyPatch]
    public static class BuildUi_ReleaseTextCapture_Patch
    {
        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.Close)), HarmonyPostfix]
        public static void AfterClose(BuildUi __instance) { if (!Utilities.VARightClickMenu.IsOpen) Release(__instance, "build menu close"); }

        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OnSelectPiece)), HarmonyPostfix]
        public static void AfterSelectPiece(BuildUi __instance) => Release(__instance, "piece picked");

        private static void Release(BuildUi ui, string reason)
        {
            var events = EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null || ui == null || !selected.transform.IsChildOf(ui.transform)) return;
            if (selected.GetComponent<TMPro.TMP_InputField>() == null && selected.GetComponent<UnityEngine.UI.InputField>() == null) return;
            selected.GetComponent<TMPro.TMP_InputField>()?.DeactivateInputField();
            events.SetSelectedGameObject(null);
            VaPieces.Log?.LogDebug($"[VABuildUi] text capture released by {reason} ({selected.name} deselected)");
        }
    }

    // NavigationUpdate is vanilla's build menu input: it closes the menu on a right click or Escape and
    // focuses the search field on a HELD F. Skipped while a Fires text field has focus, or typing an f into
    // FAP's rename and new-tab prompts jumped to the search field. Skipped for the frame FAP's context menu
    // opens, or the same right click would also close the build menu under it.
    [HarmonyPatch(typeof(BuildUi), "NavigationUpdate")]
    public static class BuildUi_NavigationUpdate_VAInput_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
            => !FiresCore.Input.FiresInputBlock.IsCapturing && !VABuildUiContextMenu.TryOpenForButtonUnderPointer();
    }

    // Replaces BuildUi.OnFavoritePieceAdded/Removed, which read
    // pieceButton.Piece.gameObject.name with no null check. One button holding a
    // destroyed Piece throws there — and the handlers run inside Player.Load, via
    // BuildUi.LoadFromBinary → FavoritePieceList.Deserialize. LoadFromBinary only catches
    // EndOfStream and Argument exceptions, so the NRE aborts Player.Load and UpdateRespawn
    // retries it every FixedUpdate forever: any character with a single saved favorite
    // could never respawn after dying.
    //
    // Replaced rather than wrapped: a postfix can't rescue a throw, and a finalizer would
    // drop the refresh for every button after the dead one. Same loop, dead entries skipped.
    internal static class VABuildUiFavoriteRefresh
    {
        private static AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> _buttons;
        private static bool _resolved;
        private static bool _warnedResolve;
        private static bool _warnedDead;

        private static List<BuildUiPieceButton> Buttons(BuildUi ui)
        {
            if (ui == null) return null;
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    _buttons = AccessTools.FieldRefAccess<BuildUi, List<BuildUiPieceButton>>("m_pieceButtons");
                }
                catch (Exception ex)
                {
                    _buttons = null;
                    if (!_warnedResolve)
                    {
                        _warnedResolve = true;
                        Debug.LogWarning($"{VaPieces.PluginName}: BuildUi.m_pieceButtons did not resolve ({ex.GetType().Name}) - " +
                                         "favorite-star refreshes fall back to vanilla, which can crash the respawn path.");
                    }
                }
            }
            return _buttons != null ? _buttons(ui) : null;
        }

        // true when we handled it and vanilla should be skipped.
        internal static bool Refresh(BuildUi ui, string pieceName)
        {
            var buttons = Buttons(ui);
            if (buttons == null) return false;

            int dead = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                var button = buttons[i];
                if (button == null) { dead++; continue; }
                var piece = button.Piece;
                if (piece == null) { dead++; continue; }      // Unity null: destroyed OR never assigned
                if (piece.gameObject.name != pieceName) continue;
                button.RefreshFavorite();
            }

            if (dead > 0 && !_warnedDead)
            {
                _warnedDead = true;
                Debug.LogWarning($"{VaPieces.PluginName}: {dead} of {buttons.Count} build-menu piece button(s) point at a " +
                                 "destroyed Piece. Vanilla throws here and leaves the respawn loop unrecoverable; they are " +
                                 "being skipped. Something is destroying a prefab after it entered the piece table.");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BuildUi), "OnFavoritePieceAdded")]
    public static class BuildUi_OnFavoritePieceAdded_VANullGuard_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(BuildUi __instance, string pieceName)
            => !VABuildUiFavoriteRefresh.Refresh(__instance, pieceName);
    }

    [HarmonyPatch(typeof(BuildUi), "OnFavoritePieceRemoved")]
    public static class BuildUi_OnFavoritePieceRemoved_VANullGuard_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(BuildUi __instance, string pieceName)
            => !VABuildUiFavoriteRefresh.Refresh(__instance, pieceName);
    }
}
