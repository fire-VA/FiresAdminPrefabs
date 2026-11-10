using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Bridge;
using FiresCore.Pieces;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // 1.1.317 (Fire 10-26 via the lead): Infinity Hammer's yaml tools (config/tools/infinity_tools*.yaml) get an
    // "Infinity Tools" row on the 1.0 build menu's tag list. IH injects each tool as a runtime piece (_IH_Tool_<name>,
    // component InfinityHammer.BuildMenuTool) into the EQUIPPED table's available pieces only, so:
    //  (a) the held item's table gets a row listing its tools (FAP's hammer row via VABuildUiCategoryTags, any other
    //      item's table via this file's own source, registered after FAP's so every other mod's source still wins),
    //  (b) a grey row under it names the tools that sit on other items ("+ 3 tools on the hoe"),
    //  (c) with Core's Debug on, "[FAP] IH tools for '<equipment>'" names every yaml tool missing from Show All and why.
    // All IH access is a soft type lookup; without IH nothing is added.
    internal sealed class VAInfinityToolsTab : BuildMenuTabs.ISource
    {
        internal const int ToolsKey = 900, HintKey = 901;   // below VABuildUiCategoryTags.SubKeyBase (1000), above any category
        private const string TabLabel = "Infinity Tools";
        private static readonly VAInfinityToolsTab s_source = new VAInfinityToolsTab();

        private static Type s_toolType, s_managerType;
        private static bool s_looked;
        private static readonly Dictionary<string, string> s_lastReport = new Dictionary<string, string>();

        internal static void Register() => BuildMenuTabs.Register(s_source);

        private static bool Ready()
        {
            if (!s_looked)
            {
                s_looked = true;
                s_toolType = QuietTypes.Find("InfinityHammer.BuildMenuTool");
                s_managerType = QuietTypes.Find("InfinityHammer.ToolManager");
                VaPieces.Log?.LogDebug($"[FAP] Infinity Tools tab: {(s_toolType != null ? "IH BuildMenuTool found" : "IH BuildMenuTool NOT found - no tab")}, ToolManager {(s_managerType != null ? "found" : "NOT found - no hint / diagnostic")}");
            }
            return s_toolType != null;
        }

        private static bool IsTool(Piece piece) => piece != null && s_toolType != null && piece.GetComponent(s_toolType) != null;

        // FAP's tables (hammer, IH Builds page) are VABuildUiCategoryTags'; this source takes any other table holding IH tools.
        public bool Owns(PieceTable table)
        {
            if (table == null || HammerCategoryUI.IsOrganizable(table) || !Ready()) return false;
            foreach (var piece in table.m_availablePieces) if (IsTool(piece)) return true;
            return false;
        }

        public void CollectTabs(PieceTable table, List<BuildMenuTabs.Tab> into) => AddRows(table, into);

        public void Fill(PieceTable table, int key, IList<Piece> into) => FillRow(table, key, into);

        /// <summary>The tools row (+ grey hint row) for this table; called by VABuildUiCategoryTags for FAP's tables too.</summary>
        internal static void AddRows(PieceTable table, List<BuildMenuTabs.Tab> into)
        {
            if (table == null || !Ready()) return;
            int here = 0;
            foreach (var piece in table.m_availablePieces) if (IsTool(piece)) here++;
            string equipment = EquipmentOf(table);
            var others = OtherEquipmentCounts(equipment);
            if (here > 0) into.Add(new BuildMenuTabs.Tab(TabLabel, ToolsKey));
            if (others.Count > 0)
            {
                string hint = "+ " + string.Join(", ", others.Select(kv => $"{kv.Value} tool{(kv.Value == 1 ? "" : "s")} on the {kv.Key}").ToArray());
                into.Add(new BuildMenuTabs.Tab("<color=#8c8c8c>" + hint + "</color>", HintKey));
            }
            Diagnose(table, equipment);
        }

        /// <summary>True when the key is one of this file's rows (FAP's Fill hands those over).</summary>
        internal static bool IsOwnKey(int key) => key == ToolsKey || key == HintKey;

        /// <summary>1.1.326: an IH tool piece; FAP's category rows leave these out (they live in the Infinity Tools row only).</summary>
        internal static bool IsToolPiece(Piece piece) => Ready() && IsTool(piece);

        // 1.1.326 (a player via Fire: tools mixed in with item stands, "i added a new one and cant find it"): the row lists the tools
        // in the yaml's order (ToolManager.Tools[equipment]); a tool with "index: N" is moved to position N, as IH places it.
        // Tools IH injected that the yaml list doesn't name come last. Nothing is dropped.
        internal static void FillRow(PieceTable table, int key, IList<Piece> into)
        {
            if (table == null || key != ToolsKey || !Ready()) return;   // the hint row is a label: it opens empty
            var byName = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            var injected = new List<Piece>();
            foreach (var piece in table.m_availablePieces)
            {
                if (!IsTool(piece)) continue;
                injected.Add(piece);
                string n = piece.name.StartsWith("_IH_Tool_") ? piece.name.Substring("_IH_Tool_".Length) : piece.name;
                if (!byName.ContainsKey(n)) byName[n] = piece;
            }
            var ordered = new List<Piece>();
            var indexed = new List<KeyValuePair<int, Piece>>();
            var used = new HashSet<Piece>();
            var all = AllTools();
            string equipment = EquipmentOf(table);
            if (all != null && all.Contains(equipment))
                foreach (var tool in (all[equipment] as IEnumerable) ?? new object[0])
                {
                    string name = Member(tool, "Name") as string;
                    if (name == null || !byName.TryGetValue(name, out var piece) || !used.Add(piece)) continue;
                    if (Member(tool, "Index") is int index && index >= 0) indexed.Add(new KeyValuePair<int, Piece>(index, piece));
                    else ordered.Add(piece);
                }
            foreach (var kv in indexed) ordered.Insert(Mathf.Clamp(kv.Key, 0, ordered.Count), kv.Value);
            int extra = 0;
            foreach (var piece in injected) if (used.Add(piece)) { ordered.Add(piece); extra++; }
            foreach (var piece in ordered) into.Add(piece);
            string line = $"[FAP] Infinity Tools row '{equipment}': {ordered.Count} tool(s) in yaml order, {indexed.Count} placed by index, {extra} not in the yaml list (last)";
            if (!s_lastReport.TryGetValue("row:" + equipment, out var last) || last != line) { s_lastReport["row:" + equipment] = line; VaPieces.Log?.LogDebug(line); }
        }

        // IH's PieceTableToEquipment: "_HammerPieceTable" -> "hammer".
        private static string EquipmentOf(PieceTable table)
        {
            string name = table.name ?? "";
            if (name.StartsWith("_")) name = name.Substring(1);
            if (name.EndsWith("PieceTable")) name = name.Substring(0, name.Length - "PieceTable".Length);
            return name.ToLowerInvariant();
        }

        // ToolManager.Tools: equipment -> List<Tool>, read softly.
        private static IDictionary AllTools()
        {
            if (s_managerType == null) return null;
            try { return Traverse.Create(s_managerType).Property("Tools").GetValue() as IDictionary ?? Traverse.Create(s_managerType).Field("Tools").GetValue() as IDictionary; }
            catch { return null; }
        }

        private static List<KeyValuePair<string, int>> OtherEquipmentCounts(string equipment)
        {
            var result = new List<KeyValuePair<string, int>>();
            var all = AllTools();
            if (all == null) return result;
            foreach (DictionaryEntry e in all)
            {
                string key = e.Key as string;
                if (string.IsNullOrEmpty(key) || string.Equals(key, equipment, StringComparison.OrdinalIgnoreCase)) continue;
                int n = (e.Value as ICollection)?.Count ?? 0;
                if (n > 0) result.Add(new KeyValuePair<string, int>(key, n));
            }
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
            return result;
        }

        private static object Member(object obj, string name)
        {
            if (obj == null) return null;
            var t = Traverse.Create(obj);
            try { var p = t.Property(name); if (p.PropertyExists()) return p.GetValue(); } catch { }
            try { var f = t.Field(name); if (f.FieldExists()) return f.GetValue(); } catch { }
            return null;
        }

        // (c) Debug only: every yaml tool for the held equipment that is not in Show All (m_availablePieces), with the reason.
        // Logged when the result changes, so a reload or permission change shows up once.
        private static void Diagnose(PieceTable table, string equipment)
        {
            var log = VaPieces.Log;
            if (log == null) return;
            var all = AllTools();
            if (all == null || !all.Contains(equipment)) return;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var piece in table.m_availablePieces)
                if (IsTool(piece)) present.Add(piece.name.StartsWith("_IH_Tool_") ? piece.name.Substring("_IH_Tool_".Length) : piece.name);
            int tabCount = PieceTableAccess.ByCategory(table)?.Count ?? 0;
            var missing = new List<string>();
            int total = 0;
            foreach (var tool in (all[equipment] as IEnumerable) ?? new object[0])
            {
                total++;
                string name = Member(tool, "Name") as string ?? "?";
                if (present.Contains(name)) continue;
                string reason;
                object ti = Member(tool, "TabIndex");
                if (ti is int tab && (tab < 0 || (tabCount > 0 && tab >= tabCount))) reason = $"tab index {tab} out of range (0-{tabCount - 1})";
                else reason = "not injected by IH (command not allowed for you, or ToolsEnabled off)";
                missing.Add($"{name} ({reason})");
            }
            string line = $"[FAP] IH tools for '{equipment}': {total} in IH, {present.Count} in Show All; missing: {(missing.Count == 0 ? "none" : string.Join("; ", missing.ToArray()))}";
            string key = table.name ?? equipment;
            if (s_lastReport.TryGetValue(key, out var last) && last == line) return;
            s_lastReport[key] = line;
            log.LogDebug(line);
        }
    }
}
