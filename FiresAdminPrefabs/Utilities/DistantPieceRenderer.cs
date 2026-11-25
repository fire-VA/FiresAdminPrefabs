using System;
using UnityEngine;
using FiresCore.Services;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // FAP-side wrapper around FiresCore.Services.DistantRenderer. Owns
    // the two FAP-specific concerns FUC's generic service deliberately
    // doesn't:
    //   - syncing the FAP per-piece override store (VAFapPieceOverrides)
    //     + the legacy hardcoded prefab HashSet (VAPieceManager.
    //     DistantRenderPrefabs) into FUC's registry on startup + on
    //     every override-change event
    //   - propagating Distant=true to the prefab ZNetView so new
    //     instances inherit the flag without a world reload
    //   - per-piece tag that the Piece.SetCreator postfix below sets on a
    //     NEWLY PLACED piece: ZDO Distant + ForceSendZDO (1.1.415; it used to
    //     run from Piece.Awake on every piece at load)
    //
    // 1.1.415: FUC's DistantRenderer is only a registry now (forwarded to FGN).
    // Its old server scan, client cache, CreateDestroyObjects / FindSectorObjects
    // patches and driver are gone (FiresUnifiedCore Services\DistantRenderer.cs:14).
    // Pieces already in the world keep whatever tag they have (Fire 11-11: keep).
    public static class DistantPieceRenderer
    {
        private const float LegacyDefaultDistance = 3000f;
        private const string CloneSuffix = "(Clone)";

        private static bool _wired;

        public static void Initialize()
        {
            SyncRegistrations();
            if (!_wired)
            {
                VAFapPieceOverrides.OnOverridesChanged -= OnOverridesChanged;
                VAFapPieceOverrides.OnOverridesChanged += OnOverridesChanged;
                _wired = true;
            }
        }

        public static void ClearAll()
        {
            if (_wired)
            {
                VAFapPieceOverrides.OnOverridesChanged -= OnOverridesChanged;
                _wired = false;
            }
            DistantRenderer.Clear();
        }

        // Per-piece tag. Called from the Piece.SetCreator postfix when a piece is
        // placed: if the prefab is registered with FUC's DistantRenderer,
        // mark the ZDO Distant + ForceSendZDO so vanilla's distant pass creates it far away.
        private static readonly System.Collections.Generic.Dictionary<int, string> s_nameByHash = new System.Collections.Generic.Dictionary<int, string>();   // 1.1.402

        // 1.1.415 (Fire 11-11, "only meant to add them to new pieces not any pieces in the world"): called ONLY from the Piece.SetCreator postfix
        // below, i.e. when a player places a piece (vanilla Player.PlacePiece -> SetCreator, Player.cs:3289). It used to run from Piece.Awake on every
        // piece as zones loaded (server only), re-tagging the whole world at each arrival. Pieces already in the world keep the tag they have (Core no longer has a periodic scan: FiresUnifiedCore ServicesDistantRenderer.cs:14).
        // The placer owns the new ZDO (SetCreator itself requires IsOwner, Piece.cs:445), so the owner check replaces the old server-only check:
        // this also covers a client placing on a dedicated server, which the server-only check never saw.
        private static bool s_placementOnlyLogged;

        [HarmonyLib.HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        private static class Piece_SetCreator_DistantTag
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(Piece __instance)
            {
                try
                {
                    if (!s_placementOnlyLogged)
                    {
                        s_placementOnlyLogged = true;
                        VaPieces.Log?.LogInfo("[DistantTag] the Distant tag is set on newly placed pieces only (Piece.SetCreator); pieces already in the world are not re-tagged at load (1.1.415, Fire 11-11)");
                    }
                    RegisterPiece(__instance);
                }
                catch (Exception ex) { VaPieces.Log?.LogDebug($"[DistantTag] placement tag threw: {ex.Message}"); }
            }
        }

        public static void RegisterPiece(Piece piece)
        {
            if (piece == null) return;
            if (ZNet.instance == null || ZDOMan.instance == null) return;
            // 1.1.402 ([visual] audit via [lead] 11-08): nothing registered = return; otherwise the prefab name comes from a per-hash cache instead of
            // a new string per piece. Same IsRegistered answer: the registry can change at any time, so it is still asked every time.
            if (!DistantRenderer.HasAny) return;

            var znv = piece.GetComponent<ZNetView>();
            if (znv == null || !znv.IsValid()) return;

            ZDO zdo = znv.GetZDO();
            if (zdo == null || !znv.IsOwner()) return;   // 1.1.415: the placer owns the new piece

            int hash = zdo.GetPrefab();
            if (!s_nameByHash.TryGetValue(hash, out string prefabName))
            {
                prefabName = piece.gameObject.name;
                if (prefabName.EndsWith(CloneSuffix))
                    prefabName = prefabName.Substring(0, prefabName.Length - CloneSuffix.Length).TrimEnd();
                if (prefabName.GetStableHashCode() == hash) s_nameByHash[hash] = prefabName;   // only cache a name that is the ZDO's prefab
            }

            if (!DistantRenderer.IsRegistered(prefabName)) return;

            if (!zdo.Distant) zdo.SetDistant(true);
            ZDOMan.instance.ForceSendZDO(zdo.m_uid);
        }

        public static void UnregisterPiece(ZNetView znv) { }

        private static void OnOverridesChanged() => SyncRegistrations();

        // 1.1.411 ([generator] M22): EBM's far buildings (EBM 1.2.210+) skip the pieces this VA distant method already draws. EBM cannot read
        // Core, so FAP pushes the same prefab + distance list. Pushed after every sync and once more when the world's ZNetScene is up (EBM may
        // load after FAP's Awake).
        // 1.1.412 ([ghost]'s EBM 1.2.210, the real API): EbmApi.RegisterFarSkip(string prefabName, float maxDistance), maxDistance <= 0 removes,
        // last value wins. (1.1.411 bound SetDistantDrawn / ClearDistantDrawn, names that never shipped.) By name, no compile reference;
        // an EBM without it = one Info line, nothing pushed. Prefabs dropped since the last push are removed with 0.
        private static readonly System.Collections.Generic.Dictionary<string, float> s_pushed = new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.Dictionary<string, float> s_inEbm = new System.Collections.Generic.Dictionary<string, float>();
        private static readonly System.Collections.Generic.List<string> s_drop = new System.Collections.Generic.List<string>();

        private static void Reg(string name, float dist)
        {
            DistantRenderer.Register(name, dist);
            s_pushed[name] = dist;   // the last distance wins, as in DistantRenderer
        }

        internal static void PushToEbm(string why)
        {
            VAFarBakeSource.TryRegister();   // 1.1.426 ([visual] G2): baked blueprints drawn far by EBM, before the skip list is pushed
            var skip = EbmFarSkipApi.Bind();
            if (skip == null) return;
            try
            {
                s_drop.Clear();
                foreach (var kv in s_inEbm) if (!s_pushed.ContainsKey(kv.Key) || (kv.Key == "VA_BakeParent" && VAFarBakeSource.EbmTakesBakes)) s_drop.Add(kv.Key);
                foreach (var name in s_drop) { skip(name, 0f); s_inEbm.Remove(name); }
                int changed = 0;
                foreach (var kv in s_pushed)
                {
                    if (kv.Key == "VA_BakeParent" && VAFarBakeSource.EbmTakesBakes) continue;   // 1.1.426: EBM draws these far itself now
                    if (s_inEbm.TryGetValue(kv.Key, out float was) && Mathf.Approximately(was, kv.Value)) continue;
                    skip(kv.Key, kv.Value); s_inEbm[kv.Key] = kv.Value; changed++;
                }
                if (changed > 0 || s_drop.Count > 0)
                    VaPieces.Log?.LogInfo($"[DistantPieces] EBM far-building skip ({why}): {s_inEbm.Count} VA distant prefab(s) registered through EbmApi.RegisterFarSkip ({changed} set, {s_drop.Count} removed): EBM does not draw them inside their distance.");
            }
            catch (Exception ex) { VaPieces.Log?.LogWarning($"[DistantPieces] EbmApi.RegisterFarSkip threw: {ex.Message}"); }
        }

        private static class EbmFarSkipApi
        {
            private static bool s_tried; private static Action<string, float> s_api;
            internal static Action<string, float> Bind()
            {
                if (s_api != null || s_tried) return s_api;
                System.Reflection.Assembly ebm = null;
                foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                {
                    var inst = info?.Instance; if (inst == null) continue;
                    var asm = inst.GetType().Assembly;
                    if (asm.GetName().Name == "FiresEasyBakeMeshes") { ebm = asm; break; }
                }
                if (ebm == null) { if (ZNetScene.instance != null) s_tried = true; return null; }   // not loaded (yet): look again until the world is up
                s_tried = true;
                var t = ebm.GetType("FiresEasyBakeMeshes.EbmApi", false);
                var m = t?.GetMethod("RegisterFarSkip", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(string), typeof(float) }, null);
                if (m == null) { VaPieces.Log?.LogInfo("[DistantPieces] this EBM has no EbmApi.RegisterFarSkip (EBM 1.2.210+ far buildings): nothing pushed."); return null; }
                s_api = (Action<string, float>)Delegate.CreateDelegate(typeof(Action<string, float>), m);
                return s_api;
            }
        }

        // 1.1.423 ([ghost], lead 11-12; Fire 8:4x PM: "why does cores distant renderer register ANYTHING those prefabs arent even in this world",
        // "the config should not be automatically filled out"). Every source below used to register with no check: ConsMassiveTower always
        // (VATowerNearChunks.NearPrefabs yielded it unconditionally), every file name in plugins\<mod>\FapBundles\Near\ (RavensRestGuildHall ships one
        // in VAPieces), the baked / legacy set and PieceOverrides.json. Each registration made the far-building scan walk the whole world.
        // Now a prefab is registered ONLY when it resolves in this world's ZNetScene (VAPieces not installed = ConsMassiveTower never resolves = never
        // registered). Before ZNetScene is up nothing is registered; the world-load hook (Ascend, ZNetScene ready) syncs again. Whether the world
        // holds at least one instance is Core's gate behind its API (Core 0.2.1233), not repeated here. One Info line per prefab when its
        // state changes (registered / skipped), so a resync does not repeat them.
        private static readonly System.Collections.Generic.Dictionary<string, bool> s_saidState = new System.Collections.Generic.Dictionary<string, bool>();

        private static bool Resolves(ZNetScene scene, string name, string source)
        {
            // 1.1.425: ZNetScene.GetPrefab would realize a lazy stub (FAP's own GetPrefab postfix, a sync bundle load) just to answer this;
            // the named-prefab table answers without loading anything
            bool ok = VALazyBundles.PrefabKnown(scene, name);
            if (scene != null && (!s_saidState.TryGetValue(name, out bool was) || was != ok))
            {
                s_saidState[name] = ok;
                VaPieces.Log?.LogInfo(ok ? $"[DistantPieces] registered '{name}' ({source}): the prefab exists in this game"
                                         : $"[DistantPieces] skipped '{name}' ({source}): no such prefab in this game, nothing registered for it");
            }
            return ok;
        }

        internal static void SyncForWorld(string why) { SyncRegistrations(); }

        // 1.1.425 ([generator] 11-12, Core 0.2.1234): Core forwards a rule only while the prefab resolves AND the world holds >= 1 of it; a rule
        // registered before that stays pending, and in single player nothing re-triggers it. FiresCore.Services.DistantRenderer.Recheck(string
        // prefabName = null) re-checks (null = every pending rule). Bound by reflection: Core 0.2.1233 and older have no Recheck (a direct call
        // would throw MissingMethodException there), so on those it is a silent no-op after one Info line.
        private static Action<string> s_recheck;
        private static bool s_recheckBound;
        private static int s_lastRecheckSaid = -1;
        internal static void RecheckInCore(string prefabName)
        {
            if (!s_recheckBound)
            {
                s_recheckBound = true;
                try
                {
                    var m = typeof(DistantRenderer).GetMethod("Recheck", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (m != null) s_recheck = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), m);
                    VaPieces.Log?.LogInfo(m != null ? "[DistantPieces] Core has DistantRenderer.Recheck: pending far-draw rules are re-checked after a registration sync and when a lazy prefab becomes real"
                                                    : "[DistantPieces] this Core has no DistantRenderer.Recheck (Core 0.2.1234+): nothing re-checked");
                }
                catch (Exception ex) { VaPieces.Log?.LogInfo($"[DistantPieces] DistantRenderer.Recheck not bound ({ex.Message})"); }
            }
            if (s_recheck == null) return;
            try { s_recheck(prefabName); } catch (Exception ex) { VaPieces.Log?.LogDebug($"[DistantPieces] Recheck('{prefabName ?? "all"}') threw: {ex.Message}"); }
        }

        // A prefab another mod adds to ZNetScene after FAP's ZNetScene.Awake postfix would read "no such prefab" there. One more sync when the
        // local player first spawns in a world (every prefab is in by then); the skip lines above flip to "registered" if that happened.
        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned_DistantResync
        {
            private static ZNetScene s_syncedFor;
            [HarmonyLib.HarmonyPostfix]
            private static void AfterSpawn(Player __instance)
            {
                if (__instance != Player.m_localPlayer || ZNetScene.instance == null || s_syncedFor == ZNetScene.instance) return;
                s_syncedFor = ZNetScene.instance;
                try { SyncRegistrations(); } catch (Exception ex) { VaPieces.Log?.LogWarning($"[DistantPieces] resync at spawn threw: {ex.Message}"); }
            }
        }

        private static void SyncRegistrations()
        {
            DistantRenderer.Clear();
            s_pushed.Clear();
            var scene = ZNetScene.instance;

            foreach (var name in VAPieceManager.DistantRenderPrefabs)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (Resolves(scene, name, "baked / legacy list")) Reg(name, LegacyDefaultDistance);
            }

            // 1.1.377: the castle tower's impostor range; PieceOverrides below can still change it. 1.1.423: only when the prefab exists.
            foreach (var nearPrefab in VATowerNearChunks.NearPrefabs())
                if (Resolves(scene, nearPrefab, "near bundle")) Reg(nearPrefab, VATowerNearChunks.FarDistanceM);

            try
            {
                foreach (var kvp in VAFapPieceOverrides.GetAllWithDistance())
                {
                    string prefabName = kvp.Key;
                    float dist = kvp.Value;
                    if (string.IsNullOrEmpty(prefabName) || dist <= 0f) continue;
                    if (!Resolves(scene, prefabName, "PieceOverrides.json")) continue;

                    Reg(prefabName, dist);

                    if (!VAPieceManager.DistantRenderPrefabs.Contains(prefabName))
                        VAPieceManager.DistantRenderPrefabs.Add(prefabName);

                    if (scene != null)
                    {
                        var prefab = scene.GetPrefab(prefabName);
                        if (prefab != null)
                        {
                            var nv = prefab.GetComponent<ZNetView>();
                            if (nv != null && !nv.m_distant) nv.m_distant = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: DistantPieceRenderer.SyncRegistrations propagate-pass threw: {ex.Message}");
            }

            DistantRenderer.Invalidate();
            if (ZNetScene.instance != null)
            {
                RecheckInCore(null);   // 1.1.425: anything registered now that Core held back gets looked at again
                if (s_recheck != null && s_pushed.Count != s_lastRecheckSaid) { s_lastRecheckSaid = s_pushed.Count; VaPieces.Log?.LogInfo($"[DistantPieces] distant: {s_pushed.Count} rule(s) rechecked in Core (live count is Core's: its forward line names them)"); }
            }
            PushToEbm("registrations synced");
        }
    }
}
