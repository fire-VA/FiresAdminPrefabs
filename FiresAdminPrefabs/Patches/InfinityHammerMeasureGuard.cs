using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // Infinity Hammer compatibility for hammer_measure and zoop dimensions.
    //
    // IH's hammer_measure instantiates EVERY ZNetScene prefab in one frame with
    // deferred Destroy, so all of them are alive at once until frame end. Vanilla
    // survives that; a stack with companion AI and whole bundled buildings does not.
    // When IH is present the command is re-registered under the same name (Terminal
    // keeps one entry per name and the last registration wins) with a batched
    // coroutine that measures a few prefabs per frame and tears each down immediately.
    //
    // Separately, zoop needs a measured dimension to know its step size, and IH only
    // ships them for vanilla prefabs — so injected pieces don't zoop. Their AABBs are
    // computed from collider data (no instantiation) and pushed into IH's runtime
    // dictionary for missing keys only, session-scoped rather than bloating IH's
    // persisted config.
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    [HarmonyPriority(Priority.Last)]
    public static class InfinityHammerMeasureGuard
    {
        private const int PrefabsPerFrame = 25;
        private const int MaxTransforms = 400;
        private const float MinMeasurableSize = 0.01f;
        private const string InfinityHammerGuid = "infinity_hammer";
        private const string ConfigTypeName = "InfinityHammer.Configuration";
        private const string MeasureTypeName = "InfinityHammer.HammerMeasureCommand";

        private static bool _registered;

        // Infinity Hammer's types, found through BepInEx's plugin table: IH's own assembly by its GUID, else the assembly
        // of any loaded plugin that holds the name (a fork). This used AccessTools.TypeByName, which can fall back to
        // GetTypes() over every loaded assembly: 132-784 ms at the main menu's Terminal.InitTerminal, and again at the
        // join ([ghost], Tools\FAP_CLIENT_LOAD_COST.md #1). A miss is not cached, since a plugin may still be loading.
        private static Type s_configType, s_measureType;

        private static Type ConfigType => s_configType ?? (s_configType = FindPluginType(ConfigTypeName));
        private static Type MeasureType => s_measureType ?? (s_measureType = FindPluginType(MeasureTypeName));

        public static bool InfinityHammerPresent => ConfigType != null;

        private static Type FindPluginType(string fullName)
        {
            try
            {
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (plugins.TryGetValue(InfinityHammerGuid, out var ih) && ih?.Instance != null)
                {
                    var type = ih.Instance.GetType().Assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                var seen = new HashSet<System.Reflection.Assembly>();
                foreach (var plugin in plugins.Values)
                {
                    if (plugin?.Instance == null) continue;
                    var assembly = plugin.Instance.GetType().Assembly;
                    if (!seen.Add(assembly)) continue;
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: Infinity Hammer lookup for '{fullName}' failed: {ex.Message}");
            }
            return null;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (_registered || !InfinityHammerPresent || MeasureType == null) return;
            _registered = true;

            new Terminal.ConsoleCommand("hammer_measure",
                "Tries to measure all structures (FAP-safe batched version).",
                args =>
                {
                    var host = VaPieces.Instance;
                    if (host == null) { args.Context?.AddString("hammer_measure: mod host not ready."); return; }
                    host.StartCoroutine(MeasureAllCoroutine(args.Context));
                });
        }

        private static IEnumerator MeasureAllCoroutine(Terminal terminal)
        {
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) yield break;

            var checkWithCollider = AccessTools.Method(MeasureType, "CheckWithCollider");
            if (checkWithCollider == null)
            {
                terminal?.AddString("hammer_measure: Infinity Hammer internals not found — aborting.");
                yield break;
            }

            terminal?.AddString($"hammer_measure: measuring {scene.m_prefabs.Count} prefabs (batched — the game stays responsive)...");

            int measured = 0, skippedHeavy = 0, failed = 0, sliceCount = 0;
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                if (!IsMeasurable(prefab)) continue;

                if (IsUnsafeToInstantiate(prefab))
                {
                    skippedHeavy++;
                    continue;
                }

                ZNetView.m_forceDisableInit = true;
                GameObject instance = null;
                try
                {
                    instance = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                    if (instance.TryGetComponent<ShieldGenerator>(out var shieldGenerator))
                        shieldGenerator.m_isPlacementGhost = true;
                    checkWithCollider.Invoke(null, new object[] { instance });
                    measured++;
                }
                catch (Exception)
                {
                    failed++;
                }
                finally
                {
                    if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                    ZNetView.m_forceDisableInit = false;
                }

                if (++sliceCount >= PrefabsPerFrame)
                {
                    sliceCount = 0;
                    yield return null;
                }
            }

            terminal?.AddString($"hammer_measure: done — {measured} measured, {skippedHeavy} skipped (heavy/creature), {failed} failed safely.");
        }

        // Infinity Hammer's own eligibility filters, replicated.
        private static bool IsMeasurable(GameObject prefab)
        {
            string name = prefab.name;
            if (name == "Player") return false;
            if (name.StartsWith("_", StringComparison.Ordinal)) return false;
            if (name.StartsWith("fx_", StringComparison.Ordinal)) return false;
            if (name.StartsWith("sfx_", StringComparison.Ordinal)) return false;
            if (prefab.GetComponentInChildren<Ragdoll>() != null) return false;
            if (prefab.GetComponentInChildren<ItemDrop>() != null
                && prefab.GetComponentInChildren<Piece>() == null) return false;
            if (prefab.GetComponentInChildren<RandomFlyingBird>() != null) return false;
            if (prefab.GetComponentInChildren<Collider>() == null) return false;
            return true;
        }

        // Creatures never zoop, baked-mesh parents load megabytes in Awake, and huge
        // hierarchies are the memory spike that caused the crash.
        private static bool IsUnsafeToInstantiate(GameObject prefab)
        {
            if (prefab.GetComponentInChildren<Character>() != null) return true;
            if (prefab.GetComponentInChildren<VerdantsAscent.Utilities.VABakeParentLoader>() != null) return true;
            return CountTransforms(prefab.transform, MaxTransforms + 1) > MaxTransforms;
        }

        // The same push as PushDimensionsForInjected, spread over frames at `frameBudgetMs`: it ran in the one frame
        // after the heavy piece pass, a collider walk of ~2,300 prefabs ([ghost], Tools\FAP_CLIENT_LOAD_COST.md #5).
        // The entries are copied first, so the caller's dictionary may change between frames.
        public static IEnumerator PushDimensionsForInjectedCoroutine(IDictionary<string, GameObject> eligibleByName, long frameBudgetMs)
        {
            if (!InfinityHammerPresent || eligibleByName == null || eligibleByName.Count == 0) yield break;
            Dictionary<string, Vector3> dimensions;
            try
            {
                var dimensionsField = AccessTools.Field(ConfigType, "Dimensions");
                dimensions = dimensionsField?.GetValue(null) as Dictionary<string, Vector3>;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: IH dimension push failed: {ex.Message}");
                yield break;
            }
            if (dimensions == null) yield break;

            var entries = new List<KeyValuePair<string, GameObject>>(eligibleByName);
            var frame = System.Diagnostics.Stopwatch.StartNew();
            int pushed = 0;
            foreach (var entry in entries)
            {
                if (frame.ElapsedMilliseconds >= frameBudgetMs)
                {
                    yield return null;
                    frame.Restart();
                }
                var prefab = entry.Value;
                if (prefab == null) continue;
                try
                {
                    string key = entry.Key.ToLowerInvariant();
                    if (dimensions.ContainsKey(key)) continue;
                    if (!TryComputePrefabBounds(prefab, out var size)) continue;
                    dimensions[key] = size;
                    pushed++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: IH dimension push failed on '{entry.Key}': {ex.Message}");
                }
            }

            if (pushed > 0)
                Debug.Log($"{VaPieces.PluginName}: pushed {pushed} zoop dimension(s) into Infinity Hammer for injected pieces.");
        }

        public static void PushDimensionsForInjected(IDictionary<string, GameObject> eligibleByName)
        {
            if (!InfinityHammerPresent || eligibleByName == null || eligibleByName.Count == 0) return;

            try
            {
                var dimensionsField = AccessTools.Field(ConfigType, "Dimensions");
                if (!(dimensionsField?.GetValue(null) is Dictionary<string, Vector3> dimensions)) return;

                int pushed = 0;
                foreach (var entry in eligibleByName)
                {
                    var prefab = entry.Value;
                    if (prefab == null) continue;
                    string key = entry.Key.ToLowerInvariant();
                    if (dimensions.ContainsKey(key)) continue;
                    if (!TryComputePrefabBounds(prefab, out var size)) continue;
                    dimensions[key] = size;
                    pushed++;
                }

                if (pushed > 0)
                    Debug.Log($"{VaPieces.PluginName}: pushed {pushed} zoop dimension(s) into Infinity Hammer for injected pieces.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: IH dimension push failed: {ex.Message}");
            }
        }

        // Composes a root-space AABB from collider LOCAL data, matching IH's
        // rotation-identity measuring convention without instantiating anything.
        private static bool TryComputePrefabBounds(GameObject prefab, out Vector3 size)
        {
            size = Vector3.zero;
            bool anyCollider = false;
            var bounds = new Bounds();

            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (!TryGetLocalBounds(collider, out var localBounds)) continue;

                var colliderTransform = collider.transform;
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2)
                        {
                            var corner = localBounds.center + Vector3.Scale(new Vector3(x, y, z), localBounds.extents);
                            var rootSpace = prefab.transform.InverseTransformPoint(colliderTransform.TransformPoint(corner));
                            if (!anyCollider) { bounds = new Bounds(rootSpace, Vector3.zero); anyCollider = true; }
                            else bounds.Encapsulate(rootSpace);
                        }
            }

            if (!anyCollider) return false;
            size = bounds.size;
            return size.x > MinMeasurableSize || size.y > MinMeasurableSize || size.z > MinMeasurableSize;
        }

        private static bool TryGetLocalBounds(Collider collider, out Bounds bounds)
        {
            switch (collider)
            {
                case BoxCollider box:
                    bounds = new Bounds(box.center, box.size);
                    return true;
                case SphereCollider sphere:
                    bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                    return true;
                case CapsuleCollider capsule:
                    var size = Vector3.one * (capsule.radius * 2f);
                    if (capsule.direction == 0) size.x = capsule.height;
                    else if (capsule.direction == 1) size.y = capsule.height;
                    else size.z = capsule.height;
                    bounds = new Bounds(capsule.center, size);
                    return true;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    bounds = mesh.sharedMesh.bounds;
                    return true;
                default:
                    bounds = default;
                    return false;
            }
        }

        private static int CountTransforms(Transform root, int cap)
        {
            int count = 1;
            for (int i = 0; i < root.childCount && count < cap; i++)
                count += CountTransforms(root.GetChild(i), cap - count);
            return count;
        }
    }
}
