using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Cross-bundle Texture2D cache, mirror of VAShaderLibrary for textures.
    // VAMaterialRepair uses it to resolve manifest-recorded texture names
    // against every loaded AssetBundle (textures often ship in companion
    // bundles separate from their materials).
    //
    // Lazy build: first TryGetTexture walks every loaded bundle's Texture2D
    // assets and caches name → first-found. First-bundle-wins on collisions.
    public static class VATextureLibrary
    {
        private static readonly Dictionary<string, Texture2D> _texturesByName =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        private static bool _built;
        private static int _scannedBundles;
        private static int _cachedTextures;

        public static void Build()
        {
            if (_built) return;
            _built = true;

            try
            {
                int textures = 0;
                int bundles = 0;
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    if (bundle.isStreamedSceneAssetBundle) continue;
                    bundles++;

                    Texture2D[] all;
                    try { all = bundle.LoadAllAssets<Texture2D>(); }
                    catch { continue; }
                    if (all == null) continue;

                    foreach (var t in all)
                    {
                        if (t == null) continue;
                        if (string.IsNullOrEmpty(t.name)) continue;
                        if (!_texturesByName.ContainsKey(t.name))
                        {
                            _texturesByName[t.name] = t;
                            textures++;
                        }
                    }
                }

                _scannedBundles = bundles;
                _cachedTextures = textures;
                Debug.Log($"{VaPieces.PluginName}: VATextureLibrary built — {textures} unique texture(s) cached across {bundles} bundle(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VATextureLibrary.Build failed: {ex.Message}");
            }
        }

        public static bool TryGetTexture(string name, out Texture2D texture)
        {
            texture = null;
            if (string.IsNullOrEmpty(name)) return false;
            if (!_built) Build();
            if (_texturesByName.TryGetValue(name, out texture) && texture != null) return true;

            IndexGameTextures();
            return _texturesByName.TryGetValue(name, out texture) && texture != null;
        }

        // Bundles routinely name a texture they do not ship, because it is the GAME's. 'wildfire06' is named
        // 56 times across five Balrond manifests and lives on vanilla's own wraith_smoke and
        // distant_cloud_planes_2 particle materials; no bundle carries it. Walking only loaded bundles left
        // every one of those unresolved, the material kept a null _MainTex, and the renderer drew untextured.
        //
        // On a ParticleSystemRenderer that does not read as the usual white-untextured look: a bare quad shows
        // the material's own colour instead, so CapeInfernalNomad_bal's flame_large2 (0.23, 1.06, 0.22) drew as
        // large flat GREEN planes. A missing TEXTURE and a broken SHADER look nothing alike.
        //
        // Indexed on the first miss rather than up front, so a session whose bundles are self-contained never
        // pays for it, and bundle copies still win every shared name exactly as Build's first-write-wins does.
        private static bool _gameTexturesIndexed;

        private static void IndexGameTextures()
        {
            if (_gameTexturesIndexed) return;
            _gameTexturesIndexed = true;
            try
            {
                Texture2D[] loaded = Resources.FindObjectsOfTypeAll<Texture2D>();
                int added = 0;
                foreach (var texture in loaded)
                {
                    if (texture == null || string.IsNullOrEmpty(texture.name)) continue;
                    if (_texturesByName.ContainsKey(texture.name)) continue;
                    _texturesByName[texture.name] = texture;
                    added++;
                }
                _cachedTextures += added;
                Debug.Log($"{VaPieces.PluginName}: VATextureLibrary — a bundle lookup missed, so {added} game texture(s) " +
                          $"were indexed as a fallback (bundle copies keep priority on shared names).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VATextureLibrary — could not index the game's textures: {ex.Message}");
            }
        }

        public static void Invalidate()
        {
            _texturesByName.Clear();
            _built = false;
            _gameTexturesIndexed = false;
            _scannedBundles = 0;
            _cachedTextures = 0;
        }

        // Companion path for textures loaded from disk by
        // VATextureFolderLoader (no bundle to walk). Same first-write-wins
        // policy as Build's bundle walk.
        public static bool RegisterLooseTexture(string name, Texture2D tex)
        {
            if (string.IsNullOrEmpty(name) || tex == null) return false;
            if (_texturesByName.ContainsKey(name)) return false;
            _texturesByName[name] = tex;
            _cachedTextures++;
            return true;
        }

        // Priority.Last so we run after bundle-injection prefixes have
        // populated Unity's loaded-bundle table. Loose-folder load runs
        // AFTER Build so bundle copies claim shared names first.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VATextureLibrary_Patch
        {
            [HarmonyPriority(Priority.Last)]
            [HarmonyPostfix]
            public static void Postfix()
            {
                Build();
                Utilities.VATextureFolderLoader.LoadAll();
            }
        }
    }
}
