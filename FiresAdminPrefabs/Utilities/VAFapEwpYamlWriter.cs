using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // VAFapEwpYamlWriter — writes the two shared static YAML files that hand
    // persistence off to ExpandWorldPrefabs (EWP): one universal destroy→spawn
    // rule (expand_prefabs_FAP.yaml) plus its data entry (data_FAP.yaml). Each
    // effect-eligible placement stamps FAP_persist=1; EWP's <prefab> template
    // respawns whatever was destroyed, so one rule covers every tagged prefab.
    public static class VAFapEwpYamlWriter
    {
        // Curated list of vanilla prefab names that should receive
        // EWP-persistence treatment alongside the auto-detected effect
        // prefabs. These auto-destroy via mechanisms other than
        // TimedDestruction (script death, projectile impact, AOE
        // consumption) and the user wants them to respawn.
        //
        // Maintained by us as we discover new vanilla offenders.
        // Players can extend via configCustomPersistentPrefabs.
        // Names are case-sensitive — must match ZNetScene's prefab name.
        private static readonly HashSet<string> _curatedPersistentPrefabs =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "lavabomb_rock1",
            };

        private static HashSet<string> _customPersistentSet;
        private static string _customPersistentRaw;

        // Returns true if the prefab name appears on EITHER the curated
        // hardcoded list OR the user's configCustomPersistentPrefabs
        // config. Extends effect-eligibility to specific vanilla prefabs
        // by name.
        public static bool IsCustomPersistent(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (_curatedPersistentPrefabs.Contains(prefabName)) return true;

            var cfg = ConfigManager.Instance?.configCustomPersistentPrefabs;
            var raw = cfg?.Value ?? string.Empty;
            if (_customPersistentSet == null || _customPersistentRaw != raw)
            {
                _customPersistentRaw = raw;
                _customPersistentSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var piece in raw.Split(','))
                {
                    var trimmed = piece.Trim();
                    if (trimmed.Length > 0) _customPersistentSet.Add(trimmed);
                }
            }
            return _customPersistentSet.Contains(prefabName);
        }

        // Output paths. Resolved lazily so unit tests / non-game contexts
        // can still load the class without crashing on BepInEx.Paths.
        // Both files are SHARED across admins — universal content, no
        // per-admin variation to isolate.
        private static string ExpandWorldDir =>
            Path.Combine(Paths.ConfigPath, "expand_world");
        private static string DataDir =>
            Path.Combine(Paths.ConfigPath, "data");
        private static string ScriptsFilePath =>
            Path.Combine(ExpandWorldDir, "expand_prefabs_FAP.yaml");
        private static string DataFilePath =>
            Path.Combine(DataDir, "data_FAP.yaml");

        // Shared data-entry name the universal spawn rule references.
        // EWP looks this up in data_FAP.yaml and applies its fields
        // (FAP_persist=1) when the rule fires.
        public const string SharedDataName = "FAP_Respawn";

        // The three ZDO markers FAP stamps, all ints so EWP's "int, key, value" filter
        // syntax works on each:
        //
        //   FAP_persist        what the universal destroy→spawn rule filters on. Only
        //                      effect-eligible prefabs get it; solid props already
        //                      persist through WearNTear.
        //   fap_piece          on every FAP placement, so a rule can target "anything
        //                      anyone placed".
        //   fap_<playerID>     per-player attribution, for "respawn everything player X
        //                      placed" or "wipe player Y's placements after a ban".
        public const string PersistMarkerKey = "FAP_persist";
        public const string PiecePlacedMarkerKey = "fap_piece";
        public const string OwnerMarkerKeyPrefix = "fap_";

        // Top-level matcher in the universal rule: every prefab, narrowed by the
        // FAP_persist filter.
        //
        // The quotes are mandatory. A bare `*` opens a YAML alias node, so `prefab: *`
        // fails to parse and takes the WHOLE rule file with it.
        //
        // This was `ZNetView` until 1.1.111, described here as a catchall. It is not one.
        // EWP resolves a rule's prefab through PrefabHelper.ParsePrefabs: wildcard forms,
        // then ZNetScene.m_namedPrefabs, then a value group, else
        // Log.Warning("Failed to resolve prefab") and null. ZNetView is a component type
        // and never a prefab name, so the rule registered against ZERO prefab keys and
        // the universal persist rule NEVER FIRED on any rig that had EWP. The singular
        // `filter:` bug fixed in 1.1.104 was a second defect on a rule that was already
        // unreachable, which is why neither ever showed symptoms.
        private const string UniversalMatcherPrefab = "\"*\"";

        // The unreachable form, still on disk wherever FAP wrote it. Triggers a rewrite.
        private const string LegacyUniversalMatcherPrefab = "ZNetView";

        // EWP template variable that resolves to the destroyed prefab's
        // name in the spawn step. One rule respawns whatever was
        // destroyed instead of needing per-prefab entries.
        private const string SpawnPrefabTemplate = "<prefab>";

        // Session-level guard. Once EnsureSharedEntriesOnLoad has run
        // successfully this flag short-circuits re-entry — the files
        // don't change after first write.
        private static bool _sharedEntriesEnsured;
        private static readonly object _ioLock = new object();

        // ──────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────

        // Called once at mod load (from VaPieces.FinishInit) on whichever
        // side runs FAP — client, host, or dedicated server. Writes both
        // yaml files if they don't already contain our shared entries.
        // Idempotent: re-running is a no-op once the files are on disk.
        //
        // No config gating here on purpose — the files are tiny + static,
        // and writing them defensively means a config-flip later doesn't
        // require a restart. EWP only acts on ZDOs that carry the
        // FAP_persist marker, and we only stamp that marker when the
        // configs are on (see RegisterPlacement), so unused files are
        // harmless.
        public static void EnsureSharedEntriesOnLoad()
        {
            lock (_ioLock)
            {
                if (_sharedEntriesEnsured) return;
                try
                {
                    EnsureDir(ExpandWorldDir);
                    EnsureDir(DataDir);
                    bool wroteData = EnsureSharedDataEntry();
                    bool wroteScript = EnsureSharedScriptEntry();
                    _sharedEntriesEnsured = true;
                    if (wroteData || wroteScript)
                        Debug.Log($"{VaPieces.PluginName}: VAFapEwpYamlWriter wrote shared EWP entries (data={wroteData}, script={wroteScript}).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapEwpYamlWriter EnsureSharedEntriesOnLoad failed: {ex.Message}");
                }
            }
        }

        // Stamps the placed ZDO with FAP's markers. Called only from the client
        // placement postfix — deliberately the ONLY path that writes them. A
        // server-side auto-tagger was tried and removed: Piece.Awake fires for every
        // pre-existing piece on zone load, so it stamped FAP_persist onto every wet
        // effect and vfx the world already had. Pieces placed by non-FAP clients simply
        // carry no markers.
        //
        //   fap_piece = 1       always
        //   fap_<playerID> = 1  always, for "wipe everything player X placed"
        //   FAP_persist = 1     only for effect-eligible prefabs, and only while the
        //                       EWP integration is on — it drives the respawn rule
        //
        // The first two are unconditional regardless of vanilla-friendly mode: they're
        // what the FX clear brush and the admin tooling key off.
        //
        // Static-physics suppression isn't here — HammerTogglePatches owns it, and its
        // direct field toggle is already vanilla-compatible, since the owner never
        // writes a fall-update back to the ZDO.
        public static void RegisterPlacement(GameObject placedRoot)
        {
            if (placedRoot == null) return;
            var cfg = ConfigManager.Instance;

            var nview = placedRoot.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            // Skip if already tagged (handles double-fire of SetCreator
            // postfix across patch chains).
            if (zdo.GetInt(PiecePlacedMarkerKey, 0) != 0) return;

            zdo.Set(PiecePlacedMarkerKey, 1);

            long localId = Player.m_localPlayer != null
                ? Player.m_localPlayer.GetPlayerID()
                : 0L;
            if (localId != 0L)
                zdo.Set(OwnerMarkerKeyPrefix + localId, 1);

            // FAP_persist is per-prefab opt-in: the player right-clicks an FX/VFX piece
            // and picks "Place with Persistence" before placing. Three gates — the EWP
            // integration on, the piece in CatFX/CatVFX as a sanity guard, and that
            // opt-in.
            //
            // Earlier versions inferred it from category, name prefix and a
            // TimedDestruction subtree scan. That was far too broad: rain wetness,
            // draugr glow and troll-smash dust all carry TimedDestruction somewhere, so
            // they inherited respawn nobody asked for. (It also never actually gated
            // anything, because the YAML rule of the day used `data:` + `injectData:`
            // where it needed `filter:`, and fired on every destroy in the world.)
            string prefabKey = StripCloneSuffix(placedRoot.name);
            bool inFxTab = IsInEffectCategory(placedRoot);
            bool userOptedIn = VAFapPersistentOptIn.IsOptedIn(prefabKey);
            bool wroteRespawn = false;
            // The FAP_persist marker is what EWP's universal respawn
            // rule filters on, so it's only meaningful in EWP-deferred
            // mode. Vanilla-friendly is an ORTHOGONAL feature — it
            // controls which TABS surface modded content. EWP
            // persistence works the same regardless of vanilla-
            // friendly state: with vanilla-friendly OFF the admin
            // sees modded effects in their hammer and EWP still
            // respawns the ones they tagged. With vanilla-friendly
            // ON the modded tabs hide but EWP still respawns the
            // tagged effects so any non-FAP teammates see them too.
            if (inFxTab
                && userOptedIn
                && VerdantsAscent.Utilities.VAEwpSoftDep.IsActive)
            {
                zdo.Set(PersistMarkerKey, 1);
                wroteRespawn = true;
            }

            if (cfg.configVerboseLogging.Value)
            {
                string prefabName = StripCloneSuffix(placedRoot.name);
                Debug.Log($"{VaPieces.PluginName}: Tagged '{prefabName}' — fap_piece=1, fap_{localId}=1, FAP_persist={(wroteRespawn ? 1 : 0)}.");
            }
        }

        // Returns true if the placed Piece's hammer category is one of
        // our FX-tab categories (CatFX or CatVFX). The category lives
        // on the Piece component as m_category (a PieceCategory enum
        // value) — we resolve our registered FX / VFX category values
        // via VACategoryRegistry and equality-check. Falls open (false)
        // when the prefab has no Piece component, since "no Piece" can't
        // have been placed via a hammer tab.
        //
        // This is the gate that keeps FAP_persist scoped strictly to
        // hammer-FX-tab picks. Without it any prefab with a transient
        // sub-effect (TimedDestruction in subtree, rain wetness, etc.)
        // would get tagged for respawn.
        private static bool IsInEffectCategory(GameObject placedRoot)
        {
            if (placedRoot == null) return false;
            var piece = placedRoot.GetComponent<Piece>();
            if (piece == null) return false;

            var cat = piece.m_category;
            if (VerdantsAscent.Patches.VACategoryRegistry.TryGet(
                    VerdantsAscent.Patches.VACategoryRegistry.CatFX, out var fxCat)
                && cat == fxCat)
            {
                return true;
            }
            if (VerdantsAscent.Patches.VACategoryRegistry.TryGet(
                    VerdantsAscent.Patches.VACategoryRegistry.CatVFX, out var vfxCat)
                && cat == vfxCat)
            {
                return true;
            }
            return false;
        }

        // Returns the absolute paths of the two yaml files. Used by
        // diagnostic console commands (fap_ewpstatus, fap_wipeewp).
        public static (string ScriptsPath, string DataPath) GetFilePaths()
            => (ScriptsFilePath, DataFilePath);

        // ──────────────────────────────────────────────────────────────
        //  Maintenance: clean stale entries (migration from older versions)
        // ──────────────────────────────────────────────────────────────

        // Sweeps the scripts file and drops any per-prefab entries left
        // over from older FAP versions that wrote a destroy rule per
        // prefab. With universal rules there's only one entry to keep
        // (- prefab: ZNetView). Returns (scriptsKept, scriptsDropped,
        // dataKept, dataDropped) for the console command to echo.
        // dataKept/dataDropped are always 0/0 — data_FAP.yaml carries
        // only the one FAP_Respawn entry, never stale.
        //
        // Atomic write — temp file + replace so a crash mid-clean
        // doesn't truncate the yaml.
        public static (int ScriptsKept, int ScriptsDropped, int DataKept, int DataDropped)
            CleanStaleEntries()
        {
            int sk = 0, sd = 0, dk = 0, dd = 0;
            lock (_ioLock)
            {
                if (File.Exists(ScriptsFilePath))
                    (sk, sd) = CleanScriptsFile();
                _sharedEntriesEnsured = false;
            }
            return (sk, sd, dk, dd);
        }

        private static (int kept, int dropped) CleanScriptsFile()
        {
            string content = File.ReadAllText(ScriptsFilePath, Encoding.UTF8);
            var (header, entries) = SplitHeaderAndEntries(content);

            int kept = 0, dropped = 0;
            bool sawUniversal = false;
            var keepList = new List<string>();
            foreach (var entry in entries)
            {
                int newline = entry.IndexOf('\n');
                string firstLine = (newline >= 0 ? entry.Substring(0, newline) : entry).Trim();
                if (firstLine == "- prefab: " + UniversalMatcherPrefab
                    || firstLine == "- prefab: " + LegacyUniversalMatcherPrefab)
                {
                    if (sawUniversal)
                    {
                        dropped++;
                    }
                    else
                    {
                        sawUniversal = true;
                        keepList.Add(entry);
                        kept++;
                    }
                }
                else
                {
                    dropped++;
                }
            }

            var sb = new StringBuilder();
            sb.Append(header);
            if (header.Length > 0 && !header.EndsWith("\n"))
                sb.AppendLine();
            foreach (var entry in keepList)
            {
                sb.Append(entry);
                if (!entry.EndsWith("\n")) sb.AppendLine();
            }
            AtomicWrite(ScriptsFilePath, sb.ToString(), VAEwpYamlValidator.FileKind.Rules);
            return (kept, dropped);
        }

        private static (string header, List<string> entries) SplitHeaderAndEntries(string content)
        {
            var entries = new List<string>();
            if (string.IsNullOrEmpty(content)) return (string.Empty, entries);

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var headerSb = new StringBuilder();
            StringBuilder current = null;
            bool inEntries = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    inEntries = true;
                    if (current != null && current.Length > 0)
                        entries.Add(current.ToString().TrimEnd('\r', '\n', ' ', '\t') + "\n");
                    current = new StringBuilder();
                    current.AppendLine(line);
                }
                else if (inEntries)
                {
                    current.AppendLine(line);
                }
                else
                {
                    headerSb.AppendLine(line);
                }
            }
            if (current != null && current.Length > 0)
                entries.Add(current.ToString().TrimEnd('\r', '\n', ' ', '\t') + "\n");
            return (headerSb.ToString(), entries);
        }

        // ──────────────────────────────────────────────────────────────
        //  Maintenance: wipe local files
        // ──────────────────────────────────────────────────────────────

        // Deletes both yaml files and resets the session guard. Returns
        // true if at least one file existed. Used by fap_wipeewp to
        // start fresh.
        public static bool WipeLocalFiles()
        {
            bool deleted = false;
            lock (_ioLock)
            {
                try
                {
                    if (File.Exists(ScriptsFilePath))
                    {
                        File.Delete(ScriptsFilePath);
                        deleted = true;
                    }
                    if (File.Exists(DataFilePath))
                    {
                        File.Delete(DataFilePath);
                        deleted = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: WipeLocalFiles failed: {ex.Message}");
                }
                _sharedEntriesEnsured = false;
            }
            return deleted;
        }

        // Atomic write — temp file + delete-then-move so a mid-write
        // crash doesn't leave a truncated yaml on disk.
        private static void AtomicWrite(string finalPath, string content, VAEwpYamlValidator.FileKind kind)
        {
            string tmpPath = finalPath + ".tmp";
            File.WriteAllText(tmpPath, content, Encoding.UTF8);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);
            VAEwpYamlValidator.Validate(finalPath, kind);
        }

        // ──────────────────────────────────────────────────────────────
        //  YAML emission
        // ──────────────────────────────────────────────────────────────

        // Writes the single universal destroy→spawn rule. Idempotent for the current
        // form, and rewrites the file outright if it finds the legacy one.
        //
        // That legacy form used `data: int, FAP_persist, 1` with `injectData: true`,
        // which reads like a filter but isn't — EWP's `data:` is what to INJECT when a
        // rule fires. With no filter the rule matched every ZNetView destruction in the
        // world and injected the tag into the dying ZDO just before respawning it, so
        // ambient vanilla FX entered the loop too.
        //
        // Its replacement, `filter:`, was ALSO not a filter. EWP's top-level schema has
        // only `filters:` (a YAML list) and `filterLimit` — there is no singular field,
        // so it was dropped silently and the rule went on matching every destroy. Both
        // broken forms are migrated below. Verified against ExpandWorldPrefabsREF
        // (EWP 1.52.0) `Prefab\Data.cs:131` `public string[]? filters;`.
        // A top-level `filter:` in any value form. EWP's rule schema has only `filters:`
        // (a list) and `filterLimit`, so the singular key never filtered anything.
        private static bool HasSingularFilterKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("  filter:", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool EnsureSharedScriptEntry()
        {
            string current = File.Exists(ScriptsFilePath)
                ? File.ReadAllText(ScriptsFilePath)
                : string.Empty;

            // Tell the admin when what is already on disk is invalid, before we decide
            // whether we recognise it. The targeted patterns below only know the forms
            // FAP itself wrote; this reports anything else EWP will silently ignore.
            VAEwpYamlValidator.Validate(ScriptsFilePath, VAEwpYamlValidator.FileKind.Rules);

            // Detect the legacy broken form. If present, blow away the
            // file (we wrote it; we own its content) and rewrite from
            // scratch with the correct filter-based form.
            //
            // The singular-`filter:` test matches the KEY only. It used to spell out
            // `filter: int, FAP_persist, 1`, which missed every file where the value was
            // quoted — and FAP wrote both forms, so on one profile that left six dead
            // rules in place with the migration reporting nothing.
            bool hasLegacyBrokenRule = current.Contains("injectData: true")
                || current.Contains("data: int, " + PersistMarkerKey + ", 1")
                || HasSingularFilterKey(current)
                || current.Contains("- prefab: " + LegacyUniversalMatcherPrefab);

            if (hasLegacyBrokenRule)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: Detected legacy broken EWP rule in '{ScriptsFilePath}' "
                    + "(a data:+injectData or singular filter: form, neither of which EWP treats as a filter, so the "
                    + "rule fired on every destroy). Rewriting with the filters: list form.");
                try { File.Delete(ScriptsFilePath); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: Could not delete legacy rule file ({ex.Message}); appending new rule instead. "
                        + "Manual cleanup may be required.");
                }
                current = string.Empty;
            }

            // Idempotent: if the corrected rule is already present, no-op.
            // Match signature includes BOTH the filter line and the spawn
            // data reference so a hand-edit can't collide with us.
            string expectedFilterEntry = "  - int, " + PersistMarkerKey + ", 1";
            if (current.Contains("- prefab: " + UniversalMatcherPrefab)
                && current.Contains(expectedFilterEntry)
                && current.Contains("data: " + SharedDataName))
                return false;

            var sb = new StringBuilder();
            if (!File.Exists(ScriptsFilePath))
            {
                sb.AppendLine("# expand_prefabs_FAP.yaml");
                sb.AppendLine("# Auto-generated by FiresAdminPrefabs (FAP).");
                sb.AppendLine("# One universal destroy→spawn rule: respawns prefabs whose ZDO");
                sb.AppendLine($"# carries the '{PersistMarkerKey}=1' marker. The marker is only");
                sb.AppendLine("# written by FAP when the user explicitly opted-in via right-click");
                sb.AppendLine("# 'Place with Persistence' on the FX/VFX hammer piece BEFORE");
                sb.AppendLine("# placing. EWP's <prefab> template resolves to the destroyed");
                sb.AppendLine("# prefab's name. The respawn data preset re-applies the marker");
                sb.AppendLine("# so the respawned ZDO stays in the loop.");
                sb.AppendLine();
            }
            sb.AppendLine("- prefab: " + UniversalMatcherPrefab);
            sb.AppendLine("  type: destroy");
            sb.AppendLine("  filters:");
            sb.AppendLine(expectedFilterEntry);
            sb.AppendLine("  spawn:");
            sb.AppendLine("    - prefab: " + SpawnPrefabTemplate);
            sb.AppendLine("      data: " + SharedDataName);
            sb.AppendLine();
            File.AppendAllText(ScriptsFilePath, sb.ToString());
            return true;
        }

        // Write the single shared data entry to data_FAP.yaml. Just
        // re-applies the FAP_persist marker so the respawned ZDO stays
        // in the respawn loop. Idempotent: returns true on first write,
        // false if entry already exists.
        private static bool EnsureSharedDataEntry()
        {
            if (File.Exists(DataFilePath))
            {
                string existing = File.ReadAllText(DataFilePath);
                if (existing.Contains("- name: " + SharedDataName))
                    return false;
            }

            var sb = new StringBuilder();
            if (!File.Exists(DataFilePath))
            {
                sb.AppendLine("# data_FAP.yaml");
                sb.AppendLine("# Auto-generated by FiresAdminPrefabs (FAP).");
                sb.AppendLine($"# Single shared data entry '{SharedDataName}' that the");
                sb.AppendLine("# universal spawn rule in expand_prefabs_FAP.yaml references.");
                sb.AppendLine("# Re-applies the FAP_persist marker so respawned pieces stay");
                sb.AppendLine("# in the EWP respawn loop.");
                sb.AppendLine();
            }
            sb.AppendLine("- name: " + SharedDataName);
            sb.AppendLine("  ints:");
            sb.AppendLine("    - " + PersistMarkerKey + ", 1");
            sb.AppendLine();
            File.AppendAllText(DataFilePath, sb.ToString());
            VAEwpYamlValidator.Validate(DataFilePath, VAEwpYamlValidator.FileKind.Data);
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        //  Small helpers
        // ──────────────────────────────────────────────────────────────

        private static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // Strip Unity's "(Clone)" suffix from GameObject names so log
        // messages reference the un-suffixed prefab name.
        private static string StripCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string suffix = "(Clone)";
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - suffix.Length);
            return name;
        }
    }
}
