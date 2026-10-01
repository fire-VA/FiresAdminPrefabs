using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FiresAdminPrefabs.Patches
{
    // PERF FIX. Hud.UpdatePieceBuildStatusAll re-scans EVERY build piece to recompute the can-build
    // tint. With this modlist the hammer table is thousands of pieces, and it gets re-triggered every
    // frame the build menu is open — that's the 48-136ms stall. Its result depends only on known
    // recipes + current inventory, so when neither has changed since the last call the result is
    // identical and the whole scan is wasted. Skip it then; build something or learn a recipe and the
    // signature changes, so it re-runs. The per-frame visible-icon status (UpdatePieceBuildStatus,
    // non-All) is untouched, so the open category always shows correct tint.
    [HarmonyPatch(typeof(Hud), "UpdatePieceBuildStatusAll")]
    public static class VAPieceBuildStatusGuard
    {
        private static FieldInfo s_knownRecipes;
        private static int s_lastSig = int.MinValue;

        public static bool Prefix(Player player)
        {
            if (player == null) return true;
            int sig = Signature(player);
            if (sig == s_lastSig) return false;
            s_lastSig = sig;
            return true;
        }

        private static int Signature(Player player)
        {
            unchecked
            {
                int sig = 17;
                if (s_knownRecipes == null)
                {
                    s_knownRecipes = AccessTools.Field(typeof(Player), "m_knownRecipes");
                }
                if (s_knownRecipes != null && s_knownRecipes.GetValue(player) is HashSet<string> known)
                {
                    sig = sig * 31 + known.Count;
                }

                Inventory inv = player.GetInventory();
                if (inv != null)
                {
                    List<ItemDrop.ItemData> items = inv.GetAllItems();
                    sig = sig * 31 + items.Count;
                    for (int i = 0; i < items.Count; i++)
                    {
                        ItemDrop.ItemData it = items[i];
                        if (it == null) continue;
                        if (it.m_shared != null && it.m_shared.m_name != null)
                        {
                            sig = sig * 31 + it.m_shared.m_name.GetHashCode();
                        }
                        sig = sig * 31 + it.m_stack;
                    }
                }
                return sig;
            }
        }
    }
}
