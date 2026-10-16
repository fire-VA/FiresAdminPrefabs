using System.Collections;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // 1.1.254 (Fire 10-14: "lavabomb_rock1 ... be able to select 'place with persistence' and have our own code respawn them").
    // Which persistence a "Place with Persistence" placement gets:
    //   Ewp       - EWP active: FAP_persist=1, EWP's universal rule respawns it (as before; vanilla clients see it too).
    //   Fap       - VanillaFriendly OFF, EWP not active, [Persistence] FAP respawner ON: fap_respawn=1, the ZDO owner re-creates
    //               the piece 2 s after it is destroyed (TTL, damage) unless the same prefab already stands within 0.5 m.
    //               Hammer removal never respawns. Modded clients only.
    //   None      - why it can't persist here (shown disabled in the menu).
    internal enum VAPersistMode { Ewp, Fap, None }

    // 1.1.270 (Check-HarmonyClassAttributes FAIL since 1.1.251-254): method-level [HarmonyPatch] only, so FAP's per-type
    // CreateClassProcessor never applied this class - the feature was silently off. The class-level attribute turns it on.
    [HarmonyPatch]
    internal static class VAFapRespawner
    {
        internal static readonly int RespawnKey = "fap_respawn".GetStableHashCode();
        private const float RespawnDelaySeconds = 2f;
        private const float DuplicateRadius = 0.5f;
        private static bool s_hammerRemoving;

        private sealed class Host : MonoBehaviour { }
        private static Host s_host;

        // FX/VFX-tab pieces (as before) plus anything that destroys itself: a TimedDestruction, or a non-looping particle duration.
        internal static bool IsEligible(Piece piece, bool inFxTab)
        {
            if (piece == null) return false;
            if (inFxTab) return true;
            try { return VAEffectTtlOverride.HasTimedComponents(piece.gameObject); } catch { return false; }
        }

        internal static VAPersistMode Mode(out string why)
        {
            var cfg = ConfigManager.Instance;
            why = null;
            if (VAEwpSoftDep.IsActive && cfg?.configUseEwpForPersistence?.Value == true) return VAPersistMode.Ewp;
            if (cfg?.configVanillaFriendly?.Value == true) { why = "vanilla clients can't keep it: needs EWP"; return VAPersistMode.None; }
            if (cfg?.configFapRespawner?.Value != true) { why = "turn on [Persistence] FAP respawner, or use EWP"; return VAPersistMode.None; }
            return VAPersistMode.Fap;
        }

        internal static string MenuLabel(bool optedIn)
        {
            string tick = optedIn ? "» " : "    ";
            switch (Mode(out string why))
            {
                case VAPersistMode.Ewp: return tick + "Place with Persistence  <color=#888>(EWP respawn)</color>";
                case VAPersistMode.Fap: return tick + "Place with Persistence  <color=#888>(FAP respawn - modded clients only)</color>";
                default: return "    Place with Persistence  <color=#888>(" + why + ")</color>";
            }
        }

        // Called from the placement tagger with the piece's ZDO; returns true if the FAP marker was written.
        internal static bool TagIfFapMode(ZDO zdo, string prefabName)
        {
            if (zdo == null || Mode(out _) != VAPersistMode.Fap) return false;
            zdo.Set(RespawnKey, 1);
            VaPieces.Log?.LogDebug($"[FAP Respawn] tagged '{prefabName}' at {zdo.GetPosition():F1}");
            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.RemovePiece)), HarmonyPrefix]
        private static void BeforeRemove() => s_hammerRemoving = true;

        [HarmonyPatch(typeof(Player), nameof(Player.RemovePiece)), HarmonyFinalizer]
        private static void AfterRemove() => s_hammerRemoving = false;

        // Hammer removal of a WearNTear piece goes through an RPC to the owner, so clear the marker on the ZDO first.
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Remove)), HarmonyPrefix]
        private static void BeforeWntRemove(WearNTear __instance)
        {
            var zdo = __instance != null && __instance.m_nview != null ? __instance.m_nview.GetZDO() : null;
            if (zdo != null && zdo.GetInt(RespawnKey) == 1)
            {
                zdo.Set(RespawnKey, 0);
                VaPieces.Log?.LogDebug($"[FAP Respawn] hammer removal of '{Utils.GetPrefabName(__instance.gameObject)}' at {zdo.GetPosition():F1}: marker cleared, not respawning");
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO)), HarmonyPrefix]
        private static void BeforeDestroy(ZDO zdo)
        {
            if (zdo == null || zdo.GetInt(RespawnKey) != 1 || !zdo.IsOwner()) return;
            if (s_hammerRemoving) { VaPieces.Log?.LogDebug("[FAP Respawn] hammer removal, not respawning"); return; }
            if (Mode(out _) != VAPersistMode.Fap) return;   // switched off since placement: let it go
            int prefab = zdo.GetPrefab();
            Vector3 pos = zdo.GetPosition();
            Quaternion rot = zdo.GetRotation();
            bool noTtl = zdo.GetBool(VAEffectTtlKeys.OverrideHash, false);
            if (s_host == null) { var go = new GameObject("FapRespawner"); Object.DontDestroyOnLoad(go); s_host = go.AddComponent<Host>(); }
            s_host.StartCoroutine(Respawn(prefab, pos, rot, noTtl));
        }

        private static IEnumerator Respawn(int prefabHash, Vector3 pos, Quaternion rot, bool noTtl)
        {
            yield return new WaitForSeconds(RespawnDelaySeconds);
            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            if (prefab == null) { VaPieces.Log?.LogWarning($"[FAP Respawn] prefab {prefabHash} not found, nothing respawned at {pos:F1}"); yield break; }
            foreach (var c in Physics.OverlapSphere(pos, DuplicateRadius))
            {
                var nv = c != null ? c.GetComponentInParent<ZNetView>() : null;
                if (nv != null && nv.IsValid() && nv.GetZDO().GetPrefab() == prefabHash)
                {
                    VaPieces.Log?.LogDebug($"[FAP Respawn] '{prefab.name}' already stands at {pos:F1}, skipped (no duplicates)");
                    yield break;
                }
            }
            var inst = Object.Instantiate(prefab, pos, rot);
            var view = inst.GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo != null)
            {
                zdo.Set(RespawnKey, 1);
                if (noTtl) { zdo.Set(VAEffectTtlKeys.OverrideHash, true); VAEffectTtlOverride.Apply(inst); }
            }
            VaPieces.Log?.LogInfo($"[FAP Respawn] respawned '{prefab.name}' at {pos:F1}");
        }
    }
}
