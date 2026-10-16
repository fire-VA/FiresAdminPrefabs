using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // 1.1.239 (Fire 10-13 hand test: with a dungeon room or location on the Hammer, right-click to reopen the menu and "the hammer
    // ghost takes up our whole screen" for a second). The placement ghost of a very big piece is drawn on and around the player
    // while the menu opens. Two rules, applied after vanilla positions the ghost each frame:
    //   - (1.1.239 also hid it while the build menu showed; that closed the menu, so 1.1.240 dropped it);
    //   - a ghost bigger than LargeRadius whose bounds contain the camera is hidden until it is aimed clear of the camera.
    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    internal static class VAPlacementGhostGuard
    {
        private const float LargeRadius = 8f;

        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        private static GameObject s_measured;
        private static float s_radius;
        private static Vector3 s_localCenter;
        private static bool s_hiddenByUs;

        // 1.1.241 (Fire, FAP 1.1.240: RMB to reopen the menu "still causes the ghost preview to raycast on the player or the
        // menu", and FDM room ghosts don't hold Infinity Hammer's Keypad-0 freeze). A ghost's own colliders must sit on the
        // ghost's layer, which the placement ray ignores. FDM's room pieces build their geometry in Start(), AFTER vanilla set
        // the ghost's layers, so the room's colliders stay on world layers: the placement ray hits the ghost itself and drags
        // it onto the camera, every frame, freeze or not. Before vanilla aims the ghost, every collider under it is moved to
        // the ghost root's layer (checked again while the hierarchy is still growing, then left alone).
        private static GameObject s_layered;
        private static int s_layeredColliders = -1;
        private static float s_nextLayerCheck;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            // 1.1.264: always on (Fire 10-15: fixes are not toggles); the "Placement ghost guard" key is gone.
            GameObject ghost;
            try { ghost = Ghost(__instance); } catch { return; }
            if (ghost == null) return;
            if (ghost != s_layered) { s_layered = ghost; s_layeredColliders = -1; s_nextLayerCheck = 0f; }
            if (Time.unscaledTime < s_nextLayerCheck) return;
            s_nextLayerCheck = Time.unscaledTime + 0.25f;
            var colliders = ghost.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == s_layeredColliders) { s_nextLayerCheck = Time.unscaledTime + 2f; return; }
            s_layeredColliders = colliders.Length;
            int ghostLayer = LayerMask.NameToLayer("ghost");
            int layer = ghostLayer >= 0 ? ghostLayer : ghost.layer, moved = 0;
            foreach (var c in colliders)
                if (c != null && c.gameObject.layer != layer) { c.gameObject.layer = layer; moved++; }
            if (moved > 0) VaPieces.Log?.LogDebug($"[FAP] placement ghost '{ghost.name}': {moved} collider(s) moved to the ghost layer {LayerMask.LayerToName(layer)} (built after the ghost was set up)");
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            GameObject ghost;
            try { ghost = Ghost(__instance); } catch { return; }
            if (ghost == null) { s_hiddenByUs = false; return; }
            // 1.1.243 (the lead: on the RMB frame the menu is not visible yet, the guard hid the ghost, and vanilla silently dropped
            // the menu): the ghost is NEVER hidden any more. The collider-layer prefix above stops the self-hit that filled the
            // screen. This postfix only puts back a ghost an older run hid.
            if (s_hiddenByUs && !ghost.activeSelf) ghost.SetActive(true);
            s_hiddenByUs = false;
            return;
#pragma warning disable CS0162   // the 1.1.239-1.1.242 hiding below is kept for reference only
            // 1.1.240 (the lead: Fire's menu "won't open" on 1.1.239): a live off-switch for the A/B. Off puts back anything we hid.
            if (false)   // 1.1.264: the key is gone; kept for reference
            {
                if (s_hiddenByUs && !ghost.activeSelf) ghost.SetActive(true);
                s_hiddenByUs = false;
                return;
            }

            // 1.1.240 (Fire 21:08, "you're hiding the MENU"): the 1.1.239 rule "hide the ghost while the build menu shows" is
            // gone. Vanilla reads the ghost's active state while the menu is up, and a hidden ghost there shut the menu. With
            // the menu up nothing is touched at all.
            bool menu = false;
            try { menu = Hud.IsPieceSelectionVisible(); } catch { }
            if (menu)
            {
                if (s_hiddenByUs && !ghost.activeSelf) ghost.SetActive(true);
                s_hiddenByUs = false;
                return;
            }
            bool hide = false;
            {
                Measure(ghost);
                var cam = Utils.GetMainCamera();
                if (s_radius > LargeRadius && cam != null)
                {
                    Vector3 center = ghost.transform.TransformPoint(s_localCenter);
                    hide = (cam.transform.position - center).sqrMagnitude < s_radius * s_radius;
                }
            }

            if (hide && ghost.activeSelf) { ghost.SetActive(false); s_hiddenByUs = true; }
            else if (!hide && s_hiddenByUs && !ghost.activeSelf) { ghost.SetActive(true); s_hiddenByUs = false; }
        }

        // Once per ghost object: the radius of its renderers' bounds (the ghost is rebuilt when the selected piece changes).
        private static void Measure(GameObject ghost)
        {
            if (ghost == s_measured && s_radius > 0f) return;   // an inactive ghost has empty bounds: measure again once it shows
            s_measured = ghost;
            s_radius = 0f;
            s_localCenter = Vector3.zero;
            var renderers = ghost.GetComponentsInChildren<Renderer>(true);
            // 1.1.243 ([seasons] + the lead: menu won't open with the guard ON + FDM 0.1.160): only renderers that draw count. An
            // inactive or disabled renderer reports empty bounds at the world origin, and encapsulating that point made the
            // radius span the world (camera "inside" the ghost, ghost hidden). Zero-size bounds are skipped too.
            bool any = false;
            var b = new Bounds();
            foreach (var r in renderers)
            {
                if (r == null || r is ParticleSystemRenderer || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var rb = r.bounds;
                if (rb.size.sqrMagnitude < 1e-6f) continue;
                if (!any) { b = rb; any = true; } else b.Encapsulate(rb);
            }
            if (!any) return;
            s_radius = b.extents.magnitude;
            if (s_radius > 500f) { s_radius = 0f; return; }   // implausible for one placement ghost: never hide on it
            s_localCenter = ghost.transform.InverseTransformPoint(b.center);
        }
    }
}
