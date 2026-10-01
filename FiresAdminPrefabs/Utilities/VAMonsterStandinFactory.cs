using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Builds UI-only stand-ins for VAMonsterRegistry entries so they show
    // up on the hammer's Monsters tab. The placement intercept
    // (HammerClutterPaintPatch) routes clicks to VAMonsterSpawner instead
    // of letting vanilla actually place the stand-in.
    //
    // Visual copy intentionally SKIPS SkinnedMeshRenderers and instead
    // promotes their sharedMesh onto a plain MeshFilter + MeshRenderer.
    // The bind-pose mesh renders as a frozen silhouette without bone
    // setup — Unity's "Bones do not match bindpose" check only fires on
    // SMR components. Creatures with no MeshRenderers at all (most
    // humanoids) get a sized translucent capsule placeholder so the
    // placement ghost stays visible.
    public static class VAMonsterStandinFactory
    {
        public const string NamePrefix = "FAP_MonsterPick_";

        private const float MinPlaceholderHalfSize = 0.25f;
        private const float MinPlaceholderHeight = 0.5f;
        private static readonly Vector3 DefaultPlaceholderSize = new Vector3(1f, 2f, 1f);

        private static GameObject _root;
        private static readonly Dictionary<string, GameObject> _byPrefabName
            = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public static IEnumerable<KeyValuePair<string, GameObject>> AllStandins => _byPrefabName;

        public static bool IsStandinName(string name)
            => !string.IsNullOrEmpty(name)
               && name.StartsWith(NamePrefix, StringComparison.Ordinal);

        public static string ResolveCreatureNameFromStandin(GameObject standin)
        {
            if (standin == null) return null;
            if (!IsStandinName(standin.name)) return null;
            return standin.name.Substring(NamePrefix.Length);
        }

        public static GameObject GetOrCreate(VAMonsterRegistry.Entry entry, Piece.PieceCategory category)
        {
            if (entry == null || entry.Prefab == null) return null;

            if (_byPrefabName.TryGetValue(entry.PrefabName, out var cached) && cached != null)
                return cached;

            EnsureRoot();

            var standin = new GameObject(NamePrefix + entry.PrefabName);
            standin.transform.SetParent(_root.transform, worldPositionStays: false);
            standin.SetActive(false);

            CopyVisualsRecursive(entry.Prefab.transform, standin.transform);
            EnsurePlaceholderIfEmpty(standin, entry.Prefab);

            var piece = standin.AddComponent<Piece>();
            piece.m_name = entry.DisplayName;
            piece.m_description = BuildDescription(entry);
            piece.m_enabled = true;
            piece.m_category = category;
            piece.m_clipGround = true;
            piece.m_clipEverything = true;
            piece.m_allowRotatedOverlap = true;
            piece.m_canBeRemoved = false;
            piece.m_resources = Array.Empty<Piece.Requirement>();
            piece.m_icon = null;

            var znv = standin.AddComponent<ZNetView>();
            znv.m_persistent = false;

            _byPrefabName[entry.PrefabName] = standin;
            return standin;
        }

        private static string BuildDescription(VAMonsterRegistry.Entry entry)
        {
            return $"Place '{entry.PrefabName}'"
                + (entry.IsBoss ? "\n<color=red>Boss creature</color>" : string.Empty)
                + (entry.HasHumanoid ? "\nHumanoid AI" : "\nNon-humanoid")
                + "\n\n[<color=yellow>right-click</color>] to configure";
        }

        private static void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("FAP_MonsterStandinRoot");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
        }

        private static void CopyVisualsRecursive(Transform source, Transform dest)
        {
            dest.localPosition = source.localPosition;
            dest.localRotation = source.localRotation;
            dest.localScale = source.localScale;

            var mf = source.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var newMf = dest.gameObject.AddComponent<MeshFilter>();
                newMf.sharedMesh = mf.sharedMesh;

                var mr = source.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    var newMr = dest.gameObject.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = mr.sharedMaterials;
                }
            }

            // SkinnedMeshRenderer -> static MeshFilter+MeshRenderer using
            // the SMR's bind-pose sharedMesh. Bones/blendshapes ignored.
            var smr = source.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                if (dest.GetComponent<MeshFilter>() == null)
                {
                    var newMf = dest.gameObject.AddComponent<MeshFilter>();
                    newMf.sharedMesh = smr.sharedMesh;
                    var newMr = dest.gameObject.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = smr.sharedMaterials;
                }
            }

            for (int i = 0; i < source.childCount; i++)
            {
                var child = source.GetChild(i);
                if (child == null) continue;
                if (IsNonVisualChild(child.name)) continue;
                var newChild = new GameObject(child.name);
                newChild.transform.SetParent(dest, worldPositionStays: false);
                CopyVisualsRecursive(child, newChild.transform);
            }
        }

        private static void EnsurePlaceholderIfEmpty(GameObject standin, GameObject sourcePrefab)
        {
            if (standin == null || sourcePrefab == null) return;

            var existing = standin.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (existing != null && existing.Length > 0) return;

            Bounds combined = default;
            bool hasBounds = false;
            var sourceRenderers = sourcePrefab.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < sourceRenderers.Length; i++)
            {
                var r = sourceRenderers[i];
                if (r == null) continue;
                if (!hasBounds) { combined = r.bounds; hasBounds = true; }
                else combined.Encapsulate(r.bounds);
            }

            Vector3 size = hasBounds ? combined.size : DefaultPlaceholderSize;
            Vector3 localOffset = hasBounds
                ? combined.center - sourcePrefab.transform.position
                : new Vector3(0f, size.y * 0.5f, 0f);

            var placeholder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var col = placeholder.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);

            placeholder.name = "VAMonsterPlaceholderVisual";
            placeholder.transform.SetParent(standin.transform, worldPositionStays: false);
            placeholder.transform.localPosition = localOffset;
            placeholder.transform.localScale = new Vector3(
                Mathf.Max(MinPlaceholderHalfSize, size.x),
                Mathf.Max(MinPlaceholderHeight, size.y * 0.5f),
                Mathf.Max(MinPlaceholderHalfSize, size.z));

            var mr = placeholder.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                var matInstance = new Material(mr.sharedMaterial);
                matInstance.color = new Color(1f, 0.85f, 0.2f, 0.5f);
                mr.sharedMaterial = matInstance;
            }
        }

        private static bool IsNonVisualChild(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("fx_", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("sfx_", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("vfx_", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
