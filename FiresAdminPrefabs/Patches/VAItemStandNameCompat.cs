using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // 1.1.327 (a player via Fire: "World Edit features, such as `object visual=item`, have stopped working"): World Edit Commands'
    // Actions.SetVisual(ItemStand, Item) writes ZDO.Set("item", <item NAME>) - a string - and calls UpdateVisual. Since Valheim 1.0
    // ItemStand.UpdateVisual reads ZDO.GetInt("item"): the prefab-name HASH. The int is 0, so the stand draws nothing.
    // When the int is 0 and the string names an item in ObjectDB:
    //   - the stand's owner stores the hash under the int key, so vanilla (and every client, FAP or not) draws it from then on;
    //   - any other FAP client draws it at once through vanilla's own SetVisualItem (no ZDO write; the owner's write follows).
    // The string is left as World Edit wrote it. One Debug line per stand that is converted.
    [HarmonyPatch]
    public static class VAItemStandNameCompat
    {
        private static readonly System.Reflection.MethodInfo s_setVisual =
            AccessTools.Method(typeof(ItemStand), "SetVisualItem", new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
        private static readonly System.Reflection.MethodInfo s_orientation = AccessTools.Method(typeof(ItemStand), "GetOrientation");
        private static readonly HashSet<ZDOID> s_said = new HashSet<ZDOID>();

        [HarmonyPatch(typeof(ItemStand), "UpdateVisual"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool ItemStand_UpdateVisual_Prefix(ItemStand __instance)
        {
            var view = __instance != null ? __instance.m_nview : null;
            if (view == null || !view.IsValid()) return true;
            var zdo = view.GetZDO();
            if (zdo.GetInt(ZDOVars.s_item) != 0) return true;
            string name = zdo.GetString(ZDOVars.s_item);
            if (string.IsNullOrEmpty(name) || ObjectDB.instance == null) return true;
            int hash = name.GetStableHashCode();
            if (ObjectDB.instance.GetItemPrefab(hash) == null) return true;
            if (s_said.Add(zdo.m_uid))
                VaPieces.Log?.LogDebug($"[FAP] item stand {zdo.m_uid}: item '{name}' was written as a name (World Edit visual=); "
                    + (view.IsOwner() ? "owner stores its hash so every client draws it" : "drawn here from the name until the owner stores the hash"));
            if (view.IsOwner()) { zdo.Set(ZDOVars.s_item, hash); return true; }
            if (s_setVisual == null || s_orientation == null) return true;
            int orientation = (int)s_orientation.Invoke(__instance, null);
            s_setVisual.Invoke(__instance, new object[] { hash, zdo.GetInt(ZDOVars.s_variant), zdo.GetInt(ZDOVars.s_quality, 1), orientation });
            return false;
        }
    }
}
