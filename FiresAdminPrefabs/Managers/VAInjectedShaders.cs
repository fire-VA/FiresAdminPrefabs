using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Managers
{
    // Injected bundles were built for their authors' mods, and those mods swap stand-ins for the game's assets at
    // load: Balrond ships `Balrond/X` shaders and swaps them for the game's `Custom/X`; Jotunn and PieceManager ship
    // `JVLmock_X` / `_REPLACE_X` materials, shaders and textures that name the game's X. FAP loads the bundles without
    // those mods, so this pass performs the same swaps with the authors' own rules, once per material. A material it
    // adopts is left alone by FAP's name-based heuristics.
    internal static class VAInjectedShaders
    {
        private const string BalrondPrefix = "Balrond/";
        private const string GamePrefix = "Custom/";
        private const string JotunnPrefix = "JVLmock_";
        private const string PieceManagerPrefix = "_REPLACE_";
        private const string InstanceSuffix = " (Instance)";
        private const string ParticleMarker = "Particle";
        private const string ScrollingTexMarker = "ScrollingTex";
        private const string MainTexProperty = "_MainTex";

        private static readonly string[] BalrondKeptShaders =
        {
            "Balrond/Particles/Standard Surface",
            "Balrond/Particles/Standard Unlit",
        };

        private const float CutoffEpsilon = 0.001f;
        private const float DefaultCutoff = 0.5f;
        private const float CutoutMode = 1f;
        private const float FlagOn = 1f;
        private const float BlendOne = (float)BlendMode.One;
        private const float BlendZero = (float)BlendMode.Zero;
        private const float CullOff = (float)CullMode.Off;
        private const int AlphaTestQueue = (int)RenderQueue.AlphaTest;
        private const int TransparentQueue = (int)RenderQueue.Transparent;

        private static readonly string[] CutoffProperties = { "_AlphaCutoff", "_Cutoff", "_CutoffThreshold" };
        private static readonly string[] AlphaTestKeywords = { "_ALPHATEST_ON", "ALPHATEST_ON" };
        private static readonly string[] EmissionKeywords = { "_EMISSION", "EMISSION" };
        private const string NormalMapKeyword = "_NORMALMAP";
        private const string EmissionEnabledProperty = "_EmissionEnabled";
        private const string ModeProperty = "_Mode";
        private const string ZWriteProperty = "_ZWrite";
        private const string SrcBlendProperty = "_SrcBlend";
        private const string DstBlendProperty = "_DstBlend";
        private const string CullProperty = "_Cull";

        private static readonly string[] NormalTextures = { "_BumpMap", "_NormalTex", "_Normal" };
        private static readonly string[] EmissionTextures = { "_EmissionMap", "_EmissiveTex" };
        private static readonly string[] EmissionColors = { "_EmissionColor", "_EmissiveColor" };

        private static readonly (string from, string to)[] TextureAliases =
        {
            ("_NormalTex", "_BumpMap"), ("_BumpMap", "_NormalTex"),
            ("_EmissiveTex", "_EmissionMap"), ("_EmissionMap", "_EmissiveTex"),
            ("_MetalTex", "_MetallicGlossMap"), ("_MetallicTex", "_MetallicGlossMap"), ("_GlossMap", "_MetallicGlossMap"),
        };

        private static readonly (string from, string to)[] FloatAliases =
        {
            ("_NormalScale", "_BumpScale"),
            ("_MetalGloss", "_Glossiness"), ("_MetalGlossiness", "_Glossiness"), ("_MetallicAlphaGloss", "_Glossiness"),
        };

        private static readonly string[] CutoutMaterialWords = { "grass", "flower", "plant", "leaf", "bush", "shrub", "heath", "fern" };
        private static readonly string[] CutoutShaderWords = { "vegetation", "grass" };
        private const string DoubleSidedShaderWord = "Vegetation";

        private enum Author { None, Balrond, Jotunn, Game }

        private sealed class Snapshot
        {
            internal string[] Keywords;
            internal int Queue;
            internal readonly Dictionary<string, Texture> Textures = new Dictionary<string, Texture>(StringComparer.Ordinal);
            internal readonly Dictionary<string, (Vector2 scale, Vector2 offset)> Placement = new Dictionary<string, (Vector2, Vector2)>(StringComparer.Ordinal);
            internal readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        }

        private static readonly Dictionary<Material, bool> s_adopted = new Dictionary<Material, bool>();
        private static readonly Dictionary<Material, Material> s_gameMaterialFor = new Dictionary<Material, Material>();
        private static Dictionary<string, Shader> s_gameShaders;
        private static bool s_gameShadersBuiltWithScene;
        private static Dictionary<string, Material> s_gameMaterials;
        private static HashSet<string> s_mockTextureMaterials;
        private static readonly List<int> s_textureIds = new List<int>();

        private static readonly SortedDictionary<string, int> s_balrondByTarget = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private static readonly SortedDictionary<string, int> s_unresolved = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private static int s_cutout;
        private static int s_keptBlend;
        private static int s_jotunnShaders;
        private static int s_mockMaterials;
        private static int s_mockTextures;
        private static int s_rebound;

        private static bool IsHeadless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        internal static bool IsAdopted(Material material) =>
            material != null && s_adopted.TryGetValue(material, out bool adopted) && adopted;

        internal static bool IsParticle(Renderer renderer, Material material) =>
            renderer is ParticleSystemRenderer || IsParticleShader(material?.shader);

        internal static bool IsParticleShader(Shader shader) =>
            shader != null && IsParticleName(shader.name);

        private static bool IsParticleName(string shaderName) =>
            shaderName.IndexOf(ParticleMarker, StringComparison.OrdinalIgnoreCase) >= 0;

        // The shader an author meant by a recorded name: Balrond's and Jotunn's stand-ins name the game's shader.
        internal static string IntendedShaderName(string recorded)
        {
            switch (AuthorOf(recorded))
            {
                case Author.Balrond: return GamePrefix + recorded.Substring(BalrondPrefix.Length);
                case Author.Jotunn: return recorded.Substring(JotunnPrefix.Length);
                default: return recorded;
            }
        }

        internal static bool TryGameShader(string name, out Shader shader)
        {
            shader = null;
            if (string.IsNullOrEmpty(name) || IsHeadless) return false;
            EnsureGameShaders();
            return s_gameShaders.TryGetValue(name, out shader);
        }

        // A Jotunn or PieceManager mock material stands for the game's material of the same name.
        internal static bool TryGameMaterial(Material material, out Material game)
        {
            game = null;
            if (material == null || IsHeadless) return false;
            if (s_gameMaterialFor.TryGetValue(material, out game)) return game != null;

            string name = MockedName(material.name);
            if (name != null)
            {
                EnsureGameMaterials();
                if (s_gameMaterials.TryGetValue(name, out game))
                {
                    s_adopted[game] = true;
                    s_mockMaterials++;
                }
                else Unresolved(material.name);
            }
            s_gameMaterialFor[material] = game;
            return game != null;
        }

        // Swaps an author's stand-in for the game's shader and applies that author's material rules. The recorded
        // name stands in for a shader that no longer loads. True when the material now follows its author, so FAP's
        // heuristics must not touch it.
        internal static bool Adopt(Material material, bool particle, string recordedShader = null)
        {
            if (material == null || IsHeadless) return false;
            if (s_adopted.TryGetValue(material, out bool known)) return known;

            string authored = recordedShader ?? material.shader?.name;
            bool adopted;
            switch (AuthorOf(authored))
            {
                case Author.Balrond: adopted = AdoptBalrond(material, authored, particle); break;
                case Author.Jotunn: adopted = AdoptJotunn(material, authored); break;
                case Author.Game: adopted = AdoptGame(material, authored); break;
                default: adopted = false; break;
            }
            ReplaceMockTextures(material, authored);
            s_adopted[material] = adopted;
            return adopted;
        }

        private static Author AuthorOf(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return Author.None;
            if (shaderName.StartsWith(BalrondPrefix, StringComparison.Ordinal))
                return IsBalrondKept(shaderName) ? Author.None : Author.Balrond;
            if (shaderName.StartsWith(JotunnPrefix, StringComparison.Ordinal)) return Author.Jotunn;
            if (shaderName.StartsWith(GamePrefix, StringComparison.Ordinal)) return Author.Game;
            return Author.None;
        }

        private static bool IsBalrondKept(string shaderName) =>
            shaderName.IndexOf(ScrollingTexMarker, StringComparison.Ordinal) >= 0 || BalrondKeptShaders.Contains(shaderName);

        private static bool AdoptBalrond(Material material, string authored, bool particle)
        {
            string target = IntendedShaderName(authored);
            if (!TryGameShader(target, out Shader shader))
            {
                Unresolved(authored);
                return false;
            }

            bool queueAuthored = material.shader != null && material.shader.isSupported;
            var snapshot = Capture(material);
            material.shader = shader;
            material.shaderKeywords = snapshot.Keywords;
            if (queueAuthored) material.renderQueue = snapshot.Queue;
            ApplyAliases(material, snapshot);

            if (particle || IsParticleName(authored) || material.renderQueue >= TransparentQueue) s_keptBlend++;
            else if (ApplyBalrondCutout(material, snapshot, authored)) s_cutout++;

            ApplyFeatureKeywords(material, snapshot);
            material.enableInstancing = true;
            s_balrondByTarget.TryGetValue(target, out int count);
            s_balrondByTarget[target] = count + 1;
            return true;
        }

        // The cutout settings Balrond's bundles expect on their stand-in shaders, applied in one pass of our own.
        private static bool ApplyBalrondCutout(Material material, Snapshot snapshot, string authored)
        {
            if (WantsFullCutout(material, authored))
            {
                SetCutout(material, snapshot);
                SetFloat(material, CullProperty, CullOff);
                material.renderQueue = AlphaTestQueue;
                return true;
            }

            if (!CutoffProperties.Any(p => material.HasProperty(p))) return false;
            SetCutout(material, snapshot);
            if (material.renderQueue < AlphaTestQueue) material.renderQueue = AlphaTestQueue;
            if (authored.IndexOf(DoubleSidedShaderWord, StringComparison.Ordinal) >= 0) SetFloat(material, CullProperty, CullOff);
            return true;
        }

        private static bool WantsFullCutout(Material material, string authored)
        {
            string materialName = material.name.ToLowerInvariant();
            string shaderName = authored.ToLowerInvariant();
            if (CutoutMaterialWords.Any(w => materialName.Contains(w)) || CutoutShaderWords.Any(w => shaderName.Contains(w))) return true;
            return material.HasProperty(MainTexProperty) && material.GetTexture(MainTexProperty) != null;
        }

        private static void SetCutout(Material material, Snapshot snapshot)
        {
            float cutoff = FirstPositive(CutoffProperties.Where(p => material.HasProperty(p)).Select(p => material.GetFloat(p)))
                           ?? FirstPositive(CutoffProperties.Where(snapshot.Floats.ContainsKey).Select(p => snapshot.Floats[p]))
                           ?? DefaultCutoff;
            foreach (string property in CutoffProperties) SetFloat(material, property, cutoff);
            foreach (string keyword in AlphaTestKeywords) material.EnableKeyword(keyword);
            SetFloat(material, ModeProperty, CutoutMode);
            SetFloat(material, ZWriteProperty, FlagOn);
            SetFloat(material, SrcBlendProperty, BlendOne);
            SetFloat(material, DstBlendProperty, BlendZero);
        }

        private static void ApplyFeatureKeywords(Material material, Snapshot snapshot)
        {
            bool emissive = EmissionTextures.Any(p => snapshot.Textures.ContainsKey(p))
                            || EmissionColors.Any(p => snapshot.Colors.TryGetValue(p, out Color c) && c.maxColorComponent > CutoffEpsilon);
            if (emissive)
            {
                foreach (string keyword in EmissionKeywords) material.EnableKeyword(keyword);
                SetFloat(material, EmissionEnabledProperty, FlagOn);
            }
            if (NormalTextures.Any(p => material.HasProperty(p)) && NormalTextures.Any(p => snapshot.Textures.ContainsKey(p)))
                material.EnableKeyword(NormalMapKeyword);
        }

        private static bool AdoptJotunn(Material material, string authored)
        {
            if (!TryGameShader(IntendedShaderName(authored), out Shader shader))
            {
                Unresolved(authored);
                return false;
            }
            string[] keywords = material.shaderKeywords;
            material.shader = shader;
            material.shaderKeywords = keywords;
            s_jotunnShaders++;
            return true;
        }

        // A bundle can carry its own compiled copy of a game shader, which may lack the game's variants (instancing,
        // for painted clutter). Its material moves to the game's copy; the properties share names, so nothing is lost.
        private static bool AdoptGame(Material material, string authored)
        {
            if (!TryGameShader(authored, out Shader shader)) return false;
            if (material.shader != shader)
            {
                int queue = material.renderQueue;
                string[] keywords = material.shaderKeywords;
                material.shader = shader;
                material.shaderKeywords = keywords;
                material.renderQueue = queue;
                s_rebound++;
            }
            return true;
        }

        private static void ReplaceMockTextures(Material material, string authored)
        {
            if (!MayCarryMockTextures(material, authored)) return;
            material.GetTexturePropertyNameIDs(s_textureIds);
            foreach (int id in s_textureIds)
            {
                Texture texture = material.GetTexture(id);
                if (texture == null) continue;
                string name = MockedName(texture.name);
                if (name == null) continue;
                if (VATextureLibrary.TryGetTexture(name, out Texture2D game))
                {
                    material.SetTexture(id, game);
                    s_mockTextures++;
                }
                else Unresolved(texture.name);
            }
        }

        private static bool MayCarryMockTextures(Material material, string authored)
        {
            if (AuthorOf(authored) == Author.Jotunn) return true;
            if (s_mockTextureMaterials == null) IndexMockTextureMaterials();
            return s_mockTextureMaterials.Contains(StripInstance(material.name));
        }

        private static void IndexMockTextureMaterials()
        {
            s_mockTextureMaterials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderers in VAExternalBundleLoader.AllRendererDescriptors())
            {
                if (renderers == null) continue;
                foreach (var renderer in renderers)
                {
                    if (renderer?.materials == null) continue;
                    foreach (var descriptor in renderer.materials)
                    {
                        if (descriptor?.name == null || MockedName(descriptor.mainTexture) == null) continue;
                        s_mockTextureMaterials.Add(descriptor.name);
                    }
                }
            }
        }

        private static string MockedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string bare = StripInstance(name);
            if (bare.StartsWith(JotunnPrefix, StringComparison.Ordinal)) return bare.Substring(JotunnPrefix.Length);
            if (bare.StartsWith(PieceManagerPrefix, StringComparison.Ordinal)) return bare.Substring(PieceManagerPrefix.Length);
            return null;
        }

        private static string StripInstance(string name)
        {
            string bare = name;
            while (bare.EndsWith(InstanceSuffix, StringComparison.Ordinal)) bare = bare.Substring(0, bare.Length - InstanceSuffix.Length);
            return bare;
        }

        private static bool IsStandIn(string name) =>
            name.StartsWith(BalrondPrefix, StringComparison.Ordinal) || name.StartsWith(JotunnPrefix, StringComparison.Ordinal);

        // Valheim's own copy wins: shaders read off known vanilla prefabs and vanilla clutter first, then any loaded copy
        // that runs. The seeds matter because a ripped copy that names a Fallback also reports isSupported and draws as
        // that fallback (the rip's Custom/Vegetation as Legacy Diffuse, with no cutout). Built once more after ZNetScene
        // exists, because the vanilla prefabs can only be looked up from then on.
        private static void EnsureGameShaders()
        {
            bool sceneReady = ZNetScene.instance != null;
            if (s_gameShaders != null && (s_gameShadersBuiltWithScene || !sceneReady)) return;
            s_gameShadersBuiltWithScene = sceneReady;

            if (sceneReady) ShaderReplacement.InitializeValheimShaders();
            var shaders = new Dictionary<string, Shader>(StringComparer.Ordinal);
            foreach (var pair in ShaderReplacement.EnumerateCachedShaders())
                if (pair.Value != null && pair.Value.isSupported && !IsStandIn(pair.Key)) shaders[pair.Key] = pair.Value;
            SeedFromVanillaClutter(shaders);

            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (shader == null || !shader.isSupported) continue;
                string name = shader.name;
                if (string.IsNullOrEmpty(name) || IsStandIn(name) || shaders.ContainsKey(name)) continue;
                shaders[name] = shader;
            }
            s_gameShaders = shaders;
        }

        private static void SeedFromVanillaClutter(Dictionary<string, Shader> shaders)
        {
            var clutter = ClutterSystem.instance?.m_clutter;
            if (clutter == null) return;
            foreach (var entry in clutter)
            {
                if (entry?.m_prefab == null || VAExternalBundleLoader.IsBundleOwnedPrefab(entry.m_prefab)) continue;
                foreach (var instanced in entry.m_prefab.GetComponentsInChildren<InstanceRenderer>(true))
                {
                    var shader = instanced.m_material?.shader;
                    if (shader == null || !shader.isSupported || shaders.ContainsKey(shader.name)) continue;
                    shaders[shader.name] = shader;
                }
            }
        }

        private static void EnsureGameMaterials()
        {
            if (s_gameMaterials != null) return;
            s_gameMaterials = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader == null || !material.shader.isSupported) continue;
                string name = material.name;
                if (string.IsNullOrEmpty(name) || MockedName(name) != null || name.EndsWith(InstanceSuffix, StringComparison.Ordinal)) continue;
                if (IsStandIn(material.shader.name) || s_gameMaterials.ContainsKey(name)) continue;
                s_gameMaterials[name] = material;
            }
        }

        private static Snapshot Capture(Material material)
        {
            var snapshot = new Snapshot { Keywords = material.shaderKeywords, Queue = material.renderQueue };
            foreach (var (from, _) in TextureAliases) CaptureTexture(material, snapshot, from);
            foreach (string property in NormalTextures.Concat(EmissionTextures)) CaptureTexture(material, snapshot, property);
            foreach (var (from, _) in FloatAliases)
                if (material.HasProperty(from)) snapshot.Floats[from] = material.GetFloat(from);
            foreach (string property in CutoffProperties)
                if (material.HasProperty(property)) snapshot.Floats[property] = material.GetFloat(property);
            foreach (string property in EmissionColors)
                if (material.HasProperty(property)) snapshot.Colors[property] = material.GetColor(property);
            return snapshot;
        }

        private static void CaptureTexture(Material material, Snapshot snapshot, string property)
        {
            if (snapshot.Textures.ContainsKey(property) || !material.HasProperty(property)) return;
            Texture texture = material.GetTexture(property);
            if (texture == null) return;
            snapshot.Textures[property] = texture;
            snapshot.Placement[property] = (material.GetTextureScale(property), material.GetTextureOffset(property));
        }

        private static void ApplyAliases(Material material, Snapshot snapshot)
        {
            foreach (var (from, to) in TextureAliases)
            {
                if (!material.HasProperty(to) || !snapshot.Textures.TryGetValue(from, out Texture texture)) continue;
                material.SetTexture(to, texture);
                var (scale, offset) = snapshot.Placement[from];
                material.SetTextureScale(to, scale);
                material.SetTextureOffset(to, offset);
            }
            foreach (var (from, to) in FloatAliases)
                if (material.HasProperty(to) && snapshot.Floats.TryGetValue(from, out float value)) material.SetFloat(to, value);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static float? FirstPositive(IEnumerable<float> values)
        {
            foreach (float value in values)
                if (value > CutoffEpsilon) return value;
            return null;
        }

        private static void Unresolved(string name)
        {
            s_unresolved.TryGetValue(name, out int count);
            s_unresolved[name] = count + 1;
        }

        internal static string FormatSummary()
        {
            int balrond = s_balrondByTarget.Values.Sum();
            string targets = string.Join(", ", s_balrondByTarget.OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}"));
            string line = $"author shaders: Balrond stand-ins -> game shaders on {balrond} material(s) ({targets}), cutout set on {s_cutout}, blend kept on {s_keptBlend} particle/transparent; "
                          + $"Jotunn/PieceManager mocks -> game assets: {s_mockMaterials} material(s), {s_jotunnShaders} shader(s), {s_mockTextures} texture(s); "
                          + $"bundle copies of game shaders moved to the game's: {s_rebound}";
            if (s_unresolved.Count == 0) return line + "; nothing left unresolved";
            string misses = string.Join(", ", s_unresolved.OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}"));
            return line + $"; NO game asset for (kept as shipped): {misses}";
        }
    }
}
