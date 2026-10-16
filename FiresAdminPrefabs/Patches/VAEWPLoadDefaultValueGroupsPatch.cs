using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Null-safe replacement for ExpandWorldPrefabs' DataLoading.LoadDefaultValueGroups.
    // EWP calls GetType() on every ZNetView.m_tempComponents entry with no null check, so
    // a prefab with a missing-script slot yields a fake-null component and NREs the whole
    // ZoneSystem.Start content init. Sibling of VAEWDLoadDefaultValueGroupsPatch, but EWP's
    // version calls its private AddDefaultValue and needs its own faithful copy.
    //
    // Beyond EWP's body it skips fake-null components, and guards the Humanoid/WearNTear
    // alias lookups that EWP indexes blindly — they throw KeyNotFoundException in a world
    // with no Humanoid or WearNTear prefab.
    [HarmonyPatch]
    public static class VAEWPLoadDefaultValueGroupsPatch
    {
        private static Type _dataLoadingType;
        private static FieldInfo _defaultValueGroupsField;
        private static FieldInfo _wearNTearHashField;
        private static FieldInfo _humanoidHashField;
        private static FieldInfo _creatureHashField;
        private static FieldInfo _structureHashField;
        private static MethodInfo _loadDefaultValueGroupsMethod;

        public static bool Prepare()
        {
            try
            {
                _dataLoadingType = FindEwpDataLoadingType();
                if (_dataLoadingType == null) return false;

                // 1.1.265 (Fire 10-15: a HarmonyX "Could not find method ... LoadDefaultValueGroups" warning on every start): quiet
                // lookups, as in the EWD sibling. EWP 1.63 renamed it (LoadDefaults / DefaultGroups) and no longer NREs on FAP's
                // missing-script prefabs, so absence is the normal case now; one Info line says so.
                const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _loadDefaultValueGroupsMethod = _dataLoadingType.GetMethod("LoadDefaultValueGroups", Any);
                if (_loadDefaultValueGroupsMethod == null)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch: target absent (ExpandWorldPrefabs {_dataLoadingType.Assembly.GetName().Version} has no Data.DataLoading.LoadDefaultValueGroups; the NRE it guarded is gone upstream)");
                    return false;
                }

                _defaultValueGroupsField = _dataLoadingType.GetField("DefaultValueGroups", Any);
                _wearNTearHashField      = _dataLoadingType.GetField("WearNTearHash", Any);
                _humanoidHashField       = _dataLoadingType.GetField("HumanoidHash", Any);
                _creatureHashField       = _dataLoadingType.GetField("CreatureHash", Any);
                _structureHashField      = _dataLoadingType.GetField("StructureHash", Any);

                if (_defaultValueGroupsField == null
                    || _wearNTearHashField == null
                    || _humanoidHashField == null
                    || _creatureHashField == null
                    || _structureHashField == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch Prepare() — ExpandWorldPrefabs Data.DataLoading found but expected private static fields are missing (EWP internal layout changed?). Skipping patch.");
                    return false;
                }

                Debug.Log($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch will apply to {_dataLoadingType.Assembly.GetName().Name}.dll's Data.DataLoading.LoadDefaultValueGroups (null-component safety).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch Prepare() failed: {ex.Message}");
                return false;
            }
        }

        public static MethodBase TargetMethod() => _loadDefaultValueGroupsMethod;

        public static bool Prefix()
        {
            try
            {
                var defaultValueGroups = (Dictionary<int, List<string>>)_defaultValueGroupsField.GetValue(null);
                if (defaultValueGroups == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch — EWP's DefaultValueGroups is null at LoadDefaultValueGroups time; falling through to vanilla.");
                    return true;
                }

                int wearNTearHash = (int)_wearNTearHashField.GetValue(null);
                int humanoidHash  = (int)_humanoidHashField.GetValue(null);
                int creatureHash  = (int)_creatureHashField.GetValue(null);
                int structureHash = (int)_structureHashField.GetValue(null);

                if (defaultValueGroups.Count == 0)
                {
                    var scene = ZNetScene.instance;
                    if (scene == null || scene.m_namedPrefabs == null)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch — ZNetScene.m_namedPrefabs unavailable; falling through to vanilla.");
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

                        gameObject.GetComponentsInChildren(ZNetView.m_tempComponents);
                        foreach (var temp in ZNetView.m_tempComponents)
                        {
                            if (temp == null)
                            {
                                skippedNullComponents++;
                                continue;
                            }

                            AddDefaultValue(defaultValueGroups, temp.GetType().Name, gameObject.name);
                            if (temp is WearNTear wearNtear)
                                AddDefaultValue(defaultValueGroups, $"material_{wearNtear.m_materialType}", gameObject.name);
                            if (temp is ItemDrop itemDrop && itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null)
                                AddDefaultValue(defaultValueGroups, $"itemtype_{itemDrop.m_itemData.m_shared.m_itemType}", gameObject.name);

                            builtComponents++;
                        }
                    }

                    Debug.Log($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch — built {defaultValueGroups.Count} default value group(s) from {scene.m_namedPrefabs.Count} prefab(s) ({builtComponents} components catalogued, skipped {skippedNullComponents} missing-script components, {skippedNullPrefabs} fake-null prefabs).");
                }

                if (defaultValueGroups.ContainsKey(humanoidHash))
                    defaultValueGroups[creatureHash] = defaultValueGroups[humanoidHash];
                if (defaultValueGroups.ContainsKey(wearNTearHash))
                    defaultValueGroups[structureHash] = defaultValueGroups[wearNTearHash];

                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEWPLoadDefaultValueGroupsPatch Prefix() crashed: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }

        // Mirror of EWP's private Data.DataLoading.AddDefaultValue.
        private static void AddDefaultValue(Dictionary<int, List<string>> defaultValueGroups, string name, string prefab)
        {
            int stableHashCode = name.ToLowerInvariant().Replace(" ", "_").GetStableHashCode();
            if (!defaultValueGroups.ContainsKey(stableHashCode))
                defaultValueGroups[stableHashCode] = new List<string>();
            defaultValueGroups[stableHashCode].Add(prefab);
        }

        // 1.1.212: EWP's Data.DataLoading, picked by its assembly's name. ExpandWorldData declares a Data.DataLoading
        // too, and EWD 1.76 dropped the DefaultPrefabComponents field the old field-based guess used to tell them apart.
        // Applied from Ascend.Start (DeferredPatchTypes): EWP may load after FAP, so its assembly isn't there at Awake.
        private static Type FindEwpDataLoadingType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetName().Name != "ExpandWorldPrefabs") continue;
                    var t = asm.GetType("Data.DataLoading", throwOnError: false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
