using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Stamped on every piece spawned by a blueprint placement. The WearNTear
    // SetHealthVisual prefix checks for this component and short-circuits vanilla's
    // m_worn.SetActive call when the wear-state children have been pruned.
    public class VABlueprintPieceMarker : MonoBehaviour
    {
    }
}
