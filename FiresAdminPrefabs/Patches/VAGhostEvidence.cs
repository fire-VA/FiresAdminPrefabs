using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // 1.1.251 (Fire 10-14 in JotunnRepro: Morkhalla_StatuePieceHorn2 selected, name + icon shown, but NO ghost; "pieces must land at
    // the correct position"). Point-of-use evidence, Debug level, read-only:
    //   [GhostEvidence] select  once per new ghost: the piece, FAP-injected or not, ghost active, renderers (enabled/total), meshes
    //                           missing, combined bounds, layers, scale, error shaders.
    //   [GhostEvidence] aim     0.5 s after the select: ghost position vs the camera's placement-ray hit, still active, renderers.
    //   [GhostEvidence] place   on every PlacePiece by the local player: requested pos/rot vs the ghost's, then 1 s later the placed
    //                           instance and its ZDO position, or "NOT FOUND".
    // 1.1.270 (Check-HarmonyClassAttributes FAIL since 1.1.251-254): method-level [HarmonyPatch] only, so FAP's per-type
    // CreateClassProcessor never applied this class - the feature was silently off. The class-level attribute turns it on.
    [HarmonyPatch]
    internal static class VAGhostEvidence
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRayMask = AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        private static GameObject s_lastGhost;
        private sealed class Host : MonoBehaviour { }
        private static Host s_host;

        private static Host H()
        {
            if (s_host == null) { var go = new GameObject("FapGhostEvidence"); Object.DontDestroyOnLoad(go); s_host = go.AddComponent<Host>(); }
            return s_host;
        }

        private static bool Injected(string prefabName)
        {
            try { return VAVanillaPieceManager.WasInjectedByUs(prefabName); } catch { return false; }
        }

        internal static string Describe(GameObject ghost)
        {
            if (ghost == null) return "ghost NULL";
            var rs = ghost.GetComponentsInChildren<Renderer>(true).Where(r => r != null && !(r is ParticleSystemRenderer)).ToArray();
            int enabled = rs.Count(r => r.enabled && r.gameObject.activeInHierarchy);
            int noMesh = ghost.GetComponentsInChildren<MeshFilter>(true).Count(f => f.sharedMesh == null)
                         + ghost.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(s => s.sharedMesh == null);
            int errShader = rs.SelectMany(r => r.sharedMaterials ?? new Material[0]).Count(m => m == null || m.shader == null || m.shader.name == "Hidden/InternalErrorShader");
            bool any = false; var b = new Bounds();
            foreach (var r in rs)
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                var rb = r.bounds; if (rb.size.sqrMagnitude < 1e-6f) continue;
                if (!any) { b = rb; any = true; } else b.Encapsulate(rb);
            }
            string layers = string.Join(",", rs.Select(r => LayerMask.LayerToName(r.gameObject.layer)).Distinct().Take(4).ToArray());
            return $"active {ghost.activeSelf}/{ghost.activeInHierarchy}, renderers {enabled}/{rs.Length} enabled, meshes missing {noMesh}, "
                 + $"null/error materials {errShader}, bounds {(any ? b.size.ToString("F2") + " at " + b.center.ToString("F1") : "EMPTY")}, "
                 + $"layers [{layers}], lossyScale {ghost.transform.lossyScale.ToString("F2")}";
        }

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterSetup(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            GameObject ghost;
            try { ghost = Ghost(__instance); } catch { return; }
            if (ghost == s_lastGhost) return;
            s_lastGhost = ghost;
            if (ghost == null) return;   // no piece selected
            string prefab = Utils.GetPrefabName(ghost);
            VaPieces.Log?.LogDebug($"[GhostEvidence] select '{prefab}' (FAP-injected {Injected(prefab)}): {Describe(ghost)}");
            H().StartCoroutine(Aim(__instance, ghost, prefab));
        }

        private static IEnumerator Aim(Player me, GameObject ghost, string prefab)
        {
            yield return new WaitForSeconds(0.5f);
            if (me == null || ghost == null || Ghost(me) != ghost) yield break;
            var cam = Utils.GetMainCamera();
            string hit = "no camera";
            if (cam != null)
            {
                int mask = 0; try { mask = PlaceRayMask(me); } catch { }
                hit = Physics.Raycast(cam.transform.position, cam.transform.forward, out var h, 50f, mask == 0 ? ~0 : mask)
                    ? $"ray hit {h.point.ToString("F1")} ('{h.collider.name}'), ghost {Vector3.Distance(h.point, ghost.transform.position):F2} m from it"
                    : "placement ray hit nothing within 50 m";
            }
            VaPieces.Log?.LogDebug($"[GhostEvidence] aim '{prefab}': ghost at {ghost.transform.position.ToString("F1")}, {hit}; {Describe(ghost)}");
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece)), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void BeforePlace(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            if (__instance != Player.m_localPlayer || piece == null) return;
            GameObject ghost = null;
            try { ghost = Ghost(__instance); } catch { }
            string g = ghost != null ? $"ghost {ghost.transform.position.ToString("F2")} rot {ghost.transform.rotation.eulerAngles.ToString("F0")}" : "no ghost";
            VaPieces.Log?.LogDebug($"[GhostEvidence] place '{piece.name}' (FAP-injected {Injected(piece.name)}): requested {pos.ToString("F2")} rot {rot.eulerAngles.ToString("F0")}; {g}");
            H().StartCoroutine(Placed(piece.name, pos, rot));
        }

        private static IEnumerator Placed(string prefab, Vector3 pos, Quaternion rot)
        {
            yield return new WaitForSeconds(1f);
            var found = Physics.OverlapSphere(pos, 4f).Select(c => c.GetComponentInParent<ZNetView>())
                .Where(v => v != null && v.IsValid() && Utils.GetPrefabName(v.gameObject) == prefab)
                .OrderBy(v => Vector3.Distance(v.transform.position, pos)).FirstOrDefault();
            if (found == null) { VaPieces.Log?.LogDebug($"[GhostEvidence] placed '{prefab}': NOT FOUND within 4 m of {pos.ToString("F2")} (no collider, or it never spawned)"); yield break; }
            var zdo = found.GetZDO();
            float dPos = Vector3.Distance(found.transform.position, pos), dRot = Quaternion.Angle(found.transform.rotation, rot);
            VaPieces.Log?.LogDebug($"[GhostEvidence] placed '{prefab}': instance {found.transform.position.ToString("F2")} ({dPos:F2} m, {dRot:F1} deg from the request), "
                + $"ZDO {(zdo != null ? zdo.GetPosition().ToString("F2") : "none")}; {Describe(found.gameObject)}");
        }
    }
}
