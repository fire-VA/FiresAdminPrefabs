using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Routes each bundle prefab to category-specific ZNetView setup at
    // registration time, based on its manifest category (when available) or
    // component signature (fallback): clutter/effect get non-persistent
    // ZNetViews, everything else persistent. Items and clutter are also
    // queued for drain into ObjectDB.m_itemPrefabs / ClutterSystem.m_clutter.
    public static class VABundleAssetRouter
    {
        public enum BundleCategory
        {
            Unknown,
            Piece,
            Clutter,
            Pickable,
            Container,
            Plant,
            MineRock,
            Item,
            Other,
            // One-shot visual / audio effect prefabs (m_placeEffect /
            // m_hitEffect chains). Routed to non-persistent ZNetViews so their
            // transient ZDOs never reach the world save — otherwise saved
            // effect ZDOs collide with fresh ones on respawn ("Double ZNetview"
            // cascade) and balloon the world file.
            Effect,
        }

        // Manifest-declared categories per prefab name. When a prefab appears
        // here the manifest classification wins over component-based detection
        // — EXCEPT for ItemDrop, which always overrides (see ResolveCategory).
        private static readonly Dictionary<string, BundleCategory> _manifestCategoryByPrefab
            = new Dictionary<string, BundleCategory>(StringComparer.Ordinal);

        // Categories resolved during Apply(), cached for GetCategory.
        private static readonly Dictionary<string, BundleCategory> _resolvedCategoryByPrefab
            = new Dictionary<string, BundleCategory>(StringComparer.Ordinal);

        // Items queued for ObjectDB.m_itemPrefabs registration. Drained
        // by ObjectDB_Awake_VARouterDrainItems_Patch (below).
        public static readonly List<GameObject> ItemPrefabsToRegister = new List<GameObject>();
        private static readonly HashSet<string> _itemPrefabNamesQueued
            = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _itemNamesAlreadyRegistered
            = new HashSet<string>(StringComparer.Ordinal);

        // Clutter prefabs queued for ClutterSystem.m_clutter registration.
        // Drained by VAExternalBundleLoader.RegisterClutterFromManifests
        // at ClutterSystem.Awake postfix.
        public static readonly Dictionary<string, GameObject> ClutterPrefabsToRegister
            = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        // Per-category counters for the end-of-injection summary log.
        public static readonly Dictionary<BundleCategory, int> CategoryCounts
            = new Dictionary<BundleCategory, int>();

        // ──────────────────────────────────────────────────────────────
        // Manifest hookup
        // ──────────────────────────────────────────────────────────────

        // Parses a manifest "category" string into the enum value.
        // Tolerates case + whitespace; unknown strings → BundleCategory.Unknown.
        public static BundleCategory ParseManifestCategory(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return BundleCategory.Unknown;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "piece":     return BundleCategory.Piece;
                case "clutter":   return BundleCategory.Clutter;
                case "pickable":  return BundleCategory.Pickable;
                case "container": return BundleCategory.Container;
                case "plant":     return BundleCategory.Plant;
                case "minerock":  return BundleCategory.MineRock;
                case "item":      return BundleCategory.Item;
                case "effect":    return BundleCategory.Effect;
                case "vfx":       return BundleCategory.Effect;
                case "sfx":       return BundleCategory.Effect;
                case "fx":        return BundleCategory.Effect;
                case "other":     return BundleCategory.Other;
                default:          return BundleCategory.Unknown;
            }
        }

        // Called by VAExternalBundleLoader.LoadClutterManifestForBundle
        // for each manifest prefab entry. Records the manifest's
        // classification so Apply() can prefer it over component detection.
        public static void RegisterManifestCategory(string prefabName, string manifestCategoryString)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            var cat = ParseManifestCategory(manifestCategoryString);
            if (cat == BundleCategory.Unknown) return;
            _manifestCategoryByPrefab[prefabName] = cat;
        }

        // ──────────────────────────────────────────────────────────────
        // Per-prefab apply
        // ──────────────────────────────────────────────────────────────

        // Main per-prefab entry: resolves the category, applies the
        // category-specific ZNetView setup, and queues clutter/item
        // registrations. Called per external prefab before it joins m_prefabs.
        public static BundleCategory Apply(GameObject prefab) => Apply(prefab, null);

        // hierarchy: VAExternalBundleLoader's single walk of this prefab, every component in
        // GetComponentsInChildren order, read here instead of walking the prefab again. Null walks
        // the prefab as before.
        public static BundleCategory Apply(GameObject prefab, List<Component> hierarchy)
        {
            if (prefab == null) return BundleCategory.Unknown;

            var cat = ResolveCategory(prefab, hierarchy);
            _resolvedCategoryByPrefab[prefab.name] = cat;

            int prev;
            CategoryCounts.TryGetValue(cat, out prev);
            CategoryCounts[cat] = prev + 1;

            switch (cat)
            {
                case BundleCategory.Clutter:
                    NormalizeZNetView(prefab, persistent: false, hierarchy);
                    QueueClutter(prefab);
                    break;

                case BundleCategory.Effect:
                    NormalizeZNetView(prefab, persistent: false, hierarchy);
                    break;

                case BundleCategory.Item:
                    NormalizeZNetView(prefab, persistent: true, hierarchy);
                    QueueItem(prefab);
                    break;

                case BundleCategory.Piece:
                case BundleCategory.Pickable:
                case BundleCategory.Container:
                case BundleCategory.Plant:
                case BundleCategory.MineRock:
                case BundleCategory.Other:
                case BundleCategory.Unknown:
                default:
                    NormalizeZNetView(prefab, persistent: true, hierarchy);
                    break;
            }

            return cat;
        }

        // Resolved-cache first, then manifest-declared category.
        public static BundleCategory GetCategory(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return BundleCategory.Unknown;
            BundleCategory cat;
            if (_resolvedCategoryByPrefab.TryGetValue(prefabName, out cat)) return cat;
            if (_manifestCategoryByPrefab.TryGetValue(prefabName, out cat)) return cat;
            return BundleCategory.Unknown;
        }

        // ──────────────────────────────────────────────────────────────
        // Category resolution
        // ──────────────────────────────────────────────────────────────

        // Priority: ItemDrop on root overrides everything (an item even if the
        // manifest mis-classified it), then a manifest entry when present, then
        // component-based detection.
        private static BundleCategory ResolveCategory(GameObject prefab, List<Component> hierarchy)
        {
            if (prefab.GetComponent<ItemDrop>() != null)
                return BundleCategory.Item;

            BundleCategory manifestCat;
            if (_manifestCategoryByPrefab.TryGetValue(prefab.name, out manifestCat)
                && manifestCat != BundleCategory.Unknown)
            {
                return manifestCat;
            }

            return DetermineFromComponents(prefab, hierarchy);
        }

        // Component-based classifier. Mirrors BundleManifestGenerator's
        // priority order verbatim so manifested and unmanifested bundles
        // produce the same answer. Effect detection is checked LAST so a
        // gameplay prefab with "VFX" in its name still resolves to its role.
        private static BundleCategory DetermineFromComponents(GameObject prefab, List<Component> hierarchy)
        {
            if (prefab.GetComponent<MineRock>() != null) return BundleCategory.MineRock;
            if (prefab.GetComponent<MineRock5>() != null) return BundleCategory.MineRock;
            if (prefab.GetComponent<Container>() != null) return BundleCategory.Container;
            if (prefab.GetComponent<Plant>() != null) return BundleCategory.Plant;
            if (prefab.GetComponent<Pickable>() != null) return BundleCategory.Pickable;
            if (prefab.GetComponent<Piece>() != null || prefab.GetComponent<WearNTear>() != null)
                return BundleCategory.Piece;
            if (HasInChildren<InstanceRenderer>(prefab, hierarchy))
                return BundleCategory.Clutter;

            // ── Effect detection ───────────────────────────────────
            if (IsEffectByName(prefab.name)) return BundleCategory.Effect;
            if (HasInChildren<TimedDestruction>(prefab, hierarchy))
                return BundleCategory.Effect;
            if (HasInChildren<ParticleSystem>(prefab, hierarchy)
                || HasInChildren<AudioSource>(prefab, hierarchy))
            {
                return BundleCategory.Effect;
            }

            return BundleCategory.Other;
        }

        // GetComponentInChildren<T>(includeInactive: true) != null, answered from the single walk when
        // there is one. A component destroyed since the walk (a stripped singleton) is fake-null and
        // does not count, as it would not be found by a fresh search either.
        private static bool HasInChildren<T>(GameObject prefab, List<Component> hierarchy) where T : Component
        {
            if (hierarchy == null) return prefab.GetComponentInChildren<T>(includeInactive: true) != null;
            foreach (var component in hierarchy)
                if (component is T found && found != null) return true;
            return false;
        }

        // Case-insensitive effect-prefab name markers (VFX/SFX/FX prefixes plus
        // the Dato/Krumpac material-effect prefix). Conservative: a missed tag
        // just falls through to the Particle/Audio check or lands in Other.
        private static readonly string[] _effectNameMarkers =
        {
            "_vfx_", "vfx_", "_sfx_", "sfx_",
            "_fx_", "fx_",
            "_krump_mat_",
        };

        private static bool IsEffectByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < _effectNameMarkers.Length; i++)
            {
                if (lower.IndexOf(_effectNameMarkers[i], StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        // ──────────────────────────────────────────────────────────────
        // ZNetView normalization (shared helper)
        // ──────────────────────────────────────────────────────────────

        // Vanilla components that dereference the ZNetView on their own GameObject with no null check. They only
        // ever drove the nested network object, so they leave with its ZNetView.
        private static readonly Type[] ComponentsRequiringOwnZNetView =
        {
            typeof(ZSyncTransform), typeof(Projectile), typeof(CreatureSpawner), typeof(ItemDrop), typeof(Destructible),
        };

        // Reused by NormalizeZNetView for the views read off the single walk. Main thread only.
        private static readonly List<ZNetView> s_walkViews = new List<ZNetView>();

        // Guarantees the prefab has exactly one ZNetView (preferring root)
        // with the given persistence flag, destroying extras. Catching a
        // ZNetView baked into a child avoids the "Double ZNetview" spam that
        // a root-only check + AddComponent caused. The child stays as a visual
        // of the prefab's one network object.
        public static void NormalizeZNetView(GameObject prefab, bool persistent) =>
            NormalizeZNetView(prefab, persistent, null);

        // hierarchy: as in Apply. The walk lists components in the same depth-first order as
        // GetComponentsInChildren<ZNetView>, so a prefab with no root view keeps the same nested one.
        public static void NormalizeZNetView(GameObject prefab, bool persistent, List<Component> hierarchy)
        {
            if (prefab == null) return;

            IList<ZNetView> allViews = hierarchy == null
                ? (IList<ZNetView>)prefab.GetComponentsInChildren<ZNetView>(includeInactive: true)
                : VAExternalBundleLoader.CollectFromWalk(hierarchy, s_walkViews);
            ZNetView keep = null;
            for (int i = 0; i < allViews.Count; i++)
            {
                var view = allViews[i];
                if (view == null) continue;
                if (view.gameObject == prefab) { keep = view; break; }
            }
            if (keep == null && allViews.Count > 0) keep = allViews[0];

            var removedDependents = new List<string>();
            for (int i = 0; i < allViews.Count; i++)
            {
                var view = allViews[i];
                if (view == null || view == keep) continue;
                RemoveNestedZNetView(view, removedDependents);
            }
            s_walkViews.Clear();

            if (keep == null)
                keep = prefab.AddComponent<ZNetView>();
            keep.m_persistent = persistent;

            if (removedDependents.Count > 0)
                Debug.Log($"{VaPieces.PluginName}: NormalizeZNetView — '{prefab.name}' nests network objects; removed {removedDependents.Count} component(s) that need the nested ZNetView [{string.Join(", ", removedDependents)}].");
        }

        private static void RemoveNestedZNetView(ZNetView view, List<string> removedDependents)
        {
            var nested = view.gameObject;
            foreach (var dependentType in ComponentsRequiringOwnZNetView)
            {
                foreach (var dependent in nested.GetComponents(dependentType))
                {
                    removedDependents.Add($"{dependentType.Name}@{nested.name}");
                    UnityEngine.Object.DestroyImmediate(dependent, allowDestroyingAssets: true);
                }
            }
            UnityEngine.Object.DestroyImmediate(view, allowDestroyingAssets: true);
        }

        // ──────────────────────────────────────────────────────────────
        // Clutter queueing
        // ──────────────────────────────────────────────────────────────

        private static void QueueClutter(GameObject prefab)
        {
            if (prefab == null || string.IsNullOrEmpty(prefab.name)) return;
            if (ClutterPrefabsToRegister.ContainsKey(prefab.name)) return;
            ClutterPrefabsToRegister[prefab.name] = prefab;
        }

        // ──────────────────────────────────────────────────────────────
        // Item queueing
        // ──────────────────────────────────────────────────────────────

        private static void QueueItem(GameObject prefab)
        {
            if (prefab == null || string.IsNullOrEmpty(prefab.name)) return;
            if (!_itemPrefabNamesQueued.Add(prefab.name)) return;
            ItemPrefabsToRegister.Add(prefab);
        }

        // Drains queued bundle-item prefabs into ObjectDB.m_items, then calls
        // the private UpdateRegisters via reflection to rebuild the hash dicts
        // so the items become findable. Idempotent across repeat Awake fires
        // via _itemNamesAlreadyRegistered.
        //
        // Every world load builds a new ObjectDB with vanilla's own m_items, so the names already
        // registered belong to one instance. Kept for the whole process, they made the second
        // join of a session skip every bundle item: the new ObjectDB never got them, and the bot's
        // rejoin logged "Failed to find item prefab" 198 times while the first join logged none
        // ([ghost], Tools\FAP_CLIENT_LOAD_COST.md). Inventory.Load drops an item it cannot find,
        // so the next save would have lost it.
        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        public static class ObjectDB_Awake_VARouterDrainItems_Patch
        {
            private static System.Reflection.MethodInfo _updateRegistersMethod;
            private static ObjectDB _drainedInstance;

            [HarmonyPostfix]
            public static void Postfix(ObjectDB __instance)
            {
                if (__instance == null) return;
                if (ItemPrefabsToRegister.Count == 0) return;

                // The main menu's ObjectDB: FejdStartup.SetupObjectDB adds it and then CopyOtherDB replaces its m_items
                // with the ObjectDB prefab's list, so anything drained here is thrown away. It gets nothing, as it did
                // before the per-instance reset below, when every name was already registered by then.
                if (__instance.GetComponent<FejdStartup>() != null) return;

                if (!ReferenceEquals(_drainedInstance, __instance))
                {
                    _drainedInstance = __instance;
                    _itemNamesAlreadyRegistered.Clear();
                }

                int added = 0, skipped = 0, collisions = 0;
                // Every name m_items already holds, read once: the old per-item linear scan compared each queued item
                // against every entry through UnityEngine.Object.name (a native call that allocates), items x m_items
                // times, ~487 ms at the join ([ghost], Tools\FAP_CLIENT_LOAD_COST.md #2). Same result, O(N + M).
                var itemNames = new HashSet<string>(StringComparer.Ordinal);
                if (__instance.m_items != null)
                    foreach (var existing in __instance.m_items)
                        if (existing != null) itemNames.Add(existing.name);

                foreach (var prefab in ItemPrefabsToRegister)
                {
                    if (prefab == null) { skipped++; continue; }
                    string name = prefab.name;
                    if (_itemNamesAlreadyRegistered.Contains(name)) { skipped++; continue; }

                    // Vanilla + sibling-mod items win: TryGetItemPrefab, plus the names in m_items in case the hash
                    // dict is stale.
                    bool collision = itemNames.Contains(name) || __instance.TryGetItemPrefab(name, out _);

                    if (collision)
                    {
                        collisions++;
                        _itemNamesAlreadyRegistered.Add(name);
                        if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                            Debug.Log($"{VaPieces.PluginName}: VABundleAssetRouter — ObjectDB already has an item named '{name}'; skipping bundle-item registration to avoid clobbering.");
                        continue;
                    }

                    __instance.m_items.Add(prefab);
                    itemNames.Add(name);
                    _itemNamesAlreadyRegistered.Add(name);
                    added++;
                }

                if (added > 0)
                {
                    try
                    {
                        if (_updateRegistersMethod == null)
                        {
                            _updateRegistersMethod = AccessTools.Method(typeof(ObjectDB), "UpdateRegisters");
                        }
                        _updateRegistersMethod?.Invoke(__instance, null);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VABundleAssetRouter — ObjectDB.UpdateRegisters reflection invoke threw: {ex.Message}");
                    }
                }

                if (added > 0 || collisions > 0)
                {
                    Debug.Log($"{VaPieces.PluginName}: VABundleAssetRouter — registered {added} bundle-item prefab(s) into ObjectDB.m_items ({collisions} skipped due to name collision, {skipped} skipped as already-registered or null).");
                }
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Summary helpers
        // ──────────────────────────────────────────────────────────────

        // One-line per-category summary, e.g.
        // "Clutter=215, Item=33, Piece=3400, ...". Called by
        // VAExternalBundleLoader after the registration loop.
        public static string FormatCategorySummary()
        {
            if (CategoryCounts.Count == 0) return "(no prefabs routed)";
            var parts = new List<string>(CategoryCounts.Count);
            foreach (var kvp in CategoryCounts)
            {
                if (kvp.Value <= 0) continue;
                parts.Add($"{kvp.Key}={kvp.Value}");
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts);
        }
    }
}
