using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Baked-blueprint removal only.
    //
    // Injected FAP pieces are removed by vanilla: Player.RemovePiece already handles
    // non-WearNTear pieces natively, and FAP duplicating that was actively harmful —
    // it bypassed the method body Infinity Hammer and World Edit Commands transpile
    // their undo recording into, and a second raycast raced vanilla's own removal
    // across both RemovePiece call sites, deleting the piece behind the target.
    //
    // Baked batches are the one case with no vanilla equivalent: the bake strips the
    // per-piece Piece component, so vanilla's raycast finds nothing to remove.
    [HarmonyPatch(typeof(Player), "RemovePiece")]
    public static class Player_RemovePiece_HammerRemoval_Patch
    {
        private const float HammerRaycastDistance = 50f;
        private const string HammerItemName = "$item_hammer";

        private static int _pieceRemovalMask = -1;

        private static int GetPieceRemovalMask()
        {
            if (_pieceRemovalMask == -1)
            {
                _pieceRemovalMask = LayerMask.GetMask(
                    "Default", "static_solid", "Default_small", "piece",
                    "piece_nonsolid", "terrain", "vehicle", "item");
            }
            return _pieceRemovalMask;
        }

        [HarmonyPrefix]
        public static bool Prefix(Player __instance, ref bool __result)
        {
            if (__instance == null) return true;
            var rightItem = __instance.GetRightItem();
            if (rightItem == null || rightItem.m_shared.m_name != HammerItemName) return true;

            var camera = GameCamera.instance;
            if (camera == null) return true;

            var cameraTransform = camera.transform;
            if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out var hit,
                                 HammerRaycastDistance, GetPieceRemovalMask()))
                return true;
            if (Vector3.Distance(hit.point, __instance.m_eye.position) >= __instance.m_maxPlaceDistance)
                return true;

            var bakeMarker = hit.collider.GetComponentInParent<VABakeBatchMarker>();
            if (bakeMarker != null && !string.IsNullOrEmpty(bakeMarker.GroupId))
            {
                __result = TryRemoveBakedBlueprint(__instance, hit, bakeMarker);
                return false;
            }

            return true;
        }

        private static bool TryRemoveBakedBlueprint(Player player, RaycastHit hit, VABakeBatchMarker marker)
        {
            if (Location.IsInsideNoBuildLocation(hit.point))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_nobuildzone", 0, null);
                return false;
            }
            if (!PrivateArea.CheckAccess(hit.point))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_privatezone", 0, null);
                return false;
            }

            int wiped = VABlueprintGroupRegistry.HandleMemberDestroyed(marker.GroupId, null);

            if (player.m_removeEffects != null)
                player.m_removeEffects.Create(hit.point, Quaternion.identity);

            var item = player.GetRightItem();
            if (item != null)
            {
                player.FaceLookDirection();
                player.m_zanim.SetTrigger(item.m_shared.m_attack.m_attackAnimation);
            }

            if (ConfigManager.Instance?.configVerboseLogging?.Value ?? false)
                Debug.Log($"{VaPieces.PluginName}: Removed baked blueprint group '{marker.GroupId}' via hammer — {wiped} sibling piece(s) wiped.");

            return true;
        }
    }
}
