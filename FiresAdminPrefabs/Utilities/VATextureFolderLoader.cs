using System;
using System.IO;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // Loads loose PNG textures FiresSteamyDumps writes alongside scanned
    // bundles into VATextureLibrary so VAMaterialRepair's existing
    // TryGetTexture path finds them. Each PNG's filename (without .png)
    // becomes its registered texture name, matching how it left
    // FiresSteamyDumps' TextureDumper.
    //
    // Layout:
    //   BepInEx/config/FiresAdminPrefabs/Textures/<bundleName>/<textureName>.png
    public static class VATextureFolderLoader
    {
        private const string SubfolderName = "FiresAdminPrefabs";
        private const string TexturesFolderName = "Textures";
        private const int TempTextureSeed = 2;

        public static string TexturesDir =>
            Path.Combine(Paths.ConfigPath, SubfolderName, TexturesFolderName);

        private static bool _loaded;

        public static int LoadAll()
        {
            if (_loaded) return 0;
            _loaded = true;

            var dir = TexturesDir;
            if (!Directory.Exists(dir))
            {
                if (ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.Log($"{VaPieces.PluginName}: VATextureFolderLoader — no Textures/ folder at '{dir}', skipping.");
                return 0;
            }

            int totalLoaded = 0;
            int totalSkipped = 0;
            int totalFailed = 0;
            int bundlesWithTextures = 0;

            string[] bundleDirs;
            try { bundleDirs = Directory.GetDirectories(dir); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VATextureFolderLoader enumerate failed on '{dir}': {ex.Message}");
                return 0;
            }

            foreach (var bundleDir in bundleDirs)
            {
                string[] pngs;
                try { pngs = Directory.GetFiles(bundleDir, "*.png"); }
                catch { continue; }
                if (pngs == null || pngs.Length == 0) continue;

                bundlesWithTextures++;
                foreach (var pngPath in pngs)
                {
                    string texName = Path.GetFileNameWithoutExtension(pngPath);
                    if (string.IsNullOrEmpty(texName)) continue;

                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(pngPath); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: VATextureFolderLoader read failed for '{pngPath}': {ex.Message}");
                        totalFailed++;
                        continue;
                    }

                    // Texture2D.LoadImage lives in ImageConversionModule
                    // (netstandard 2.1) which this csproj can't reference
                    // directly. VAPieceIconRenderer wraps the reflection.
                    var tex = new Texture2D(TempTextureSeed, TempTextureSeed, TextureFormat.RGBA32, mipChain: false);
                    bool loaded = VAPieceIconRenderer.LoadImageIntoTexture(tex, bytes);
                    if (!loaded)
                    {
                        UnityEngine.Object.Destroy(tex);
                        totalFailed++;
                        continue;
                    }
                    tex.name = texName;
                    UnityEngine.Object.DontDestroyOnLoad(tex);

                    if (VATextureLibrary.RegisterLooseTexture(texName, tex))
                        totalLoaded++;
                    else
                    {
                        UnityEngine.Object.Destroy(tex);
                        totalSkipped++;
                    }
                }
            }

            Debug.Log($"{VaPieces.PluginName}: VATextureFolderLoader loaded {totalLoaded} loose texture(s) across {bundlesWithTextures} bundle folder(s) (skipped {totalSkipped} duplicates, {totalFailed} failed).");
            return totalLoaded;
        }
    }
}
