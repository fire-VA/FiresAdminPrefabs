using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.426 ([ghost], [visual] G2 of the far plan, lead approved): EBM draws FAP's baked blueprints (VA_BakeParent placements) far. EBM never
    // compiles against FAP; FAP hands it two lookups through EbmApi, by reflection, once EBM is loaded:
    //   EbmApi.RegisterFarBakeSource(Func<string, Mesh[]> meshesByCacheName, Func<string, Material[][]> materialsByCacheName)
    //   EbmApi.RaiseFarBakeReady(string cacheName)
    // Key = the ZDO's "va_bp_cache_name" (VABakeParentPrefab.ZdoKeyCacheName). A bake is several parts (VAFapBlueprintBakeCache.CachedPart:
    // one combined mesh, a submesh per material, per zone bucket and LOD level), all in blueprint-local space = the bake parent's local space,
    // scale 1. The two arrays are index-aligned, one entry per part, and hold ONLY the coarsest LOD level of each zone (a far tier).
    // Null = no live bake in memory. On the first miss per name FAP queues ONE rebake (the existing baker coroutine) and raises Ready when it
    // lands; Set / SetMaterialized / Remove on the cache raise Ready too, so EBM re-reads. Main thread, no instantiation, no allocation on a hit
    // after the first (the arrays are cached per name until the bake changes).
    internal static class VAFarBakeSource
    {
        private static Action<string> s_raise;
        private static bool s_registered, s_tried;
        internal static bool EbmTakesBakes { get; private set; }   // true = VA_BakeParent stops going through RegisterFarSkip

        private static readonly Dictionary<string, Mesh[]> s_meshes = new Dictionary<string, Mesh[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Material[][]> s_mats = new Dictionary<string, Material[][]>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_rebakeAsked = new HashSet<string>(StringComparer.Ordinal);
        private static int s_served, s_misses, s_rebakes;

        // Called where FAP pushes RegisterFarSkip (world loading); harmless to call again.
        internal static void TryRegister()
        {
            if (s_registered || s_tried && ZNetScene.instance != null) return;
            System.Reflection.Assembly ebm = null;
            foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
            {
                var inst = info?.Instance; if (inst == null) continue;
                var asm = inst.GetType().Assembly;
                if (asm.GetName().Name == "FiresEasyBakeMeshes") { ebm = asm; break; }
            }
            if (ebm == null) { if (ZNetScene.instance != null) s_tried = true; return; }
            s_tried = true;
            try
            {
                var t = ebm.GetType("FiresEasyBakeMeshes.EbmApi", false);
                const System.Reflection.BindingFlags pub = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                var reg = t?.GetMethod("RegisterFarBakeSource", pub, null, new[] { typeof(Func<string, Mesh[]>), typeof(Func<string, Material[][]>) }, null);
                var raise = t?.GetMethod("RaiseFarBakeReady", pub, null, new[] { typeof(string) }, null);
                if (reg == null || raise == null)
                {
                    VaPieces.Log?.LogInfo("[FarBakes] this EBM has no EbmApi.RegisterFarBakeSource / RaiseFarBakeReady (EBM 1.2.341+): baked blueprints are not drawn far by EBM; VA_BakeParent stays on RegisterFarSkip");
                    return;
                }
                s_raise = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), raise);
                reg.Invoke(null, new object[] { (Func<string, Mesh[]>)MeshesFor, (Func<string, Material[][]>)MaterialsFor });
                s_registered = true; EbmTakesBakes = true;
                VaPieces.Log?.LogInfo("[FarBakes] registered with EbmApi.RegisterFarBakeSource: EBM draws baked blueprints (VA_BakeParent) far from FAP's bake cache (coarsest LOD per zone); VA_BakeParent leaves RegisterFarSkip");
            }
            catch (Exception ex) { VaPieces.Log?.LogWarning($"[FarBakes] EbmApi.RegisterFarBakeSource failed: {ex.Message}"); }
        }

        private static Mesh[] MeshesFor(string cacheName) => Fill(cacheName) ? s_meshes[cacheName] : null;
        private static Material[][] MaterialsFor(string cacheName) => Fill(cacheName) ? s_mats[cacheName] : null;

        private static bool Fill(string cacheName)
        {
            if (string.IsNullOrEmpty(cacheName)) return false;
            if (s_meshes.TryGetValue(cacheName, out var have))
            {
                bool alive = true;
                for (int i = 0; i < have.Length; i++) if (have[i] == null) { alive = false; break; }
                if (alive) return true;
                s_meshes.Remove(cacheName); s_mats.Remove(cacheName);
            }
            if (!VAFapBlueprintBakeCache.TryGetMaterializedAlive(cacheName, out var bake))
            {
                s_misses++;
                QueueRebakeOnce(cacheName);
                return false;
            }
            // coarsest LOD per zone bucket
            var coarsest = new Dictionary<VABakeZoneKey, int>();
            foreach (var p in bake.Parts)
                if (p != null && p.CombinedMesh != null && (!coarsest.TryGetValue(p.ZoneKey, out int lod) || p.LodLevel > lod)) coarsest[p.ZoneKey] = p.LodLevel;
            var meshes = new List<Mesh>(); var mats = new List<Material[]>();
            foreach (var p in bake.Parts)
            {
                if (p == null || p.CombinedMesh == null || coarsest[p.ZoneKey] != p.LodLevel) continue;
                meshes.Add(p.CombinedMesh); mats.Add(p.Materials ?? Array.Empty<Material>());
            }
            if (meshes.Count == 0) return false;
            s_meshes[cacheName] = meshes.ToArray(); s_mats[cacheName] = mats.ToArray();
            s_served++;
            if (s_served == 1) VaPieces.Log?.LogInfo($"[FarBakes] first far bake served to EBM: '{cacheName}', {meshes.Count} part(s) at the coarsest LOD of {coarsest.Count} zone(s); further ones are Debug lines");
            else VaPieces.Log?.LogDebug($"[FarBakes] served '{cacheName}': {meshes.Count} part(s), {coarsest.Count} zone(s) ({s_served} served, {s_misses} misses, {s_rebakes} rebakes this session)");
            return true;
        }

        // A far-only placement never loads its bake parent, so nothing else would rebake it after a restart.
        private static void QueueRebakeOnce(string cacheName)
        {
            if (!s_rebakeAsked.Add(cacheName)) return;
            if (VaPieces.IsDedicatedServer || VaPieces.Instance == null || ZNetScene.instance == null) return;
            if (!VABlueprintHammerIntegration.TryGetBlueprintBySourceFileName(cacheName, out var blueprint) || blueprint == null)
            {
                VaPieces.Log?.LogDebug($"[FarBakes] '{cacheName}' is not a registered blueprint: no far bake for it");
                return;
            }
            s_rebakes++;
            VaPieces.Log?.LogDebug($"[FarBakes] '{cacheName}' has no bake in memory: one rebake queued for EBM's far tier");
            VaPieces.Instance.StartCoroutine(VAFapBlueprintMeshBaker.BakeFromBlueprintPrefabsCoroutine(blueprint, ZNetScene.instance, cacheName));
            // the bake ends in VAFapBlueprintBakeCache.Set, which calls BakeChanged -> RaiseFarBakeReady
        }

        // VAFapBlueprintBakeCache.Set / SetMaterialized / Remove
        internal static void BakeChanged(string cacheName)
        {
            if (string.IsNullOrEmpty(cacheName)) return;
            s_meshes.Remove(cacheName); s_mats.Remove(cacheName);
            if (s_raise == null) return;
            try { s_raise(cacheName); } catch (Exception ex) { VaPieces.Log?.LogDebug($"[FarBakes] RaiseFarBakeReady('{cacheName}') threw: {ex.Message}"); }
        }
    }
}
