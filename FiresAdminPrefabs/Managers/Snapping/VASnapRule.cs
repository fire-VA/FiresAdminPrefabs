using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.Snapping
{
    // One prefab's snap treatment, written as data. A rule can:
    //   - add snap points: plain positions, labelled positions (the label is what manual-snap mods show), a
    //     generated grid, or the eight corners of a named mesh's bounds;
    //   - move snap points the prefab already carries (by child name) instead of adding any;
    //   - fix what snapping depends on: colliders onto the 'piece' layer, replacement box colliders, one named box
    //     resized, a child recentred, a child's RandomPieceRotation removed.
    // VASnapLayouts lists every rule; VASnapPoints applies them.
    internal sealed class VASnapRule
    {
        internal readonly string[] Prefabs;

        // In the order they become children. Piece.GetSnapPoints hands the game its points in child order, and
        // the placement ghost starts on the first, so the order here is part of the data.
        internal readonly List<(Vector3 pos, string label)> Points = new List<(Vector3, string)>();
        internal readonly List<(string child, Vector3 pos)> Moves = new List<(string, Vector3)>();

        internal string CornersOfMesh;
        internal bool CollidersToPieceLayer;
        internal (Vector3 center, Vector3 size)[] ReplacementBoxes;
        internal string ResizedBox;
        internal Vector3 ResizedBoxCenterDelta, ResizedBoxSizeDelta;
        internal string RecentredChild;
        internal string NoRandomRotationOn;

        internal VASnapRule(params string[] prefabs) { Prefabs = prefabs; }

        internal VASnapRule At(params Vector3[] points)
        {
            foreach (var p in points) Points.Add((p, null));
            return this;
        }

        internal VASnapRule Labelled(params (Vector3 pos, string label)[] points)
        {
            Points.AddRange(points);
            return this;
        }

        // Every (x, y, z) with y from ys, x in [xMin, xMax] and z in [zMin, zMax] by step; y is the outer loop and
        // z the inner one. skipOuterCorners leaves out the four columns where both |x| and |z| are at their maximum.
        internal VASnapRule Grid(float[] ys, int xMin, int xMax, int zMin, int zMax, int step = 1, bool skipOuterCorners = false)
        {
            foreach (float y in ys)
                for (int x = xMin; x <= xMax; x += step)
                    for (int z = zMin; z <= zMax; z += step)
                    {
                        bool outerCorner = (x == xMin || x == xMax) && (z == zMin || z == zMax);
                        if (skipOuterCorners && outerCorner) continue;
                        Points.Add((new Vector3(x, y, z), null));
                    }
            return this;
        }

        internal VASnapRule Move(string child, Vector3 pos)
        {
            Moves.Add((child, pos));
            return this;
        }

        internal VASnapRule MeshCorners(string meshNamePrefix) { CornersOfMesh = meshNamePrefix; return this; }
        internal VASnapRule OnPieceLayer() { CollidersToPieceLayer = true; return this; }
        internal VASnapRule Boxes(params (Vector3 center, Vector3 size)[] boxes) { ReplacementBoxes = boxes; return this; }
        internal VASnapRule Recentre(string child) { RecentredChild = child; return this; }
        internal VASnapRule NoRandomRotation(string child) { NoRandomRotationOn = child; return this; }

        internal VASnapRule ResizeBox(string colliderName, Vector3 centerDelta, Vector3 sizeDelta)
        {
            ResizedBox = colliderName;
            ResizedBoxCenterDelta = centerDelta;
            ResizedBoxSizeDelta = sizeDelta;
            return this;
        }
    }
}
