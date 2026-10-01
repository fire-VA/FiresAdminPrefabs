using System;
using UnityEngine;

namespace VerdantsAscent.Api
{
    // Raised once per bundle prefab, right after FAP has injected it into ZNetScene and re-attached the scripts
    // its manifest names. A manifest can carry a component and its plain fields, but not a reference (a child
    // Transform, a sibling component's EffectList), so a plugin that ships the scripts some bundle needs finishes
    // that wiring here, on the prefab, before anything is instantiated from it.
    //
    // Handlers run on the main thread in subscription order. One that throws is logged with the prefab's name and
    // does not stop the others, or the load.
    public static class FapExternalPrefabs
    {
        public static event Action<GameObject> Registered;

        internal static void Raise(GameObject prefab)
        {
            var handlers = Registered;
            if (handlers == null || prefab == null) return;
            foreach (Action<GameObject> handler in handlers.GetInvocationList())
            {
                try { handler(prefab); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: a FapExternalPrefabs.Registered handler ({handler.Method.DeclaringType?.FullName}) failed on '{prefab.name}': {ex.Message}");
                }
            }
        }
    }
}
