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
        public static void RegisterPiece(Piece piece)
        {
            if (piece == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZDOMan.instance == null) return;

            string prefabName = piece.gameObject.name;
            if (prefabName.EndsWith(CloneSuffix))
                prefabName = prefabName.Substring(0, prefabName.Length - CloneSuffix.Length).TrimEnd();

            if (!DistantRenderer.IsRegistered(prefabName)) return;

            var znv = piece.GetComponent<ZNetView>();
            if (znv == null || !znv.IsValid()) return;

            ZDO zdo = znv.GetZDO();
            if (zdo == null) return;

            if (!zdo.Distant) zdo.SetDistant(true);
            ZDOMan.instance.ForceSendZDO(zdo.m_uid);
        }

        public static void UnregisterPiece(ZNetView znv) { }

        private static void OnOverridesChanged() => SyncRegistrations();

        private static void SyncRegistrations()
        {
            DistantRenderer.Clear();

            foreach (var name in VAPieceManager.DistantRenderPrefabs)
            {
                if (string.IsNullOrEmpty(name)) continue;
                DistantRenderer.Register(name, LegacyDefaultDistance);
            }

            try
            {
                var scene = ZNetScene.instance;
                foreach (var kvp in VAFapPieceOverrides.GetAllWithDistance())
                {
                    string prefabName = kvp.Key;
                    float dist = kvp.Value;
                    if (string.IsNullOrEmpty(prefabName) || dist <= 0f) continue;

                    DistantRenderer.Register(prefabName, dist);

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
        }
    }
}
