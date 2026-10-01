using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Disables every MeshCollider in a bake's subtree when the player is
    // far from the bake's AABB, re-enables on approach. A multi-part bake
    // ships ~25M static tris of collider; cutting them when the player
    // can't physically interact saves frame budget on megabases.
    //
    // Distance check uses Bounds.ClosestPoint against an AABB built from
    // every renderer+collider in the subtree, NOT the parent transform —
    // tall bakes (towers) have origin at the base while the player can be
    // standing 30m up, well inside the geometry.
    //
    // Templates (off-world bundle prefab originators) bypass entirely so
    // clones don't inherit a disabled-collider state through Instantiate.
    public class VABakeColliderToggle : MonoBehaviour
    {
        public float ColliderActiveDistance = 150f;
        public float CheckIntervalSec = 0.5f;

        private const float BoundsRefreshIntervalSec = 5f;
        private const int CamRefreshEveryNChecks = 60;

        private MeshCollider[] _colliders;
        private float _nextCheckAt;
        private bool _currentlyActive = true;

        private Bounds _bounds;
        private bool _boundsBuilt;
        private float _nextBoundsAt;

        private Transform _camT;
        private int _camRefreshCountdown;

        private void Start()
        {
            if (VABakeTemplateRegistry.IsTemplate(gameObject)) return;

            RebuildColliderCacheAndBounds();

            // Explicit reset — clones can inherit .enabled=false from a
            // template baked under an older session; the state-comparison
            // guard in Update would otherwise lock them at disabled.
            for (int i = 0; i < _colliders.Length; i++)
            {
                var c = _colliders[i];
                if (c != null) c.enabled = true;
            }
            _currentlyActive = true;

            _nextCheckAt = Time.unscaledTime + CheckIntervalSec;
            _nextBoundsAt = Time.unscaledTime + BoundsRefreshIntervalSec;
        }

        private void Update()
        {
            if (VABakeTemplateRegistry.IsTemplate(gameObject)) return;

            if (Time.unscaledTime < _nextCheckAt) return;
            _nextCheckAt = Time.unscaledTime + CheckIntervalSec;

            if (Time.unscaledTime >= _nextBoundsAt)
            {
                RebuildColliderCacheAndBounds();
                _nextBoundsAt = Time.unscaledTime + BoundsRefreshIntervalSec;
            }

            if (_colliders == null || _colliders.Length == 0) return;
            if (!_boundsBuilt) return;

            if (_camT == null || _camRefreshCountdown <= 0)
            {
                var cam = Camera.main;
                _camT = cam != null ? cam.transform : null;
                _camRefreshCountdown = CamRefreshEveryNChecks;
            }
            _camRefreshCountdown--;
            if (_camT == null) return;

            Vector3 closest = _bounds.ClosestPoint(_camT.position);
            float sqrDist = (_camT.position - closest).sqrMagnitude;
            bool shouldBeActive = sqrDist <= (ColliderActiveDistance * ColliderActiveDistance);
            if (shouldBeActive == _currentlyActive) return;

            for (int i = 0; i < _colliders.Length; i++)
            {
                var c = _colliders[i];
                if (c == null) continue;
                c.enabled = shouldBeActive;
            }
            _currentlyActive = shouldBeActive;
        }

        private void RebuildColliderCacheAndBounds()
        {
            _colliders = GetComponentsInChildren<MeshCollider>(includeInactive: true);

            bool any = false;
            Bounds b = default;
            var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            for (int i = 0; i < _colliders.Length; i++)
            {
                var c = _colliders[i];
                if (c == null) continue;
                var cb = c.bounds;
                if (!any) { b = cb; any = true; }
                else b.Encapsulate(cb);
            }

            _bounds = b;
            _boundsBuilt = any;
        }
    }
}
