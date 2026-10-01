using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Runtime shader cache built from FiresAdminPrefabs/Shaders/registry.json, which
    // SteamyDumps' `export_shaders` writes.
    //
    // Entries are "rehost" (v1, what export_shaders produces): the entry names its
    // source bundle, which VAExternalBundleLoader has already loaded, so the shader
    // comes from LoadAllAssets<Shader>. "embedded" is the not-yet-built v2 — see
    // ResolveEmbedded.
    //
    // A Shader.Find postfix gap-fills: vanilla wins whenever it returns non-null, so
    // the library can't shadow a vanilla shader even if a mod ships one named
    // "Standard". LoadFromConfig is idempotent.
    public static class VAShaderLibrary
    {
        // Highest schema version VAShaderLibrary understands. Registries
        // with a higher schema version still load — extra fields are
        // ignored with a best-effort warning, since the reader is
        // permissive about forward-compat additions.
        private const int SupportedRegistrySchemaVersion = 1;

        // Cached shaders keyed by shader name (as it appears in
        // Shader.Find calls). Populated by LoadFromConfig at ZNetScene.Awake
        // postfix time. Read-mostly after that; the Shader.Find patch
        // looks here for gap-fill matches.
        private static readonly Dictionary<string, Shader> _shadersByName =
            new Dictionary<string, Shader>(StringComparer.Ordinal);

        // Per-bundle bundle-handle cache so we don't call LoadAllAssets
        // twice on the same bundle. AssetBundle.LoadAllAssets walks the
        // whole asset table — running it once per bundle (regardless of
        // how many shaders that bundle contributes) is the cheap path.
        private static readonly Dictionary<string, AssetBundle> _bundleByFilename =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        // Paths
        public static string ShadersDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "Shaders");
        public static string AssetsDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "Assets");
        public static string RegistryPath =>
            Path.Combine(ShadersDir, "registry.json");

        // Public read API. Used by the Shader.Find postfix and any
        // downstream code that wants to consult the cache directly.
        public static bool TryGetShader(string name, out Shader shader)
        {
            if (string.IsNullOrEmpty(name))
            {
                shader = null;
                return false;
            }
            return _shadersByName.TryGetValue(name, out shader);
        }

        // ──────────────────────────────────────────────────────────────
        //  Registry load
        // ──────────────────────────────────────────────────────────────

        // Parses the registry, walks each entry, dispatches to the
        // mode-specific loader. Logs counts + per-mode failures. Called
        // from ZNetScene.Awake postfix (so source bundles in FAP/Assets/
        // have been loaded by VAExternalBundleLoader's prefix already)
        // exactly once per session.
        public static void LoadFromConfig()
        {
            if (_loaded) return;
            _loaded = true;

            if (!File.Exists(RegistryPath))
            {
                // Common case: user hasn't run export_shaders yet. No log
                // spam — this is a normal "no shader library configured"
                // state, not a failure.
                return;
            }

            ShaderRegistryDto registry;
            try
            {
                var json = File.ReadAllText(RegistryPath);
                registry = JsonConvert.DeserializeObject<ShaderRegistryDto>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — failed to parse {RegistryPath}: {ex.Message}");
                return;
            }

            if (registry == null || registry.shaders == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — registry parsed to null or empty.");
                return;
            }

            if (registry.version > SupportedRegistrySchemaVersion)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — registry schema version {registry.version} is newer than supported ({SupportedRegistrySchemaVersion}). Reading best-effort; unknown fields ignored.");
            }

            int rehosted = 0;
            int embedded = 0;
            int rehostFailedMissingBundle = 0;
            int rehostFailedMissingShader = 0;
            int embeddedFailed = 0;
            int duplicateSkipped = 0;
            int collisionsWithVanilla = 0;

            foreach (var entry in registry.shaders)
            {
                if (entry == null || string.IsNullOrEmpty(entry.name)) continue;

                if (_shadersByName.ContainsKey(entry.name))
                {
                    duplicateSkipped++;
                    continue;
                }

                // Collision check with vanilla: if Shader.Find returns a
                // real shader for this name, vanilla wins (gap-fill mode).
                // We still count the entry — the cache will still answer
                // the lookup if Find ever returns null for this name in
                // the future — but we DON'T cache the mod variant for
                // names vanilla already owns. (If a user wants override
                // mode, that's a future config toggle; current behavior
                // is strict gap-fill.)
                var vanilla = Shader.Find(entry.name);
                if (vanilla != null)
                {
                    collisionsWithVanilla++;
                    continue;
                }

                Shader resolved = null;
                switch ((entry.mode ?? "").ToLowerInvariant())
                {
                    case "rehost":
                        resolved = TryLoadRehost(entry, out var rehostErr);
                        if (resolved == null)
                        {
                            if (rehostErr == RehostError.MissingBundle) rehostFailedMissingBundle++;
                            else if (rehostErr == RehostError.MissingShader) rehostFailedMissingShader++;
                        }
                        else
                        {
                            rehosted++;
                        }
                        break;

                    case "embedded":
                        resolved = TryLoadEmbedded(entry);
                        if (resolved == null) embeddedFailed++;
                        else embedded++;
                        break;

                    default:
                        Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — shader '{entry.name}' has unrecognized mode '{entry.mode}', skipping.");
                        break;
                }

                if (resolved != null)
                    _shadersByName[entry.name] = resolved;
            }

            Debug.Log($"{VaPieces.PluginName}: VAShaderLibrary loaded {_shadersByName.Count} shader(s) from {registry.shaders.Count} registry entries " +
                      $"(rehost OK={rehosted}, embedded OK={embedded}, rehost missing-bundle={rehostFailedMissingBundle}, rehost missing-shader={rehostFailedMissingShader}, embedded fail={embeddedFailed}, dupe-skipped={duplicateSkipped}, vanilla-collision={collisionsWithVanilla}).");
        }

        private enum RehostError
        {
            None,
            MissingBundle,
            MissingShader,
        }

        // Rehost mode: source bundle is in FAP/Assets/, we open it and
        // pull the shader by name. AssetBundle.LoadFromFile is idempotent
        // on the same path (Unity returns the existing handle) — so this
        // is safe to call regardless of whether VAExternalBundleLoader
        // already loaded it.
        private static Shader TryLoadRehost(ShaderRegistryEntryDto entry, out RehostError err)
        {
            err = RehostError.None;
            if (string.IsNullOrEmpty(entry.sourceBundleFile))
            {
                err = RehostError.MissingBundle;
                return null;
            }

            string bundlePath = Path.Combine(AssetsDir, entry.sourceBundleFile);
            if (!File.Exists(bundlePath))
            {
                err = RehostError.MissingBundle;
                return null;
            }

            AssetBundle bundle;
            if (!_bundleByFilename.TryGetValue(entry.sourceBundleFile, out bundle) || bundle == null)
            {
                try { bundle = AssetBundle.LoadFromFile(bundlePath); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — LoadFromFile('{bundlePath}') threw: {ex.Message}");
                    err = RehostError.MissingBundle;
                    return null;
                }
                if (bundle == null)
                {
                    err = RehostError.MissingBundle;
                    return null;
                }
                _bundleByFilename[entry.sourceBundleFile] = bundle;
            }

            Shader[] all;
            try { all = bundle.LoadAllAssets<Shader>(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — LoadAllAssets<Shader>() on '{entry.sourceBundleFile}' threw: {ex.Message}");
                err = RehostError.MissingShader;
                return null;
            }
            if (all == null || all.Length == 0)
            {
                err = RehostError.MissingShader;
                return null;
            }

            foreach (var sh in all)
            {
                if (sh != null && sh.name == entry.name) return sh;
            }
            err = RehostError.MissingShader;
            return null;
        }

        // Embedded mode is a forward-compat stub: the loader is wired but returns null,
        // and export_shaders v1 never writes registry entries that use it.
        //
        // Loading would be trivial — AssetBundle.LoadFromFile on the .shaderpkg, then
        // LoadAsset<Shader>. Producing that file is the blocker: it means fabricating a
        // valid single-asset UnityFS container (header, ObjectTable, TypeTree, the
        // shader's binary range, a rebuilt block table and asset directory). UABEA's
        // AssetsTools.NET does it at editor time; doing it at runtime is its own
        // project, and it belongs on the SteamyDumps side.
        private static Shader TryLoadEmbedded(ShaderRegistryEntryDto entry)
        {
            if (string.IsNullOrEmpty(entry.embeddedFile)) return null;

            string pkgPath = Path.Combine(ShadersDir, entry.embeddedFile);
            if (!File.Exists(pkgPath))
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — embedded mode entry '{entry.name}' references '{pkgPath}' but file is missing.");
                return null;
            }

            AssetBundle pkg;
            try { pkg = AssetBundle.LoadFromFile(pkgPath); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — LoadFromFile('{pkgPath}') threw: {ex.Message}");
                return null;
            }
            if (pkg == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — '{pkgPath}' loaded as null (not a valid .shaderpkg).");
                return null;
            }

            try
            {
                var sh = pkg.LoadAsset<Shader>(entry.name);
                if (sh == null)
                {
                    // Fallback: pkg author may have stored the shader
                    // under its bundle-internal asset name rather than
                    // the shader's display name.
                    var all = pkg.LoadAllAssets<Shader>();
                    if (all != null)
                    {
                        foreach (var s in all)
                        {
                            if (s != null && s.name == entry.name) return s;
                        }
                    }
                }
                return sh;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAShaderLibrary — LoadAsset<Shader>('{entry.name}') from '{entry.embeddedFile}' threw: {ex.Message}");
                return null;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  ZNetScene.Awake postfix — trigger load
        // ──────────────────────────────────────────────────────────────

        // VAExternalBundleLoader's prefix on ZNetScene.Awake adds bundle
        // prefabs to scene.m_prefabs BEFORE vanilla builds m_namedPrefabs.
        // We piggy-back on the postfix (HarmonyPriority.Last so we run
        // after VAExternalBundleLoader's scrub) — the source bundles are
        // already loaded into Unity's bundle table by that point, so
        // AssetBundle.LoadFromFile in the rehost path returns the existing
        // handle for free.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_VAShaderLibrary_Patch
        {
            [HarmonyPriority(HarmonyLib.Priority.Last)]
            [HarmonyPostfix]
            public static void Postfix() => LoadFromConfig();
        }

        // ──────────────────────────────────────────────────────────────
        //  Shader.Find postfix — gap-fill lookups
        // ──────────────────────────────────────────────────────────────

        // Postfix gap-fill: vanilla wins when Shader.Find returns non-null;
        // otherwise we consult the cache. This is intentionally NOT a
        // prefix override — we never shadow vanilla shaders. The cache is
        // for "this shader name has no vanilla owner; route to a mod
        // shader that happens to provide it" cases.
        //
        // Why a Shader.Find patch instead of a per-prefab fixup pass:
        //   - Mod prefab materials reference shaders BY NAME at load time
        //     via Shader.Find (or equivalent serialized references that
        //     resolve through the same dictionary). Intercepting Find
        //     covers ALL call sites — vanilla, modded, and bundle-loaded
        //     materials that resolve shaders lazily — with one patch.
        //   - A per-prefab fixup would need to walk every Renderer's
        //     materials, identify missing shaders, and swap. Same outcome
        //     for the common case; more code surface; slower.
        [HarmonyPatch(typeof(Shader), nameof(Shader.Find))]
        public static class Shader_Find_VAShaderLibrary_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string name, ref Shader __result)
            {
                if (__result != null) return;        // vanilla wins — gap-fill only
                if (string.IsNullOrEmpty(name)) return;
                if (_shadersByName.TryGetValue(name, out var cached) && cached != null)
                    __result = cached;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Registry DTOs (mirrors SteamyDumps' ExportShadersToFap output)
        // ──────────────────────────────────────────────────────────────

        // Registry JSON shapes, filled by Newtonsoft; only the fields FAP reads are declared.
#pragma warning disable CS0649
        [Serializable]
        private class ShaderRegistryDto
        {
            public int version;
            public List<ShaderRegistryEntryDto> shaders;
        }

        [Serializable]
        private class ShaderRegistryEntryDto
        {
            public string name;
            public string mode;
            public string sourceBundleFile;
            public string embeddedFile;
        }
#pragma warning restore CS0649
    }
}
