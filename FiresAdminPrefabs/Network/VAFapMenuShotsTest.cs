using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Network
{
    // 1.1.333 ([lead] for Fire 10-30: "Open up the player's build menu in the actual game to take a look at it for parity to walk mode
    // menu"): fap_menu_shots_test - the in-game build menu, opened the human way, one SHOT per top tab (the 1.0 piece lists: By usage,
    // By material, Recent, Favorites = BuildUi.SelectPieceList 0..3) and one more on a FAP tab of the left list, then everything closed
    // and put back. REUSES fap_live_test (VAFapLiveTest): its Test-world gate, hammer equip / test-hammer add, place-mode wait,
    // Hud.TogglePieceSelection, SetCategory + HammerCategoryUI.RebuildPieceListIfHammer, and its FDT shot-settle wait, unchanged.
    internal static class VAFapMenuShotsTest
    {
        private const string Tag = "[FapMenuShotsTest]";
        private static bool s_running;
        private static string s_shotCaption;
        private static volatile bool s_shotSettled;
        private sealed class Listener : BepInEx.Logging.ILogListener
        {
            public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
            {
                var msg = e?.Data?.ToString();
                if (msg != null && s_shotCaption != null && msg.Contains("[FiresTestShots] SHOT '" + s_shotCaption) && msg.Contains("settled")) s_shotSettled = true;
            }
            public void Dispose() { }
        }
        private static Listener s_listener;

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fap_menu_shots_test",
                "[F5 test] [FAP] the in-game build menu: one shot per top tab (By usage / By material / Recent / Favorites) + one FAP tab (admin)",
                args =>
                {
                    var ctx = args.Context;
                    if (s_running || VaPieces.Instance == null) return;
                    string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                    // 1.1.334 (12:21 round on RPGWorld: no BEGIN - the Test-world gate refused into the console only): no world gate - this test
                    // changes nothing in the world (it opens the menu and adds / removes a test hammer in the tester's own bag); refusals go to the log.
                    if (Player.m_localPlayer == null || ObjectDB.instance == null) { VaPieces.Log?.LogWarning($"{Tag} refused: no local player / world"); ctx?.AddString($"{Tag} no local player / world"); return; }
                    VaPieces.Instance.StartCoroutine(Run(ctx));
                }, isCheat: true);
        }

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            return hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        }

        private static IEnumerator WaitShot()
        {
            for (int attempt = 1; attempt <= 2 && s_shotCaption != null; attempt++)
            {
                if (attempt > 1) { s_shotSettled = false; VaPieces.Log?.LogMessage($"{Tag} SHOT {s_shotCaption}"); }
                float until = Time.realtimeSinceStartup + 10f;
                while (!s_shotSettled && Time.realtimeSinceStartup < until) yield return null;
                if (s_shotSettled) break;
                VaPieces.Log?.LogWarning($"{Tag} shot '{s_shotCaption}': no FDT confirmation within 10 s (attempt {attempt}/2)");
            }
            yield return new WaitForSecondsRealtime(0.5f);
            s_shotCaption = null;
        }

        private static IEnumerator Run(Terminal console)
        {
            s_running = true;
            void Say(string line) { VaPieces.Log?.LogMessage(line); console?.AddString(line); }
            void Shot(string caption) { s_shotCaption = caption; s_shotSettled = false; VaPieces.Log?.LogMessage($"{Tag} SHOT {caption}"); }
            if (s_listener == null) { s_listener = new Listener(); BepInEx.Logging.Logger.Listeners.Add(s_listener); }
            ItemDrop.ItemData prevRight = null, hammerItem = null, addedHammer = null;
            bool equipped = false, opened = false;
            int shots = 0, planned = 5;
            Say($"{Tag} BEGIN {VaPieces.PluginName} {VaPieces.PluginVersion}: {planned} shots");
            try
            {
                var me = Player.m_localPlayer;
                var t = HammerTable();
                prevRight = me.GetRightItem();
                if (prevRight == null || prevRight.m_shared?.m_buildPieces != t)
                {
                    hammerItem = me.GetInventory().GetAllItems().FirstOrDefault(i => i?.m_shared?.m_buildPieces == t);
                    if (hammerItem == null)
                    {
                        var hp = ObjectDB.instance.GetItemPrefab("Hammer"); var hd = hp != null ? hp.GetComponent<ItemDrop>() : null;
                        if (hd != null) { var copy = hd.m_itemData.Clone(); copy.m_dropPrefab = hp; copy.m_stack = 1; if (me.GetInventory().AddItem(copy)) { hammerItem = copy; addedHammer = copy; } }
                    }
                    if (hammerItem != null) { me.EquipItem(hammerItem, false); equipped = true; }
                }
                // open the menu the human way (fap_live_test 1.1.285: wait for place mode, then TogglePieceSelection), max 3 s
                float m0 = Time.realtimeSinceStartup; bool open = false;
                while (Time.realtimeSinceStartup - m0 < 3f)
                {
                    if (me.GetRightItem()?.m_shared?.m_buildPieces == t && me.InPlaceMode() && Hud.instance != null)
                    {
                        if (!Hud.IsPieceSelectionVisible()) { Hud.instance.TogglePieceSelection(); opened = true; }
                        open = Hud.IsPieceSelectionVisible();
                    }
                    if (open) break;
                    yield return null;
                }
                Say($"{Tag} build menu {(open ? "open" : "NOT open")} after {Time.realtimeSinceStartup - m0:0.0} s");
                var ui = Hud.instance != null ? Hud.instance.m_buildUi : null;
                string[] lists = { "by_usage", "by_material", "recent", "favorites" };
                for (int i = 0; i < lists.Length && open && ui != null; i++)
                {
                    try { ui.SelectPieceList(i); } catch (Exception ex) { Say($"{Tag} SelectPieceList({i}) threw {ex.Message}"); }
                    yield return new WaitForSecondsRealtime(0.5f);
                    Shot("fap_menu_top_" + lists[i]); yield return WaitShot(); shots++;
                }
                // one FAP tab on the left list: the last table category that has pieces (a FAP tab; vanilla's come first)
                if (open && ui != null)
                {
                    try { ui.SelectPieceList(0); } catch { }
                    var cat = t.m_categories.LastOrDefault(c => t.m_pieces.Any(g => g != null && g.GetComponent<Piece>()?.m_category == c));
                    int ci = t.m_categories.IndexOf(cat);
                    string label = ci >= 0 && ci < t.m_categoryLabels.Count ? t.m_categoryLabels[ci] : cat.ToString();
                    t.SetCategory(cat);
                    HammerCategoryUI.RebuildPieceListIfHammer(me, "FapMenuShotsTest");
                    yield return new WaitForSecondsRealtime(0.5f);
                    Say($"{Tag} left-list tab: '{label}' (category {(int)cat})");
                    Shot("fap_menu_left_fap_tab"); yield return WaitShot(); shots++;
                }
            }
            finally
            {
                try { if (opened) Hud.HidePieceSelection(); } catch { }
                try
                {
                    var me = Player.m_localPlayer;
                    if (me != null && equipped && hammerItem != null) { me.UnequipItem(hammerItem, false); if (prevRight != null) me.EquipItem(prevRight, false); }
                    if (me != null && addedHammer != null) { if (me.IsItemEquiped(addedHammer)) me.UnequipItem(addedHammer, false); me.GetInventory().RemoveItem(addedHammer); }
                }
                catch (Exception ex) { Say($"{Tag} restore threw {ex.Message}"); }
                Say($"{Tag} END {(shots == planned ? "PASS" : "FAIL")} {shots}/{planned} shot(s)");
                s_running = false;
            }
        }
    }
}
