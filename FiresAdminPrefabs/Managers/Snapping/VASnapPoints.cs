using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.Snapping
{
    // Applies VASnapLayouts to the vanilla props FAP puts in the hammer.
    //
    // A snap point is vanilla's own mechanism: an inactive child object tagged "snappoint". Piece.GetSnapPoints
    // collects those children in child order, and the placement ghost pairs its points with the target's within
    // 0.5 m. So this only ever adds children, moves children, or fixes the colliders the remove raycast and the snap
    // search hit. It runs once per prefab (the hammer scan and the late baked-bundle pass can both reach one).
    internal static class VASnapPoints
    {
        private const string SnapTag = "snappoint";

        private static Dictionary<string, VASnapRule> _byPrefab;
        private static readonly HashSet<int> _done = new HashSet<int>();

        private static Dictionary<string, VASnapRule> ByPrefab
        {
            get
            {
                if (_byPrefab != null) return _byPrefab;
                var map = new Dictionary<string, VASnapRule>(StringComparer.Ordinal);
                foreach (var rule in VASnapLayouts.Rules)
                    foreach (var name in rule.Prefabs)
                    {
                        if (map.ContainsKey(name)) Debug.LogWarning($"{VaPieces.PluginName}: snap layout for '{name}' is listed twice; the first one is used.");
                        else map[name] = rule;
                    }
                return _byPrefab = map;
            }
        }

        internal static bool HasLayout(string prefabName) => prefabName != null && ByPrefab.ContainsKey(prefabName);

        internal static void Apply(GameObject prefab)
        {
            if (prefab == null || !ByPrefab.TryGetValue(prefab.name, out var rule)) return;
            if (!_done.Add(prefab.GetInstanceID())) return;

            try
            {
                FixColliders(prefab, rule);
                foreach (var (child, pos) in rule.Moves)
                {
                    var t = prefab.transform.Find(child);
                    if (t != null) t.localPosition = pos;
                }

                int n = 0;
                foreach (var (pos, label) in rule.Points) AddPoint(prefab, pos, label ?? $"fap snap {++n}");
                if (rule.CornersOfMesh != null)
                    foreach (var corner in MeshCorners(prefab, rule.CornersOfMesh)) AddPoint(prefab, corner, $"fap snap {++n}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: snap layout for '{prefab.name}' failed: {ex.Message}");
            }
        }

        private static void FixColliders(GameObject prefab, VASnapRule rule)
        {
            if (rule.RecentredChild != null)
            {
                var t = prefab.transform.Find(rule.RecentredChild);
                if (t != null) t.localPosition = Vector3.zero;
            }

            if (rule.NoRandomRotationOn != null)
            {
                var rotation = prefab.transform.Find(rule.NoRandomRotationOn)?.GetComponent<RandomPieceRotation>();
                if (rotation != null) UnityEngine.Object.DestroyImmediate(rotation);
            }

            if (rule.CollidersToPieceLayer)
            {
                int pieceLayer = LayerMask.NameToLayer("piece");
                foreach (var collider in prefab.GetComponentsInChildren<Collider>()) collider.gameObject.layer = pieceLayer;
            }

            if (rule.ReplacementBoxes != null)
            {
                foreach (var collider in prefab.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
                foreach (var (center, size) in rule.ReplacementBoxes)
                {
                    var box = prefab.AddComponent<BoxCollider>();
                    box.center = center;
                    box.size = size;
                }
            }

            if (rule.ResizedBox != null)
                foreach (var box in prefab.GetComponentsInChildren<BoxCollider>())
                    if (box.name == rule.ResizedBox)
                    {
                        box.center += rule.ResizedBoxCenterDelta;
                        box.size += rule.ResizedBoxSizeDelta;
                    }
        }

        // The corners of the first mesh whose name starts with the prefix, in that mesh's own space.
        private static IEnumerable<Vector3> MeshCorners(GameObject prefab, string meshNamePrefix)
        {
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || !mesh.name.StartsWith(meshNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var bounds = mesh.bounds;
                var corners = new List<Vector3>(8);
                foreach (int sx in new[] { -1, 1 })
                    foreach (int sy in new[] { -1, 1 })
                        foreach (int sz in new[] { -1, 1 })
                            corners.Add(bounds.center + Vector3.Scale(new Vector3(sx, sy, sz), bounds.extents));
                return corners;
            }
            return Array.Empty<Vector3>();
        }

        private static void AddPoint(GameObject prefab, Vector3 localPos, string label)
        {
            var point = new GameObject(label) { tag = SnapTag };
            point.transform.SetParent(prefab.transform, false);
            point.transform.localPosition = localPos;
            point.SetActive(false);
        }

        // fap_snapdump: what the game will read off a piece right now, so "it won't snap" can be split into "there
        // are no points", "the points are there but the ghost picks another" and "nothing solid on the piece layer".
        internal static List<string> Describe(string prefabName)
        {
            var lines = new List<string>();
            GameObject prefab = string.IsNullOrEmpty(prefabName)
                ? Player.m_localPlayer?.GetSelectedPiece()?.gameObject
                : ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                lines.Add(string.IsNullOrEmpty(prefabName)
                    ? "[fap_snapdump] no piece selected in the hammer and no prefab name given."
                    : $"[fap_snapdump] '{prefabName}' is not in ZNetScene.");
                return lines;
            }

            string name = prefab.name;
            lines.Add($"[fap_snapdump] {name}");

            var tables = new List<string>();
            foreach (var table in Resources.FindObjectsOfTypeAll<PieceTable>())
                if (table?.m_pieces != null && table.m_pieces.Exists(p => p != null && p.name == name)) tables.Add(table.name);
            lines.Add(tables.Count > 0
                ? $"  on PieceTable(s): {string.Join(", ", tables)} (a prefab another table already has is not FAP's, so no layout runs on it)"
                : "  on no PieceTable before FAP: FAP handles it");
            lines.Add($"  FAP snap layout: {(HasLayout(name) ? "yes" : "none")}; applied this session: {(_done.Contains(prefab.GetInstanceID()) ? "yes" : "no")}");

            var piece = prefab.GetComponent<Piece>();
            if (piece == null) lines.Add("  no Piece component: it can't be placed or removed as a build piece");
            else
            {
                var wear = prefab.GetComponent<WearNTear>();
                var view = prefab.GetComponent<ZNetView>();
                lines.Add($"  Piece: canBeRemoved={piece.m_canBeRemoved}, groundPiece={piece.m_groundPiece}, clipGround={piece.m_clipGround}, allowedInDungeons={piece.m_allowedInDungeons}");
                lines.Add($"  WearNTear: {(wear != null ? "yes" : "no (removal falls back to destroying the ZNetView)")}; ZNetView: {(view != null ? $"yes (persistent={view.m_persistent})" : "no (it can't persist or be removed)")}");

                int pieceLayer = LayerMask.NameToLayer("piece"), onLayer = 0, elsewhere = 0;
                foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.isTrigger) continue;
                    if (collider.gameObject.layer == pieceLayer) onLayer++; else elsewhere++;
                }
                lines.Add($"  solid colliders: {onLayer} on the piece layer, {elsewhere} elsewhere{(onLayer == 0 ? " (the remove raycast and the snap search have nothing to hit)" : "")}");
            }

            int index = 0;
            foreach (Transform child in prefab.transform)
            {
                if (!child.CompareTag(SnapTag)) continue;
                var p = child.localPosition;
                lines.Add($"    [{index++}] {child.name}  ({p.x:0.###}, {p.y:0.###}, {p.z:0.###})");
            }
            lines.Add(index == 0
                ? "  no snap points: this piece can't snap"
                : $"  {index} snap point(s); the ghost pairs points within 0.5 m and takes the nearest");
            return lines;
        }
    }
}
