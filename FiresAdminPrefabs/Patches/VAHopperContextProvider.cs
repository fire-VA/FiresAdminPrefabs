using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.UI.ContextMenu;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    /// <summary>
    /// 1.1.276 (Fire 10-19: "make hopper" on any container from FAP's right-click menu). A hopper is Core's: any container whose ZDO
    /// carries Core's FiresCore.Storage.Containers.HopperChest.FlagKey ("core_hopper") = true gets Core's HopperChest (pull radius, mode,
    /// sweep) - the Hammer's "Hopper Chest" piece is only a chest born with that flag. This row sets / clears the flag on the targeted
    /// container and attaches / removes Core's HopperChest at once. Admin only; greyed out while Core's [Storage] Hopper Chests is off.
    /// Reused: Core HopperChest (FlagKey, IsHopper, TryAttach), Core FiresContextMenu.
    /// </summary>
    internal sealed class VAHopperContextProvider : IContextMenuProvider
    {
        private static Type s_hopper; private static MethodInfo s_tryAttach; private static PropertyInfo s_on; private static bool s_looked;
        private static void Look()
        {
            if (s_looked) return; s_looked = true;
            s_hopper = Type.GetType("FiresCore.Storage.Containers.HopperChest, FiresUnifiedCore");
            s_tryAttach = s_hopper?.GetMethod("TryAttach", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(Container) }, null);
            s_on = s_hopper?.GetProperty("On", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Debug.Log($"{VaPieces.PluginName}: [Hopper] Core HopperChest {(s_hopper != null ? "found" : "NOT found")} (TryAttach {(s_tryAttach != null)}, On {(s_on != null)})");
        }
        private static string FlagKey => "core_hopper";   // = Core HopperChest.FlagKey (public const)

        private static Container ResolveContainer(ContextTarget t)
        {
            var c = t?.Find<Container>();
            if (c == null) return null;
            var nv = c.GetComponent<ZNetView>();
            return nv != null && nv.IsValid() ? c : null;
        }
        private static bool IsAdmin() { try { return VerdantsAscent.Managers.VAFapServerPresence.LocalMayUseAdminFeatures(); } catch { return false; } }

        public string TitleFor(ContextTarget target) => null;

        public IEnumerable<ContextMenuItem> GetItems(ContextTarget target)
        {
            var c = ResolveContainer(target);
            if (c == null || !VerdantsAscent.Managers.VAFapServerPresence.MayEditPiece(c.gameObject)) return null;   // 1.1.306 (Fire 10-21): own containers only on a server without FAP; admins any
            Look();
            if (s_hopper == null) return null;
            bool on = false; try { on = s_on != null && (bool)s_on.GetValue(null, null); } catch { }
            bool isHopper = c.GetComponent<ZNetView>().GetZDO().GetBool(FlagKey, false);
            var items = new List<ContextMenuItem>();
            if (!on) items.Add(ContextMenuItem.Row(isHopper ? "Hopper (Core [Storage] Hopper Chests is off)" : "Make hopper (Core [Storage] Hopper Chests is off)", null, enabled: false));
            else if (isHopper) items.Add(ContextMenuItem.Row("Remove hopper", () => SetHopper(c, false)));
            else items.Add(ContextMenuItem.Row("Make hopper", () => SetHopper(c, true)));
            return items;
        }

        // 1.1.336: FapPresets.Hopper (the Studio E window) sets it through this same row action
        internal static void SetHopperFor(Container c, bool hopper) { Look(); SetHopper(c, hopper); }
        internal static bool IsHopper(Container c) { var nv = c != null ? c.GetComponent<ZNetView>() : null; return nv != null && nv.IsValid() && nv.GetZDO().GetBool(FlagKey, false); }
        private static void SetHopper(Container c, bool hopper)
        {
            try
            {
                if (c == null) return;
                var nv = c.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) return;
                nv.ClaimOwnership();
                nv.GetZDO().Set(FlagKey, hopper);
                if (hopper) s_tryAttach?.Invoke(null, new object[] { c });
                else if (s_hopper != null) { var comp = c.GetComponent(s_hopper); if (comp != null) UnityEngine.Object.Destroy(comp); }
                bool attached = s_hopper != null && c.GetComponent(s_hopper) != null;
                Debug.Log($"{VaPieces.PluginName}: [Hopper] '{c.name}' {(hopper ? "made a hopper" : "no longer a hopper")} (Core HopperChest {(attached ? "attached" : "not attached")})");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, hopper ? "Hopper: this container now pulls nearby drops" : "Hopper removed");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [Hopper] set failed: " + (ex.InnerException ?? ex).Message); }
        }
    }
}
