using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Rule: FAP never modifies a prefab that another mod registered through Jotunn.
    //
    // Why (Fire 10-13, Jotunn users seeing red text): FAP puts every eligible ZNetScene prefab on the Hammer and used to prepare each
    // one by changing it - a Piece component, a persistent ZNetView, snap points, a fireplace fix, a nature shader. A prefab a mod
    // registers through Jotunn's PieceManager or PrefabManager belongs to that mod, and Jotunn manages it after registration.
    //
    // What FAP does instead (VAVanillaPieceManager.ProcessEligibleForHammer asks Classify):
    //   ByReference  the prefab already has a Piece: it is added to the Hammer as it is, nothing on it is changed;
    //   LeaveAlone   no Piece: it is not added (making it placeable would mean changing it);
    //   NotJotunn    any other prefab: FAP's normal path.
    //
    // How ownership is known: the names in Jotunn's own registries (PieceManager.Pieces, PrefabManager.Prefabs), read by reflection
    // so FAP has no Jotunn dependency. Vanilla prefabs are never in them. Jotunn registers at the main menu; FAP's Hammer scan runs
    // in-world, after it, and Reset() rereads the registries for each world.
    //
    // If Jotunn is loaded but its registries can't be read (a Jotunn version that renamed them), FAP says so in ONE warning naming
    // the member, and the guard then protects nothing - it never fails silently.
    internal static class VAJotunnGuard
    {
        internal enum Decision { NotJotunn, ByReference, LeaveAlone }

        private const int NamesInSummary = 8;
        private static HashSet<string> s_owned;
        private static readonly List<string> s_byReference = new List<string>();
        private static readonly List<string> s_leftAlone = new List<string>();

        internal static int ByReferenceCount => s_byReference.Count;
        internal static int LeftAloneCount => s_leftAlone.Count;

        /// <summary>How FAP must treat this prefab on its way to the Hammer.</summary>
        internal static Decision Classify(GameObject prefab)
        {
            if (prefab == null || !IsJotunnOwned(prefab.name)) return Decision.NotJotunn;
            return prefab.GetComponent<Piece>() != null ? Decision.ByReference : Decision.LeaveAlone;
        }

        internal static void RecordByReference(string prefabName) => s_byReference.Add(prefabName);
        internal static void RecordLeftAlone(string prefabName) => s_leftAlone.Add(prefabName);

        internal static bool IsJotunnOwned(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (s_owned == null) s_owned = ReadJotunnRegistries();
            return s_owned.Contains(prefabName);
        }

        /// <summary>A new world: the registries are read again and the summary starts over.</summary>
        internal static void Reset()
        {
            s_owned = null;
            s_byReference.Clear();
            s_leftAlone.Clear();
        }

        /// <summary>One line per world after FAP's Hammer scan, naming the prefabs (the first few of each kind).</summary>
        internal static void ReportOnce()
        {
            if (s_byReference.Count == 0 && s_leftAlone.Count == 0) return;
            Debug.Log($"{VaPieces.PluginName}: Jotunn-registered prefabs (FAP never modifies them): "
                + $"{s_byReference.Count} added to the Hammer unchanged{Names(s_byReference)}; "
                + $"{s_leftAlone.Count} left out, no Piece{Names(s_leftAlone)}.");
        }

        private static string Names(List<string> list)
        {
            if (list.Count == 0) return "";
            int shown = Math.Min(NamesInSummary, list.Count);
            return " (" + string.Join(", ", list.GetRange(0, shown).ToArray()) + (list.Count > shown ? $", +{list.Count - shown} more" : "") + ")";
        }

        private static HashSet<string> ReadJotunnRegistries()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (global::VerdantsAscent.Utilities.QuietTypes.Find("Jotunn.Main") == null && global::VerdantsAscent.Utilities.QuietTypes.Find("Jotunn.Managers.PrefabManager") == null)
                return names;   // Jotunn isn't loaded: nothing to guard
            ReadRegistry(names, "Jotunn.Managers.PieceManager", "Pieces");
            ReadRegistry(names, "Jotunn.Managers.PrefabManager", "Prefabs");
            VaPieces.Log?.LogDebug($"[FAP] Jotunn registries: {names.Count} prefab name(s) owned by Jotunn mods");
            return names;
        }

        // Jotunn's managers keep their registries as a Dictionary<string, Custom*> keyed by prefab name (a field or a property).
        private static void ReadRegistry(HashSet<string> into, string typeName, string member)
        {
            var type = global::VerdantsAscent.Utilities.QuietTypes.Find(typeName);
            if (type == null) { Unreadable(typeName, member, "type not found"); return; }
            object instance;
            try { instance = AccessTools.Property(type, "Instance")?.GetValue(null, null); }
            catch (Exception ex) { Unreadable(typeName, member, "Instance threw " + (ex.InnerException ?? ex).Message); return; }
            if (instance == null) { Unreadable(typeName, member, "no Instance"); return; }
            // 1.1.399 (log sweep 11-07): plain reflection, which never logs a miss; AccessTools.Field warned on every launch
            // because Jotunn 2.30 keeps Prefabs as a property.
            object registry = type.GetField(member, AccessTools.all)?.GetValue(instance) ?? type.GetProperty(member, AccessTools.all)?.GetValue(instance, null);
            if (!(registry is IDictionary dictionary)) { Unreadable(typeName, member, registry == null ? "member not found" : $"is a {registry.GetType().Name}, not a dictionary"); return; }
            foreach (var key in dictionary.Keys) if (key is string name) into.Add(name);
        }

        private static void Unreadable(string typeName, string member, string why) =>
            Debug.LogWarning($"{VaPieces.PluginName}: Jotunn is loaded but {typeName}.{member} could not be read ({why}); FAP cannot tell which "
                + "prefabs Jotunn mods own and may modify them. Please report the Jotunn version.");
    }
}
