using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAItemStandStandinFactory — builds FAP-owned stand-in pieces, one
    // per (eligible item, orientation) pair. Each stand-in carries a
    // Piece component (for hammer UI) with the item's vanilla icon, plus
    // a copied mesh hierarchy of the vanilla itemstand + the item's
    // attach visual so the placement ghost shows what'll spawn.
    //
    // Vanilla item/stand prefabs are NEVER mutated; stand-ins are
    // FAP-owned GameObjects parented to a hidden DontDestroyOnLoad root.
    //
    // Resolution: stand-ins use name prefix "FAP_ItemStandPickV_" or
    // "FAP_ItemStandPickH_" + the item name, so the placement intercept
    // can detect the orientation + look up the real item via
    // VAItemStandRegistry.
    //
    // Idempotent: factory caches per (orientation, itemName).
    public static class VAItemStandStandinFactory
    {
        public const string PrefixVertical   = "FAP_ItemStandPickV_";
        public const string PrefixHorizontal = "FAP_ItemStandPickH_";

        // Vanilla prefab names — resolved at registration time via
        // ZNetScene.GetPrefab.  "itemstand" (vertical wall mount) ships
        // with base Valheim; "itemstandh" (horizontal shelf mount)
        // ships with the Hildir update. If horizontal is missing in
        // a player's install, the horizontal tab silently no-ops.
        public const string VanillaVerticalPrefabName   = "itemstand";
        public const string VanillaHorizontalPrefabName = "itemstandh";

        private static GameObject _root;
        private static readonly Dictionary<string, GameObject> _byKey
            = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public enum Orientation { Vertical, Horizontal }

        public static bool IsStandinName(string name)
            => IsVerticalStandinName(name) || IsHorizontalStandinName(name);
        public static bool IsVerticalStandinName(string name)
            => !string.IsNullOrEmpty(name)
               && name.StartsWith(PrefixVertical, StringComparison.Ordinal);
        public static bool IsHorizontalStandinName(string name)
            => !string.IsNullOrEmpty(name)
               && name.StartsWith(PrefixHorizontal, StringComparison.Ordinal);

        // Returns (itemName, orientation) parsed from a stand-in's
        // GameObject name, or (null, ...) if not a stand-in.
        public static bool TryResolve(GameObject standin, out string itemName, out Orientation orientation)
        {
            itemName = null;
            orientation = Orientation.Vertical;
            if (standin == null) return false;
            var n = standin.name;
            if (IsVerticalStandinName(n))
            {
                itemName = n.Substring(PrefixVertical.Length);
                orientation = Orientation.Vertical;
                return true;
            }
            if (IsHorizontalStandinName(n))
            {
                itemName = n.Substring(PrefixHorizontal.Length);
                orientation = Orientation.Horizontal;
                return true;
            }
            return false;
        }

        public static IEnumerable<KeyValuePair<string, GameObject>> AllStandins => _byKey;

        // Build (or fetch cached) a stand-in for the given item +
        // orientation. Returns null when:
        //   * The vanilla itemstand prefab for the orientation isn't in
        //     ZNetScene (e.g. itemstandh on an old Valheim install)
        //   * The item is null or has no usable mesh
        // Stand-ins whose ghost meshes are not built yet (see GetOrCreate).
        private static readonly Dictionary<GameObject, (VAItemStandRegistry.Entry item, Orientation orientation, GameObject standPrefab)> _pendingVisuals
            = new Dictionary<GameObject, (VAItemStandRegistry.Entry, Orientation, GameObject)>();

        /// <summary>
        /// Builds the stand-in's placement-ghost meshes the first time it is needed: the stand's mesh hierarchy (so the ghost
        /// shows the stand, not a blank object) and the item on it, placed the way vanilla ItemStand.SetVisualItem places it
        /// (ItemStand.cs:309-356), so the ghost matches the placed result. Safe to call on anything; no-op after the first.
        /// </summary>
        public static int VisualsBuilt { get; private set; }

        // 1.1.196 (Arbby via Fire, "item stand icons vanished after fap_reload_icons"): a stand-in with no meshes yet can't be
        // re-rendered; its icon is the item's own sprite, which fap_reload_icons puts back instead of blanking it.
        public static bool TryGetUnbuiltIcon(GameObject standin, out Sprite icon)
        {
            icon = null;
            if (standin == null || !_pendingVisuals.TryGetValue(standin, out var pending)) return false;
            icon = pending.item?.Icon;
            return true;
        }

        public static void EnsureVisuals(GameObject standin)
        {
            if (standin == null || !_pendingVisuals.TryGetValue(standin, out var pending)) return;
            _pendingVisuals.Remove(standin);
            VisualsBuilt++;
            if (pending.standPrefab == null) return;
            VAPieceIconRenderer.CopyVisualsRecursivePublic(pending.standPrefab.transform, standin.transform);
            BuildGhostItemAttachment(standin, pending.standPrefab, pending.item, pending.orientation);
        }

        // 1.1.196 (Fire's hand test 10-09: "[tag_blueberrypie_bal] (Shelf)"): localize the item's name token; a token no loaded
        // mod translates comes back as "[token]", so fall back to the prefab name, made readable.
        private static string ReadableItemName(VAItemStandRegistry.Entry item)
        {
            string token = item.ItemDrop?.m_itemData?.m_shared?.m_name;
            string text = null;
            if (!string.IsNullOrEmpty(token))
            {
                try { text = Localization.instance != null ? Localization.instance.Localize(token) : token; } catch { text = token; }
            }
            if (string.IsNullOrEmpty(text) || text.StartsWith("[", StringComparison.Ordinal) || text.StartsWith("$", StringComparison.Ordinal))
                text = System.Text.RegularExpressions.Regex.Replace(item.Name ?? "", "(?<=[a-z])(?=[A-Z])|_", " ").Trim();
            return text;
        }

        public static GameObject GetOrCreate(VAItemStandRegistry.Entry item,
                                              Orientation orientation,
                                              Piece.PieceCategory category)
        {
            if (item == null || item.Prefab == null) return null;

            string key = (orientation == Orientation.Vertical ? "V:" : "H:") + item.Name;
            if (_byKey.TryGetValue(key, out var cached) && cached != null) return cached;

            var scene = ZNetScene.instance;
            if (scene == null) return null;

            string standPrefabName = orientation == Orientation.Vertical
                ? VanillaVerticalPrefabName
                : VanillaHorizontalPrefabName;
            var standPrefab = scene.GetPrefab(standPrefabName);
            if (standPrefab == null) return null;

            EnsureRoot();

            string prefix = orientation == Orientation.Vertical ? PrefixVertical : PrefixHorizontal;
            var go = new GameObject(prefix + item.Name);
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            go.SetActive(false);

            // The meshes (the stand's hierarchy + the item on it) are only for the placement ghost, so they are built when
            // this stand-in is first SELECTED (EnsureVisuals, from Player.SetupPlacementGhost), not here. Building all of
            // them up front at join made 2 x ~1,200 full copies (2,422 FAP_ItemStandPickItem, ~33k GameObjects per process,
            // kept for the session: [release]'s MEMORY_LEADS #5, 2026-09-29) for a menu that shows only icons.
            _pendingVisuals[go] = (item, orientation, standPrefab);

            var piece = go.AddComponent<Piece>();
            string itemDisplayName = ReadableItemName(item);
            piece.m_name = string.IsNullOrEmpty(itemDisplayName)
                ? $"Stand: {item.Name}"
                : (orientation == Orientation.Vertical
                    ? $"{itemDisplayName} (Wall)"
                    : $"{itemDisplayName} (Shelf)");
            piece.m_description = BuildDescription(item, orientation);
            piece.m_enabled = true;
            piece.m_category = category;
            piece.m_clipGround = true;
            piece.m_clipEverything = true;
            piece.m_allowRotatedOverlap = true;
            piece.m_canBeRemoved = false;   // stand-in itself never spawned; placed stand has its own m_canBeRemoved
            piece.m_resources = Array.Empty<Piece.Requirement>();
            piece.m_icon = item.Icon;

            _byKey[key] = go;
            return go;
        }

        // Builds the item-on-stand ghost with the same math vanilla's
        // ItemStand.SetVisualItem uses at placement (ItemStand.cs:309-356), so the
        // preview matches the placed result exactly:
        //
        //   1. resolve the stand's attach Transform (m_attachOther) and mirror its path
        //      onto the copied standin hierarchy
        //   2. resolve the item's "attach" Transform and its "attachobj" child — the
        //      mesh vanilla actually instantiates
        //   3. parent the ghost under the stand's attach and take localPosition,
        //      rotation and scale from the item's "attach"
        //   4. apply the per-item offset from m_itemStandOffsets for this stand's
        //      orientation, as GetOrientationSettings does
        //   5. copy attachobj's mesh hierarchy in as the visible mesh
        //
        // Failures fall through quietly: the standin keeps the stand mesh without the
        // item, which beats throwing mid hammer-build.
        private static void BuildGhostItemAttachment(
            GameObject standin,
            GameObject standPrefab,
            VAItemStandRegistry.Entry item,
            Orientation orientation)
        {
            try
            {
                // ── 1. Stand attach point ──────────────────────────
                //
                // Read m_attachOther from the SOURCE ItemStand
                // component. Its Transform was set in the prefab's
                // inspector; the name is whatever the artist chose
                // ("attach", "attachpoint", "mount", etc.) — we
                // cannot assume a fixed name.
                var sourceItemStand = standPrefab.GetComponent<ItemStand>();
                Transform standAttachOnSource = sourceItemStand != null
                    ? sourceItemStand.m_attachOther
                    : null;
                if (standAttachOnSource == null)
                {
                    // Best-effort: try the conventional names. If
                    // none match, fall back to attaching at root —
                    // the ghost will be wrong but won't NRE.
                    standAttachOnSource = standPrefab.transform.Find("attach")
                                       ?? standPrefab.transform.Find("attachpoint")
                                       ?? standPrefab.transform;
                }

                // Compute relative path from source standPrefab.root
                // → m_attachOther, then walk the same path on our
                // standin copy. CopyVisualsRecursivePublic preserves
                // the Transform names, so the path resolves.
                string attachPath = ComputeRelativePath(standPrefab.transform, standAttachOnSource);
                Transform standAttachOnStandin = string.IsNullOrEmpty(attachPath)
                    ? standin.transform
                    : standin.transform.Find(attachPath);
                if (standAttachOnStandin == null) standAttachOnStandin = standin.transform;

                // ── 2. Item attach + attachobj ─────────────────────
                //
                // Vanilla's GetAttachPrefab(item) returns the item's
                // "attach" Transform.gameObject; GetAttachGameObject
                // returns its "attachobj" child. We mirror both.
                var itemAttachOnSource = item.Prefab.transform.Find("attach");
                if (itemAttachOnSource == null) return; // no attach pose — item can't sit on stand

                Transform attachObjOnSource = itemAttachOnSource.Find("attachobj");
                // Some items don't have a nested attachobj — the
                // visual is the attach Transform itself. Fall back.
                Transform visualSource = attachObjOnSource != null ? attachObjOnSource : itemAttachOnSource;

                // ── 3. Create ghost + apply attach's local pose ────
                //
                // Parent at standAttachOnStandin with worldPositionStays
                // = false so localPosition starts at zero, then
                // OVERWRITE with the item's "attach" Transform's
                // local values. This is the exact same dance vanilla
                // does (SetVisualItem.cs:344-346): parent under the
                // stand's attach, then set localPos/Rot/Scale to the
                // item's attach pose.
                var itemGhost = new GameObject("FAP_ItemStandPickItem");
                itemGhost.transform.SetParent(standAttachOnStandin, worldPositionStays: false);
                itemGhost.transform.localPosition = itemAttachOnSource.localPosition;
                itemGhost.transform.localRotation = itemAttachOnSource.localRotation;
                itemGhost.transform.localScale    = itemAttachOnSource.localScale;

                // ── 4. Per-orientation offset ──────────────────────
                //
                // Vanilla reads m_orientationType from the stand and
                // filters m_itemStandOffsets to entries matching
                // that flag (Vertical = wall, Horizontal = shelf).
                // The first matching entry's m_position/Rotation/Scale
                // gets added/multiplied onto the ghost's local pose.
                var orientationOffset = ResolveOrientationOffset(
                    sourceItemStand,
                    item.ItemDrop,
                    orientation);
                if (orientationOffset != null)
                {
                    itemGhost.transform.localPosition += orientationOffset.m_position;
                    itemGhost.transform.localRotation *= Quaternion.Euler(orientationOffset.m_rotation);
                    itemGhost.transform.localScale    = Vector3.Scale(itemGhost.transform.localScale, orientationOffset.m_scale);
                }

                // ── 5. Copy the actual mesh ────────────────────────
                VAPieceIconRenderer.CopyVisualsRecursivePublic(visualSource, itemGhost.transform);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAItemStandStandinFactory.BuildGhostItemAttachment threw for '{item.Name}' ({orientation}): {ex.Message}. Standin will show stand mesh only.");
            }
        }

        // Picks the first ItemStand.OrientationSettings from the item's
        // m_itemStandOffsets that matches the stand's m_orientationType.
        // Mirrors ItemStand.GetOrientationSettings logic — returns null
        // if no match (in which case vanilla uses an empty default).
        private static ItemStand.OrientationSettings ResolveOrientationOffset(
            ItemStand sourceItemStand,
            ItemDrop itemDrop,
            Orientation orientation)
        {
            try
            {
                if (itemDrop == null || itemDrop.m_itemData == null || itemDrop.m_itemData.m_shared == null)
                    return null;
                var offsets = itemDrop.m_itemData.m_shared.m_itemStandOffsets;
                if (offsets == null || offsets.Count == 0) return null;

                // Source ItemStand defines m_orientationType. We
                // prefer reading it from the source component; if it's
                // missing (e.g., custom stand prefab without an
                // ItemStand component), fall back to deriving it from
                // our standin's Orientation enum.
                ItemStand.Orientation orientationFlag = sourceItemStand != null
                    ? sourceItemStand.m_orientationType
                    : (orientation == Orientation.Vertical
                        ? ItemStand.Orientation.Vertical
                        : ItemStand.Orientation.Horizontal);

                foreach (var offset in offsets)
                {
                    if ((orientationFlag & offset.m_orientations) != ItemStand.Orientation.None)
                        return offset;
                }
            }
            catch { /* fall through to null */ }
            return null;
        }

        // Computes the dot-separated child path from `root` to
        // `descendant`. Returns empty string when descendant IS root.
        // Used to look up the matching child on the standin's copied
        // hierarchy.
        private static string ComputeRelativePath(Transform root, Transform descendant)
        {
            if (root == null || descendant == null || descendant == root) return string.Empty;
            // Walk up the parent chain collecting names until we hit
            // root. If we never hit root, the descendant isn't actually
            // a descendant — return empty so caller falls back.
            var segments = new List<string>();
            var cursor = descendant;
            while (cursor != null && cursor != root)
            {
                segments.Add(cursor.name);
                cursor = cursor.parent;
            }
            if (cursor != root) return string.Empty;
            segments.Reverse();
            return string.Join("/", segments);
        }

        private static void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("FAP_ItemStandStandinRoot");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
        }

        private static string BuildDescription(VAItemStandRegistry.Entry item, Orientation orientation)
        {
            string orientationLabel = orientation == Orientation.Vertical
                ? "vertical (wall mount)"
                : "horizontal (shelf / floor mount)";
            string displayName = item.ItemDrop?.m_itemData?.m_shared?.m_name ?? item.Name;
            return $"<color=#88aaff>Item stand</color> — places a vanilla "
                 + $"{orientationLabel} stand with <i>{displayName}</i> "
                 + $"pre-attached. Normal vanilla persistence; right-click to remove "
                 + $"the item from the placed stand.";
        }
    }

    // Builds an item-stand stand-in's ghost meshes the moment it becomes the selected build piece: SetupPlacementGhost and
    // PlacePiece both read GetSelectedPrefab, so the ghost is always built from a complete stand-in.
    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.GetSelectedPrefab))]
    public static class PieceTable_GetSelectedPrefab_ItemStandVisuals_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GameObject __result)
        {
            if (__result != null) VAItemStandStandinFactory.EnsureVisuals(__result);
        }
    }
}
