// Loads PlanBuild .blueprint and BuildShare .vbuild files in the same format
// InfinityHammer uses, so blueprints authored in either round-trip into FAP's hammer
// tab. Ported from VABackpacks' BlueprintLoader; the one behavioural difference is
// that EnumerateRoots also probes FAP's own HammerTabs/Blueprints/ drop folder.
//
// Schema reference: InfinityHammer/HammerBlueprintCommand.cs (GetPlanBuild*,
// GetBuildShare*).
//
// Format (.blueprint):
//   #name:<name>
//   #description:<text>
//   #center:<prefabName>
//   #coordinates:x,z,y          (XZY order, hammer convention)
//   #rotation:x,z,y
//   #height:fnp;fnr;dist        (terrain section header)
//     ...rows of semicolon-separated heights...
//   #paint
//     ...rows of "r:g:b:a;r:g:b:a;..." paint cells...
//   #snappoints
//     x;y;z   per row
//   #pieces                     (default mode at file start)
//     name;id;x;y;z;qx;qy;qz;qw;extraInfo;sx;sy;sz;data;chance
//
// Per piece line: position 2-4, rotation 5-8 (xyzw), scale 10-12 (default 1), data
// blob 13 (base64 ZPackage), chance 14 (default 1). Field 9 is "extra info", used by
// some mods instead of the data blob.
//
// Format (.vbuild):  name qx qy qz qw px py pz [data] [chance]   (space-separated)
//
// Numbers parse in invariant culture; a row containing commas has them swapped to
// dots first, for files written before the en_US default.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class VABlueprintLoader
    {
        private const int SourceHashReadBufferSizeBytes = 64 * 1024;
        private const int SourceHashFileStreamBufferSizeBytes = 4096;
        private const string LowercaseHexDigits = "0123456789abcdef";

        // ── Data classes (match InfinityHammer.Blueprint /
        //    InfinityHammer.BlueprintObject so the field shape is the
        //    same and the centering logic ports cleanly). ──────────────

        public class Piece
        {
            public string Prefab = "";
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.identity;
            public Vector3 Scale = Vector3.one;
            public string ExtraInfo = "";
            public string Data = "";
            public float Chance = 1f;
        }

        public class Blueprint
        {
            public string Name = "";
            public string Description = "";
            public string Category = "";
            public string CenterPiece = "";
            public Vector3 Coordinates;
            public Vector3 Rotation;
            public List<Piece> Objects = new List<Piece>();
            public List<Vector3> SnapPoints = new List<Vector3>();
            public string SourcePath = "";
            public float Radius;

            // Re-center every object and snap point so the blueprint's
            // origin is either the named center piece (rotated so its
            // forward becomes +Z) or, lacking that, the bounding-box
            // center with Y at the lowest object. Mirrors
            // InfinityHammer.Blueprint.Center().
            public Vector3 Center(string centerPieceOverride)
            {
                if (!string.IsNullOrEmpty(centerPieceOverride))
                    CenterPiece = centerPieceOverride;

                if (Objects.Count == 0) return Vector3.zero;

                var bounds = new Bounds(Objects[0].Pos, Vector3.zero);
                float minY = float.MaxValue;
                Quaternion rot = Quaternion.identity;
                foreach (var p in Objects)
                {
                    minY = Mathf.Min(minY, p.Pos.y);
                    bounds.Encapsulate(p.Pos);
                }
                Vector3 center = new Vector3(bounds.center.x, minY, bounds.center.z);

                if (!string.IsNullOrEmpty(CenterPiece))
                {
                    foreach (var p in Objects)
                    {
                        if (p.Prefab == CenterPiece)
                        {
                            center = p.Pos;
                            rot = Quaternion.Inverse(p.Rot);
                            break;
                        }
                    }
                }

                Radius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x
                                    + bounds.extents.z * bounds.extents.z);

                foreach (var p in Objects) p.Pos -= center;
                for (int i = 0; i < SnapPoints.Count; i++) SnapPoints[i] -= center;

                if (rot != Quaternion.identity)
                {
                    foreach (var p in Objects)
                    {
                        p.Pos = rot * p.Pos;
                        p.Rot = rot * p.Rot;
                    }
                    for (int i = 0; i < SnapPoints.Count; i++)
                        SnapPoints[i] = rot * SnapPoints[i];
                }
                return center;
            }
        }

        // ── File lookup ───────────────────────────────────────────────

        public static Blueprint LoadByName(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return null;
            if (blueprintName.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase))
                blueprintName = blueprintName.Substring(0, blueprintName.Length - ".blueprint".Length);
            else if (blueprintName.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase))
                blueprintName = blueprintName.Substring(0, blueprintName.Length - ".vbuild".Length);

            foreach (var path in EnumerateCandidatePaths(blueprintName))
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var bp = ParseFile(path);
                    if (string.IsNullOrEmpty(bp.Name)) bp.Name = blueprintName;
                    bp.SourcePath = path;
                    return bp;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABlueprintLoader] Failed to parse blueprint at {path}: {ex.Message}");
                }
            }
            return null;
        }

        // The hammer's Blueprints tab is curated by the FAP drop folder
        // ONLY — BepInEx/config/FiresAdminPrefabs/HammerTabs/Blueprints/.
        // The user copies the .blueprint/.vbuild files they want on the
        // hammer into there (per that folder's README); PlanBuild is
        // deliberately NOT auto-scanned, so the tab shows the curated set
        // instead of every blueprint on disk. LoadByName still reaches
        // PlanBuild for name-based lookups via EnumerateCandidatePaths.
        public static IEnumerable<string> EnumerateAllBlueprintFiles()
        {
            string fapRoot = VABlueprintHammerIntegration.BlueprintsDir;
            if (!Directory.Exists(fapRoot)) yield break;
            string[] files;
            try { files = Directory.GetFiles(fapRoot); }
            catch { yield break; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".blueprint" || ext == ".vbuild") yield return f;
            }
        }

        private sealed class SourceHashEntry
        {
            private readonly long _lastWriteUtcTicks;
            private readonly long _lengthBytes;
            public readonly string Hash;

            public SourceHashEntry(long lastWriteUtcTicks, long lengthBytes, string hash)
            {
                _lastWriteUtcTicks = lastWriteUtcTicks;
                _lengthBytes = lengthBytes;
                Hash = hash;
            }

            public bool MatchesFile(long lastWriteUtcTicks, long lengthBytes) =>
                _lastWriteUtcTicks == lastWriteUtcTicks && _lengthBytes == lengthBytes;
        }

        private static readonly ConcurrentDictionary<string, SourceHashEntry> _sourceHashByPath =
            new ConcurrentDictionary<string, SourceHashEntry>(StringComparer.OrdinalIgnoreCase);

        [ThreadStatic] private static byte[] _sourceHashReadBuffer;

        // Hex SHA1 of a blueprint file, or null when it is missing or unreadable. Bake caches store it
        // to recognise when the blueprint they were baked from has changed, so a file whose write time
        // and length are unchanged returns the hash it was read for last time instead of being re-read.
        public static string TryComputeSourceHash(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            long lastWriteUtcTicks, lengthBytes;
            try
            {
                var file = new FileInfo(path);
                lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
                lengthBytes = file.Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            if (_sourceHashByPath.TryGetValue(path, out var known) && known.MatchesFile(lastWriteUtcTicks, lengthBytes))
                return known.Hash;

            string hash = ReadSourceHash(path);
            if (hash != null) _sourceHashByPath[path] = new SourceHashEntry(lastWriteUtcTicks, lengthBytes, hash);
            return hash;
        }

        private static string ReadSourceHash(string path)
        {
            try
            {
                var readBuffer = _sourceHashReadBuffer ?? (_sourceHashReadBuffer = new byte[SourceHashReadBufferSizeBytes]);
                using (var sha1 = SHA1.Create())
                using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                       SourceHashFileStreamBufferSizeBytes, FileOptions.SequentialScan))
                {
                    int readBytes;
                    while ((readBytes = fileStream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                        sha1.TransformBlock(readBuffer, 0, readBytes, null, 0);
                    sha1.TransformFinalBlock(readBuffer, 0, 0);
                    return ToLowercaseHex(sha1.Hash);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string ToLowercaseHex(byte[] hash)
        {
            var hex = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                hex[i * 2] = LowercaseHexDigits[hash[i] >> 4];
                hex[i * 2 + 1] = LowercaseHexDigits[hash[i] & 0xF];
            }
            return new string(hex);
        }

        private static IEnumerable<string> EnumerateCandidatePaths(string blueprintName)
        {
            foreach (var ext in new[] { ".blueprint", ".vbuild" })
            {
                string fileName = blueprintName + ext;
                foreach (var root in EnumerateRoots())
                {
                    yield return Path.Combine(root, "blueprints", fileName);
                    yield return Path.Combine(root, "server", fileName);
                    yield return Path.Combine(root, fileName);
                }
            }
        }

        private static IEnumerable<string> EnumerateRoots()
        {
            string fapRoot = VABlueprintHammerIntegration.BlueprintsDir;
            if (Directory.Exists(fapRoot)) yield return fapRoot;

            // PlanBuild (process's own BepInEx config).
            string here = Path.Combine(BepInEx.Paths.ConfigPath, "PlanBuild");
            if (Directory.Exists(here)) yield return here;

            // Steam-installed Valheim PlanBuild config (cross-process).
            string steamCommon = GuessSteamCommonRoot();
            string ded = Path.Combine(steamCommon, "Valheim dedicated server", "BepInEx", "config", "PlanBuild");
            string local = Path.Combine(steamCommon, "Valheim", "BepInEx", "config", "PlanBuild");
            if (Directory.Exists(ded)) yield return ded;
            if (Directory.Exists(local)) yield return local;
        }

        private static string GuessSteamCommonRoot()
        {
            try
            {
                string here = AppDomain.CurrentDomain.BaseDirectory;
                int idx = here.IndexOf(Path.DirectorySeparatorChar + "common" + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                    return here.Substring(0, idx + ("\\common\\").Length);
            }
            catch { }
            return @"C:\Program Files (x86)\Steam\steamapps\common\";
        }

        // ── Top-level dispatch ────────────────────────────────────────

        public static Blueprint ParseFile(string path)
        {
            string[] rows = File.ReadAllLines(path);
            string ext = Path.GetExtension(path);
            var bp = new Blueprint
            {
                Name = Path.GetFileNameWithoutExtension(path).Replace(' ', '_'),
                SourcePath = path,
            };
            switch (ext.ToLowerInvariant())
            {
                case ".blueprint": return ParsePlanBuild(bp, rows);
                case ".vbuild":    return ParseBuildShare(bp, rows);
                default: throw new InvalidOperationException($"Unknown blueprint extension: {ext}");
            }
        }

        // ── PlanBuild .blueprint state-machine parser ────────────────

        private enum Section { Pieces, TerrainHeight, TerrainPaint, SnapPoints }

        private static Blueprint ParsePlanBuild(Blueprint bp, string[] rows)
        {
            Section section = Section.Pieces;

            foreach (var raw in rows)
            {
                if (raw == null) continue;
                var row = raw;
                if (row.Length == 0) continue;

                if (row.StartsWith("#", StringComparison.Ordinal))
                {
                    // Intentionally IGNORE the in-file `#name:` directive
                    // and stick with the filename-derived Name set by the
                    // caller (Path.GetFileNameWithoutExtension(path)).
                    // Per user direction: blueprint display name is the
                    // filename, period. PlanBuild's "Save Selection" UI
                    // auto-generates `#name: Multiple` (etc.) when the
                    // user doesn't enter a name, which produced confusing
                    // tooltips like "Multiple (15)" instead of the file's
                    // actual descriptive name. The filename IS the source
                    // of truth for piece-tab labels.
                    /* #name intentionally NOT parsed — filename owns it */
                    if (HeaderEquals(row, "#description:", out var desc))          bp.Description = desc;
                    else if (HeaderEquals(row, "#category:", out var cat))         bp.Category = cat;
                    else if (HeaderEquals(row, "#center:", out var c))             bp.CenterPiece = c;
                    else if (HeaderEquals(row, "#coordinates:", out var coord))    bp.Coordinates = ParseVectorXZY(coord);
                    else if (HeaderEquals(row, "#rotation:", out var rotS))        bp.Rotation = ParseVectorXZY(rotS);
                    else if (row.StartsWith("#height:", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainHeight;
                    else if (row.StartsWith("#paint", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainPaint;
                    else if (row.StartsWith("#snappoints", StringComparison.OrdinalIgnoreCase))
                        section = Section.SnapPoints;
                    else if (row.StartsWith("#terrain", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainHeight;
                    else if (row.StartsWith("#pieces", StringComparison.OrdinalIgnoreCase))
                        section = Section.Pieces;
                    continue;
                }

                switch (section)
                {
                    case Section.Pieces:
                        var piece = ParsePlanBuildPiece(row);
                        if (piece != null) bp.Objects.Add(piece);
                        break;
                    case Section.SnapPoints:
                        bp.SnapPoints.Add(ParsePlanBuildSnapPoint(row));
                        break;
                    case Section.TerrainHeight:
                    case Section.TerrainPaint:
                        break;
                }
            }
            return bp;
        }

        private static bool HeaderEquals(string row, string prefix, out string payload)
        {
            if (row.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int colon = row.IndexOf(':');
                payload = colon >= 0 && colon + 1 < row.Length
                    ? row.Substring(colon + 1).Trim()
                    : string.Empty;
                return true;
            }
            payload = null;
            return false;
        }

        private static Piece ParsePlanBuildPiece(string row)
        {
            if (row.IndexOf(',') > -1) row = row.Replace(',', '.');
            string[] cols = row.Split(';');
            if (cols.Length < 9) return null;

            string name = cols[0];
            if (string.IsNullOrEmpty(name)) return null;

            float x  = InvariantFloat(cols, 2);
            float y  = InvariantFloat(cols, 3);
            float z  = InvariantFloat(cols, 4);
            float qx = InvariantFloat(cols, 5);
            float qy = InvariantFloat(cols, 6);
            float qz = InvariantFloat(cols, 7);
            float qw = InvariantFloat(cols, 8);
            string info = cols.Length > 9 ? cols[9] : "";
            float sx = InvariantFloat(cols, 10, 1f);
            float sy = InvariantFloat(cols, 11, 1f);
            float sz = InvariantFloat(cols, 12, 1f);
            string data = cols.Length > 13 ? cols[13] : "";
            float chance = InvariantFloat(cols, 14, 1f);

            return new Piece
            {
                Prefab    = name,
                Pos       = new Vector3(x, y, z),
                Rot       = new Quaternion(qx, qy, qz, qw),
                Scale     = new Vector3(sx, sy, sz),
                ExtraInfo = info,
                Data      = data,
                Chance    = chance,
            };
        }

        private static Vector3 ParsePlanBuildSnapPoint(string row)
        {
            if (row.IndexOf(',') > -1) row = row.Replace(',', '.');
            string[] cols = row.Split(';');
            return new Vector3(
                InvariantFloat(cols, 0),
                InvariantFloat(cols, 1),
                InvariantFloat(cols, 2));
        }

        // ── BuildShare .vbuild parser ─────────────────────────────────

        private static Blueprint ParseBuildShare(Blueprint bp, string[] rows)
        {
            foreach (var raw in rows)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw.StartsWith("#")) continue;
                var piece = ParseBuildSharePiece(raw);
                if (piece != null) bp.Objects.Add(piece);
            }
            return bp;
        }

        private static Piece ParseBuildSharePiece(string row)
        {
            if (row.IndexOf(',') > -1) row = row.Replace(',', '.');
            string[] cols = row.Split(' ');
            if (cols.Length < 8) return null;
            string name = cols[0];
            if (string.IsNullOrEmpty(name)) return null;

            float qx = InvariantFloat(cols, 1);
            float qy = InvariantFloat(cols, 2);
            float qz = InvariantFloat(cols, 3);
            float qw = InvariantFloat(cols, 4);
            float px = InvariantFloat(cols, 5);
            float py = InvariantFloat(cols, 6);
            float pz = InvariantFloat(cols, 7);
            string data = cols.Length > 8 ? cols[8] : "";
            float chance = InvariantFloat(cols, 9, 1f);

            return new Piece
            {
                Prefab = name,
                Pos    = new Vector3(px, py, pz),
                Rot    = new Quaternion(qx, qy, qz, qw),
                Scale  = Vector3.one,
                Data   = data,
                Chance = chance,
            };
        }

        // ── Numeric utilities ─────────────────────────────────────────

        private static float InvariantFloat(string[] row, int index, float def = 0f)
        {
            if (row == null || index >= row.Length) return def;
            return float.TryParse(row[index]?.Trim(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var v)
                ? v : def;
        }

        private static Vector3 ParseVectorXZY(string s)
        {
            if (string.IsNullOrEmpty(s)) return Vector3.zero;
            string[] parts = s.Split(',', ';', ' ');
            float x = parts.Length > 0 ? ParseInv(parts[0]) : 0f;
            float z = parts.Length > 1 ? ParseInv(parts[1]) : 0f;
            float y = parts.Length > 2 ? ParseInv(parts[2]) : 0f;
            return new Vector3(x, y, z);
        }

        private static float ParseInv(string s)
            => float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }
}
