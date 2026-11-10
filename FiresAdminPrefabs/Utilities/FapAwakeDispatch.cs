using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.407 ([generator] piece-create audit row 4, Tools\AUDIT_PieceAwakeCost_20261108.md): FAP's ONE ZNetView.Awake postfix and ONE
    // Piece.Awake postfix. Before, ten ZNetView.Awake postfixes and three Piece.Awake postfixes each paid their own Harmony call, try
    // frame, IsValid / GetZDO and, for two of them, a GetComponent on every networked object created. Now:
    //   - one call per Awake; the feature bodies are unchanged (each feature's own Run / NViewAwake, the old postfix bodies);
    //   - the two "is this prefab that kind of object" answers (EventZone, LocationProxy) are prefab facts, cached per prefab hash
    //     (a ZNetView without a ZDO asks live), so those features are skipped at one dictionary lookup for every other prefab;
    //   - EBM skip: with EBM 1.2.198+ (EbmBakedPieces.Draws AND the PieceLeftBake event, both bound by presence), a piece the bake
    //     draws defers its light-state and sound-preset VISUAL setup; it runs exactly once when that piece leaves the bake
    //     (PieceLeftBake), and only for pieces FAP itself deferred (ghost / copy-tool clones are ignored). Older EBM = no deferral.
    // Left as they were: the bake-template bypass (a PREFIX that stops Awake) and the WearNTear bundle-root prune (its component can
    // be per instance, so not a prefab fact).
    internal static class FapAwakeDispatch
    {
        private const byte CapEventZone = 1, CapLocationProxy = 2;
        private static readonly Dictionary<int, byte> s_caps = new Dictionary<int, byte>();

        private static byte CapsOf(ZNetView nv, int hash)
        {
            if (hash != 0 && s_caps.TryGetValue(hash, out byte c)) return c;
            c = 0;
            if (nv.GetComponent<EventZone>() != null) c |= CapEventZone;
            if (nv.GetComponent<LocationProxy>() != null) c |= CapLocationProxy;
            if (hash != 0) s_caps[hash] = c;
            return c;
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        private static class ZNetView_Awake
        {
            [HarmonyPostfix]
            private static void Postfix(ZNetView __instance)
            {
                if (__instance == null) return;
                var zdo = __instance.IsValid() ? __instance.GetZDO() : null;
                byte caps = CapsOf(__instance, zdo != null ? zdo.GetPrefab() : 0);
                Patches.VABlueprintGroupPatches.ZNetView_Awake_VABlueprintGroupRegister_Patch.Run(__instance);
                Patches.VABlueprintGroupPatches.ZNetView_Awake_VAScaleReadback_Patch.Run(__instance);
                Patches.VAFapPieceShaderOverridePatch.Run(__instance);
                Patches.VAAreaScaleAwakePatch.Run(__instance);
                if ((caps & CapEventZone) != 0) VAEventZonePresets.NViewAwake(__instance);
                if ((caps & CapLocationProxy) != 0) Safe(() => VAFapDungeons.ZNetView_Awake_FullProxy.Run(__instance), "dungeon proxy");
                VAFapDungeons.ZNetView_Awake_Floor.Run(__instance);
                VAPlacedSpawnerBoxes.NViewAwake(__instance);
                if (Ebm.Defer(__instance.gameObject)) return;   // sound preset waits for PieceLeftBake
                VASoundPresets.Patches.NViewAwake(__instance);
            }
        }

        [HarmonyPatch(typeof(Piece), "Awake")]
        private static class Piece_Awake
        {
            [HarmonyPostfix]
            private static void Postfix(Piece __instance)
            {
                if (__instance == null) return;
                VaPieces.Piece_Awake_Patch.Run(__instance);
                Patches.Piece_Awake_VAEffectTtl_Patch.Run(__instance);
                if (Ebm.IsDeferred(__instance.gameObject)) return;   // light state waits for PieceLeftBake (ZNetView.Awake deferred it)
                VALightApplier.Piece_Awake_VALight_Patch.Run(__instance);
            }
        }

        // 1.1.409: EBM's public "the bake draws this" check, for FAP's horizon / shadow caster cull (false without EBM 1.2.198+).
        internal static bool EbmDraws(GameObject go) => Ebm.Draws(go);

        private static void Safe(Action a, string what)
        {
            try { a(); } catch (Exception ex) { VaPieces.Log?.LogWarning($"[FapAwake] {what} threw: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- EBM skip (EBM 1.2.198+ only)
        private static class Ebm
        {
            private static bool s_bound, s_on;
            private static Func<GameObject, bool> s_draws;
            private static readonly HashSet<int> s_deferred = new HashSet<int>();
            private static int s_deferredTotal, s_ranTotal;

            private static int s_lastTryFrame = -1;

            // 1.1.408 ([classes-ui] 11-08: "Could not find type named FiresEasyBakeMeshes.EbmBakedPieces"): AccessTools.TypeByName
            // ran at the first ZNetView.Awake, which can come while plugins are still loading (before EBM's assembly), and it warns on a
            // miss. Now: EBM is found among the chainloader's LOADED plugins (by assembly name, no GUID to drift), its type comes from its
            // own assembly with GetType(name, false) (no warning), and nothing latches until EBM is there or the world scene is up
            // (chainloading long done); then one Info line says which. At most one look per frame until then.
            private static System.Reflection.Assembly FindEbmAssembly()
            {
                foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                {
                    var inst = info?.Instance;
                    if (inst == null) continue;
                    var asm = inst.GetType().Assembly;
                    if (asm.GetName().Name == "FiresEasyBakeMeshes") return asm;
                }
                return null;
            }

            private static void Bind()
            {
                if (s_bound) return;
                if (s_lastTryFrame == Time.frameCount) return;
                s_lastTryFrame = Time.frameCount;
                var ebm = FindEbmAssembly();
                if (ebm == null)
                {
                    if (ZNetScene.instance == null) return;   // still loading: look again later, no latch, no log
                    s_bound = true;
                    VaPieces.Log?.LogInfo("[FapAwake] EBM skip off: EBM is not installed; light/sound set up on every piece as before.");
                    return;
                }
                s_bound = true;
                try
                {
                    var t = ebm.GetType("FiresEasyBakeMeshes.EbmBakedPieces", false);
                    var m = t?.GetMethod("Draws", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                    var ev = t?.GetEvent("PieceLeftBake", BindingFlags.Public | BindingFlags.Static);
                    if (m == null || m.ReturnType != typeof(bool) || ev == null || ev.EventHandlerType != typeof(Action<GameObject>))
                    {
                        VaPieces.Log?.LogInfo($"[FapAwake] EBM skip off: this EBM has {(m != null ? "Draws" : "no Draws")} and {(ev != null ? "PieceLeftBake" : "no PieceLeftBake")} (both needed, EBM 1.2.198+): light/sound set up on every piece as before.");
                        return;
                    }
                    s_draws = (Func<GameObject, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, bool>), m);
                    ev.AddEventHandler(null, new Action<GameObject>(OnLeftBake));
                    s_on = true;
                    VaPieces.Log?.LogInfo("[FapAwake] EBM skip on: pieces the bake draws defer their light / sound visual setup until EBM's PieceLeftBake.");
                }
                catch (Exception ex) { s_draws = null; s_on = false; VaPieces.Log?.LogInfo($"[FapAwake] EBM skip off (bind threw: {ex.Message})."); }
            }

            // ZNetView.Awake: true = deferred (the bake draws it); remembered so Piece.Awake and PieceLeftBake know.
            internal static bool Defer(GameObject go)
            {
                Bind();
                if (!s_on || go == null) return false;
                bool draws;
                try { draws = s_draws(go); } catch { return false; }
                if (!draws) return false;
                s_deferred.Add(go.GetInstanceID());
                s_deferredTotal++;
                return true;
            }

            internal static bool IsDeferred(GameObject go) => s_on && go != null && s_deferred.Contains(go.GetInstanceID());

            // 1.1.409 ([visual] 11-08: "skip any renderer whose root is EbmBakedPieces.Draws(go) == true"): the bake draws this object.
            internal static bool Draws(GameObject go)
            {
                Bind();
                if (!s_on || go == null) return false;
                try { return s_draws(go); } catch { return false; }
            }

            private static void OnLeftBake(GameObject go)
            {
                try
                {
                    if (go == null || !s_deferred.Remove(go.GetInstanceID())) return;   // not one FAP deferred (ghost / copy clone): ignore
                    var nv = go.GetComponent<ZNetView>();
                    if (nv != null) VASoundPresets.Patches.NViewAwake(nv);
                    var piece = go.GetComponent<Piece>();
                    if (piece != null) VALightApplier.Piece_Awake_VALight_Patch.Run(piece);
                    s_ranTotal++;
                    if (s_ranTotal == 1 || s_ranTotal % 500 == 0)
                        VaPieces.Log?.LogDebug($"[FapAwake] EBM hand-back: {s_ranTotal} deferred piece(s) set up on leaving the bake ({s_deferredTotal} deferred so far, {s_deferred.Count} still baked)");
                }
                catch (Exception ex) { VaPieces.Log?.LogWarning($"[FapAwake] PieceLeftBake setup threw: {ex.Message}"); }
            }
        }
    }
}
