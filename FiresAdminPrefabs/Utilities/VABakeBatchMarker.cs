using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Data carrier attached to each per-material batch GameObject produced by the
    // blueprint mesh baker. Lets a raycast hit on the combined collider recover
    // the blueprint group identity so the destroy cascade can fire.
    public class VABakeBatchMarker : MonoBehaviour
    {
        public string GroupId;
    }
}
