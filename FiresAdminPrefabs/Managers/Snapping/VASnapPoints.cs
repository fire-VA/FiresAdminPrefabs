using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers.Snapping
{
    // Names on bundle pieces' own snap points, and fap_snapdump. New points come from VASnapGeometry.
    //
    // A snap point is vanilla's own mechanism: an inactive child object tagged "snappoint". Piece.GetSnapPoints
    // collects those children in child order, and the placement ghost pairs its points with the target's within
    // 0.5 m. So this only ever adds children, moves children, or fixes the colliders the remove raycast and the snap
    // search hit. It runs once per prefab (the hammer scan and the late baked-bundle pass can both reach one).
    internal static class VASnapPoints
    {
        private const string SnapTag = "snappoint";

        // 1.1.323 (Fire 10-27: "It's just snap point and a number" + "Exactly 1.7.2"): the bundle-authored names were all over the place
        // (snap1, Fapsnap1, snapoint). A piece from one of FAP's asset bundles with no layout of its own gets MoreVanillaBuildPrefabs 1.7.2's
        // default for unlabelled points, "Extra 1..N" (SnapPointManager.CreateNamedSnapPoints), in child order (vanilla's
        // Piece.GetSnapPoints order, which saved snap defaults index). Positions and order are untouched; only names change. Once per prefab.
        private static readonly HashSet<int> _bundleNamed = new HashSet<int>();

        internal static void NormaliseBundleNames(GameObject prefab) => NormaliseBundleNames(prefab, out _, out _, true);

        // Returns false when the prefab is not a FAP bundle piece, has a layout, or was done already. points / renamed: its snap points
        // and how many names changed. say: the per-piece line (only when a name changed).
        private static bool NormaliseBundleNames(GameObject prefab, out int points, out int renamed, bool say)
        {
            points = 0; renamed = 0;
            if (prefab == null || _bundleNamed.Contains(prefab.GetInstanceID())) return false;
            string bundle = null;
            if (!VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out bundle) && !VAExternalBundleLoader.IsBundleOwnedPrefab(prefab)) return false;
            _bundleNamed.Add(prefab.GetInstanceID());
            try
            {
                var was = new List<string>();
                foreach (Transform child in prefab.transform)
                {
                    if (child == null || !child.CompareTag(SnapTag)) continue;
                    string want = $"Snap point {++points}";
                    if (child.name == want) continue;
                    if (was.Count < 6) was.Add(child.name);
                    child.name = want;
                    renamed++;
                }
                if (say && renamed > 0)
                    VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: [Snap] {prefab.name} (bundle {bundle ?? "?"}): {points} point(s) named Snap point 1..{points}, renamed {renamed} (were {string.Join(", ", was)}{(renamed > was.Count ? ", ..." : "")})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: [Snap] {prefab.name}: bundle snap point names not normalised: {ex.Message}");
            }
            return true;
        }

        // 1.1.324 (round #25: no "[Snap]" line at all with 1.1.323 - FAP's bundle pieces reach the Hammer by another route and the late
        // pass skips prefabs already in the table, so the per-prefab hook never saw them): one pass over the Hammer table itself after each
        // scan. Info summary per pass; per-piece lines only where a name changed, the first 20.
        internal static void NormaliseHammerBundlePieces(PieceTable table, string pass)
        {
            if (table?.m_pieces == null) return;
            int pieces = 0, withPoints = 0, changed = 0, points = 0, renamedAll = 0, said = 0;
            foreach (var go in table.m_pieces.ToArray())
            {
                if (go == null) continue;
                bool say = said < 20;
                if (!NormaliseBundleNames(go, out int p, out int r, say)) continue;
                pieces++;
                if (p > 0) withPoints++;
                if (r > 0) { changed++; if (say) said++; }
                points += p; renamedAll += r;
            }
            if (pieces > 0)
                Debug.Log($"{VaPieces.PluginName}: [Snap] FAP bundle pieces ({pass}): {pieces} new on the Hammer, {withPoints} with snap points ({points} points), {changed} renamed to Extra 1..N ({renamedAll} names changed{(changed > said ? $"; {said} listed" : "")})");
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
                ? $"  on PieceTable(s): {string.Join(", ", tables)}"
                : "  on no PieceTable before FAP: FAP handles it");
            lines.Add($"  points from FAP's geometry rule: {(VASnapGeometry.MadeByRule(name) ? "yes" : "no")}");

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
