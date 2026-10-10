using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // [InfinityHammer] KeepAreaGrabScale (default off): a scaled Infinity Hammer multi-object selection keeps its size.
    //
    // IH places a multi-object selection (area grab, zoop, a blueprint) in ObjectSelection.HandleMultiple. It walks
    // the ghost's children and gets each object's ZDO from World Edit Commands' DataHelper.Init(int prefab, Transform,
    // data). WEC 1.77 took the child's lossyScale there; WEC 1.80 takes its localScale. The scale set with the hammer
    // sits on the selection ROOT, so a child's localScale is still the size it was grabbed at. The ghost shows the new
    // size, and the placed object comes out at the old one.
    //
    // With the toggle on, inside HandleMultiple, the ZDO Init returns gets the child's lossyScale (root x child: what
    // the ghost showed) in vanilla's s_scaleHash. That only happens where WEC itself wrote a scale (an
    // m_syncInitialScale prefab: trees, rocks, bushes). It is the field vanilla's ZNetView.Awake applies and saves, so
    // the size survives a relog, looks the same in a vanilla client, and saves into blueprints like any vanilla scale.
    //
    // IH and WEC load after FAP (BepInEx orders plugins by GUID), so the two targets are resolved at the main menu, once.
    [HarmonyPatch(typeof(FejdStartup), "Awake")]
    public static class VAInfinityHammerAreaScalePatch
    {
        private const string InfinityHammerGuid = "infinity_hammer";
        private const string WorldEditCommandsGuid = "world_edit_commands";
        private const string ObjectSelectionTypeName = "InfinityHammer.ObjectSelection";
        // EWD and EWP ship their own Data.DataHelper; IH calls WEC's, so it is looked up in WEC's assembly only.
        private const string DataHelperTypeName = "Data.DataHelper";

        private static bool s_tried;

        // Depth of HandleMultiple on the stack, and what the current placement changed (for the evidence line).
        private static int s_depth;
        private static int s_seen, s_fixed;
        private static string s_example;
        // 1.1.195 ([visual], ih_area_scale_test): every seen copy's ZDOID on the summary line, so a test finds the copies by id.
        private static readonly System.Collections.Generic.List<ZDOID> s_ids = new System.Collections.Generic.List<ZDOID>();

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (s_tried) return;
            s_tried = true;
            try { Hook(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: KeepAreaGrabScale could not hook Infinity Hammer: {ex.Message}");
            }
        }

        private static void Hook()
        {
            var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
            if (!plugins.TryGetValue(InfinityHammerGuid, out var ih) || ih?.Instance == null) return;
            if (!plugins.TryGetValue(WorldEditCommandsGuid, out var wec) || wec?.Instance == null) return;

            var selectionType = ih.Instance.GetType().Assembly.GetType(ObjectSelectionTypeName, false);
            var handleMultiple = selectionType == null ? null
                : AccessTools.Method(selectionType, "HandleMultiple", new[] { typeof(GameObject) });

            var dataHelperType = wec.Instance.GetType().Assembly.GetType(DataHelperTypeName, false);
            var init = dataHelperType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Init" && m.ReturnType == typeof(ZDO) && IsPrefabTransformOverload(m));

            if (handleMultiple == null || init == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: KeepAreaGrabScale found Infinity Hammer {ih.Metadata.Version} and " +
                                 $"World Edit Commands {wec.Metadata.Version} but not their placement methods " +
                                 $"(HandleMultiple: {handleMultiple != null}, DataHelper.Init(int, Transform, data): {init != null}); the toggle does nothing.");
                return;
            }

            var self = typeof(VAInfinityHammerAreaScalePatch);
            VaPieces.harmony.Patch(handleMultiple,
                prefix: new HarmonyMethod(self, nameof(EnterPlacement)),
                finalizer: new HarmonyMethod(self, nameof(ExitPlacement)));
            VaPieces.harmony.Patch(init, postfix: new HarmonyMethod(self, nameof(InitPostfix)));
            VaPieces.Log?.LogDebug($"KeepAreaGrabScale: hooked Infinity Hammer {ih.Metadata.Version} multi-object placement " +
                                   $"and World Edit Commands {wec.Metadata.Version} DataHelper.Init (toggle {(Enabled ? "on" : "off")}).");
        }

        private static bool IsPrefabTransformOverload(MethodInfo method)
        {
            var parameters = method.GetParameters();
            return parameters.Length == 3
                && parameters[0].ParameterType == typeof(int)
                && parameters[1].ParameterType == typeof(Transform);
        }

        private static bool Enabled => ConfigManager.Instance?.configIHKeepAreaGrabScale?.Value ?? false;

        private static void EnterPlacement()
        {
            if (s_depth++ == 0)
            {
                s_seen = 0;
                s_fixed = 0;
                s_example = null;
                s_ids.Clear();
                // 1.1.196: say so when the toggle is the reason a scaled selection places at its grabbed size.
                if (!Enabled) VaPieces.Log?.LogDebug("KeepAreaGrabScale: multi-object placement left at the grabbed scale ([InfinityHammer] KeepAreaGrabScale = false)");
            }
        }

        private static void ExitPlacement()
        {
            if (--s_depth > 0) return;
            s_depth = 0;
            if (s_seen > 0)
                VaPieces.Log?.LogDebug($"KeepAreaGrabScale: placed {s_fixed} of {s_seen} scalable object(s) at the ghost scale" +
                                       (s_example != null ? $" (first: {s_example})" : " (already at the ghost scale)") +
                                       $" ids: {string.Join(",", s_ids)}");
        }

        // Runs after WEC built the ZDO and before IH instantiates the object, which reads the ZDO in ZNetView.Awake.
        private static void InitPostfix(object[] __args, ZDO __result)
        {
            if (s_depth <= 0 || __result == null || !Enabled) return;
            if (__args == null || __args.Length < 2 || !(__args[1] is Transform child) || child == null) return;

            // 1.1.198 (Fire: "whatever the ghost preview shows for scale should be what's applied", every piece, no exceptions).
            // WEC writes s_scaleHash only for a prefab whose ZNetView syncs its initial scale; for any other prefab the
            // baseline is the prefab's own scale, and FAP writes the ghost scale plus its marker, which AreaScaleAwake below
            // applies on every FAP client's ZNetView.Awake (so it shows at once, after a relog, and for other FAP players).
            string prefabName = __args[0] is int hash && ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash)?.name : null;
            GameObject prefabGo = __args[0] is int h2 && ZNetScene.instance ? ZNetScene.instance.GetPrefab(h2) : null;
            Vector3 written = __result.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            bool vanillaScalable = written != Vector3.zero;
            Vector3 baseline = vanillaScalable ? written : (prefabGo != null ? prefabGo.transform.localScale : Vector3.one);

            s_seen++;
            s_ids.Add(__result.m_uid);
            Vector3 ghost = child.lossyScale;
            if ((ghost - baseline).sqrMagnitude < 1e-8f) return;

            __result.Set(ZDOVars.s_scaleHash, ghost);
            if (!vanillaScalable) __result.Set(AreaScaleMarker, 1);
            s_fixed++;
            VaPieces.Log?.LogDebug($"area place {prefabName ?? "?"}: preview scale {ghost:F2} -> placed {ghost:F2} (persisted{(vanillaScalable ? "" : ", FAP-applied")}) was {baseline:F2}");
            if (s_example == null) s_example = $"{prefabName ?? "?"} {baseline:F2} -> {ghost:F2}";
        }

        // ZDO key marking a scale FAP wrote for a prefab vanilla does not scale (no m_syncInitialScale).
        internal static readonly int AreaScaleMarker = "fap_area_scale".GetStableHashCode();
    }

    // 1.1.198: applies FAP's area-place scale to prefabs whose ZNetView ignores s_scaleHash, on load, for every FAP client.
    // Only ZDOs carrying FAP's marker are touched, so nothing vanilla or another mod scaled is affected.
    [HarmonyPatch(typeof(ZNetView), "Awake")]
    public static class VAAreaScaleAwakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ZNetView __instance)
        {
            try
            {
                if (__instance == null || __instance.m_syncInitialScale) return;
                var zdo = __instance.GetZDO();
                if (zdo == null || zdo.GetInt(VAInfinityHammerAreaScalePatch.AreaScaleMarker, 0) != 1) return;
                Vector3 s = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                if (s != Vector3.zero) __instance.transform.localScale = s;
            }
            catch { }
        }
    }
}
