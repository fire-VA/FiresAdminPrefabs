using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Missing Pieces' drawers put a Container and a Sign behind one CustomInteract: E opens the container, the alt key edits
    // the label. Vanilla picks what to hover and use with GetComponentInParent<Hoverable>/<Interactable>(), which returns the
    // FIRST such component, so the source prefab had CustomInteract above both. Without that mod the bundle's own slot loads
    // dead, and FAP's stand-in can only be added at the END of the component list, behind the Sign: E would edit the label
    // and the drawer could never be opened. Unity can't reorder components at runtime, so the Sign and the Container are
    // rebuilt behind CustomInteract with their serialized fields copied across, once, on the prefab at injection.
    internal static class CustomInteractWiring
    {
        internal static void Wire(GameObject prefab, CustomInteract customInteract)
        {
            try
            {
                bool movedSign = MoveToEnd<Sign>(prefab);
                bool movedContainer = MoveToEnd<Container>(prefab);
                var sign = prefab.GetComponent<Sign>();
                var container = prefab.GetComponent<Container>();
                customInteract.Sign = sign;
                customInteract.Container = container;

                // The bundle's own values for these lived in the dead slot; the hover reads the drawer's name and the label's.
                if (string.IsNullOrEmpty(customInteract.Name) && container != null) customInteract.Name = container.m_name;
                if (string.IsNullOrEmpty(customInteract.AlternateInteract) && sign != null) customInteract.AlternateInteract = sign.m_name;

                Debug.Log($"{VaPieces.PluginName}: CustomInteract on '{prefab.name}': " +
                          $"{(movedSign ? "Sign" : "no Sign")} and {(movedContainer ? "Container" : "no Container")} rebuilt behind it, " +
                          "so use opens the container and alt-use edits the label.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: CustomInteract wiring on '{prefab.name}' failed: {ex.Message}");
            }
        }

        // Destroys T and adds a new T at the end of the list carrying the old one's serialized fields. References to children
        // of the same prefab stay valid; instancing remaps them like any other in-prefab reference.
        private static bool MoveToEnd<T>(GameObject prefab) where T : MonoBehaviour
        {
            var old = prefab.GetComponent<T>();
            if (old == null) return false;
            var values = new List<KeyValuePair<FieldInfo, object>>();
            foreach (var field in SerializedFields(typeof(T)))
                values.Add(new KeyValuePair<FieldInfo, object>(field, field.GetValue(old)));
            bool enabled = old.enabled;

            UnityEngine.Object.DestroyImmediate(old, allowDestroyingAssets: true);
            var fresh = prefab.AddComponent<T>();
            foreach (var pair in values) pair.Key.SetValue(fresh, pair.Value);
            fresh.enabled = enabled;
            return true;
        }

        // What Unity serializes on a MonoBehaviour: public or [SerializeField] instance fields, minus [NonSerialized] and readonly.
        private static IEnumerable<FieldInfo> SerializedFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var field in t.GetFields(flags))
                {
                    if (field.IsInitOnly || field.IsNotSerialized) continue;
                    if (field.IsPublic || field.IsDefined(typeof(SerializeField), false)) yield return field;
                }
        }
    }
}
