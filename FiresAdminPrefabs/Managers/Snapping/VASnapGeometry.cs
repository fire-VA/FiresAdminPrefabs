using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.Snapping
{
    // 1.1.379 (Fire 11-03/04): snap points for a piece with none of its own, from that piece's own geometry; no per-piece data.
    //   one point (bottom centre): machines, crafting stations + extensions, cartography table, tables
    //   ships, carts: origin
    //   bounds under 0.1 m: origin, top centre, bottom centre (bottom centre first); no bounds at all: origin
    //   otherwise: 8 corners (bottom left first), bottom centre, top centre, centre
    // 1.1.431 ([ghost], Fire 11-16, Arbby: "the ice waterfall thing" has hundreds of snap points; Fire: FAP only ADDS corners + a centre to pieces
    // that lack them): no more points along the edges (they made 100-231 points on 10-20 m pieces: AshlandsTree6 231, rock2_ice_bal 191,
    // BlackIce_Core_outer 119). The [Hammer] Snap edge point spacing key is gone. Saved snap defaults are kept by position (VASnapDefaults), so a
    // default saved on an edge point falls back to Auto.
    // Bottom left in piece space = x min, y min, z min of the bounds. A point appended last in child order would still cycle after the
    // piece's others; a piece given points here has no others, so index 0 is what the first cycle press selects. Start stays Auto.
    internal static class VASnapGeometry
    {
        private const string SnapTag = "snappoint";
        private const float TinySide = 0.1f;
        private static readonly HashSet<int> _done = new HashSet<int>();
        private static readonly HashSet<string> _madeHere = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether this prefab's points came from this rule (its saved defaults by old index mean nothing).</summary>
        internal static bool MadeByRule(string prefab) => prefab != null && _madeHere.Contains(prefab);

        internal static void Apply(GameObject prefab)
        {
            if (prefab == null || VaPieces.IsDedicatedServer || VerdantsAscent.Utilities.VALazyBundles.IsStub(prefab)) return;
            if (!_done.Add(prefab.GetInstanceID()) || HasOwnPoints(prefab)) return;
            try
            {
                var points = Points(prefab, out string why);
                for (int i = 0; i < points.Count; i++) AddPoint(prefab, points[i], $"Snap point {i + 1}");
                _madeHere.Add(prefab.name);
                VaPieces.Log?.LogDebug($"[Snap] {prefab.name}: {points.Count} point(s), {why}");
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [Snap] {prefab.name}: no snap points added: {ex.Message}"); }
        }

        // Every piece on a table: pieces vanilla or a bundle already placed on the Hammer never reach the per-prefab hook.
        internal static void ApplyToTable(PieceTable table)
        {
            if (table?.m_pieces == null) return;
            foreach (var go in table.m_pieces.ToArray()) Apply(go);
        }

        private static bool HasOwnPoints(GameObject prefab)
        {
            foreach (Transform child in prefab.transform) if (child.CompareTag(SnapTag)) return true;
            return false;
        }

        private static List<Vector3> Points(GameObject prefab, out string why)
        {
            var list = new List<Vector3>();
            bool hasBounds = TryLocalBounds(prefab, out Bounds b);
            Vector3 bottom = hasBounds ? new Vector3(b.center.x, b.min.y, b.center.z) : Vector3.zero;
            Vector3 top = hasBounds ? new Vector3(b.center.x, b.max.y, b.center.z) : Vector3.zero;

            if (prefab.GetComponent<Ship>() != null || prefab.GetComponent<Vagon>() != null) { list.Add(Vector3.zero); why = "ship or cart: origin"; return list; }
            if (IsOnePointClass(prefab, out string cls)) { list.Add(bottom); why = $"{cls}: bottom centre"; return list; }
            if (!hasBounds) { list.Add(Vector3.zero); why = "no bounds: origin"; return list; }

            float longest = Mathf.Max(b.size.x, b.size.y, b.size.z);
            if (longest < TinySide) { list.Add(bottom); list.Add(Vector3.zero); list.Add(top); why = "tiny: bottom centre, origin, top centre"; return list; }

            for (int y = 0; y < 2; y++)
                foreach (var (x, z) in new[] { (0, 0), (0, 1), (1, 1), (1, 0) }) list.Add(Corner(b, x, y, z));
            list.Add(bottom); list.Add(top); list.Add(b.center);
            why = $"corners + centres ({longest:0.#} m)";
            return list;
        }

        // The vanilla component says what it is; a machine sits on one point at its foot.
        private static bool IsOnePointClass(GameObject prefab, out string cls)
        {
            cls = null;
            if (prefab.GetComponent<Smelter>() != null) cls = "Smelter";
            else if (prefab.GetComponent<Fermenter>() != null) cls = "Fermenter";
            else if (prefab.GetComponent<Incinerator>() != null) cls = "Incinerator";
            else if (prefab.GetComponent<ShieldGenerator>() != null) cls = "ShieldGenerator";
            else if (prefab.GetComponent<Beehive>() != null) cls = "Beehive";
            else if (prefab.GetComponent<SapCollector>() != null) cls = "SapCollector";
            else if (prefab.GetComponent<CraftingStation>() != null) cls = "CraftingStation";
            else if (prefab.GetComponent<StationExtension>() != null) cls = "StationExtension";
            else if (prefab.GetComponent<MapTable>() != null) cls = "MapTable";
            else
            {
                var piece = prefab.GetComponent<Piece>();
                // Balrond files cabinets and shelves under Table too; those stay on the general rule (Container, ItemStand).
                if (piece != null && piece.m_comfortGroup == Piece.ComfortGroup.Table && prefab.GetComponent<Container>() == null && prefab.GetComponent<ItemStand>() == null) cls = "Table";
            }
            return cls != null;
        }

        private static Vector3 Corner(Bounds b, int x, int y, int z) =>
            new Vector3(x == 0 ? b.min.x : b.max.x, y == 0 ? b.min.y : b.max.y, z == 0 ? b.min.z : b.max.z);

        private static void AddPoint(GameObject prefab, Vector3 localPos, string label)
        {
            var point = new GameObject(label) { tag = SnapTag };
            point.transform.SetParent(prefab.transform, false);
            point.transform.localPosition = localPos;
            point.SetActive(false);
        }

        // Solid colliders in root space; renderers when there are none. Off parts (worn, broken) don't count.
        internal static bool TryLocalBounds(GameObject prefab, out Bounds bounds)
        {
            bool any = false;
            var result = new Bounds();
            var toRoot = prefab.transform.worldToLocalMatrix;
            void Grow(Matrix4x4 local, Vector3 center, Vector3 size)
            {
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                {
                    var p = toRoot.MultiplyPoint3x4(local.MultiplyPoint3x4(center + Vector3.Scale(size * 0.5f, new Vector3(x, y, z))));
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; } else result.Encapsulate(p);
                }
            }
            foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger || !OnInHierarchy(c.transform, prefab.transform)) continue;
                var m = c.transform.localToWorldMatrix;
                switch (c)
                {
                    case BoxCollider box: Grow(m, box.center, box.size); break;
                    case SphereCollider s: Grow(m, s.center, Vector3.one * s.radius * 2f); break;
                    case CapsuleCollider cap: { var size = Vector3.one * cap.radius * 2f; size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2f); Grow(m, cap.center, size); break; }
                    case MeshCollider mesh: if (mesh.sharedMesh != null) Grow(m, mesh.sharedMesh.bounds.center, mesh.sharedMesh.bounds.size); break;
                }
            }
            if (!any)
                foreach (var f in prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (f.sharedMesh != null && OnInHierarchy(f.transform, prefab.transform)) Grow(f.transform.localToWorldMatrix, f.sharedMesh.bounds.center, f.sharedMesh.bounds.size);
            bounds = result;
            return any;
        }

        private static bool OnInHierarchy(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }
    }
}
