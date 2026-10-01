using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Replaces ExpandWorldData's Data.DataLoading.LoadDefaultValueGroups
    // with a null-safe equivalent. EWD's vanilla loop calls .GetType() on
    // every entry in ZNetView.m_tempComponents without a null check —
    // bundle prefabs with "missing script" component slots NRE the whole
    // iteration and cascade into KeyNotFoundException downstream.
    //
    // Two safety additions over EWD's body:
    //   1. Skip fake-null components before .GetType()
    //   2. ContainsKey guards before the synthetic Creature/Structure
    //      aliases (vanilla EWD throws when no prefab has Humanoid/WNT).
    [HarmonyPatch]
    public static class VAEWDLoadDefaultValueGroupsPatch
    {
        private static Type _dataLoadingType;
        private static FieldInfo _defaultValueGroupsField;
        private static FieldInfo _defaultPrefabComponentsField;
        private static FieldInfo _wearNTearHashField;
        private static FieldInfo _humanoidHashField;
        private static FieldInfo _creatureHashField;
        private static FieldInfo _structureHashField;
        private static MethodInfo _loadDefaultValueGroupsMethod;

        public static bool Prepare()
        {
            try
            {
                // Quiet lookup — when EWD isn't installed the type's
                // absence is expected, and AccessTools.TypeByName would
                // log a HarmonyX warning per startup.
                _dataLoadingType = FindTypeQuietly("Data.DataLoading");
                if (_dataLoadingType == null) return false;

                _loadDefaultValueGroupsMethod = AccessTools.Method(_dataLoadingType, "LoadDefaultValueGroups");
                if (_loadDefaultValueGroupsMethod == null) return false;

                _defaultValueGroupsField      = AccessTools.Field(_dataLoadingType, "DefaultValueGroups");
                _defaultPrefabComponentsField = AccessTools.Field(_dataLoadingType, "DefaultPrefabComponents");
                _wearNTearHashField           = AccessTools.Field(_dataLoadingType, "WearNTearHash");
                _humanoidHashField            = AccessTools.Field(_dataLoadingType, "HumanoidHash");
                _creatureHashField            = AccessTools.Field(_dataLoadingType, "CreatureHash");
                _structureHashField           = AccessTools.Field(_dataLoadingType, "StructureHash");

                if (_defaultValueGroupsField == null
                    || _defaultPrefabComponentsField == null
                    || _wearNTearHashField == null
                    || _humanoidHashField == null
                    || _creatureHashField == null
                    || _structureHashField == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch Prepare() — Data.DataLoading found but one or more expected private static fields are missing (EWD internal layout changed?). Skipping patch.");
                    return false;
                }

                Debug.Log($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch will apply to {_dataLoadingType.Assembly.GetName().Name}.dll's Data.DataLoading.LoadDefaultValueGroups (null-component safety).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch Prepare() failed: {ex.Message}");
                return false;
            }
        }

        public static MethodBase TargetMethod() => _loadDefaultValueGroupsMethod;

        public static bool Prefix()
        {
            try
            {
                var defaultValueGroups = (Dictionary<int, List<string>>)_defaultValueGroupsField.GetValue(null);
                var defaultPrefabComponents = (Dictionary<string, List<string>>)_defaultPrefabComponentsField.GetValue(null);
                if (defaultValueGroups == null || defaultPrefabComponents == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch — EWD's DefaultValueGroups/DefaultPrefabComponents is null at LoadDefaultValueGroups time; falling through to vanilla.");
                    return true;
                }

                if (defaultValueGroups.Count != 0) return false;

                int wearNTearHash = (int)_wearNTearHashField.GetValue(null);
                int humanoidHash  = (int)_humanoidHashField.GetValue(null);
                int creatureHash  = (int)_creatureHashField.GetValue(null);
                int structureHash = (int)_structureHashField.GetValue(null);

                var scene = ZNetScene.instance;
                if (scene == null || scene.m_namedPrefabs == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch — ZNetScene.m_namedPrefabs unavailable; falling through to vanilla.");
                    return true;
                }

                int skippedNullComponents = 0;
                int skippedNullPrefabs = 0;
                int builtComponents = 0;

                foreach (GameObject gameObject in scene.m_namedPrefabs.Values)
                {
                    if ((bool)(UnityEngine.Object)gameObject == false)
                    {
                        skippedNullPrefabs++;
                        continue;
                    }

                    string name = gameObject.name;
                    if (!defaultPrefabComponents.ContainsKey(name))
                        defaultPrefabComponents[name] = new List<string>();

                    gameObject.GetComponentsInChildren(ZNetView.m_tempComponents);

                    foreach (var temp in ZNetView.m_tempComponents)
                    {
                        if (temp == null)
                        {
                            skippedNullComponents++;
                            continue;
                        }

                        string lowerInvariant = temp.GetType().Name.ToLowerInvariant();
                        int stableHashCode = lowerInvariant.GetStableHashCode();
                        if (!defaultValueGroups.ContainsKey(stableHashCode))
                            defaultValueGroups[stableHashCode] = new List<string>();
                        defaultValueGroups[stableHashCode].Add(gameObject.name);
                        defaultPrefabComponents[name].Add(lowerInvariant);

                        if (lowerInvariant == "humanoid")
                            defaultPrefabComponents[name].Add("character");
                        if (lowerInvariant == "wearntear")
                            defaultPrefabComponents[name].Add("structure");

                        builtComponents++;
                    }
                }

                if (defaultValueGroups.ContainsKey(humanoidHash))
                    defaultValueGroups[creatureHash] = defaultValueGroups[humanoidHash];
                if (defaultValueGroups.ContainsKey(wearNTearHash))
                    defaultValueGroups[structureHash] = defaultValueGroups[wearNTearHash];

                Debug.Log($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch — built {defaultValueGroups.Count} default value group(s) from {scene.m_namedPrefabs.Count} prefab(s) ({builtComponents} components catalogued, skipped {skippedNullComponents} missing-script components, {skippedNullPrefabs} fake-null prefabs).");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEWDLoadDefaultValueGroupsPatch Prefix() crashed: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }

        private static Type FindTypeQuietly(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            // Both ExpandWorldData and ExpandWorldPrefabs declare a Data.DataLoading. This patch
            // rebuilds the EWD layout, so return the one that declares DefaultPrefabComponents
            // (EWD). EWP's field-less type is handled by VAEWPLoadDefaultValueGroupsPatch.
            Type firstMatch = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t == null) continue;
                    if (firstMatch == null) firstMatch = t;
                    if (AccessTools.Field(t, "DefaultPrefabComponents") != null) return t;
                }
                catch { }
            }
            return firstMatch;
        }
    }
}
