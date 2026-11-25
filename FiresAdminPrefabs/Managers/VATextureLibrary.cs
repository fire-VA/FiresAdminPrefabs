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

        // 1.1.249 ([Diagnostics] "Texture library at spawn", default OFF; A/B): before the local player spawns the scan is
        // deferred (lookups find nothing), then Player.OnSpawned builds it. Keeps the scan out of Unity's scene-switch sweep.
        private static int _deferredLookups;
        private static bool AtSpawn => VerdantsAscent.Managers.ConfigManager.Instance?.configTextureLibraryAtSpawn?.Value ?? false;

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class BuildAtSpawn
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || _built || !AtSpawn) return;
                VaPieces.Log?.LogDebug($"[TextureLibrary] at spawn: building now ({_deferredLookups} lookup(s) before the spawn found nothing)");
                Build();
            }
        }

        public static void Build()
        {
            if (_built) return;
            if (AtSpawn && Player.m_localPlayer == null && !VaPieces.IsDedicatedServer)
            {
                if (_deferredLookups++ == 0) VaPieces.Log?.LogDebug("[TextureLibrary] deferred to the player's spawn ([Diagnostics] Texture library at spawn = true)");
                return;
            }
            _built = true;
            // 1.1.222 ([worldgen] / the lead 10-13, dedi memory): the scan loads every Texture2D of every loaded bundle, and a
            // dedicated server draws nothing. Its only users (VAMaterialRepair, the injected shaders) set material textures
            // for rendering; on a dedi their lookups simply find nothing and leave the material as it is.
            if (VaPieces.IsDedicatedServer)
            {
                Debug.Log($"{VaPieces.PluginName}: VATextureLibrary skipped on the dedicated server (it only feeds rendering); no bundle textures loaded for it.");
                return;
            }

            // 1.1.438 ([ghost], lead 11-19 perf audit FAP#1): this used to LoadAllAssets<Texture2D>() on EVERY loaded bundle (other
            // mods' and FAP's lazy ones too), synchronously, and keep all of them (HandTest log: 3609 textures across 82 bundles), which
            // pulled every bundle texture into RAM and undid the lazy bundles. Now it only reads each bundle's asset NAMES (no asset
            // loads) into a name -> (bundle, path) index; TryGetTexture loads the one texture a material asks for, from the first
            // bundle that has it (the same first-bundle-wins order as before). A name no bundle file carries still falls back to
            // the textures already in memory (embedded / game textures), exactly as before.
            try
            {
                int names = 0;
                int bundles = 0;
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    if (bundle.isStreamedSceneAssetBundle) continue;
                    bundles++;

                    string[] paths;
                    try { paths = bundle.GetAllAssetNames(); }
                    catch { continue; }
                    if (paths == null) continue;

                    foreach (var path in paths)
                    {
                        if (string.IsNullOrEmpty(path) || !IsTextureFile(path)) continue;
                        string key = System.IO.Path.GetFileNameWithoutExtension(path);   // asset names are lower-case; texture names are not
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!_index.TryGetValue(key, out var list)) { list = new List<KeyValuePair<AssetBundle, string>>(1); _index[key] = list; names++; }
                        list.Add(new KeyValuePair<AssetBundle, string>(bundle, path));
                    }
                }

                _scannedBundles = bundles;
                Debug.Log($"{VaPieces.PluginName}: VATextureLibrary built — {names} texture name(s) indexed across {bundles} bundle(s), nothing loaded; each texture loads when a material asks for it (at spawn: {AtSpawn}, frame {Time.frameCount}).");
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

            // 1.1.438: a bundle copy (loaded now, on first ask) keeps priority over loose files and in-memory game textures
            if (TryLoadIndexed(name, out texture)) return true;
            if (_loose.TryGetValue(name, out texture) && texture != null) { _texturesByName[name] = texture; return true; }

            IndexGameTextures();
            return _texturesByName.TryGetValue(name, out texture) && texture != null;
        }

        // 1.1.438: name -> every (bundle, asset path) carrying a texture FILE of that name, in loaded-bundle order.
        private static readonly Dictionary<string, List<KeyValuePair<AssetBundle, string>>> _index =
            new Dictionary<string, List<KeyValuePair<AssetBundle, string>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _indexMissed = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Texture2D> _loose = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static int _loadedOnDemand;

        private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif", ".dds" };

        private static bool IsTextureFile(string path)
        {
            foreach (var ext in TextureExtensions)
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool TryLoadIndexed(string name, out Texture2D texture)
        {
            texture = null;
            if (_indexMissed.Contains(name) || !_index.TryGetValue(name, out var list)) return false;
            foreach (var entry in list)
            {
                if (entry.Key == null) continue;   // bundle unloaded since the index was built
                Texture2D t = null;
                try { t = entry.Key.LoadAsset<Texture2D>(entry.Value); } catch { }
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                if (!_texturesByName.ContainsKey(t.name)) { _texturesByName[t.name] = t; _loadedOnDemand++; _cachedTextures++; }
                if (string.Equals(t.name, name, StringComparison.Ordinal)) { texture = _texturesByName[t.name]; return true; }
            }
            _indexMissed.Add(name);   // only case-different copies: never walk this list again
            return false;
        }

        /// <summary>1.1.438: how many bundle textures were actually loaded (on demand) — for the load / RAM comparison.</summary>
        public static int LoadedOnDemand => _loadedOnDemand;

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
                    if (_index.ContainsKey(texture.name) && !_indexMissed.Contains(texture.name)) continue;   // 1.1.438: a bundle file of that name still wins (loaded on its first ask)
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
            _index.Clear();
            _indexMissed.Clear();
            _loose.Clear();
            _loadedOnDemand = 0;
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
            // 1.1.438: a bundle texture file of the same name still wins (it is no longer pre-loaded, so check the index, not the cache)
            // (the index ignores case because bundle asset names are lower-case; textures are named after their file)
            if (_texturesByName.ContainsKey(name) || _loose.ContainsKey(name) || _index.ContainsKey(name)) return false;
            _loose[name] = tex;
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
                try { PostfixBody(); }   // 1.1.318 (audit M8): a throw no longer skips the later Awake postfixes
                catch (System.Exception e) { UnityEngine.Debug.LogError($"[FAP] texture library: ZNetScene.Awake build failed: {e.Message}"); }
            }

            private static void PostfixBody()
            {
                Build();
                Utilities.VATextureFolderLoader.LoadAll();
            }
        }
    }
}
