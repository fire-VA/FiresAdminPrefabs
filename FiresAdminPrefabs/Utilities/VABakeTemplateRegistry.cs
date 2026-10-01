using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Tracks the GameObject instance IDs of our runtime-built bundle prefab
    // templates so the ZNetView.Awake prefix below can skip vanilla's ZDO
    // logic for templates only. Templates never reach the world; their
    // clones (via Object.Instantiate) get fresh instance IDs and go through
    // the normal Awake path so their ZNetView claims the init ZDO or creates
    // a new one at placement time.
    public static class VABakeTemplateRegistry
    {
        private static readonly HashSet<int> _templateInstanceIds = new HashSet<int>();

        // Call after `new GameObject(name)` and before any AddComponent<ZNetView>.
        public static void RegisterTemplate(GameObject go)
        {
            if (go == null) return;
            _templateInstanceIds.Add(go.GetInstanceID());
        }

        // True when this GameObject is registered as a template OR is a
        // descendant of one. Parent-chain walk is required because at
        // template-build time we Instantiate source prefabs as children of
        // the template root, and their Awake fires before they're reparented
        // anywhere else.
        public static bool IsTemplate(GameObject go)
        {
            if (go == null) return false;
            var t = go.transform;
            while (t != null)
            {
                if (_templateInstanceIds.Contains(t.gameObject.GetInstanceID()))
                    return true;
                t = t.parent;
            }
            return false;
        }

        // Templates die with ZNetScene and are rebuilt on the next world load, so the
        // recorded ids are stale from that point on. Unity recycles instance ids, so
        // leaving them in would eventually make IsTemplate claim an unrelated object
        // and skip its ZNetView.Awake.
        public static void ClearAll()
        {
            _templateInstanceIds.Clear();
        }
    }
}

namespace VerdantsAscent.Patches
{
    // Skips ZNetView.Awake for runtime-built bundle prefab templates so
    // they don't spawn junk ZDOs at the template's off-world position.
    // Clones (which get fresh instance IDs from Unity at Instantiate time)
    // are never in the registry and run vanilla Awake normally.
    [HarmonyPatch(typeof(ZNetView), "Awake")]
    public static class ZNetView_Awake_VABakeTemplateBypass_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZNetView __instance)
        {
            try
            {
                if (__instance == null) return true;
                if (VerdantsAscent.Utilities.VABakeTemplateRegistry.IsTemplate(__instance.gameObject))
                    return false;
            }
            catch { }
            return true;
        }
    }
}
