using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VerdantsAscent.Utilities
{
    // Fail-loud check on every EWP yaml FAP writes.
    //
    // EWP deserializes strictly and then RETRIES the same text with
    // IgnoreUnmatchedProperties (its Service\Yaml.cs Deserialize<T>), so a misspelled
    // field never stops it — the rule just silently does nothing. That is how `filter:`
    // sat in the universal persist rule through two rounds of fixes without the rule
    // ever filtering once.
    //
    // So this runs EWP's own strict pass, against EWP's own types pulled out of the
    // loaded assembly rather than a field-name list kept here. A list would drift: the
    // installed EWP decides what is valid, and fields differ by version — 1.60's `log`
    // does not exist in 1.52. Nothing to maintain, it is right for whatever the admin
    // has installed, and a wrong field TYPE is caught as well as an unknown key.
    //
    // Silent when EWP is absent, which is also when the files are not read by anything.
    public static class VAEwpYamlValidator
    {
        public enum FileKind { Rules, Data }

        private const string RuleTypeName = "ExpandWorld.Prefab.Data";
        private const string DataTypeName = "Data.DataData";
        private const string PrefabsAssemblyName = "ExpandWorldPrefabs";
        private const string DataAssemblyName = "ExpandWorldData";

        private static IDeserializer _strict;
        private static readonly Dictionary<FileKind, Type> _entryTypes = new Dictionary<FileKind, Type>();
        private static readonly HashSet<FileKind> _missingTypeLogged = new HashSet<FileKind>();

        public static void Validate(string path, FileKind kind)
        {
            if (string.IsNullOrEmpty(path)) return;

            string name = Path.GetFileName(path);
            string text;
            Type entryType;
            try
            {
                if (!File.Exists(path)) return;
                entryType = ResolveEntryType(kind);
                if (entryType == null) return;
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEwpYamlValidator could not read {name} to check it: {ex.Message}");
                return;
            }

            try
            {
                Deserializer().Deserialize(new StringReader(text), typeof(List<>).MakeGenericType(entryType));
            }
            catch (YamlException ex)
            {
                // The message EWP would have written to its own log and then swallowed.
                Debug.LogError(
                    $"{VaPieces.PluginName}: VAEwpYamlValidator — {name} line {ex.Start.Line} is not valid to " +
                    $"ExpandWorldPrefabs and it will be IGNORED SILENTLY: {ex.Message}" +
                    $"{DescribeLine(text, ex.Start.Line)}  File: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAEwpYamlValidator could not check {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Quoted so the error names the actual text, not just a line number — the file is
        // generated, so the reader did not write that line and cannot picture it.
        private static string DescribeLine(string text, long line)
        {
            if (line <= 0) return string.Empty;
            var lines = text.Split('\n');
            if (line > lines.Length) return string.Empty;
            return $"  ->  \"{lines[(int)line - 1].TrimEnd('\r')}\"";
        }

        // Matches EWP's own deserializer exactly: camelCase, no IgnoreUnmatchedProperties.
        private static IDeserializer Deserializer()
        {
            return _strict ?? (_strict = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build());
        }

        private static Type ResolveEntryType(FileKind kind)
        {
            if (_entryTypes.TryGetValue(kind, out var cached)) return cached;

            Type resolved = kind == FileKind.Rules
                ? FindType(RuleTypeName, PrefabsAssemblyName)
                : FindType(DataTypeName, DataAssemblyName) ?? FindType(DataTypeName, PrefabsAssemblyName);

            _entryTypes[kind] = resolved;
            if (resolved == null && _missingTypeLogged.Add(kind))
            {
                Debug.Log(
                    $"{VaPieces.PluginName}: VAEwpYamlValidator found no {(kind == FileKind.Rules ? RuleTypeName : DataTypeName)} " +
                    $"in a loaded assembly, so generated {kind.ToString().ToLowerInvariant()} yaml is not checked. " +
                    $"Expected when Expand World is not installed — nothing reads those files either.");
            }
            return resolved;
        }

        private static Type FindType(string typeName, string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetName().Name != assemblyName) continue;
                    var type = assembly.GetType(typeName, false);
                    if (type != null) return type;
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}
