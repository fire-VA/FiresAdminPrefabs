using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Managers
{
    // Detects + repairs broken materials on bundle prefabs at injection
    // time (null shader, null material slot, Unity error shader, or missing
    // main texture). Resolves a replacement in-place via manifest descriptor,
    // VAShaderLibrary, Shader.Find, prefab-type heuristic, then Standard.
    // A per-session Material→bool cache makes the pass idempotent across the
    // many renderers that share a material.
    public static class VAMaterialRepair
    {
        // Per-session cache of materials we've already inspected. The
        // value is `true` if we repaired it, `false` if we examined it
        // and decided no action was needed. Either way, we skip on
        // subsequent visits.
        private static readonly Dictionary<Material, bool> _examined =
            new Dictionary<Material, bool>();

        // Vanilla shader handles, cached on first repair pass so we
        // don't re-Shader.Find on every material. Lazy-populated.
        private static Shader _vanillaCustomVegetation;
        private static Shader _vanillaCustomPiece;
        private static Shader _vanillaCustomCreature;
        private static Shader _vanillaStandard;
        private static Shader _vanillaParticleUnlit;
        private static bool _vanillaCacheBuilt;

        // Vanilla material templates by category (rock, wood, metal, foliage,
        // fabric, default), built lazily from known vanilla prefab names. Used
        // as a texture source for null/textureless slots; callers copy via
        // new Material(template) so per-prefab tweaks don't leak into vanilla.
        private static Dictionary<string, Material> _vanillaMaterialTemplates;

        // Diagnostic counters. Drained by VAExternalBundleLoader's
        // end-of-injection summary log.
        public static int TotalPrefabsScanned;
        public static int TotalMaterialsScanned;
        public static int TotalMaterialsRepaired;
        public static int TotalMaterialsNullSlot;        // sharedMaterials[i] == null
        public static int TotalMaterialsNullShader;      // material.shader == null
        public static int TotalMaterialsErrorShader;    // material.shader is Hidden/InternalErrorShader
        public static int TotalMaterialsUnrepairable;   // resolver returned nothing

        // "Repaired" only means a shader was found. A material that comes out of the repair
        // with a shader but NO main texture renders untextured white, which reads as a broken
        // prefab and counted as a success in every summary before this. This is that number,
        // plus which prefabs it happened on, because the per-material lines are behind verbose
        // logging and the one run that mattered did not have it on.
        public static int TotalMaterialsNoTexture;
        private static readonly Dictionary<string, int> _noTextureByPrefab =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // Materials whose real texture was recovered from the manifests by material name,
        // rather than substituted with a lookalike.
        public static int TotalMaterialsTextureByName;

        // Renderers that arrived with ZERO material slots. Counted separately from a null
        // slot inside a sized array, because these were skipped outright before 1.1.118.
        public static int TotalMaterialsEmptyArray;
        private static readonly Dictionary<string, int> _emptyRendererByPrefab =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static void NoteEmptyRendererPrefab(GameObject prefab)
        {
            if (prefab == null) return;
            string key = prefab.name ?? "(unnamed)";
            _emptyRendererByPrefab.TryGetValue(key, out int n);
            _emptyRendererByPrefab[key] = n + 1;
        }

        // Wants an albedo and has none — the definition of the white case.
        private static bool NeedsMainTexture(Material mat)
        {
            try { return mat != null && mat.HasProperty("_MainTex") && mat.mainTexture == null; }
            catch { return false; }
        }

        // material name -> the texture the manifests record for it, built from EVERY loaded
        // manifest. A bundle's manifest records the same material many times (once per renderer
        // slot that uses it) and only some of those entries carry its texture: 'AshWood_mat_Copy'
        // appears with "mainTexture": "BlackWood_d" in one place and null in another. 5,419
        // entries across the shipped bundles are null, so reading only the entry in front of you
        // loses a texture the manifest plainly holds a few lines away.
        //
        // This recovers the REAL texture rather than substituting a lookalike.
        private static Dictionary<string, string> _textureByMaterialName;

        private static void EnsureMaterialNameTextureIndex()
        {
            if (_textureByMaterialName != null) return;
            _textureByMaterialName = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var rendererList in VAExternalBundleLoader.AllRendererDescriptors())
                {
                    if (rendererList == null) continue;
                    foreach (var rd in rendererList)
                    {
                        if (rd?.materials == null) continue;
                        foreach (var md in rd.materials)
                        {
                            if (md == null) continue;
                            if (string.IsNullOrEmpty(md.name)) continue;
                            if (string.IsNullOrEmpty(md.mainTexture)) continue;
                            if (!_textureByMaterialName.ContainsKey(md.name))
                                _textureByMaterialName[md.name] = md.mainTexture;
                        }
                    }
                }
                Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair — material-name texture index built: {_textureByMaterialName.Count} material(s) whose texture the manifests name somewhere.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — could not build the material-name texture index: {ex.Message}");
            }
        }

        // The texture this material is actually supposed to use, from the manifests, by material
        // name. Null when no manifest entry anywhere names one.
        private static Texture ResolveTextureByMaterialName(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return null;
            EnsureMaterialNameTextureIndex();

            string textureName;
            if (!_textureByMaterialName.TryGetValue(materialName, out textureName))
            {
                // Bundle materials get an "(Instance)"/"_Copy" suffix as they are cloned around;
                // the manifest holds the base name.
                string trimmed = StripMaterialNameSuffix(materialName);
                if (trimmed == materialName) return null;
                if (!_textureByMaterialName.TryGetValue(trimmed, out textureName)) return null;
            }

            return VATextureLibrary.TryGetTexture(textureName, out var texture) ? texture : null;
        }

        private static string StripMaterialNameSuffix(string name)
        {
            string s = name;
            for (int guard = 0; guard < 4; guard++)
            {
                if (s.EndsWith(" (Instance)", StringComparison.Ordinal)) { s = s.Substring(0, s.Length - 11); continue; }
                if (s.EndsWith("(Clone)", StringComparison.Ordinal)) { s = s.Substring(0, s.Length - 7); continue; }
                if (s.EndsWith("_Copy", StringComparison.Ordinal)) { s = s.Substring(0, s.Length - 5); continue; }
                break;
            }
            return s.TrimEnd();
        }

        private static void NoteRepairResult(Material mat, GameObject prefab)
        {
            if (mat == null || prefab == null) return;
            bool textureless;
            try { textureless = mat.HasProperty("_MainTex") && mat.mainTexture == null; }
            catch { return; }
            if (!textureless) return;

            TotalMaterialsNoTexture++;
            string key = prefab.name ?? "(unnamed)";
            _noTextureByPrefab.TryGetValue(key, out int n);
            _noTextureByPrefab[key] = n + 1;
        }

        // The worst offenders, so the summary names what to go and look at in-world.
        private static string FormatOffenders(Dictionary<string, int> counts, int take)
        {
            if (counts == null || counts.Count == 0) return string.Empty;
            var worst = new List<KeyValuePair<string, int>>(counts);
            worst.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < worst.Count && i < take; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(worst[i].Key).Append(" x").Append(worst[i].Value);
            }
            if (worst.Count > take) sb.Append(", +").Append(worst.Count - take).Append(" more");
            return sb.ToString();
        }

        // Frame budget for the paced async repair pass. Matches the
        // 100ms cadence used by VAVanillaPieceManager + the blueprint
        // ghost-visuals coroutine so all three share the same yield
        // rhythm during world load.
        private const long RepairFrameBudgetMs = 100;

        // Standard shader cutout tuning applied when forcing foliage-style
        // alpha clipping. _Cutoff at the Standard default; _Mode = 1 selects
        // Standard's Cutout rendering mode.
        private const float StandardCutoutThreshold = 0.5f;
        private const float StandardModeCutout = 1f;

        // Paced bulk-repair coroutine that walks ExternalPrefabs one frame
        // after ZNetScene.Awake, yielding every RepairFrameBudgetMs. Runs
        // before ZoneSystem loads zones in earnest so zone/clutter references
        // resolve repaired. emitSummary emits the diagnostic summary log on
        // completion.
        public static IEnumerator RepairManyAsync(
            IEnumerable<GameObject> prefabs,
            bool emitSummary)
        {
            if (prefabs == null) yield break;
            yield return null;

            var frameTimer = Stopwatch.StartNew();
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                try { RepairPrefab(prefab); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair.RepairManyAsync threw on '{prefab.name}': {ex.Message}");
                }

                if (frameTimer.ElapsedMilliseconds >= RepairFrameBudgetMs)
                {
                    yield return null;
                    frameTimer.Restart();
                }
            }

            // 1.1.164: the no-LOD bundle prefabs' impostors, baked after every prefab is repaired (a couple a frame).
            yield return VerdantsAscent.Utilities.VABundleImpostors.BakeAll();

            if (emitSummary && TotalPrefabsScanned > 0)
            {
                Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair (deferred) — {FormatSummary()}.");
                Debug.Log($"{VaPieces.PluginName}: VAInjectedShaders — {VAInjectedShaders.FormatSummary()}.");
            }
        }

        // Walks every Renderer + sharedMaterials array on the prefab and
        // repairs in-place. Called once per external prefab, after the router
        // applies category setup and before the prefab joins scene.m_prefabs.
        public static void RepairPrefab(GameObject prefab)
        {
            if (prefab == null) return;
            TotalPrefabsScanned++;

            EnsureVanillaShaderCache();
            AdoptInstancedMaterials(prefab);

            // Schema v4+ manifest descriptors keyed by transform-path-from-root;
            // used to reconstruct broken materials by name instead of guessing.
            var rendererDescs = VAExternalBundleLoader.GetRendererDescriptorsForPrefab(prefab.name);
            Dictionary<string, VAExternalBundleLoader.RendererDescriptorDto> descByPath = null;
            if (rendererDescs != null && rendererDescs.Count > 0)
            {
                descByPath = new Dictionary<string, VAExternalBundleLoader.RendererDescriptorDto>(StringComparer.Ordinal);
                foreach (var desc in rendererDescs)
                {
                    if (desc == null) continue;
                    string key = desc.path ?? string.Empty;
                    if (!descByPath.ContainsKey(key)) descByPath[key] = desc;
                }
            }

            Renderer[] renderers;
            try { renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true); }
            catch { return; }
            if (renderers == null || renderers.Length == 0) return;

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                Material[] mats;
                try { mats = renderer.sharedMaterials; }
                catch { continue; }

                VAExternalBundleLoader.RendererDescriptorDto rendererDesc = null;
                bool rendererDescResolved = false;

                // A renderer with NO material slots at all. This is the case that renders
                // white — a missing material is white, a broken shader is magenta — and it
                // used to `continue` here before anything counted it, so every summary
                // reported unrepairable=0 while the prefab was plainly white in-world.
                //
                // The manifest knows how many materials this renderer is supposed to have,
                // so rebuild the whole set from it; with no descriptor, one category
                // fallback is still better than a renderer with nothing bound.
                if (mats == null || mats.Length == 0)
                {
                    ResolveRendererDesc(descByPath, renderer, prefab.transform, ref rendererDesc, ref rendererDescResolved);
                    int wanted = rendererDesc?.materials != null && rendererDesc.materials.Count > 0
                        ? rendererDesc.materials.Count
                        : 1;
                    var rebuilt = new Material[wanted];
                    int filled = 0;
                    for (int i = 0; i < wanted; i++)
                    {
                        var desc = rendererDesc?.materials != null && i < rendererDesc.materials.Count
                            ? rendererDesc.materials[i]
                            : null;
                        TotalMaterialsScanned++;
                        TotalMaterialsEmptyArray++;
                        rebuilt[i] = BuildFallbackMaterial(prefab, i, desc, renderer);
                        if (rebuilt[i] != null)
                        {
                            NoteRepairResult(rebuilt[i], prefab);
                            TotalMaterialsRepaired++;
                            filled++;
                        }
                        else TotalMaterialsUnrepairable++;
                    }

                    if (filled > 0)
                    {
                        try { renderer.sharedMaterials = rebuilt; }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — could not bind {filled} rebuilt material(s) to an empty renderer on '{prefab.name}': {ex.Message}");
                        }
                        NoteEmptyRendererPrefab(prefab);
                    }
                    continue;
                }

                bool slotChanged = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    TotalMaterialsScanned++;
                    var mat = mats[i];

                    VAExternalBundleLoader.MaterialDescriptorDto matDesc = null;

                    if (mat == null)
                    {
                        TotalMaterialsNullSlot++;
                        matDesc = MaterialDescAt(descByPath, renderer, prefab.transform, i, ref rendererDesc, ref rendererDescResolved);
                        var fallback = BuildFallbackMaterial(prefab, i, matDesc, renderer);
                        if (fallback != null)
                        {
                            mats[i] = fallback;
                            slotChanged = true;
                            TotalMaterialsRepaired++;
                        }
                        else
                        {
                            TotalMaterialsUnrepairable++;
                        }
                        continue;
                    }

                    if (VAInjectedShaders.TryGameMaterial(mat, out var gameMaterial))
                    {
                        mats[i] = gameMaterial;
                        slotChanged = true;
                        continue;
                    }

                    // Shared materials are repaired once across the many
                    // renderers that reference them.
                    if (_examined.ContainsKey(mat)) continue;

                    bool particle = VAInjectedShaders.IsParticle(renderer, mat);
                    bool shaderBroken = false;
                    Shader shader = null;
                    try { shader = mat.shader; } catch { shader = null; }

                    if (shader == null)
                    {
                        TotalMaterialsNullShader++;
                        shaderBroken = true;
                    }
                    else if (IsErrorShader(shader))
                    {
                        TotalMaterialsErrorShader++;
                        shaderBroken = true;
                    }
                    else
                    {
                        VAInjectedShaders.Adopt(mat, particle);
                    }

                    bool textureMissing = !shaderBroken && IsMainTextureMissing(mat);

                    if (!shaderBroken && !textureMissing)
                    {
                        _examined[mat] = false;
                        continue;
                    }

                    // Past this point the material genuinely needs work, so the descriptor is worth resolving.
                    matDesc = MaterialDescAt(descByPath, renderer, prefab.transform, i, ref rendererDesc, ref rendererDescResolved);

                    if (textureMissing && !shaderBroken)
                    {
                        // Shader OK but texture missing. PRIORITY 1: reapply the
                        // descriptor's recorded texture to the existing material.
                        if (matDesc != null && !string.IsNullOrEmpty(matDesc.mainTexture))
                        {
                            if (VATextureLibrary.TryGetTexture(matDesc.mainTexture, out var texture))
                            {
                                try
                                {
                                    // _MainTex guard: writing the getter/setter on a
                                    // shader lacking it spams a per-access Unity error.
                                    if (mat.HasProperty("_MainTex"))
                                        mat.mainTexture = texture;
                                    ApplyDescriptorProperties(mat, matDesc);
                                    _examined[mat] = true;
                                    TotalMaterialsRepaired++;
                                    if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                                    {
                                        Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair — manifest-resolved texture '{matDesc.mainTexture}' applied to material '{mat.name}' on '{prefab.name}' (slot {i}).");
                                    }
                                    continue;
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — texture apply threw on '{mat.name}': {ex.Message}");
                                }
                            }
                        }

                        // PRIORITY 1b: the descriptor in front of us has no texture name, but the
                        // manifests name this material's texture somewhere else. Bind the real
                        // one to the REAL material — the shader is fine, only the albedo is
                        // unbound, so replacing the whole slot would throw away a working
                        // material and its properties to show a lookalike.
                        var byName = ResolveTextureByMaterialName(mat.name);
                        if (byName != null && mat.HasProperty("_MainTex"))
                        {
                            try
                            {
                                mat.mainTexture = byName;
                                if (matDesc != null) ApplyDescriptorProperties(mat, matDesc);
                                ApplyAlphaCutoutIfNeeded(mat, prefab);
                                _examined[mat] = true;
                                TotalMaterialsRepaired++;
                                TotalMaterialsTextureByName++;
                                continue;
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — name-resolved texture apply threw on '{mat.name}': {ex.Message}");
                            }
                        }

                        // PRIORITY 2: substitute the whole slot via vanilla
                        // template / descriptor reconstruction.
                        TotalMaterialsNullShader++;
                        var substitute = BuildFallbackMaterial(prefab, i, matDesc, renderer);
                        if (substitute != null)
                        {
                            mats[i] = substitute;
                            NoteRepairResult(substitute, prefab);
                            slotChanged = true;
                            _examined[mat] = true;
                            TotalMaterialsRepaired++;
                            if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                            {
                                Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair — substituted material on '{prefab.name}' (slot {i}): shader was OK but mainTexture missing → category '{ClassifyPrefabForMaterial(prefab)}'.");
                            }
                        }
                        else
                        {
                            _examined[mat] = false;
                            TotalMaterialsUnrepairable++;
                        }
                        continue;
                    }

                    string originalShaderName = SafeShaderName(shader);
                    bool adopted = !string.IsNullOrEmpty(matDesc?.shader) && VAInjectedShaders.Adopt(mat, particle, matDesc.shader);
                    var replacement = adopted ? mat.shader : ResolveReplacementShader(originalShaderName, prefab, matDesc, renderer);
                    if (replacement != null)
                    {
                        try
                        {
                            if (!adopted) mat.shader = replacement;
                            if (matDesc != null)
                            {
                                ApplyDescriptorProperties(mat, matDesc);
                            }
                            // Same _MainTex guard as above.
                            if (matDesc != null
                                && !string.IsNullOrEmpty(matDesc.mainTexture)
                                && mat.HasProperty("_MainTex")
                                && (mat.mainTexture == null))
                            {
                                if (VATextureLibrary.TryGetTexture(matDesc.mainTexture, out var texture))
                                {
                                    try { mat.mainTexture = texture; } catch { }
                                }
                            }
                            ApplyAlphaCutoutIfNeeded(mat, prefab);
                            NoteRepairResult(mat, prefab);
                            _examined[mat] = true;
                            TotalMaterialsRepaired++;
                            if (ConfigManager.Instance != null && ConfigManager.Instance.configVerboseLogging.Value)
                            {
                                Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair — fixed material '{mat.name}' on '{prefab.name}': shader '{originalShaderName}' → '{replacement.name}'.");
                            }
                        }
                        catch (Exception ex)
                        {
                            _examined[mat] = false;
                            TotalMaterialsUnrepairable++;
                            Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — failed to set replacement shader on '{mat.name}' / '{prefab.name}': {ex.Message}");
                        }
                    }
                    else
                    {
                        _examined[mat] = false;
                        TotalMaterialsUnrepairable++;
                    }
                }

                if (slotChanged)
                {
                    try { renderer.sharedMaterials = mats; }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — failed to assign repaired sharedMaterials on '{prefab.name}': {ex.Message}");
                    }
                }
            }

            // ─── Always-run alpha-cutout pass for foliage-classified prefabs ───
            //
            // The per-slot loop only enables cutout when it swaps a shader or
            // builds a fallback. Tents / thatch / fabric arrive with healthy
            // shaders that aren't in cutout mode (the Krump_B_Tent_Sticks
            // white-square problem), so force-enable cutout here regardless of
            // shader state. ApplyAlphaCutoutIfNeeded is idempotent; the
            // _examined cache is skipped because its no-action entries would
            // wrongly short-circuit us.
            if (ClassifyPrefabForMaterial(prefab) == "foliage")
            {
                foreach (var renderer in renderers)
                {
                    if (renderer == null || renderer is ParticleSystemRenderer) continue;
                    Material[] mats;
                    try { mats = renderer.sharedMaterials; }
                    catch { continue; }
                    if (mats == null) continue;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var material = mats[i];
                        if (material == null) continue;
                        ApplyAlphaCutoutIfNeeded(material, prefab);
                    }
                }
            }

            // Worldgen-only bundle nature props (trees, mushrooms, pickables, rocks) never pass through the hammer
            // registration that swaps an un-Valheim shader to Custom/Vegetation (or Custom/Piece for rocks). On Unity's
            // Standard shader they render pure white (e.g. ElderMushroom_bal, authored on Standard). RepairPrefab runs for
            // EVERY injected bundle prefab, so apply the same nature swap here. It is guarded to the same prefabs the hammer
            // path treats as nature, and ApplyBundleNatureShader skips any material VAInjectedShaders already adopted
            // (Balrond/Jotunn/Custom), so authored materials are untouched and vanilla prefabs are never in this list.
            if (VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out var natureBundle)
                && (ConfigManager.Instance.IsBundleInNatureShaderAllOf(natureBundle) || VAVanillaPieceManager.IsNaturePrefab(prefab)))
            {
                try { VerdantsAscent.Utilities.ShaderReplacement.ApplyBundleNatureShader(prefab); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: nature-shader swap threw on '{prefab.name}': {ex.Message}");
                }
            }

            try { EnsureCullLodGroup(prefab); }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: cull LODGroup threw on '{prefab.name}': {ex.Message}"); }
        }

        // 1.1.163 (Fire's 5 fps log, 10-05: Poplar_bal 70.0M tris on 321 renderers, Willow_bal 2.9M, Cypress_bal 2.2M ...):
        // those Balrond prefabs SHIP WITHOUT an LODGroup (the bundle dump: 537 of 867 balrondnature prefabs have one; the heavy
        // trees don't, only a LodFadeInOut that has nothing to drive), so every copy drew full detail to the far clip. A bundle
        // prefab with no LODGroup of its own now gets one level holding all its mesh renderers, culled past
        // BundleCullDistance m (sized from its own bounds). Prefabs with their own LODGroup, items and characters are left alone.
        // There are no lower-detail meshes to switch to: this removes the distant copies, not the cost of a near one.
        internal static int LodGroupsAuthored, LodGroupsAdded, LodGroupsSkipped;

        private static void EnsureCullLodGroup(GameObject prefab)
        {
            if (!VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out _)) return;
            float distance = ConfigManager.Instance?.configBundleCullDistance?.Value ?? 0f;
            if (prefab.GetComponentInChildren<LODGroup>(true) != null) { LodGroupsAuthored++; return; }
            if (distance <= 0f || prefab.GetComponent<Character>() != null || prefab.GetComponent<ItemDrop>() != null) { LodGroupsSkipped++; return; }
            var renderers = new List<Renderer>();
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                if (r is MeshRenderer || r is SkinnedMeshRenderer) renderers.Add(r);
            if (renderers.Count == 0) { LodGroupsSkipped++; return; }
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            float size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (size <= 0.01f) size = 1f;
            var group = prefab.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(VerdantsAscent.Utilities.VABakeLodPolicy.ScreenRelativeHeightAt(distance, size), renderers.ToArray()) });
            group.RecalculateBounds();
            LodGroupsAdded++;
            VerdantsAscent.Utilities.VABundleImpostors.Queue(prefab);   // 1.1.164: its far level becomes a baked impostor
            if (LodGroupsAdded <= 12 || prefab.name.StartsWith("Poplar", StringComparison.Ordinal))
                VaPieces.Log?.LogDebug($"[VAMaterialRepair] {prefab.name}: no LODGroup in its bundle; added a cull LODGroup ({renderers.Count} renderer(s), size {size:0.0} m, culled past {distance:0} m)");
        }

        // InstanceRenderer draws through Graphics.DrawMeshInstanced, so its material is not in any Renderer's slots
        // and must have instancing on.
        private static void AdoptInstancedMaterials(GameObject prefab)
        {
            foreach (var instanced in prefab.GetComponentsInChildren<InstanceRenderer>(includeInactive: true))
            {
                if (instanced?.m_material == null) continue;
                if (VAInjectedShaders.TryGameMaterial(instanced.m_material, out var gameMaterial)) instanced.m_material = gameMaterial;
                else VAInjectedShaders.Adopt(instanced.m_material, particle: false);
                instanced.m_material.enableInstancing = true;
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Replacement-shader resolution
        // ──────────────────────────────────────────────────────────────

        // Tries in order:
        // Resolves a replacement shader by trying, in order: the manifest
        // descriptor's recorded shader, VAShaderLibrary, Shader.Find on the
        // material's current name, a prefab-type heuristic, then Standard.
        // Never returns null (Standard is the final fallback).
        private static Shader ResolveReplacementShader(
            string originalShaderName,
            GameObject prefab,
            VAExternalBundleLoader.MaterialDescriptorDto matDesc,
            Renderer renderer)
        {
            if (matDesc != null && !string.IsNullOrEmpty(matDesc.shader))
            {
                if (VAInjectedShaders.TryGameShader(VAInjectedShaders.IntendedShaderName(matDesc.shader), out var gameShader))
                    return gameShader;
                if (VAShaderLibrary.TryGetShader(matDesc.shader, out var manifestShader)
                    && manifestShader != null)
                {
                    return manifestShader;
                }
                var foundByDescriptorName = Shader.Find(matDesc.shader);
                if (foundByDescriptorName != null) return foundByDescriptorName;
            }

            if (!string.IsNullOrEmpty(originalShaderName)
                && VAShaderLibrary.TryGetShader(originalShaderName, out var cached)
                && cached != null)
            {
                return cached;
            }

            if (!string.IsNullOrEmpty(originalShaderName))
            {
                var found = Shader.Find(originalShaderName);
                if (found != null) return found;
            }

            var heuristic = ResolveFallbackShader(prefab, renderer);
            if (heuristic != null) return heuristic;

            return _vanillaStandard;
        }

        // Best-guess vanilla shader for a prefab from its name + component
        // signature. Used both as the heuristic step of ResolveReplacementShader
        // and as the stand-in shader when a material slot is entirely null.
        // Only a particle renderer gets the particle shader: a torch's mesh is
        // not a particle because the prefab also carries flames.
        private static Shader ResolveFallbackShader(GameObject prefab, Renderer renderer)
        {
            if (prefab == null) return _vanillaStandard;

            if (renderer is ParticleSystemRenderer && _vanillaParticleUnlit != null)
            {
                return _vanillaParticleUnlit;
            }

            if (prefab.GetComponentInChildren<InstanceRenderer>(includeInactive: true) != null
                && _vanillaCustomVegetation != null)
            {
                return _vanillaCustomVegetation;
            }

            string prefabName = prefab.name ?? string.Empty;
            if (prefabName.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("plant", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("flower", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("fern", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("seagrass", StringComparison.OrdinalIgnoreCase) >= 0
                || prefabName.IndexOf("seaweed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (_vanillaCustomVegetation != null) return _vanillaCustomVegetation;
            }

            if ((prefab.GetComponent<WearNTear>() != null || prefab.GetComponent<Piece>() != null)
                && _vanillaCustomPiece != null)
            {
                return _vanillaCustomPiece;
            }

            if ((prefab.GetComponentInChildren<Character>(includeInactive: true) != null
                 || prefab.GetComponentInChildren<Humanoid>(includeInactive: true) != null
                 || prefab.GetComponentInChildren<AnimalAI>(includeInactive: true) != null)
                && _vanillaCustomCreature != null)
            {
                return _vanillaCustomCreature;
            }

            return _vanillaStandard;
        }

        // ──────────────────────────────────────────────────────────────
        // Vanilla material template cache
        // ──────────────────────────────────────────────────────────────

        // Builds the per-category vanilla material cache by stashing the
        // first healthy Renderer material found among each category's curated
        // candidate prefabs. Subsequent calls are no-ops.
        private static void EnsureVanillaMaterialCache()
        {
            if (_vanillaMaterialTemplates != null) return;
            _vanillaMaterialTemplates = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            var scene = ZNetScene.instance;
            if (scene == null) return;

            TryStashMaterial(scene, "rock",
                "rock_4", "rock_3", "Rock_4", "Rock_3",
                "rock_4_plains", "RockDolmen_1", "RockDolmen_2",
                "rock_swamp_4", "rock_mountain_2", "Rock_destructible");

            TryStashMaterial(scene, "wood",
                "wood_pole", "wood_floor", "wood_wall_log",
                "Beech_log", "Beech_Stub", "FirTree_log", "FirTree_Stub",
                "PineTree_log", "wood_log");

            TryStashMaterial(scene, "metal",
                "iron_grate", "MetalBar", "smelter", "blastfurnace",
                "forge", "iron_floor", "wood_metal_floor");

            // Foliage candidates ship Custom/Vegetation with a cutout texture,
            // so copies inherit cutout without a manual keyword enable.
            TryStashMaterial(scene, "foliage",
                "Bush01", "Bush02_en", "BushFernShrub01",
                "shrub_2", "shrub_2_heath", "Pickable_Branch",
                "Pickable_Mushroom", "PickableTinNugget");

            TryStashMaterial(scene, "fabric",
                "banner_white", "banner_red", "banner_yellow",
                "darkwood_roof_creep_45", "wood_roof");

            TryStashMaterial(scene, "default",
                "Hammer", "piece_workbench", "wood_door", "stone_wall_2x1");

            int templateCount = _vanillaMaterialTemplates.Count;
            Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair vanilla material cache built — {templateCount} category template(s): [{string.Join(", ", _vanillaMaterialTemplates.Keys)}]");
        }

        // Walks the candidate prefab names looking for the FIRST one
        // whose first Renderer has a healthy material. Stashes that
        // material as the category's template. Silently bails if no
        // candidate matches (the category just won't have a template;
        // BuildFallbackMaterial falls through to a generic Standard).
        private static void TryStashMaterial(ZNetScene scene, string category, params string[] candidatePrefabs)
        {
            if (_vanillaMaterialTemplates.ContainsKey(category)) return;
            if (candidatePrefabs == null) return;
            foreach (var name in candidatePrefabs)
            {
                if (string.IsNullOrEmpty(name)) continue;
                GameObject candidate;
                try { candidate = scene.GetPrefab(name); } catch { continue; }
                if (candidate == null) continue;
                var renderer = candidate.GetComponentInChildren<Renderer>(includeInactive: true);
                if (renderer == null) continue;
                Material[] sharedMats;
                try { sharedMats = renderer.sharedMaterials; } catch { continue; }
                if (sharedMats == null || sharedMats.Length == 0) continue;
                var material = sharedMats[0];
                if (material == null) continue;
                Shader shader = null;
                try { shader = material.shader; } catch { shader = null; }
                if (shader == null) continue;
                if (IsErrorShader(shader)) continue;
                _vanillaMaterialTemplates[category] = material;
                return;
            }
        }

        // Classifies a prefab into a material category by name keyword.
        // Expanded keyword list compared to the IsNaturePrefab heuristic
        // — picks up rock/stone/mine/dolmen/cliff/boulder/etc. for the
        // mine-rock case + foliage variants + wood/metal/fabric.
        //
        // Used in two paths:
        //   1. BuildFallbackMaterial → which template to clone
        //   2. ApplyAlphaCutoutIfNeeded → whether to enable _ALPHATEST_ON
        public static string ClassifyPrefabForMaterial(GameObject prefab)
        {
            if (prefab == null) return "default";
            string prefabName = (prefab.name ?? string.Empty).ToLowerInvariant();
            if (prefabName.Length == 0) return "default";

            // Foliage first: it often has rock-like names (rockshrub, stonebush)
            // and we want the cutout material, not the rock one. Tent / thatch /
            // fabric share foliage's alpha-cutout path (Krump_B_Tent_Sticks).
            if (Contains(prefabName, "grass", "plant", "flower", "fern", "bush",
                            "leaf", "leaves", "seagrass", "seaweed",
                            "vine", "ivy", "clutter", "shrub",
                            "moss", "lichen", "tuft",
                            "tent", "thatch", "canvas", "cloth",
                            "fabric", "tarp", "awning", "drape",
                            "curtain", "banner_cloth", "sail"))
                return "foliage";

            if (Contains(prefabName, "rock", "stone", "mine", "boulder",
                            "dolmen", "crystal", "ore", "granite",
                            "marble", "cliff", "cave", "rubble",
                            "pebble", "gem", "geode", "obsidian",
                            "minerock"))
                return "rock";

            if (Contains(prefabName, "wood", "log", "plank", "stump", "trunk",
                            "branch", "beech", "oak", "pine", "birch",
                            "fir", "yggdrasil"))
                return "wood";

            if (Contains(prefabName, "metal", "iron", "bronze", "copper",
                            "silver", "gold", "steel", "tin",
                            "blackmetal", "anvil"))
                return "metal";

            if (Contains(prefabName, "banner", "flag", "cloth", "fabric",
                            "tapestry", "linen", "wool"))
                return "fabric";

            return "default";
        }

        // Multi-needle substring check helper.
        private static bool Contains(string haystack, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (haystack.IndexOf(needles[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // Shared template copies (see BuildFallbackMaterial, Priority 2), by template + descriptor values.
        private static readonly Dictionary<string, Material> _repairCopies = new Dictionary<string, Material>(StringComparer.Ordinal);

        private static string DescriptorKey(VAExternalBundleLoader.MaterialDescriptorDto d)
        {
            if (d == null) return "-";
            var keywords = d.keywords != null ? new List<string>(d.keywords) : new List<string>();
            keywords.Sort(StringComparer.Ordinal);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join("|", d.color ?? "", d.cutoff.ToString("R", inv), d.glossiness.ToString("R", inv), string.Join(",", keywords));
        }

        // Builds a substitute Material for a prefab + slot, preferring a copy
        // of the category-matched vanilla template (textures/cutout/shader come
        // for free) and falling back to a bare prefab-type-heuristic shader.
        // Returns a non-null Material except when no fallback shader resolves.
        private static Material BuildFallbackMaterial(
            GameObject prefab,
            int slotIndex,
            VAExternalBundleLoader.MaterialDescriptorDto matDesc,
            Renderer renderer)
        {
            EnsureVanillaMaterialCache();
            bool particle = renderer is ParticleSystemRenderer;

            // PRIORITY 1: reconstruct from the manifest descriptor (v4+).
            if (matDesc != null && !string.IsNullOrEmpty(matDesc.shader))
            {
                Shader shader = null;
                if (!VAInjectedShaders.TryGameShader(VAInjectedShaders.IntendedShaderName(matDesc.shader), out shader)
                    && (!VAShaderLibrary.TryGetShader(matDesc.shader, out shader) || shader == null))
                {
                    shader = Shader.Find(matDesc.shader);
                }
                if (shader != null)
                {
                    var reconstructed = new Material(shader)
                    {
                        name = $"FAP_RepairFromManifest_{prefab.name}_{slotIndex}"
                    };
                    if (!string.IsNullOrEmpty(matDesc.mainTexture)
                        && reconstructed.HasProperty("_MainTex")
                        && VATextureLibrary.TryGetTexture(matDesc.mainTexture, out var texture))
                    {
                        try { reconstructed.mainTexture = texture; } catch { }
                    }
                    ApplyDescriptorProperties(reconstructed, matDesc);
                    VAInjectedShaders.Adopt(reconstructed, particle, matDesc.shader);

                    // A reconstructed material that wants _MainTex and has nothing in it is
                    // WHITE, and this used to return it anyway and count a repair. The manifest
                    // records "mainTexture": null for 5,419 materials across the shipped bundles
                    // — 46% of vapieces alone — so it is the common case, not an edge one.
                    //
                    // Keep the descriptor's shader and properties (better fidelity than a whole
                    // template copy) but borrow a texture from the vanilla category template. If
                    // even that has none, fall through to Priority 2 rather than return white.
                    if (NeedsMainTexture(reconstructed))
                    {
                        var real = ResolveTextureByMaterialName(matDesc.name);
                        if (real != null)
                        {
                            try { reconstructed.mainTexture = real; } catch { }
                        }
                    }

                    if (!NeedsMainTexture(reconstructed)) return reconstructed;
                }
            }

            // PRIORITY 2: vanilla material template copy by category.
            string category = ClassifyPrefabForMaterial(prefab);

            Material template = null;
            if (_vanillaMaterialTemplates != null
                && _vanillaMaterialTemplates.TryGetValue(category, out template)
                && template != null)
            {
                try
                {
                    // One shared copy per (template, descriptor values): the copy is never changed after this, and a copy
                    // per prefab and slot left thousands of identical FAP_RepairCopy_* materials alive for the session
                    // ([release]'s MEMORY_LEADS #5, 2026-09-29).
                    string key = template.GetInstanceID() + "|" + DescriptorKey(matDesc);
                    if (_repairCopies.TryGetValue(key, out var shared) && shared != null) return shared;
                    var copy = new Material(template);
                    copy.name = $"FAP_RepairCopy_({category})_{_repairCopies.Count}";
                    if (matDesc != null) ApplyDescriptorProperties(copy, matDesc);
                    _repairCopies[key] = copy;
                    return copy;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — Material copy from template '{template?.name}' failed for '{prefab.name}': {ex.Message}. Falling through to bare-shader stub.");
                }
            }

            // PRIORITY 3: bare Material with prefab-type heuristic shader.
            var fallbackShader = ResolveFallbackShader(prefab, renderer);
            if (fallbackShader == null) return null;
            var stub = new Material(fallbackShader) { name = $"FAP_RepairStub_{prefab.name}_{slotIndex}_({category})" };
            if (matDesc != null) ApplyDescriptorProperties(stub, matDesc);
            ApplyAlphaCutoutIfNeeded(stub, prefab);
            return stub;
        }

        // ──────────────────────────────────────────────────────────────
        // Descriptor property application
        // ──────────────────────────────────────────────────────────────

        // Applies the recorded color / cutoff / glossiness / keywords
        // from a v4 MaterialDescriptor onto a material. Safe to call
        // with a partially-populated descriptor — each field is checked
        // and only applied if the material supports it.
        private static void ApplyDescriptorProperties(
            Material mat,
            VAExternalBundleLoader.MaterialDescriptorDto matDesc)
        {
            if (mat == null || matDesc == null) return;

            // Color is a defensively-parsed "r,g,b,a" string.
            if (!string.IsNullOrEmpty(matDesc.color) && mat.HasProperty("_Color"))
            {
                try
                {
                    var parts = matDesc.color.Split(',');
                    if (parts.Length == 4
                        && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r)
                        && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g)
                        && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)
                        && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a))
                    {
                        mat.SetColor("_Color", new Color(r, g, b, a));
                    }
                }
                catch { /* malformed — skip */ }
            }

            if (mat.HasProperty("_Cutoff") && matDesc.cutoff > 0f)
            {
                try { mat.SetFloat("_Cutoff", matDesc.cutoff); } catch { }
            }

            if (matDesc.glossiness > 0f)
            {
                try
                {
                    if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", matDesc.glossiness);
                    else if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", matDesc.glossiness);
                }
                catch { }
            }

            // Recorded keywords (e.g. _ALPHATEST_ON, _NORMALMAP, _METALLICGLOSSMAP).
            if (matDesc.keywords != null)
            {
                foreach (var keyword in matDesc.keywords)
                {
                    if (string.IsNullOrEmpty(keyword)) continue;
                    try { mat.EnableKeyword(keyword); } catch { }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Transform path resolution
        // ──────────────────────────────────────────────────────────────

        // Builds the slash-delimited path from prefabRoot to the
        // renderer's transform. Mirrors the SteamyDumps-side
        // BundleManifestGenerator.GetTransformPath so the two ends agree
        // on the format. Empty string when renderer is on the root.
        // Builds into a reused buffer instead of a fresh List + Reverse + Join. This runs once per renderer
        // that actually needs its descriptor, and the shipped bundles hold 43,440 renderer descriptors, so the
        // per-call garbage was worth removing even after the call itself became rare. Depth is a handful of
        // levels, so Insert(0, ...) costs less than the list it replaces.
        private static readonly System.Text.StringBuilder _pathBuilder = new System.Text.StringBuilder(128);

        private static string GetTransformPath(Transform transform, Transform root)
        {
            if (transform == null || root == null) return string.Empty;
            if (transform == root) return string.Empty;

            _pathBuilder.Length = 0;
            var current = transform;
            while (current != null && current != root)
            {
                if (_pathBuilder.Length > 0) _pathBuilder.Insert(0, '/');
                _pathBuilder.Insert(0, current.name);
                current = current.parent;
            }
            return _pathBuilder.ToString();
        }

        // The descriptor is only ever needed for a material that is NULL or BROKEN, which is a small minority.
        // Resolving it up front meant building a transform-path string for every one of the renderers on every
        // injected prefab, then throwing it away - the path was computed even for materials that
        // `_examined` skips on the very next line. Resolved on demand instead, with no closure so a renderer
        // that needs nothing allocates nothing.
        private static VAExternalBundleLoader.RendererDescriptorDto ResolveRendererDesc(
            Dictionary<string, VAExternalBundleLoader.RendererDescriptorDto> descByPath,
            Renderer renderer,
            Transform root,
            ref VAExternalBundleLoader.RendererDescriptorDto cached,
            ref bool resolved)
        {
            if (resolved) return cached;
            resolved = true;
            if (descByPath == null || descByPath.Count == 0 || renderer == null) return null;
            descByPath.TryGetValue(GetTransformPath(renderer.transform, root), out cached);
            return cached;
        }

        // The manifest entry for one material slot, resolved only when that slot turns out to need repairing.
        private static VAExternalBundleLoader.MaterialDescriptorDto MaterialDescAt(
            Dictionary<string, VAExternalBundleLoader.RendererDescriptorDto> descByPath,
            Renderer renderer,
            Transform root,
            int slot,
            ref VAExternalBundleLoader.RendererDescriptorDto cached,
            ref bool resolved)
        {
            var desc = ResolveRendererDesc(descByPath, renderer, root, ref cached, ref resolved);
            if (desc?.materials == null || slot >= desc.materials.Count) return null;
            return desc.materials[slot];
        }

        // True only when the shader has a `_MainTex` slot and that texture is
        // null (renders solid white). The HasProperty gate is critical: reading
        // mat.mainTexture on a shader without `_MainTex` spams a per-access
        // Unity error, so shaders lacking it return false ("nothing missing").
        private static bool IsMainTextureMissing(Material mat)
        {
            if (mat == null) return false;
            if (mat.shader == null) return false;
            if (!mat.HasProperty("_MainTex")) return false;
            try
            {
                if (mat.mainTexture == null) return true;
            }
            catch { return true; }
            return false;
        }

        // Puts a Standard-style foliage material into transparent-cutout mode
        // (keyword + _Cutoff + _Mode + RenderType tag + AlphaTest queue); without
        // it, leaf/grass alpha renders as solid-white quads. Safe on any material
        // — HasProperty gates and the no-op keyword make non-Standard shaders
        // (e.g. Custom/Vegetation, which already cuts out) unaffected.
        private static void ApplyAlphaCutoutIfNeeded(Material mat, GameObject prefab)
        {
            if (mat == null || mat.shader == null) return;
            if (prefab == null) return;
            if (VAInjectedShaders.IsAdopted(mat) || VAInjectedShaders.IsParticleShader(mat.shader)) return;

            string category = ClassifyPrefabForMaterial(prefab);
            if (category != "foliage") return;

            try
            {
                mat.EnableKeyword("_ALPHATEST_ON");
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", StandardCutoutThreshold);
                if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", StandardModeCutout);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAMaterialRepair — ApplyAlphaCutoutIfNeeded threw on '{mat.name}' / '{prefab.name}': {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Vanilla shader cache (existing)
        // ──────────────────────────────────────────────────────────────

        // Lazy-build the vanilla shader handles (bool guard makes repeat calls
        // free). Shader.Find misses vanilla shaders that are loaded but not yet
        // referenced by any material at ZNetScene.Awake timing, so a
        // Resources.FindObjectsOfTypeAll backstop rescues anything it missed.
        private static void EnsureVanillaShaderCache()
        {
            if (_vanillaCacheBuilt) return;
            _vanillaCacheBuilt = true;

            try { _vanillaCustomVegetation = Shader.Find("Custom/Vegetation"); } catch { }
            try { _vanillaCustomPiece      = Shader.Find("Custom/Piece"); } catch { }
            try { _vanillaCustomCreature   = Shader.Find("Custom/Creature"); } catch { }
            try { _vanillaStandard         = Shader.Find("Standard"); } catch { }
            try { _vanillaParticleUnlit    = Shader.Find("Particles/Standard Unlit"); } catch { }
            if (_vanillaParticleUnlit == null)
            {
                try { _vanillaParticleUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit"); } catch { }
            }

            bool anyMissing =
                _vanillaCustomVegetation == null
                || _vanillaCustomPiece == null
                || _vanillaCustomCreature == null
                || _vanillaStandard == null
                || _vanillaParticleUnlit == null;

            int rescued = 0;
            if (anyMissing)
            {
                Shader[] allShaders;
                try { allShaders = Resources.FindObjectsOfTypeAll<Shader>(); }
                catch { allShaders = null; }

                if (allShaders != null)
                {
                    foreach (var shader in allShaders)
                    {
                        if (shader == null) continue;
                        string shaderName;
                        try { shaderName = shader.name ?? string.Empty; } catch { continue; }
                        if (shaderName.Length == 0) continue;

                        switch (shaderName)
                        {
                            case "Custom/Vegetation":
                                if (_vanillaCustomVegetation == null) { _vanillaCustomVegetation = shader; rescued++; }
                                break;
                            case "Custom/Piece":
                                if (_vanillaCustomPiece == null) { _vanillaCustomPiece = shader; rescued++; }
                                break;
                            case "Custom/Creature":
                                if (_vanillaCustomCreature == null) { _vanillaCustomCreature = shader; rescued++; }
                                break;
                            case "Standard":
                                if (_vanillaStandard == null) { _vanillaStandard = shader; rescued++; }
                                break;
                            case "Particles/Standard Unlit":
                            case "Universal Render Pipeline/Particles/Unlit":
                                if (_vanillaParticleUnlit == null) { _vanillaParticleUnlit = shader; rescued++; }
                                break;
                        }
                    }
                }
            }

            Debug.Log($"{VaPieces.PluginName}: VAMaterialRepair shader cache — Custom/Vegetation: {(_vanillaCustomVegetation != null ? "OK" : "MISSING")}, Custom/Piece: {(_vanillaCustomPiece != null ? "OK" : "MISSING")}, Custom/Creature: {(_vanillaCustomCreature != null ? "OK" : "MISSING")}, Standard: {(_vanillaStandard != null ? "OK" : "MISSING")}, Particles/Standard Unlit: {(_vanillaParticleUnlit != null ? "OK" : "MISSING")}. (Resources backstop rescued {rescued} shader handle(s).)");
        }

        // Unity's "pink" missing-shader fallback. Detects all three
        // common variants — names differ across Unity versions.
        private static bool IsErrorShader(Shader sh)
        {
            if (sh == null) return true;
            string shaderName = SafeShaderName(sh);
            if (string.IsNullOrEmpty(shaderName)) return true;
            return shaderName == "Hidden/InternalErrorShader"
                || shaderName == "Hidden/InternalErrorShader (Replacement)"
                || shaderName.IndexOf("InternalErrorShader", StringComparison.Ordinal) >= 0
                || shaderName == "Hidden/CompileError";
        }

        private static string SafeShaderName(Shader sh)
        {
            try { return sh.name ?? string.Empty; }
            catch { return string.Empty; }
        }

        // One-line summary for the end-of-injection log.
        public static string FormatSummary()
        {
            string baseLine = $"prefabs={TotalPrefabsScanned}, materials={TotalMaterialsScanned}, repaired={TotalMaterialsRepaired} (null-slot={TotalMaterialsNullSlot}, null-shader={TotalMaterialsNullShader}, error-shader={TotalMaterialsErrorShader}), unrepairable={TotalMaterialsUnrepairable}";
            baseLine += $", LODGroups: {LodGroupsAuthored} bundle prefab(s) with their own, {LodGroupsAdded} given a cull LODGroup, {LodGroupsSkipped} skipped";
            if (TotalMaterialsTextureByName > 0)
                baseLine += $", texture recovered by material name={TotalMaterialsTextureByName}";
            if (TotalMaterialsEmptyArray > 0)
                baseLine += $", EMPTY renderer slots rebuilt={TotalMaterialsEmptyArray} on {_emptyRendererByPrefab.Count} prefab(s) (these were rendering white): {FormatOffenders(_emptyRendererByPrefab, 8)}";
            if (TotalMaterialsNoTexture == 0) return baseLine;
            return baseLine + $", NO TEXTURE after repair={TotalMaterialsNoTexture} (these render white): {FormatOffenders(_noTextureByPrefab, 8)}";
        }
    }
}
