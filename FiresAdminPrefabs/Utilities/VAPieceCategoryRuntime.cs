using System;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Piece.PieceCategory.Max read from the LOADED game assembly, not baked in at compile time.
    //
    // Enum members are constants, so `(int)Piece.PieceCategory.Max` is inlined into our DLL by the
    // compiler and keeps whatever value the assembly we built against had. Valheim moves this enum:
    // the May build had Feasts=5, Food=6, Meads=7, Max=8; the 2026-09-25 patch inserted DeepNorth at
    // 5, pushing the rest up to Meads=8 and Max=9. Every value at or after an insertion point means a
    // DIFFERENT category afterwards.
    //
    // A FAP built before such a patch therefore treats the old Max as "hidden" when the game now
    // treats that number as a real category, and bases its custom tab numbering one slot low — which
    // is how a patch empties every FAP build tab while vanilla's keep working.
    public static class VAPieceCategoryRuntime
    {
        private static int _max = -1;

        // The first value past vanilla's categories, as the running game defines it.
        public static int Max
        {
            get
            {
                if (_max >= 0) return _max;
                try
                {
                    _max = (int)Enum.Parse(typeof(Piece.PieceCategory), nameof(Piece.PieceCategory.Max));
                    int compiled = (int)Piece.PieceCategory.Max;
                    if (_max != compiled)
                    {
                        Debug.LogWarning(
                            $"{VaPieces.PluginName}: Piece.PieceCategory.Max is {_max} in the running game but {compiled} was compiled in — "
                            + "Valheim changed its build categories. Using the runtime value; rebuild FAP against the current assembly when convenient.");
                    }
                }
                catch (Exception ex)
                {
                    _max = (int)Piece.PieceCategory.Max;
                    Debug.LogWarning($"{VaPieces.PluginName}: could not read Piece.PieceCategory.Max at runtime ({ex.Message}); falling back to the compiled value {_max}.");
                }
                return _max;
            }
        }

        // The sentinel FAP stamps on a piece it wants vanilla to leave out of every category list.
        public static Piece.PieceCategory Hidden => (Piece.PieceCategory)Max;

        public static bool IsHidden(Piece.PieceCategory category) => (int)category == Max;
    }
}
