using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Network
{
    // 1.1.283 ([ghost] proof for the 1.1.282 live hide/unhide + live bake): fap_live_test, an in-game test in the runner's
    // format (BEGIN "N steps, ~S s" / "n/N step PASS|FAIL|SKIP - detail" / END, SHOT lines), tag [FapLiveTest].
    // Admin (isCheat), Test worlds only. Format, guarded steps, finally-END and the FDT shot-settle listener are reused
    // from FiresNPCs Debug\FiresNpcsTest.cs; the Test-world rule from FiresMonsterOverhaul Bosses\BossPhaseTest.cs IsTestWorld.
    //  1 hide_unhide_piece: the right-click Hide path (VAHammerRightClickPatches.HidePrefabAction = HidePrefab +
    //    RemovePieceFromHammerByName), then VAHammerTabOrganizer.UnhidePrefab; piece gone, then back once, "[FAP] unhide:" seen.
    //  2 hide_unhide_tab: the right-click tab Hide path (HideTabAction = VAHammerTabOrganizer.HideTab), then UnhideTab
    //    (UnhideTabAction); category + pieces gone, then back with the same count, "[FAP] unhide: tab" seen.
    //  3 live_bake: the right-click Pre-bake path (PreBakeCoroutine) on the smallest not-yet-baked blueprint; prefab
    //    registered + on the hammer in Baked Meshes, "[FAP] bake: ... registered live" seen; shot of the build menu on
    //    Baked Meshes; the bundle + manifest this run created are removed (VABakeBundle.Unpublish, only when they did not exist before).
    // 1.1.284 (no more SKIPs): steps 2 and 3 build their own fixtures and remove them in the finally - a temp tab
    //    FapLiveTest_Tab holding one moved spare piece (MovePrefab/RetagPieceCategoryByName there and back, prefabs.txt
    //    byte-restored, VAHammerTabOrganizer.RemoveEmptyTab), and a 2-piece FapLiveTest_BP.blueprint registered via
    //    VABlueprintHammerIntegration.RegisterBlueprintFile (Unpublish + cache Remove + UnregisterBlueprint + Recycle Bin).
    internal static class VAFapLiveTest
    {
        private const string Tag = "[FapLiveTest]";
        private const int Planned = 3;
        private const float UnhideWait = 5f;
        private const float BakeWait = 120f;
        private const int MaxTabPieces = 20;
        private const int MaxBakeObjects = 200;
        private const string TempTab = "FapLiveTest_Tab";          // 1.1.284 (own tab fixture)
        private const string TempBlueprint = "FapLiveTest_BP";     // 1.1.284 (own blueprint fixture)
        private static bool s_running;

        // Reused from FiresMonsterOverhaul Bosses\BossPhaseTest.cs IsTestWorld (FDT Utilities\TestSite.cs rule).
        internal static bool IsTestWorld(string world) =>
            !string.IsNullOrEmpty(world) && (world.StartsWith("ATomTest", StringComparison.OrdinalIgnoreCase)
                || world.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0 || world.StartsWith("AB", StringComparison.Ordinal));

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fap_live_test",
                "[F5 test] [FAP] live hide/unhide of a hammer piece and a user tab, plus a live bake into Baked Meshes (admin, Test worlds only)",
                args =>
                {
                    var ctx = args.Context;
                    if (s_running || VaPieces.Instance == null) return;
                    string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                    if (!IsTestWorld(world)) { ctx?.AddString($"{Tag} fap_live_test: refused, '{world ?? "?"}' is not a Test world"); return; }
                    if (Player.m_localPlayer == null || ZNetScene.instance == null || ObjectDB.instance == null) { ctx?.AddString($"{Tag} fap_live_test: no local player / world"); return; }
                    VaPieces.Instance.StartCoroutine(Run(ctx));
                }, isCheat: true);
        }

        private sealed class Step { internal string Name, Outcome, Detail; }

        // Log listener (reused from FiresNpcsTest ShotListener): FDT's shot-settle line plus FAP's own debug lines.
        private static string s_shotCaption;
        private static volatile bool s_shotSettled;
        private static volatile bool s_unhideSeen, s_unhideTabSeen, s_bakeSeen;
        private static string s_bakePrefab;
        private sealed class Listener : BepInEx.Logging.ILogListener
        {
            public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
            {
                var msg = e?.Data?.ToString();
                if (msg == null) return;
                if (s_shotCaption != null && msg.Contains("[FiresTestShots] SHOT '" + s_shotCaption) && msg.Contains("settled")) s_shotSettled = true;
                if (msg.Contains("[FAP] unhide: tab ")) s_unhideTabSeen = true;
                else if (msg.Contains("[FAP] unhide: '")) s_unhideSeen = true;
                if (s_bakePrefab != null && msg.Contains("[FAP] bake: '" + s_bakePrefab + "' registered live")) s_bakeSeen = true;
            }
            public void Dispose() { }
        }
        private static Listener s_listener;

        private static IEnumerator WaitShot()
        {
            // 1.1.285 (FDT 1.1.882 loop): the old timeout warning started with "SHOT '" and FDT re-captured it as a shot request; the
            // warning is lowercase now, and an unconfirmed request is re-sent once (2 tries x 10 s, reused from FiresNPCs ChatScrollTest.HoldShot)
            for (int attempt = 1; attempt <= 2 && s_shotCaption != null; attempt++)
            {
                if (attempt > 1) { s_shotSettled = false; VaPieces.Log?.LogMessage($"{Tag} SHOT {s_shotCaption}"); }
                float until = Time.realtimeSinceStartup + 10f;
                while (!s_shotSettled && Time.realtimeSinceStartup < until) yield return null;
                if (s_shotSettled) break;
                VaPieces.Log?.LogWarning($"{Tag} shot '{s_shotCaption}': no FDT confirmation within 10 s (attempt {attempt}/2{(attempt < 2 ? ", re-sending" : ", moving on")})");
            }
            yield return new WaitForSecondsRealtime(0.5f);
            s_shotCaption = null;
        }

        private static string DllHash()
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(typeof(VAFapLiveTest).Assembly.Location))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 12);
            }
            catch { return "hash?"; }
        }

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            return hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        }

        private static int CountByName(PieceTable t, string name) =>
            t?.m_pieces == null ? 0 : t.m_pieces.Count(g => g != null && string.Equals(g.name, name, StringComparison.Ordinal));

        private static int CountInCategory(PieceTable t, Piece.PieceCategory cat) =>
            t?.m_pieces == null ? 0 : t.m_pieces.Count(g => g != null && g.GetComponent<Piece>() != null && g.GetComponent<Piece>().m_category == cat);

        private static IEnumerator Run(Terminal console)
        {
            s_running = true;
            float started = Time.realtimeSinceStartup;
            var steps = new List<Step>();
            void Say(string line) { VaPieces.Log?.LogMessage(line); console?.AddString(line); }
            void Shot(string caption) { s_shotCaption = caption; s_shotSettled = false; VaPieces.Log?.LogMessage($"{Tag} SHOT {caption}"); }
            Step Add(string name) { var s = new Step { Name = name }; steps.Add(s); return s; }
            Say($"{Tag} BEGIN {VaPieces.PluginName} {VaPieces.PluginVersion} ({DllHash()}) on client: {Planned} steps, ~150 s");
            if (s_listener == null) { s_listener = new Listener(); BepInEx.Logging.Logger.Listeners.Add(s_listener); }

            string hiddenPiece = null;            // still hidden by this run -> unhide in finally
            string hiddenTab = null;              // still hidden by this run -> unhide in finally
            string bakeKey = null, bakedPrefab = null;
            bool bakeFilesExisted = true;
            ItemDrop.ItemData hammerItem = null, prevRight = null;
            bool equippedHammer = false, openedMenu = false;
            ItemDrop.ItemData addedHammer = null;   // 1.1.290 (FDT empties the bag): test-added Hammer, removed in the finally
            // 1.1.284 fixtures (removed in the finally)
            string tabFixPiece = null, tabFixOrig = null, tabFixOrigFile = null; byte[] tabFixOrigBytes = null; bool tabFixMoved = false;
            string bpFixFile = null, bpFixStub = null;
            try
            {
                var table = HammerTable();

                // ---- 1 hide_unhide_piece ----
                var s1 = Add("hide_unhide_piece");
                IEnumerator e1 = null;
                try
                {
                    if (table == null) { s1.Outcome = "FAIL"; s1.Detail = "no hammer PieceTable"; }
                    else
                    {
                        var pick = table.m_pieces.FirstOrDefault(g => g != null && VAVanillaPieceManager.IsFapOwnedPiece(g)
                            && !VAHammerTabOrganizer.IsIgnored(g.name) && VABakeBundle.TryGetCacheKeyForPrefab(g.name) == null
                            && CountByName(table, g.name) == 1);
                        if (pick == null) { s1.Outcome = "SKIP"; s1.Detail = "no FAP-owned, unhidden piece on the hammer"; }
                        else e1 = PieceStep(s1, pick.name, table, n => hiddenPiece = n);
                    }
                }
                catch (Exception ex) { s1.Outcome = "FAIL"; s1.Detail = "threw " + ex.Message; }
                if (e1 != null) yield return Guard(e1, s1);

                // ---- 2 hide_unhide_tab ----
                var s2 = Add("hide_unhide_tab");
                IEnumerator e2 = null;
                try
                {
                    // 1.1.284: own fixture. ONE spare FAP piece that sits in a tab folder's prefabs.txt is moved into a
                    // new tab 'FapLiveTest_Tab' through the right-click Move path (VAHammerRightClickPatches.MovePrefabAction
                    // = VAHammerTabOrganizer.MovePrefab + VAVanillaPieceManager.RetagPieceCategoryByName); the finally moves
                    // it back the same way, byte-restores its original prefabs.txt and removes the empty tab (RemoveEmptyTab).
                    string dir = VAHammerTabOrganizer.HammerTabsDir;
                    if (table == null) { s2.Outcome = "FAIL"; s2.Detail = "no hammer PieceTable"; }
                    else if (Directory.Exists(Path.Combine(dir, TempTab)) || Directory.Exists(Path.Combine(dir, VAHammerTabOrganizer.HiddenTabPrefix + TempTab)))
                    { s2.Outcome = "FAIL"; s2.Detail = $"leftover '{TempTab}' (or Hidden_ twin) folder from an earlier run - remove it first"; }
                    else
                    {
                        GameObject spare = null; string orig = null;
                        foreach (var g in table.m_pieces.Where(g => g != null && VAVanillaPieceManager.IsFapOwnedPiece(g)
                                     && !VAHammerTabOrganizer.IsIgnored(g.name) && VABakeBundle.TryGetCacheKeyForPrefab(g.name) == null
                                     && !VABlueprintHammerIntegration.IsBlueprintStub(g.name) && CountByName(table, g.name) == 1).ToList())
                        {
                            if (!VAHammerTabOrganizer.TryGetTabOverride(g.name, out var t) || string.IsNullOrEmpty(t)
                                || t.StartsWith(VAHammerTabOrganizer.HiddenTabPrefix, StringComparison.Ordinal)) continue;
                            string f = Path.Combine(dir, t, "prefabs.txt");
                            if (!File.Exists(f) || !File.ReadAllLines(f).Any(l => string.Equals((l ?? "").Trim(), g.name, StringComparison.Ordinal))) continue;
                            spare = g; orig = t; break;
                        }
                        if (spare == null) { s2.Outcome = "FAIL"; s2.Detail = "no spare FAP piece listed in a tab folder's prefabs.txt to move into the temp tab"; }
                        else
                        {
                            tabFixPiece = spare.name; tabFixOrig = orig;
                            tabFixOrigFile = Path.Combine(dir, orig, "prefabs.txt");
                            tabFixOrigBytes = File.ReadAllBytes(tabFixOrigFile);
                            bool moved = VAHammerTabOrganizer.MovePrefab(tabFixPiece, TempTab);
                            tabFixMoved = moved;
                            int retag = moved ? VAVanillaPieceManager.RetagPieceCategoryByName(tabFixPiece, TempTab) : 0;
                            if (!moved || retag < 1 || !VACategoryRegistry.TryGet(TempTab, out var tc) || !table.m_categories.Contains(tc))
                            { s2.Outcome = "FAIL"; s2.Detail = $"fixture: MovePrefab('{tabFixPiece}' -> '{TempTab}')={moved}, retagged {retag}, category on hammer=false"; }
                            else
                            {
                                int cnt = CountInCategory(table, tc);
                                Say($"{Tag} hide_unhide_tab: fixture '{TempTab}' holds '{tabFixPiece}' (from '{orig}'), {cnt} piece(s)");
                                e2 = TabStep(s2, TempTab, tc, cnt, table, n => hiddenTab = n);
                            }
                        }
                    }
                }
                catch (Exception ex) { s2.Outcome = "FAIL"; s2.Detail = "threw " + ex.Message; }
                if (e2 != null) yield return Guard(e2, s2);

                // ---- 3 live_bake ----
                var s3 = Add("live_bake");
                VABlueprintLoader.Blueprint bp = null;
                try
                {
                    // 1.1.284: own fixture. A 2-piece PlanBuild file 'FapLiveTest_BP.blueprint' (the #pieces row format
                    // VABlueprintLoader.ParsePlanBuildPiece reads) is written to VABlueprintHammerIntegration.BlueprintsDir
                    // and registered the way ScanAndRegisterCoroutine registers (RegisterBlueprintFile); the finally
                    // Unpublishes the bake, drops its cache entry, unregisters the stub and recycles the file.
                    var scene = ZNetScene.instance;
                    string bpPath = Path.Combine(VABlueprintHammerIntegration.BlueprintsDir, TempBlueprint + ".blueprint");
                    string preKey = Path.GetFileName(bpPath);
                    string[] parts = new[] { "wood_floor", "woodwall", "stone_wall_1x1", "wood_wall_half" }.Where(p => scene.GetPrefab(p) != null).Take(2).ToArray();
                    if (!VACategoryRegistry.TryGet(VACategoryRegistry.CatBakedMeshes, out _)) { s3.Outcome = "FAIL"; s3.Detail = "no Baked Meshes category registered"; }
                    else if (parts.Length < 2) { s3.Outcome = "FAIL"; s3.Detail = "fixture: fewer than 2 of wood_floor/woodwall/stone_wall_1x1/wood_wall_half in ZNetScene"; }
                    else if (VABlueprintHammerIntegration.IsBlueprintStub(VABlueprintHammerIntegration.MakeStubName(bpPath)))
                    { s3.Outcome = "FAIL"; s3.Detail = $"'{TempBlueprint}' is already a registered stub (leftover from an earlier run in this session)"; }
                    else
                    {
                        // a leftover file / bake from a crashed earlier run is test-owned: clear it the same way the finally does
                        if (File.Exists(bpPath) && !VAHammerTabOrganizer.RecyclePath(bpPath)) throw new IOException($"leftover '{bpPath}' could not be recycled");
                        VABakeBundle.Unpublish(preKey); VAFapBlueprintBakeCache.Remove(preKey);
                        Directory.CreateDirectory(VABlueprintHammerIntegration.BlueprintsDir);
                        File.WriteAllLines(bpPath, new[]
                        {
                            "#name:" + TempBlueprint,
                            "#description:fap_live_test fixture (the test removes it)",
                            "#pieces",
                            parts[0] + ";;0;0;0;0;0;0;1;;1;1;1",
                            parts[1] + ";;0;0;2;0;0;0;1;;1;1;1",
                        });
                        bpFixFile = bpPath;
                        bool reg = VABlueprintHammerIntegration.RegisterBlueprintFile(bpPath, scene, table, out bpFixStub, out var fixBp);
                        string key = fixBp != null ? VABlueprintHammerIntegration.GetCacheKey(fixBp) : null;
                        bool listed = reg && VAHammerRightClickPatches.CollectBakeableBlueprints(out _).Any(b => b.CacheKey == key);
                        if (!reg || !listed || fixBp.Objects.Count != 2)
                        { s3.Outcome = "FAIL"; s3.Detail = $"fixture: RegisterBlueprintFile={reg}, in CollectBakeableBlueprints={listed}, objects={fixBp?.Objects.Count ?? 0} (want 2)"; }
                        else if (VAFapBlueprintBakeCache.Has(key) || File.Exists(VABakeBundle.BundlePathFor(key)) || File.Exists(VABakeBundle.ManifestPathFor(key))
                                 || scene.m_namedPrefabs.ContainsKey(VABakeBundle.MakePrefabName(key).GetStableHashCode()))
                        { s3.Outcome = "FAIL"; s3.Detail = $"fixture '{key}' already baked / registered after the pre-clean (a world reload clears the template prefab)"; }
                        else { bakeKey = key; bp = fixBp; }
                    }
                    if (bp != null)
                    {
                        bakedPrefab = VABakeBundle.MakePrefabName(bakeKey);
                        bakeFilesExisted = false;
                        s_bakePrefab = bakedPrefab; s_bakeSeen = false;
                        Say($"{Tag} live_bake: baking '{bakeKey}' ({bp.Objects.Count} objects) -> '{bakedPrefab}'");
                    }
                }
                catch (Exception ex) { s3.Outcome = "FAIL"; s3.Detail = "threw " + ex.Message; }
                if (bp != null)
                {
                    float t0 = Time.realtimeSinceStartup;
                    // the right-click Pre-bake path (VAHammerRightClickPatches.PreBakeCoroutine)
                    yield return Guard(VAHammerRightClickPatches.PreBakeCoroutine(bakeKey, bp), s3);
                    VACategoryRegistry.TryGet(VACategoryRegistry.CatBakedMeshes, out var bakedCat);
                    bool registered = false, onHammer = false;
                    while (Time.realtimeSinceStartup - t0 < BakeWait)
                    {
                        try
                        {
                            registered = ZNetScene.instance != null && ZNetScene.instance.GetPrefab(bakedPrefab) != null;
                            var t = HammerTable();
                            onHammer = t != null && t.m_pieces.Count(g => g != null && g.name == bakedPrefab && g.GetComponent<Piece>()?.m_category == bakedCat) == 1;
                        }
                        catch { }
                        if (registered && onHammer && s_bakeSeen) break;
                        yield return null;
                    }
                    float took = Time.realtimeSinceStartup - t0;
                    if (s3.Outcome == null)
                    {
                        int compared = 3;
                        bool ok = registered && onHammer && s_bakeSeen;
                        s3.Outcome = ok ? "PASS" : "FAIL";
                        s3.Detail = $"'{bakedPrefab}' registered={registered}, once in Baked Meshes={onHammer}, \"[FAP] bake: ... registered live\" seen={s_bakeSeen} after {took:0.0} s ({compared} checks)";
                    }

                    // shot: the build menu on Baked Meshes, opened the human way (hammer equipped, Hud.TogglePieceSelection as the build-menu key does)
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
                                // 1.1.290 (r8: "no hammer in the inventory" - FDT's runner empties the bag): add one ObjectDB Hammer, test-owned
                                var hp = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
                                var hd = hp != null ? hp.GetComponent<ItemDrop>() : null;
                                if (hd != null)
                                {
                                    var copy = hd.m_itemData.Clone();
                                    copy.m_dropPrefab = hp; copy.m_stack = 1;
                                    if (me.GetInventory().AddItem(copy)) { hammerItem = copy; addedHammer = copy; Say($"{Tag} live_bake: 1.1.290 added a test Hammer (bag had none)"); }
                                    else Say($"{Tag} live_bake: 1.1.290 test Hammer AddItem failed (bag full?)");
                                }
                            }
                            if (hammerItem != null) { me.EquipItem(hammerItem, false); equippedHammer = true; }
                        }
                    }
                    catch (Exception ex) { Say($"{Tag} live_bake: hammer equip threw {ex.Message}"); }

                    // 1.1.285 (r: shot showed NO build menu): Hud.UpdateBuild hides the piece selection on any frame the player is
                    // not in place mode, and the equip lands a frame later, so the old same-frame toggle was closed before the shot.
                    // Measured: wait for InPlaceMode, open via TogglePieceSelection (the build-menu key's path), select Baked Meshes,
                    // re-open if Hud closed it, until visible AND the category is Baked Meshes, max 3 s.
                    float m0 = Time.realtimeSinceStartup;
                    bool menuOk = false, placeMode = false;
                    string menuWhy = "no hammer in the inventory";
                    while (Time.realtimeSinceStartup - m0 < 3f)
                    {
                        try
                        {
                            var me = Player.m_localPlayer;
                            var t = HammerTable();
                            if (me != null && t != null && me.GetRightItem()?.m_shared?.m_buildPieces == t && Hud.instance != null)
                            {
                                placeMode = me.InPlaceMode();
                                menuWhy = placeMode ? "piece selection not visible / category not Baked Meshes" : "hammer equipped, player not in place mode";
                                if (placeMode)
                                {
                                    if (!Hud.IsPieceSelectionVisible()) { Hud.instance.TogglePieceSelection(); openedMenu = true; }
                                    if (t.GetSelectedCategory() != bakedCat)
                                    {
                                        t.SetCategory(bakedCat);
                                        HammerCategoryUI.RebuildPieceListIfHammer(me, "FapLiveTest");   // reused: FAP's own build-menu redraw
                                    }
                                    menuOk = Hud.IsPieceSelectionVisible() && t.GetSelectedCategory() == bakedCat;
                                }
                            }
                        }
                        catch (Exception ex) { menuWhy = "build menu open threw " + ex.Message; }
                        if (menuOk) break;
                        yield return null;
                    }
                    float menuTook = Time.realtimeSinceStartup - m0;
                    if (menuOk) Say($"{Tag} live_bake: build menu open on Baked Meshes after {menuTook:0.0} s");
                    else
                    {
                        Say($"{Tag} live_bake: build menu NOT open after {menuTook:0.0} s ({menuWhy})");
                        s3.Outcome = "FAIL";
                        s3.Detail = (s3.Detail ?? "") + $"; build menu on Baked Meshes NOT open after {menuTook:0.0} s ({menuWhy}, placeMode={placeMode})";
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    Shot("fap_live_bake_baked_meshes");
                    yield return WaitShot();
                    // measured wait for the async bundle write (VABakeBundle.PendingBundleWrites) before the finally removes its file, max 30 s
                    float w0 = Time.realtimeSinceStartup;
                    while (VABakeBundle.PendingBundleWrites > 0 && Time.realtimeSinceStartup - w0 < 30f) yield return null;
                    Say($"{Tag} live_bake: bundle write flushed in {Time.realtimeSinceStartup - w0:0.0} s (pending {VABakeBundle.PendingBundleWrites})");
                }
            }
            finally
            {
                try { if (openedMenu) Hud.HidePieceSelection(); } catch { }
                try
                {
                    var me = Player.m_localPlayer;
                    if (me != null && equippedHammer && hammerItem != null)
                    {
                        me.UnequipItem(hammerItem, false);
                        if (prevRight != null) me.EquipItem(prevRight, false);
                    }
                    if (me != null && addedHammer != null)
                    {
                        // 1.1.290: remove exactly the test-added Hammer (this instance, not by name)
                        if (me.IsItemEquiped(addedHammer)) me.UnequipItem(addedHammer, false);
                        bool rm = me.GetInventory().RemoveItem(addedHammer);
                        Say($"{Tag} restore: 1.1.290 test Hammer removed={rm}");
                    }
                }
                catch (Exception ex) { Say($"{Tag} hammer restore threw {ex.Message}"); }
                if (hiddenPiece != null)
                {
                    try { bool u = VAHammerTabOrganizer.UnhidePrefab(hiddenPiece); Say($"{Tag} restore: piece '{hiddenPiece}' unhidden={u}"); } catch (Exception ex) { Say($"{Tag} restore piece threw {ex.Message}"); }
                }
                if (hiddenTab != null)
                {
                    try { bool u = VAHammerTabOrganizer.UnhideTab(VAHammerTabOrganizer.HiddenTabPrefix + hiddenTab); Say($"{Tag} restore: tab '{hiddenTab}' unhidden={u}"); } catch (Exception ex) { Say($"{Tag} restore tab threw {ex.Message}"); }
                }
                if (tabFixMoved)
                {
                    // 1.1.284: the tab fixture goes back through the same Move path, the original prefabs.txt is
                    // byte-restored (MovePrefab re-appends the line), then the empty temp tab is removed + recycled.
                    var st2 = steps.FirstOrDefault(x => x.Name == "hide_unhide_tab");
                    try
                    {
                        bool back = VAHammerTabOrganizer.MovePrefab(tabFixPiece, tabFixOrig);
                        int retag = back ? VAVanillaPieceManager.RetagPieceCategoryByName(tabFixPiece, tabFixOrig) : 0;
                        bool bytes = false;
                        if (back && tabFixOrigBytes != null) { File.WriteAllBytes(tabFixOrigFile, tabFixOrigBytes); bytes = true; }
                        bool removed = VAHammerTabOrganizer.RemoveEmptyTab(TempTab, out var rd);
                        bool gone = !Directory.Exists(Path.Combine(VAHammerTabOrganizer.HammerTabsDir, TempTab));
                        Say($"{Tag} cleanup: '{tabFixPiece}' moved back to '{tabFixOrig}'={back} (retagged {retag}, prefabs.txt byte-restored={bytes}); RemoveEmptyTab('{TempTab}')={removed} ({rd}), folder gone={gone}");
                        if (st2 != null && (!back || retag < 1 || !removed || !gone) && st2.Outcome != "FAIL") { st2.Outcome = "FAIL"; st2.Detail += " | fixture cleanup incomplete"; }
                    }
                    catch (Exception ex) { Say($"{Tag} tab fixture cleanup threw {ex.Message}"); if (st2 != null) { st2.Outcome = "FAIL"; st2.Detail += " | fixture cleanup threw"; } }
                }
                if (bakeKey != null && !bakeFilesExisted)
                {
                    try
                    {
                        // only files this run created (none existed when picked): reused VABakeBundle.Unpublish + VAFapBlueprintBakeCache.Remove
                        VABakeBundle.Unpublish(bakeKey);
                        VAFapBlueprintBakeCache.Remove(bakeKey);
                        int off = VAVanillaPieceManager.RemovePieceFromHammerByName(bakedPrefab);
                        Say($"{Tag} cleanup: '{bakeKey}' bundle+manifest removed,{off} hammer entry removed; the template prefab leaves ZNetScene on the next world load");
                    }
                    catch (Exception ex) { Say($"{Tag} bake cleanup threw {ex.Message}"); }
                }
                if (bpFixFile != null)
                {
                    // 1.1.284: blueprint fixture - stub unregistered, then the file to the Recycle Bin (never a hard delete)
                    var st3 = steps.FirstOrDefault(x => x.Name == "live_bake");
                    try
                    {
                        bool unreg = bpFixStub == null || VABlueprintHammerIntegration.UnregisterBlueprint(bpFixStub, ZNetScene.instance, HammerTable());
                        bool recycled = VAHammerTabOrganizer.RecyclePath(bpFixFile);
                        Say($"{Tag} cleanup: blueprint stub '{bpFixStub}' unregistered={unreg}, '{Path.GetFileName(bpFixFile)}' recycled={recycled}");
                        if (st3 != null && (!unreg || !recycled) && st3.Outcome != "FAIL") { st3.Outcome = "FAIL"; st3.Detail += " | fixture cleanup incomplete"; }
                    }
                    catch (Exception ex) { Say($"{Tag} blueprint fixture cleanup threw {ex.Message}"); if (st3 != null) { st3.Outcome = "FAIL"; st3.Detail += " | fixture cleanup threw"; } }
                }
                s_bakePrefab = null;

                int pass = 0, fail = 0, skip = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    var s = steps[i];
                    if (s.Outcome == null) { s.Outcome = "FAIL"; s.Detail = "no result (the run stopped mid-step)"; }
                    if (s.Outcome == "PASS") pass++; else if (s.Outcome == "FAIL") fail++; else skip++;
                    Say($"{Tag} {i + 1}/{steps.Count} {s.Name} {s.Outcome} - {s.Detail}");
                }
                // 0 compared = FAIL (every step skipped)
                bool none = pass == 0 && fail == 0;
                Say($"{Tag} END {(fail > 0 || none ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0.0} s{(none ? " (0 compared)" : "")}");
                s_running = false;
            }
        }

        // Runs a step's enumerator; an exception = that step FAILs, the run goes on.
        private static IEnumerator Guard(IEnumerator inner, Step s)
        {
            while (true)
            {
                object cur;
                try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
                catch (Exception ex) { if (s.Outcome == null || s.Outcome == "PASS") { s.Outcome = "FAIL"; s.Detail = "threw " + ex.Message; } yield break; }
                yield return cur;
            }
        }

        private static IEnumerator PieceStep(Step s, string name, PieceTable table, Action<string> setHidden)
        {
            int before = CountByName(table, name);
            // reused from VAHammerRightClickPatches.HidePrefabAction (the right-click "Hide")
            if (!VAHammerTabOrganizer.HidePrefab(name)) { s.Outcome = "FAIL"; s.Detail = $"HidePrefab('{name}') returned false"; yield break; }
            setHidden(name);
            int removed = VAVanillaPieceManager.RemovePieceFromHammerByName(name);
            int afterHide = CountByName(table, name);
            s_unhideSeen = false;
            float t0 = Time.realtimeSinceStartup;
            bool un = VAHammerTabOrganizer.UnhidePrefab(name);
            if (un) setHidden(null);
            int back = 0;
            while (Time.realtimeSinceStartup - t0 < UnhideWait)
            {
                back = CountByName(table, name);
                if (back >= 1 && s_unhideSeen) break;
                yield return null;
            }
            float took = Time.realtimeSinceStartup - t0;
            back = CountByName(table, name);
            bool ok = before == 1 && removed >= 1 && afterHide == 0 && un && back == 1 && s_unhideSeen;
            s.Outcome = ok ? "PASS" : "FAIL";
            s.Detail = $"'{name}': {before} on hammer, hide removed {removed} -> {afterHide}, UnhidePrefab={un} -> {back} (want 1, no duplicate) after {took:0.00} s, \"[FAP] unhide:\" seen={s_unhideSeen}";
        }

        private static IEnumerator TabStep(Step s, string tab, Piece.PieceCategory cat, int before, PieceTable table, Action<string> setHidden)
        {
            // reused from VAHammerRightClickPatches.HideTabAction / UnhideTabAction (the right-click tab menu)
            if (!VAHammerTabOrganizer.HideTab(tab)) { s.Outcome = "FAIL"; s.Detail = $"HideTab('{tab}') returned false"; yield break; }
            setHidden(tab);
            bool catGone = !table.m_categories.Contains(cat);
            int afterHide = CountInCategory(table, cat);
            s_unhideTabSeen = false;
            float t0 = Time.realtimeSinceStartup;
            bool un = VAHammerTabOrganizer.UnhideTab(VAHammerTabOrganizer.HiddenTabPrefix + tab);
            if (un) setHidden(null);
            Piece.PieceCategory cat2 = cat;
            bool catBack = false; int back = 0;
            while (Time.realtimeSinceStartup - t0 < UnhideWait)
            {
                catBack = VACategoryRegistry.TryGet(tab, out cat2) && table.m_categories.Contains(cat2);
                back = catBack ? CountInCategory(table, cat2) : 0;
                if (catBack && back >= before && s_unhideTabSeen) break;
                yield return null;
            }
            float took = Time.realtimeSinceStartup - t0;
            int catCopies = table.m_categories.Count(c => c == cat2);
            bool ok = catGone && afterHide == 0 && un && catBack && back == before && catCopies == 1 && s_unhideTabSeen;
            s.Outcome = ok ? "PASS" : "FAIL";
            s.Detail = $"tab '{tab}' ({before} piece(s)): hidden -> category gone={catGone}, {afterHide} left; UnhideTab={un} -> category back={catBack} x{catCopies}, {back} piece(s) (want {before}) after {took:0.00} s, \"[FAP] unhide: tab\" seen={s_unhideTabSeen}";
        }
    }
}
