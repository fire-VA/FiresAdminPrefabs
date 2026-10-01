using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // Ordering-proof guarantee that FAP's bundle clutter is visible to Expand World Data.
    //
    // EWD snapshots ClutterSystem.m_clutter into a static dictionary ONCE, inside
    // ClutterManager.Initialize, and never re-reads it. A clutter name absent at that
    // instant is frozen out for the session — every reload then logs "Failed to find
    // clutter prefab" and drops it, and re-injecting afterwards does nothing for EWD.
    //
    // FAP registers its clutter at ClutterSystem.Awake and ZNetScene.Awake, both of
    // which precede ZoneSystem.Start, so the normal case already works. This prefix
    // re-runs that idempotent registration immediately before EWD takes its snapshot,
    // so the guarantee no longer depends on Unity execution order.
    //
    // Prepare() returns false without EWD, making this a silent no-op on profiles
    // that don't have it.
    [HarmonyPatch]
    public static class VAEWDClutterInitializePatch
    {
        private const string ClutterManagerFullName = "ExpandWorldData.ClutterManager";
        private const string ClutterManagerTypeName = "ClutterManager";
        private const string ExpandWorldDataAssemblyName = "ExpandWorldData";

        private static MethodInfo _initializeMethod;

        public static bool Prepare()
        {
            try
            {
                var clutterManagerType = FindClutterManagerType();
                if (clutterManagerType == null) return false;

                _initializeMethod = AccessTools.Method(clutterManagerType, "Initialize");
                if (_initializeMethod == null)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAEWDClutterInitializePatch Prepare() — {clutterManagerType.FullName} found but Initialize() is missing (EWD layout changed?). Skipping.");
                    return false;
                }

                Debug.Log($"{VaPieces.PluginName}: VAEWDClutterInitializePatch will re-inject FAP clutter before {clutterManagerType.Assembly.GetName().Name}.dll snapshots ClutterSystem.m_clutter for clutter name resolution.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEWDClutterInitializePatch Prepare() failed: {ex.Message}");
                return false;
            }
        }

        public static MethodBase TargetMethod() => _initializeMethod;

        public static void Prefix()
        {
            try
            {
                VAExternalBundleLoader.RegisterClutterFromManifests();
                VAExternalBundleLoader.EnsureRegistered();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEWDClutterInitializePatch Prefix() failed: {ex.Message}");
            }
        }

        // Exact name first, then a simple-name scan of the EWD assembly so a future
        // namespace move can't silently break the hook.
        private static Type FindClutterManagerType()
        {
            var byFullName = FindTypeQuietly(ClutterManagerFullName);
            if (byFullName != null) return byFullName;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name.IndexOf(ExpandWorldDataAssemblyName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                        if (type != null && type.Name == ClutterManagerTypeName) return type;
                }
                catch { }
            }
            return null;
        }

        private static Type FindTypeQuietly(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }
    }
}
