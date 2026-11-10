using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.377 (Fire 11-03, castle tower back) + 1.1.383 (any rebuilt VA prefab): a big prefab's root holds only its mid and far stages and collision. The render chunks
    // sit in their own bundle, in plugins\<mod>\FapBundles\Near\ (outside VAExternalBundleLoader's scan, which isn't recursive): opened when one is within LoadDistanceM of the
    // camera, unloaded when none is inside ReleaseDistanceM. Clients only; a dedicated server never opens it. The bundle is named after its prefab; the tower keeps its
    // old name and old config folder. No bundle file = the prefab stays on its mid stage, one warning.
    internal static class VATowerNearChunks
    {
        internal const string PrefabName = "ConsMassiveTower";
        internal const float FarDistanceM = 1500f;
        internal static int WantedCount, ShellOffCount, ReleasedCount;   // fap_near_loader_test reads these
        private const float LoadDistanceM = 500f, ReleaseDistanceM = 600f;   // Fire 11-04: 7-8 s to open while flying, so start sooner
        private const float CheckSeconds = 1f;
        private const string TowerBundle = "vapieces_tower_near";
        private static readonly Vector3 TowerOffset = new Vector3(0f, 0.02f, 0f);   // where the tower's old render object sat under the root

        private static readonly Dictionary<string, Entry> s_entries = new Dictionary<string, Entry>(System.StringComparer.Ordinal);

        // Prefabs with a near bundle, for far-range registration.
        internal static IEnumerable<string> NearPrefabs()
        {
            yield return PrefabName;
            string[] mods;
            try { mods = Directory.GetDirectories(Paths.PluginPath); } catch { yield break; }
            foreach (var mod in mods)
            {
                string dir = Path.Combine(mod, "FapBundles", "Near");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    if (name != TowerBundle && !name.Contains(".")) yield return name;
                }
            }
        }

        private static string BundlePath(string prefab)
        {
            bool tower = prefab == PrefabName;
            string inMod = VAExternalBundleLoader.ResolveBundleFile(tower ? TowerBundle : prefab, "Near");
            if (File.Exists(inMod)) return inMod;
            return tower ? Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "NearBundles", TowerBundle) : null;
        }

        // 1.1.402 ([visual] audit via [lead] 11-08: the first sighting of every prefab ran BundlePath -> ResolveBundleFile + File.Exists, disk I/O
        // inside Instantiate, a hitch per new prefab kind): the prefabs that have a near bundle are indexed ONCE (the same folders NearPrefabs and
        // ResolveBundleFile read: plugins\<mod>\FapBundles\Near\, plus the tower), by prefab hash; every other piece returns before any name or disk work.
        private static HashSet<int> s_nearHashes;

        internal static void WarmNearIndex()
        {
            if (s_nearHashes != null) return;
            var set = new HashSet<int>();
            var lower = new HashSet<string>(System.StringComparer.Ordinal);
            int n = 0;
            if (set.Add(PrefabName.GetStableHashCode())) n++;   // the tower: its bundle may also sit in config\FiresAdminPrefabs\NearBundles
            try
            {
                // every file name in the Near folders (BundlePath accepts any; a manifest's name never matches a prefab hash)
                foreach (var mod in Directory.GetDirectories(Paths.PluginPath))
                {
                    string dir = Path.Combine(mod, "FapBundles", "Near");
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir)) { string name = Path.GetFileName(f); if (name != TowerBundle) { lower.Add(name.ToLowerInvariant()); if (set.Add(name.GetStableHashCode())) n++; } }
                }
                // BundlePath's File.Exists ignores letter case on Windows: a registered prefab whose name matches a file only case-insensitively is
                // indexed by its own hash too, so behaviour is unchanged (lead 11-08).
                var scene = ZNetScene.instance;
                if (lower.Count > 0 && scene != null)
                    foreach (var list in new[] { scene.m_prefabs, scene.m_nonNetViewPrefabs })
                        if (list != null)
                            foreach (var p in list)
                                if (p != null && lower.Contains(p.name.ToLowerInvariant()) && set.Add(p.name.GetStableHashCode())) n++;
            }
            catch (System.Exception e) { VaPieces.Log?.LogWarning($"[NearChunks] near bundle scan failed ({e.Message}); only the tower is indexed"); }
            s_nearHashes = set;
            VaPieces.Log?.LogInfo($"[NearChunks] {n} prefab(s) with a near bundle indexed once at load; other pieces skip the bundle lookup (1.1.402)");
        }

        internal static void OnPieceAwake(Piece piece)
        {
            if (VaPieces.IsDedicatedServer) return;
            var nv = piece.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;   // the placement ghost has no ZDO (it never got a Root before either)
            if (s_nearHashes == null) WarmNearIndex();
            if (!s_nearHashes.Contains(nv.GetZDO().GetPrefab())) return;
            string name = piece.gameObject.name.Replace("(Clone)", "").Trim();
            if (!s_entries.TryGetValue(name, out var entry))
            {
                string path = BundlePath(name);
                s_entries[name] = entry = path != null ? new Entry(name, path, name == PrefabName ? TowerOffset : Vector3.zero) : null;
            }
            if (entry == null) return;
            var nview = piece.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;   // the placement ghost has no ZDO
            if (piece.GetComponent<Root>() == null) piece.gameObject.AddComponent<Root>().Entry = entry;
        }

        private sealed class Entry
        {
            private readonly string _name, _path;
            internal readonly Vector3 Offset;
            private readonly HashSet<Root> _wanting = new HashSet<Root>();
            private AssetBundle _bundle;
            private GameObject[] _chunks;
            private bool _running, _missingWarned;

            internal Entry(string name, string path, Vector3 offset) { _name = name; _path = path; Offset = offset; }

            internal void Want(Root root) { _wanting.Add(root); Run(); }
            internal void Unwant(Root root) { _wanting.Remove(root); Run(); }

            private void Run()
            {
                if (_running || VaPieces.Instance == null) return;
                _running = true;
                VaPieces.Instance.StartCoroutine(Drive());
            }

            // One coroutine per prefab owns its bundle, so a load and an unload can never overlap; it re-reads _wanting after every wait.
            private IEnumerator Drive()
            {
                while (true)
                {
                    _wanting.RemoveWhere(t => t == null);
                    if (_wanting.Count > 0 && _chunks == null)
                    {
                        if (!File.Exists(_path))
                        {
                            if (!_missingWarned) { _missingWarned = true; Debug.LogWarning($"{VaPieces.PluginName}: {_name} chunks not found at '{_path}'; it stays on its mid stage"); }
                            break;
                        }
                        var t = System.Diagnostics.Stopwatch.StartNew();
                        var open = AssetBundle.LoadFromFileAsync(_path);
                        yield return open;
                        _bundle = open.assetBundle;
                        if (_bundle == null) { Debug.LogWarning($"{VaPieces.PluginName}: {_name} chunks: could not open '{_path}'"); _missingWarned = true; break; }
                        var load = _bundle.LoadAllAssetsAsync<GameObject>();
                        yield return load;
                        var list = new List<GameObject>();
                        foreach (var asset in load.allAssets)
                            if (asset is GameObject go) { VAMaterialRepair.RepairPrefab(go); list.Add(go); }
                        _chunks = list.ToArray();
                        VaPieces.Log?.LogDebug($"[TowerChunks] {_name} opened: {_chunks.Length} chunk prefab(s), {t.Elapsed.TotalMilliseconds:0} ms");
                        continue;
                    }
                    if (_wanting.Count > 0)
                    {
                        bool spawning = false;
                        foreach (var root in _wanting)
                            if (root != null && root.SpawnNext(_chunks)) { spawning = true; break; }
                        if (spawning) { yield return null; continue; }   // one chunk a frame
                        break;
                    }
                    if (_bundle != null)
                    {
                        yield return null;   // instances destroyed last frame are gone before their assets are
                        if (_wanting.Count > 0) continue;
                        _bundle.Unload(true);
                        _bundle = null;
                        _chunks = null;
                        VaPieces.Log?.LogDebug($"[TowerChunks] {_name}: none near, bundle unloaded");
                    }
                    break;
                }
                _running = false;
            }
        }

        private sealed class Root : MonoBehaviour
        {
            internal Entry Entry;
            private readonly List<GameObject> _spawned = new List<GameObject>();
            private bool _near;
            private LODGroup _lod;
            private readonly List<Renderer> _shell = new List<Renderer>();

            // 1.1.378 (Fire 11-03, "derenders for the entire time we fly towards it"): a root's LOD0 may have no renderers (the chunks moved out),
            // so from 33% of screen height (1178 m at LOD bias 5) nothing drew until the chunks spawned. Without that level the mid stage stays up close.
            private void Awake()
            {
                _lod = GetComponentInChildren<LODGroup>();
                if (_lod == null) return;
                var kept = new List<LOD>();
                foreach (var level in _lod.GetLODs())
                {
                    if (level.renderers == null || level.renderers.Length == 0) continue;
                    kept.Add(level);
                    foreach (var r in level.renderers) if (r != null) _shell.Add(r);
                }
                _lod.SetLODs(kept.ToArray());
            }

            // Hidden only once every chunk is up, and back before any chunk goes, so there is no gap either way.
            private void ShowShell(bool show)
            {
                if (_lod == null) return;
                if (!show) _lod.enabled = false;
                foreach (var r in _shell) if (r != null) r.enabled = show;
                if (show) _lod.enabled = true;
                VaPieces.Log?.LogDebug($"[TowerChunks] {name} at {transform.position.ToString("F0")}: shell {(show ? "on" : "off, all chunks up")}");
            }

            private void OnEnable() => StartCoroutine(Check());

            // Per root and only while a root instance exists; nothing fires when a camera crosses a distance.
            private IEnumerator Check()
            {
                // 1.1.386: OnEnable runs inside AddComponent, before OnPieceAwake sets Entry; the first pass threw and the loader never ran.
                yield return null;
                if (Entry == null) yield break;
                var wait = new WaitForSeconds(CheckSeconds);
                while (true)
                {
                    var cam = Utils.GetMainCamera();
                    if (cam != null)
                    {
                        float d = Vector3.Distance(cam.transform.position, transform.position);
                        if (!_near && d <= LoadDistanceM) { _near = true; VaPieces.Log?.LogDebug($"[TowerChunks] {name} at {transform.position.ToString("F0")}: {d:0} m, wanted"); WantedCount++; Entry.Want(this); }
                        else if (_near && d >= ReleaseDistanceM) { VaPieces.Log?.LogDebug($"[TowerChunks] {name} at {transform.position.ToString("F0")}: {d:0} m, released"); ReleasedCount++; Release(); }
                    }
                    yield return wait;
                }
            }

            internal bool SpawnNext(GameObject[] chunks)
            {
                if (_spawned.Count >= chunks.Length) return false;
                var go = Instantiate(chunks[_spawned.Count], transform, false);
                go.transform.localPosition = Entry.Offset;
                go.transform.localRotation = Quaternion.identity;
                _spawned.Add(go);
                if (_spawned.Count >= chunks.Length) { ShowShell(false); ShellOffCount++; }
                return true;
            }

            private void Release()
            {
                _near = false;
                ShowShell(true);
                foreach (var go in _spawned) if (go != null) Destroy(go);
                _spawned.Clear();
                Entry.Unwant(this);
            }

            private void OnDisable()
            {
                StopAllCoroutines();
                if (_near) Release();
            }
        }
    }
}
