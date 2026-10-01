using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Writes armed field edits in the format Valheim's own loader reads (ZNetView.LoadFields): each value under
    // "<ComponentType>.<field>", plus HasFields and HasFields<ComponentType>. Every other client then creates the
    // object with the fields applied, now and on every later load - no network code of ours.
    // Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    internal static class VAExtraDataStamp
    {
        private const string HasFieldsKey = "HasFields";
        private const string HasFieldsPrefix = "HasFields";
        private const char KeySeparator = '.';

        public static bool StampArmedValues(GameObject placed, string prefabName)
        {
            if (placed == null) return false;
            if (!VAExtraDataQueue.TryGetFor(prefabName, out List<VAExtraValue> values)) return false;

            ZNetView netView = placed.GetComponent<ZNetView>();
            ZDO zdo = netView != null ? netView.GetZDO() : null;
            if (zdo == null) return false;

            WriteValues(zdo, values);
            return true;
        }

        public static void WriteValues(ZDO zdo, List<VAExtraValue> values)
        {
            if (zdo == null || values == null || values.Count == 0) return;

            zdo.Set(HasFieldsKey, true);
            var gatedTypes = new HashSet<string>();
            foreach (VAExtraValue value in values)
            {
                string componentType = ComponentTypeOf(value.Key);
                if (componentType.Length > 0 && gatedTypes.Add(componentType))
                    zdo.Set(HasFieldsPrefix + componentType, true);

                switch (value.Kind)
                {
                    case VAExtraFieldKind.Int:
                        zdo.Set(value.Key, (int)value.Value);
                        break;
                    case VAExtraFieldKind.Float:
                        zdo.Set(value.Key, (float)value.Value);
                        break;
                    case VAExtraFieldKind.Bool:
                        zdo.Set(value.Key, (bool)value.Value);
                        break;
                    case VAExtraFieldKind.Vector3:
                        zdo.Set(value.Key, (Vector3)value.Value);
                        break;
                    default:
                        zdo.Set(value.Key, Convert.ToString(value.Value));
                        break;
                }
            }
        }

        /// <summary>The placer's own copy already exists by the time the ZDO is written, so the loader has missed
        /// it. Setting the values on the PREFAB for the duration of one PlacePiece makes the placed copy start
        /// life with them; the returned scope puts the prefab back.</summary>
        public static IDisposable BeginPrefabSwap()
        {
            if (!VAExtraDataQueue.IsArmed) return null;
            GameObject prefab = ZNetScene.instance != null
                ? ZNetScene.instance.GetPrefab(VAExtraDataQueue.ArmedPrefab)
                : null;
            if (prefab == null) return null;
            if (!VAExtraDataQueue.TryGetFor(VAExtraDataQueue.ArmedPrefab, out List<VAExtraValue> values)) return null;

            return new PrefabFieldSwap(prefab, values);
        }

        private static string ComponentTypeOf(string key)
        {
            int separator = key.IndexOf(KeySeparator);
            return separator > 0 ? key.Substring(0, separator) : string.Empty;
        }

        private sealed class PrefabFieldSwap : IDisposable
        {
            private readonly List<Action> _restores = new List<Action>();

            public PrefabFieldSwap(GameObject prefab, List<VAExtraValue> values)
            {
                MonoBehaviour[] components = prefab.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (VAExtraValue value in values)
                {
                    string componentType = value.Key.Substring(0, Math.Max(0, value.Key.IndexOf(KeySeparator)));
                    string fieldName = value.Key.Substring(value.Key.IndexOf(KeySeparator) + 1);
                    foreach (MonoBehaviour component in components)
                    {
                        if (component == null || component.GetType().Name != componentType) continue;
                        FieldInfo field = component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
                        if (field == null) continue;
                        Apply(component, field, value);
                    }
                }
            }

            private void Apply(MonoBehaviour component, FieldInfo field, VAExtraValue value)
            {
                object previous = field.GetValue(component);
                MonoBehaviour target = component;
                FieldInfo targetField = field;
                _restores.Add(() => targetField.SetValue(target, previous));

                if (value.Kind == VAExtraFieldKind.PrefabRef || value.Kind == VAExtraFieldKind.ItemRef)
                {
                    GameObject referenced = ZNetScene.instance != null
                        ? ZNetScene.instance.GetPrefab(Convert.ToString(value.Value))
                        : null;
                    if (referenced == null) return;
                    if (value.Kind == VAExtraFieldKind.ItemRef)
                    {
                        ItemDrop drop = referenced.GetComponent<ItemDrop>();
                        if (drop != null) field.SetValue(component, drop);
                        return;
                    }
                    field.SetValue(component, referenced);
                    return;
                }
                field.SetValue(component, value.Value);
            }

            public void Dispose()
            {
                for (int index = _restores.Count - 1; index >= 0; index--)
                {
                    try { _restores[index](); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: extra-data prefab restore failed: {ex.Message}");
                    }
                }
                _restores.Clear();
            }
        }
    }
}
