using System;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Patches
{
    /// <summary>
    /// 1.1.248 diagnostic (the lead 10-14, R1 02:43: Unity's "UnloadUnusedAssets incorrect caused some assets to load. This can
    /// easily cause deadlocks or crashes", 1 run in ~12). Unity runs its own UnloadUnusedAssets natively after a single-mode scene
    /// load, so there is no managed caller to hook; the error means something LOADED an asset during that sweep. This probe logs
    /// every managed asset load from the moment a scene load starts until 3 frames after ZNetScene.Awake (cap 60 s), with the caller,
    /// so the next hit names the loader. Outside that window every patch returns at its first line. Debug level only.
    /// </summary>
    internal static class AssetLoadWindowProbe
    {
        private static bool s_armed;
        private static float s_armedAt;
        private static int s_disarmFrame = -1;
        private static int s_logged;
        private const int MaxLines = 400;

        private static bool Active
        {
            get
            {
                if (!s_armed) return false;
                if ((s_disarmFrame >= 0 && Time.frameCount > s_disarmFrame) || Time.realtimeSinceStartup - s_armedAt > 60f)
                {
                    s_armed = false;
                    VaPieces.Log?.LogDebug($"[AssetLoadWindow] closed at frame {Time.frameCount} after {s_logged} logged load(s)");
                    return false;
                }
                return true;
            }
        }

        private static void Arm(string why)
        {
            s_armed = true; s_armedAt = Time.realtimeSinceStartup; s_disarmFrame = -1; s_logged = 0;
            VaPieces.Log?.LogDebug($"[AssetLoadWindow] armed at frame {Time.frameCount}: {why}");
        }

        private static void Note(string api, string what)
        {
            if (!Active || s_logged >= MaxLines) return;
            s_logged++;
            string caller;
            try
            {
                var frames = new StackTrace(2, false).GetFrames() ?? new StackFrame[0];
                caller = string.Join(" < ", frames.Take(14).Select(f => { var m = f.GetMethod(); return m == null ? "?" : $"{m.DeclaringType?.FullName}.{m.Name}"; }));
            }
            catch (Exception ex) { caller = "stack failed: " + ex.Message; }
            VaPieces.Log?.LogDebug($"[AssetLoadWindow] {api} '{what}' frame {Time.frameCount} t={(Time.realtimeSinceStartup - s_armedAt) * 1000f:0} ms; caller: {caller}");
        }

        // ── Arming: the scene load starts ─────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene), new[] { typeof(string) })]
        private static class LoadSceneName { private static void Prefix(string sceneName) { Arm("SceneManager.LoadScene('" + sceneName + "')"); } }

        [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), new[] { typeof(string) })]
        private static class LoadSceneAsyncName { private static void Prefix(string sceneName) { Arm("SceneManager.LoadSceneAsync('" + sceneName + "')"); } }

        // Belt and braces: arm on ZNetScene.Awake too (if the load went through another overload), and close 3 frames later.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetSceneAwake
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() { if (!s_armed) Arm("ZNetScene.Awake"); }
            // 1.1.249: R2 showed the error 2 frames after Awake; 30 frames covers the scene sweep with room.
            private static void Postfix() { s_disarmFrame = Time.frameCount + 30; VaPieces.Log?.LogDebug($"[AssetLoadWindow] ZNetScene.Awake at frame {Time.frameCount}; closing after frame {s_disarmFrame}"); }
        }

        // ── The loads ─────────────────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Type) })]
        private static class ResLoad { private static void Prefix(string path) { Note("Resources.Load", path); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), new[] { typeof(string), typeof(Type) })]
        private static class BundleLoadAsset { private static void Prefix(AssetBundle __instance, string name) { Note("AssetBundle.LoadAsset", (__instance != null ? __instance.name : "?") + "/" + name); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAssetAsync), new[] { typeof(string), typeof(Type) })]
        private static class BundleLoadAssetAsync { private static void Prefix(AssetBundle __instance, string name) { Note("AssetBundle.LoadAssetAsync", (__instance != null ? __instance.name : "?") + "/" + name); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAllAssets), new[] { typeof(Type) })]
        private static class BundleLoadAll { private static void Prefix(AssetBundle __instance) { Note("AssetBundle.LoadAllAssets", __instance != null ? __instance.name : "?"); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadFromFile), new[] { typeof(string) })]
        private static class BundleFromFile { private static void Prefix(string path) { Note("AssetBundle.LoadFromFile", path); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadFromFileAsync), new[] { typeof(string) })]
        private static class BundleFromFileAsync { private static void Prefix(string path) { Note("AssetBundle.LoadFromFileAsync", path); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadFromMemory), new[] { typeof(byte[]) })]
        private static class BundleFromMemory { private static void Prefix(byte[] binary) { Note("AssetBundle.LoadFromMemory", (binary?.Length ?? 0) + " B"); } }

        // 1.1.249 (R2: the error fell between a TextCore TextSettings being CREATED and its OnEnable's Resources.Load): name the creator.
        [HarmonyPatch(typeof(ScriptableObject), nameof(ScriptableObject.CreateInstance), new[] { typeof(Type) })]
        private static class SoCreate { private static void Prefix(Type type) { Note("ScriptableObject.CreateInstance", type?.FullName ?? "?"); } }

        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadFromStream), new[] { typeof(System.IO.Stream) })]
        private static class BundleFromStream { private static void Prefix() { Note("AssetBundle.LoadFromStream", "stream"); } }
    }
}
