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
    //   - per-piece sync helper that the Piece.Awake postfix in Ascend.cs
    //     calls to mark a freshly-placed ZDO Distant + ForceSendZDO
    //
    // Everything else (server periodic scan, client cached ZDO list,
    // Harmony patches on CreateDestroyObjects + FindSectorObjects, the
    // driver MonoBehaviour) lives in FUC's DistantRenderer service.
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

        // Per-piece sync. Called from Piece.Awake postfix when a piece is
        // placed — if the prefab is registered with FUC's DistantRenderer,
        // mark the ZDO Distant + ForceSendZDO so the placement reaches
        // far peers immediately rather than waiting on the periodic scan.
        private static readonly System.Collections.Generic.Dictionary<int, string> s_nameByHash = new System.Collections.Generic.Dictionary<int, string>();   // 1.1.402

        public static void RegisterPiece(Piece piece)
        {
            if (piece == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZDOMan.instance == null) return;
            // 1.1.402 ([visual] audit via [lead] 11-08): nothing registered = return; otherwise the prefab name comes from a per-hash cache instead of
            // a new string per piece. Same IsRegistered answer: the registry can change at any time, so it is still asked every time.
            if (!DistantRenderer.HasAny) return;

            var znv = piece.GetComponent<ZNetView>();
            if (znv == null || !znv.IsValid()) return;

            ZDO zdo = znv.GetZDO();
            if (zdo == null) return;

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

        // 1.1.411 ([generator] M22 + [visual] 11-08): EBM's far buildings (EBM 1.2.210+) skip the pieces this VA distant method already draws.
        // EBM cannot read Core, so FAP pushes the same prefab + distance list through EbmApi.SetDistantDrawn / ClearDistantDrawn, by name
        // (no compile reference; an EBM without them = nothing pushed). Pushed after every sync and once more when the world's ZNetScene is up
        // (EBM may load after FAP's Awake).
        private static readonly System.Collections.Generic.Dictionary<string, float> s_pushed = new System.Collections.Generic.Dictionary<string, float>();

        private static void Reg(string name, float dist)
        {
            DistantRenderer.Register(name, dist);
            s_pushed[name] = dist;   // the last distance wins, as in DistantRenderer
        }

        internal static void PushToEbm(string why)
        {
            var api = EbmDistantApi.Bind();
            if (api == null) return;
            try
            {
                api.Value.clear();
                foreach (var kv in s_pushed) api.Value.set(kv.Key, kv.Value);
                VaPieces.Log?.LogInfo($"[DistantPieces] {s_pushed.Count} VA distant prefab(s) pushed to EBM's far buildings (EbmApi.SetDistantDrawn, {why}): EBM skips them within their distance.");
            }
            catch (Exception ex) { VaPieces.Log?.LogWarning($"[DistantPieces] pushing to EbmApi threw: {ex.Message}"); }
        }

        private static class EbmDistantApi
        {
            private static bool s_tried; private static (Action clear, Action<string, float> set)? s_api;
            internal static (Action clear, Action<string, float> set)? Bind()
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
                var mSet = t?.GetMethod("SetDistantDrawn", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(string), typeof(float) }, null);
                var mClear = t?.GetMethod("ClearDistantDrawn", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mSet == null || mClear == null) { VaPieces.Log?.LogInfo("[DistantPieces] this EBM has no EbmApi.SetDistantDrawn / ClearDistantDrawn (EBM 1.2.210+): nothing pushed."); return null; }
                s_api = ((Action)Delegate.CreateDelegate(typeof(Action), mClear), (Action<string, float>)Delegate.CreateDelegate(typeof(Action<string, float>), mSet));
                return s_api;
            }
        }

        private static void SyncRegistrations()
        {
            DistantRenderer.Clear();
            s_pushed.Clear();

            foreach (var name in VAPieceManager.DistantRenderPrefabs)
            {
                if (string.IsNullOrEmpty(name)) continue;
                Reg(name, LegacyDefaultDistance);
            }

            // 1.1.377: the castle tower's impostor range; PieceOverrides below can still change it. In code, not PieceOverrides.json, so it ships.
            foreach (var nearPrefab in VATowerNearChunks.NearPrefabs()) Reg(nearPrefab, VATowerNearChunks.FarDistanceM);

            try
            {
                var scene = ZNetScene.instance;
                foreach (var kvp in VAFapPieceOverrides.GetAllWithDistance())
                {
                    string prefabName = kvp.Key;
                    float dist = kvp.Value;
                    if (string.IsNullOrEmpty(prefabName) || dist <= 0f) continue;

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
            PushToEbm("registrations synced");
        }
    }
}
