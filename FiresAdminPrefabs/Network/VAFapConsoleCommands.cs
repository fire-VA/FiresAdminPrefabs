using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;
using VerdantsAscent.Patches;

using FiresCore.Net;

namespace VerdantsAscent.Network
{
    // FAP's console commands. Each command's description string is its documentation,
    // so it isn't repeated here. All but the read-only fap_dump_categories are isCheat,
    // so vanilla blocks them unless the player is admin or has cheats enabled. Nothing
    // is registered on a dedicated server, which has no Terminal to attach to.
    public static class VAFapConsoleCommands
    {
        // Vanilla UpdateAvailable hardcodes 9 when fanning an All-category piece across the
        // per-category lists (REF1.0 PieceTable.cs:83) - it is NOT PieceCategory.Max, and it does
        // not follow the tables FAP grows past 9. Kept literal for that reason.
        private const int VanillaAllCategoryFanout = 9;
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            if (VaPieces.IsDedicatedServer) return;

            new Terminal.ConsoleCommand("fap_ewpstatus",
                "Show the FAP yaml file paths + their current sizes.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var (scriptsPath, dataPath) = VAFapEwpYamlWriter.GetFilePaths();
                    args.Context.AddString("[FAP] EWP yaml status:");
                    args.Context.AddString(DescribeFile("scripts", scriptsPath));
                    args.Context.AddString(DescribeFile("data   ", dataPath));
                    args.Context.AddString("[FAP] VanillaFriendly=" + ConfigManager.Instance.configVanillaFriendly.Value
                        + " UseEwpForPersistence=" + ConfigManager.Instance.configUseEwpForPersistence.Value);
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_dump_categories",
                "Print every build category the hammer resolves, its piece count and whether FAP owns it, plus the runtime vs compiled PieceCategory.Max. Answers 'are the FAP tabs empty' without opening the menu.",
                (Terminal.ConsoleEventArgs args) => DumpCategories(args.Context),
                // Read-only, so not a cheat: 1.0's console never enables cheats on a client of a dedicated server
                // (Terminal.IsCheatsEnabled wants IsServer), so as a cheat it was unusable on a vanilla server - the
                // client-only test's own automation could not run it.
                isCheat: false);

            new Terminal.ConsoleCommand("fap_selftest_place",
                "Run every registered item-stand, monster and clutter stand-in through the live placement classifier and each spawner precondition. Reports which would fall through to vanilla, which have no button, and which would fail on click - without placing anything.",
                (Terminal.ConsoleEventArgs args) => SelfTestPlace(args.Context),
                isCheat: true);

