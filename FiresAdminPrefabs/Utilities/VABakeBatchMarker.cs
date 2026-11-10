using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Data carrier attached to each per-material batch GameObject produced by the
    // blueprint mesh baker. Lets a raycast hit on the combined collider recover
    // the blueprint group identity so the destroy cascade can fire.
    public class VABakeBatchMarker : MonoBehaviour
    {
        public string GroupId;

        // 1.1.404 ([fgn] WearNTear audit, via [lead] 11-08): live markers in the scene; with none alive the Highlight prefix skips its GetComponent.
        internal static int LiveCount;
        private void Awake() => LiveCount++;
        private void OnDestroy() { if (LiveCount > 0) LiveCount--; }
    }
}
