using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Emits one in-memory MonoBehaviour stand-in type per .cs file in
    // config/FiresAdminPrefabs/Monos/, via Reflection.Emit, so a bundle prefab's
    // captured script types resolve even when the mod that defined them is gone.
    //
    // The parser is deliberately restricted rather than a real C# compiler: it reads
    // an optional namespace, a `class X : MonoBehaviour`, and public (or
    // [SerializeField] private) fields of the primitive, Unity-struct and
    // Unity-object types listed in TypeMap, plus arrays and Lists of those. That is
    // exactly what SteamyDumps' MonoStubWriter emits. Everything else — methods,
    // properties, nested types, enums, generics — is silently skipped, so a stub file
    // can keep its method bodies as documentation.
    //
    // LoadAll runs at plugin Awake, before the bundle loader, so the emitted types are
    // in the AppDomain by the time prefabs resolve their scripts. From there the
    // existing AddComponent + JsonUtility.FromJsonOverwrite path works unchanged.
    public static class VARuntimeMonoStub
    {
        public static string MonosDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "Monos");

        // The single dynamic assembly we emit all stubs into. Built
        // once per session at LoadAll() time, then queried via the
        // standard AppDomain reflection lookup for the rest of the run.
        private static AssemblyBuilder _assemblyBuilder;
        private static ModuleBuilder _moduleBuilder;
        private static readonly Dictionary<string, Type> _emittedByFullName =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        // Idempotent — re-calling is a no-op if we already loaded once.
        // The dynamic assembly survives for the AppDomain lifetime so
        // there's no reason to rebuild within a session.
        private static bool _loaded;

        public static void LoadAll()
        {
            if (_loaded) return;
            _loaded = true;

            if (!Directory.Exists(MonosDir))
            {
                // Common case for clean installs — no Monos folder
                // yet, nothing to load. Drop a README so the user
                // knows what the folder is for once it gets created.
                return;
            }

            string[] files;
            try { files = Directory.GetFiles(MonosDir, "*.cs"); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VARuntimeMonoStub — failed to enumerate Monos/: {ex.Message}");
                return;
            }
            if (files.Length == 0) return;

            EnsureAssemblyBuilder();

            int emitted = 0;
            int skipped = 0;
            foreach (var file in files)
            {
                StubSchema schema;
                try { schema = ParseStubFile(file); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VARuntimeMonoStub — parse failed for '{Path.GetFileName(file)}': {ex.Message}");
                    skipped++;
                    continue;
                }
                if (schema == null || string.IsNullOrEmpty(schema.FullName))
                {
                    skipped++;
                    continue;
                }
                if (_emittedByFullName.ContainsKey(schema.FullName))
                {
                    // Duplicate FullName across two files — the first
                    // wins; subsequent files with the same FQN are
                    // silently dropped. User can resolve by renaming
                    // one of the conflicting files.
                    skipped++;
                    continue;
                }

                Type t;
                try { t = EmitType(schema); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VARuntimeMonoStub — emit failed for '{schema.FullName}' (file '{Path.GetFileName(file)}'): {ex.Message}");
                    skipped++;
                    continue;
                }
                if (t == null)
                {
                    skipped++;
                    continue;
                }
                _emittedByFullName[schema.FullName] = t;
                emitted++;
            }

            Debug.Log($"{VaPieces.PluginName}: VARuntimeMonoStub loaded {emitted} mono stub(s) from {files.Length} .cs file(s); {skipped} skipped.");
        }

        // Public lookup — used by VAExternalBundleLoader's
        // ApplyScriptAttachments when it can't find the original host
        // type via AccessTools.TypeByName. Falls back to our emitted
        // stub by the captured FullName.
        public static bool TryGetStub(string fullName, out Type type)
        {
            type = null;
            if (string.IsNullOrEmpty(fullName)) return false;
            return _emittedByFullName.TryGetValue(fullName, out type);
        }

        // ─────────────────────────────────────────────────────────────
        //  Parser — restricted subset
        // ─────────────────────────────────────────────────────────────

        // Returned by the parser. FullName drives the AppDomain lookup
        // back-pointer from captured scriptAttachments. Fields are in
        // declaration order; the emitter writes them in the same order
        // so JsonUtility round-trips correctly.
        internal class StubSchema
        {
            public string Namespace;
            public string ClassName;
            public string FullName;
            public List<FieldSchema> Fields = new List<FieldSchema>();
        }

        internal class FieldSchema
        {
            public string Name;
            public Type Type;
            public bool IsPublic;        // true for `public T name;`, false for `[SerializeField] private T name;`
            public bool SerializeField;  // [SerializeField] attribute applied (drives MonoBehaviour serialization for private fields)
        }

        // Parses a stub file into a schema. Tolerates noise (method
        // bodies, using directives, attribute lines, comments, blank
        // lines). Returns null on critical failures (no class found).
        internal static StubSchema ParseStubFile(string filePath)
        {
            string body = File.ReadAllText(filePath);
            body = StripComments(body);

            // Namespace — optional. Take the first match.
            string namespaceName = null;
            var nsMatch = Regex.Match(body, @"namespace\s+([\w\.]+)");
            if (nsMatch.Success) namespaceName = nsMatch.Groups[1].Value;

            // Class declaration. Take the first class line that
            // mentions MonoBehaviour as base — anything else either
            // ISN'T our stub or is a helper class the user added
            // that we ignore. We also accept a class with no explicit
            // base (defaults to MonoBehaviour for our purposes since
            // these files only exist to stand in for MBs).
            var classMatch = Regex.Match(body, @"class\s+(\w+)(?:\s*:\s*[\w\.]+)?");
            if (!classMatch.Success) return null;
            string className = classMatch.Groups[1].Value;

            string fullName = string.IsNullOrEmpty(namespaceName)
                ? className
                : namespaceName + "." + className;

            // Locate the class body — everything between the first
            // `{` after the class declaration and its matching `}`.
            int classBodyStart = body.IndexOf('{', classMatch.Index + classMatch.Length);
            if (classBodyStart < 0) return null;
            int classBodyEnd = FindMatchingBrace(body, classBodyStart);
            if (classBodyEnd < 0) classBodyEnd = body.Length;
            string classBody = body.Substring(classBodyStart + 1, classBodyEnd - classBodyStart - 1);

            // Strip out method bodies inside the class body so field
            // declarations on lines like `public float foo;` aren't
            // confused with calls / locals inside methods. Cheap
            // heuristic: walk through, when we see `(`, scan for the
            // matching `)`, then if the next non-whitespace char is
            // `{`, skip until matching `}`. Anything inside is method
            // body — strip it.
            classBody = StripMethodBodies(classBody);

            // Per-statement parse. We split on `;` then on each line
            // look for the field pattern.
            var schema = new StubSchema
            {
                Namespace = namespaceName,
                ClassName = className,
                FullName = fullName,
            };

            // The parser handles attributes on lines above the field
            // decl by accumulating them until the next field decl
            // arrives. Reset between fields.
            bool pendingSerializeField = false;

            // Split by `;` to get statements. Trim each.
            var statements = classBody.Split(';');
            foreach (var rawStmt in statements)
            {
                string stmt = rawStmt;
                if (string.IsNullOrWhiteSpace(stmt)) continue;

                // Pull off leading attributes — each `[...]` block.
                // Track [SerializeField] specifically; ignore the rest.
                while (true)
                {
                    var attrMatch = Regex.Match(stmt, @"^\s*\[([^\]]+)\]\s*");
                    if (!attrMatch.Success) break;
                    string attrBody = attrMatch.Groups[1].Value.Trim();
                    // Match "SerializeField" or "SerializeField()" or fully-qualified.
                    if (attrBody.Equals("SerializeField", StringComparison.Ordinal)
                        || attrBody.StartsWith("SerializeField", StringComparison.Ordinal)
                        || attrBody.EndsWith(".SerializeField", StringComparison.Ordinal))
                    {
                        pendingSerializeField = true;
                    }
                    stmt = stmt.Substring(attrMatch.Length);
                }
                stmt = stmt.Trim();
                if (stmt.Length == 0) continue;

                // Field decl: <modifier> <type> <name>(<= default>)?
                // Modifier is required to be one of: public, private,
                // protected, internal. Type can be any token sequence
                // ending before the name (which is the last identifier
                // before `=` or end-of-stmt).
                //
                // We use a fairly tight regex to avoid matching things
                // like local-variable decls that survived the method-
                // body strip (shouldn't happen but defensive).
                var fieldMatch = Regex.Match(stmt,
                    @"^\s*(?:(?:public|private|protected|internal)\s+)+(?:static\s+|readonly\s+|const\s+)*(?<type>[\w\.\<\>\[\]\?]+)\s+(?<name>\w+)\s*(?:=\s*[^;]*)?\s*$");
                if (!fieldMatch.Success)
                {
                    pendingSerializeField = false;   // reset, this wasn't a field
                    continue;
                }

                bool isPublic = stmt.StartsWith("public", StringComparison.Ordinal)
                    || Regex.IsMatch(stmt, @"^\s*public\b");
                bool serializeField = pendingSerializeField;
                pendingSerializeField = false;

                // Private fields without [SerializeField] are ignored —
                // they aren't part of the serialized contract Steamy
                // Dumps captures, so re-emitting them risks JSON state
                // mismatch.
                if (!isPublic && !serializeField) continue;

                string typeName = fieldMatch.Groups["type"].Value.Trim();
                string fieldName = fieldMatch.Groups["name"].Value.Trim();

                Type fieldType = ResolveTypeByName(typeName);
                if (fieldType == null)
                {
                    // Unsupported type — silently skip. Stub class
                    // gets one less field but the file still loads.
                    continue;
                }

                schema.Fields.Add(new FieldSchema
                {
                    Name = fieldName,
                    Type = fieldType,
                    IsPublic = isPublic,
                    SerializeField = serializeField,
                });
            }

            return schema;
        }

        // ─────────────────────────────────────────────────────────────
        //  Type emission
        // ─────────────────────────────────────────────────────────────

        private static void EnsureAssemblyBuilder()
        {
            if (_assemblyBuilder != null) return;
            var asmName = new AssemblyName("VerdantsAscent.GeneratedMonoStubs");
            _assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
                asmName, AssemblyBuilderAccess.Run);
            _moduleBuilder = _assemblyBuilder.DefineDynamicModule(asmName.Name);
        }

        private static Type EmitType(StubSchema schema)
        {
            // Use the original FullName so AccessTools.TypeByName /
            // Type.GetType resolve the stub via the same key the
            // captured scriptAttachments record. The assembly bit of
            // the qualified name will differ (our generated assembly
            // vs the original mod's), but FullName matching is what
            // FAP's lookup actually uses.
            var tb = _moduleBuilder.DefineType(
                schema.FullName,
                TypeAttributes.Public | TypeAttributes.Class,
                parent: typeof(MonoBehaviour));

            // SerializeField attribute constructor — cached for
            // efficiency since most stubs have ≥1 private serialized
            // field.
            var serializeFieldAttrCtor = typeof(SerializeField).GetConstructor(Type.EmptyTypes);
            CustomAttributeBuilder serializeAttrBuilder = serializeFieldAttrCtor != null
                ? new CustomAttributeBuilder(serializeFieldAttrCtor, new object[0])
                : null;

            foreach (var f in schema.Fields)
            {
                var attrs = f.IsPublic
                    ? FieldAttributes.Public
                    : FieldAttributes.Private;
                var fb = tb.DefineField(f.Name, f.Type, attrs);

                // Apply [SerializeField] to private serialized fields
                // so Unity's serializer / JsonUtility includes them.
                if (!f.IsPublic && f.SerializeField && serializeAttrBuilder != null)
                {
                    fb.SetCustomAttribute(serializeAttrBuilder);
                }
            }

            return tb.CreateType();
        }

        // ─────────────────────────────────────────────────────────────
        //  Type name → System.Type
        // ─────────────────────────────────────────────────────────────

        // Maps the restricted-parser type names to .NET Type objects.
        // Supports primitives, common Unity structs / object refs,
        // arrays (T[]), and List<T>. Unknown types return null so the
        // parser skips the field instead of crashing.
        private static readonly Dictionary<string, Type> _primitiveMap = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { "int", typeof(int) },
            { "float", typeof(float) },
            { "double", typeof(double) },
            { "bool", typeof(bool) },
            { "string", typeof(string) },
            { "byte", typeof(byte) },
            { "short", typeof(short) },
            { "long", typeof(long) },
            { "uint", typeof(uint) },
            { "ushort", typeof(ushort) },
            { "ulong", typeof(ulong) },
            { "char", typeof(char) },
            { "Vector2", typeof(Vector2) },
            { "Vector3", typeof(Vector3) },
            { "Vector4", typeof(Vector4) },
            { "Color", typeof(Color) },
            { "Color32", typeof(Color32) },
            { "Quaternion", typeof(Quaternion) },
            { "Rect", typeof(Rect) },
            { "GameObject", typeof(GameObject) },
            { "Transform", typeof(Transform) },
            { "Material", typeof(Material) },
            { "Texture", typeof(Texture) },
            { "Texture2D", typeof(Texture2D) },
            { "Sprite", typeof(Sprite) },
            { "AudioClip", typeof(AudioClip) },
            { "Mesh", typeof(Mesh) },
            { "Shader", typeof(Shader) },
        };

        private static Type ResolveTypeByName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            typeName = typeName.Trim();

            // Array T[] — recurse on T.
            if (typeName.EndsWith("[]", StringComparison.Ordinal))
            {
                string inner = typeName.Substring(0, typeName.Length - 2).Trim();
                var innerType = ResolveTypeByName(inner);
                return innerType == null ? null : innerType.MakeArrayType();
            }

            // List<T> — recurse on T.
            if (typeName.StartsWith("List<", StringComparison.Ordinal)
                && typeName.EndsWith(">", StringComparison.Ordinal))
            {
                string inner = typeName.Substring(5, typeName.Length - 6).Trim();
                var innerType = ResolveTypeByName(inner);
                return innerType == null ? null : typeof(List<>).MakeGenericType(innerType);
            }

            // Nullable<T> — not in our restricted set, skip.

            // Direct primitive / unity lookup. Try the short name first.
            if (_primitiveMap.TryGetValue(typeName, out var primitive))
                return primitive;

            // Try the suffix (handles fully-qualified names like
            // UnityEngine.Vector3 → "Vector3" lookup).
            int lastDot = typeName.LastIndexOf('.');
            if (lastDot > 0)
            {
                string shortName = typeName.Substring(lastDot + 1);
                if (_primitiveMap.TryGetValue(shortName, out var qualified))
                    return qualified;
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────
        //  Source preprocessing
        // ─────────────────────────────────────────────────────────────

        // Strip both line comments (//) and block comments (/* */).
        // Done as one pass with a state machine since regex on C# code
        // with mixed comment styles is fragile.
        private static string StripComments(string src)
        {
            if (string.IsNullOrEmpty(src)) return src;
            var sb = new System.Text.StringBuilder(src.Length);
            int i = 0;
            int len = src.Length;
            while (i < len)
            {
                char c = src[i];
                // Line comment.
                if (c == '/' && i + 1 < len && src[i + 1] == '/')
                {
                    while (i < len && src[i] != '\n') i++;
                    continue;
                }
                // Block comment.
                if (c == '/' && i + 1 < len && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < len && !(src[i] == '*' && src[i + 1] == '/')) i++;
                    if (i + 1 < len) i += 2;
                    continue;
                }
                // String literal — preserve verbatim so embedded // or
                // /* inside strings don't accidentally trigger comment
                // stripping. Don't bother with @"..." verbatim strings;
                // they're rare in field decls.
                if (c == '"')
                {
                    sb.Append(c);
                    i++;
                    while (i < len)
                    {
                        char sc = src[i];
                        sb.Append(sc);
                        i++;
                        if (sc == '\\' && i < len) { sb.Append(src[i]); i++; }
                        else if (sc == '"') break;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Walk through the class body and strip method bodies. A
        // method is detected by `<token>(` followed by an `(...)`
        // balanced group, then `{...}` balanced group. We replace
        // the entire balanced `{...}` body (including the braces)
        // with a single `;` so subsequent statement-splitting logic
        // doesn't see field-like patterns inside method bodies.
        private static string StripMethodBodies(string classBody)
        {
            if (string.IsNullOrEmpty(classBody)) return classBody;
            var sb = new System.Text.StringBuilder(classBody.Length);
            int i = 0;
            int len = classBody.Length;
            while (i < len)
            {
                char c = classBody[i];
                if (c == '(')
                {
                    // Append the `(...)` block verbatim — could be a
                    // method parameter list or a property/lambda arg.
                    int parenEnd = FindMatchingParen(classBody, i);
                    if (parenEnd < 0) { sb.Append(classBody, i, len - i); break; }
                    sb.Append(classBody, i, parenEnd - i + 1);
                    i = parenEnd + 1;

                    // Skip whitespace and look for an immediately-
                    // following `{` (= method body) vs `;` / `=>`
                    // (= other constructs). If we see `{`, skip the
                    // matching close brace.
                    int j = i;
                    while (j < len && (classBody[j] == ' ' || classBody[j] == '\t' || classBody[j] == '\r' || classBody[j] == '\n')) j++;
                    if (j < len && classBody[j] == '{')
                    {
                        int bodyEnd = FindMatchingBrace(classBody, j);
                        if (bodyEnd < 0) { i = len; break; }
                        // Replace method body with `;` so the
                        // statement splitter sees a clean boundary.
                        sb.Append(';');
                        i = bodyEnd + 1;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Brace-matching helpers — find the index of the closing brace
        // matching the open brace at `startIndex` (which must point AT
        // the open brace). Returns -1 on unbalanced input.
        private static int FindMatchingBrace(string src, int startIndex)
        {
            if (startIndex < 0 || startIndex >= src.Length || src[startIndex] != '{') return -1;
            int depth = 0;
            for (int i = startIndex; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindMatchingParen(string src, int startIndex)
        {
            if (startIndex < 0 || startIndex >= src.Length || src[startIndex] != '(') return -1;
            int depth = 0;
            for (int i = startIndex; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }
}
