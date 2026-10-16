using HarmonyLib;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // 1.1.245 (a FAP user via Fire: "I have unlock messages disabled but they ain't stopping"). FAP puts every prefab on the
    // Hammer, so vanilla's Player.AddKnownPiece queues a "new piece" popup (MessageHud.QueueUnlockMsg) for each one the player
    // comes to know. With [General] "Show unlock messages for admin pieces" off (default), a FAP-added piece becomes known
    // exactly as vanilla does it (known list, recent pieces), only its popup is skipped. Vanilla pieces and every other
    // message are untouched, and Server Devcommands' own switch keeps working as before (no dependency on it).
    [HarmonyPatch]
    internal static class VAQuietAdminUnlocks
    {
        private static bool s_silent;
        private static int s_silenced;

        [HarmonyPatch(typeof(Player), "AddKnownPiece"), HarmonyPrefix]
        private static void Prefix(Piece piece)
        {
            s_silent = false;
            if (piece == null || (ConfigManager.Instance?.configShowAdminPieceUnlocks?.Value ?? false)) return;
            if (VAVanillaPieceManager.WasInjectedByUs(piece.gameObject)) s_silent = true;
        }

        [HarmonyPatch(typeof(Player), "AddKnownPiece"), HarmonyPostfix]
        private static void Postfix() => s_silent = false;

        [HarmonyPatch(typeof(MessageHud), nameof(MessageHud.QueueUnlockMsg)), HarmonyPrefix]
        private static bool QueueUnlockMsg()
        {
            if (!s_silent) return true;
            s_silenced++;
            if (s_silenced == 1 || s_silenced % 100 == 0)
                VaPieces.Log?.LogDebug($"[FAP] {s_silenced} unlock popup(s) for FAP-added pieces skipped ([General] Show unlock messages for admin pieces = off)");
            return false;
        }
    }
}
