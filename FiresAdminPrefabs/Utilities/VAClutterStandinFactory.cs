using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Builds UI-only stand-in GameObjects for vanilla clutter prefabs so
    // they appear on the hammer's Clutter tab. The stand-in has a Piece +
    // visual copy but NO ZNetView — the place patch intercepts before any
    // Instantiate, so the stand-in never networks or persists.
    //
    // Clutter rendered via InstanceRenderer (vanilla grass) doesn't have a
    // MeshFilter/MeshRenderer for the icon renderer to capture, so we
    // synthesize a flat MeshFilter+MeshRenderer pair on the mirror node
    // using the InstanceRenderer's mesh + a copy of its material.
    public static class VAClutterStandinFactory
    {
        public const string NamePrefix = "FAP_ClutterPick_";

        private static GameObject _root;
        private static readonly Dictionary<string, GameObject> _byClutterName
            = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public static IEnumerable<KeyValuePair<string, GameObject>> AllStandins
            => _byClutterName;

        public static bool IsStandinName(string name)
            => !string.IsNullOrEmpty(name)
               && name.StartsWith(NamePrefix, StringComparison.Ordinal);

        public static string ResolveRealNameFromStandin(GameObject standin)
        {
            if (standin == null) return null;
            return IsStandinName(standin.name)
                ? standin.name.Substring(NamePrefix.Length)
                : null;
        }

        public static GameObject GetOrCreate(GameObject realPrefab, Piece.PieceCategory category)
        {
            if (realPrefab == null) return null;
            EnsureRoot();
            if (_byClutterName.TryGetValue(realPrefab.name, out var cached)
                && cached != null) return cached;

            var go = new GameObject(NamePrefix + realPrefab.name);
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            go.SetActive(false);

            VAPieceIconRenderer.CopyVisualsRecursivePublic(realPrefab.transform, go.transform);
            EnsureInstanceRendererPreview(realPrefab, go);

            var piece = go.AddComponent<Piece>();
            piece.m_name = realPrefab.name;
            piece.m_description = BuildDescription(realPrefab);
            piece.m_enabled = true;
            piece.m_category = category;
            piece.m_clipGround = true;
            piece.m_clipEverything = true;
            piece.m_allowRotatedOverlap = true;
            piece.m_canBeRemoved = false;
            piece.m_resources = Array.Empty<Piece.Requirement>();

            _byClutterName[realPrefab.name] = go;
            return go;
        }

        // Deep-copies the InstanceRenderer's material so later repair-pass
        // mutations on the source bundle don't bleed into the stand-in's
        // frozen visual snapshot.
        private static void EnsureInstanceRendererPreview(GameObject source, GameObject standin)
        {
            var srcIRs = source.GetComponentsInChildren<InstanceRenderer>(includeInactive: true);
            if (srcIRs == null || srcIRs.Length == 0) return;

            foreach (var ir in srcIRs)
            {
                if (ir == null || ir.m_mesh == null) continue;

                Transform target = FindMirrorPath(source.transform, standin.transform, ir.transform);
                if (target == null) continue;

                if (target.GetComponent<MeshFilter>() != null) continue;
                if (target.GetComponent<MeshRenderer>() != null) continue;

                var mf = target.gameObject.AddComponent<MeshFilter>();
                mf.sharedMesh = ir.m_mesh;
                var mr = target.gameObject.AddComponent<MeshRenderer>();
                if (ir.m_material != null)
                    mr.sharedMaterial = new Material(ir.m_material);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        private static Transform FindMirrorPath(Transform sourceRoot, Transform standinRoot, Transform sourceNode)
        {
            if (sourceNode == sourceRoot) return standinRoot;

            var stack = new Stack<int>();
            var cur = sourceNode;
            while (cur != null && cur != sourceRoot)
            {
                if (cur.parent == null) return null;
                stack.Push(cur.GetSiblingIndex());
                cur = cur.parent;
            }
            if (cur != sourceRoot) return null;

            var mirror = standinRoot;
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                if (idx < 0 || idx >= mirror.childCount) return null;
                mirror = mirror.GetChild(idx);
                if (mirror == null) return null;
            }
            return mirror;
        }

        private static void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("FAP_ClutterStandinRoot");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
        }

        private static string BuildDescription(GameObject realPrefab)
        {
            return "<color=#ffaa00>Clutter brush.</color> Click on terrain to paint a circle of "
                 + realPrefab.name + ". Shift+Scroll resizes the brush live. "
                 + "Painted positions persist via TerrainComp ZDO; "
                 + "painting dirt/paved/path over the area erases the clutter.";
        }
    }
}
