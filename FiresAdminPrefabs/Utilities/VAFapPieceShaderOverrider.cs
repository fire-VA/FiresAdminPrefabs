using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Applies the user's per-piece shader overrides to individual placed instances.
    // The prefab template is never mutated — that would bleed into every system
    // reading from it: other mods cloning the prefab, vanilla preview ghosts, hammer
    // icons cached off the template.
    //
    // Two entry points cover both directions. Changing an override raises
    // OnOverridesChanged, and this class walks the live instances of that prefab in
    // ZNetScene and re-applies, so existing placements update without a reload. New
    // spawns are caught by VAFapPieceShaderOverridePatch on ZNetView.Awake.
    //
    // Distinct from VAClutterShaderRepair and ShaderReplacement, which run on prefab
    // templates at init for bundle content; this layer only fires on spawn or mutation.
    public static class VAFapPieceShaderOverrider
    {
        // Cached shader lookups so repeated overrides for the same shader
        // name don't keep calling Shader.Find.
        private static readonly Dictionary<string, Shader> _shaderCache =
            new Dictionary<string, Shader>(StringComparer.Ordinal);

        // An overridden instance gets copies, never the originals changed: one copy per (original material, shader), shared
        // by every instance with that override. Before 1.1.157 this set .shader on renderer.sharedMaterials, which changed
        // the material every instance and the prefab share, and, through a Jotunn/PieceManager mock slot, vanilla's own.
        private static readonly Dictionary<(Material source, Shader shader), Material> _copies =
            new Dictionary<(Material source, Shader shader), Material>();
        private static readonly Dictionary<Material, Material> _sourceOf = new Dictionary<Material, Material>();

        // Prefabs given an override this session, so removing one puts the originals back on their live instances.
        private static readonly HashSet<string> _overridden = new HashSet<string>(StringComparer.Ordinal);

        // ──────────────────────────────────────────────────────────────
        //  Public init / events
        // ──────────────────────────────────────────────────────────────

        // Called once from VaPieces.Awake after VAFapPieceOverrides.Load.
        // Hooks the change event so subsequent right-click mutations
        // re-apply to live instances. Newly-spawned clones are handled
        // by the ZNetView.Awake postfix, not here.
        public static void Initialize()
        {
            VAFapPieceOverrides.OnOverridesChanged -= OnOverridesChanged;
            VAFapPieceOverrides.OnOverridesChanged += OnOverridesChanged;
        }

        // Sweep all stored overrides through every currently-spawned
        // instance. Called from a ZNetScene.Awake postfix so any already-
        // existing instances at world load (e.g., pieces saved from a
        // prior session) pick up their configured override on first
        // appearance.
        public static void ApplyAllAtInit()
        {
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kvp in VAFapPieceOverrides.GetAllWithShader())
            {
                current.Add(kvp.Key);
                _overridden.Add(kvp.Key);
                ApplyToLiveInstances(kvp.Key, kvp.Value);
            }
            var removed = new List<string>();
            foreach (string prefabName in _overridden)
                if (!current.Contains(prefabName)) removed.Add(prefabName);
            foreach (string prefabName in removed)
            {
                _overridden.Remove(prefabName);
                RestoreLiveInstances(prefabName);
            }
        }

        // Fired whenever the user mutates an override (right-click menu).
        // Re-applies every stored override across every live instance.
        // Simple but correct — diff'ing which prefab changed and
        // bounding the walk to just that one's instances is a future
        // optimization (overrides count is small, instance walk is
        // already O(scene size) anyway).
        private static void OnOverridesChanged()
        {
            ApplyAllAtInit();
        }

        // ──────────────────────────────────────────────────────────────
        //  Per-instance apply (called from ZNetView.Awake patch)
        // ──────────────────────────────────────────────────────────────

        // Called from the ZNetView.Awake postfix in
        // VAFapPieceShaderOverridePatch for every fresh clone. Looks up
        // the stored override for the prefab name (stripping the
        // "(Clone)" suffix) and applies it to JUST this instance's
        // renderers. No-op when no override is configured.
        public static void ApplyToInstance(GameObject instance)
        {
            if (instance == null) return;
            string name = instance.name;
            if (string.IsNullOrEmpty(name)) return;
            // Strip "(Clone)" suffix that Instantiate adds.
            int cloneIdx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIdx > 0) name = name.Substring(0, cloneIdx).TrimEnd();

            if (!VAFapPieceOverrides.TryGetShader(name, out var shaderName)) return;
            var shader = ResolveShader(shaderName);
            if (shader == null) return;

            ApplyShaderToRenderers(instance, shader);
        }

        // Walks every active instance of prefabName in the live scene and
        // re-applies the shader override. Required for "user changes the
        // override mid-session and wants to see it now without rebuilding
        // the world." Looks up instances via ZNetScene.m_instances.
        public static void ApplyToLiveInstances(string prefabName, string shaderName)
        {
            if (string.IsNullOrEmpty(prefabName) || string.IsNullOrEmpty(shaderName)) return;
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_instances == null) return;

            var shader = ResolveShader(shaderName);
            if (shader == null) return;

            int prefabHash = prefabName.GetStableHashCode();
            int liveSwapped = 0;
            int liveCount = 0;

            foreach (var kvp in scene.m_instances)
            {
                var znv = kvp.Value;
                if (znv == null) continue;
                var zdo = znv.GetZDO();
                if (zdo == null) continue;
                if (zdo.GetPrefab() != prefabHash) continue;
                liveCount++;
                liveSwapped += ApplyShaderToRenderers(znv.gameObject, shader);
            }

            if (liveCount > 0)
            {
                Debug.Log($"{VaPieces.PluginName}: VAFapPieceShaderOverrider — '{prefabName}': swapped {liveSwapped} material(s) across {liveCount} live instance(s) to '{shaderName}'.");
            }
        }

        // The override was removed: its live instances get their original materials back.
        private static void RestoreLiveInstances(string prefabName)
        {
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_instances == null) return;
            int prefabHash = prefabName.GetStableHashCode();
            int restored = 0, liveCount = 0;
            foreach (var kvp in scene.m_instances)
            {
                var znv = kvp.Value;
                var zdo = znv != null ? znv.GetZDO() : null;
                if (zdo == null || zdo.GetPrefab() != prefabHash) continue;
                liveCount++;
                restored += ApplyShaderToRenderers(znv.gameObject, null);
            }
            if (liveCount > 0)
                Debug.Log($"{VaPieces.PluginName}: VAFapPieceShaderOverrider — '{prefabName}': override removed, {restored} original material(s) back across {liveCount} live instance(s).");
        }

        // ──────────────────────────────────────────────────────────────
        //  Shader catalog (for right-click submenu)
        // ──────────────────────────────────────────────────────────────

        // Session-scoped cache of the full shader-name list. Built on
        // the first ListAvailableShaders call after VaPieces has loaded
        // far enough that ZNetScene + ClutterSystem are populated; the
        // sweep itself is ~milliseconds for the entire ZNetScene prefab
        // table, but no point repeating it every right-click.
        //
        private static List<string> _availableShaderCache;

        // Shader names offered in the override submenu, aggregated from four sources:
        // a pinned preferred baseline (kept in declared order at the top), the shaders
        // ShaderReplacement's runtime sweep has already cached, ClutterSystem's
        // renderers for grass shaders that sweep missed, and a full ZNetScene prefab
        // walk as the catch-all. The walk skips particle renderers and keeps only
        // Custom/* and Standard, so Hidden/* and Sprites/* never surface.
        //
        // Preferred first, then alphabetical, and cached for the session.
        public static List<string> ListAvailableShaders()
        {
            if (_availableShaderCache != null) return _availableShaderCache;

            var preferred = new[]
            {
                "Custom/Grass",
                "Custom/Vegetation",
                "Custom/Piece",
                "Custom/Tree",
                "Custom/StaticRock",
                "Custom/Player",
                "Custom/Creature",
                "Standard",
            };

            var set = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();

            // Pass 1: preferred baseline — skip any that fail to resolve
            // in this Unity build (e.g., shader stripping).
            foreach (var n in preferred)
            {
                if (ResolveShader(n) == null) continue;
                if (set.Add(n)) list.Add(n);
            }

            // Pass 2: ShaderReplacement's cache — the richest single
            // source. Already filtered to vanilla-resolved shaders.
            try
            {
                foreach (var kvp in ShaderReplacement.EnumerateCachedShaders())
                {
                    var n = kvp.Key;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (kvp.Value == null) continue;
                    if (n.Contains("InternalErrorShader")) continue;
                    if (!IsRelevantShaderName(n)) continue;
                    if (set.Add(n)) list.Add(n);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: ListAvailableShaders — ShaderReplacement enumerate threw: {ex.Message}");
            }

            // Pass 3: ClutterSystem renderers — catches clutter-specific
            // shaders that may not appear in ShaderReplacement's sweep.
            var cs = ClutterSystem.instance;
            if (cs?.m_clutter != null)
            {
                foreach (var c in cs.m_clutter)
                {
                    if (c?.m_prefab == null) continue;
                    var ir = c.m_prefab.GetComponent<InstanceRenderer>();
                    var sh = ir?.m_material?.shader;
                    if (sh == null) continue;
                    var n = sh.name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.Contains("InternalErrorShader")) continue;
                    if (!IsRelevantShaderName(n)) continue;
                    if (set.Add(n)) list.Add(n);
                }
            }

            // Pass 4: full ZNetScene.m_prefabs sweep. Catches every
            // remaining shader currently in use anywhere in the prefab
            // table. ~milliseconds for ~6500 prefabs. Skip particle
            // renderers — their shaders aren't useful for piece overrides.
            var scene = ZNetScene.instance;
            if (scene?.m_prefabs != null)
            {
                for (int p = 0; p < scene.m_prefabs.Count; p++)
                {
                    var prefab = scene.m_prefabs[p];
                    if (prefab == null) continue;
                    var renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        var renderer = renderers[r];
                        if (renderer == null) continue;
                        if (renderer is ParticleSystemRenderer) continue;
                        var mats = renderer.sharedMaterials;
                        if (mats == null) continue;
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var mat = mats[m];
                            if (mat?.shader == null) continue;
                            var n = mat.shader.name;
                            if (string.IsNullOrEmpty(n)) continue;
                            if (n.Contains("InternalErrorShader")) continue;
                            if (!IsRelevantShaderName(n)) continue;
                            if (set.Add(n)) list.Add(n);
                        }
                    }
                }
            }

            // Sort with the preferred entries pinned at the front in
            // their declared order, then alphabetical for the rest.
            list.Sort((a, b) =>
            {
                int ai = Array.IndexOf(preferred, a);
                int bi = Array.IndexOf(preferred, b);
                if (ai >= 0 && bi >= 0) return ai.CompareTo(bi);
                if (ai >= 0) return -1;
                if (bi >= 0) return 1;
                return string.Compare(a, b, StringComparison.Ordinal);
            });

            _availableShaderCache = list;
            Debug.Log($"{VaPieces.PluginName}: ListAvailableShaders — built shader list with {list.Count} entries (cached for session).");
            return list;
        }

        // Filter: only surface shaders that make sense to substitute
        // onto a piece's MeshRenderer. Custom/* and Standard cover all
        // the useful options. Hidden/*, Sprites/*, GUI/*, Particle/*,
        // Skybox/*, Mobile/*, etc. would just be noise / actively bad
        // picks for piece overrides.
        private static bool IsRelevantShaderName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (n.StartsWith("Custom/", StringComparison.Ordinal)) return true;
            if (n.Equals("Standard", StringComparison.Ordinal)) return true;
            return false;
        }

        // ──────────────────────────────────────────────────────────────
        //  Internals
        // ──────────────────────────────────────────────────────────────

        private static Shader ResolveShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return null;
            if (_shaderCache.TryGetValue(shaderName, out var cached)) return cached;
            var sh = Shader.Find(shaderName);
            if (sh != null && !sh.name.Contains("InternalErrorShader"))
            {
                _shaderCache[shaderName] = sh;
                return sh;
            }
            _shaderCache[shaderName] = null;
            return null;
        }

        // Points every material slot of `go`'s renderers at the override copy of its original for `shader`, or back at the
        // original when `shader` is null. Skips ParticleSystemRenderer (their shaders are specific to particle rendering).
        // Returns the count of slots changed.
        private static int ApplyShaderToRenderers(GameObject go, Shader shader)
        {
            if (go == null) return 0;
            int swapped = 0;
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (r is ParticleSystemRenderer) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                bool dirty = false;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null) continue;
                    Material source = _sourceOf.TryGetValue(mats[m], out var original) && original != null ? original : mats[m];
                    Material wanted = shader == null || source.shader == shader ? source : CopyFor(source, shader);
                    if (mats[m] == wanted) continue;
                    mats[m] = wanted;
                    swapped++;
                    dirty = true;
                }
                if (dirty) r.sharedMaterials = mats;
            }
            return swapped;
        }

        private static Material CopyFor(Material source, Shader shader)
        {
            if (_copies.TryGetValue((source, shader), out var copy) && copy != null) return copy;
            copy = new Material(source) { name = $"{source.name} (FAP override {shader.name})" };
            copy.shader = shader;
            _copies[(source, shader)] = copy;
            _sourceOf[copy] = source;
            return copy;
        }
    }
}
