using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Resolves a manifest's captured script type back to a live Type at injection
    // time. The old single AccessTools.TypeByName call returned null whenever the
    // source mod's assembly wasn't loaded under the exact captured identity.
    //
    // First hit wins, cached per session:
    //   1. Direct            the exact name, first in assembly load order — the source mod is
    //                        still installed as-is, or FAP's own ExternalScripts copy
    //   2. Alias             user-maintained redirect JSON in FAP/Manifests, for
    //                        renames the manifests don't know about
    //   3. AssemblyQualified uses the manifest's assemblyName hint, for short names
    //                        that several assemblies define
    //   4. ShortName         namespace moved but the class name held: scans loaded
    //                        assemblies for a matching MonoBehaviour, first match wins
    //   5. Stub              nothing resolved — returns VAUnresolvedScriptStub so the
    //                        slot still gets a component, which the caller then fills
    //                        with the manifest's diagnostics
    //
    // This only answers "which Type?" — attaching it and restoring field state is the
    // caller's job.
    public static class VAScriptTypeResolver
    {
        public enum ResolutionMethod
        {
            None,
            Direct,
            Alias,
            AssemblyQualified,
            ShortName,
            MonoStub,   // VARuntimeMonoStub-emitted type from a Monos/*.cs stub
            Stub,
            Cached,
        }

        // Per-session resolution cache. Same type names are queried for
        // many prefabs of the same kind across bundles — memoize so the
        // ShortName scan (which walks every loaded assembly) only runs
        // once per unique name. Keyed by the ORIGINAL typeFullName from
        // the manifest, not the resolved type's name, so cache hits
        // remain stable across resolution-method paths.
        private static readonly Dictionary<string, ResolvedType> _cache =
            new Dictionary<string, ResolvedType>(StringComparer.Ordinal);

        // User-managed redirect file. Lazy-loaded on first Resolve call.
        // Format: { "<oldFullName>": "<newFullName>", ... }. Both fields
        // are typeFullNames; the resolver looks the new name up via the
        // Direct path.
        private static Dictionary<string, string> _aliases;
        private static bool _aliasesLoaded;

        // Diagnostic counters — incremented by Resolve, drained by
        // ApplyScriptAttachments' summary log so the user can see at a
        // glance how many attachments hit each path.
        public static readonly Dictionary<ResolutionMethod, int> MethodCounts =
            new Dictionary<ResolutionMethod, int>();

        public static string AliasesPath =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "Manifests", "script_type_aliases.json");

        // Main entry. Returns the resolved Type + the method that found
        // it. Never returns null — if all strategies miss, returns the
        // VAUnresolvedScriptStub type so callers can always AddComponent
        // something.
        public static ResolvedType Resolve(string typeFullName, string assemblyName)
        {
            if (string.IsNullOrEmpty(typeFullName))
                return new ResolvedType(typeof(VAUnresolvedScriptStub), ResolutionMethod.Stub);

            if (_cache.TryGetValue(typeFullName, out var cached))
            {
                IncMethod(ResolutionMethod.Cached);
                return cached;
            }

            // 1. Direct lookup
            var t = TryDirect(typeFullName);
            if (t != null) return Cache(typeFullName, t, ResolutionMethod.Direct);

            // 2. Alias redirect (user-managed override)
            EnsureAliasesLoaded();
            if (_aliases != null && _aliases.TryGetValue(typeFullName, out var aliasTarget)
                && !string.IsNullOrEmpty(aliasTarget))
            {
                t = TryDirect(aliasTarget);
                if (t != null) return Cache(typeFullName, t, ResolutionMethod.Alias);
            }

            // 3. Assembly-qualified
            if (!string.IsNullOrEmpty(assemblyName))
            {
                t = TryAssemblyQualified(typeFullName, assemblyName);
                if (t != null) return Cache(typeFullName, t, ResolutionMethod.AssemblyQualified);
            }

            // 4. Short-name across all loaded assemblies
            t = TryShortName(typeFullName);
            if (t != null) return Cache(typeFullName, t, ResolutionMethod.ShortName);

            // 5. Runtime-emitted MonoBehaviour stub from a .cs file in
            //    BepInEx/config/FiresAdminPrefabs/Monos/. Steamy Dumps
            //    writes one .cs per captured type so the user doesn't
            //    need the host mod installed to keep prefab field state
            //    alive across sessions. The stub IS a real MonoBehaviour
            //    subclass — AddComponent works, JsonUtility.FromJsonOverwrite
            //    restores the captured field values. Method behaviour
            //    (Update/Awake/etc.) is NOT preserved — the original
            //    host's compiled IL can't be cleanly read back as source.
            if (Utilities.VARuntimeMonoStub.TryGetStub(typeFullName, out var monoStub))
            {
                return Cache(typeFullName, monoStub, ResolutionMethod.MonoStub);
            }

            // 6. Unresolved-stub fallback — guaranteed-attachable type so
            //    the prefab still has a component slot. Caller populates
            //    diagnostic fields from the manifest entry so the user
            //    can see what was supposed to be there.
            return Cache(typeFullName, typeof(VAUnresolvedScriptStub), ResolutionMethod.Stub);
        }

        // ──────────────────────────────────────────────────────────────
        // Strategy implementations
        // ──────────────────────────────────────────────────────────────

        // The first type of this exact name in assembly load order, as AccessTools.TypeByName returned, but through each
        // assembly's own name index. TypeByName fell back to GetTypes() over every assembly loaded before the match, for
        // every name: 294 ms for the rig's 9 attachment types in R21, most of the injection's attachments step ([ghost],
        // Tools\FAP_CLIENT_LOAD_COST.md #7). Runtime-emitted stubs keep their original FullName, so they resolve here too.
        private static Type TryDirect(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = assembly.GetType(fullName, false); }
                catch { continue; }
                if (t != null) return IsValidComponentType(t) ? t : null;
            }
            return null;
        }

        // Type.GetType with assembly-qualified name. Covers the case
        // where multiple loaded assemblies define the same simple name
        // and AccessTools.TypeByName resolved the wrong one (or none).
        private static Type TryAssemblyQualified(string typeFullName, string assemblyName)
        {
            try
            {
                // Strip ".dll" if present — assembly identity is the
                // simple name, not the filename.
                string asmName = assemblyName;
                if (asmName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    asmName = asmName.Substring(0, asmName.Length - 4);

                var aqn = $"{typeFullName}, {asmName}";
                var t = Type.GetType(aqn, throwOnError: false);
                if (t != null && IsValidComponentType(t)) return t;
            }
            catch { }
            return null;
        }

        // Scan every loaded assembly for a MonoBehaviour subclass whose
        // Type.Name (last segment, no namespace) matches. Handles mods
        // that renamed their namespace between versions. First match wins
        // — when multiple matches exist the user disambiguates via an
        // alias entry.
        //
        // Reflection over GetTypes() can throw ReflectionTypeLoadException
        // on assemblies with broken references (common with mod DLLs
        // that have optional deps). We catch + continue with the loaded
        // subset; partial scans are still useful.
        //
        // The scan is done once per session into a short-name index (first valid type per name, in the same load order),
        // instead of once per unresolved name.
        private static Dictionary<string, Type> _shortNameIndex;

        private static Type TryShortName(string typeFullName)
        {
            int lastDot = typeFullName.LastIndexOf('.');
            string shortName = lastDot >= 0
                ? typeFullName.Substring(lastDot + 1)
                : typeFullName;
            if (string.IsNullOrEmpty(shortName)) return null;

            if (_shortNameIndex == null) _shortNameIndex = BuildShortNameIndex();
            return _shortNameIndex.TryGetValue(shortName, out var type) ? type : null;
        }

        private static Dictionary<string, Type> BuildShortNameIndex()
        {
            var index = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types;     // partial — keep going on the loadable ones
                }
                catch { continue; }
                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null || index.ContainsKey(type.Name)) continue;
                    if (!IsValidComponentType(type)) continue;
                    index[type.Name] = type;
                }
            }
            return index;
        }

        // Components must derive from UnityEngine.Component AND be
        // concrete (non-abstract). MonoBehaviour subclasses are the
        // common case but we accept any Component to handle weird
        // mods that ship custom Behaviour subclasses or attach
        // non-MonoBehaviour Components via Add. abstract types and
        // interfaces can't be AddComponent'd so we filter them out.
        private static bool IsValidComponentType(Type t)
        {
            if (t == null) return false;
            if (t.IsAbstract) return false;
            if (t.IsInterface) return false;
            if (!typeof(UnityEngine.Component).IsAssignableFrom(t)) return false;
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        // Aliases (user-managed redirect file)
        // ──────────────────────────────────────────────────────────────

        private static void EnsureAliasesLoaded()
        {
            if (_aliasesLoaded) return;
            _aliasesLoaded = true;

            try
            {
                if (!File.Exists(AliasesPath)) return;
                var json = File.ReadAllText(AliasesPath);
                _aliases = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (_aliases != null && _aliases.Count > 0)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAScriptTypeResolver loaded {_aliases.Count} alias(es) from {AliasesPath}.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAScriptTypeResolver — failed to read {AliasesPath}: {ex.Message}");
                _aliases = null;
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Cache + counter helpers
        // ──────────────────────────────────────────────────────────────

        private static ResolvedType Cache(string key, Type type, ResolutionMethod method)
        {
            var rt = new ResolvedType(type, method);
            _cache[key] = rt;
            IncMethod(method);
            return rt;
        }

        private static void IncMethod(ResolutionMethod m)
        {
            int prev;
            MethodCounts.TryGetValue(m, out prev);
            MethodCounts[m] = prev + 1;
        }

        // Result struct — Type plus how we found it. Callers (e.g.
        // VAExternalBundleLoader.ApplyScriptAttachments) branch on
        // Method == Stub to populate diagnostic fields rather than
        // calling JsonUtility on the original captured state.
        public struct ResolvedType
        {
            public Type Type;
            public ResolutionMethod Method;
            public ResolvedType(Type t, ResolutionMethod m) { Type = t; Method = m; }
            public bool IsStub => Method == ResolutionMethod.Stub;
        }
    }

    // Stub MonoBehaviour bound to prefabs when no real type can be
    // resolved for a manifest's script attachment. Carries the diagnostic
    // metadata (original type name, source assembly, captured JSON state)
    // so debugging tools + the verbose log can still tell what was
    // supposed to be at this slot — and the user can either install the
    // missing mod or add an alias entry to redirect to a replacement type.
    //
    // The stub has no Update/FixedUpdate/etc., so it costs ~nothing at
    // runtime. Its only role is to preserve the prefab's component shape
    // so ZDOs referencing this slot don't fail-fast and so any sibling
    // components that GetComponent the original type at least find
    // something at the right slot index.
    public class VAUnresolvedScriptStub : MonoBehaviour
    {
        // Public fields so JsonUtility can populate them via the
        // FromJsonOverwrite path — though the stub populator
        // (VAExternalBundleLoader.ApplyScriptAttachments) writes them
        // explicitly from the manifest entry to avoid relying on field
        // matching with the original type's JSON.
        public string originalTypeFullName;
        public string originalAssemblyName;
        public string serializedJsonState;
    }
}
