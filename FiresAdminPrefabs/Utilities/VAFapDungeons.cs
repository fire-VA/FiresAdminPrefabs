using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // 1.1.359 (Fire 11-02: "we already have blueprints in our plan build folder that are literally just dungeon entrances that we can place and
    // they have an empty EnV box. That's all I want. I want the same thing but already on our Hammer for every type of dungeon entrance"; generating
    // a dungeon from an entrance is Dungeon Master's job). One Dungeons-tab entry per location with an interior (ZoneLocation.m_interiorRadius > 0,
    // data-driven, so modded ones show when they exist). Placing makes exactly the row of Fire's dungeonproxy_noterrainmod_*.blueprint: a LocationProxy
    // with location + seed + vanilla's own field overrides TerrainModifier.m_levelRadius / m_smoothRadius / m_paintRadius = -1, which every client's
    // ZNetView.LoadFields applies (ZNetView.cs:120). Vanilla data, so a client without FAP sees the same. Stand-ins are never instantiated.
    public static class VAFapDungeons
    {
        public const string Prefix = "FAP_Dungeon_";

        // Worlds tested with 1.1.351-1.1.358 can hold these; fap_dungeons lists and removes them.
        private static readonly int ZdoOldEmpty = "fap_loc_empty".GetStableHashCode();
        private static readonly int ZdoOldLanding = "fap_landing".GetStableHashCode();
        private static readonly int ZdoNoTerrain = "HasFieldsTerrainModifier".GetStableHashCode();

        private static GameObject _root;
        private static Mesh s_discMesh;
        private static Material s_discMat;
        private static readonly Dictionary<string, GameObject> _standins = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public const string LocationPrefix = "FAP_Location_";
        // 1.1.371 (Fire: "we want all the proxies"; his dungeonproxy_noterrainmod_mountaincave_nofog, keys decoded): GlobalWind field edits on the proxy turn the cave's fog off.
        private const string NoFogSuffix = "#nofog", NoFogLocation = "MountainCave02";

        private static void StampNoFog(ZDO zdo)
        {
            zdo.Set("HasFieldsGlobalWind", true);
            zdo.Set("GlobalWind.m_alignToWindDirection", true);
            zdo.Set("GlobalWind.m_particleVelocity", true);
            zdo.Set("GlobalWind.m_particleForce", false);
            zdo.Set("GlobalWind.m_particleEmission", true);
            zdo.Set("GlobalWind.m_particleEmissionMax", -1);
        }

        private static string SplitNoFog(string location, out bool noFog)
        {
            noFog = location != null && location.EndsWith(NoFogSuffix, StringComparison.Ordinal);
            return noFog ? location.Substring(0, location.Length - NoFogSuffix.Length) : location;
        }

        public static bool TryResolveStandin(string name, out string location)
        {
            location = null;
            if (string.IsNullOrEmpty(name)) return false;
            name = name.Replace("(Clone)", "");
            if (name.EndsWith(NoFogSuffix, StringComparison.Ordinal)) name = name.Substring(0, name.Length - NoFogSuffix.Length);
            if (name.StartsWith(Prefix, StringComparison.Ordinal)) { location = name.Substring(Prefix.Length); return true; }
            if (name.StartsWith(LocationPrefix, StringComparison.Ordinal)) { location = name.Substring(LocationPrefix.Length); return true; }
            return false;
        }

        private static bool IsListed(ZoneSystem.ZoneLocation l)
            => l != null && l.m_enable && l.m_prefab.IsValid && !string.IsNullOrEmpty(l.m_prefabName)
               && !l.m_prefabName.StartsWith("FiresDM_", StringComparison.Ordinal);   // 1.1.361 ([seasons]): FDM's per-peer clones, placed through its doors

        // 1.1.365 (Fire 11-02: "organized into locations by biome and dungeons by biome. Dungeons are something that you have to teleport into"): classified from
        // the live location itself (radius alone misses morkborg, and AncientUpgradeStation has one with no door), biome from vanilla's m_biome. One asset a frame.
        private static PieceTable s_table;
        private static bool s_scanned;
        private static readonly Dictionary<string, Heightmap.Biome> s_biomes = new Dictionary<string, Heightmap.Biome>(StringComparer.Ordinal);
        private static readonly KeyValuePair<string, Heightmap.Biome>[] BiomeRows =
        {
            new KeyValuePair<string, Heightmap.Biome>("Meadows", Heightmap.Biome.Meadows), new KeyValuePair<string, Heightmap.Biome>("Black Forest", Heightmap.Biome.BlackForest),
            new KeyValuePair<string, Heightmap.Biome>("Swamp", Heightmap.Biome.Swamp), new KeyValuePair<string, Heightmap.Biome>("Mountain", Heightmap.Biome.Mountain),
            new KeyValuePair<string, Heightmap.Biome>("Plains", Heightmap.Biome.Plains), new KeyValuePair<string, Heightmap.Biome>("Mistlands", Heightmap.Biome.Mistlands),
            new KeyValuePair<string, Heightmap.Biome>("Ashlands", Heightmap.Biome.AshLands), new KeyValuePair<string, Heightmap.Biome>("Deep North", Heightmap.Biome.DeepNorth),
            new KeyValuePair<string, Heightmap.Biome>("Ocean", Heightmap.Biome.Ocean),
        };

        public static void RegisterStandins(PieceTable table)
        {
            if (table == null) return;
            s_table = table;   // 1.1.366: the scan waits for the first admin hammer open
        }

        // 1.1.366 (client crash at commit 99 %, lead + Fire: cache over repeat): first admin hammer open only, and once per game + mod list: the kinds are read
        // from BepInEx\cache\FiresAdminPrefabs\location_kinds.txt, and only a location missing there loads its asset.
        private static Dictionary<string, bool> s_kinds;
        private static string KindsPath => System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresAdminPrefabs", "location_kinds.txt");
        private static string KindsKey()
        {
            var mods = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Select(p => p.Metadata.GUID + "=" + p.Metadata.Version).OrderBy(x => x, StringComparer.Ordinal);
            return Version.GetVersionString() + "|" + string.Join(",", mods).GetStableHashCode();
        }

        private static void LoadKinds()
        {
            s_kinds = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                if (!System.IO.File.Exists(KindsPath)) return;
                var lines = System.IO.File.ReadAllLines(KindsPath);
                if (lines.Length == 0 || lines[0] != KindsKey()) { VaPieces.Log?.LogDebug("[FAP] location kinds cache: game or mod list changed, rescanning"); return; }
                for (int i = 1; i < lines.Length; i++)
                {
                    int eq = lines[i].LastIndexOf((char)61);
                    if (eq > 0) s_kinds[lines[i].Substring(0, eq)] = lines[i].Substring(eq + 1) == "1";
                }
            }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[FAP] location kinds cache unreadable: {ex.Message}"); }
        }

        private static void SaveKinds()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(KindsPath));
                var lines = new List<string> { KindsKey() };
                foreach (var kv in s_kinds) lines.Add(kv.Key + (char)61 + (kv.Value ? "1" : "0"));
                System.IO.File.WriteAllLines(KindsPath, lines);
            }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[FAP] location kinds cache not written: {ex.Message}"); }
        }

        private static bool s_standinBiomeHas(string prefix, Heightmap.Biome biome)
        {
            foreach (var kv in _standins)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && TryResolveStandin(kv.Key, out var n) && s_biomes.TryGetValue(n, out var b) && (b & biome) != 0) return true;
            return false;
        }

        private static long PrivateMb() { try { return UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024); } catch { return -1; } }

        internal static void OnHammerOpened() { if (!s_scanned) TryStartScan(); }

        private static void TryStartScan()
        {
            if (s_scanned || s_table == null || ZoneSystem.instance == null || ZoneSystem.instance.m_locations == null || VaPieces.Instance == null || !LocalIsRealAdmin()) return;
            s_scanned = true;
            VaPieces.Instance.StartCoroutine(Scan());
        }

        private static System.Collections.IEnumerator Scan()
        {
            if (!VACategoryRegistry.TryGet(VACategoryRegistry.CatDungeons, out var entrances) || !VACategoryRegistry.TryGet(VACategoryRegistry.CatLocations, out var locations)) yield break;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LoadKinds();
            int cached = s_kinds.Count, loaded = 0;
            long memBefore = PrivateMb();
            VaPieces.Log?.LogDebug($"[FAP] [Mem] location scan start: Unity allocated {memBefore} MB, {cached} kind(s) cached");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int dungeons = 0, others = 0;
            foreach (var l in ZoneSystem.instance.m_locations.ToArray())
            {
                if (!IsListed(l)) continue;
                Heightmap.Biome had;
                s_biomes[l.m_prefabName] = (s_biomes.TryGetValue(l.m_prefabName, out had) ? had : 0) | l.m_biome;
                if (!seen.Add(l.m_prefabName)) continue;
                bool dungeon = false;
                if (s_kinds.TryGetValue(l.m_prefabName, out bool known)) dungeon = known;
                else
                {
                    // 1.1.367 (client crash right after the stand-ins, 1.1.365 + 1.1.366's first read): vanilla's own async load (LocationPrefabLoadData), never a
                    // synchronous Load + Release on the main thread between its other loads; one at a time, released a frame after it is read.
                    loaded++;
                    bool done = false, ok = false;
                    try { l.m_prefab.LoadAsync((id, result) => { done = true; ok = result == SoftReferenceableAssets.LoadResult.Succeeded; }); }
                    catch (Exception ex) { VaPieces.Log?.LogDebug($"[FAP] location {l.m_prefabName}: not loaded ({ex.Message})"); done = true; }
                    float until = Time.realtimeSinceStartup + 10f;
                    while (!done && Time.realtimeSinceStartup < until) yield return null;
                    if (ok)
                    {
                        var asset = l.m_prefab.Asset;
                        var location = asset != null ? asset.GetComponent<Location>() : null;
                        dungeon = location != null && location.m_hasInterior && asset.GetComponentsInChildren<Teleport>(true).Length >= 2;
                        yield return null;
                    }
                    else VaPieces.Log?.LogDebug($"[FAP] location {l.m_prefabName}: load {(done ? "failed" : "timed out")}");
                    if (done) l.m_prefab.Release();
                    if (!ok) continue;
                }
                s_kinds[l.m_prefabName] = dungeon;
                try
                {
                    var s = GetOrCreate(l, dungeon ? entrances : locations, dungeon);
                    if (s != null && !s_table.m_pieces.Contains(s)) s_table.m_pieces.Add(s);
                    if (dungeon && l.m_prefabName == NoFogLocation)
                    {
                        var nf = GetOrCreate(l, entrances, true, true);
                        if (nf != null && !s_table.m_pieces.Contains(nf)) s_table.m_pieces.Add(nf);
                    }
                }
                catch (Exception ex) { Debug.LogWarning($"[FAP] location {l.m_prefabName}: no hammer entry ({ex.Message})"); }
                if (dungeon) dungeons++; else others++;
                if (loaded > 0) VaPieces.Log?.LogDebug($"[FAP] location table: {l.m_prefabName} = {(dungeon ? "dungeon" : "location")}, biome {s_biomes[l.m_prefabName]}, radius {l.m_interiorRadius}");
                yield return null;   // one a frame, cached or not
            }
            if (loaded > 0) SaveKinds();
            VaPieces.Log?.LogDebug($"[FAP] [Mem] location scan end: Unity allocated {PrivateMb()} MB (was {memBefore}), {loaded} asset(s) loaded, {cached} from the cache");
            // 1.1.373 (Fire 11-02: "entrances was meant to show on the sorted tab not on its own tab outside of dungeon sorts"): rows inside the Dungeons sort.
            VACategoryRegistry.AddSubFilter(VACategoryRegistry.CatDungeons, "Entrances", p => p != null && p.name.StartsWith(Prefix, StringComparison.Ordinal));
            foreach (var row in BiomeRows)
            {
                var biome = row.Value;
                bool Has(string prefix) => s_standinBiomeHas(prefix, biome);
                if (Has(Prefix)) VACategoryRegistry.AddSubFilter(VACategoryRegistry.CatDungeons, "Entrances: " + row.Key, p => p != null && p.name.StartsWith(Prefix, StringComparison.Ordinal) && TryResolveStandin(p.name, out var n) && s_biomes.TryGetValue(n, out var b) && (b & biome) != 0);
                if (Has(LocationPrefix)) VACategoryRegistry.AddSubFilter(VACategoryRegistry.CatLocations, row.Key, p => p != null && p.name.StartsWith(LocationPrefix, StringComparison.Ordinal) && TryResolveStandin(p.name, out var n) && s_biomes.TryGetValue(n, out var b) && (b & biome) != 0);
            }
            Debug.Log($"[FAP] Dungeons > Entrances + Locations tab: {dungeons} dungeon(s), {others} location(s), {sw.ElapsedMilliseconds} ms");
        }

        private static GameObject GetOrCreate(ZoneSystem.ZoneLocation l, Piece.PieceCategory cat, bool dungeon, bool noFog = false)
        {
            string name = (dungeon ? Prefix : LocationPrefix) + l.m_prefabName + (noFog ? NoFogSuffix : "");
            if (_standins.TryGetValue(name, out var cached) && cached != null) return cached;

            var go = new GameObject(name);
            if (_root == null)   // 1.1.370: lost in 1.1.365's rewrite, so the scan died on its first stand-in and the tabs stayed empty
            {
                _root = new GameObject("FAP_DungeonStandinRoot");
                _root.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }
            go.transform.SetParent(_root.transform, false);
            go.SetActive(false);

            // Ghost: the footprint (vanilla exterior radius), a flat translucent disc, so a location never loads just to be hovered.
            // 1.1.369 ([fgn]: Scan was the top GC site, 57 stops): one shared mesh + material per kind, not a primitive + material per location.
            if (s_discMesh == null)
            {
                var prim = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                s_discMesh = prim.GetComponent<MeshFilter>().sharedMesh;
                s_discMat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.3f, 0.7f, 1f, 0.35f) };   // 1.1.373: the primitive's default material drew pink
                UnityEngine.Object.DestroyImmediate(prim);
            }
            var disc = new GameObject("FAP_DungeonFootprint");
            disc.AddComponent<MeshFilter>().sharedMesh = s_discMesh;
            disc.AddComponent<MeshRenderer>().sharedMaterial = s_discMat;
            disc.transform.SetParent(go.transform, false);
            float r = Mathf.Max(4f, l.m_exteriorRadius);
            r = 2f;   // 1.1.373: a marker until the real meshes load, not the footprint
            disc.transform.localScale = new Vector3(r * 2f, 0.05f, r * 2f);
            var piece = go.AddComponent<Piece>();
            piece.m_name = l.m_prefabName + (dungeon ? (noFog ? " (entrance, no fog)" : " (entrance)") : "");
            piece.m_description = dungeon ? $"The {l.m_prefabName} entrance with its empty environment box. Right-click it once placed: Fully generated or Empty." : $"The whole {l.m_prefabName} location with its spawners and everything; ground not levelled. Needs FiresAdminPrefabs on the server.";
            piece.m_enabled = true;
            piece.m_category = cat;
            piece.m_clipGround = true;
            piece.m_clipEverything = true;
            piece.m_allowRotatedOverlap = true;
            piece.m_canBeRemoved = false;
            piece.m_resources = Array.Empty<Piece.Requirement>();

            var znv = go.AddComponent<ZNetView>();
            znv.m_persistent = false;

            _standins[name] = go;
            return go;
        }

        // 1.1.361 (Fire 11-02: "should never be ... shown on a normal client's Hammer tabs. They should only ever be shown to admins"): the admin answer
        // alone, never the AdminOnlyMode cfg: the server's admin list (Core), or debugmode on a server without FAP (VAFapServerPresence).
        private static bool s_lastAdmin, s_adminLogged;
        public static bool LocalIsRealAdmin()
        {
            bool admin = false; string source = "none";
            try
            {
                if (VAFapServerPresence.IsClientOnly) { admin = VAFapServerPresence.LocalIsAdmin(out _); source = "client-only: debugmode"; }
                else { admin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); source = "server admin list"; }
            }
            catch (Exception ex) { source = ex.GetType().Name; }
                        if (!s_adminLogged || admin != s_lastAdmin)
            {
                s_adminLogged = true; s_lastAdmin = admin;
                VaPieces.Log?.LogDebug($"[FAP] Entrances tab for this player: {(admin ? "shown" : "hidden")} (admin {admin}, source {source})");
            }
            return admin;
        }

        private const string RpcPlace = "FAP_PlaceEntrance", RpcRemove = "FAP_RemoveEntrance", RpcMode = "FAP_EntranceMode";
        private static bool s_menuRegistered;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNet_Awake_RegisterEntranceRpc
        {
            private static void Postfix()
            {
                try
                {
                    ZRoutedRpc.instance?.Register<ZPackage>(RpcPlace, OnPlaceRpc);
                    ZRoutedRpc.instance?.Register<ZDOID>(RpcRemove, OnRemoveRpc);
                    ZRoutedRpc.instance?.Register<ZDOID, bool>(RpcMode, OnModeRpc);
                    if (!s_menuRegistered) { s_menuRegistered = true; FiresCore.UI.ContextMenu.FiresContextMenu.Register(new EntranceMenu()); }
                }
                catch (Exception ex) { Debug.LogWarning($"[FAP] entrances: RPC not registered ({ex.Message})"); }
            }
        }

        private static bool ServerSaysAdmin(long sender, string what)
        {
            bool ok = FiresCore.Sync.AdminSyncing.IsAdmin(sender);
            VaPieces.Log?.LogDebug($"[FAP] entrance {what} from {sender}: admin {ok}{(ok ? "" : ", REFUSED")}");
            if (!ok) try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ShowMessage", (int)MessageHud.MessageType.Center, "Dungeons: admins only."); } catch { }
            return ok;
        }

        private static void OnPlaceRpc(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !ServerSaysAdmin(sender, "place")) return;
            string location; Vector3 pos; Quaternion rot;
            bool full;
            try { location = pkg.ReadString(); pos = pkg.ReadVector3(); rot = pkg.ReadQuaternion(); full = pkg.ReadBool(); } catch { return; }
            if (location == null || location.Length > 128 || float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) || float.IsInfinity(pos.x) || float.IsInfinity(pos.z)) return;
            string msg = full ? PlaceFull(location, pos, rot) : PlaceHere(location, pos, rot);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ShowMessage", (int)MessageHud.MessageType.Center, msg); } catch { }
        }

        private static void OnRemoveRpc(long sender, ZDOID id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !ServerSaysAdmin(sender, "remove")) return;
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || zdo.GetPrefab() != ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode()) return;   // 1.1.362: world-gen proxies too
            RaiseRemoved(zdo);
            RemoveFloor(zdo);
            DestroyGenerated(zdo);
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
        }

        // ---- 1.1.365 (Fire 11-02: "dungeon entrances should have a context menu at option to choose between fully generated or empty"; locations place in full) ----
        // Vanilla's full spawn (Core SpawnLocationAt: every part its own ZDO, generator + saved rooms, vanilla clients see it), with the same field edit on its proxy
        // as Fire's blueprints; every ZDO stamped with an id by Core so removal takes the whole thing. Needs FAP on the server and the Core with Stamped.
        private static readonly int ZdoFull = "fap_loc_full".GetStableHashCode();
        private static readonly int ZdoDungeonId = "fires_dungeon_id".GetStableHashCode();
        private static readonly int ZdoFdmId = "fdm_dungeon_id".GetStableHashCode();
        private static bool s_inFull, s_fullNoFog;
        private static bool s_coreLooked;
        private static MethodInfo s_spawnAt, s_stamped, s_destroyById, s_newId;

        private static void LookCore()
        {
            if (s_coreLooked) return;
            s_coreLooked = true;
            var dc = AccessTools.TypeByName("FiresCore.Dungeon.FiresDungeonCore");
            var td = AccessTools.TypeByName("FiresCore.Dungeon.FiresDungeonTeardown");
            s_spawnAt = dc != null ? AccessTools.Method(dc, "SpawnLocationAt", new[] { typeof(ZoneSystem.ZoneLocation), typeof(Vector3), typeof(Quaternion), typeof(ZoneSystem.SpawnMode), typeof(bool) }) : null;
            s_stamped = td != null ? AccessTools.FirstMethod(td, m => m.Name == "Stamped" && m.IsGenericMethodDefinition) : null;
            s_destroyById = td != null ? AccessTools.Method(td, "DestroyById", new[] { typeof(long), typeof(ZDOID) }) : null;
            s_newId = td != null ? AccessTools.Method(td, "NewId") : null;
            VaPieces.Log?.LogDebug($"[FAP] full placement: Core SpawnLocationAt {s_spawnAt != null}, Stamped {s_stamped != null}, DestroyById {s_destroyById != null}");
        }

        internal static bool CanPlaceFull() { LookCore(); return s_spawnAt != null && s_stamped != null && s_destroyById != null && s_newId != null; }

        private static string PlaceFull(string location, Vector3 pos, Quaternion rot)
        {
            location = SplitNoFog(location, out s_fullNoFog);
            var zs = ZoneSystem.instance;
            if (zs == null || !ZNet.instance.IsServer()) return "Full placement runs on the server.";
            if (!CanPlaceFull()) return "Fully generated needs a newer FiresUnifiedCore.";
            var loc = zs.m_locations?.Find(l => l != null && l.m_prefabName == location);
            if (!IsListed(loc)) return $"'{location}' is not a location.";
            pos = ClampToZone(loc, pos);
            long id = (long)s_newId.Invoke(null, null);
            Func<string> spawn = () => s_spawnAt.Invoke(null, new object[] { loc, pos, rot, ZoneSystem.SpawnMode.Full, true }) as string;
            var noLevel = NoLevel();
            s_inFull = true;
            try
            {
                noLevel.Key?.Invoke(null, null);
                string r = s_stamped.MakeGenericMethod(typeof(string)).Invoke(null, new object[] { id, spawn }) as string;
                VaPieces.Log?.LogDebug($"[FAP] full {location} at {pos}: id {id}, FAT no-level {noLevel.Key != null}: {r}");
                return $"{location}: {r}";
            }
            catch (Exception ex) { return $"{location}: failed ({(ex.InnerException ?? ex).Message})"; }
            finally { s_inFull = false; noLevel.Value?.Invoke(null, null); }
        }

        private static KeyValuePair<MethodInfo, MethodInfo> NoLevel()
        {
            var t = AccessTools.AllTypes().FirstOrDefault(x => x.Name == "FatApi");
            return new KeyValuePair<MethodInfo, MethodInfo>(t != null ? AccessTools.Method(t, "BeginNoLevelSpawn") : null, t != null ? AccessTools.Method(t, "EndNoLevelSpawn") : null);
        }

        // The field edit goes on the proxy vanilla makes inside the full spawn, before it draws.
        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        internal static class ZNetView_Awake_FullProxy
        {
            internal static void Run(ZNetView __instance)
            {
                if (!s_inFull || __instance == null || !__instance.IsValid() || __instance.GetComponent<LocationProxy>() == null) return;
                var zdo = __instance.GetZDO();
                StampFieldEdit(zdo);
                if (s_fullNoFog) StampNoFog(zdo);
                zdo.Set(ZdoFull, true);
                zdo.Set(VAPlacedSpawnerBoxes.ZdoKey, true);
            }
        }

        private static void StampFieldEdit(ZDO zdo)
        {
            zdo.Set("HasFields", true);
            zdo.Set(ZdoNoTerrain, true);
            zdo.Set("TerrainModifier.m_levelRadius", -1f);
            zdo.Set("TerrainModifier.m_smoothRadius", -1f);
            zdo.Set("TerrainModifier.m_paintRadius", -1f);
        }

        // Both keys: FDM's pick stores its id as fdm_dungeon_id; the Core drop's DestroyById treats the two as one.
        private static void DestroyGenerated(ZDO zdo)
        {
            LookCore();
            if (s_destroyById == null) return;
            foreach (var key in new[] { ZdoDungeonId, ZdoFdmId })
            {
                long id = zdo.GetLong(key);
                if (id != 0L) s_destroyById.Invoke(null, new object[] { id, zdo.m_uid });
            }
        }

        private static void SetMode(ZDO zdo, bool full)
        {
            var zs = ZoneSystem.instance;
            int h = zdo.GetInt(ZDOVars.s_location);
            var loc = zs.m_locations.Find(l => l != null && l.m_prefabName != null && l.m_prefabName.GetStableHashCode() == h);
            if (loc == null) return;
            Vector3 pos = zdo.GetPosition(); Quaternion rot = zdo.GetRotation();
            RemoveFloor(zdo);
            DestroyGenerated(zdo);
            zdo.Set(ZdoFdmId, 0L);
            zdo.Set(ZdoNoFloor, false);
            bool hadFull = zdo.GetBool(ZdoFull), noFog = zdo.GetBool("HasFieldsGlobalWind");
            if (full || hadFull)
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                string again = loc.m_prefabName + (noFog ? NoFogSuffix : "");   // the no-fog edit stays through a switch
                if (full) PlaceFull(again, pos, rot); else PlaceHere(again, pos, rot);
            }
            else   // Empty on a plain row: the floor comes back now if the proxy is loaded here, else when it next spawns
            {
                var live = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                var proxy = live != null ? live.GetComponent<LocationProxy>() : null;
                if (proxy != null) EnsureFloor(proxy, s_proxyInstance?.GetValue(proxy) as GameObject);
            }
        }

        // 1.1.374 (Fire: "our ground entrances should place a stone floor 4x4 centered on the door in the sky ... so we can have a place to teleport to without being blocked").
        // An Empty hammer entrance owns one vanilla stone_floor under its sky door. Gone with the entrance, and whenever the box stops being empty.
        private const string FloorPrefab = "stone_floor";
        private static readonly int ZdoEntrance = "fap_entrance".GetStableHashCode();
        private static readonly int ZdoNoFloor = "fap_no_floor".GetStableHashCode();
        private static readonly int ZdoFloorOf = "fap_floor_of".GetStableHashCode();

        private static int FloorKey(ZDO proxy) => proxy.m_uid.GetHashCode();

        private static void EnsureFloor(LocationProxy proxy, GameObject inst)
        {
            var nv = proxy != null ? proxy.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid() || inst == null || !nv.IsOwner()) return;
            var zdo = nv.GetZDO();
            if (!zdo.GetBool(ZdoEntrance) || zdo.GetBool(ZdoFull) || zdo.GetBool(ZdoNoFloor) || zdo.GetLong(ZdoFdmId) != 0L || zdo.GetInt(ZdoFloorOf) != 0) return;
            Transform door = null;
            foreach (var tp in inst.GetComponentsInChildren<Teleport>(true))
                if (tp != null && tp.transform.position.y > proxy.transform.position.y + 1000f) { door = tp.transform; break; }
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(FloorPrefab) : null;
            if (door == null || prefab == null) { VaPieces.Log?.LogDebug($"[FAP] entrance at {proxy.transform.position}: no floor (door {door != null}, prefab {prefab != null})"); return; }
            var floor = UnityEngine.Object.Instantiate(prefab, door.position, Quaternion.Euler(0f, door.eulerAngles.y, 0f));
            var fnv = floor.GetComponent<ZNetView>();
            if (fnv == null || !fnv.IsValid()) return;
            fnv.GetZDO().Set(ZdoFloorOf, FloorKey(zdo));
            zdo.Set(ZdoFloorOf, FloorKey(zdo));
            ApplyFloorNoWear(floor);
            VaPieces.Log?.LogDebug($"[FAP] entrance at {proxy.transform.position}: {FloorPrefab} at {floor.transform.position}");
        }

        // It floats at +5000 with nothing under it: vanilla support wear would break it.
        private static void ApplyFloorNoWear(GameObject go) { var w = go != null ? go.GetComponent<WearNTear>() : null; if (w != null) w.m_noSupportWear = false; }

        // 1.1.407: run by FapAwakeDispatch (FAP's ONE ZNetView.Awake / Piece.Awake postfix; [generator] audit row 4)
        internal static class ZNetView_Awake_Floor
        {
            internal static void Run(ZNetView __instance)
            {
                try { if (__instance != null && __instance.IsValid() && __instance.GetZDO().GetInt(ZdoFloorOf) != 0 && __instance.GetComponent<WearNTear>() != null) ApplyFloorNoWear(__instance.gameObject); }
                catch { }
            }
        }

        private static void RemoveFloor(ZDO proxy)
        {
            int key = proxy.GetInt(ZdoFloorOf);
            if (key == 0) return;
            proxy.Set(ZdoFloorOf, 0);
            var floors = new List<ZDO>(); int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(FloorPrefab, floors, ref index)) { }
            foreach (var f in floors)
                if (f != null && f.GetInt(ZdoFloorOf) == key) { f.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(f); }
        }

        private static ZDO s_lastPlaced;

        // 1.1.372 ([seasons], FDM 0.1.198 "any other dungeon generated from that entrance"): server only. The entrance as Empty, its generated content gone; FDM then
        // generates from the returned proxy. A full row gets a new proxy; a row that is already a plain proxy returns itself. Removed is not raised. Null = failed.
        internal static ZDO ResetToEmpty(ZDO zdo)
        {
            try
            {
                if (zdo == null || ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null) return null;
                if (zdo.GetPrefab() != ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode()) return null;
                ZDO result = zdo;
                if (zdo.GetBool(ZdoFull)) { s_lastPlaced = null; SetMode(zdo, false); result = s_lastPlaced; }
                else SetMode(zdo, false);
                if (result != null) { RemoveFloor(result); result.Set(ZdoNoFloor, true); }   // the caller generates here next: no floor inside its dungeon
                return result;
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP] entrance reset failed: {ex.Message}"); return null; }
        }

        private static void OnModeRpc(long sender, ZDOID id, bool full)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !ServerSaysAdmin(sender, full ? "fully generated" : "empty")) return;
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || zdo.GetPrefab() != ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode() || !zdo.GetBool(ZdoNoTerrain)) return;
            if (full && !CanPlaceFull()) { try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "ShowMessage", (int)MessageHud.MessageType.Center, "Fully generated needs a newer FiresUnifiedCore on the server."); } catch { } return; }
            SetMode(zdo, full);
        }

        private sealed class EntranceMenu : FiresCore.UI.ContextMenu.IContextMenuProvider
        {
            public string TitleFor(FiresCore.UI.ContextMenu.ContextTarget target) => null;

            public IEnumerable<FiresCore.UI.ContextMenu.ContextMenuItem> GetItems(FiresCore.UI.ContextMenu.ContextTarget target)
            {
                if (!LocalIsRealAdmin() || target?.GameObject == null) return null;
                var proxy = target.GameObject.GetComponentInParent<LocationProxy>();
                var nv = proxy != null ? proxy.GetComponent<ZNetView>() : null;
                if (nv == null || !nv.IsValid() || !nv.GetZDO().GetBool(ZdoNoTerrain)) return null;
                var zdo = nv.GetZDO();
                var items = new List<FiresCore.UI.ContextMenu.ContextMenuItem>();
                ZDOID id = zdo.m_uid;
                bool generated = zdo.GetBool(ZdoFull) || zdo.GetLong(ZdoFdmId) != 0L;
                if (generated) items.Add(FiresCore.UI.ContextMenu.ContextMenuItem.Row("Empty", () => Choose(id, false)));
                if (!zdo.GetBool(ZdoFull)) items.Add(FiresCore.UI.ContextMenu.ContextMenuItem.Row("Fully generated", () => Choose(id, true)));
                return items;
            }

            private static void Choose(ZDOID id, bool full)
            {
                if (VAFapServerPresence.IsClientOnly && full) { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Fully generated needs FiresAdminPrefabs on the server."); return; }
                if (ZNet.instance.IsServer()) OnModeRpc(ZRoutedRpc.instance.m_id, id, full);
                else ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcMode, id, full);
            }
        }

        // With FAP on the server the server places and checks its admin list; a server without FAP can't check, so the local debugmode answer places.
        public static string Place(string standin, Vector3 pos, Quaternion rot)
        {
            if (!TryResolveStandin(standin, out string location)) return "No location selected.";
            bool full = standin.StartsWith(LocationPrefix, StringComparison.Ordinal);
            if (standin.EndsWith(NoFogSuffix, StringComparison.Ordinal)) location += NoFogSuffix;
            if (full && (VAFapServerPresence.IsClientOnly || !CanPlaceFull())) return "Locations need FiresAdminPrefabs on the server and a newer FiresUnifiedCore.";
            if (!LocalIsRealAdmin()) return "Dungeons: admins only.";
            if (ZNet.instance == null || ZRoutedRpc.instance == null) return "Not in a world.";
            if (ZNet.instance.IsServer()) return full ? PlaceFull(location, pos, rot) : PlaceHere(location, pos, rot);
            if (VAFapServerPresence.IsClientOnly) return PlaceHere(location, pos, rot);
            var pkg = new ZPackage();
            pkg.Write(location); pkg.Write(pos); pkg.Write(rot); pkg.Write(full);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcPlace, pkg);
            return $"Placing {location}...";
        }

        private static string PlaceHere(string location, Vector3 pos, Quaternion rot)
        {
            location = SplitNoFog(location, out bool noFog);
            var zs = ZoneSystem.instance;
            if (zs == null || ZNet.instance == null || zs.m_locationProxyPrefab == null) return "Not in a world.";
            var loc = zs.m_locations?.Find(l => l != null && l.m_prefabName == location);
            if (!IsListed(loc)) return $"'{location}' is not a location.";
            var go = UnityEngine.Object.Instantiate(zs.m_locationProxyPrefab, pos, rot);
            var proxy = go.GetComponent<LocationProxy>();
            var nv = go.GetComponent<ZNetView>();
            if (proxy == null || nv == null || !nv.IsValid()) { UnityEngine.Object.Destroy(go); return "Location proxy failed."; }
            var zdo = nv.GetZDO();
            StampFieldEdit(zdo);
            if (noFog) StampNoFog(zdo);
            zdo.Set(ZdoEntrance, true);
            s_lastPlaced = zdo;
            proxy.SetLocation(loc.m_prefabName, UnityEngine.Random.Range(0, 99999), true);
            VAPlacedSpawnerBoxes.OnPlaced(go);   // 1.1.361: the hammer box, so it can be removed
            VaPieces.Log?.LogDebug($"[FAP] dungeon entrance {loc.m_prefabName} placed at {pos}");
            return $"{loc.m_prefabName}: entrance placed.";
        }

        // 1.1.361 (Fire 11-02: removing the entrance; [seasons] tears a linked dungeon down from this): the box has no Piece, so vanilla's RemovePiece
        // finds nothing, and a Location's own no-build area would refuse it anyway. The entrance is removed here, admin only, before vanilla looks.
        private static void RaiseRemoved(ZDO zdo)
        {
            try { VerdantsAscent.Api.FapEntrances.RaiseRemoved(zdo); }
            catch (Exception ex) { Debug.LogWarning($"[FAP] entrance removed handler threw: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(Player), "RemovePiece")]
        private static class Player_RemovePiece_Entrance
        {
            private static bool Prefix(Player __instance, ref bool __result)
            {
                if (GameCamera.instance == null || !Physics.Raycast(GameCamera.instance.transform.position, GameCamera.instance.transform.forward, out var hit, 50f, __instance.m_removeRayMask)) return true;
                if (hit.collider.name != "HammerBox" || Vector3.Distance(hit.point, __instance.m_eye.position) > __instance.m_maxPlaceDistance) return true;
                var proxy = hit.collider.GetComponentInParent<LocationProxy>();
                var nv = proxy != null ? proxy.GetComponent<ZNetView>() : null;
                if (nv == null || !nv.IsValid() || proxy.transform.Find("HammerBox") == null) return true;
                if (!LocalIsRealAdmin()) { __instance.Message(MessageHud.MessageType.Center, "Dungeons: admins only."); __result = false; return false; }
                if (!ZNet.instance.IsServer() && !VAFapServerPresence.IsClientOnly)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcRemove, nv.GetZDO().m_uid);
                    __result = true;
                    return false;
                }
                RaiseRemoved(nv.GetZDO());
                RemoveFloor(nv.GetZDO());
                DestroyGenerated(nv.GetZDO());
                VaPieces.Log?.LogDebug($"[FAP] dungeon entrance at {proxy.transform.position} removed by hammer");
                nv.ClaimOwnership();
                ZNetScene.instance.Destroy(proxy.gameObject);
                __result = true;
                return false;
            }
        }

        // 1.1.362 (Fire: "removing the entrance", vanilla dungeons too): a world-gen proxy whose spawned location has an interior gets the box, for admins only.
        private static readonly FieldInfo s_proxyInstance = AccessTools.Field(typeof(LocationProxy), "m_instance");

        [HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
        private static class LocationProxy_SpawnLocation_Box
        {
            private static void Postfix(LocationProxy __instance, bool __result)
            {
                try
                {
                    if (!__result) return;
                    var inst = s_proxyInstance?.GetValue(__instance) as GameObject;
                    EnsureFloor(__instance, inst);   // 1.1.374, on whichever peer owns the row
                    if (Application.isBatchMode || !LocalIsRealAdmin()) return;
                    var location = inst != null ? inst.GetComponent<Location>() : null;
                    if (location == null || !location.m_hasInterior) return;
                    VAPlacedSpawnerBoxes.TrackAny(__instance.GetComponent<ZNetView>());
                }
                catch (Exception ex) { VaPieces.Log?.LogDebug($"[FAP] entrance box: {ex.Message}"); }
            }
        }

        // 1.1.373 (Fire: "give me a ghost not a pink circle that doesnt show the rotation or placement of the actual entrance"): the ghost is the location's own
        // exterior meshes, copied onto the stand-in when it is picked, loaded with vanilla's async path and held for the session.
        private static ZoneSystem.ZoneLocation s_ghostLoc;
        private static readonly HashSet<string> s_ghostBuilt = new HashSet<string>(StringComparer.Ordinal);

        // 1.1.396 (Fire 11-06: "our locations on the hammer should have actual ghost previews"): this postfix sat on VAFapDungeons itself, a class
        // with no [HarmonyPatch], and FAP patches type by type with CreateClassProcessor(type), which skips unannotated classes, so it never
        // attached and every location kept the blue marker disc. Now its own annotated class; the body is unchanged.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        private static class Player_SetupPlacementGhost_LocationGhost
        {
            [HarmonyPostfix]
            private static void Postfix(Player __instance) => Player_SetupPlacementGhost(__instance);
        }

        private static void Player_SetupPlacementGhost(Player __instance)
        {
            try
            {
                if (__instance != Player.m_localPlayer || s_table == null) return;
                var ghost = s_ghost(__instance);
                if (ghost == null || !TryResolveStandin(ghost.name, out string name)) return;
                string key = ghost.name.Replace("(Clone)", "");
                if (s_ghostBuilt.Contains(key) || !_standins.TryGetValue(key, out var stub) || stub == null) return;
                var loc = ZoneSystem.instance?.m_locations?.Find(l => l != null && l.m_prefabName == name);
                if (loc == null) { Debug.Log($"[FAP] location ghost {key}: no vanilla location named '{name}', marker kept"); return; }
                s_ghostBuilt.Add(key);
                // Held, not released: the stand-in keeps using these meshes for the session; only picked locations load.
                s_ghostLoc = loc;
                loc.m_prefab.LoadAsync((id, result) =>
                {
                    if (result != SoftReferenceableAssets.LoadResult.Succeeded) { s_ghostBuilt.Remove(key); Debug.Log($"[FAP] location ghost {key}: load {result}, marker kept"); return; }
                    BuildGhost(stub, loc.m_prefab.Asset);
                    var p = Player.m_localPlayer;
                    if (p != null && s_ghost(p) != null && s_ghost(p).name.Replace("(Clone)", "") == key) p.SetupPlacementGhost();
                });
            }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[FAP] entrance ghost: {ex.Message}"); }
        }

        private static void BuildGhost(GameObject stub, GameObject asset)
        {
            if (stub == null || asset == null) return;
            var skip = new HashSet<Renderer>();
            foreach (var lg in asset.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) skip.Add(r);
            }
            var toRoot = asset.transform.worldToLocalMatrix;
            int made = 0;
            foreach (var r in asset.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (r == null || skip.Contains(r) || !r.enabled || made >= 1500) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var m = toRoot * r.transform.localToWorldMatrix;
                if (m.GetColumn(3).y > 1000f) continue;   // the interior, in the sky
                var part = new GameObject("FAP_GhostPart");
                part.transform.SetParent(stub.transform, false);
                part.transform.localPosition = m.GetColumn(3);
                part.transform.localRotation = m.rotation;
                part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
                made++;
            }
            if (made > 0) { var disc = stub.transform.Find("FAP_DungeonFootprint"); if (disc != null) UnityEngine.Object.DestroyImmediate(disc.gameObject); }
            if (made == 0) Debug.Log($"[FAP] location ghost {stub.name}: 0 active meshes in the location, marker kept");
            else VaPieces.Log?.LogDebug($"[FAP] location ghost {stub.name}: {made} mesh(es)");
        }

        // Vanilla keeps a location inside its zone by its radius (TestSpawnLocation).
        internal static Vector3 ClampToZone(ZoneSystem.ZoneLocation loc, Vector3 pos)
        {
            float rad = Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius);
            Vector3 zc = ZoneSystem.GetZonePos(ZoneSystem.GetZone(pos));
            pos.x = Mathf.Clamp(pos.x, zc.x - 32f + rad, zc.x + 32f - rad);
            pos.z = Mathf.Clamp(pos.z, zc.z - 32f + rad, zc.z + 32f - rad);
            return pos;
        }

        private static readonly AccessTools.FieldRef<Player, GameObject> s_ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        // The ghost shows where the entrance really lands.
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class Player_UpdatePlacementGhost_Dungeon
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Player __instance)
            {
                var ghost = s_ghost(__instance);
                if (ghost == null || !ghost.activeSelf || !TryResolveStandin(ghost.name, out string name) || ZoneSystem.instance == null) return;
                var loc = ZoneSystem.instance.m_locations?.Find(l => l != null && l.m_prefabName == name);
                // 1.1.373: only a location placed in full stays inside its zone (vanilla's spawn expects it); an entrance lands at the cursor, as Fire's blueprints do.
                if (loc != null && ghost.name.StartsWith(LocationPrefix, StringComparison.Ordinal)) ghost.transform.position = ClampToZone(loc, ghost.transform.position);
            }
        }

        // fap_dungeons [remove <x> <z>], on the server: every entrance FAP placed in this world (old = fap_loc_empty), remove = the one at x z
        // plus an old landing floor in its zone.
        internal static string ListOrRemove(bool remove, float x, float z)
        {
            var zs = ZoneSystem.instance;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || zs?.m_locationProxyPrefab == null) return "fap_dungeons: run it on the server";
            var list = new List<ZDO>(); int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(zs.m_locationProxyPrefab.name, list, ref index)) { }
            var lines = new List<string>(); int removed = 0;
            foreach (var zdo in list)
            {
                if (zdo == null) continue;
                bool old = zdo.GetBool(ZdoOldEmpty);
                if (!old && !zdo.GetBool(ZdoNoTerrain)) continue;
                var p = zdo.GetPosition();
                int h = zdo.GetInt(ZDOVars.s_location);
                string name = zs.m_locations.Find(l => l != null && l.m_prefabName != null && l.m_prefabName.GetStableHashCode() == h)?.m_prefabName ?? h.ToString();
                lines.Add($"{name}{(old ? " (old)" : "")} at ({p.x:0},{p.y:0},{p.z:0})");
                if (!remove || new Vector2(p.x - x, p.z - z).magnitude > 2f) continue;
                var zone = ZoneSystem.GetZone(p);
                var floors = new List<ZDO>(); int fi = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative("stone_floor_2x2", floors, ref fi)) { }
                foreach (var f in floors)
                    if (f != null && f.GetBool(ZdoOldLanding) && ZoneSystem.GetZone(f.GetPosition()) == zone) { f.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(f); }
                RaiseRemoved(zdo);
                RemoveFloor(zdo);
                DestroyGenerated(zdo);
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }
            return (remove ? $"fap_dungeons: removed {removed}. " : "fap_dungeons: ") + $"{lines.Count} entrance(s): {string.Join("; ", lines)}";
        }
    }
}