            new Terminal.ConsoleCommand("fap_cleanewp",
                "Migration sweep: drops any per-prefab entries left over from older FAP versions and ensures only the single universal rule remains. Local-only.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var (sk, sd, dk, dd) = VAFapEwpYamlWriter.CleanStaleEntries();
                    args.Context.AddString($"[FAP] Clean complete:");
                    args.Context.AddString($"  scripts: kept {sk}, dropped {sd}");
                    args.Context.AddString($"  data:    kept {dk}, dropped {dd}");
                    if (sd + dd == 0)
                        args.Context.AddString($"[FAP] No stale entries found — files are already clean.");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_wipeewp",
                "Delete the FAP yaml files and reset the session guard so they're rewritten on next world load. Type 'fap_wipeewp confirm' to actually delete (no confirm arg = no-op, prevents fat-finger wipes).",
                (Terminal.ConsoleEventArgs args) =>
                {
                    bool confirmed = args.Length >= 2
                        && string.Equals(args[1], "confirm", System.StringComparison.OrdinalIgnoreCase);
                    if (!confirmed)
                    {
                        var (scriptsPath, dataPath) = VAFapEwpYamlWriter.GetFilePaths();
                        args.Context.AddString("[FAP] Wipe NOT performed (missing 'confirm' arg).");
                        args.Context.AddString("[FAP] Files that WOULD be deleted:");
                        args.Context.AddString("  " + scriptsPath);
                        args.Context.AddString("  " + dataPath);
                        args.Context.AddString("[FAP] Re-run as: fap_wipeewp confirm");
                        return;
                    }
                    bool deleted = VAFapEwpYamlWriter.WipeLocalFiles();
                    args.Context.AddString(deleted
                        ? "[FAP] FAP yaml files deleted + session guard reset. Rewrite on next world load."
                        : "[FAP] Nothing to wipe — no FAP yaml files exist.");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_snapdump",
                "Diagnostic: report the snap points actually present on a build piece RIGHT NOW. No arg uses the piece currently selected in the hammer; otherwise pass a prefab name. Prints whether the prefab is in ZNetScene, whether any PieceTable already claimed it (which makes FAP skip it entirely, snap patch included), whether FAP's curated table ran, and every snappoint child with its local position. Use this when a piece refuses to snap: it separates 'the points are missing' from 'the points are there but the ghost picks the wrong one'.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    string wanted = args.Length > 1 ? args[1] : null;
                    foreach (var line in Managers.Snapping.VASnapPoints.Describe(wanted))
                    {
                        args.Context.AddString(line);
                        // AddString only reaches the in-game console buffer, which is
                        // no use when the answer has to be read back out of the log.
                        Debug.Log($"{VaPieces.PluginName}: {line}");
                    }
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_snaplive",
                "Diagnostic: while the build ghost is up, log which snap pair vanilla actually chooses — ghost point, target point, distance, and whether you are in AUTO or a manual selection. 'on' to start, 'off' to stop; samples once a second to the log. fap_snapdump proves the points exist, this proves which one wins, which is what 'snaps from the centre' is really about.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    bool on = args.Length <= 1 || !string.Equals(args[1], "off", System.StringComparison.OrdinalIgnoreCase);
                    string line = VerdantsAscent.Utilities.VASnapLiveDiagnostics.Toggle(on);
                    args.Context.AddString(line);
                    Debug.Log($"{VaPieces.PluginName}: {line}");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_push_presets",
                "Push your local FAP chest/monster preset yaml + EWP rule yaml UP to the connected server so respawns fire server-side. Admin-only; only works when connected to a REMOTE server (on a listen-host the files are already local). Same payload as the AutoPushOnPlace feature, on demand. The server admin-gates the write and reloads its presets so rules go live without a restart.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    VerdantsAscent.Sync.VAFapAssetSync.PushPresetsNow(s => args.Context.AddString(s));
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_push_bundle",
                "Push your locally baked blueprint pieces UP to the connected server — each Assets/*.bakebundle plus its Manifests/*.bakemanifest sidecar. No arg pushes all of them; pass part of a name to push just the matching ones. Admin-only, remote servers only. The server stores the files, hands them to every other client on connect, and registers the piece at its next world load. Baked bundles run to tens of MB, so expect the transfer to take a moment.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    string nameFilter = args.Length > 1 ? args[1] : null;
                    VerdantsAscent.Sync.VAFapAssetSync.PushBakedBundlesNow(s => args.Context.AddString(s), nameFilter);
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_clear_orphan_fx",
                "Sweep orphaned vanilla status-effect FX (wet / frost / etc.) left in the world when their character unloaded — the non-networked follow-FX the normal FX clear brush can't see. No arg or 'scan [radius]' reports; 'confirm [radius]' destroys. Radius defaults to 64m, centered on you. Args are order-independent.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null)
                    {
                        args.Context.AddString("[FAP] No local player — load into a world first.");
                        return;
                    }
                    bool confirm = false;
                    float radius = 64f;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (string.Equals(args[i], "confirm", System.StringComparison.OrdinalIgnoreCase))
                            confirm = true;
                        else if (float.TryParse(args[i], out float r) && r > 0f)
                            radius = r;
                    }
                    VAFapOrphanFxCleaner.Run(player.transform.position, radius, confirm,
                        s => args.Context.AddString(s));
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_fx_dump",
                "Diagnostic: list EVERY renderer-bearing object near you (NO filtering) with its renderer type + lineage (root name, ZNetView/Character ancestor, ParentConstraint state, layer), closest first. Use to identify an FX that fap_clear_orphan_fx isn't catching. Optional radius (default 10m).",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null)
                    {
                        args.Context.AddString("[FAP] No local player — load into a world first.");
                        return;
                    }
                    float radius = 10f;
                    for (int i = 1; i < args.Length; i++)
                        if (float.TryParse(args[i], out float r) && r > 0f) radius = r;
                    VAFapOrphanFxCleaner.Dump(player.transform.position, radius,
                        s => args.Context.AddString(s));
                },
                isCheat: true);

            // One-shot cleanup for worlds polluted by the pre-fix era where
            // the EWP YAML rule fired on every ZNetView destroy and injected
            // FAP_persist=1 into every ZDO. Walks ZDOMan.m_objectsByID and
            // clears FAP_persist on any ZDO that has it set, regardless of
            // whether it "should" be there per the new opt-in system. After
            // this, the new filter-based YAML rule does the right thing
            // automatically — only ZDOs the user has explicitly opted into
            // (via right-click → "Place with Persistence") will get re-tagged
            // on subsequent placements.
            new Terminal.ConsoleCommand("fap_clear_legacy_persistence",
                "Strip FAP_persist=1 from EVERY ZDO in the world. Use this once after updating to recover from the pre-fix era where FX attached to monsters / vanilla content got tagged for respawn. After running this, only pieces you explicitly opt-in via right-click → 'Place with Persistence' will be persistent going forward.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (ZDOMan.instance == null)
                    {
                        args.Context.AddString("[FAP] ZDOMan not ready — load into a world first.");
                        return;
                    }

                    int persistKey = VAFapEwpYamlWriter.PersistMarkerKey.GetStableHashCode();
                    int cleared = 0;
                    int scanned = 0;

                    var zdos = VAZdoManAccess.SnapshotAllZdos();
                    if (zdos == null)
                    {
                        args.Context.AddString("[FAP] Could not read ZDOMan's ZDO table — see the log.");
                        return;
                    }

                    foreach (var zdo in zdos)
                    {
                        if (zdo == null) continue;
                        scanned++;
                        if (zdo.GetInt(persistKey, 0) != 0)
                        {
                            zdo.Set(persistKey, 0);
                            zdo.IncreaseDataRevision();
                            cleared++;
                        }
                    }

                    // Also wipe the session opt-in set so the user starts
                    // clean — anything they had ticked is forgotten and
                    // future placements default to non-persistent until
                    // they explicitly re-opt-in.
                    VAFapPersistentOptIn.Clear();

                    args.Context.AddString($"[FAP] Scanned {scanned} ZDO(s); cleared FAP_persist on {cleared}. "
                        + "Session opt-ins reset. New placements will default to NOT persistent until you "
                        + "right-click an FX/VFX hammer piece and choose 'Place with Persistence'.");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_orphan_zdo",
                "Report ZDOs whose prefab no longer exists on this machine — the objects behind vanilla's 'Missing prefab hash' log spam — one line per prefab with a ZDO count and a sample position. FAP names its own content (blueprint stubs, baked bundles) from the blueprint files on disk; anything else is reported as unknown, because it usually belongs to a mod that is only temporarily absent. 'fap_orphan_zdo purge' deletes only the FAP content it could name. 'fap_orphan_zdo purge <hash>' deletes one prefab's ZDOs. 'fap_orphan_zdo purge all' deletes every orphan, other mods' included — only do that when their mod is gone for good. Purging is a world edit and is admin-only. Only ZDOs this machine currently holds are scanned, so fly the area first for a distant build.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var groups = VAFapOrphanZdoScan.Scan();
                    if (groups == null)
                    {
                        args.Context.AddString("[FAP] ZDO table not readable — load into a world first (see the log if this persists).");
                        return;
                    }
                    if (groups.Count == 0)
                    {
                        args.Context.AddString("[FAP] No orphaned ZDOs: every prefab referenced by the ZDOs this machine holds resolves.");
                        return;
                    }

                    bool purgeRequested = args.Length >= 2
                        && string.Equals(args[1], "purge", System.StringComparison.OrdinalIgnoreCase);
                    if (!purgeRequested)
                    {
                        ReportOrphanGroups(args, groups);
                        return;
                    }

                    if (!IsLocalAdminForWorldEdit())
                    {
                        args.Context.AddString("[FAP] Purge refused — admin only.");
                        return;
                    }

                    string target = args.Length >= 3 ? args[2] : null;
                    var hashesToPurge = SelectOrphanHashes(args, groups, target);
                    if (hashesToPurge == null) return;

                    int destroyed = VAFapOrphanZdoScan.Purge(hashesToPurge);
                    args.Context.AddString($"[FAP] Destroyed {destroyed} orphaned ZDO(s) across {hashesToPurge.Count} prefab(s). "
                        + "Re-run the command to confirm the world is clean.");
                },
                isCheat: true);

            // Deleting copies of a prefab that RESOLVES - what fap_orphan_zdo cannot do, because it only finds
            // ZDOs whose prefab is missing. Leftovers from a blueprint whose parent piece is gone are ordinary
            // valid ZDOs and nothing reports them. Destructive with no undo, so it reports and stops unless the
            // caller ends the line with "confirm".
            new Terminal.ConsoleCommand("fap_purge_prefab",
                "Delete every ZDO of one prefab near you. Usage: fap_purge_prefab <prefab|hash> [radius|all] [confirm]. Without 'confirm' it only REPORTS what it would delete - count, distance range and a per-zone breakdown - and deletes nothing. Radius defaults to 64 m and is measured from the player; 'all' means every copy this machine holds, at any distance. For leftovers of a blueprint whose parent piece no longer registers: those children are valid ZDOs with a resolving prefab, so fap_orphan_zdo will never list them. Deleting is a world edit, is admin-only, and CANNOT BE UNDONE - back the world up first. Only ZDOs this machine currently holds are scanned, so fly the area first for a distant build.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length < 2)
                    {
                        args.Context.AddString("[FAP] Usage: fap_purge_prefab <prefab|hash> [radius|all] [confirm]");
                        args.Context.AddString("[FAP] Without 'confirm' nothing is deleted - run it bare first and read the report.");
                        return;
                    }

                    int prefabHash = VerdantsAscent.Utilities.VAFapPrefabPurge.ResolveHash(args[1], out string prefabName);
                    if (prefabHash == 0)
                    {
                        args.Context.AddString($"[FAP] '{args[1]}' does not name a prefab this machine knows. Nothing scanned.");
                        return;
                    }

                    bool confirmed = false;
                    float radius = 64f;
                    for (int i = 2; i < args.Length; i++)
                    {
                        string token = args[i];
                        if (string.Equals(token, "confirm", System.StringComparison.OrdinalIgnoreCase)) { confirmed = true; continue; }
                        if (string.Equals(token, "all", System.StringComparison.OrdinalIgnoreCase)) { radius = -1f; continue; }
                        if (float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed) && parsed > 0f)
                        {
                            radius = parsed;
                            continue;
                        }
                        args.Context.AddString($"[FAP] Did not understand '{token}'. Expected a radius in metres, 'all', or 'confirm'.");
                        return;
                    }

                    UnityEngine.Vector3 center = Player.m_localPlayer != null
                        ? Player.m_localPlayer.transform.position
                        : (ZNet.instance != null ? ZNet.instance.GetReferencePosition() : UnityEngine.Vector3.zero);

                    var match = VerdantsAscent.Utilities.VAFapPrefabPurge.Survey(prefabHash, prefabName, center, radius);
                    if (match == null)
                    {
                        args.Context.AddString("[FAP] ZDO table not readable - load into a world first.");
                        return;
                    }

                    string scope = radius < 0f ? "anywhere this machine holds" : $"within {radius:0} m";
                    if (match.TotalHeld == 0)
                    {
                        args.Context.AddString($"[FAP] No '{prefabName}' ZDOs held at all. Nothing to do.");
                        return;
                    }
                    if (match.WithinRadius == 0)
                    {
                        args.Context.AddString($"[FAP] {match.TotalHeld} '{prefabName}' held, but none {scope} "
                            + $"(nearest {match.NearestDistance:0} m). Widen the radius or fly closer.");
                        return;
                    }

                    if (!confirmed)
                    {
                        args.Context.AddString($"[FAP] WOULD DELETE {match.WithinRadius} of {match.TotalHeld} '{prefabName}' ZDO(s) {scope}.");
                        args.Context.AddString($"[FAP]   distance {match.NearestDistance:0} m to {match.FarthestDistance:0} m, across {match.ByZone.Count} zone(s)");
                        foreach (var sample in match.Samples)
                            args.Context.AddString($"[FAP]   at {sample.x:0}, {sample.y:0}, {sample.z:0}");
                        args.Context.AddString("[FAP] Nothing deleted. Re-run with 'confirm' on the end to delete. THERE IS NO UNDO.");
                        return;
                    }

                    if (!IsLocalAdminForWorldEdit())
                    {
                        args.Context.AddString("[FAP] Purge refused - admin only.");
                        return;
                    }

                    int destroyed = VerdantsAscent.Utilities.VAFapPrefabPurge.Purge(prefabHash, center, radius);
                    args.Context.AddString($"[FAP] Destroyed {destroyed} '{prefabName}' ZDO(s) {scope}. "
                        + "Re-run without 'confirm' to confirm the area is clean.");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_assetsdir",
                "Print the absolute path to the FAP bundle-assets folder + list its current contents. The folder is auto-created at plugin Awake; drop *.bundle / *.assetbundle / *.unity3d files (or extensionless UnityFS bundles ripped via SteamyDumps) here for FAP to load on next world entry.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var dir = VerdantsAscent.Managers.VAExternalBundleLoader.AssetsDir;
                    args.Context.AddString($"[FAP] Bundle assets dir: {dir}");
                    if (!System.IO.Directory.Exists(dir))
                    {
                        args.Context.AddString("[FAP] Folder DOES NOT EXIST — Awake-time creation failed (check BepInEx log for warning).");
                        return;
                    }
                    string[] files;
                    try { files = System.IO.Directory.GetFiles(dir); }
                    catch (System.Exception ex)
                    {
                        args.Context.AddString($"[FAP] Could not list folder: {ex.Message}");
                        return;
                    }
                    if (files.Length == 0)
                    {
                        args.Context.AddString("[FAP] Folder is empty. Drop bundles here, then reload the world.");
                        return;
                    }
                    args.Context.AddString($"[FAP] Contents ({files.Length} files):");
                    foreach (var f in files)
                    {
                        var fi = new System.IO.FileInfo(f);
                        args.Context.AddString($"  {fi.Name} ({fi.Length} bytes)");
                    }
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_import_yoink",
                "Pull bundles + manifests from FiresSteamyDumps' YoinkScanMe output into FAP's Assets/ and Manifests/ folders. Usage: fap_import_yoink [force]",
                (Terminal.ConsoleEventArgs args) =>
                {
                    bool force = args.Length > 1 && args[1].Equals("force", System.StringComparison.OrdinalIgnoreCase);
                    ImportFromYoink(args, force);
                },
                isCheat: true);

            // Clear the on-disk icon cache + null out m_icon on every
            // FAP-injected piece + re-enqueue them for fresh rendering.
            // New PNG files get written as the queue processes (one icon
            // per frame). Use when a bundle's textures changed, shaders
            // got fixed mid-session, or you just want a fresh batch.
            new Terminal.ConsoleCommand("fap_reload_icons",
                "Clear the FAP icon cache and re-render hammer icons for every injected piece. New PNGs are written as the queue completes.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    var stats = VerdantsAscent.Utilities.VAPieceIconRenderer.ReloadAll();
                    args.Context.AddString($"[FAP] fap_reload_icons: deleted {stats.filesDeleted} cached PNG(s), re-queued {stats.piecesReQueued} piece(s) for re-render. Icons will refresh as the queue drains (one per frame).");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("fap_rebake_all",
                "Bake every registered blueprint into a fresh bundle, replacing existing bakes. Runs one blueprint at a time; relaunch afterwards to use the new prefabs.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (VaPieces.Instance == null)
                    {
                        args.Context.AddString("[FAP] fap_rebake_all: plugin instance not available — try again after world is fully loaded.");
                        return;
                    }
                    args.Context.AddString("[FAP] fap_rebake_all: re-baking every registered blueprint into fresh bundles. Watch the message hud + log for progress; this can take minutes per megablueprint.");
                    VaPieces.Instance.StartCoroutine(
                        VerdantsAscent.Patches.VAHammerRightClickPatches.RebakeAllCoroutine());
                },
                isCheat: true);

            // Migrate clutter prefabs that prior sessions parked in
            // bundle-named folders into the Clutter tab. Default is
            // dry-run; pass 'apply' to actually rewrite the prefabs.txt
            // files on disk. After applying, AutoCategorize re-runs so
            // the moves take effect in-session — no world reload needed.
            new Terminal.ConsoleCommand("fap_recategorize_clutter",
                "Walk every disk-override entry, identify prefabs that are TRULY clutter (InstanceRenderer / ClutterSystem membership / BundleClutterAllOf) but parked in a non-Clutter folder, and migrate them into Clutter/prefabs.txt. Usage: fap_recategorize_clutter [apply]. Without 'apply', dry-run only.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    bool apply = args.Length >= 2
                        && string.Equals(args[1], "apply", System.StringComparison.OrdinalIgnoreCase);

                    var (moved, sourceFolderCount, unresolved) =
                        VAHammerTabOrganizer.RecategorizeClutter(dryRun: !apply);

                    if (!apply)
                    {
                        args.Context.AddString($"[FAP] fap_recategorize_clutter DRY-RUN:");
                        args.Context.AddString($"  Would migrate {moved} prefab(s) from {sourceFolderCount} folder(s) into Clutter/prefabs.txt.");
                        if (unresolved > 0)
                            args.Context.AddString($"  {unresolved} disk entry/entries skipped — no GameObject currently loaded for those names (probably from a removed mod).");
                        if (moved > 0)
                            args.Context.AddString($"[FAP] Re-run as 'fap_recategorize_clutter apply' to commit.");
                        else
                            args.Context.AddString($"[FAP] Nothing to migrate — every clutter prefab is already in Clutter/.");
                        return;
                    }

                    args.Context.AddString($"[FAP] fap_recategorize_clutter APPLY:");
                    args.Context.AddString($"  Migrated {moved} prefab(s) from {sourceFolderCount} folder(s) into Clutter/prefabs.txt.");
                    if (unresolved > 0)
                        args.Context.AddString($"  {unresolved} disk entry/entries skipped (no live GameObject).");

                    // Re-route m_category on every eligible piece now that
                    // the override map points clutter prefabs at the
                    // Clutter tab. Forces the hammer UI to reshuffle in
                    // the current session — no world reload needed.
                    if (moved > 0)
                    {
                        VAVanillaPieceManager.RecategorizeEligiblePieces();
                        args.Context.AddString($"[FAP] Re-categorized live pieces. Open the hammer to see the Clutter tab refreshed.");
                    }
                },
                isCheat: true);

            // Destroys persistent ZDOs saved into the world before the m_forceDisableInit
            // fix in VAClutterPaintTool.SpawnInstanced — clutter containers and SFX
            // sub-effects that re-instantiate every load with a "Double ZNetview" cascade and
            // a stone-break sound on the loading screen. Matches only those specific noise
            // names, so unrelated objects of the same type are untouched. Dry run by default;
            // pass 'apply' to commit. Safe to re-run.
            new Terminal.ConsoleCommand("fap_clean_stale_clutter",
                "Walk ZNetScene.m_instances, find stale persistent ZDOs for clutter-effect prefabs (D_sfx_rock_destroy, instanced_small_rock1, etc.) saved into the world before the SpawnInstanced m_forceDisableInit fix landed, and destroy them via SafeDestroy. Usage: fap_clean_stale_clutter [apply]. Without 'apply', dry-run only.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    bool apply = args.Length >= 2
                        && string.Equals(args[1], "apply", System.StringComparison.OrdinalIgnoreCase);

                    var scene = ZNetScene.instance;
                    if (scene == null)
                    {
                        args.Context.AddString("[FAP] fap_clean_stale_clutter: ZNetScene not ready — try after fully spawning in.");
                        return;
                    }

                    // Patterns that indicate stale clutter-effect ZDOs.
                    // Conservative — only the prefab kinds that produce
                    // the Double-ZNetview cascade. Add more here if a
                    // user reports other prefab names spamming the same
                    // warnings.
                    string[] stalePatterns =
                    {
                        // Clutter container leftovers
                        "D_sfx_rock_destroy",
                        "D_sfx_",
                        "instanced_small_rock1",
                        "instanced_forest_groundcover",
                        "instanced_",
                        "FAP_PaintedClutter_",
                        // Effect-prefab leftovers (VFX/SFX/material-effects)
                        // — same patterns as VAClutterPaintTool._stalePrefabPatterns
                        // and VABundleAssetRouter._effectNameMarkers.
                        // Pre-fix sessions saved these as persistent ZDOs;
                        // this cleanup removes them from the world save.
                        "_VFX_",
                        "_vfx_",
                        "VFX_",
                        "_SFX_",
                        "_sfx_",
                        "SFX_",
                        "_FX_",
                        "_fx_",
                        "Krump_Mat_",
                    };

                    var victims = new System.Collections.Generic.List<UnityEngine.GameObject>();
                    foreach (var kv in scene.m_instances)
                    {
                        var nview = kv.Value;
                        if (nview == null) continue;
                        UnityEngine.GameObject go;
                        try { go = nview.gameObject; } catch { continue; }
                        if (go == null) continue;
                        string n;
                        try { n = go.name ?? string.Empty; } catch { continue; }
                        if (n.Length == 0) continue;

                        bool matches = false;
                        for (int i = 0; i < stalePatterns.Length; i++)
                        {
                            if (n.IndexOf(stalePatterns[i], System.StringComparison.Ordinal) >= 0)
                            {
                                matches = true;
                                break;
                            }
                        }
                        if (!matches) continue;

                        victims.Add(go);
                    }

                    if (!apply)
                    {
                        args.Context.AddString($"[FAP] fap_clean_stale_clutter DRY-RUN:");
                        args.Context.AddString($"  Found {victims.Count} stale clutter ZDO(s) in ZNetScene.m_instances.");
                        if (victims.Count > 0)
                        {
                            int sample = System.Math.Min(victims.Count, 10);
                            args.Context.AddString($"  First {sample} match(es):");
                            for (int i = 0; i < sample; i++)
                            {
                                args.Context.AddString($"    - {victims[i].name} @ {victims[i].transform.position}");
                            }
                            args.Context.AddString($"[FAP] Re-run as 'fap_clean_stale_clutter apply' to destroy them.");
                        }
                        else
                        {
                            args.Context.AddString($"[FAP] Nothing to clean — world save is free of stale clutter ZDOs.");
                        }
                        return;
                    }

                    int destroyed = 0;
                    foreach (var go in victims)
                    {
                        try
                        {
                            FiresCore.Net.NetworkObjectHelper.SafeDestroy(go);
                            destroyed++;
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"{VaPieces.PluginName}: fap_clean_stale_clutter — SafeDestroy threw on '{go?.name}': {ex.Message}");
                        }
                    }
                    args.Context.AddString($"[FAP] fap_clean_stale_clutter APPLY: destroyed {destroyed} stale clutter ZDO(s) via SafeDestroy. World save will reflect the cleanup on next save tick (or zone unload). Re-run after a /save command to persist the cleanup.");
                },
                isCheat: true);

            // Read-only bake diagnostic: classifies each prefab and reports whether it
            // lands geometry in the combined mesh, without combining or baking anything.
            new Terminal.ConsoleCommand("fap_bake_diagnose",
                "Diagnose why a blueprint's pieces do/don't land in the combined mesh, with no rebake. Usage: fap_bake_diagnose <blueprintName> [nameFilter]. Detail goes to LogOutput.log; a summary prints here.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length < 2)
                    {
                        args.Context.AddString("[FAP] usage: fap_bake_diagnose <blueprintName> [nameFilter]");
                        return;
                    }
                    var scene = ZNetScene.instance;
                    if (scene == null)
                    {
                        args.Context.AddString("[FAP] fap_bake_diagnose: ZNetScene not ready — run after fully spawning in.");
                        return;
                    }

                    string bpName = args[1];
                    string filter = args.Length >= 3 ? args[2] : null;

                    var bp = VABlueprintLoader.LoadByName(bpName);
                    if (bp == null)
                    {
                        args.Context.AddString($"[FAP] fap_bake_diagnose: blueprint '{bpName}' not found in any probed blueprint folder.");
                        return;
                    }
                    bp.Center(string.Empty);   // match the bake's re-centering so transforms line up

                    // Tee every line to the terminal AND the BepInEx log.
                    int lineCount = 0;
                    System.Action<string> emit = (line) =>
                    {
                        args.Context.AddString(line);
                        Debug.Log($"{VaPieces.PluginName}: [bake_diagnose] {line}");
                        lineCount++;
                    };

                    try
                    {
                        VAFapBlueprintMeshBaker.DiagnoseBlueprintBake(bp, scene, filter, emit);
                    }
                    catch (System.Exception ex)
                    {
                        args.Context.AddString($"[FAP] fap_bake_diagnose threw: {ex.Message}");
                        Debug.LogWarning($"{VaPieces.PluginName}: fap_bake_diagnose threw: {ex}");
                        return;
                    }
                    args.Context.AddString($"[FAP] fap_bake_diagnose: {lineCount} line(s) — full detail in LogOutput.log (search '[bake_diagnose]').");
                },
                isCheat: true);

            // Bake every registered blueprint and write a library-wide diagnostic report
            // (unresolved materials, churn-risk materials, zero-geometry prefabs).
            new Terminal.ConsoleCommand("fap_bake_all",
                "Bake EVERY registered blueprint and write a full diagnostic report to config/FiresAdminPrefabs/Diagnostics/bake_all_report.txt (unresolved materials, zero-geometry pieces, churn-risk materials). Can take many minutes.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (VaPieces.Instance == null)
                    {
                        args.Context.AddString("[FAP] fap_bake_all: plugin instance not available — try again after the world is fully loaded.");
                        return;
                    }
                    args.Context.AddString("[FAP] fap_bake_all: baking ALL blueprints + writing the diagnostic report. Watch the message hud; this can take many minutes. Report → config/FiresAdminPrefabs/Diagnostics/bake_all_report.txt");
                    VaPieces.Instance.StartCoroutine(
                        VerdantsAscent.Patches.VAHammerRightClickPatches.BakeAllWithDiagnosticCoroutine());
                },
                isCheat: true);

            LogRegisteredCommands();
        }

        private const string CommandNamePrefix = "fap_";

        // Read back from the terminal rather than written by hand: the list used to be a literal
        // that drifted with every command added or removed, naming some that no longer existed
        // and missing others that did.
        private static void LogRegisteredCommands()
        {
            var commandsField = HarmonyLib.AccessTools.Field(typeof(Terminal), "commands");
            var registered = commandsField?.GetValue(null) as System.Collections.IDictionary;
            if (registered == null)
            {
                Debug.Log($"{VaPieces.PluginName}: VAFapConsoleCommands registered — the terminal's command table could not be read, so the list is unavailable.");
                return;
            }

            var names = new System.Collections.Generic.List<string>();
            foreach (var key in registered.Keys)
            {
                string name = key as string;
                if (!string.IsNullOrEmpty(name) && name.StartsWith(CommandNamePrefix, System.StringComparison.Ordinal))
                    names.Add(name);
            }
            names.Sort(System.StringComparer.Ordinal);

            Debug.Log($"{VaPieces.PluginName}: VAFapConsoleCommands registered {names.Count}: {string.Join(", ", names.ToArray())}.");
        }

        // Copies SteamyDumps' YoinkScanMe output (Yoink/ScanMe/Bundles and
        // Yoink/ScanMe/Manifests) into FAP's Assets and Manifests folders, where the next
        // world load picks them up. Files already in the destination are skipped, so it's
        // safe to re-run after a partial copy; `force` overwrites them. A missing source
        // folder is a no-op that logs the path it looked for.
        private static void ImportFromYoink(Terminal.ConsoleEventArgs args, bool force)
        {
            string configRoot = BepInEx.Paths.ConfigPath;
            string yoinkBundlesDir = Path.Combine(configRoot, "FiresSteamyDumps", "Yoink", "ScanMe", "Bundles");
            string yoinkManifestsDir = Path.Combine(configRoot, "FiresSteamyDumps", "Yoink", "ScanMe", "Manifests");
            string fapAssetsDir = VAExternalBundleLoader.AssetsDir;
            string fapManifestsDir = VAExternalBundleLoader.ManifestsDir;

            args.Context.AddString($"[FAP] fap_import_yoink: {(force ? "FORCE OVERWRITE" : "skip-existing")} mode");

            int bundlesCopied = 0, bundlesSkipped = 0;
            int manifestsCopied = 0, manifestsSkipped = 0;

            // 1. Bundles → Assets/
            if (Directory.Exists(yoinkBundlesDir))
            {
                try { Directory.CreateDirectory(fapAssetsDir); } catch { }
                foreach (var src in Directory.GetFiles(yoinkBundlesDir))
                {
                    string name = Path.GetFileName(src);
                    // SteamyDumps drops a README.txt; don't copy that —
                    // FAP's Assets/ has its own README.
                    if (name.Equals("README.txt", System.StringComparison.OrdinalIgnoreCase)) continue;
                    string dst = Path.Combine(fapAssetsDir, name);
                    if (File.Exists(dst) && !force) { bundlesSkipped++; continue; }
                    try
                    {
                        File.Copy(src, dst, overwrite: true);
                        bundlesCopied++;
                    }
                    catch (System.Exception ex)
                    {
                        args.Context.AddString($"  bundle '{name}': copy failed: {ex.Message}");
                    }
                }
                args.Context.AddString($"[FAP] Bundles: copied {bundlesCopied}, skipped {bundlesSkipped} (already present).");
            }
            else
            {
                args.Context.AddString($"[FAP] No bundles to import — source folder missing: {yoinkBundlesDir}");
            }

            // 2. Manifests → Manifests/
            if (Directory.Exists(yoinkManifestsDir))
            {
                try { Directory.CreateDirectory(fapManifestsDir); } catch { }
                foreach (var src in Directory.GetFiles(yoinkManifestsDir, "*.manifest.json"))
                {
                    string name = Path.GetFileName(src);
                    string dst = Path.Combine(fapManifestsDir, name);
                    if (File.Exists(dst) && !force) { manifestsSkipped++; continue; }
                    try
                    {
                        File.Copy(src, dst, overwrite: true);
                        manifestsCopied++;
                    }
                    catch (System.Exception ex)
                    {
                        args.Context.AddString($"  manifest '{name}': copy failed: {ex.Message}");
                    }
                }
                args.Context.AddString($"[FAP] Manifests: copied {manifestsCopied}, skipped {manifestsSkipped} (already present).");
            }
            else
            {
                args.Context.AddString($"[FAP] No manifests to import — source folder missing: {yoinkManifestsDir}");
            }

            if (bundlesCopied > 0 || manifestsCopied > 0)
            {
                args.Context.AddString("[FAP] Import complete. Re-enter the world (or restart Valheim) to load the new bundles + manifests.");
            }
            else if (bundlesSkipped > 0 || manifestsSkipped > 0)
            {
                args.Context.AddString("[FAP] Nothing new to import. Use 'fap_import_yoink force' to overwrite existing destination files.");
            }
        }

        // Format a one-line description of a yaml file for the status
        // command. Counts top-level "- " entries so the player can verify
        // their placements actually produced yaml content before pushing.
        // Every line goes through Debug.Log as well as the terminal, so an unattended runner
        // (FDT's Auto Census Commands) gets the answer in the log with nobody at the keyboard.

        // Walks every registered stand-in through the LIVE placement classifier and
        // pre-checks each spawner's real preconditions, so "hammer swings but nothing
        // placed" is answered without clicking anything. Three independent failures
        // are separated, because in game they look identical:
        //   * claimed=None   -> the prefix falls through and vanilla Instantiates the
        //                       stand-in; every stand-in is built inactive, so the copy
        //                       is invisible AND the hammer still swings.
        //   * no button      -> the piece is not in the table's piece list, so the tab
        //                       is empty (or absent) and there is nothing to click.
        //   * would-fail     -> the branch is claimed but the spawner's precondition is
        //                       already false, so a click reports "Failed to ...".
        private static void SelfTestPlace(Terminal context)
        {
            Action<string> Line = s => { if (context != null) context.AddString(s); Debug.Log(s); };

            var scene = ZNetScene.instance;
            Line($"[FAP] === placement self-test (FAP {VaPieces.PluginVersion}) ===");
            if (scene == null)
            {
                Line("[FAP] ZNetScene is null - join a world first. Nothing tested.");
                return;
            }

            var table = Player.m_localPlayer != null ? Player.m_localPlayer.m_buildPieces : null;
            if (table == null) table = FindHammerPieceTable();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            if (table != null && table.m_pieces != null)
                foreach (var go in table.m_pieces)
                    if (go != null) listed.Add(go.name);
            Line($"[FAP] hammer table: {(table != null ? table.name : "<none>")}, {listed.Count} piece(s) listed");

            // itemstand / itemstandh are the two prefabs every item-stand placement
            // needs; if the game ever renames them, every stand fails with spawned=0.
            foreach (var vanillaName in new[] { VAItemStandStandinFactory.VanillaVerticalPrefabName,
                                                VAItemStandStandinFactory.VanillaHorizontalPrefabName })
            {
                var p = scene.GetPrefab(vanillaName);
                Line($"[FAP] vanilla '{vanillaName}': {(p != null ? "present" : "*** MISSING -> every stand of this orientation fails ***")}");
            }

            int total = 0, notClaimed = 0, noButton = 0, wouldFail = 0, ok = 0;
            var examples = new List<string>();

            Action<string, GameObject, Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch> Check =
                (group, standin, expected) =>
            {
                if (standin == null) return;
                total++;

                var actual = Player_PlacePiece_VAClutterPaint_Patch.Classify(standin, out string detail);
                bool claimed = actual == expected;
                bool hasButton = listed.Contains(standin.name);

                string failure = null;
                if (!claimed)
                {
                    notClaimed++;
                    failure = $"claimed={actual} expected={expected} -> FALLS THROUGH TO VANILLA"
                            + (standin.activeSelf ? string.Empty : " (stand-in is inactive, so vanilla's copy is INVISIBLE and the hammer still swings)");
                }
                else if (!hasButton)
                {
                    noButton++;
                    failure = "claimed correctly but NOT in the table's piece list -> no button to click";
                }
                else
                {
                    string why = PlacementPrecondition(scene, actual, detail);
                    if (why != null) { wouldFail++; failure = "would fail at click: " + why; }
                    else ok++;
                }

                if (failure != null && examples.Count < 12)
                    examples.Add($"[FAP]   {group} '{standin.name}': {failure}");
            };

            foreach (var kv in VAItemStandStandinFactory.AllStandins)
                Check("item-stand", kv.Value, Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch.ItemStand);
            foreach (var kv in VAMonsterStandinFactory.AllStandins)
                Check("monster", kv.Value, Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch.Monster);
            foreach (var kv in VAClutterStandinFactory.AllStandins)
                Check("clutter", kv.Value, Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch.ClutterStandin);

            Line($"[FAP] checked {total} stand-in(s): {ok} OK, {notClaimed} fall through to vanilla, "
                 + $"{noButton} with no button, {wouldFail} would fail at click");
            if (examples.Count > 0)
            {
                Line("[FAP] first failures:");
                foreach (var e in examples) Line(e);
            }
            if (total == 0)
                Line("[FAP] no stand-ins registered at all - the factories never ran (check the load summary for an earlier throw).");
            else if (notClaimed == 0 && noButton == 0 && wouldFail == 0)
                Line("[FAP] every stand-in is claimed, listed and placeable. If placement still fails in game, the defect is downstream of this check.");
        }

        // Mirrors each spawner's own early-return conditions. Returns null when a click
        // would proceed, or the reason it would report a failure.
        private static string PlacementPrecondition(
            ZNetScene scene,
            Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch branch,
            string detail)
        {
            try
            {
                switch (branch)
                {
                    case Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch.ItemStand:
                    {
                        // detail is "<item> (V|H)"; the registry is keyed on the item alone.
                        string itemName = detail;
                        int paren = itemName != null ? itemName.LastIndexOf(" (", StringComparison.Ordinal) : -1;
                        if (paren > 0) itemName = itemName.Substring(0, paren);
                        var entry = VAItemStandRegistry.Resolve(itemName);
                        if (entry == null) return $"item '{itemName}' not in VAItemStandRegistry (vanilla-friendly filter?)";
                        if (entry.Prefab == null) return $"item '{itemName}' resolves to a null prefab";
                        return null;
                    }

                    case Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch.Monster:
                    {
                        if (string.IsNullOrEmpty(detail)) return "creature name did not resolve from the stand-in name";
                        if (scene.GetPrefab(detail) == null) return $"creature '{detail}' not in ZNetScene";
                        return null;
                    }

                    case Player_PlacePiece_VAClutterPaint_Patch.PlaceBranch.ClutterStandin:
                    {
                        if (string.IsNullOrEmpty(detail)) return "real clutter name did not resolve from the stand-in name";
                        if (VAClutterRegistry.ResolvePrefab(detail) == null)
                            return $"clutter '{detail}' not in ClutterSystem";
                        return null;
                    }

                    default:
                        return null;
                }
            }
            catch (Exception ex) { return "precondition check threw: " + ex.Message; }
        }
        private static void DumpCategories(Terminal context)
        {
            void Line(string text)
            {
                context?.AddString(text);
                Debug.Log($"{VaPieces.PluginName}: {text}");
            }

            int runtimeMax = VAPieceCategoryRuntime.Max;
            int compiledMax = (int)Piece.PieceCategory.Max;
            Line($"[FAP] PieceCategory.Max runtime={runtimeMax} compiled={compiledMax}"
                 + (runtimeMax == compiledMax ? " (match)" : " ** MISMATCH — built against a different game version **"));

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Line("[FAP] no local player — run this in a world.");
                return;
            }

            // m_buildPieces is only set while a build tool is HELD, which makes this check
            // impossible to run unattended (no input injection on the perf rig). Fall back to
            // the Hammer's own PieceTable and populate it the same way equipping would, so the
            // answer does not depend on anyone pressing a key.
            var table = player.m_buildPieces;
            string source = "equipped build tool";
            if (table == null)
            {
                table = FindHammerPieceTable();
                source = "Hammer prefab (nothing equipped)";
                if (table != null)
                {
                    try
                    {
                        table.UpdateAvailable(player.m_knownRecipes, player, false, false);
                    }
                    catch (Exception ex)
                    {
                        Line($"[FAP] UpdateAvailable on the hammer table threw: {ex.Message}");
                    }
                }
            }

            if (table == null)
            {
                Line("[FAP] no build table found — equip a hammer and run this again.");
                return;
            }
            Line($"[FAP] source: {source}");

            var byCategory = FiresCore.Bridge.PieceTableAccess.ByCategory(table);
            if (byCategory == null)
            {
                Line("[FAP] m_availablePiecesByCategory did not resolve.");
                return;
            }

            Line($"[FAP] table '{table.name}' declares {(table.m_categories != null ? table.m_categories.Count : 0)} category(ies), "
                 + $"{byCategory.Count} slot(s), {table.m_pieces?.Count ?? 0} piece(s) total");

            // Valheim 1.0's menu reads the FLAT m_availablePieces set, while the per-slot counts
            // below come from m_availablePiecesByCategory. Those are two collections, and a prune
            // that hit one and missed the other would look like healthy tabs full of pieces that
            // cannot be selected. Printing both makes that disagreement impossible to miss
            // instead of something the next reader has to suspect.
            int perCategoryTotal = 0;
            foreach (var list in byCategory) perCategoryTotal += list?.Count ?? 0;
            int flat = table.m_availablePieces?.Count ?? 0;
            int enabled = table.m_enabledPieces?.Count ?? 0;

            // These three are NOT expected to be equal, and comparing them naively produced a false
            // DISAGREE. Vanilla UpdateAvailable (REF1.0 PieceTable.cs:43-92) makes them differ by
            // design, in two ways worth reading off separately:
            //   * a piece whose recipe is UNLEARNED lands in m_enabledPieces and then `continue`s, so
            //     it never reaches m_availablePieces. enabled - flat = unlearned, not a defect.
            //   * a piece with m_category == All is filed into ALL NINE vanilla slots, so it counts
            //     once in the flat set and nine times in the per-category total: +8 each.
            // Only a residual after accounting for both is a real one-sided prune.
            int allCategoryPieces = 0;
            if (table.m_availablePieces != null)
                foreach (var piece in table.m_availablePieces)
                    if (piece != null && piece.m_category == Piece.PieceCategory.All) allCategoryPieces++;

            int expectedPerCategory = flat + (allCategoryPieces * (VanillaAllCategoryFanout - 1));
            int residual = perCategoryTotal - expectedPerCategory;

            Line($"[FAP] collections: m_availablePieces={flat} (flat set the 1.0 menu reads), "
                 + $"m_enabledPieces={enabled}, per-category total={perCategoryTotal}");
            Line($"[FAP]   accounted: {enabled - flat} unlearned recipe(s) (enabled but not available), "
                 + $"{allCategoryPieces} All-category piece(s) filed into {VanillaAllCategoryFanout} slots each (+{allCategoryPieces * (VanillaAllCategoryFanout - 1)})"
                 + (residual == 0
                     ? "  -> consistent"
                     : $"   <-- {Math.Abs(residual)} piece-slot(s) UNACCOUNTED: a prune hit one collection and not the other"));
            int listed = table.m_pieces?.Count ?? 0;
            if (listed > enabled)
                Line($"[FAP]   {listed - enabled} piece(s) on the table are not enabled at all (Piece.m_enabled false), so vanilla skips them before either set.");

            // The gate that decides whether FAP's own pieces are pruned out of the menu at all.
            // AdminOnlyMode couples them to debugmode, so an admin with debugmode off sees the
            // PLAYER view and every FAP piece is filtered — which looks exactly like "the tab is
            // there but nothing in it works".
            bool isAdmin;
            try { isAdmin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); } catch { isAdmin = false; }
            bool adminOnly = ConfigManager.Instance?.configAdminOnlyMode?.Value ?? true;
            bool asPlayer = VerdantsAscent.Patches.VABuildMenuVisibility.ViewingAsPlayer();
            Line($"[FAP] gate: isAdmin={isAdmin} AdminOnlyMode={adminOnly} debugmode={VerdantsAscent.Patches.VABuildMenuVisibility.IsDebugModeActive()} "
                 + $"-> view={(asPlayer ? "PLAYER (FAP pieces pruned)" : "ADMIN (FAP pieces shown)")}");
            bool vanillaFriendlyConfig = ConfigManager.Instance?.configVanillaFriendly?.Value ?? false;
            bool clientOnly = VAFapServerPresence.IsClientOnly;
            Line($"[FAP] vanillaFriendly={vanillaFriendlyConfig} clientOnly={clientOnly} "
                 + $"(server runs FAP: {(VAFapServerPresence.ServerRunsFap ? "yes" : clientOnly ? "no" : "not decided")}) "
                 + $"-> menu is vanilla-friendly: {vanillaFriendlyConfig || clientOnly}");

            // A category can hold pieces and still have NO TAB, because the tab strip is built
            // from m_categories. That is a different failure from "the tab is empty" and the two
            // were indistinguishable before.
            var declared = new HashSet<int>();
            if (table.m_categories != null)
                foreach (var c in table.m_categories) declared.Add((int)c);

            // PUBLISHED tabs are the only ones a player can click, so they are what the counters
            // below report. Counting registered CATEGORIES instead made the summary contradict its
            // own table once empty tabs started being pruned: every row read tab=NO while the
            // summary still claimed 25 empty tabs, and "empty: 0" could never be reached however
            // well the prune worked. A registered category with no published tab is normal now.
            int fapPublished = 0, fapPublishedEmpty = 0, fapUnpublished = 0, orphanedPieces = 0;
            for (int i = 0; i < byCategory.Count; i++)
            {
                string owner = VerdantsAscent.Patches.VACategoryRegistry.TryGetName((Piece.PieceCategory)i, out var tabName)
                    ? $"FAP '{tabName}'"
                    : (i < runtimeMax ? $"vanilla {(Piece.PieceCategory)i}" : "unclaimed");
                int count = byCategory[i]?.Count ?? 0;
                bool hasTab = declared.Contains(i);

                if (owner.StartsWith("FAP", StringComparison.Ordinal))
                {
                    if (hasTab) { fapPublished++; if (count == 0) fapPublishedEmpty++; }
                    else fapUnpublished++;
                }

                // Pieces in a category with no tab are unreachable in the menu. Pruning cannot fix
                // this one - it is the inverse of the empty-tab bug and needs a tab or a recategorise.
                if (!hasTab && count > 0) orphanedPieces += count;

                string flag = !hasTab && count > 0 ? "   <-- PIECES BUT NO TAB"
                            : count == 0 ? "   <-- EMPTY"
                            : string.Empty;
                Line($"[FAP]   [{i,2}] {owner,-34} {count,5} piece(s)  tab={(hasTab ? "yes" : "NO ")}{flag}");
            }

            Line($"[FAP] FAP tabs: {fapPublished} published, of which empty: {fapPublishedEmpty}"
                 + $"; {fapUnpublished} registered but not published (expected - empty tabs are pruned)"
                 + (fapPublished > 0 && fapPublishedEmpty == fapPublished ? "  ** ALL PUBLISHED FAP TABS EMPTY **" : string.Empty));
            if (orphanedPieces > 0)
                Line($"[FAP] {orphanedPieces} piece(s) sit in a category with no tab and cannot be reached from the menu.");

            // The other half of the inlined-constant bug: a pre-patch build hides pieces by stamping
            // the OLD Max, which the patched game reads as a real category.
            if (compiledMax < runtimeMax && compiledMax < byCategory.Count)
            {
                int leaked = byCategory[compiledMax]?.Count ?? 0;
                Line($"[FAP] slot {compiledMax} ({(Piece.PieceCategory)compiledMax}) holds {leaked} piece(s) — "
                     + "anything FAP meant to hide would land here on a build compiled before the category shift.");
            }
        }

        // The Hammer's PieceTable, straight off the item prefab, so the category dump works with
        // nothing equipped. Falls back to any item that carries a build table if the Hammer has
        // been renamed or replaced.
        private static PieceTable FindHammerPieceTable()
        {
            var odb = ObjectDB.instance;
            if (odb == null) return null;

            var hammer = odb.GetItemPrefab("Hammer");
            var drop = hammer != null ? hammer.GetComponent<ItemDrop>() : null;
            var table = drop?.m_itemData?.m_shared?.m_buildPieces;
            if (table != null) return table;

            foreach (var item in odb.m_items)
            {
                if (item == null) continue;
                var itemDrop = item.GetComponent<ItemDrop>();
                var candidate = itemDrop?.m_itemData?.m_shared?.m_buildPieces;
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static string DescribeFile(string label, string path)
        {
            if (!File.Exists(path))
                return $"  {label}: (none yet) {path}";
            try
            {
                string text = File.ReadAllText(path);
                int entries = 0;
                foreach (var line in text.Split('\n'))
                {
                    if (line.StartsWith("- ", System.StringComparison.Ordinal)) entries++;
                }
                return $"  {label}: {entries} entries, {text.Length} chars  {path}";
            }
            catch (System.Exception ex)
            {
                return $"  {label}: read error: {ex.Message}";
            }
        }

        private static void ReportOrphanGroups(
            Terminal.ConsoleEventArgs args,
            System.Collections.Generic.List<VAFapOrphanZdoScan.OrphanGroup> groups)
        {
            int totalZdos = 0;
            int namedGroups = 0;
            foreach (var group in groups)
            {
                totalZdos += group.ZdoCount;
                if (group.IsNamed) namedGroups++;
            }

            args.Context.AddString($"[FAP] {groups.Count} orphaned prefab(s), {totalZdos} ZDO(s):");
            foreach (var group in groups)
            {
                var position = group.SamplePosition;
                args.Context.AddString($"  {(group.IsNamed ? group.PrefabName : "<unknown prefab>")}"
                    + $"  hash={group.PrefabHash}  {group.ZdoCount} ZDO(s)"
                    + $"  near ({position.x:F0}, {position.y:F0}, {position.z:F0})");
            }
            args.Context.AddString($"[FAP] 'fap_orphan_zdo purge' would delete the {namedGroups} FAP prefab(s) named above. "
                + "Use 'purge <hash>' for one prefab, or 'purge all' for every orphan.");
        }

        // Null means the caller already reported why nothing will be purged.
        private static System.Collections.Generic.List<int> SelectOrphanHashes(
            Terminal.ConsoleEventArgs args,
            System.Collections.Generic.List<VAFapOrphanZdoScan.OrphanGroup> groups,
            string target)
        {
            var hashes = new System.Collections.Generic.List<int>();

            if (!string.IsNullOrEmpty(target) && !string.Equals(target, "all", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(target, out int requestedHash))
                {
                    args.Context.AddString($"[FAP] '{target}' is not a prefab hash. Pass a hash from the report, or 'all'.");
                    return null;
                }
                foreach (var group in groups)
                {
                    if (group.PrefabHash == requestedHash) hashes.Add(requestedHash);
                }
                if (hashes.Count == 0)
                    args.Context.AddString($"[FAP] No orphaned ZDOs with hash {requestedHash}.");
                return hashes.Count == 0 ? null : hashes;
            }

            bool purgeEverything = string.Equals(target, "all", System.StringComparison.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (purgeEverything || group.IsNamed) hashes.Add(group.PrefabHash);
            }

            if (hashes.Count == 0)
            {
                args.Context.AddString("[FAP] Nothing purged: every orphan belongs to a prefab FAP cannot name. "
                    + "They are usually another mod's objects, so name a hash explicitly or use 'purge all'.");
                return null;
            }
            return hashes;
        }

        private static bool IsLocalAdminForWorldEdit()
        {
            try { return FiresCore.Sync.AdminSyncing.IsLocalAdmin(); }
            catch { return false; }
        }
    }
}
