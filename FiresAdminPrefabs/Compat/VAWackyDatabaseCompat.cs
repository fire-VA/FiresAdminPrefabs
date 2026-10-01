using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Compat
{
    // WackysDatabase (WackyMole.WackysDatabase) integration. Reflection only — WDB publishes
    // API.WackyAPI for exactly this, so FAP carries no assembly reference and every entry point
    // no-ops when it isn't installed.
    //
    // Three problems, all met at the one method WDB resolves a clone's base prefab through:
    //
    //   timing     WDB resolves clone sources from an ObjectDB.Awake postfix. FAP's scan is
    //              deferred until the player spawns, so its pieces aren't on the hammer yet and
    //              a clone of one silently resolves to nothing. Harmony priority can't fix that;
    //              the frames are already gone. The postfix registers the one prefab WDB asked
    //              for instead of forcing a reload or moving the whole scan up.
    //
    //   identity   A clone carries the user's chosen name, so FAP's ownership and vanilla-friendly
    //              rules read it as foreign content and skip it. ResolveCloneSource maps a clone
    //              back to the prefab it came from so those rules answer the same for both.
    //
    //   safety     Stubs, stand-ins, bake parents and bundle prefabs must never be clone sources.
    //              A clone of one saves a ZDO whose prefab nothing registers, which is the
    //              missing-prefab-hash orphan we already clean up after.
    public static class VAWackyDatabaseCompat
    {
        private const string ApiTypeName = "API.WackyAPI, WackysDatabase";
        private const string DataHelpersTypeName = "wackydatabase.Datas.DataHelpers, WackysDatabase";
        private const string FindPieceMethodName = "FindPieceObjectName";
        private const string AddBlacklistCloneMethodName = "AddBlacklistClone";
        private const string GetClonedMapMethodName = "GetClonedMap";
        private const string BakeParentPrefabName = "VA_BakeParent";

        private static MethodInfo _addBlacklistClone;
        private static MethodInfo _getClonedMap;
        private static bool _apiResolved;
        private static bool _apiPresent;

        // Clone name → source prefab name, "" for a name WDB doesn't know. The lookup is a
        // reflected Invoke and the menu asks about every piece, so the answer is kept.
        private static readonly Dictionary<string, string> _cloneSourceByName =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly HashSet<string> _blacklistPublished =
            new HashSet<string>(StringComparer.Ordinal);

        public static bool IsInstalled => ResolveApi();

        private static bool Enabled =>
            (ConfigManager.Instance?.configWackyDatabaseCompat?.Value ?? true) && ResolveApi();

        private static bool ResolveApi()
        {
            if (_apiResolved) return _apiPresent;
            _apiResolved = true;
            try
            {
                var api = Type.GetType(ApiTypeName);
                if (api == null) return false;
                _addBlacklistClone = api.GetMethod(AddBlacklistCloneMethodName, BindingFlags.Static | BindingFlags.Public);
                _getClonedMap = api.GetMethod(GetClonedMapMethodName, BindingFlags.Static | BindingFlags.Public);
                _apiPresent = _addBlacklistClone != null || _getClonedMap != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: WackysDatabase API lookup failed: {ex.Message}");
            }
            return _apiPresent;
        }

        // Prefabs FAP registers that only mean anything while FAP is loaded and holding their
        // backing data. Checked by name so it holds before the piece exists.
        public static bool IsUnsafeCloneSource(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (prefabName.StartsWith(VABlueprintHammerIntegration.BlueprintPrefabPrefix, StringComparison.Ordinal)) return true;
            if (prefabName.StartsWith(VAVanillaPieceManager.StandinPrefabPrefix, StringComparison.Ordinal)) return true;
            if (string.Equals(prefabName, BakeParentPrefabName, StringComparison.Ordinal)) return true;
            return VABakeBundle.TryGetCacheKeyForPrefab(prefabName) != null;
        }

        // Tells WDB to refuse these before it reaches its own resolution, so it reports the
        // refusal in its log instead of failing further in. The lookup postfix enforces the same
        // rule regardless, because this can only cover what is registered when it runs.
        public static void PublishCloneBlacklist()
        {
            if (!Enabled || _addBlacklistClone == null) return;

            int published = 0;
            foreach (string name in CollectUnsafeCloneSources())
            {
                if (!_blacklistPublished.Add(name)) continue;
                try
                {
                    _addBlacklistClone.Invoke(null, new object[] { name });
                    published++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: WackysDatabase blacklist rejected '{name}': {ex.Message}");
                }
            }

            if (published > 0)
                Debug.Log($"{VaPieces.PluginName}: WackysDatabase — refused {published} FAP prefab(s) as clone sources ({_blacklistPublished.Count} total).");
        }

        private static IEnumerable<string> CollectUnsafeCloneSources()
        {
            yield return BakeParentPrefabName;

            foreach (string stubName in VABlueprintHammerIntegration.RegisteredBlueprintPrefabNames)
                yield return stubName;

            foreach (string bundlePrefab in VABakeBundle.RegisteredBundlePrefabNames)
                yield return bundlePrefab;

            var scene = ZNetScene.instance;
            if (scene?.m_prefabs == null) yield break;
            foreach (var prefab in scene.m_prefabs)
            {
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null) continue;
                if (prefab.name.StartsWith(VAVanillaPieceManager.StandinPrefabPrefix, StringComparison.Ordinal))
                    yield return prefab.name;
            }
        }

        // The prefab a piece should be judged as. Returns the input unchanged for anything WDB
        // didn't clone, and for a clone whose source is no longer in the scene.
        public static GameObject ResolveCloneSource(GameObject pieceObject)
        {
            if ((UnityEngine.Object)pieceObject == (UnityEngine.Object)null) return pieceObject;
            if (!Enabled || _getClonedMap == null) return pieceObject;

            string sourceName = LookupCloneSourceName(pieceObject.name);
            if (string.IsNullOrEmpty(sourceName)) return pieceObject;

            var source = ZNetScene.instance?.GetPrefab(sourceName);
            return (UnityEngine.Object)source == (UnityEngine.Object)null ? pieceObject : source;
        }

        private static string LookupCloneSourceName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (_cloneSourceByName.TryGetValue(prefabName, out string cached)) return cached;

            string sourceName = string.Empty;
            try
            {
                sourceName = _getClonedMap.Invoke(null, new object[] { prefabName }) as string ?? string.Empty;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: WackysDatabase clone lookup failed for '{prefabName}': {ex.Message}");
            }

            // A clone of a clone resolves to its own source, so the chain is worth nothing here;
            // WDB already flattens what it stores.
            _cloneSourceByName[prefabName] = sourceName;
            return sourceName;
        }

        internal static void ResetCaches()
        {
            _cloneSourceByName.Clear();
            _blacklistPublished.Clear();
        }

        private static MethodBase ResolveFindPieceMethod()
        {
            try { return Type.GetType(DataHelpersTypeName)?.GetMethod(FindPieceMethodName, BindingFlags.Static | BindingFlags.Public); }
            catch { return null; }
        }

        // WDB's single resolution path for a clone's base prefab, used by both its reload
        // coroutine and SetPieceRecipeData.
        [HarmonyPatch]
        private static class DataHelpers_FindPieceObjectName_Patch
        {
            private static bool Prepare() => ResolveFindPieceMethod() != null;

            private static MethodBase TargetMethod() => ResolveFindPieceMethod();

            private static void Postfix(string name, ref GameObject __result)
            {
                if (!Enabled) return;

                if (IsUnsafeCloneSource(name))
                {
                    if ((UnityEngine.Object)__result != (UnityEngine.Object)null)
                        Debug.LogWarning($"{VaPieces.PluginName}: WackysDatabase asked to clone '{name}', which only exists while FAP holds its data. Refused — a clone of it would save an unresolvable ZDO.");
                    __result = null;
                    return;
                }

                if ((UnityEngine.Object)__result != (UnityEngine.Object)null) return;

                var registered = VAVanillaPieceManager.RegisterSinglePieceOnDemand(name);
                if ((UnityEngine.Object)registered == (UnityEngine.Object)null) return;

                __result = registered;
                Debug.Log($"{VaPieces.PluginName}: WackysDatabase asked for '{name}' before the hammer scan reached it — registered it now so the clone resolves.");
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake_WdbCompat_Patch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                ResetCaches();
                PublishCloneBlacklist();
            }
        }
    }
}
