using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.235 (the lead 10-13, TOP PRIORITY: Fire's game crashes, ntdll 0xc0000005 +0x3ed12, while the build menu opens after a
    // FATI-spawned hammer is equipped; no "[FAP] build menu opened" line, so it died inside the open). A crash-proof trace of each
    // step of the open: equip, Hud.TogglePieceSelection, the available-piece rebuild, BuildUi.OpenBuildMenu / UpdatePieceButtons,
    // Hud.UpdatePieceList, lazy realize, icon flush. Each line is written AND flushed before the step runs, so after a hard crash
    // the last line names it. Written into FAT's BepInEx\FiresTests\fat_native_trace.log (FAT's FatNativeTrace.Step, reflected) so
    // FAP's steps interleave with FAT's/EBM's destroy lines; without FAT, fap_native_trace.log in the same folder.
    // Frame-level steps are traced only for 5 s after an equip or a toggle. Config: [Diagnostics] BuildMenuNativeTrace.
    [HarmonyPatch]
    public static class FapNativeTrace
    {
        private static Action<string, int, string> s_fatStep;
        private static bool s_resolved;
        private static readonly object s_lock = new object();
        private static FileStream s_file;
        private static float s_windowUntil;

        internal static bool On => !VaPieces.IsDedicatedServer && (ConfigManager.Instance?.configBuildMenuNativeTrace?.Value ?? true);
        internal static bool InWindow => On && Time.unscaledTime < s_windowUntil;

        internal static void OpenWindow() { s_windowUntil = Time.unscaledTime + 5f; s_buttons = 0; }

        public static void Step(string step, string detail = null)
        {
            if (!On) return;
            try
            {
                Resolve();
                string s = "FAP " + step;
                if (s_fatStep != null) { s_fatStep(s, 0, detail); return; }
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " t" + Thread.CurrentThread.ManagedThreadId + " " + s + (detail != null ? " " + detail : "") + "\n";
                byte[] bytes = Encoding.ASCII.GetBytes(line);
                lock (s_lock)
                {
                    if (s_file == null)
                    {
                        string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                        Directory.CreateDirectory(dir);
                        s_file = new FileStream(Path.Combine(dir, "fap_native_trace.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                    }
                    s_file.Write(bytes, 0, bytes.Length);
                    s_file.Flush(true);
                }
            }
            catch { }
        }

        private static void Resolve()
        {
            if (s_resolved) return;
            s_resolved = true;
            try
            {
                // 1.1.251 (the lead 10-14, JotunnRepro without FAT): only FAT's own assembly is asked. AccessTools.TypeByName scans every
                // loaded assembly, which logged "Could not find type named ...FatNativeTrace" and tripped other mods' GetTypes errors.
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name == "FiresAdminTerrain") { t = asm.GetType("VerdantsAscent.Terrain.Voxel.FatNativeTrace", false); break; }
                VaPieces.Log?.LogDebug($"[FapNativeTrace] FAT trace sink: {(t != null ? "FAT's FatNativeTrace" : "FAT not loaded, FAP's own file")}");
                var m = t != null ? AccessTools.Method(t, "Step", new[] { typeof(string), typeof(int), typeof(string) }) : null;
                if (m != null) s_fatStep = (Action<string, int, string>)Delegate.CreateDelegate(typeof(Action<string, int, string>), m);
            }
            catch { s_fatStep = null; }
        }

        private static string Item(ItemDrop.ItemData item) =>
            item == null ? "null" : $"'{item.m_dropPrefab?.name ?? item.m_shared?.m_name}' buildPieces {(item.m_shared?.m_buildPieces != null ? item.m_shared.m_buildPieces.name : "none")}";

        // ── equip ──
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Equip_Pre(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (__instance != Player.m_localPlayer || item?.m_shared?.m_buildPieces == null) return;
            OpenWindow();
            Step("equip begin", Item(item));
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Equip_Post(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (__instance != Player.m_localPlayer || item?.m_shared?.m_buildPieces == null) return;
            Step("equip end", Item(item));
        }

        // ── the menu toggle ──
        [HarmonyPatch(typeof(Hud), nameof(Hud.TogglePieceSelection)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Toggle_Pre()
        {
            OpenWindow();
            Step("Hud.TogglePieceSelection begin", $"visible {SafeVisible()}");
        }

        [HarmonyPatch(typeof(Hud), nameof(Hud.TogglePieceSelection)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Toggle_Post() => Step("Hud.TogglePieceSelection end", $"visible {SafeVisible()}");

        private static bool SafeVisible() { try { return Hud.IsPieceSelectionVisible(); } catch { return false; } }

        // 1.1.240 (Fire 21:08: "the hammer menu won't even open", yet it logged open): who closes the menu, and how soon after it
        // opened. One Debug line per close with the calling methods (no trace window: closes are rare).
        private static float s_openedAt = -1f;
        private static float s_nextGhostCaller;

        [HarmonyPatch(typeof(Hud), nameof(Hud.TogglePieceSelection)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Toggle_Opened() { if (SafeVisible()) s_openedAt = Time.unscaledTime; }

        [HarmonyPatch(typeof(Hud), nameof(Hud.HidePieceSelection)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Hide_Pre()
        {
            if (!SafeVisible()) return;
            float age = s_openedAt < 0f ? -1f : Time.unscaledTime - s_openedAt;
            string who = "?";
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
                var names = new System.Collections.Generic.List<string>();
                foreach (var f in frames) { var m = f.GetMethod(); if (m != null) names.Add((m.DeclaringType?.Name ?? "?") + "." + m.Name); if (names.Count >= 6) break; }
                who = string.Join(" < ", names.ToArray());
            }
            catch { }
            VaPieces.Log?.LogDebug($"[FAP] build menu closed {(age >= 0f ? age.ToString("0.00") + " s after it opened" : "")} by {who}");
            Step("Hud.HidePieceSelection", $"{age:0.00} s after open; {who}");
        }

        // ── the available-piece rebuild ──
        [HarmonyPatch(typeof(Player), "UpdateAvailablePiecesList"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Avail_Pre() { if (InWindow) Step("Player.UpdateAvailablePiecesList begin"); }

        [HarmonyPatch(typeof(Player), "UpdateAvailablePiecesList"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Avail_Post() { if (InWindow) Step("Player.UpdateAvailablePiecesList end"); }

        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void TableAvail_Pre(PieceTable __instance) { if (InWindow) Step("PieceTable.UpdateAvailable begin", $"'{__instance?.name}' {__instance?.m_pieces?.Count} piece(s)"); }

        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void TableAvail_Post() { if (InWindow) Step("PieceTable.UpdateAvailable end"); }

        // ── the 1.0 build UI ──
        [HarmonyPatch(typeof(BuildUi), "OpenBuildMenu"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Open_Pre() { OpenWindow(); Step("BuildUi.OpenBuildMenu begin"); }

        [HarmonyPatch(typeof(BuildUi), "OpenBuildMenu"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Open_Post() => Step("BuildUi.OpenBuildMenu end");

        // 1.1.236 (the lead, R-1013 build_menu_crash_test pass 1: the last line was "BuildUi.OpenBuildMenu begin", lazy OFF): which
        // part of the open. Prefix at Last = every other mod's prefix ran; postfix at First = vanilla's body returned. Vanilla's
        // own steps inside the body are traced below; button creation every 250th.
        [HarmonyPatch(typeof(BuildUi), "OpenBuildMenu"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void Open_PreLast() => Step("BuildUi.OpenBuildMenu prefixes done, vanilla body begin");

        [HarmonyPatch(typeof(BuildUi), "OpenBuildMenu"), HarmonyPostfix, HarmonyPriority(Priority.First)]
        private static void Open_PostFirst() => Step("BuildUi.OpenBuildMenu vanilla body done, postfixes begin");

        private static int s_buttons;

        [HarmonyPatch(typeof(BuildUi), "TryCreatePieceButtonFromPool"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Button_Pre()
        {
            if (!InWindow) return;
            s_buttons++;
            if (s_buttons <= 3 || s_buttons % 250 == 0) Step("BuildUi.TryCreatePieceButtonFromPool", $"#{s_buttons}");
        }

        [HarmonyPatch(typeof(BuildUi), "PlaceSeparator"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Sep_Pre() { if (InWindow) Step("BuildUi.PlaceSeparator"); }

        [HarmonyPatch(typeof(BuildUi), "UpdatePieceSelection"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Sel_Pre() { if (InWindow) Step("BuildUi.UpdatePieceSelection begin"); }

        [HarmonyPatch(typeof(BuildUi), "UpdatePieceSelection"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Sel_Post() { if (InWindow) Step("BuildUi.UpdatePieceSelection end"); }

        [HarmonyPatch(typeof(BuildUi), "SelectFirstPiece"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void First_Pre() { if (InWindow) Step("BuildUi.SelectFirstPiece"); }

        [HarmonyPatch(typeof(BuildUi), "SelectFirstAvailableButton"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void FirstBtn_Pre() { if (InWindow) Step("BuildUi.SelectFirstAvailableButton"); }

        [HarmonyPatch(typeof(BuildUi), "SelectPieceList"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void PieceList_Pre() { if (InWindow) Step("BuildUi.SelectPieceList"); }

        [HarmonyPatch(typeof(BuildUi), "FocusPiece"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Focus_Pre() { if (InWindow) Step("BuildUi.FocusPiece"); }

        [HarmonyPatch(typeof(BuildUi), "OnSelectPiece"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void OnSel_Pre() { if (InWindow) Step("BuildUi.OnSelectPiece"); }

        [HarmonyPatch(typeof(Hud), "SetupPieceInfo"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Info_Pre(Piece piece) { if (InWindow) Step("Hud.SetupPieceInfo begin", piece != null ? $"'{piece.name}'" : "null"); }

        [HarmonyPatch(typeof(Hud), "SetupPieceInfo"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Info_Post() { if (InWindow) Step("Hud.SetupPieceInfo end"); }

        [HarmonyPatch(typeof(Hud), "UpdatePieceBuildStatusAll"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void StatusAll_Pre() { if (InWindow) Step("Hud.UpdatePieceBuildStatusAll begin"); }

        [HarmonyPatch(typeof(Hud), "UpdatePieceBuildStatusAll"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void StatusAll_Post() { if (InWindow) Step("Hud.UpdatePieceBuildStatusAll end"); }

        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Buttons_Pre() { if (InWindow) Step("BuildUi.UpdatePieceButtons begin"); }

        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Buttons_Post() { if (InWindow) Step("BuildUi.UpdatePieceButtons end"); }

        [HarmonyPatch(typeof(Hud), "UpdatePieceList"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void List_Pre() { if (InWindow) Step("Hud.UpdatePieceList begin"); }

        [HarmonyPatch(typeof(Hud), "UpdatePieceList"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void List_Post() { if (InWindow) Step("Hud.UpdatePieceList end"); }

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Ghost_Pre(Player __instance)
        {
            // 1.1.242 (Fire: Infinity Hammer's Keypad-0 freeze holds for no piece; IH's "Unfreeze on select = true" drops the
            // freeze whenever the ghost is set up again). Who rebuilds the ghost outside the menu: a Debug line with the
            // callers, at most one a second, regardless of the trace window.
            if (__instance == Player.m_localPlayer && Time.unscaledTime >= s_nextGhostCaller)
            {
                bool menu = false; try { menu = Hud.IsPieceSelectionVisible(); } catch { }
                if (!menu)
                {
                    s_nextGhostCaller = Time.unscaledTime + 1f;
                    string who = "?";
                    try
                    {
                        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
                        var names = new System.Collections.Generic.List<string>();
                        foreach (var f in frames) { var m = f.GetMethod(); if (m != null) names.Add((m.DeclaringType?.Name ?? "?") + "." + m.Name); if (names.Count >= 7) break; }
                        who = string.Join(" < ", names.ToArray());
                    }
                    catch { }
                    string selName = null; try { selName = __instance.GetSelectedPiece()?.gameObject.name; } catch { }
                    VaPieces.Log?.LogDebug($"[FAP] placement ghost set up again (menu closed) for '{selName}' by {who}");
                }
            }
            if (!InWindow || __instance != Player.m_localPlayer) return;
            string sel = null; try { sel = __instance.GetSelectedPiece()?.gameObject.name; } catch { }
            Step("Player.SetupPlacementGhost begin", $"'{sel}'");
        }

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Ghost_Post(Player __instance) { if (InWindow && __instance == Player.m_localPlayer) Step("Player.SetupPlacementGhost end"); }
    }
}
