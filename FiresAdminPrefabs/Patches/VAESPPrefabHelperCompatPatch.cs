using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Replaces JereKuusela-ESP's Data.PrefabHelper.LoadGroups with a
    // null-safe equivalent. Sibling of VAEWDLoadDefaultValueGroupsPatch —
    // same bug pattern in a different mod's matching method (component
    // iteration without a fake-null guard NREs on bundle prefabs with
    // "missing script" components, half-populates the Groups dict, then
    // cascades into KeyNotFoundException downstream).
    //
    // No-op when ESP isn't installed (Prepare returns false).
    [HarmonyPatch]
    public static class VAESPPrefabHelperCompatPatch
    {
        private static Type _prefabHelperType;
        private static FieldInfo _groupsField;
        private static FieldInfo _wearNTearHashField;
        private static FieldInfo _humanoidHashField;
        private static FieldInfo _creatureHashField;
        private static FieldInfo _structureHashField;

        public static bool Prepare()
        {
            try
            {
                _prefabHelperType = FindTypeQuietly("Data.PrefabHelper");
                if (_prefabHelperType == null) return false;

                // ESP's PrefabHelper has a private static LoadGroups; EWD's
                // doesn't. Direct reflection rather than AccessTools.Method
                // here because the AccessTools variant logs a HarmonyX
                // warning on null returns — and the EWD-installed-but-no-ESP
                // case is expected, not a real miss.
                var loadGroupsMethod = _prefabHelperType.GetMethod(
                    "LoadGroups",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                if (loadGroupsMethod == null) return false;

                _groupsField        = AccessTools.Field(_prefabHelperType, "Groups");
                _wearNTearHashField = AccessTools.Field(_prefabHelperType, "WearNTearHash");
                _humanoidHashField  = AccessTools.Field(_prefabHelperType, "HumanoidHash");
                _creatureHashField  = AccessTools.Field(_prefabHelperType, "CreatureHash");
                _structureHashField = AccessTools.Field(_prefabHelperType, "StructureHash");

                if (_groupsField == null
                    || _wearNTearHashField == null
                    || _humanoidHashField == null
                    || _creatureHashField == null
                    || _structureHashField == null)
                    return false;

                Debug.Log($"{VaPieces.PluginName}: VAESPPrefabHelperCompatPatch will apply to {_prefabHelperType.Assembly.GetName().Name}.dll's Data.PrefabHelper.LoadGroups (null-component safety).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAESPPrefabHelperCompatPatch Prepare() failed: {ex.Message}");
                return false;
            }
        }

        public static MethodBase TargetMethod()
            => _prefabHelperType.GetMethod(
                "LoadGroups",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

        public static bool Prefix()
        {
            try
            {
                var groups = (Dictionary<int, List<int>>)_groupsField.GetValue(null);
                if (groups == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAESPPrefabHelperCompatPatch — ESP's Groups field is null at LoadGroups time; falling through to vanilla.");
                    return true;
                }
                if (groups.Count > 0) return false;

                int wearNTearHash = (int)_wearNTearHashField.GetValue(null);
                int humanoidHash  = (int)_humanoidHashField.GetValue(null);
                int creatureHash  = (int)_creatureHashField.GetValue(null);
                int structureHash = (int)_structureHashField.GetValue(null);

                int skippedNullComponents = 0;
                int skippedNullPrefabs = 0;

                foreach (var kvp in ZNetScene.instance.m_namedPrefabs)
                {
                    if (kvp.Value == null)
                    {
                        skippedNullPrefabs++;
                        continue;
                    }
                    kvp.Value.GetComponentsInChildren(ZNetView.m_tempComponents);
                    foreach (var temp in ZNetView.m_tempComponents)
                    {
                        if (temp == null)
                        {
                            skippedNullComponents++;
                            continue;
                        }
                        int hash = temp.GetType().Name.ToLowerInvariant().GetStableHashCode();
                        if (!groups.ContainsKey(hash))
                            groups[hash] = new List<int>();
                        groups[hash].Add(kvp.Key);
                    }
                }

                if (groups.ContainsKey(humanoidHash))
                    groups[creatureHash] = groups[humanoidHash];
                if (groups.ContainsKey(wearNTearHash))
                    groups[structureHash] = groups[wearNTearHash];

                Debug.Log($"{VaPieces.PluginName}: VAESPPrefabHelperCompatPatch — built {groups.Count} groups from {ZNetScene.instance.m_namedPrefabs.Count} prefabs (skipped {skippedNullComponents} missing-script components, {skippedNullPrefabs} fake-null prefabs).");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAESPPrefabHelperCompatPatch Prefix() crashed: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }

        private static Type FindTypeQuietly(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
