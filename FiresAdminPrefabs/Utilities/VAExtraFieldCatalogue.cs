using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    internal enum VAExtraFieldKind
    {
        Int,
        Float,
        Bool,
        Vector3,
        Text,
        PrefabRef,
        ItemRef,
    }

    internal sealed class VAExtraField
    {
        public string ComponentType;
        public string FieldName;
        public string Key;
        public VAExtraFieldKind Kind;
        public string PrefabValue;
        public string Hint;
        public string UnsettableReason;

        public bool Settable => string.IsNullOrEmpty(UnsettableReason);
    }

    // Every field Valheim's own per-object override can apply, read off a prefab.
    // ZNetView.LoadFields decides the whole contract: public instance fields only, one of seven types, keyed
    // "<ComponentTypeName>.<field>" behind HasFields + HasFields<ComponentTypeName>.
    // Plan: Docs/SPAWN_WITH_EXTRA_DATA.md
    internal static class VAExtraFieldCatalogue
    {
        private const string EnumReason = "enums cannot be set per object: the loader only matches int, float, bool, Vector3, string, GameObject and ItemDrop";
        private const string DuplicateReason = "two components of this type are on the prefab, and the loader keys by type name alone, so this would write to both";
        private const string RangeSeparator = " to ";

        public static List<VAExtraField> Build(GameObject prefab)
        {
            var fields = new List<VAExtraField>();
            if (prefab == null) return fields;

            MonoBehaviour[] components = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            var typeCounts = new Dictionary<string, int>();
            foreach (MonoBehaviour component in components)
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;
                typeCounts.TryGetValue(typeName, out int seen);
                typeCounts[typeName] = seen + 1;
            }

            var done = new HashSet<string>();
            foreach (MonoBehaviour component in components)
            {
                if (component == null) continue;
                Type type = component.GetType();
                string typeName = type.Name;
                if (!done.Add(typeName)) continue;

                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    VAExtraField row = Describe(component, typeName, field);
                    if (row == null) continue;
                    if (typeCounts[typeName] > 1) row.UnsettableReason = DuplicateReason;
                    fields.Add(row);
                }
            }
            fields.Sort((first, second) =>
            {
                int byType = string.Compare(first.ComponentType, second.ComponentType, StringComparison.OrdinalIgnoreCase);
                return byType != 0 ? byType : string.Compare(first.FieldName, second.FieldName, StringComparison.OrdinalIgnoreCase);
            });
            return fields;
        }

        private static VAExtraField Describe(MonoBehaviour component, string typeName, FieldInfo field)
        {
            var row = new VAExtraField
            {
                ComponentType = typeName,
                FieldName = field.Name,
                Key = typeName + "." + field.Name,
            };

            if (field.FieldType.IsEnum)
            {
                row.Kind = VAExtraFieldKind.Int;
                row.PrefabValue = Convert.ToInt32(field.GetValue(component)).ToString(CultureInfo.InvariantCulture);
                row.UnsettableReason = EnumReason;
                row.Hint = field.FieldType.Name + " - " + EnumReason;
                return row;
            }

            if (!TryKindOf(field.FieldType, out VAExtraFieldKind kind)) return null;
            row.Kind = kind;
            row.PrefabValue = Format(kind, field.GetValue(component));
            row.Hint = BuildHint(field, kind, row.PrefabValue);
            return row;
        }

        private static bool TryKindOf(Type type, out VAExtraFieldKind kind)
        {
            if (type == typeof(int)) { kind = VAExtraFieldKind.Int; return true; }
            if (type == typeof(float)) { kind = VAExtraFieldKind.Float; return true; }
            if (type == typeof(bool)) { kind = VAExtraFieldKind.Bool; return true; }
            if (type == typeof(Vector3)) { kind = VAExtraFieldKind.Vector3; return true; }
            if (type == typeof(string)) { kind = VAExtraFieldKind.Text; return true; }
            if (type == typeof(GameObject)) { kind = VAExtraFieldKind.PrefabRef; return true; }
            if (type == typeof(ItemDrop)) { kind = VAExtraFieldKind.ItemRef; return true; }
            kind = VAExtraFieldKind.Text;
            return false;
        }

        public static string Format(VAExtraFieldKind kind, object value)
        {
            if (value == null) return string.Empty;
            switch (kind)
            {
                case VAExtraFieldKind.Float:
                    return ((float)value).ToString("0.####", CultureInfo.InvariantCulture);
                case VAExtraFieldKind.Vector3:
                    Vector3 vector = (Vector3)value;
                    return string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####}",
                        vector.x, vector.y, vector.z);
                case VAExtraFieldKind.PrefabRef:
                    return ((GameObject)value).name;
                case VAExtraFieldKind.ItemRef:
                    return ((ItemDrop)value).gameObject.name;
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Parses what an admin typed, and refuses rather than half-accepting it.</summary>
        public static bool TryParse(VAExtraFieldKind kind, string text, out object value, out string problem)
        {
            value = null;
            problem = null;
            string trimmed = (text ?? string.Empty).Trim();
            switch (kind)
            {
                case VAExtraFieldKind.Int:
                    if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int asInt))
                    {
                        value = asInt;
                        return true;
                    }
                    problem = "whole number expected";
                    return false;

                case VAExtraFieldKind.Float:
                    if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float asFloat))
                    {
                        value = asFloat;
                        return true;
                    }
                    problem = "number expected";
                    return false;

                case VAExtraFieldKind.Bool:
                    if (bool.TryParse(trimmed, out bool asBool))
                    {
                        value = asBool;
                        return true;
                    }
                    problem = "true or false expected";
                    return false;

                case VAExtraFieldKind.Vector3:
                    string[] parts = trimmed.Split(',');
                    if (parts.Length == 3
                        && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        && float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        value = new Vector3(x, y, z);
                        return true;
                    }
                    problem = "three numbers expected, as x,y,z";
                    return false;

                case VAExtraFieldKind.PrefabRef:
                case VAExtraFieldKind.ItemRef:
                    if (trimmed.Length == 0)
                    {
                        problem = "prefab name expected";
                        return false;
                    }
                    if (ZNetScene.instance != null && ZNetScene.instance.GetPrefab(trimmed) == null)
                    {
                        problem = "no prefab named " + trimmed;
                        return false;
                    }
                    value = trimmed;
                    return true;

                default:
                    value = trimmed;
                    return true;
            }
        }

        private static string BuildHint(FieldInfo field, VAExtraFieldKind kind, string prefabValue)
        {
            string written = VAExtraFieldHints.For(field.DeclaringType.Name + "." + field.Name);
            if (!string.IsNullOrEmpty(written)) return written;

            string hint = DescribeKind(kind) + ", prefab value " + (string.IsNullOrEmpty(prefabValue) ? "empty" : prefabValue);
            var range = (RangeAttribute)Attribute.GetCustomAttribute(field, typeof(RangeAttribute));
            if (range != null) hint += ", valid " + range.min.ToString("0.####", CultureInfo.InvariantCulture)
                + RangeSeparator + range.max.ToString("0.####", CultureInfo.InvariantCulture);
            var tooltip = (TooltipAttribute)Attribute.GetCustomAttribute(field, typeof(TooltipAttribute));
            if (tooltip != null) hint += ". " + tooltip.tooltip;
            return hint;
        }

        private static string DescribeKind(VAExtraFieldKind kind)
        {
            switch (kind)
            {
                case VAExtraFieldKind.Int: return "whole number";
                case VAExtraFieldKind.Float: return "number";
                case VAExtraFieldKind.Bool: return "true or false";
                case VAExtraFieldKind.Vector3: return "x,y,z";
                case VAExtraFieldKind.PrefabRef: return "prefab name";
                case VAExtraFieldKind.ItemRef: return "item prefab name";
                default: return "text";
            }
        }
    }
}
