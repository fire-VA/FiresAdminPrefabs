using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Stamped on every piece spawned by a blueprint placement. The WearNTear
    // SetHealthVisual prefix checks for this component and short-circuits vanilla's
    // m_worn.SetActive call when the wear-state children have been pruned.
    public class VABlueprintPieceMarker : MonoBehaviour
    {
        // 1.1.404 ([fgn] WearNTear audit, via [lead] 11-08): live markers in the scene. It sits on the WearNTear's own GameObject, so a
        // WearNTear that runs has a woken marker if it has one; with none alive the SetHealthVisual prefix skips its GetComponent.
        internal static int LiveCount;
        private void Awake() => LiveCount++;
        private void OnDestroy() { if (LiveCount > 0) LiveCount--; }
    }
}
