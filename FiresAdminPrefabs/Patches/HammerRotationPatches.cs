using HarmonyLib;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // RandomPieceRotation.Awake replaces the rotation the player aimed with position-
    // seeded noise (meant for world-generated props). It is suppressed only inside the
    // two vanilla placement entry points, so generated content keeps its variety.
    // The depth counter, rather than a bool, survives nested placement calls.

    internal static class VARandomRotationSuppress
    {
        public static int Depth;
    }

    [HarmonyPatch(typeof(RandomPieceRotation), "Awake")]
    public static class RandomPieceRotation_Awake_VASuppress_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!ConfigManager.Instance.configHammerSuppressRandomRotation.Value) return true;
            return VARandomRotationSuppress.Depth <= 0;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class Player_PlacePiece_VASuppress_Patch
    {
        [HarmonyPrefix]  public static void Prefix()  => VARandomRotationSuppress.Depth++;
        [HarmonyPostfix] public static void Postfix() => VARandomRotationSuppress.Depth--;
    }

    [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
    public static class Player_SetupPlacementGhost_VASuppress_Patch
    {
        [HarmonyPrefix]  public static void Prefix()  => VARandomRotationSuppress.Depth++;
        [HarmonyPostfix] public static void Postfix() => VARandomRotationSuppress.Depth--;
    }
}
