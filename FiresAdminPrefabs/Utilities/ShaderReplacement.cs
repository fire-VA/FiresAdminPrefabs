using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Caches Valheim's own shaders and applies the configured nature shader to bundle foliage and
    // rocks. Bundle prefabs carry shader binaries built for another Unity version, and vanilla's
    // Custom/* shaders are only reachable through materials the game has already loaded, so the
    // cache is filled by walking a set of known vanilla prefabs.
    //
    // Foliage gets Custom/Vegetation (sway and leaf cutout) or a Standard cutout, per the
    // BundleNatureShader setting; rocks always get Custom/Piece. Every touched prefab is
    // remembered so a change to the setting can re-apply.
    public static class ShaderReplacement
    {
        private const float CutoffEpsilon = 0.01f;
        private const float DefaultAlphaCutoff = 0.5f;
        private const float StandardCutoutMode = 1f;
        private const string StandardShaderName = "Standard";
        private const string VegetationShaderName = "Custom/Vegetation";
        private const string PieceShaderName = "Custom/Piece";
        private const string InternalErrorShaderMarker = "InternalErrorShader";

        // Standard shows _EmissionColor only while _EMISSION is on, so bundles leave white there with emission off;
        // Valheim's shaders add it regardless. Measured 2026-09-27 (RenderDoc frame 13501): the Trunks2 material of
        // Balrond's swamp trees kept Standard's white _EmissionColor on Custom/Vegetation and drew solid white.
        private const string EmissionKeyword = "_EMISSION";
        private const string EmissionColorProperty = "_EmissionColor";

        // True when the material's own shader gates emission on _EMISSION and it is off, so its _EmissionColor is
        // invisible now and must not light up once the material moves to a Valheim shader.
        internal static bool HidesEmission(Material material) =>
            material.shader != null
            && material.shader.keywordSpace.FindKeyword(EmissionKeyword).isValid
            && !material.IsKeywordEnabled(EmissionKeyword);

        // Characters, equipment, effects, then building and nature pieces for Custom/Piece and
        // Custom/Vegetation. The first shader found under a name is the one cached.
        private static readonly string[] ShaderSourcePrefabs =
        {
            "Player",
            "Troll", "Skeleton", "Draugr", "Greydwarf", "Haldor", "Hildir", "Greyling", "Neck",
            "ArmorBronzeChest", "ArmorIronChest", "ArmorLeatherChest", "ArmorTrollLeatherChest",
            "ArmorBronzeLegs", "ArmorIronLegs", "ArmorLeatherLegs",
            "CapeDeerHide", "CapeTrollHide", "CapeWolf",
            "HelmetBronze", "HelmetIron", "HelmetLeather",
            "SwordBronze", "SwordIron", "AxeBronze", "ShieldWood", "ShieldBronze",
            "Torch",
            "vfx_HitSparks", "sfx_arrow_hit", "fx_guardstone_permitted_add",
            "vfx_torch_fire", "vfx_Place_stone_wall_2x1", "fx_float_hitwater",
            "vfx_FireAdditive", "vfx_bonfire_AddFuel", "vfx_sledge_hit",
            "wood_wall", "wood_floor", "wood_beam", "stone_wall_2x1",
            "piece_workbench", "fire_pit", "Pickable_Branch",
            "FirTree", "Bush01", "Pickable_Mushroom",
            "rock4_forest", "rug_deer",
        };

        // Case-sensitive, so "ore" inside "Forest" doesn't match "Ore".
        private static readonly string[] RockyNameNeedles =
            { "Rock", "Stone", "Boulder", "Cliff", "Pebble", "Crystal", "Ore", "Mineral" };

        private enum BundleNatureKind
        {
            Foliage,
            Rocky,
        }

        private static readonly Dictionary<string, Shader> _valheimShaders = new Dictionary<string, Shader>();
        private static readonly HashSet<GameObject> _bundleNatureTouched = new HashSet<GameObject>();
        private static bool _shadersInitialized;

        // Needs ZNetScene's prefabs, so it returns without marking itself done when called earlier.
        public static void InitializeValheimShaders()
        {
            if (_shadersInitialized && _valheimShaders.Count > 0) return;
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null || scene.m_prefabs.Count == 0) return;

            foreach (var prefabName in ShaderSourcePrefabs)
            {
                var prefab = scene.GetPrefab(prefabName);
                if (prefab == null && ObjectDB.instance != null) prefab = ObjectDB.instance.GetItemPrefab(prefabName);
                if (prefab != null) CacheShaders(prefab);
            }

            _shadersInitialized = true;
            Debug.Log($"{VaPieces.PluginName}: Cached {_valheimShaders.Count} Valheim shaders.");
        }

        private static void CacheShaders(GameObject prefab)
        {
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null) continue;
                    if (!_valheimShaders.ContainsKey(material.shader.name))
                        _valheimShaders[material.shader.name] = material.shader;
                }
            }
        }

        public static IEnumerable<KeyValuePair<string, Shader>> EnumerateCachedShaders() => _valheimShaders;

        public static void ApplyBundleNatureShader(GameObject prefab)
        {
            if (prefab == null) return;
            _bundleNatureTouched.Add(prefab);
            ApplyBundleNatureShaderInternal(prefab, ConfigManager.Instance.configBundleNatureShader.Value);
        }

        public static void ReapplyBundleNatureShader()
        {
            if (_bundleNatureTouched.Count == 0) return;
            var mode = ConfigManager.Instance.configBundleNatureShader.Value;
            int touched = 0;
            foreach (var prefab in _bundleNatureTouched)
            {
                if (prefab == null) continue;
                ApplyBundleNatureShaderInternal(prefab, mode);
                touched++;
            }
            Debug.Log($"{VaPieces.PluginName}: ReapplyBundleNatureShader ({mode}) → re-applied on {touched} bundle prefab(s).");
        }

        // Foliage is the default because misclassified sway costs less than broken tree cutout.
        private static BundleNatureKind ClassifyBundleNaturePrefab(GameObject prefab)
        {
            if (prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null)
                return BundleNatureKind.Rocky;
            foreach (var needle in RockyNameNeedles)
                if (prefab.name.IndexOf(needle, StringComparison.Ordinal) >= 0) return BundleNatureKind.Rocky;
            return BundleNatureKind.Foliage;
        }

        // The main texture, colour and normal map are re-set after the swap, so the material looks
        // the same even if the new shader exposes a different property set.
        private static void ApplyBundleNatureShaderInternal(GameObject prefab, BundleNatureShaderMode mode)
        {
            if (!_shadersInitialized || _valheimShaders.Count == 0) InitializeValheimShaders();

            var kind = ClassifyBundleNaturePrefab(prefab);
            var targetShader = ResolveShaderForKind(kind, mode);
            if (targetShader == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: ApplyBundleNatureShader could not resolve target shader for kind {kind} (mode {mode}) on '{prefab.name}'.");
                return;
            }
            bool isStandardShader = targetShader.name == StandardShaderName;

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer is ParticleSystemRenderer) continue;

                var materials = renderer.sharedMaterials;
                bool shaderChanged = false;
                foreach (var material in materials)
                {
                    if (material == null) continue;
                    if (VAInjectedShaders.Adopt(material, particle: false)) continue;

                    Texture mainTexture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                    Color color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                    Texture normalMap = material.HasProperty("_BumpMap") ? material.GetTexture("_BumpMap") : null;
                    bool emissionHidden = HidesEmission(material);
                    // 1.1.162 (Fire 10-05: Balrond's ElderMushroom_bal / Pickable_Mushroom_blueNew_bal still white): what the
                    // source material's emission really was, before the swap. Standard masks _EmissionColor by _EmissionMap;
                    // Custom/Vegetation has no map and adds the colour over the whole surface.
                    string fromShader = material.shader != null ? material.shader.name : "null";
                    bool keywordKnown = material.shader != null && material.shader.keywordSpace.FindKeyword(EmissionKeyword).isValid;
                    bool emissionOn = material.IsKeywordEnabled(EmissionKeyword);
                    Texture emissionMap = material.HasProperty("_EmissionMap") ? material.GetTexture("_EmissionMap") : null;
                    Color emissionColor = material.HasProperty(EmissionColorProperty) ? material.GetColor(EmissionColorProperty) : Color.black;

                    if (material.shader != targetShader)
                    {
                        material.shader = targetShader;
                        shaderChanged = true;
                    }

                    if (mainTexture != null && material.HasProperty("_MainTex")) material.SetTexture("_MainTex", mainTexture);
                    if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                    if (normalMap != null && material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", normalMap);
                    string emissionVerdict = "kept";
                    if (!isStandardShader && material.HasProperty(EmissionColorProperty))
                    {
                        bool targetMasks = material.HasProperty("_EmissionMap");
                        if (emissionHidden) { material.SetColor(EmissionColorProperty, Color.black); emissionVerdict = "off (was hidden)"; }
                        // A masked glow can't be drawn without its mask: a solid _EmissionColor would paint the whole mesh. The
                        // prefab's own lights still glow; the surface shows its albedo again.
                        else if (emissionMap != null && !targetMasks) { material.SetColor(EmissionColorProperty, Color.black); emissionVerdict = "off (masked by _EmissionMap, the target has no mask)"; }
                        else if (emissionMap != null) material.SetTexture("_EmissionMap", emissionMap);
                        // The source shader is a stand-in whose keywords can't be read: a full-white colour means "unset".
                        else if (!keywordKnown && emissionColor.maxColorComponent >= 0.99f) { material.SetColor(EmissionColorProperty, Color.black); emissionVerdict = "off (white, keyword unreadable)"; }
                    }
                    // 1.1.188 ([worldgen] 10-07: 4,746 of these during load, 4,605 "kept"): only the materials whose emission was
                    // changed get a line; a kept one tells nothing and cost main-thread time before the player spawned.
                    if (emissionVerdict != "kept")
                    VaPieces.Log?.LogDebug($"[VAMaterialRepair] {prefab.name}/{renderer.name} material {material.name}: shader {fromShader} -> {targetShader.name}, "
                        + $"_MainTex {(mainTexture != null ? mainTexture.name : "null")}, emission {(keywordKnown ? (emissionOn ? "on" : "off") : "?")} "
                        + $"colour {emissionColor} map {(emissionMap != null ? emissionMap.name : "null")} -> {emissionVerdict}");

                    ApplyAlphaCutoutSettings(material, isStandardShader);
                }
                if (shaderChanged) renderer.sharedMaterials = materials;
            }
        }

        // Rocks always use Custom/Piece; foliage follows the setting. Either falls back to Standard
        // when the custom shader can't be resolved.
        private static Shader ResolveShaderForKind(BundleNatureKind kind, BundleNatureShaderMode mode)
        {
            Shader customShader = null;
            if (kind == BundleNatureKind.Rocky) customShader = ResolveShaderByName(PieceShaderName);
            else if (mode == BundleNatureShaderMode.Vegetation) customShader = ResolveShaderByName(VegetationShaderName);
            return customShader != null ? customShader : Shader.Find(StandardShaderName);
        }

        // Valheim's Custom/Vegetation, for the baked impostors (VABundleImpostors); null when it can't be resolved.
        internal static Shader VegetationShader => ResolveShaderByName(VegetationShaderName);

        private static Shader ResolveShaderByName(string shaderName)
        {
            if (_valheimShaders.TryGetValue(shaderName, out var cached) && cached != null) return cached;
            var found = Shader.Find(shaderName);
            if (found == null || found.name.Contains(InternalErrorShaderMarker)) return null;
            _valheimShaders[shaderName] = found;
            return found;
        }

        // Alpha-cutout pipeline: the TransparentCutout queue and _ALPHATEST_ON, plus _Mode and cleared
        // blend keywords for Standard. A bundle's own non-zero _Cutoff is kept; otherwise it takes
        // the value Valheim's vegetation uses.
        private static void ApplyAlphaCutoutSettings(Material material, bool isStandardShader)
        {
            float cutoff = DefaultAlphaCutoff;
            if (material.HasProperty("_Cutoff"))
            {
                float existing = material.GetFloat("_Cutoff");
                if (existing > CutoffEpsilon) cutoff = existing;
            }

            material.EnableKeyword("_ALPHATEST_ON");
            if (isStandardShader)
            {
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", StandardCutoutMode);
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }

            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", cutoff);
        }
    }
}
