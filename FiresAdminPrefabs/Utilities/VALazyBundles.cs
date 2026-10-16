using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// Stage 2 of FAP's lazy third-party bundles (Tools\FAP_LAZY_BUNDLES_DESIGN.md; Fire 10-13, option A: lazy with a look-ahead).
    /// For an Assets bundle with a current VALazyBundleManifest, its PIECE prefabs start as stubs: an inactive GameObject
    /// with the prefab's name, a persistent ZNetView and a Piece (display name, description, category; requirements and
    /// crafting station resolved once the items exist). Stubs sit in ZNetScene under the real hash, so the Hammer lists
    /// them and saved pieces resolve. Items, creatures and every other non-piece prefab still load at startup, one by name.
    /// The real prefab loads from the still-open bundle and replaces its stub (ZNetScene, the Hammer table) when:
    ///   - anything asks ZNetScene for it (GetPrefab: a piece spawning in a loaded zone, a blueprint, the baker): synchronous;
    ///   - it is taken out on the Hammer (the placement ghost): synchronous;
    ///   - one of its pieces is within LookAheadZones of the player (once a second): asynchronous, before it is needed.
    /// A stub is never instantiated. Client only; LazyBundles > PreloadAll restores loading everything at startup.
    /// </summary>
    [HarmonyPatch]
    public static class VALazyBundles
    {
        /// <summary>Marks a stub and says where its real prefab lives.</summary>
        public sealed class Stub : MonoBehaviour
        {
            public string Bundle;
            public VALazyBundleManifest.Entry Entry;
        }

        private static GameObject s_holder;
        private static readonly Dictionary<string, Stub> s_stubsByName = new Dictionary<string, Stub>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Stub> s_stubsByHash = new Dictionary<int, Stub>();
        private static readonly HashSet<int> s_stubInstanceIds = new HashSet<int>();
        private static readonly Dictionary<string, GameObject> s_realized = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_loading = new HashSet<string>(StringComparer.Ordinal);
        private static int s_syncLoads, s_asyncLoads;
        private static double s_syncMs, s_worstSyncMs;
        private static string s_worstSyncName;
        // 1.1.230 stage 3: last use per realized prefab (GetPrefab, Hammer, a nearby ZDO); idle ones go back to their stub.
        private static readonly Dictionary<string, float> s_lastUse = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> s_realizedById = new Dictionary<int, string>();
        private static int s_unloaded, s_callerLogs;
        private static float s_nextSweep;

        public static int StubCount => s_stubsByName.Count;
        public static int RealizedCount => s_realized.Count;

        /// <summary>True while the stub of this prefab has not been replaced by its real prefab.</summary>
        public static bool IsStub(GameObject go) => go != null && s_stubInstanceIds.Contains(go.GetInstanceID());

        /// <summary>1.1.231: a realized prefab that went back to its stub; its assets may be released, so nothing may render or
        /// instantiate it. True for a destroyed object too (matched by instance id).</summary>
        public static bool IsReleased(GameObject go) => !ReferenceEquals(go, null) && s_releasedIds.Contains(go.GetInstanceID());
        private static readonly HashSet<int> s_releasedIds = new HashSet<int>();

        /// <summary>Whether this bundle should load lazily: client, not PreloadAll, and a current manifest with pieces.</summary>
        public static bool ShouldLoadLazy(string bundleLabel, string bundlePath, out VALazyBundleManifest.Manifest manifest)
        {
            manifest = null;
            if (VaPieces.IsDedicatedServer) return false;
            var cfg = ConfigManager.Instance;
            if (cfg == null || cfg.configLazyBundlesPreloadAll == null || cfg.configLazyBundlesPreloadAll.Value) return false;
            if (!VALazyBundleManifest.IsCurrent(bundleLabel, bundlePath, out manifest)) return false;
            return manifest.prefabs.Any(e => e.hasPiece);
        }

        /// <summary>The prefabs this bundle contributes now: non-pieces loaded by name, pieces as stubs.</summary>
        public static GameObject[] Harvest(AssetBundle bundle, string bundleLabel, VALazyBundleManifest.Manifest manifest)
        {
            var list = new List<GameObject>(manifest.prefabs.Count);
            int stubs = 0, loaded = 0, missing = 0;
            foreach (var e in manifest.prefabs)
            {
                if (e == null || string.IsNullOrEmpty(e.prefab)) continue;
                if (e.hasPiece) { list.Add(CreateStub(bundleLabel, e)); stubs++; continue; }
                GameObject go = null;
                try { go = bundle.LoadAsset<GameObject>(e.prefab); } catch { }
                if (go != null) { list.Add(go); loaded++; } else missing++;
            }
            Debug.Log($"{VaPieces.PluginName}: [LazyBundles] '{bundleLabel}': {stubs} piece(s) as stubs, {loaded} other prefab(s) loaded{(missing > 0 ? $", {missing} not found by name" : "")}");
            return list.ToArray();
        }

        private static GameObject CreateStub(string bundleLabel, VALazyBundleManifest.Entry e)
        {
            if (s_holder == null)
            {
                s_holder = new GameObject("FAP_LazyStubs");
                s_holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(s_holder);
            }
            var go = new GameObject(e.prefab);
            go.transform.SetParent(s_holder.transform, false);
            var nview = go.AddComponent<ZNetView>();
            nview.m_persistent = true;
            var piece = go.AddComponent<Piece>();
            piece.m_name = e.name;
            piece.m_description = e.description;
            piece.m_category = (Piece.PieceCategory)e.category;
            piece.m_resources = Array.Empty<Piece.Requirement>();
            var stub = go.AddComponent<Stub>();
            stub.Bundle = bundleLabel;
            stub.Entry = e;
            s_stubsByName[e.prefab] = stub;
            s_stubsByHash[e.prefab.GetStableHashCode()] = stub;
            s_stubInstanceIds.Add(go.GetInstanceID());
            return go;
        }

        // Requirements and crafting station once ZNetScene holds the items and stations (after the item-ref repair).
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
            if (__instance == null || s_stubsByName.Count == 0) return;
            var stations = new Dictionary<string, CraftingStation>(StringComparer.Ordinal);
            foreach (var p in __instance.m_prefabs)
            {
                var cs = p != null ? p.GetComponent<CraftingStation>() : null;
                if (cs != null && !string.IsNullOrEmpty(cs.m_name) && !stations.ContainsKey(cs.m_name)) stations[cs.m_name] = cs;
            }
            int resolved = 0;
            foreach (var stub in s_stubsByName.Values)
            {
                if (stub == null) continue;
                var piece = stub.GetComponent<Piece>();
                var reqs = new List<Piece.Requirement>();
                foreach (var r in stub.Entry.requirements)
                {
                    var item = __instance.GetPrefab(r.item)?.GetComponent<ItemDrop>();
                    if (item != null) reqs.Add(new Piece.Requirement { m_resItem = item, m_amount = r.amount, m_amountPerLevel = r.perLevel, m_recover = r.recover });
                }
                piece.m_resources = reqs.ToArray();
                if (!string.IsNullOrEmpty(stub.Entry.station) && stations.TryGetValue(stub.Entry.station, out var st)) piece.m_craftingStation = st;
                resolved++;
            }
            Debug.Log($"{VaPieces.PluginName}: [LazyBundles] {resolved} stub(s) given their requirements and station; {s_realized.Count} realized so far");
        }

        /// <summary>The real prefab for a stub, loaded now if it isn't yet (synchronous). Null when it can't be loaded.</summary>
        public static GameObject Realize(string name, GameObject loaded = null, bool sync = true)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (s_realized.TryGetValue(name, out var done) && done != null) return done;
            if (!s_stubsByName.TryGetValue(name, out var stub) || stub == null) return null;
            var t = System.Diagnostics.Stopwatch.StartNew();
            FapNativeTrace.Step("lazy realize begin", $"'{name}' from '{stub.Bundle}' {(sync ? "on demand" : "look-ahead")}");
            var real = loaded;
            if (real == null)
            {
                var bundle = VAExternalBundleLoader.GetOpenBundle(stub.Bundle);
                if (bundle == null) { Debug.LogWarning($"{VaPieces.PluginName}: [LazyBundles] '{name}': bundle '{stub.Bundle}' is not open; the stub stays"); return null; }
                try { real = bundle.LoadAsset<GameObject>(name); } catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [LazyBundles] '{name}' load threw: {ex.Message}"); }
            }
            if (real == null) { Debug.LogWarning($"{VaPieces.PluginName}: [LazyBundles] '{name}' not found in '{stub.Bundle}'; the stub stays"); return null; }
            if (!VAExternalBundleLoader.PrepareLatePrefab(real, stub.Bundle)) return null;
            FapNativeTrace.Step("lazy swap-in", $"'{name}'");
            SwapIn(stub, real);
            FapNativeTrace.Step("lazy realize end", $"'{name}'");
            s_realized[name] = real;
            s_realizedById[real.GetInstanceID()] = name;
            s_releasedIds.Remove(real.GetInstanceID());
            s_lastUse[name] = Time.unscaledTime;
            t.Stop();
            // Who asks for stubs on demand (R-1013AM: 21 realized at the join, caller unknown): the first 30, Debug.
            if (sync && s_callerLogs < 30)
            {
                s_callerLogs++;
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
                VaPieces.Log?.LogDebug($"[LazyBundles] '{name}' asked for by " + string.Join(" < ", frames.Take(6)
                    .Select(f => f.GetMethod()).Where(m => m != null).Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name).ToArray()));
            }
            if (sync)
            {
                s_syncLoads++; s_syncMs += t.Elapsed.TotalMilliseconds;
                if (t.Elapsed.TotalMilliseconds > s_worstSyncMs) { s_worstSyncMs = t.Elapsed.TotalMilliseconds; s_worstSyncName = name; }
            }
            else s_asyncLoads++;
            VaPieces.Log?.LogDebug($"[LazyBundles] realized '{name}' from '{stub.Bundle}' ({(sync ? "on demand" : "look-ahead")}, {t.Elapsed.TotalMilliseconds:0.0} ms)");
            return real;
        }

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> NamedPrefabs =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs");

        private static void SwapIn(Stub stub, GameObject real)
        {
            var scene = ZNetScene.instance;
            var stubGo = stub.gameObject;
            if (scene != null)
            {
                int i = scene.m_prefabs.IndexOf(stubGo);
                if (i >= 0) scene.m_prefabs[i] = real; else scene.m_prefabs.Add(real);
                var named = NamedPrefabs(scene);
                if (named != null) named[real.name.GetStableHashCode()] = real;
            }
            // The Hammer: same slot, FAP's category from the stub, FAP's own hammer preparation on the real prefab.
            VAVanillaPieceManager.ReplaceRealizedPiece(stubGo, real);
        }

        // Every way a prefab is asked for by name or hash (ZNetScene.CreateObject, blueprints, the baker, other mods): a stub
        // answer becomes the real prefab, so a stub is never instantiated.
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.GetPrefab), new[] { typeof(int) }), HarmonyPostfix]
        private static void GetPrefab_Hash(ref GameObject __result) => OnGetPrefab(ref __result);

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.GetPrefab), new[] { typeof(string) }), HarmonyPostfix]
        private static void GetPrefab_Name(ref GameObject __result) => OnGetPrefab(ref __result);

        private static void OnGetPrefab(ref GameObject result)
        {
            if (result == null || s_stubsByName.Count == 0) return;
            int id = result.GetInstanceID();
            if (s_stubInstanceIds.Contains(id)) result = Realize(result.name) ?? result;
            else if (s_realizedById.TryGetValue(id, out var name)) s_lastUse[name] = Time.unscaledTime;
        }

        // Taking a piece out on the Hammer: the table still holds the stub object; the real one goes in before the ghost.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPrefix]
        private static void Player_SetupPlacementGhost(Player __instance)
        {
            if (s_stubsByName.Count == 0 || __instance != Player.m_localPlayer) return;
            var piece = __instance.GetSelectedPiece();
            if (piece != null && s_stubInstanceIds.Contains(piece.gameObject.GetInstanceID())) Realize(piece.gameObject.name);
        }

        // ── look-ahead: pieces within LookAheadZones of the player are loaded in the background before they are needed ──
        private static float s_nextScan;
        private static readonly List<ZDO> s_scan = new List<ZDO>();

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Player_Update(Player __instance)
        {
            if (s_stubsByName.Count == 0 || __instance != Player.m_localPlayer || Time.unscaledTime < s_nextScan) return;
            s_nextScan = Time.unscaledTime + 1f;
            if (ZDOMan.instance == null) return;
            if (Time.unscaledTime >= s_nextSweep)
            {
                s_nextSweep = Time.unscaledTime + 60f;
                int minutes = ConfigManager.Instance?.configLazyBundlesUnloadMinutes?.Value ?? 10;
                if (minutes > 0) UnloadIdle(minutes);
            }
            if (s_loading.Count > 0) return;
            int zones = ConfigManager.Instance?.configLazyBundlesLookAheadZones?.Value ?? 3;
            s_scan.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(__instance.transform.position), new SimulationDistance(zones, 0), s_scan);
            string next = null; AssetBundle nextBundle = null;
            foreach (var zdo in s_scan)
            {
                if (zdo == null || !s_stubsByHash.TryGetValue(zdo.GetPrefab(), out var stub) || stub == null) continue;
                string name = stub.Entry.prefab;
                if (s_realized.ContainsKey(name)) { s_lastUse[name] = Time.unscaledTime; continue; }   // still near: in use
                if (next != null || s_loading.Contains(name)) continue;
                var bundle = VAExternalBundleLoader.GetOpenBundle(stub.Bundle);
                if (bundle == null) continue;
                next = name; nextBundle = bundle;   // one at a time: the next scan picks the next
            }
            if (next != null && VaPieces.Instance != null)
            {
                s_loading.Add(next);
                VaPieces.Instance.StartCoroutine(LoadAhead(nextBundle, next));
            }
        }

        /// <summary>1.1.230: realized prefabs not used for this many minutes go back to their stub and their assets are
        /// released (Resources.UnloadUnusedAssets, once per sweep). The Hammer's selected piece is kept. Returns how many.</summary>
        public static int UnloadIdle(float minutes)
        {
            if (s_realized.Count == 0) return 0;
            // 1.1.234 (R-1013 Fire hand test, 18:27 ntdll 0xc0000005: the 60 s sweep released 21 pieces the moment the build menu opened, and
            // the next tab click crashed): never while the build menu is showing or the player is in place mode.
            if (BuildUiBusy(out string busy)) { VaPieces.Log?.LogDebug($"[LazyBundles] idle unload deferred: {busy}"); return 0; }
            float cutoff = Time.unscaledTime - minutes * 60f;
            string selected = null;
            try { selected = Player.m_localPlayer?.GetSelectedPiece()?.gameObject.name; } catch { }
            // 1.1.232 (R-1013AP: the quit-time crash rendered a piece the test had just released): a piece still waiting for its
            // icon render stays loaded until the render is done.
            var idle = s_realized.Where(kv => kv.Key != selected && kv.Value != null && kv.Value.GetComponent<Piece>()?.m_icon != null
                && (!s_lastUse.TryGetValue(kv.Key, out var t) || t < cutoff)).Select(kv => kv.Key).ToList();
            int n = 0;
            foreach (var name in idle) if (SwapOut(name)) n++;
            // The player's available-piece lists hold the real Piece objects; rebuild them so nothing (a tab click, the HUD) reads a released one.
            if (n > 0) RefreshAvailablePieces();
            if (n == 0) return 0;
            s_unloaded += n;
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            FapNativeTrace.Step("lazy UnloadUnusedAssets begin", $"{n} piece(s)");
            Resources.UnloadUnusedAssets();
            FapNativeTrace.Step("lazy UnloadUnusedAssets end");
            Debug.Log($"{VaPieces.PluginName}: [LazyBundles] {n} idle piece(s) back to stubs (unused {minutes:0} min); assets released in {t0.Elapsed.TotalMilliseconds:0} ms (call); {Summary()}");
            return n;
        }

        private static bool SwapOut(string name)
        {
            if (!s_realized.TryGetValue(name, out var real) || !s_stubsByName.TryGetValue(name, out var stub) || stub == null) return false;
            var stubGo = stub.gameObject;
            var scene = ZNetScene.instance;
            if (scene != null && real != null)
            {
                int i = scene.m_prefabs.IndexOf(real);
                if (i >= 0) scene.m_prefabs[i] = stubGo;
                var named = NamedPrefabs(scene);
                if (named != null) named[name.GetStableHashCode()] = stubGo;
            }
            VAVanillaPieceManager.RestoreStubPiece(real, stubGo);
            s_realized.Remove(name);
            s_lastUse.Remove(name);
            if (real != null) s_realizedById.Remove(real.GetInstanceID());
            if (!ReferenceEquals(real, null)) s_releasedIds.Add(real.GetInstanceID());
            return true;
        }

        // ── for fap_lazy_bundle_test (FDT reads these by reflection) ──
        /// <summary>Stub names not realized now, optionally from one bundle.</summary>
        public static List<string> StubNames(int max, string bundle = null) =>
            s_stubsByName.Where(kv => kv.Value != null && !s_realized.ContainsKey(kv.Key) && (bundle == null || string.Equals(kv.Value.Bundle, bundle, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key).Take(Math.Max(0, max)).ToList();

        private static bool BuildUiBusy(out string why)
        {
            why = null;
            try
            {
                if (Hud.IsPieceSelectionVisible()) why = "the build menu is open";
                else if (Player.m_localPlayer != null && Player.m_localPlayer.InPlaceMode()) why = "the player is in place mode";
            }
            catch (Exception ex) { why = "build state unreadable (" + ex.GetType().Name + ")"; }
            return why != null;
        }

        private static readonly System.Reflection.MethodInfo UpdateAvailablePiecesList = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        private static void RefreshAvailablePieces()
        {
            try { if (Player.m_localPlayer != null) UpdateAvailablePiecesList?.Invoke(Player.m_localPlayer, null); }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[LazyBundles] available-pieces refresh threw {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Realizes one stub now (as the Hammer would) and returns the milliseconds it took; -1 when it failed.</summary>
        public static double RealizeTimed(string name)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            return Realize(name) != null ? t.Elapsed.TotalMilliseconds : -1;
        }

        /// <summary>The bundle a stub's real prefab lives in, or null.</summary>
        public static string BundleOf(string name) => s_stubsByName.TryGetValue(name, out var s) && s != null ? s.Bundle : null;

        private static IEnumerator LoadAhead(AssetBundle bundle, string name)
        {
            AssetBundleRequest req = null;
            try { req = bundle.LoadAssetAsync<GameObject>(name); } catch { }
            if (req != null) yield return req;
            try { if (!s_realized.ContainsKey(name)) Realize(name, req?.asset as GameObject, sync: false); }
            finally { s_loading.Remove(name); }
        }

        /// <summary>One line for logs and tests.</summary>
        public static string Summary() =>
            $"[LazyBundles] stubs {s_stubsByName.Count}, realized {s_realized.Count} ({s_asyncLoads} by look-ahead, {s_syncLoads} on demand, " +
            $"{s_syncMs:0} ms total on demand, worst {s_worstSyncMs:0} ms '{s_worstSyncName ?? "-"}'), {s_unloaded} unloaded";
    }
}
