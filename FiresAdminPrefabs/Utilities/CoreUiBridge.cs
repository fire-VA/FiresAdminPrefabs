using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// The UI audit (Tools\UI_HANDLING_AUDIT.md, 10-15): Core is the ONE implementation of panel drag / resize / dock / slot look.
    /// Core 0.2.572 FiresShell.RegisterLivePanel / RegisterHudElement, DockService.Register / Unregister and FiresSkin.ApplySlotLook,
    /// called by reflection so an older Core leaves the panel where it is (no local fallback by design). Every miss is logged once.
    /// </summary>
    internal static class CoreUiBridge
    {
        private const string Asm = ", FiresUnifiedCore";
        private static bool s_looked;
        private static Type s_shell, s_dock, s_dockOpts, s_hudOpts, s_edge, s_skin;
        private static MethodInfo s_live, s_hud, s_dockReg, s_dockUnreg, s_slotLook;

        private static void Look()
        {
            if (s_looked) return;
            s_looked = true;
            try
            {
                s_shell = Type.GetType("FiresCore.UI.Shell.FiresShell" + Asm);
                s_dock = Type.GetType("FiresCore.UI.Dock.DockService" + Asm);
                s_dockOpts = Type.GetType("FiresCore.UI.Dock.DockElementOptions" + Asm);
                s_hudOpts = Type.GetType("FiresCore.UI.Shell.HudElementOptions" + Asm);
                s_edge = Type.GetType("FiresCore.UI.Dock.DockEdge" + Asm);
                s_skin = Type.GetType("FiresCore.UI.Shell.FiresSkin" + Asm);
                s_live = s_shell?.GetMethod("RegisterLivePanel", new[] { typeof(string), typeof(string), typeof(RectTransform), typeof(RectTransform), typeof(string), typeof(string) });
                if (s_hudOpts != null) s_hud = s_shell?.GetMethod("RegisterHudElement", new[] { typeof(string), typeof(RectTransform), s_hudOpts });
                if (s_dockOpts != null) s_dockReg = s_dock?.GetMethod("Register", new[] { s_dockOpts });
                s_dockUnreg = s_dock?.GetMethod("Unregister", new[] { typeof(string) });
                if (s_skin != null)
                    foreach (var m in s_skin.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "ApplySlotLook" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Image)) { s_slotLook = m; break; }
            }
            catch (Exception ex) { Debug.LogWarning("[FiresAdminPrefabs] [CoreUi] lookup failed: " + ex.Message); }
            Debug.Log($"[FiresAdminPrefabs] [CoreUi] Core UI API: live panels {(s_live != null)}, HUD elements {(s_hud != null)}, dock {(s_dockReg != null)}, slot look {(s_slotLook != null)}{(s_live == null || s_hud == null || s_dockReg == null || s_slotLook == null ? " (Core older than 0.2.572: those panels stay where they are)" : "")}");
        }

        /// <summary>A drag starts only on a raycastable Graphic: a header that is a bare layout node gets a transparent Image.</summary>
        private static void EnsureRaycast(RectTransform handle)
        {
            if (handle == null || handle.GetComponent<Graphic>() != null) return;
            var img = handle.gameObject.AddComponent<Image>(); img.color = new Color(0f, 0f, 0f, 0f); img.raycastTarget = true;
        }

        private static void Set(object o, string field, object value)
        {
            var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(o, value); return; }
            o.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance)?.SetValue(o, value, null);
        }
        private static object Edge(string edge)
        {
            if (s_edge == null || string.IsNullOrEmpty(edge)) return null;
            try { return Enum.Parse(s_edge, edge, true); } catch { return null; }
        }

        /// <summary>FiresShell.RegisterLivePanel: header drag moves, bottom-right handle resizes, docks on release, saved per window.</summary>
        internal static bool RegisterLivePanel(string screenKey, string id, RectTransform panel, RectTransform header, string label, string owner)
        {
            Look();
            if (s_live == null || panel == null || header == null) return false;
            EnsureRaycast(header);
            try { s_live.Invoke(null, new object[] { screenKey, id, panel, header, label, owner }); VerdantsAscent.VaPieces.Log?.LogDebug($"[FiresAdminPrefabs] [CoreUi] live panel {screenKey}/{id} '{label}' (header '{header.name}')"); return true; }
            catch (Exception ex) { Debug.LogWarning($"[FiresAdminPrefabs] [CoreUi] live panel {id}: " + (ex.InnerException ?? ex).Message); return false; }
        }

        /// <summary>DockService.Register on a layer ("hud" | "inventory" | a screen layer), optionally docked to another element.</summary>
        internal static bool RegisterDock(string id, string owner, string layer, RectTransform rect, RectTransform dragHandle, string label,
            string defaultDockTo = null, string defaultEdge = null, Func<bool> visible = null, bool liveEdit = true, bool showGrip = true,
            Action<Vector2> sizeChanged = null, Vector2 minSize = default, Action sizeReset = null)
        {
            Look();
            if (s_dockReg == null || rect == null) return false;
            try
            {
                var o = Activator.CreateInstance(s_dockOpts);
                Set(o, "Id", id); Set(o, "Owner", owner); Set(o, "Layer", layer); Set(o, "Rect", rect);
                if (dragHandle != null) { EnsureRaycast(dragHandle); Set(o, "DragHandle", dragHandle); }
                Set(o, "Label", label); Set(o, "LiveEdit", liveEdit); Set(o, "ShowGrip", showGrip);
                if (!string.IsNullOrEmpty(defaultDockTo)) Set(o, "DefaultDockTo", defaultDockTo);
                var e = Edge(defaultEdge); if (e != null) Set(o, "DefaultEdge", e);
                if (sizeChanged != null) { Set(o, "SizeChanged", sizeChanged); Set(o, "MinSize", minSize); }   // Core 0.2.574+: size resize
                if (sizeReset != null) Set(o, "SizeReset", sizeReset);   // Core 0.2.579+
                if (visible != null) Set(o, "Visible", visible);
                s_dockReg.Invoke(null, new[] { o });
                VerdantsAscent.VaPieces.Log?.LogDebug($"[FiresAdminPrefabs] [CoreUi] dock element {layer}/{id} '{label}'{(string.IsNullOrEmpty(defaultDockTo) ? "" : $" docked to {defaultDockTo} ({defaultEdge})")}");
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresAdminPrefabs] [CoreUi] dock {id}: " + (ex.InnerException ?? ex).Message); return false; }
        }

        /// <summary>FiresShell.RegisterHudElement: a HUD element in the dock (layout mode moves / scales / docks it).</summary>
        internal static bool RegisterHud(string id, RectTransform rect, string label, string owner, bool resizable = true,
            string defaultDockTo = null, string defaultEdge = null, Func<bool> visible = null, bool alsoInInventoryLayout = false,
            Action<Vector2> sizeChanged = null, Vector2 minSize = default, Action sizeReset = null)
        {
            Look();
            if (s_hud == null || rect == null) return false;
            try
            {
                var o = Activator.CreateInstance(s_hudOpts);
                Set(o, "Resizable", resizable); Set(o, "Label", label); Set(o, "Owner", owner);
                if (!string.IsNullOrEmpty(defaultDockTo)) Set(o, "DefaultDockTo", defaultDockTo);
                var e = Edge(defaultEdge); if (e != null) Set(o, "DefaultEdge", e);
                if (visible != null) Set(o, "Visible", visible);
                if (alsoInInventoryLayout) Set(o, "AlsoInInventoryLayout", true);
                // Core 0.2.574: SizeChanged + MinSize = the bottom-right handle resizes sizeDelta (top-left fixed) instead of scaling
                if (sizeChanged != null) { Set(o, "SizeChanged", sizeChanged); Set(o, "MinSize", minSize); }
                if (sizeReset != null) Set(o, "SizeReset", sizeReset);   // Core 0.2.579: the layout Reset resets the size too
                s_hud.Invoke(null, new[] { id, rect, o });
                VerdantsAscent.VaPieces.Log?.LogDebug($"[FiresAdminPrefabs] [CoreUi] HUD element {id} '{label}'");
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresAdminPrefabs] [CoreUi] HUD {id}: " + (ex.InnerException ?? ex).Message); return false; }
        }

        internal static void Unregister(string id)
        {
            Look();
            if (s_dockUnreg == null || string.IsNullOrEmpty(id)) return;
            try { s_dockUnreg.Invoke(null, new object[] { id }); } catch { }
        }

        /// <summary>FiresSkin.ApplySlotLook: the approved pouch-cell look. False = Core too old, or the vanilla sprite isn't ready yet.</summary>
        internal static bool ApplySlotLook(Image face)
        {
            Look();
            if (s_slotLook == null || face == null) return false;
            try
            {
                var ps = s_slotLook.GetParameters();
                var args = new object[ps.Length]; args[0] = face;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                return s_slotLook.Invoke(null, args) is bool b && b;
            }
            catch { return false; }
        }
        internal static bool HasSlotLook { get { Look(); return s_slotLook != null; } }
        internal static bool HasLivePanels { get { Look(); return s_live != null; } }
        internal static bool HasDock { get { Look(); return s_dockReg != null; } }
        internal static bool HasHud { get { Look(); return s_hud != null; } }
    }
}
