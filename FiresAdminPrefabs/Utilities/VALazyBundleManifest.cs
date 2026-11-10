using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// Stage 1 of FAP's lazy third-party bundles (Tools\FAP_LAZY_BUNDLES_DESIGN.md; Fire 10-13, option A: lazy with a
    /// look-ahead). After FAP's Hammer scan, one sidecar per Assets bundle records what a STUB prefab needs, so a later build
    /// can register the bundle's prefabs at startup without loading it: per prefab its name, the Piece's display name /
    /// description / category / requirements / crafting station, the icon cache key, and which components it carries.
    /// A sidecar is keyed on its bundle file's size + write time and rewritten only when the bundle changes.
    /// This stage changes nothing else: every bundle still loads as today.
    /// Files: BepInEx\cache\FiresAdminPrefabs\LazyManifests\&lt;bundle&gt;.lazymanifest (JSON). Client only.
    /// </summary>
    public static class VALazyBundleManifest
    {
        public const int FormatVersion = 4;   // 4 (1.1.390): Entry.lazyNonPiece; 3 (1.1.378): Entry.leads;   // 1.1.229: v1 files were written by JsonUtility, which dropped the prefabs list (R-1013AK)
        public static string Dir => Path.Combine(VAFapCachePaths.Root, "LazyManifests");

        [Serializable]
        public class Requirement { public string item; public int amount; public int perLevel; public bool recover; }

        [Serializable]
        public class Entry
        {
            public string prefab;
            public int hash;
            public bool hasPiece;
            public string name, description, station;
            public int category;
            public string icon;            // the FAP icon cache key (the prefab name; PNG in IconCache)
            public bool onHammer;
            public List<Requirement> requirements = new List<Requirement>();
            public bool wearNTear, container, door, fireplace, crafting, interactable, light, animator, collider;
            public int renderers;
            public bool lazyNonPiece;   // 1.1.390: no gameplay component a mod could read off the ZNetScene list (effects, projectiles, props): LazyNonPieces may stub it
            public List<string> leads;     // 1.1.378: prefabs this piece grows or spawns (vanilla Plant / CreatureSpawner fields); some have no container path, so only this piece reaches them
        }

        [Serializable]
        public class Manifest
        {
            public int format = FormatVersion;
            public string bundle;
            public long fileSize;
            public long fileTicks;
            public string writtenUtc;
            public List<Entry> prefabs = new List<Entry>();
        }

        public static string PathFor(string bundleLabel) => Path.Combine(Dir, VAFapFileNames.ToSafeFileName(bundleLabel) + ".lazymanifest");

        /// <summary>True when the sidecar for this bundle exists and matches the bundle file (stage 2 reads only these).</summary>
        public static bool IsCurrent(string bundleLabel, string bundlePath, out Manifest manifest)
        {
            manifest = null;
            try
            {
                string p = PathFor(bundleLabel);
                if (!File.Exists(p) || string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath)) return false;
                manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(p));
                var fi = new FileInfo(bundlePath);
                return manifest != null && manifest.prefabs != null && manifest.prefabs.Count > 0 && manifest.format == FormatVersion && manifest.fileSize == fi.Length && manifest.fileTicks == fi.LastWriteTimeUtc.Ticks;
            }
            catch { return false; }
        }

        /// <summary>After the Hammer scan: writes the sidecar of every loaded bundle that has none or an old one.</summary>
        public static void WriteMissing(ZNetScene scene, PieceTable hammer)
        {
            if (VaPieces.IsDedicatedServer || scene == null) return;
            int written = 0, current = 0;
            try
            {
                Directory.CreateDirectory(Dir);
                var onHammer = new HashSet<string>(hammer != null ? hammer.m_pieces.Where(p => p != null).Select(p => p.name) : Enumerable.Empty<string>(), StringComparer.Ordinal);
                var byBundle = new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
                foreach (var prefab in scene.m_prefabs)
                {
                    if (prefab == null || !VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out string label)) continue;
                    if (!byBundle.TryGetValue(label, out var list)) byBundle[label] = list = new List<GameObject>();
                    list.Add(prefab);
                }
                foreach (var kv in byBundle)
                {
                    if (!VAExternalBundleLoader.TryGetBundlePath(kv.Key, out string path)) continue;
                    if (IsCurrent(kv.Key, path, out _)) { current++; continue; }
                    var fi = new FileInfo(path);
                    var m = new Manifest { bundle = kv.Key, fileSize = fi.Length, fileTicks = fi.LastWriteTimeUtc.Ticks, writtenUtc = DateTime.UtcNow.ToString("o") };
                    foreach (var prefab in kv.Value) m.prefabs.Add(Describe(prefab, onHammer.Contains(prefab.name)));
                    File.WriteAllText(PathFor(kv.Key), Newtonsoft.Json.JsonConvert.SerializeObject(m));
                    written++;
                }
                Debug.Log($"{VaPieces.PluginName}: [LazyBundles] manifests: {written} written, {current} already current, {byBundle.Count} bundle(s) ({Dir})");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [LazyBundles] manifest write failed: {ex.Message}"); }
        }

        private static Entry Describe(GameObject prefab, bool hammer)
        {
            var e = new Entry { prefab = prefab.name, hash = prefab.name.GetStableHashCode(), icon = prefab.name, onHammer = hammer };
            var piece = prefab.GetComponent<Piece>();
            if (piece != null)
            {
                e.hasPiece = true;
                var plant = prefab.GetComponent<Plant>();
                if (plant != null && plant.m_grownPrefabs != null)
                    foreach (var grown in plant.m_grownPrefabs) if (grown != null) (e.leads ?? (e.leads = new List<string>())).Add(grown.name);
                var spawner = prefab.GetComponent<CreatureSpawner>();
                if (spawner != null && spawner.m_creaturePrefab != null) (e.leads ?? (e.leads = new List<string>())).Add(spawner.m_creaturePrefab.name);
                e.name = piece.m_name;
                e.description = piece.m_description;
                e.category = (int)piece.m_category;
                e.station = piece.m_craftingStation != null ? piece.m_craftingStation.m_name : "";
                if (piece.m_resources != null)
                    foreach (var r in piece.m_resources)
                        if (r != null && r.m_resItem != null)
                            e.requirements.Add(new Requirement { item = r.m_resItem.gameObject.name, amount = r.m_amount, perLevel = r.m_amountPerLevel, recover = r.m_recover });
            }
            e.wearNTear = prefab.GetComponent<WearNTear>() != null;
            e.container = prefab.GetComponentInChildren<Container>(true) != null;
            e.door = prefab.GetComponentInChildren<Door>(true) != null;
            e.fireplace = prefab.GetComponentInChildren<Fireplace>(true) != null;
            e.crafting = prefab.GetComponentInChildren<CraftingStation>(true) != null;
            e.interactable = prefab.GetComponentInChildren<Interactable>(true) != null;
            e.light = prefab.GetComponentInChildren<Light>(true) != null;
            e.animator = prefab.GetComponentInChildren<Animator>(true) != null;
            e.collider = prefab.GetComponentInChildren<Collider>(true) != null;
            e.renderers = prefab.GetComponentsInChildren<Renderer>(true).Length;
            e.lazyNonPiece = !e.hasPiece && !e.container && !e.door && !e.fireplace && !e.crafting && !e.interactable
                && prefab.GetComponentInChildren<ItemDrop>(true) == null && prefab.GetComponentInChildren<Character>(true) == null
                && prefab.GetComponentInChildren<Pickable>(true) == null && prefab.GetComponentInChildren<TreeBase>(true) == null
                && prefab.GetComponentInChildren<TreeLog>(true) == null && prefab.GetComponentInChildren<MineRock>(true) == null
                && prefab.GetComponentInChildren<MineRock5>(true) == null && prefab.GetComponentInChildren<Destructible>(true) == null
                && prefab.GetComponentInChildren<Location>(true) == null && prefab.GetComponentInChildren<CreatureSpawner>(true) == null
                && prefab.GetComponentInChildren<SpawnArea>(true) == null;
            return e;
        }
    }
}
