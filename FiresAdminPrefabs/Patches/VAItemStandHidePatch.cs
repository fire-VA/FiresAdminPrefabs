using System;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // Hides the visual mesh of an ItemStand when an item is attached so the
    // item appears to float on its own. Same approach as ItemStandsAllItems.
    //
    // Per-stand override key "itemstand_hide" (matches ISAI's stable-hash):
    //   -1 = force visible, 0 = inherit global config, 1 = force hidden.
    //
    // When ISAI is installed we defer to it entirely — ISAI's SetVisualItem
    // postfix runs after vanilla and reparents item renderers to a non-stand
    // layer, which would defeat our own hide pass.
    public static class VAItemStandHidePatch
    {
        private const string ISAI_PLUGIN_GUID_FRAGMENT = "item_stand_all_items";

        private static readonly int K_ItemstandHide = "itemstand_hide".GetStableHashCode();

        private static bool? _isaiInstalledCache;

        [HarmonyPatch(typeof(ItemStand), "UpdateVisual")]
        public static class ItemStand_UpdateVisual_VAHide_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ItemStand __instance)
            {
                if (__instance == null) return;
                ApplyHide(__instance);
            }
        }

        public static void ApplyHide(ItemStand stand)
        {
            if (IsIsaiInstalled) return;

            var nview = stand.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;

            int perStand = zdo.GetInt(K_ItemstandHide, 0);
            bool wantHide;
            if (perStand > 0)      wantHide = true;
            else if (perStand < 0) wantHide = false;
            else
            {
                var cfg = ConfigManager.Instance;
                wantHide = cfg?.configItemStandHideStandVisual?.Value ?? false;
            }

            // Same test as vanilla ItemStand.HaveAttachment.
            bool hasAttachedItem = zdo.GetInt(ZDOVars.s_item) != 0;
            bool renderersEnabled = !(wantHide && hasAttachedItem);

            // Filter by stand-layer so the attached item's renderers (on a
            // different layer) are untouched.
            int standLayer = stand.gameObject.layer;
            var renderers = stand.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (r.gameObject.layer != standLayer) continue;
                if (r.enabled != renderersEnabled) r.enabled = renderersEnabled;
            }
        }

        public static int ReapplyAll()
        {
            var stands = Resources.FindObjectsOfTypeAll<ItemStand>();
            int touched = 0;
            for (int i = 0; i < stands.Length; i++)
            {
                var s = stands[i];
                if (s == null) continue;
                if (s.gameObject.scene.name == null) continue;
                ApplyHide(s);
                touched++;
            }
            return touched;
        }

        private static bool IsIsaiInstalled
        {
            get
            {
                if (_isaiInstalledCache.HasValue) return _isaiInstalledCache.Value;
                bool found = false;
                try
                {
                    var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                    if (infos != null)
                    {
                        foreach (var key in infos.Keys)
                        {
                            if (string.IsNullOrEmpty(key)) continue;
                            if (key.IndexOf(ISAI_PLUGIN_GUID_FRAGMENT, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }
                catch { return false; }
                _isaiInstalledCache = found;
                return found;
            }
        }
    }
}
