using System;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Fixes broken InstanceRenderer materials on bundle clutter prefabs by
    // re-pointing them at a vanilla grass shader (the bundle's own shader
    // binaries were compiled against a different Unity version and render
    // pink or fail GPU instancing). Bundle-only, per-prefab idempotent via
    // _repairedPrefabIds, and bails without marking when no donor is found
    // yet so a later call can succeed.
    public static class VAClutterShaderRepair
    {
        private const float CutoffEpsilon = 0.01f;
        private const float DefaultAlphaCutoff = 0.5f;
        private const string EmissionColorProperty = "_EmissionColor";

        private static readonly HashSet<int> _repairedPrefabIds = new HashSet<int>();

        // Canonical vanilla grass donors (with player push-away displacement),
        // walked in order — the first one ClutterSystem has becomes the donor.
        // Rock-themed instanced prefabs are excluded so the fallback never
        // lands on a non-grass donor.
        private static readonly string[] PreferredGrassDonorNames = new[]
        {
            "instanced_meadows_grass",
            "instanced_meadows_grass_short",
            "instanced_forest_groundcover",
            "instanced_swamp_grass",
            "instanced_heathgrass",
            "instanced_mistlands_grass_short",
        };

        // Names Unity substitutes when a bundle's material reference fails to
        // load — they carry the intended shader but no textures/colors/cutoff.
        // Detecting by name is cheap and unambiguous; no legitimate bundle
        // material ships under these names.
        private static readonly HashSet<string> _placeholderMaterialNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "PreviewMaterial",
                "Default-Material",
            };

        // Push-away / sway property names on Custom/Grass and Custom/Vegetation.
        // Zeroed on wholesale-donor clones so the vanilla shader doesn't displace
        // bundle meshes using their un-authored vertex colors (which blew bundle
        // clutter off-screen in the donor-clone experiment).
        private static readonly string[] _swayPropertyNames = new[]
        {
            "_PushForce",
            "_PushDistance",
            "_PushSize",
            "_SwaySpeed",
            "_NoiseStrength",
            "_WindForce",
        };

        private static bool IsPlaceholderMaterial(Material m)
        {
            if (m == null) return false;
            string materialName = m.name;
            if (string.IsNullOrEmpty(materialName)) return false;
            return _placeholderMaterialNames.Contains(materialName);
        }

        public static void RepairIfBundleClutter(GameObject prefab)
        {
            if (prefab == null) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            int prefabId = prefab.GetInstanceID();
            if (_repairedPrefabIds.Contains(prefabId)) return;

            // Bundle-only scope by REFERENCE, not by name — vanilla and bundle
            // copies can share names, and only identity reliably distinguishes
            // our bundle-owned prefabs from vanilla.
            if (!VAExternalBundleLoader.IsBundleOwnedPrefab(prefab)) return;

            var donor = FindVanillaGrassMaterial(excludePrefab: prefab);
            if (donor == null || donor.shader == null)
            {
                // ClutterSystem not yet populated with vanilla grass — bail
                // without marking so a later call can succeed.
                return;
            }

            int fixedCount = 0;
            int authoredCount = 0;

            // Path 1: InstanceRenderer (GPU-instanced grass). In priority order: a null or
            // placeholder material → wholesale donor clone; an author's stand-in (Balrond,
            // Jotunn) → the game shader its author meant; broken/foreign shader → preserve
            // bundle material, swap shader binary only; instancing flag off → flip it.
            var instanceRenderers = prefab.GetComponentsInChildren<InstanceRenderer>(includeInactive: true);
            for (int i = 0; i < instanceRenderers.Length; i++)
            {
                var instanceRenderer = instanceRenderers[i];
                if (instanceRenderer == null) continue;

                if (instanceRenderer.m_material == null || IsPlaceholderMaterial(instanceRenderer.m_material))
                {
                    instanceRenderer.m_material = WholesaleDonorClone(donor, prefab.name);
                    fixedCount++;
                }
                else if (VAInjectedShaders.TryGameMaterial(instanceRenderer.m_material, out var gameMaterial))
                {
                    instanceRenderer.m_material = gameMaterial;
                    instanceRenderer.m_material.enableInstancing = true;
                    authoredCount++;
                }
                else if (VAInjectedShaders.Adopt(instanceRenderer.m_material, particle: false))
                {
                    instanceRenderer.m_material.enableInstancing = true;
                    authoredCount++;
                }
                else if (NeedsRepair(instanceRenderer.m_material, donor.shader))
                {
                    instanceRenderer.m_material = CloneWithVanillaShader(instanceRenderer.m_material, donor.shader, prefab.name, donor);
                    fixedCount++;
                }
                else if (!instanceRenderer.m_material.enableInstancing)
                {
                    instanceRenderer.m_material.enableInstancing = true;
                    fixedCount++;
                }

                // Mesh fallback: borrow a vanilla grass mesh only when missing,
                // since a null-mesh InstanceRenderer renders nothing.
                if (instanceRenderer.m_mesh == null)
                {
                    var donorMesh = FindVanillaGrassMesh(excludePrefab: prefab);
                    if (donorMesh != null) instanceRenderer.m_mesh = donorMesh;
                }
            }

            // Path 2: regular Renderers (billboard cards, and props other bundles
            // list as clutter: couches, books). They are drawn normally, so only a
            // shader that cannot draw at all is swapped for the grass donor's.
            var renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                if (renderer is ParticleSystemRenderer) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;
                bool dirty = false;
                for (int matIndex = 0; matIndex < materials.Length; matIndex++)
                {
                    var material = materials[matIndex];
                    if (material == null) continue;
                    if (VAInjectedShaders.TryGameMaterial(material, out var gameMaterial))
                    {
                        materials[matIndex] = gameMaterial;
                        dirty = true;
                        authoredCount++;
                        continue;
                    }
                    if (VAInjectedShaders.Adopt(material, particle: false))
                    {
                        authoredCount++;
                        continue;
                    }
                    if (!IsUndrawable(material.shader)) continue;
                    materials[matIndex] = CloneWithVanillaShader(material, donor.shader, prefab.name, donor);
                    dirty = true;
                    fixedCount++;
                }
                if (dirty) renderer.sharedMaterials = materials;
            }

            if (fixedCount > 0 || authoredCount > 0)
            {
                _repairedPrefabIds.Add(prefabId);
                Debug.Log($"{VaPieces.PluginName}: ClutterShaderRepair — '{prefab.name}': {authoredCount} material(s) on their author's game shader, swapped {fixedCount} using donor '{donor.name}' (shader '{donor.shader.name}').");
            }
            else
            {
                // Mark as repaired even when nothing changed so we don't re-log
                // every refresh.
                _repairedPrefabIds.Add(prefabId);
                Debug.Log($"{VaPieces.PluginName}: ClutterShaderRepair — '{prefab.name}': no repair needed (donor '{donor.name}', shader '{donor.shader.name}', {instanceRenderers.Length} InstanceRenderer(s)).");
            }
        }

        // Needs repair when the material's shader is a different OBJECT than the
        // vanilla donor's shader. Name match alone isn't enough — bundles ship
        // their own same-named shader copies that lack the instancing variant
        // Graphics.DrawMeshInstanced dispatches into, so painted clutter goes
        // invisible. ReferenceEquals catches that; an error shader always repairs.
        private static bool NeedsRepair(Material mat, Shader donorShader)
        {
            if (mat == null) return false;
            var shader = mat.shader;
            if (IsUndrawable(shader)) return true;
            if (donorShader == null) return false;   // can't decide → leave alone
            return !ReferenceEquals(shader, donorShader);
        }

        private static bool IsUndrawable(Shader shader)
        {
            if (shader == null) return true;
            var name = shader.name;
            if (string.IsNullOrEmpty(name)) return true;
            if (name.StartsWith("Hidden/InternalError", StringComparison.Ordinal)) return true;
            return !shader.isSupported;
        }

        // Clones the bundle material (keeping its textures + tuned properties)
        // and swaps to the vanilla shader so the runtime gets the correct binary
        // and full instancing variant. Then copies the donor's push-away/sway
        // properties so the clone animates like the vanilla grass it's modeled on
        // (the bundle's vertex colors must be sane or zero for displacement to
        // behave; if a bundle blows up, stop copying sway for it).
        private static Material CloneWithVanillaShader(Material source, Shader targetShader, string namePrefix, Material vanillaDonor)
        {
            bool emissionHidden = ShaderReplacement.HidesEmission(source);
            var clone = new Material(source)
            {
                shader = targetShader,
                name = $"{namePrefix}_RuntimeShader",
            };
            clone.enableInstancing = true;
            if (emissionHidden && clone.HasProperty(EmissionColorProperty)) clone.SetColor(EmissionColorProperty, Color.black);
            if (clone.HasProperty("_Cutoff") && clone.GetFloat("_Cutoff") < CutoffEpsilon)
                clone.SetFloat("_Cutoff", DefaultAlphaCutoff);
            clone.EnableKeyword("_ALPHATEST_ON");
            clone.DisableKeyword("_ALPHABLEND_ON");
            clone.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            clone.SetOverrideTag("RenderType", "TransparentCutout");
            clone.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            if (vanillaDonor != null)
            {
                for (int i = 0; i < _swayPropertyNames.Length; i++)
                {
                    var prop = _swayPropertyNames[i];
                    if (vanillaDonor.HasProperty(prop) && clone.HasProperty(prop))
                        clone.SetFloat(prop, vanillaDonor.GetFloat(prop));
                }
            }

            return clone;
        }

        // Wholesale-clones the donor when the bundle has no usable material to
        // start from. Inherits everything from the vanilla donor except the
        // push-away/sway properties, which are zeroed so the unauthored bundle
        // vertex colors can't drive displacement — the clutter renders as static
        // vanilla grass (visible, no sway), which beats invisible.
        private static Material WholesaleDonorClone(Material vanillaDonor, string namePrefix)
        {
            var clone = new Material(vanillaDonor)
            {
                name = $"{namePrefix}_RuntimeShader_Fallback",
            };
            clone.enableInstancing = true;
            for (int i = 0; i < _swayPropertyNames.Length; i++)
            {
                var prop = _swayPropertyNames[i];
                if (clone.HasProperty(prop)) clone.SetFloat(prop, 0f);
            }
            return clone;
        }

        // Finds the best vanilla donor material in ClutterSystem.m_clutter:
        // Pass 1 prefers the canonical grass names (push-away props pre-set),
        // Pass 2 falls back to the first valid vanilla instanced clutter.
        // Vanilla-only via IsBundleOwnedPrefab identity match.
        private static Material FindVanillaGrassMaterial(GameObject excludePrefab)
        {
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return null;

            // Pass 1: canonical grass donors by name.
            for (int i = 0; i < PreferredGrassDonorNames.Length; i++)
            {
                var wanted = PreferredGrassDonorNames[i];
                foreach (var clutter in clutterSystem.m_clutter)
                {
                    if (clutter?.m_prefab == null || !clutter.m_instanced) continue;
                    if (clutter.m_prefab == excludePrefab) continue;
                    if (clutter.m_prefab.name != wanted) continue;
                    if (VAExternalBundleLoader.IsBundleOwnedPrefab(clutter.m_prefab)) continue;

                    var instanceRenderer = clutter.m_prefab.GetComponent<InstanceRenderer>();
                    if (instanceRenderer?.m_material?.shader == null) continue;
                    if (instanceRenderer.m_material.shader.name.Contains("InternalErrorShader")) continue;
                    return instanceRenderer.m_material;
                }
            }

            // Pass 2: fallback — first vanilla instanced clutter with a valid material.
            foreach (var clutter in clutterSystem.m_clutter)
            {
                if (clutter?.m_prefab == null || !clutter.m_instanced) continue;
                if (clutter.m_prefab == excludePrefab) continue;
                if (VAExternalBundleLoader.IsBundleOwnedPrefab(clutter.m_prefab)) continue;

                var instanceRenderer = clutter.m_prefab.GetComponent<InstanceRenderer>();
                if (instanceRenderer?.m_material?.shader == null) continue;
                if (instanceRenderer.m_material.shader.name.Contains("InternalErrorShader")) continue;
                return instanceRenderer.m_material;
            }
            return null;
        }

        private static Mesh FindVanillaGrassMesh(GameObject excludePrefab)
        {
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return null;
            foreach (var clutter in clutterSystem.m_clutter)
            {
                if (clutter?.m_prefab == null || !clutter.m_instanced) continue;
                if (clutter.m_prefab == excludePrefab) continue;
                if (VAExternalBundleLoader.IsBundleOwnedPrefab(clutter.m_prefab)) continue;
                var instanceRenderer = clutter.m_prefab.GetComponent<InstanceRenderer>();
                if (instanceRenderer?.m_mesh != null) return instanceRenderer.m_mesh;
            }
            return null;
        }
    }
}
