using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Patches
{
    // 1.1.258 (Ulvar 10-14, player view: 33x IndexOutOfRange in Jotunn PieceManager.RefreshCategories at spawn). Jotunn's loop is
    // `for i < buildPieces.m_categories.Count: Hud.instance.m_pieceCategoryTabs[i]`, and Jotunn only grows that tab array to its OWN
    // MaxCategory(). FAP's categories (top-down values since 1.1.251) live in m_categories too, so whenever the table lists more
    // categories than Jotunn made tab objects, Jotunn throws.
    //   probe - one Debug line per distinct (view, counts) state: m_categories.Count, Hud tabs, each category with its owner.
    //   pad   - safety net, ON (a crash fix): before Jotunn runs, Hud.m_pieceCategoryTabs grows with inactive clones of tab 0 to an
    //           upper bound of what RefreshCategories can index (m_categories now + every Jotunn category + vanilla's), logged.
    // Only patches when Jotunn is installed (Prepare). Never edits m_categories.
    [HarmonyPatch]
    internal static class VAJotunnTabGuard
    {
        private static MethodBase s_target;
        private static FieldInfo s_jotunnCategories;   // Jotunn PieceManager.PieceCategories (Dictionary<string, PieceCategory>)
        private static object s_jotunnInstance;
        private static string s_lastProbe;

        private static bool Prepare()
        {
            // 1.1.265 (Fire 10-15: no HarmonyX "Could not find" warnings): quiet lookups. Jotunn is optional (absent on Fire's stack),
            // and the field name differs by Jotunn version, so AccessTools would warn on every start for whichever is missing.
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Jotunn") continue;
                try { type = asm.GetType("Jotunn.Managers.PieceManager", false); } catch { }
                if (type != null) break;
            }
            if (type == null) { try { type = Type.GetType("Jotunn.Managers.PieceManager, Jotunn", false); } catch { } }   // 1.1.269: also a loader lookup
            if (type == null) { VaPieces.Log?.LogInfo("[JotunnTabGuard] Jotunn not installed: nothing to guard"); return false; }
            s_target = type.GetMethod("RefreshCategories", Any);
            if (s_target == null) { VaPieces.Log?.LogDebug($"[JotunnTabGuard] {type.FullName} has no RefreshCategories in this Jotunn: nothing to guard"); return false; }
            s_jotunnCategories = type.GetField("PieceCategories", Any) ?? type.GetField("<PieceCategories>k__BackingField", Any);
            try { s_jotunnInstance = type.GetProperty("Instance", Any)?.GetValue(null); } catch { }
            // 1.1.269: Info, once, so a tester log proves the guard is on
            VaPieces.Log?.LogInfo($"[JotunnTabGuard] patched {type.FullName}.RefreshCategories (Jotunn {type.Assembly.GetName().Version}; categories field {(s_jotunnCategories != null ? "found" : "NOT found")})");
            return true;
        }

        private static MethodBase TargetMethod() => s_target;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            try
            {
                var hud = Hud.instance;
                var table = Player.m_localPlayer != null ? Player.m_localPlayer.m_buildPieces : null;
                if (hud == null || table == null || table.m_categories == null || hud.m_pieceCategoryTabs == null || hud.m_pieceCategoryTabs.Length == 0) return;

                int jotunnCount = 0;
                try { if (s_jotunnCategories != null && s_jotunnInstance != null && s_jotunnCategories.GetValue(s_jotunnInstance) is ICollection c) jotunnCount = c.Count; } catch { }
                int vanillaMax = (int)Piece.PieceCategory.Max;
                // 1.1.269: Jotunn's UpdatePieceTableCategories re-adds every category the table's PIECES use (FAP's own, hidden from the
                // player view, came back), so count the union of m_categories and the pieces' categories, not m_categories alone.
                var cats = new System.Collections.Generic.HashSet<Piece.PieceCategory>(table.m_categories);
                if (table.m_pieces != null) foreach (var go in table.m_pieces) { var pc = go != null ? go.GetComponent<Piece>() : null; if (pc != null) cats.Add(pc.m_category); }
                int need = cats.Count + jotunnCount + vanillaMax;   // what RefreshCategories can index, worst case
                int have = hud.m_pieceCategoryTabs.Length;

                bool player = false;
                try { player = VABuildMenuVisibility.ViewingAsPlayer(); } catch { }
                string probe = $"view {(player ? "PLAYER" : "ADMIN")}, m_categories {table.m_categories.Count}, Hud tabs {have}, Jotunn categories {jotunnCount}, vanilla {vanillaMax}";
                if (probe != s_lastProbe)
                {
                    s_lastProbe = probe;
                    string list = string.Join(",", table.m_categories.Select(cat => $"{(int)cat}:{Owner(cat)}").ToArray());
                    VaPieces.Log?.LogDebug($"[JotunnTabGuard] {probe}; categories [{list}]");
                }

                if (have >= need) return;
                // 1.1.264 (Fire 10-15: fixes are not toggles): always pads; the "Pad Jotunn category tabs" key is gone.
                var template = hud.m_pieceCategoryTabs[0];
                var parent = template.transform.parent;
                var grown = hud.m_pieceCategoryTabs.ToList();
                for (int i = have; i < need; i++)
                {
                    var tab = UnityEngine.Object.Instantiate(template, parent);
                    tab.name = "FAP_JotunnTabPad" + i;
                    tab.SetActive(false);
                    grown.Add(tab);
                }
                hud.m_pieceCategoryTabs = grown.ToArray();
                Debug.Log($"{VaPieces.PluginName}: [JotunnTabGuard] padded {need - have} tab(s) for Jotunn: categories {table.m_categories.Count} (+{jotunnCount} Jotunn, +{vanillaMax} vanilla worst case) vs tabs {have}, view {(player ? "PLAYER" : "ADMIN")}");
            }
            catch (Exception ex)
            {
                VaPieces.Log?.LogDebug($"[JotunnTabGuard] prefix skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 1.1.269: last line of defence. If Jotunn still indexes past the tabs (a category source we did not count), swallow THAT
        // exception (logged once at Info) instead of 289 errors from Player.SetPlaceMode; the next call pads again.
        private static bool s_finalLogged;
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception is IndexOutOfRangeException)
            {
                if (!s_finalLogged)
                {
                    s_finalLogged = true;
                    int tabs = Hud.instance != null && Hud.instance.m_pieceCategoryTabs != null ? Hud.instance.m_pieceCategoryTabs.Length : -1;
                    int cats = Player.m_localPlayer != null && Player.m_localPlayer.m_buildPieces != null ? Player.m_localPlayer.m_buildPieces.m_categories.Count : -1;
                    VaPieces.Log?.LogInfo($"[JotunnTabGuard] Jotunn RefreshCategories indexed past the tabs (categories {cats}, tabs {tabs}); swallowed (logged once)");
                }
                return null;
            }
            return __exception;
        }

        private static string Owner(Piece.PieceCategory cat)
        {
            if ((int)cat < (int)Piece.PieceCategory.Max) return "vanilla";
            try { if (VACategoryRegistry.IsOwnCategory(cat)) return "FAP"; } catch { }
            return "other";
        }
    }
}
