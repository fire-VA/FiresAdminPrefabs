using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Uniform window-scale grip for the FAP config windows. Dragging ANY of the
    // four corner grips scales the WHOLE panel about its centre via localScale —
    // so the box, the fonts AND the section spacing all grow together (the old
    // grip only grew sizeDelta, which left the font tiny). The scale is driven by
    // the pointer's distance from the panel centre, so one handler covers all
    // four corners with no per-corner sign juggling.
    //
    // The chosen scale persists in ConfigManager.configConfigWindowScale: it is
    // re-applied when the window is rebuilt (AddScaleGrips) and written back once
    // per completed drag (OnEndDrag), so the window reopens at the size you left.
    internal sealed class VAWindowScaleGrip : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private const float MinScaleConst = 0.7f;
        private const float MaxScaleConst = 2.5f;

        public RectTransform Panel;
        public float MinScale = MinScaleConst;
        public float MaxScale = MaxScaleConst;

        // Where this grip's scale is stored. Null = the shared config-window entry, which is what the
        // monster/container/clutter windows want (one scale for all three). The build menu passes its own
        // so resizing it doesn't drag the config windows along with it.
        public System.Action<float> Save;

        private float _startScale;
        private float _startDist;
        private Vector2 _center;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Panel == null) return;
            // Overlay canvas → RectTransform.position is already screen pixels;
            // pivot is centre, so this is the panel centre and stays fixed while
            // we scale about it.
            _center = Panel.position;
            _startDist = Vector2.Distance(eventData.position, _center);
            _startScale = Panel.localScale.x;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Panel == null || _startDist < 1f) return;
            float dist = Vector2.Distance(eventData.position, _center);
            float scale = Mathf.Clamp(_startScale * (dist / _startDist), MinScale, MaxScale);
            Panel.localScale = new Vector3(scale, scale, 1f);
        }

        // Persist once per completed gesture (not per-frame) — BepInEx auto-writes
        // the .cfg on every Value set, so an OnDrag write would spam the disk.
        public void OnEndDrag(PointerEventData eventData)
        {
            if (Panel == null) return;
            if (Save != null) Save(Mathf.Clamp(Panel.localScale.x, MinScale, MaxScale));
            else SaveScale(Panel.localScale.x);
        }

        // Persisted scale store. ConfigManager.Instance is a lazy singleton (never
        // null); the entry field is null only before Initialize runs, so guard it.
        public static float LoadScale()
        {
            var entry = ConfigManager.Instance.configConfigWindowScale;
            return entry == null ? 1f : Mathf.Clamp(entry.Value, MinScaleConst, MaxScaleConst);
        }

        public static void SaveScale(float value)
        {
            var entry = ConfigManager.Instance.configConfigWindowScale;
            if (entry != null) entry.Value = Mathf.Clamp(value, MinScaleConst, MaxScaleConst);
        }

        // Creates four corner grips on the panel, each wired to a shared-behaviour
        // VAWindowScaleGrip, then applies the persisted scale so the window opens
        // at the size it was last left. The localScale write MUST stay last — the
        // config windows never touch _panelRoot.localScale after this call.
        public static void AddScaleGrips(RectTransform panel, Color color, float size = 18f,
            float minScale = MinScaleConst, float maxScale = MaxScaleConst,
            System.Func<float> load = null, System.Action<float> save = null)
        {
            if (panel == null) return;
            var corners = new[]
            {
                new Vector2(0f, 0f), // bottom-left
                new Vector2(1f, 0f), // bottom-right
                new Vector2(0f, 1f), // top-left
                new Vector2(1f, 1f), // top-right
            };
            foreach (var corner in corners)
            {
                var grip = new GameObject("ScaleGrip");
                grip.transform.SetParent(panel, false);
                var img = grip.AddComponent<Image>();
                img.sprite = VAGripSprite.Get(corner);   // 1.1.201: a drawn grip per corner, not a solid square
                img.color = color;
                img.raycastTarget = true;
                var rt = img.rectTransform;
                rt.anchorMin = corner;
                rt.anchorMax = corner;
                rt.pivot = corner;
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(size, size);
                var h = grip.AddComponent<VAWindowScaleGrip>();
                h.Panel = panel;
                h.MinScale = minScale;
                h.MaxScale = maxScale;
                h.Save = save;
            }

            panel.localScale = Vector3.one * Mathf.Clamp(load != null ? load() : LoadScale(), minScale, maxScale);
        }
    }
}
