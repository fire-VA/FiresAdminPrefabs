using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;
using VerdantsAscent.Utilities;
using VerdantsAscent.Sync;

namespace VerdantsAscent.Network
{
    // 1.1.309 ([lead] 10-22, Expand World Data 1.79 bridge): fap_ewd_test, in the runner's format (BEGIN "N steps, ~S s" /
    // "n/N step PASS|FAIL|SKIP - detail" / END), tag [FapEwdTest]. Admin (isCheat), Test worlds only.
    //  1 ewd_bake: a 2-piece fixture blueprint (wood_floor + a sign whose data column carries "text" in the WEC/EWD DataEntry
    //    wire format) is baked through the right-click Pre-bake path (PreBakeCoroutine); the sign is a live child.
    //  2 ewd_place: the baked bundle is placed with vanilla Player.PlacePiece (the call a hammer click ends in); the sign child's
    //    VALiveChildMarker applies its data through VABundleDataApplier (the EWD bridge). PASS = Expand World Data loaded, at least
    //    one "EWD data applied to" line, 0 "data-apply threw" / "Instantiate failed" warnings, and the sign's ZDO text equals the
    //    fixture text. 0 applied lines = FAIL (the path did not run).
    // finally: the placed bundle is destroyed, the bake unpublished, its cache + hammer entry removed, the blueprint stub
    // unregistered and the fixture file recycled (never a hard delete). Reuses VAFapLiveTest's fixture / bake / cleanup calls.
    internal static class VAFapEwdTest
    {
        private const string Tag = "[FapEwdTest]";
        private const int Planned = 3;
        private const float BakeWait = 120f;
        private const string TempBlueprint = "FapEwdTest_BP";
        private static bool s_running;

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fap_ewd_test",
                "[F5 test] [FAP] bake + place a bundle whose sign carries Expand World Data data; the sign must keep its text (admin, Test worlds only)",
                args =>
                {
                    var ctx = args.Context;
                    if (s_running || VaPieces.Instance == null) return;
                    string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                    if (!VAFapLiveTest.IsTestWorld(world)) { ctx?.AddString($"{Tag} fap_ewd_test: refused, '{world ?? "?"}' is not a Test world"); return; }
                    if (Player.m_localPlayer == null || ZNetScene.instance == null || ObjectDB.instance == null) { ctx?.AddString($"{Tag} fap_ewd_test: no local player / world"); return; }
                    VaPieces.Instance.StartCoroutine(Run(ctx));
                }, isCheat: true);
        }

        private sealed class Step { internal string Name, Outcome, Detail; }

        private static volatile int s_applied, s_threw, s_instFailed;
        private static volatile bool s_bakeSeen;
        private static string s_bakePrefab;
        private sealed class Listener : BepInEx.Logging.ILogListener
        {
            public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
            {
                var msg = e?.Data?.ToString();
                if (msg == null) return;
                if (msg.Contains("VABundleDataApplier - EWD data applied to")) s_applied++;
                if (msg.Contains("data-apply threw") || msg.Contains("TryApplyImpl threw")) s_threw++;
                if (msg.Contains("Instantiate failed")) s_instFailed++;   // 1.1.310: only the real warning (the success line also names SpawnLiveChildrenForPlacedBundle)
                if (s_bakePrefab != null && msg.Contains("[FAP] bake: '" + s_bakePrefab + "' registered live")) s_bakeSeen = true;
            }
            public void Dispose() { }
        }
        private static Listener s_listener;

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            return hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        }

        // The data column: WEC/EWD DataEntry wire format (see VAZdoDataBlob): int flags, then for strings (0x010) a byte count and
        // count x { int keyHash; string }.
        private static string SignData(string text)
        {
            var pkg = new ZPackage();
            pkg.Write(0x010);
            pkg.Write((byte)1);
            pkg.Write(ZDOVars.s_text);
            pkg.Write(text);
            return Convert.ToBase64String(pkg.GetArray());
        }

        private static IEnumerator Run(Terminal console)
        {
            s_running = true;
            float started = Time.realtimeSinceStartup;
            var steps = new List<Step>();
            void Say(string line) { VaPieces.Log?.LogMessage(line); console?.AddString(line); }
            Step Add(string name) { var s = new Step { Name = name }; steps.Add(s); return s; }
            Say($"{Tag} BEGIN {VaPieces.PluginName} {VaPieces.PluginVersion} on client: {Planned} steps, ~60 s");
            if (s_listener == null) { s_listener = new Listener(); BepInEx.Logging.Logger.Listeners.Add(s_listener); }
            s_applied = 0; s_threw = 0; s_instFailed = 0; s_bakeSeen = false;

            string bpFile = null, stub = null, bakeKey = null, bakedPrefab = null;
            GameObject placed = null;
            string want = "FAP EWD test " + DateTime.Now.ToString("HHmmss");
            try
            {
                var s1 = Add("ewd_bake");
                var s2 = Add("ewd_place");
                var s3 = Add("ewd_bridge_direct");
                var scene = ZNetScene.instance;
                var table = HammerTable();
                VABlueprintLoader.Blueprint bp = null;
                try
                {
                    if (!VABundleDataApplier.IsEwdLoaded) { s1.Outcome = s2.Outcome = "FAIL"; s1.Detail = s2.Detail = "Expand World Data is not loaded on this client (the bridge cannot run)"; }
                    else if (scene.GetPrefab("wood_floor") == null || scene.GetPrefab("sign") == null) { s1.Outcome = s2.Outcome = "FAIL"; s1.Detail = s2.Detail = "fixture: wood_floor or sign missing from ZNetScene"; }
                    else
                    {
                        string path = Path.Combine(VABlueprintHammerIntegration.BlueprintsDir, TempBlueprint + ".blueprint");
                        string preKey = Path.GetFileName(path);
                        if (File.Exists(path) && !VAHammerTabOrganizer.RecyclePath(path)) throw new IOException($"leftover '{path}' could not be recycled");
                        VABakeBundle.Unpublish(preKey); VAFapBlueprintBakeCache.Remove(preKey);
                        Directory.CreateDirectory(VABlueprintHammerIntegration.BlueprintsDir);
                        // PlanBuild #pieces rows: prefab;category;x;y;z;qx;qy;qz;qw;info;sx;sy;sz;data (data = column 13)
                        File.WriteAllLines(path, new[]
                        {
                            "#name:" + TempBlueprint,
                            "#description:fap_ewd_test fixture (the test removes it)",
                            "#pieces",
                            "wood_floor;;0;0;0;0;0;0;1;;1;1;1",
                            "sign;;0;0.5;0;0;0;0;1;;1;1;1;" + SignData(want),
                        });
                        bpFile = path;
                        bool reg = VABlueprintHammerIntegration.RegisterBlueprintFile(path, scene, table, out stub, out var fixBp);
                        string key = fixBp != null ? VABlueprintHammerIntegration.GetCacheKey(fixBp) : null;
                        string data = fixBp?.Objects?.FirstOrDefault(o => o.Prefab == "sign")?.Data;
                        if (!reg || fixBp == null || fixBp.Objects.Count != 2 || string.IsNullOrEmpty(data))
                        { s1.Outcome = "FAIL"; s1.Detail = $"fixture: RegisterBlueprintFile={reg}, objects={fixBp?.Objects.Count ?? 0} (want 2), sign data read={(string.IsNullOrEmpty(data) ? "NO" : "yes")}"; }
                        else { bakeKey = key; bp = fixBp; bakedPrefab = VABakeBundle.MakePrefabName(key); s_bakePrefab = bakedPrefab; }
                    }
                }
                catch (Exception ex) { s1.Outcome = "FAIL"; s1.Detail = "fixture threw " + ex.Message; }

                if (bp != null)
                {
                    float t0 = Time.realtimeSinceStartup;
                    Say($"{Tag} ewd_bake: baking '{bakeKey}' -> '{bakedPrefab}' (sign text '{want}')");
                    yield return VAHammerRightClickPatches.PreBakeCoroutine(bakeKey, bp);
                    bool registered = false;
                    while (Time.realtimeSinceStartup - t0 < BakeWait)
                    {
                        registered = ZNetScene.instance != null && ZNetScene.instance.GetPrefab(bakedPrefab) != null;
                        if (registered && s_bakeSeen) break;
                        yield return null;
                    }
                    int lc = 0;
                    try { lc = VAFapBlueprintBakeCache.TryGet(bakeKey, out var cached) ? cached.LiveChildren.Count(c => c.PrefabName == "sign" && !string.IsNullOrEmpty(c.Data)) : -1; } catch { lc = -1; }
                    if (s1.Outcome == null)
                    {
                        s1.Outcome = registered && lc >= 1 ? "PASS" : "FAIL";
                        s1.Detail = $"'{bakedPrefab}' registered={registered} after {Time.realtimeSinceStartup - t0:0.0} s; sign live children with data in the bake: {lc}";
                    }
                }

                if (s1.Outcome == "PASS")
                {
                    var me = Player.m_localPlayer;
                    var piece = ZNetScene.instance.GetPrefab(bakedPrefab)?.GetComponent<Piece>();
                    Vector3 pos = me.transform.position + me.transform.forward * 6f;
                    pos.y = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(pos) + 0.05f : pos.y;
                    Quaternion rot = Quaternion.Euler(0f, me.transform.eulerAngles.y, 0f);
                    Sign sign = null;
                    if (piece == null) { s2.Outcome = "FAIL"; s2.Detail = $"'{bakedPrefab}' has no Piece component"; }
                    else
                    {
                        // 1.1.310: the fixture never goes to the server (it would register there on every world load)
                        VAFapAssetSync.TestNoPushPrefabs.Add(bakedPrefab);
                        me.PlacePiece(piece, pos, rot, false);   // vanilla placement, as a hammer click ends
                        yield return null; yield return null;
                        placed = UnityEngine.Object.FindObjectsOfType<Piece>()
                            .Where(p => p != null && p.gameObject.name.StartsWith(bakedPrefab, StringComparison.Ordinal) && Vector3.Distance(p.transform.position, pos) < 2f)
                            .Select(p => p.gameObject).FirstOrDefault();
                        // 1.1.310 ([lead] 08:38 run): the placed-bundle path (VABlueprintPlacementPatch -> SpawnLiveChildrenForPlacedBundle
                        // Coroutine -> VAZdoFirstSpawner) spawns the sign as its OWN world object (a GroupId sibling, not a child of the
                        // bundle) and applies its data with FAP's own reader (VAZdoDataBlob), so EWD is not involved on this path.
                        Vector3 signAt = pos + rot * new Vector3(0f, 0.5f, 0f);
                        float t1 = Time.realtimeSinceStartup;
                        string got = null;
                        while (Time.realtimeSinceStartup - t1 < 5f)
                        {
                            sign = UnityEngine.Object.FindObjectsOfType<Sign>()
                                .Where(x => x != null && Vector3.Distance(x.transform.position, signAt) < 3f)
                                .OrderBy(x => Vector3.Distance(x.transform.position, signAt)).FirstOrDefault();
                            var nv = sign != null ? sign.GetComponent<ZNetView>() : null;
                            got = nv != null && nv.IsValid() ? nv.GetZDO().GetString(ZDOVars.s_text) : null;
                            if (got == want) break;
                            yield return null;
                        }
                        bool ok = placed != null && sign != null && s_threw == 0 && s_instFailed == 0 && got == want;
                        s2.Outcome = ok ? "PASS" : "FAIL";
                        s2.Detail = placed == null ? $"no placed '{bakedPrefab}' found within 2 m of the placement"
                            : sign == null ? "no sign within 3 m of its blueprint position (the live child did not spawn)"
                            : $"sign '{sign.name}' {Vector3.Distance(sign.transform.position, signAt):0.0} m from its blueprint spot, ZDO text '{got ?? "(none)"}' vs fixture '{want}' (FAP's own reader on the placement path); {s_threw} data-apply threw, {s_instFailed} instantiate failed";
                    }

                    // 3 ewd_bridge_direct: the EWD 1.79 bridge itself (VABundleDataApplier.TryApply, the fallback for blobs FAP's reader
                    // rejects) writes a second text onto that same sign's ZDO; it must log "EWD data applied" and the text must change.
                    string want2 = want + " (bridge)";
                    var nv3 = sign != null ? sign.GetComponent<ZNetView>() : null;
                    if (nv3 == null || !nv3.IsValid()) { s3.Outcome = "FAIL"; s3.Detail = "no placed sign to write to (step 2)"; }
                    else if (!nv3.IsOwner()) { s3.Outcome = "SKIP"; s3.Detail = $"the sign's ZDO is owned by {nv3.GetZDO().GetOwner()}, not here (the test does not claim it)"; }
                    else
                    {
                        int before3 = s_applied;
                        bool r = false;
                        try { r = VABundleDataApplier.TryApply(SignData(want2), "sign", nv3.GetZDO()); }
                        catch (Exception ex) { s3.Outcome = "FAIL"; s3.Detail = "TryApply threw " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
                        yield return null;
                        if (s3.Outcome == null)
                        {
                            string got3 = nv3.GetZDO().GetString(ZDOVars.s_text);
                            int hits = s_applied - before3;
                            s3.Outcome = r && hits >= 1 && got3 == want2 && s_threw == 0 ? "PASS" : "FAIL";
                            s3.Detail = $"TryApply returned {r}; {hits} \"EWD data applied\" line(s) (0 = the bridge did not run), {s_threw} data-apply threw; ZDO text '{got3}' vs '{want2}'";
                        }
                    }
                }
                else
                {
                    if (s2.Outcome == null) { s2.Outcome = "FAIL"; s2.Detail = "no bake to place (ewd_bake failed)"; }
                    if (s3.Outcome == null) { s3.Outcome = "FAIL"; s3.Detail = "no placed sign (ewd_bake failed)"; }
                }
            }
            finally
            {
                if (placed != null)
                {
                    try
                    {
                        var nv = placed.GetComponent<ZNetView>();
                        if (nv != null && nv.IsValid()) { nv.ClaimOwnership(); ZNetScene.instance.Destroy(placed); } else UnityEngine.Object.Destroy(placed);
                        Say($"{Tag} cleanup: placed '{bakedPrefab}' destroyed");
                    }
                    catch (Exception ex) { Say($"{Tag} placed cleanup threw {ex.Message}"); }
                }
                if (bakeKey != null)
                {
                    try
                    {
                        VABakeBundle.Unpublish(bakeKey);
                        VAFapBlueprintBakeCache.Remove(bakeKey);
                        int off = VAVanillaPieceManager.RemovePieceFromHammerByName(bakedPrefab);
                        Say($"{Tag} cleanup: '{bakeKey}' bundle+manifest removed, {off} hammer entry removed; the template prefab leaves ZNetScene on the next world load");
                    }
                    catch (Exception ex) { Say($"{Tag} bake cleanup threw {ex.Message}"); }
                }
                if (bpFile != null)
                {
                    try
                    {
                        bool unreg = stub == null || VABlueprintHammerIntegration.UnregisterBlueprint(stub, ZNetScene.instance, HammerTable());
                        bool recycled = VAHammerTabOrganizer.RecyclePath(bpFile);
                        Say($"{Tag} cleanup: blueprint stub '{stub}' unregistered={unreg}, '{Path.GetFileName(bpFile)}' recycled={recycled}");
                    }
                    catch (Exception ex) { Say($"{Tag} blueprint fixture cleanup threw {ex.Message}"); }
                }
                if (bakedPrefab != null) VAFapAssetSync.TestNoPushPrefabs.Remove(bakedPrefab);   // 1.1.310
                s_bakePrefab = null;

                int pass = 0, fail = 0, skip = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    var s = steps[i];
                    if (s.Outcome == null) { s.Outcome = "FAIL"; s.Detail = "no result (the run stopped mid-step)"; }
                    if (s.Outcome == "PASS") pass++; else if (s.Outcome == "FAIL") fail++; else skip++;
                    Say($"{Tag} {i + 1}/{steps.Count} {s.Name} {s.Outcome} - {s.Detail}");
                }
                bool none = pass == 0 && fail == 0;
                Say($"{Tag} END {(fail > 0 || none ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0.0} s{(none ? " (0 compared)" : "")}");
                s_running = false;
            }
        }
    }
}
