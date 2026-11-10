using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Api
{
    // 1.1.332 ([visual] for [lead], Fire 10-30 one-implementation rule: "FAP is the only thing that reads and writes blueprints"; the
    // Studio drops its own localStorage store and FGC binds these by reflection). The WRITER is the exact inverse of FAP's own reader,
    // VABlueprintLoader.ParsePlanBuild / ParsePlanBuildPiece (row = prefab;;x;y;z;qx;qy;qz;qw;;sx;sy;sz;data;1, invariant culture,
    // the same column layout FDM's DungeonBlueprint writes), into VABlueprintHammerIntegration.BlueprintsDir; the hammer tab is
    // refreshed through the existing RegisterBlueprintFile / UnregisterBlueprint (FapHammerMenu.Revision then changes by itself).
    // Plain arrays so a reflection caller needs no FAP types. Main thread. Add-only.
    public static class FapBlueprints
    {
        private static readonly Regex NameOk = new Regex(@"^[A-Za-z0-9_ .-]{1,64}$");

        /// <summary>Blueprint names in FAP's folder (.blueprint / .vbuild, without extension), the files FAP's hammer tab loads.</summary>
        public static string[] List()
        {
            var dir = VABlueprintHammerIntegration.BlueprintsDir;
            if (!Directory.Exists(dir)) return new string[0];
            var l = new System.Collections.Generic.List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".blueprint" || ext == ".vbuild") l.Add(Path.GetFileNameWithoutExtension(f));
            }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l.ToArray();
        }

        /// <summary>Writes &lt;name&gt;.blueprint (PlanBuild format, positions relative to the first piece's ground centre as given) and puts it
        /// on the hammer's blueprint tab. prefabs[n]; posRot[n*7] = x,y,z,qx,qy,qz,qw; scale[n*3] or null (1,1,1); data[n] or null (FAP's /
        /// FDM's data column, e.g. base64 ZPackage). Refuses a bad name, a size mismatch, or an existing file unless overwrite.</summary>
        public static bool Save(string name, string description, string[] prefabs, float[] posRot, float[] scale, string[] data, bool overwrite, out string path, out string refused)
        {
            path = null; refused = null;
            if (name == null || !NameOk.IsMatch(name)) { refused = "name must match " + NameOk; return false; }
            int n = prefabs?.Length ?? 0;
            if (n == 0) { refused = "no pieces"; return false; }
            if (posRot == null || posRot.Length != n * 7) { refused = "posRot must hold 7 floats per piece"; return false; }
            if (scale != null && scale.Length != n * 3) { refused = "scale must hold 3 floats per piece (or be null)"; return false; }
            if (data != null && data.Length != n) { refused = "data must hold one entry per piece (or be null)"; return false; }
            for (int i = 0; i < n; i++)
                if (string.IsNullOrEmpty(prefabs[i]) || prefabs[i].IndexOf(';') >= 0 || (data != null && data[i] != null && data[i].IndexOfAny(new[] { ';', '\n', '\r' }) >= 0))
                { refused = "piece " + i + ": empty prefab, or ';' / newline in prefab or data"; return false; }
            var dir = VABlueprintHammerIntegration.BlueprintsDir;
            try { Directory.CreateDirectory(dir); } catch (Exception ex) { refused = "folder: " + ex.Message; return false; }
            path = Path.Combine(dir, name + ".blueprint");
            bool existed = File.Exists(path);
            if (existed && !overwrite) { refused = "exists (pass overwrite)"; return false; }
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("#name:").Append(name).Append('\n');
            sb.Append("#description:").Append((description ?? "").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            sb.Append("#pieces\n");
            for (int i = 0; i < n; i++)
            {
                int k = i * 7;
                float sx = scale != null ? scale[i * 3] : 1f, sy = scale != null ? scale[i * 3 + 1] : 1f, sz = scale != null ? scale[i * 3 + 2] : 1f;
                sb.Append(prefabs[i]).Append(";;")
                  .Append(string.Format(inv, "{0:0.####};{1:0.####};{2:0.####};{3:0.######};{4:0.######};{5:0.######};{6:0.######};;{7:0.###};{8:0.###};{9:0.###};",
                          posRot[k], posRot[k + 1], posRot[k + 2], posRot[k + 3], posRot[k + 4], posRot[k + 5], posRot[k + 6], sx, sy, sz))
                  .Append(data != null ? data[i] ?? "" : "").Append(";1\n");
            }
            try { File.WriteAllText(path, sb.ToString()); } catch (Exception ex) { refused = "write: " + ex.Message; return false; }
            Refresh(path, existed);
            Debug.Log($"[FAP] FapBlueprints.Save: {path} ({n} piece(s){(existed ? ", overwrote" : "")}) (1.1.332)");
            return true;
        }

        /// <summary>Moves &lt;name&gt;.blueprint / .vbuild to the Windows Recycle Bin (never a hard delete) and takes it off the hammer tab.
        /// Refused on a non-Windows host (no Recycle Bin) or when the file is missing.</summary>
        public static bool Delete(string name, out string refused)
        {
            refused = null;
            if (name == null || !NameOk.IsMatch(name)) { refused = "bad name"; return false; }
            var dir = VABlueprintHammerIntegration.BlueprintsDir;
            string path = Path.Combine(dir, name + ".blueprint");
            if (!File.Exists(path)) path = Path.Combine(dir, name + ".vbuild");
            if (!File.Exists(path)) { refused = "no such blueprint"; return false; }
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsServer && Application.platform != RuntimePlatform.WindowsEditor)
            { refused = "no Recycle Bin on this host - delete by hand"; return false; }
            string stub = VABlueprintHammerIntegration.MakeStubName(path);
            if (!RecycleBin(path, out string err)) { refused = "recycle: " + err; return false; }
            VABlueprintHammerIntegration.UnregisterBlueprint(stub, ZNetScene.instance, FapHammerMenu.Table("Hammer"));
            Debug.Log($"[FAP] FapBlueprints.Delete: {path} sent to the Recycle Bin (1.1.332)");
            return true;
        }

        private static void Refresh(string path, bool existed)
        {
            var table = FapHammerMenu.Table("Hammer");
            if (ZNetScene.instance == null || table == null) return;   // not in a world: the next scan picks the file up
            string stub = VABlueprintHammerIntegration.MakeStubName(path);
            if (existed) VABlueprintHammerIntegration.UnregisterBlueprint(stub, ZNetScene.instance, table);
            VABlueprintHammerIntegration.RegisterBlueprintFile(path, ZNetScene.instance, table, out _, out _);
        }

        // SHFileOperation FO_DELETE + FOF_ALLOWUNDO = the Explorer "Delete" (to the Recycle Bin), no UI.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
        private static bool RecycleBin(string path, out string err)
        {
            err = null;
            try
            {
                var op = new SHFILEOPSTRUCT { wFunc = 0x3 /*FO_DELETE*/, pFrom = Path.GetFullPath(path) + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 /*ALLOWUNDO|NOCONFIRMATION|SILENT|NOERRORUI*/ };
                int rc = SHFileOperation(ref op);
                if (rc != 0 || op.fAnyOperationsAborted || File.Exists(path)) { err = "SHFileOperation " + rc; return false; }
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }
    }
}
