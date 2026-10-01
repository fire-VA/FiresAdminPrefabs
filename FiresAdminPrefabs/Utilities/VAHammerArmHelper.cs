using System;

namespace VerdantsAscent.Utilities
{
    // Shared "close all menus + arm the hammer with this prefab" helper used
    // by preset config UIs after Save / Apply, so the placement ghost shows
    // up immediately without forcing the player to reopen the hammer.
    public static class VAHammerArmHelper
    {
        public static bool ArmAndClose(string prefabName)
        {
            CloseMenus();

            if (string.IsNullOrEmpty(prefabName)) return false;
            var player = Player.m_localPlayer;
            if (player == null) return false;
            var pieces = player.m_buildPieces;
            if (pieces == null || pieces.m_pieces == null) return false;

            for (int i = 0; i < pieces.m_pieces.Count; i++)
            {
                var go = pieces.m_pieces[i];
                if (go == null) continue;
                if (!string.Equals(go.name, prefabName, StringComparison.Ordinal))
                    continue;

                var piece = go.GetComponent<Piece>();
                if (piece == null) return false;
                return player.SetSelectedPiece(piece);
            }
            return false;
        }

        // Right-click menu MUST close before HidePieceSelection — the
        // Hud_HidePieceSelection gate blocks vanilla's hide call while the
        // right-click menu is open, so the inverse order leaks the hammer panel.
        public static void CloseMenus()
        {
            if (VARightClickMenu.IsOpen)
                VARightClickMenu.Close();

            Hud.HidePieceSelection();
        }
    }
}
