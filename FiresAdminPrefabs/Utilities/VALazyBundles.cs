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
            int stubs = 0, loaded = 0, missing = 0, nonPieceStubs = 0;
            bool lazyNonPieces = ConfigManager.Instance?.configLazyBundlesLazyNonPieces?.Value ?? false;
            foreach (var e in manifest.prefabs)
            {
                if (e == null || string.IsNullOrEmpty(e.prefab)) continue;
                if (e.hasPiece)
                {
                    list.Add(CreateStub(bundleLabel, e)); stubs++;
                    if (e.leads != null) foreach (var lead in e.leads) s_leadOwner[lead.GetStableHashCode()] = e.prefab;
                    continue;
                }
                // 1.1.390 (group F, off by default): anything asking ZNetScene for it realizes it, like a piece; a direct reference in a loaded prefab loads it anyway.
                if (lazyNonPieces && e.lazyNonPiece) { list.Add(CreateStub(bundleLabel, e)); nonPieceStubs++; continue; }
                GameObject go = null;
                try { go = bundle.LoadAsset<GameObject>(e.prefab); } catch { }
                if (go != null) { list.Add(go); loaded++; } else missing++;
            }
            VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: [LazyBundles] '{bundleLabel}': {stubs} piece(s) as stubs, {nonPieceStubs} non-piece(s) as stubs, {loaded} other prefab(s) loaded{(missing > 0 ? $", {missing} not found by name" : "")}");
            // 1.1.403 ([lead]/[fgn] 11-08, Fire's 0107w [Hitch]: 13.5 s / 5.6 s / 3.9 s frames inside ZNetScene.CreateObject): every bundle FAP loads is
            // ONE LZMA block (UnityFS headers read 11-08: vapieces 96 -> 509 MB, furniturereborn_bal 71 -> 194 MB ...), so the FIRST asset load from a
            // bundle unpacks the whole file (log: first realize from vapieces 5,565 ms, the next ones 1.5-48 ms). A bundle whose harvest loaded
            // nothing by name was never touched, so its first touch landed in a create. Prewarm: one async load per such bundle right away, on
            // Unity's loader thread, so the unpack is paid at the menu / loading screen and never inside a create.
            if (loaded == 0 && stubs + nonPieceStubs > 0) QueuePrewarm(bundleLabel, bundle, FirstStubName(manifest));
            else if (loaded > 0) s_warm.Add(bundleLabel);   // already touched by the by-name loads above
            return list.ToArray();
        }

        // ── 1.1.403 prewarm ──
        private static readonly HashSet<string> s_warm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<string, KeyValuePair<AssetBundle, string>>> s_prewarmQueue = new List<KeyValuePair<string, KeyValuePair<AssetBundle, string>>>();
        private static bool s_prewarming;
        private static int s_prewarmed; private static double s_prewarmSec;

        private static readonly Dictionary<string, long> s_bundleLen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static long BundleFileLength(string label)
        {
            if (s_bundleLen.TryGetValue(label, out long len)) return len;
            len = 0;
            try { var p = VAExternalBundleLoader.ResolveBundleFile(label); if (System.IO.File.Exists(p)) len = new System.IO.FileInfo(p).Length; } catch { }
            s_bundleLen[label] = len;
            return len;
        }

        private static string FirstStubName(VALazyBundleManifest.Manifest manifest)
        {
            foreach (var e in manifest.prefabs) if (e != null && !string.IsNullOrEmpty(e.prefab)) return e.prefab;
            return null;
        }

        private static void QueuePrewarm(string label, AssetBundle bundle, string name)
        {
            if (bundle == null || string.IsNullOrEmpty(name) || s_warm.Contains(label)) return;
            if (!(ConfigManager.Instance?.configLazyBundlesPrewarm?.Value ?? false)) return;
            s_prewarmQueue.Add(new KeyValuePair<string, KeyValuePair<AssetBundle, string>>(label, new KeyValuePair<AssetBundle, string>(bundle, name)));
            if (!s_prewarming && VaPieces.Instance != null) { s_prewarming = true; VaPieces.Instance.StartCoroutine(PumpPrewarm()); }
        }

        // One bundle at a time (one unpack in flight). Only the bundle is warmed; the prefab stays a stub until something asks for it.
        private static IEnumerator PumpPrewarm()
        {
            float begun = Time.realtimeSinceStartup;
            yield return null;   // the rest of this harvest pass queues its bundles first, so "largest first" sees them all
            while (s_prewarmQueue.Count > 0)
            {
                // Never competes with a world load: no new unpack starts while the loading screen is up (one already in flight finishes).
                while (LoadingScreenUp()) yield return null;
                // Largest file first: the biggest unpacks are the worst first touches.
                int pick = 0; long best = -1;
                for (int i = 0; i < s_prewarmQueue.Count; i++)
                {
                    long len = BundleFileLength(s_prewarmQueue[i].Key);
                    if (len > best) { best = len; pick = i; }
                }
                var next = s_prewarmQueue[pick]; s_prewarmQueue.RemoveAt(pick);
                string label = next.Key; var bundle = next.Value.Key; string name = next.Value.Value;
                if (bundle == null || s_warm.Contains(label)) continue;
                float t0 = Time.realtimeSinceStartup;
                AssetBundleRequest req = null;
                try { req = bundle.LoadAssetAsync<GameObject>(name); } catch (Exception ex) { VaPieces.Log?.LogDebug($"[LazyBundles] prewarm of '{label}' threw {ex.GetType().Name}: {ex.Message}"); }
                if (req != null) yield return req;
                s_warm.Add(label);
                s_prewarmed++; s_prewarmSec += Time.realtimeSinceStartup - t0;
                VaPieces.Log?.LogDebug($"[LazyBundles] prewarmed '{label}' (its LZMA block unpacked off the main thread) in {Time.realtimeSinceStartup - t0:0.00} s");
            }
            float wall = Time.realtimeSinceStartup - begun;
            s_prewarming = false;
            VaPieces.Log?.LogInfo($"[LazyBundles] {s_prewarmed} lazy bundle(s) prewarmed before use (unpacking {s_prewarmSec:0.0} s on Unity's loader thread, {wall:0.0} s from first to last, largest first, paused while a loading screen was up), so a first piece from them no longer unpacks its bundle inside a create (1.1.403)");
        }

        // 1.1.378: hash of a prefab with no container path -> the stubbed piece that references it. A dedi loads the whole bundle and
        // finds these in memory; here the referrer has to be realized first. Runs on the first ZDO that asks, once per hash.
        private static readonly Dictionary<int, string> s_leadOwner = new Dictionary<int, string>();

        internal static bool TryRealizeLead(ZNetScene scene, int hash)
        {
            if (!s_leadOwner.TryGetValue(hash, out var owner)) return false;
            s_leadOwner.Remove(hash);
            string bundle = s_stubsByName.TryGetValue(owner, out var stub) && stub != null ? stub.Bundle : null;
            bool realized = Realize(owner) != null;
            int registered = realized && bundle != null ? VAExternalBundleLoader.RegisterLoadedDependencies(scene, bundle) : 0;
            bool found = scene.m_namedPrefabs.ContainsKey(hash);
            VaPieces.Log?.LogDebug($"[LazyBundles] hash {hash}: via '{owner}': realized={realized}, {registered} dependency prefab(s) registered, found={found}");
            return found;
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
            if (e.hasPiece)
            {
                var piece = go.AddComponent<Piece>();
                piece.m_name = e.name;
                piece.m_description = e.description;
                piece.m_category = (Piece.PieceCategory)e.category;
                piece.m_resources = Array.Empty<Piece.Requirement>();
            }
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
            // 1.1.318 ([classes-ui] load/save audit M8): a throw here skipped the later ZNetScene.Awake postfixes
            try { ZNetScene_Awake_PostfixBody(__instance); }
            catch (Exception e) { Debug.LogError($"[FAP] lazy bundles: ZNetScene.Awake requirements pass failed: {e.Message}"); }
        }

        private static void ZNetScene_Awake_PostfixBody(ZNetScene __instance)
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
                if (piece == null) continue;   // 1.1.390: a non-piece stub has no requirements
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
            // 1.1.403: an on-demand load that stalls a frame logs (Debug, Fire 11-08: players see warnings) its ms and why (bundle not yet warm = its unpack was paid here).
            if (sync && (t.Elapsed.TotalMilliseconds >= 250.0 || !s_warm.Contains(stub.Bundle)))
                VaPieces.Log?.LogDebug($"[LazyBundles] on-demand load of '{name}' from '{stub.Bundle}' took {t.Elapsed.TotalMilliseconds:0} ms in one frame " +
                    (s_warm.Contains(stub.Bundle) ? "(bundle already warm: the prefab itself is heavy)" : "(bundle not prewarmed yet: its LZMA unpack landed here)"));
            s_warm.Add(stub.Bundle);   // any completed load has unpacked the bundle: warn once per bundle, and a queued prewarm of it is skipped
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
            if (stub.Entry.hasPiece) VAVanillaPieceManager.ReplaceRealizedPiece(stubGo, real);
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
        // 1.1.369 (Fire 11-02 "get to the bottom of this performance"; live log: Player.Update 112-149 ms once a second near dense builds): the look-ahead read every
        // object in 49 zones in one frame, every second. Now it runs when the player's zone changes, after a load finishes and as a 10 s catch-up for objects streaming
        // in, one zone per frame. What it loads and when it stops are unchanged.
        private static readonly List<ZDO> s_scan = new List<ZDO>();
        private static readonly List<Vector2s> s_scanZones = new List<Vector2s>();
        private static int s_scanIndex = -1;
        private static Vector2s s_lastZone;
        private static bool s_haveZone, s_rescan;
        private static float s_nextCatchUp, s_nextTimingLine;
        // 1.1.381 (lead 11-04, r71: 108 of 110 realizes were on demand, RavensRestGuildHall 400 ms): a scan queues every stub it finds, nearest zones first, and one pump loads them all.
        private static readonly List<KeyValuePair<string, AssetBundle>> s_queue = new List<KeyValuePair<string, AssetBundle>>();
        private static readonly HashSet<string> s_queued = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_pumping;
        private static long s_scanTicks; private static int s_scanObjects, s_scanFrames;

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Player_Update(Player __instance)
        {
            if (s_stubsByName.Count == 0 || __instance != Player.m_localPlayer || ZDOMan.instance == null) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s_scanIndex < 0)
            {
                var zone = ZoneSystem.GetZone(__instance.transform.position);
                bool moved = !s_haveZone || zone.x != s_lastZone.x || zone.y != s_lastZone.y;
                if (!moved && !s_rescan && Time.unscaledTime < s_nextCatchUp) return;
                s_haveZone = true; s_lastZone = zone; s_rescan = false;
                s_nextCatchUp = Time.unscaledTime + 10f;
                int zones = ConfigManager.Instance?.configLazyBundlesLookAheadZones?.Value ?? 3;
                s_scanZones.Clear();
                for (int y = -zones; y <= zones; y++)
                    for (int x = -zones; x <= zones; x++)
                        s_scanZones.Add(new Vector2s(zone.x + x, zone.y + y));
                s_scanZones.Sort((a, b) => ((a.x - zone.x) * (a.x - zone.x) + (a.y - zone.y) * (a.y - zone.y)).CompareTo((b.x - zone.x) * (b.x - zone.x) + (b.y - zone.y) * (b.y - zone.y)));
                s_scanIndex = 0;
                s_scanTicks = 0; s_scanObjects = 0; s_scanFrames = 0;
            }
            s_scan.Clear();
            ZDOMan.instance.FindSectorObjects(s_scanZones[s_scanIndex], new SimulationDistance(0, 0), s_scan);
            s_scanObjects += s_scan.Count;
            foreach (var zdo in s_scan)
            {
                if (zdo == null || !s_stubsByHash.TryGetValue(zdo.GetPrefab(), out var stub) || stub == null) continue;
                string name = stub.Entry.prefab;
                if (s_realized.ContainsKey(name)) { s_lastUse[name] = Time.unscaledTime; continue; }   // still near: in use
                if (s_queued.Contains(name) || s_loading.Contains(name)) continue;
                var bundle = VAExternalBundleLoader.GetOpenBundle(stub.Bundle);
                if (bundle == null) continue;
                s_queued.Add(name); s_queue.Add(new KeyValuePair<string, AssetBundle>(name, bundle));
            }
            s_scanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; s_scanFrames++;
            if (++s_scanIndex < s_scanZones.Count) return;
            s_scanIndex = -1;
            if (Time.unscaledTime >= s_nextTimingLine)
            {
                s_nextTimingLine = Time.unscaledTime + 60f;
                VaPieces.Log?.LogDebug($"[LazyBundles] look-ahead: {s_scanObjects} object(s) in {s_scanZones.Count} zone(s) over {s_scanFrames} frame(s), {s_scanTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms total");
            }
            if (s_queue.Count > 0 && !s_pumping && VaPieces.Instance != null)
            {
                s_pumping = true;
                VaPieces.Instance.StartCoroutine(PumpLoadAhead());
            }
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
            if (stub.Entry.hasPiece) VAVanillaPieceManager.RestoreStubPiece(real, stubGo);
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

        private static bool LoadingScreenUp() => Hud.instance != null && Hud.instance.m_loadingScreen != null && Hud.instance.m_loadingScreen.alpha > 0f;

        // One after another, no rescan between. Under the loading screen nothing is seen, so the load is direct (Fire 11-04: heavy is fine if hidden).
        private static IEnumerator PumpLoadAhead()
        {
            int async = 0, hidden = 0; float begun = Time.realtimeSinceStartup;
            while (s_queue.Count > 0)
            {
                var next = s_queue[0]; s_queue.RemoveAt(0); s_queued.Remove(next.Key);
                if (s_realized.ContainsKey(next.Key)) continue;
                if (LoadingScreenUp())
                {
                    try { Realize(next.Key, null, sync: false); } catch (Exception ex) { VaPieces.Log?.LogWarning($"[LazyBundles] '{next.Key}' threw: {ex.Message}"); }
                    hidden++;
                }
                else
                {
                    s_loading.Add(next.Key);
                    yield return LoadAhead(next.Value, next.Key);
                    async++;
                }
                yield return null;
            }
            s_pumping = false;
            s_rescan = true;   // what streamed in during the pump
            VaPieces.Log?.LogDebug($"[LazyBundles] look-ahead pump: {async} async, {hidden} under the loading screen in {Time.realtimeSinceStartup - begun:0.0} s; {Summary()}");
        }

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
