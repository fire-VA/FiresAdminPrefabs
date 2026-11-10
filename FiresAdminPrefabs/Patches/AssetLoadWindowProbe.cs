using System;
using System.Collections.Generic;
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
        private const int MaxLines = 1000;   // 1.1.308: was 400

        private static bool Active
        {
            get
            {
                if (!s_armed) return false;
                if ((s_disarmFrame >= 0 && Time.frameCount > s_disarmFrame) || Time.realtimeSinceStartup - s_armedAt > 60f)
                {
                    s_armed = false;
                    VaPieces.Log?.LogDebug($"[AssetLoadWindow] closed at frame {Time.frameCount} after {s_logged} distinct load(s) (cap {MaxLines}) + {s_soLogged} distinct CreateInstance(s)");
                    Summarize();
                    return false;
                }
                return true;
            }
        }

        private static void Arm(string why)
        {
            s_armed = true; s_armedAt = Time.realtimeSinceStartup; s_disarmFrame = -1; s_logged = 0; s_soLogged = 0; s_seen.Clear(); s_lastFrame.Clear();
            VaPieces.Log?.LogDebug($"[AssetLoadWindow] armed at frame {Time.frameCount}: {why}");
        }

        // 1.1.308 ([lead] 10-22: round_20261022-062337 spent all 400 lines in frame 251 on Core's CreateInstance('Recipe') and was
        // silent when the error came at :3823): the same api + asset + caller logs once (repeats counted, summed when the window
        // closes); CreateInstance lines still log (R2: the error followed a TextSettings CreateInstance) but do not use the load cap;
        // the cap is 1000 loads; every line carries its frame.
        private static readonly Dictionary<string, int> s_seen = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> s_lastFrame = new Dictionary<string, int>();
        private static int s_soLogged;
        private const int MaxSoLines = 1000;

        private static void Note(string api, string what)
        {
            if (!Active) return;
            bool so = api == "ScriptableObject.CreateInstance";
            if (so ? s_soLogged >= MaxSoLines : s_logged >= MaxLines) return;
            string caller;
            try
            {
                var frames = new StackTrace(2, false).GetFrames() ?? new StackFrame[0];
                caller = string.Join(" < ", frames.Take(14).Select(f => { var m = f.GetMethod(); return m == null ? "?" : $"{m.DeclaringType?.FullName}.{m.Name}"; }));
            }
            catch (Exception ex) { caller = "stack failed: " + ex.Message; }
            string key = api + "|" + what + "|" + caller;
            s_lastFrame[key] = Time.frameCount;
            if (s_seen.TryGetValue(key, out int n)) { s_seen[key] = n + 1; return; }   // a repeat: counted, summed at close
            s_seen[key] = 1;
            if (so) s_soLogged++; else s_logged++;
            VaPieces.Log?.LogDebug($"[AssetLoadWindow] {api} '{what}' frame {Time.frameCount} t={(Time.realtimeSinceStartup - s_armedAt) * 1000f:0} ms; caller: {caller}");
        }

        private static void Summarize()
        {
            int lines = 0;
            foreach (var kv in s_seen)
            {
                if (kv.Value < 2) continue;
                int bar = kv.Key.IndexOf('|'), bar2 = kv.Key.IndexOf('|', bar + 1);
                string head = bar2 > 0 ? kv.Key.Substring(0, bar2) : kv.Key;
                VaPieces.Log?.LogDebug($"[AssetLoadWindow] repeated {kv.Value - 1} more time(s) (last frame {s_lastFrame[kv.Key]}): {head.Replace("|", " '")}'");
                if (++lines >= 200) break;
            }
            s_seen.Clear(); s_lastFrame.Clear();
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
