using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using VerdantsAscent.Patches;
using VerdantsAscent.Utilities;
using Debug = UnityEngine.Debug;
using FiresCore.Bridge;

namespace VerdantsAscent.Managers
{
    // Runtime-attaches a Piece component to ZNetView-bearing prefabs that
    // aren't on any hammer table and registers them with the Hammer PieceTable
    // (the Jotunn-free equivalent of MoreVanillaBuildPrefabs). Forces
    // m_clipGround + m_clipEverything on injected pieces so the placement ghost
    // takes the raycast-clip path, which works for sparse-collider props that
    // the collider-driven hover path can't place.
    public static class VAVanillaPieceManager
    {
        // Pieces we injected. Keyed by stable hash (matches ZNetScene's
        // own naming key) so we can answer "is this our piece?" cheaply
        // from inside hot patches.
        private static readonly HashSet<int> _injectedPrefabHashes = new HashSet<int>();
        private static readonly Dictionary<string, GameObject> _eligibleByName = new Dictionary<string, GameObject>();

        // Read-only accessor for cross-file consumers (e.g. icon re-queue after
        // a cache wipe). IEnumerable, not the dict, so callers can't mutate it.
        public static IEnumerable<KeyValuePair<string, GameObject>> EligiblePieces => _eligibleByName;
        private static bool _initialized;

        // Lets the admin-status watcher tell a promotion that actually took
        // from one where the gate flapped shut again mid-init (InitializeOnce
        // clears _initialized on every failure path), so it knows whether to
        // stop watching or keep going.
        public static bool IsInitialized => _initialized;

        // True while the heavy init coroutine is running (vs _initialized =
        // fully done), so re-entrant calls skip instead of queuing duplicate
        // work. The coroutine clears it and sets _initialized at its tail.
        private static bool _initializing;

        // True while the init coroutine is still building stand-ins. The per-category
        // lists are half-filled during that window, so a menu pass must not read an
        // empty list as "this tab has no content".
        internal static bool IsInitializing => _initializing;

        // Set once the non-admin registration pass has run. _initialized stays false through it,
        // so a later promotion still gets the full admin build.
        private static bool _registeredForNonAdmins;

        // False while the admin gate has kept every FAP piece off this client's hammer: no build, no build under way,
        // no non-admin registration.
        internal static bool HasBuiltHammerMenu => _initialized || _initializing || _registeredForNonAdmins;

        // Frame budget for the heavy InitializeOnce coroutine: after this many ms of work it
        // yields to Unity so the game stays smooth while the hammer populates. Kept small
        // (well under a 60fps frame) so this runs in the background after spawn WITHOUT visible
        // stutter - the old 100ms budget meant a deliberate ~10fps floor for the whole scan,
        // which read as "the hammer keeps hanging the main thread." The scan just takes more
        // (smooth) frames now; total wall-clock is similar.
        private const long InitFrameBudgetMs = 8;

        // Timeout for the post-spawn late RefreshClutterStandins schedule.
        private const float LateClutterRefreshTimeoutSeconds = 600f;

        // Minimum squared bounds size before procedural snap points are worth
        // generating - below this the prefab has effectively no extent.
        private const float MinSnapBoundsSqrSize = 0.001f;

        // Minimum vertical half-extent before top-face snap points are emitted;
        // keeps ground-flat decals (rugs, plates) from getting redundant snaps.
        private const float TopSnapMinHalfHeight = 0.25f;

        // Shared by every stand-in factory and clear brush FAP builds at runtime.
        public const string StandinPrefabPrefix = "FAP_";

        // Filter: prefabs whose name starts with these prefixes are skipped.
        // The underscore prefix is Valheim's convention for internal-helper
        // prefabs nobody should build.
        private static readonly string[] _skipPrefixes =
        {
            "_", "BBH_", "rrr_", "CLLC_"
        };

        // Filter: prefabs containing these substrings are skipped. "random"
        // catches RandomFlyingBird and the random-fish helpers; we don't
        // want those on the hammer.
        private static readonly string[] _skipContains =
        {
            "Random", "random"
        };

        // Filter: prefab names always rejected regardless of components  - 
        // things the player shouldn't spawn (Odin, the Valkyrie cutscene actor,
        // treasure chests) or internal markers.
        private static readonly HashSet<string> _hardSkipNames = new HashSet<string>
        {
            "Player", "Valkyrie", "HelmetOdin", "CapeOdin", "odin",
            "Ravens", "TERRAIN_TEST", "PlaceMarker", "Circle_section",
            "demister_ball", "CargoCrate", "FishingRodFloat",
            "Pickable_RandomFood", "Pickable_Item", "Pickable_DvergerThing",
            "Pickable_DolmenTreasure",
            // 1.1.394 (Fire 11-05/06): the world loot chests are on the Hammer now (VALootPieceMode: placed = decor, no loot roll; menu switch to vanilla).
            // FAP-internal bake-parent template - the empty template must never
            // be hammer-selectable (the per-blueprint piece_<name> prefabs in
            // the Baked Meshes tab are the user-facing entry point).
            "VA_BakeParent",
        };

        public static bool WasInjectedByUs(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            return _injectedPrefabHashes.Contains(prefabName.GetStableHashCode());
        }

        public static bool WasInjectedByUs(GameObject go)
            => go != null && WasInjectedByUs(StripCloneSuffix(go.name));

        // Everything FAP puts on the hammer, not just the prefabs it injected: the stand-in
        // factories (clutter, monsters, item stands), the clear brushes, blueprint stubs and
        // baked bundles are all FAP-only content a player without the mod never has.
        public static bool IsFapOwnedPiece(GameObject pieceObject)
        {
            if (pieceObject == null) return false;

            string prefabName = StripCloneSuffix(pieceObject.name);
            if (string.IsNullOrEmpty(prefabName)) return false;

            if (WasInjectedByUs(prefabName)) return true;
            if (prefabName.StartsWith(StandinPrefabPrefix, StringComparison.Ordinal)) return true;
            if (prefabName.StartsWith(Managers.VABlueprintHammerIntegration.BlueprintPrefabPrefix, StringComparison.Ordinal)) return true;
            return Utilities.VABakeBundle.TryGetCacheKeyForPrefab(prefabName) != null;
        }

        // Public entry point. Called from Ascend.WaitForZNetReady once
        // ZNetScene + ObjectDB are populated. Idempotent - guarded by
        // _initialized so a config reload re-running this is a no-op.
        public static void InitializeOnce()
        {
            if (_initialized)
            {
                VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: VAVanillaPieceManager already initialized, skipping");
                return;
            }
            if (_initializing)
            {
                VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: VAVanillaPieceManager heavy init coroutine is already running, skipping duplicate call.");
                return;
            }

            // Admin gate - the single funnel for everything this mod adds to
            // the hammer. Deliberately leaves _initialized false so a mid-session
            // admin promotion re-fires InitializeOnce on the next world load.
            //
            // RegisterPiecesForNonAdmins keeps a non-admin's pieces REGISTERED on the
            // table - other mods only treat a prefab as a build piece when it is, and
            // their clones then render for players who never see the pieces themselves.
            // VABuildMenuVisibility is what keeps them out of the build menu.
            bool registrationOnly = !IsAdminGateOpen();
            if (registrationOnly)
            {
                if (!ShouldRegisterPiecesForNonAdmins())
                {
                    string server = VAFapServerPresence.ServerRunsFap
                        ? "The server runs FiresAdminPrefabs, so its AdminOnlyMode applies; watching for a promotion."
                        : "Whether the server runs FiresAdminPrefabs is decided when the player spawns: without it the gate opens (client-only), with it a promotion does.";
                    Debug.Log($"{VaPieces.PluginName}: VAVanillaPieceManager init skipped for now - AdminOnlyMode is on and the local player is not an admin. {server} Vanilla hammer pieces remain unmodified; search bar still active.");
                    return;
                }
                if (_registeredForNonAdmins)
                {
                    VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: VAVanillaPieceManager pieces are already registered for this non-admin session; still watching for a promotion.");
                    return;
                }
            }

            VABuildMenuVisibility.HideInjectedPiecesFromMenu = registrationOnly;
            if (!registrationOnly) _initialized = true;

            var scene = ZNetScene.instance;
            if ((UnityEngine.Object)scene == (UnityEngine.Object)null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: ZNetScene not ready, cannot init vanilla pieces");
                _initialized = false;
                return;
            }

            var existingBuildables = CollectAlreadyBuildablePrefabs();
            BuildFracToBaseMap(scene);
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: Hammer PieceTable not available, abort");
                _initialized = false;
                return;
            }

            // Must run BEFORE category registration: it builds the prefab->tab
            // override map and records folder names so they can be registered
            // as real PieceCategory values right after the built-ins.
            VAHammerTabOrganizer.LoadFromDisk();

            // Register our custom categories BEFORE we start assigning pieces
            // to them - AssignCategory needs the PieceCategory value to exist.
            VACategoryRegistry.RegisterAll(pieceTable);

            // Register every user-defined tab folder as a real category.
            // GetOrAdd is idempotent for built-in names; new user folders get
            // a fresh PieceCategory appended to the table's category list.
            foreach (var tabName in VAHammerTabOrganizer.KnownTabNames)
            {
                if (string.IsNullOrEmpty(tabName)) continue;
                var cat = VACategoryRegistry.GetOrAdd(tabName);
                if (!pieceTable.m_categories.Contains(cat))
                {
                    pieceTable.m_categories.Add(cat);
                    pieceTable.m_categoryLabels.Add(VAHammerTabOrganizer.TryGetDisplayName(tabName, out var dnTab) ? dnTab : tabName);
                    int needed = (int)cat + 1;
                    while (PieceTableAccess.ByCategory(pieceTable).Count < needed)
                        PieceTableAccess.ByCategory(pieceTable).Add(new List<Piece>());
                }
            }

            // The remaining work (blueprint scan, full ZNetScene scan, stand-in
            // factories, tab save, icon queue, UI refresh) runs on a coroutine
            // that yields every InitFrameBudgetMs so the first frame stays
            // responsive instead of freezing on the 5k+ prefab pass. Requires
            // VaPieces.Instance (the MonoBehaviour coroutines hang on); if it's
            // null, fall through to a synchronous drain.
            _initializing = true;
            if ((UnityEngine.Object)VaPieces.Instance != (UnityEngine.Object)null)
            {
                // Normal path: defer the heavy build until the player spawns so it streams in
                // behind a fast login instead of stacking stalls onto world-load.
                VaPieces.Instance.StartCoroutine(
                    InitializeHeavyWorkCoroutine(scene, pieceTable, existingBuildables, deferUntilSpawn: true, registrationOnly));
                return;
            }

            // Fallback (no coroutine host): drain synchronously and immediately - the spawn-gate is
            // skipped here, since a sync drain can't wait for a spawn that hasn't happened yet.
            Debug.LogWarning($"{VaPieces.PluginName}: VaPieces.Instance null - running heavy init synchronously (UI will hitch).");
            var stopwatch = Stopwatch.StartNew();
            var enumerator = InitializeHeavyWorkCoroutine(scene, pieceTable, existingBuildables, deferUntilSpawn: false, registrationOnly);
            while (enumerator.MoveNext()) { /* drain synchronously */ }
            Debug.Log($"{VaPieces.PluginName}: Synchronous heavy init drained in {stopwatch.ElapsedMilliseconds}ms.");
        }

        // Registers baked-bundle prefabs that VABakeBundle.RegisterAll
        // finished AFTER InitializeHeavyWorkCoroutine already snapshotted
        // ZNetScene.m_prefabs. RegisterAll is async (yields between bundles
        // for responsiveness); with a large library (100+ bundles, ~138s) it
        // commonly finishes AFTER the hammer scan's one-shot snapshot, so the
        // late bundles never land in the PieceTable - the user bakes them all
        // but only the early ones show on the hammer. RegisterAll calls this
        // at its tail to add exactly those, idempotently (anything already on
        // the hammer is skipped). No-ops if the hammer table doesn't exist yet
        // (player not spawned) - the normal scan, which runs later, gets them.
        // Turns one eligible ZNetScene prefab into a hammer piece. Shared by the
        // initial scan and the late bundle pass so both produce identical pieces.
        // Returns true when the prefab was newly added to the PieceTable.
        private static bool ProcessEligibleForHammer(GameObject prefab, PieceTable pieceTable, out bool injectedFreshPiece)
        {
            injectedFreshPiece = false;
            if (VAExternalBundleLoader.IsRemovedPrefabPlaceholder(prefab)) return false;
            // Another mod's Jotunn prefab is never modified (the rule and its reasons: VAJotunnGuard).
            switch (VAJotunnGuard.Classify(prefab))
            {
                case VAJotunnGuard.Decision.LeaveAlone:
                    VAJotunnGuard.RecordLeftAlone(prefab.name);
                    return false;
                case VAJotunnGuard.Decision.ByReference:
                    _eligibleByName[prefab.name] = prefab;
                    if (pieceTable.m_pieces.Contains(prefab)) return false;
                    pieceTable.m_pieces.Add(prefab);
                    _injectedPrefabHashes.Add(prefab.name.GetStableHashCode());
                    VAJotunnGuard.RecordByReference(prefab.name);
                    return true;
            }
            _eligibleByName[prefab.name] = prefab;
            injectedFreshPiece = EnsurePieceComponent(prefab);
            EnsurePersistentZNetView(prefab);
            Snapping.VASnapPoints.NormaliseBundleNames(prefab);   // 1.1.323 (Fire): FAP bundle pieces' points are "Snappoint N" (MVBP's convention)
            EnsureSnapPoints(prefab);
            NeutralizeBrokenFireplace(prefab);
            FixSpikeTriggerLayers(prefab);

            if (VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out var sourceBundle)
                && (ConfigManager.Instance.IsBundleInNatureShaderAllOf(sourceBundle) || IsNaturePrefab(prefab)))
            {
                ShaderReplacement.ApplyBundleNatureShader(prefab);
            }

            bool addedToTable = false;
            if (!pieceTable.m_pieces.Contains(prefab))
            {
                pieceTable.m_pieces.Add(prefab);
                addedToTable = true;
            }
            _injectedPrefabHashes.Add(prefab.name.GetStableHashCode());
            return addedToTable;
        }

        // Adds bundle prefabs that RegisterAll registered after the initial
        // hammer scan snapshotted ZNetScene.m_prefabs, running each through the
        // same ProcessEligibleForHammer pipeline. Idempotent; no-ops until the
        // hammer PieceTable exists. Returns the count newly added.
        public static int RegisterLateBundlePieces(IEnumerable<GameObject> bundlePrefabs)
        {
            if (bundlePrefabs == null) return 0;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return 0;

            int added = 0;
            foreach (var prefab in bundlePrefabs)
            {
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null) continue;
                if (pieceTable.m_pieces.Contains(prefab)) continue;
                if (ProcessEligibleForHammer(prefab, pieceTable, out _)) added++;
            }

            if (added > 0)
                Debug.Log($"{VaPieces.PluginName}: VAVanillaPieceManager.RegisterLateBundlePieces - added {added} late bundle piece(s) to the hammer.");
            Snapping.VASnapPoints.NormaliseHammerBundlePieces(pieceTable, "late bundles"); Snapping.VASnapGeometry.ApplyToTable(pieceTable);   // 1.1.324
            return added;
        }

        // 1.1.228 (lazy bundles): a stub's real prefab takes the stub's place on the Hammer: the same table slot, the
        // category the stub was routed to, and the same hammer preparation as any bundle piece.
        public static void ReplaceRealizedPiece(GameObject stub, GameObject real)
        {
            if ((UnityEngine.Object)real == (UnityEngine.Object)null) return;
            var pieceTable = GetHammerPieceTable();
            var stubPiece = stub != null ? stub.GetComponent<Piece>() : null;
            if (pieceTable == null)
            {
                if (stub != null && _eligibleByName.TryGetValue(stub.name, out var e) && e == stub) _eligibleByName[real.name] = real;
                return;
            }
            int slot = stub != null ? pieceTable.m_pieces.IndexOf(stub) : -1;
            ProcessEligibleForHammer(real, pieceTable, out _);
            var realPiece = real.GetComponent<Piece>();
            if (realPiece != null && stubPiece != null)
            {
                realPiece.m_category = stubPiece.m_category;
                if (realPiece.m_icon == null && stubPiece.m_icon != null) realPiece.m_icon = stubPiece.m_icon;   // the cached icon
                if (realPiece.m_icon == null) VAPieceIconRenderer.Enqueue(realPiece);
            }
            if (slot >= 0)
            {
                int added = pieceTable.m_pieces.LastIndexOf(real);
                if (added >= 0 && added != slot) { pieceTable.m_pieces.RemoveAt(added); if (added < slot) slot--; }
                pieceTable.m_pieces[slot] = real;
            }
        }

        // 1.1.230 (lazy bundles stage 3): an idle realized piece hands its Hammer slot back to its stub, and FAP's own
        // tables let go of it so its assets can be released.
        public static void RestoreStubPiece(GameObject real, GameObject stub)
        {
            if ((UnityEngine.Object)stub == (UnityEngine.Object)null) return;
            if (real != null && _eligibleByName.TryGetValue(real.name, out var e) && e == real) _eligibleByName[real.name] = stub;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null || real == null) return;
            int slot = pieceTable.m_pieces.IndexOf(real);
            if (slot >= 0) pieceTable.m_pieces[slot] = stub;
            var realPiece = real.GetComponent<Piece>();
            var stubPiece = stub.GetComponent<Piece>();
            if (realPiece != null && stubPiece != null && stubPiece.m_icon == null) stubPiece.m_icon = realPiece.m_icon;
        }

        // Puts one ZNetScene prefab on the hammer by name, for a caller that needs the piece
        // before the spawn-gated scan reaches it. Returns the prefab once it is registered, or
        // null when it isn't ours to make buildable. Idempotent.
        public static GameObject RegisterSinglePieceOnDemand(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;

            var scene = ZNetScene.instance;
            if (scene == null) return null;

            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return null;

            var prefab = scene.GetPrefab(prefabName);
            if ((UnityEngine.Object)prefab == (UnityEngine.Object)null) return null;
            if (pieceTable.m_pieces.Contains(prefab)) return prefab;

            return ProcessEligibleForHammer(prefab, pieceTable, out _) ? prefab : null;
        }

        // The portion of InitializeOnce that touches every ZNetScene prefab,
        // builds all the stand-ins, and queues icons. Long loops yield every
        // InitFrameBudgetMs so the game stays playable while it runs.
        private static IEnumerator InitializeHeavyWorkCoroutine(
            ZNetScene scene,
            PieceTable pieceTable,
            HashSet<string> existingBuildables,
            bool deferUntilSpawn,
            bool registrationOnly)
        {
            VAJotunnGuard.Reset();   // 1.1.219: Jotunn's registries read fresh for this world
            // Hold the entire heavy build until the local player exists. None of it (hammer scan,
            // stand-ins, icon queue) is needed until the build menu opens, and running it during
            // world-load stacked multi-second stalls onto an already-heavy login. The null-check
            // spins cheaply, yielding each frame, so login stays responsive; the work then streams
            // in over the seconds after spawn. Bundle PREFAB registration stays at ZNetScene.Awake
            // (VABakeBundle.RegisterAllAsync) so placed baked-bundle ZDOs still instantiate.
            // 1.1.424 ([ghost], lead 11-12, Fire's run_3: these slices were 57 of his 79 post-arrival hitch frames): the work no longer waits for
            // the local player. It starts at ZNetScene-ready and runs under the loading screen at FapInitBudget.LoadingBudgetMs per frame (Fire's
            // rule: a longer load beats post-load hitches); whatever is left after the world shows runs at 2 ms per frame. deferUntilSpawn is
            // kept as a parameter for the callers but no longer spins.
            var budget = new VerdantsAscent.Utilities.FapInitBudget("FAP hammer scan + stand-ins (InitializeHeavyWorkCoroutine)");

            // Phase 0: blueprint scan and stub registration. It yields as it walks the blueprint files, then
            // starts the ghost cache bake without waiting for it. A non-admin never places blueprints, and
            // a stub is a menu placeholder no world should hold, so registration-only skips it.
            if (!registrationOnly && VACategoryRegistry.TryGet(VACategoryRegistry.CatBlueprints, out var bpCat))
            {
                yield return VABlueprintHammerIntegration.ScanAndRegisterCoroutine(scene, pieceTable, bpCat);
            }

            int injected = 0;
            int registered = 0;
            budget.NextFrame();   // the blueprint scan above paced itself; a fresh slice for the prefab walk

            // Snapshot the prefab list so the foreach iterator stays valid
            // even if some patch elsewhere mutates ZNetScene.m_prefabs
            // during our yield windows. Cheap - references only.
            var prefabSnapshot = scene.m_prefabs.ToList();

            foreach (var prefab in prefabSnapshot)
            {
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null) continue;
                if (!IsEligible(prefab, existingBuildables, out string skipWhy))
                {
                    // 1.1.317: the full "why not on the Hammer" list (BepInEx\cache\FiresAdminPrefabs\hammer_skipped.txt)
                    if (skipWhy != "already on a hammer/hoe/cultivator table" && skipWhy != "not a root prefab") s_skipped.Add(skipWhy + "\t" + prefab.name);
                    continue;
                }

                // Already-injected skip. _injectedPrefabHashes is static and
                // survives world-unload, but bundle prefabs are rebuilt fresh
                // each session - so also require the cached _eligibleByName
                // entry to be the SAME live instance, or session 2's Baked
                // Meshes tab loses its entries to a stale hash match.
                int hash = prefab.name.GetStableHashCode();
                if (_injectedPrefabHashes.Contains(hash)
                    && _eligibleByName.TryGetValue(prefab.name, out var cachedPrefab)
                    && (UnityEngine.Object)cachedPrefab != (UnityEngine.Object)null
                    && cachedPrefab == prefab)
                {
                    continue;
                }

                // Clutter early-exit. Clutter passes IsEligible but must NOT
                // get Piece/SnapPoint mutations on the vanilla template - those
                // leak onto every painted clone. The stand-in builder below
                // handles all clutter paths uniformly.
                if (IsClutterPrefab(prefab))
                {
                    _injectedPrefabHashes.Add(hash);
                    if (budget.Over()) { yield return null; budget.NextFrame(); }
                    continue;
                }

                // _frac prefabs stay visible in the hammer; the placement-time
                // swap (HammerFracSwapPatch) replaces them with their whole-mesh
                // counterpart on click, gated by configHammerSwapFracOnPlace.
                if (ProcessEligibleForHammer(prefab, pieceTable, out bool addedFreshPiece))
                    registered++;
                if (addedFreshPiece) injected++;

                // Frame budget - hand back to Unity once we've burned through it.
                if (budget.Over()) { yield return null; budget.NextFrame(); }
            }
            yield return null; budget.NextFrame();   // 1.1.424: the curated pass + reports below get their own frame

            // Curated MVBP-parity pass - a handful of vanilla prefabs players
            // expect from MoreVanillaBuildPrefabs kept silently missing from
            // the generic scan (user report: rock4_coast / rock4_forest /
            // rock4_heath / stone_floor unfindable even via search). Injection
            // here is deterministic and LOUD: each one logs whether the
            // generic filter would have taken it, so a regression is visible
            // in the log instead of vanishing.
            EnsureCuratedVanillaInjects(scene, pieceTable, ref registered, ref injected);
            ReportSpikeTriggerLayers();   // 1.1.350
            WriteSkippedReport(prefabSnapshot.Count);
            VAJotunnGuard.ReportOnce();   // 1.1.219: one line for the Jotunn-owned prefabs

            // Registration-only stops here: the pieces other mods need are on the table, and
            // everything below is build-menu content for an admin - stand-ins, icons, tab files.
            if (registrationOnly)
            {
                pieceTable.m_pieces.RemoveAll(p => (UnityEngine.Object)p == (UnityEngine.Object)null);
                _registeredForNonAdmins = true;
                _initializing = false;
                ShaderReplacement.FlushPendingNatureShaders();
                budget.Done();
                Debug.Log($"{VaPieces.PluginName}: registered {registered} piece(s) on the hammer for a non-admin "
                    + "(RegisterPiecesForNonAdmins). They stay out of the build menu; other mods can still treat them as build pieces.");
                try
                {
                    if (Player.m_localPlayer != null) VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: post-registration piece-list refresh threw: {ex.Message}");
                }
                yield break;
            }

            // Yield between major sub-passes - the stand-in factories below
            // instantiate GameObjects, which is itself heavy work.
            yield return null;
            budget.NextFrame();

            // Second pass: a hammer-tab stand-in for every ClutterSystem entry.
            // Stand-ins are FAP-owned GameObjects that never spawn - placement
            // is intercepted and dispatched against the real clutter prefab, so
            // the vanilla clutter templates are never mutated.
            int vanillaClutterAdded = RegisterClutterAsStandins(pieceTable);
            yield return null;
            budget.NextFrame();

            // FX clear brush - admin tool that scrubs FAP_persist + destroys
            // EWP-persistent FX placed by FAP. Goes in the FX (Vanilla) tab
            // so it sits with the FX placements it's meant to clean up.
            RegisterFxClearStandin(pieceTable);

            // 1.1.291 (Fire: Radius Swapper): one icon at the head of the Misc tab; admin only (this point is past the registration-only return).
            if (IsAdminGateOpen())
            {
                var swapStandin = VerdantsAscent.Utilities.VARadiusSwapTool.EnsureStandin(Piece.PieceCategory.Misc);
                if (swapStandin != null && !pieceTable.m_pieces.Contains(swapStandin)) pieceTable.m_pieces.Add(swapStandin);
            }

            // Item-stand tabs - one tile per (eligible item, orientation).
            // Placement spawns a vanilla itemstand/itemstandh with the chosen
            // item pre-attached via the s_item ZDO field.
            yield return InitializeItemStandStandinsCoroutine(pieceTable);
            budget.NextFrame();

            // Monsters tab - a placeable stand-in for every Character prefab.
            // Placement dispatches to VAMonsterSpawner; right-click opens the
            // Configure-monster UI for per-spawn overrides.
            yield return InitializeMonsterStandinsCoroutine(pieceTable);
            budget.NextFrame();

            // 1.1.351 (Fire 10-31): Dungeons tab - a whole-dungeon and an empty-entrance stand-in per vanilla location with an interior.
            try { Utilities.VAFapDungeons.RegisterStandins(pieceTable); }
            catch (Exception ex) { Debug.LogWarning($"[FAP] dungeons tab: stand-ins not built ({ex.Message})"); }

            // Prune any null entries that crept in (shouldn't happen with
            // our filter but cheap insurance against another mod nulling
            // a slot mid-iteration).
            pieceTable.m_pieces.RemoveAll(p => (UnityEngine.Object)p == (UnityEngine.Object)null);

            // Schedule a late RefreshClutterStandins once the player spawns.
            // EWD and similar mods rebuild ClutterSystem.m_clutter during/after
            // our pass, so the initial call above only sees a partial set;
            // PlayerSpawnGate fires after every m_clutter-touching mod settles.
            try
            {
                FiresCore.Lifecycle.PlayerSpawnGate.RunWhenLocalReady(
                    timeoutSeconds: LateClutterRefreshTimeoutSeconds,
                    work: _ =>
                    {
                        try { RefreshClutterStandins(); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"{VaPieces.PluginName}: late RefreshClutterStandins threw: {ex.Message}");
                        }
                    });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: failed to schedule late RefreshClutterStandins: {ex.Message}");
            }

            Debug.Log($"{VaPieces.PluginName}: VAVanillaPieceManager scanned {scene.m_prefabs.Count} prefabs, " +
                      $"{_eligibleByName.Count} eligible, {injected} got fresh Piece component, {registered} new hammer entries " +
                      $"(+{vanillaClutterAdded} vanilla ClutterSystem prefabs)");
            Snapping.VASnapPoints.NormaliseHammerBundlePieces(pieceTable, "scan"); Snapping.VASnapGeometry.ApplyToTable(pieceTable);   // 1.1.324: FAP bundle pieces already on the Hammer
            VerdantsAscent.Patches.VALootPieceMode.OnHammerScanned(pieceTable);   // 1.1.395: loot chest / debris names + Info count

            // 1.1.227: lazy third-party bundles, stage 1. The stub data of every Assets bundle, written once per bundle version.
            Utilities.VALazyBundleManifest.WriteMissing(scene, pieceTable);

            // Write the final tab layout to disk so new prefabs land in their
            // natural-fit tab on next launch; user edits are preserved.
            // The tail from here used to run in one frame after the stand-ins, 355 ms at "world settling" where the player
            // already sees the world ([ghost], Tools\FAP_CLIENT_LOAD_COST.md #5): each step now gets its own frame, and
            // the dimension push is budgeted like the scan above. Same steps, same order, same results.
            yield return null; budget.NextFrame();
            VAHammerTabOrganizer.SaveCurrentLayout(_eligibleByName);
            yield return null; budget.NextFrame();

            // AFTER the FAP tabs claim their prefabs: materialize the vanilla
            // categories as folders too (Misc, Crafting, Building*, Furniture...),
            // seeded with whatever isn't listed anywhere else - so vanilla tabs
            // can be reorganized and reordered from disk like any custom tab.
            VAHammerTabOrganizer.EnsureVanillaTabFolders(GetHammerPieceTable());
            yield return null; budget.NextFrame();

            // Infinity Hammer zoop needs a per-prefab dimension to know its step
            // size, and IH only ships them for vanilla pieces - push collider
            // AABBs for our injected pieces so zoop works on them out of the box.
            yield return VerdantsAscent.Patches.InfinityHammerMeasureGuard.PushDimensionsForInjectedCoroutine(_eligibleByName, (long)budget.BudgetMs < 1 ? 1 : (long)budget.BudgetMs);   // 1.1.424: loading 40 / play 2 ms
            yield return null; budget.NextFrame();

            // Queue icon rendering for every injected piece without an authored
            // Sprite. The renderer paces itself at one icon per frame.
            QueueIconRenders();
            yield return null; budget.NextFrame();

            // Surface the new pieces in the live UI. The player may not exist
            // yet on first world load (this ran at ZNetScene-ready time, before
            // character spawn); the admin-promotion retry path runs in-game.
            try
            {
                if (Player.m_localPlayer != null)
                    VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: post-init UI refresh threw: {ex.Message}");
            }

            _initializing = false;
            ShaderReplacement.FlushPendingNatureShaders();
            budget.Done();
        }

        // Yielding variant of RegisterItemStandsAsStandins. 3,042 stand-ins
        // (1,521 items x 2 orientations) instantiated synchronously was the
        // second-biggest cost in the old init path - each GetOrCreate does
        // an Instantiate + a Piece component + a Renderer copy. Budget per
        // frame keeps that work spread out instead of stalling.
        private static IEnumerator InitializeItemStandStandinsCoroutine(PieceTable pieceTable)
        {
            if (pieceTable == null) yield break;
            if (!VACategoryRegistry.TryGet(VACategoryRegistry.CatItemStandsV, out var verticalCat))
                yield break;
            bool horizontalRegistered = VACategoryRegistry.TryGet(VACategoryRegistry.CatItemStandsH, out var horizontalCat);

            // Vanilla-friendly offers only what a player on a vanilla client could attach
            // themselves, so a shared blueprint never carries a stand vanilla would render empty.
            bool vanillaOnly = ConfigManager.Instance.configVanillaFriendly.Value;

            int verticalAdded = 0;
            int horizontalAdded = 0;
            var budget = new VerdantsAscent.Utilities.FapInitBudget("FAP item-stand stand-ins (InitializeItemStandStandinsCoroutine)");
            // 1.1.424: m_pieces.Contains was an O(n) List scan with Unity == before every one of ~3k adds (quadratic); a set mirrors the table here
            var onTable = new HashSet<GameObject>(pieceTable.m_pieces);

            // Snapshot the registry so concurrent additions don't trip
            // the enumerator across our yield windows.
            var entries = VAItemStandRegistry.All.ToList();

            bool showV = ShowStandOrientation(VAItemStandStandinFactory.Orientation.Vertical);
            bool showH = ShowStandOrientation(VAItemStandStandinFactory.Orientation.Horizontal);
            foreach (var entry in entries)
            {
                if (showV && (!vanillaOnly || entry.VanillaAcceptsVertical))
                {
                    var verticalStandin = VAItemStandStandinFactory.GetOrCreate(
                        entry, VAItemStandStandinFactory.Orientation.Vertical, verticalCat);
                    if (verticalStandin != null && onTable.Add(verticalStandin))
                    {
                        pieceTable.m_pieces.Add(verticalStandin);
                        verticalAdded++;
                    }
                }
                if (showH && horizontalRegistered && (!vanillaOnly || entry.VanillaAcceptsHorizontal))
                {
                    var horizontalStandin = VAItemStandStandinFactory.GetOrCreate(
                        entry, VAItemStandStandinFactory.Orientation.Horizontal, horizontalCat);
                    if (horizontalStandin != null && onTable.Add(horizontalStandin))
                    {
                        pieceTable.m_pieces.Add(horizontalStandin);
                        horizontalAdded++;
                    }
                }

                if (budget.Over()) { yield return null; budget.NextFrame(); }
            }
            budget.Done();

            // Always logged: the proof line for the lazy stand-in meshes (FAP 1.1.149, MEMORY_LEADS #5).
            Debug.Log($"[FAP] item stands: {verticalAdded + horizontalAdded} stand-ins ({verticalAdded} wall + {horizontalAdded} shelf from {VAItemStandRegistry.Count} items), meshes deferred until selected ({VAItemStandStandinFactory.VisualsBuilt} built so far)");
        }

        // Populates the Monsters tab with one stand-in per Character prefab.
        // The factory copies only the visual mesh hierarchy so placement ghosts
        // never fire MonsterAI/Awake side effects on hover.
        private static IEnumerator InitializeMonsterStandinsCoroutine(PieceTable pieceTable)
        {
            if (pieceTable == null) yield break;
            if (!VACategoryRegistry.TryGet(VACategoryRegistry.CatMonsters, out var monstersCat))
                yield break;

            VAEnemySpawners.AddRow();
            int found = VAMonsterRegistry.RebuildFromScene();
            int added = 0;
            var budget = new VerdantsAscent.Utilities.FapInitBudget("FAP monster stand-ins (InitializeMonsterStandinsCoroutine)");
            var onTable = new HashSet<GameObject>(pieceTable.m_pieces);   // 1.1.424: O(1) instead of List.Contains per add

            foreach (var entry in VAMonsterRegistry.All)
            {
                var standin = VAMonsterStandinFactory.GetOrCreate(entry, monstersCat);
                if (standin != null && onTable.Add(standin))
                {
                    pieceTable.m_pieces.Add(standin);
                    added++;
                }
                if (VAEnemySpawners.Wanted(entry))
                {
                    var spawnerPick = VAMonsterStandinFactory.GetOrCreateSpawner(entry, monstersCat);
                    if (spawnerPick != null && onTable.Add(spawnerPick)) { pieceTable.m_pieces.Add(spawnerPick); added++; }
                }

                if (budget.Over()) { yield return null; budget.NextFrame(); }
            }
            budget.Done();

            var spawnerClearStandin = VerdantsAscent.Utilities.VAFapMonsterClearTool.EnsureStandin(monstersCat);
            if (spawnerClearStandin != null && !pieceTable.m_pieces.Contains(spawnerClearStandin))
            {
                pieceTable.m_pieces.Add(spawnerClearStandin);
                added++;
            }

            var containerClearStandin = VerdantsAscent.Utilities.VAFapContainerClearTool.EnsureStandin(Piece.PieceCategory.Furniture);
            if (containerClearStandin != null && !pieceTable.m_pieces.Contains(containerClearStandin))
            {
                pieceTable.m_pieces.Add(containerClearStandin);
                added++;
            }

            Debug.Log($"{VaPieces.PluginName}: InitializeMonsterStandinsCoroutine built {added} monster stand-in(s) (incl. Spawner Clear + Container Clear) from {found} eligible Character prefabs.");
        }

        // Builds a FAP-owned stand-in for every clutter prefab in
        // VAClutterRegistry (which mirrors ClutterSystem.m_clutter). Vanilla
        // clutter prefabs are NEVER mutated. Idempotent - the factory caches
        // per clutter name and the pieceTable add is guarded by Contains.
        private static int RegisterClutterAsStandins(PieceTable pieceTable)
        {
            if (pieceTable == null) return 0;

            // Vanilla-friendly keeps the Clutter tab off the table entirely. Without the
            // category there is nowhere to file a stand-in, and default(PieceCategory) is Misc  - 
            // which is how clutter used to end up in a vanilla tab in vanilla-friendly mode.
            if (!VACategoryRegistry.TryGet(VACategoryRegistry.CatClutter, out var clutterCat)) return 0;

            var clearStandin = VAClutterClearTool.EnsureStandin(clutterCat);
            if (clearStandin != null && !pieceTable.m_pieces.Contains(clearStandin))
                pieceTable.m_pieces.Add(clearStandin);

            int added = 0;
            foreach (var entry in VAClutterRegistry.All)
            {
                var realPrefab = entry.Value?.m_prefab;
                if (realPrefab == null) continue;

                var standin = VAClutterStandinFactory.GetOrCreate(realPrefab, clutterCat);
                if (standin == null) continue;
                if (pieceTable.m_pieces.Contains(standin)) continue;
                pieceTable.m_pieces.Add(standin);
                added++;
            }
            if (ConfigManager.Instance.configVerboseLogging.Value)
            {
                Debug.Log($"{VaPieces.PluginName}: RegisterClutterAsStandins built {added} stand-in(s) from registry (cs.m_clutter has {VAClutterRegistry.Count} entries).");
            }
            return added;
        }

        // One FX clear brush stand-in per FX tab. The build menu lists them right after its
        // leading icons (VAAreaClearBrush.PlaceBrushesAfterMenuIcons), so table order doesn't matter.
        private static void RegisterFxClearStandin(PieceTable pieceTable)
        {
            if (pieceTable == null) return;
            var fxCategoryNames = new[]
            {
                VACategoryRegistry.CatFX,
                VACategoryRegistry.CatVFX,
            };
            int added = 0;
            foreach (var name in fxCategoryNames)
            {
                if (!VACategoryRegistry.TryGet(name, out var cat)) continue;
                var standin = VAFapEwpClearTool.EnsureStandinFor(cat);
                if (standin != null && !pieceTable.m_pieces.Contains(standin)) { pieceTable.m_pieces.Add(standin); added++; }
                // 1.1.260: the Make Persistent Brush sits next to the FX Clear Brush in each FX tab
                var persist = VAFapPersistBrushTool.EnsureStandinFor(cat);
                if (persist != null && !pieceTable.m_pieces.Contains(persist)) { pieceTable.m_pieces.Add(persist); added++; }
            }
            if (added > 0 && ConfigManager.Instance.configVerboseLogging.Value)
            {
                Debug.Log($"{VaPieces.PluginName}: Registered FX clear stand-in into {added} FX category tab(s).");
            }
        }

        // Builds FAP-owned item-stand stand-ins for every eligible ItemDrop,
        // two per item (vertical + horizontal). Separate cache resolves per
        // orientation so a missing vanilla horizontal prefab on an old install
        // doesn't block the vertical tab.
        // 1.1.197 (Arbby via Fire): [ItemStands] StandOrientation = Horizontal | Vertical | Both.
        internal static bool ShowStandOrientation(VAItemStandStandinFactory.Orientation orientation)
        {
            string mode = ConfigManager.Instance?.configItemStandOrientation?.Value ?? "Horizontal";
            if (string.Equals(mode, "Both", StringComparison.OrdinalIgnoreCase)) return true;
            return orientation == VAItemStandStandinFactory.Orientation.Vertical
                ? string.Equals(mode, "Vertical", StringComparison.OrdinalIgnoreCase)
                : !string.Equals(mode, "Vertical", StringComparison.OrdinalIgnoreCase);
        }

        // Live swap (config change or the Item Stands tab's right-click menu): drop the hidden orientation's stand-ins from
        // the hammer, add the shown one's, re-sync the tabs and repaint the menu, without closing the hammer.
        public static void ApplyItemStandOrientation()
        {
            try
            {
                var table = GetHammerPieceTable();
                if (table == null) return;
                int removed = 0;
                foreach (var kv in VAItemStandStandinFactory.AllStandins)
                {
                    if (kv.Value == null || !VAItemStandStandinFactory.TryResolve(kv.Value, out _, out var o)) continue;
                    if (!ShowStandOrientation(o) && table.m_pieces.Remove(kv.Value)) removed++;
                }
                int before = table.m_pieces.Count;
                RegisterItemStandsAsStandins(table);
                VACategoryRegistry.SyncCategories(table);
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
                VaPieces.Log?.LogDebug($"[FAP] item stand orientation = {ConfigManager.Instance.configItemStandOrientation.Value}: {removed} stand-in(s) removed, {table.m_pieces.Count - before} added");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: item stand orientation swap failed: {ex.Message}");
            }
        }

        private static void RegisterItemStandsAsStandins(PieceTable pieceTable)
        {
            if (pieceTable == null) return;
            if (!VACategoryRegistry.TryGet(VACategoryRegistry.CatItemStandsV, out var verticalCat))
                return;
            VACategoryRegistry.TryGet(VACategoryRegistry.CatItemStandsH, out var horizontalCat);

            int verticalAdded = 0;
            int horizontalAdded = 0;
            bool showV = ShowStandOrientation(VAItemStandStandinFactory.Orientation.Vertical);
            bool showH = ShowStandOrientation(VAItemStandStandinFactory.Orientation.Horizontal);
            bool vanillaOnly = ConfigManager.Instance.configVanillaFriendly.Value;
            foreach (var entry in VAItemStandRegistry.All)
            {
                var verticalStandin = showV && (!vanillaOnly || entry.VanillaAcceptsVertical) ? VAItemStandStandinFactory.GetOrCreate(
                    entry, VAItemStandStandinFactory.Orientation.Vertical, verticalCat) : null;
                if (verticalStandin != null && !pieceTable.m_pieces.Contains(verticalStandin))
                {
                    pieceTable.m_pieces.Add(verticalStandin);
                    verticalAdded++;
                }
                var horizontalStandin = showH && (!vanillaOnly || entry.VanillaAcceptsHorizontal) ? VAItemStandStandinFactory.GetOrCreate(
                    entry, VAItemStandStandinFactory.Orientation.Horizontal, horizontalCat) : null;
                if (horizontalStandin != null && !pieceTable.m_pieces.Contains(horizontalStandin))
                {
                    pieceTable.m_pieces.Add(horizontalStandin);
                    horizontalAdded++;
                }
            }
            if (ConfigManager.Instance.configVerboseLogging.Value)
            {
                Debug.Log($"{VaPieces.PluginName}: RegisterItemStandsAsStandins built {verticalAdded} vertical + {horizontalAdded} horizontal stand-in(s) from {VAItemStandRegistry.Count} eligible items (vanilla-friendly={ConfigManager.Instance.configVanillaFriendly.Value}).");
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Live piece-table mutation (right-click context menu helpers)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Live-remove a piece from PieceTable.m_pieces by name, refreshing the
        // hammer UI without a world reload. Matches eligible prefabs and
        // stand-ins alike since they share m_pieces. Returns the count removed.
        public static int RemovePieceFromHammerByName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return 0;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return 0;
            int removed = pieceTable.m_pieces.RemoveAll(go =>
            {
                if ((UnityEngine.Object)go == (UnityEngine.Object)null
                    || !string.Equals(go.name, prefabName, StringComparison.Ordinal)) return false;
                NoteRemovedByHide(go);   // 1.1.282 (unhide live): the exact object goes back on unhide
                return true;
            });
            if (removed > 0 && Player.m_localPlayer != null)
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
            return removed;
        }

        // 1.1.282 (Fire: hidden pieces/tabs only came back after a world reload): every piece object a live hide
        // took off the hammer, by prefab name, so un-hide can put the SAME object back (vanilla hammer pieces
        // included, which the eligible scan never re-adds). Cleared per object as it is restored.
        private static readonly Dictionary<string, List<GameObject>> _removedByHide =
            new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);

        internal static void NoteRemovedByHide(GameObject go)
        {
            if ((UnityEngine.Object)go == (UnityEngine.Object)null) return;
            if (!_removedByHide.TryGetValue(go.name, out var list)) _removedByHide[go.name] = list = new List<GameObject>();
            if (!list.Contains(go)) list.Add(go);
        }

        // 1.1.282 (unhide live): puts one un-hidden prefab back on the live hammer. First the objects the hide
        // removed (NoteRemovedByHide), else the prefab from ZNetScene through the same ProcessEligibleForHammer the
        // load scan uses (a prefab hidden since before this load was never scanned). m_pieces.Contains guards
        // every add, so nothing is duplicated. No refresh here: callers finish with RecategorizeEligiblePieces.
        // Returns true when the prefab is on the hammer afterwards.
        public static bool RestorePieceToHammerByName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return false;

            if (_removedByHide.TryGetValue(prefabName, out var removedList))
            {
                foreach (var go in removedList)
                    if ((UnityEngine.Object)go != (UnityEngine.Object)null && !pieceTable.m_pieces.Contains(go))
                        pieceTable.m_pieces.Add(go);
                _removedByHide.Remove(prefabName);
            }

            bool onTable = pieceTable.m_pieces.Any(go =>
                (UnityEngine.Object)go != (UnityEngine.Object)null && string.Equals(go.name, prefabName, StringComparison.Ordinal));
            if (!onTable)
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
                if ((UnityEngine.Object)prefab != (UnityEngine.Object)null
                    && (_eligibleByName.ContainsKey(prefabName) || IsEligible(prefab, CollectAlreadyBuildablePrefabs())))
                {
                    ProcessEligibleForHammer(prefab, pieceTable, out _);
                    onTable = pieceTable.m_pieces.Contains(prefab);
                }
            }

            // A piece filed under the Hidden sentinel at load (ignored then) gets its tab back.
            if (onTable)
            {
                foreach (var go in pieceTable.m_pieces)
                {
                    if ((UnityEngine.Object)go == (UnityEngine.Object)null || !string.Equals(go.name, prefabName, StringComparison.Ordinal)) continue;
                    var piece = go.GetComponent<Piece>();
                    if (piece == null || !VerdantsAscent.Utilities.VAPieceCategoryRuntime.IsHidden(piece.m_category)) continue;
                    piece.m_category = VAHammerTabOrganizer.TryGetTabOverride(prefabName, out var tab)
                                       && VAHammerTabOrganizer.TryResolveTabCategory(tab, out var cat)
                        ? cat : AutoCategorize(go);
                }
            }
            return onTable;
        }

        // 1.1.282 (unhide live): a hidden TAB comes back. Makes sure the category is on the table (same block as
        // RetagPieceCategoryByName), restores the listed prefabs, then re-adds every piece TryHideCategoryLive /
        // a hide took away that now routes to this tab (SortHammerByMod bundle tabs list no prefabs on disk).
        // Ends with RecategorizeEligiblePieces - the live re-bucket + build-menu refresh the config toggles use.
        public static int RestoreTabLive(string tabName, IEnumerable<string> listedPrefabs)
        {
            if (string.IsNullOrEmpty(tabName)) return 0;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return 0;

            if (!VAHammerTabOrganizer.IsVanillaTabName(tabName))
            {
                var cat = VACategoryRegistry.GetOrAdd(tabName);
                if (!pieceTable.m_categories.Contains(cat))
                {
                    pieceTable.m_categories.Add(cat);
                    pieceTable.m_categoryLabels.Add(VAHammerTabOrganizer.TryGetDisplayName(tabName, out var dn) ? dn : tabName);
                    int needed = (int)cat + 1;
                    while (PieceTableAccess.ByCategory(pieceTable).Count < needed)
                        PieceTableAccess.ByCategory(pieceTable).Add(new List<Piece>());
                }
            }

            int restored = 0;
            if (listedPrefabs != null)
                foreach (var name in listedPrefabs)
                    if (RestorePieceToHammerByName(name)) restored++;

            if (VAHammerTabOrganizer.TryResolveTabCategory(tabName, out var tabCat))
            {
                foreach (var name in _removedByHide.Keys.ToList())
                {
                    var list = _removedByHide[name];
                    bool routesHere = list.Any(go => (UnityEngine.Object)go != (UnityEngine.Object)null
                                                     && go.GetComponent<Piece>() != null
                                                     && (go.GetComponent<Piece>().m_category == tabCat
                                                         || (_eligibleByName.TryGetValue(name, out var e) && e == go && AutoCategorize(go) == tabCat)));
                    if (routesHere && !VAHammerTabOrganizer.IsIgnored(name) && RestorePieceToHammerByName(name)) restored++;
                }
            }

            RefreshAfterRestore();
            return restored;
        }

        // 1.1.282 (unhide live): RecategorizeEligiblePieces re-syncs the tabs and redraws; it no-ops before the
        // scan has eligible pieces, so the plain redraw RetagPieceCategoryByName uses runs then instead.
        public static void RefreshAfterRestore()
        {
            if (_initialized && _eligibleByName.Count > 0) { RecategorizeEligiblePieces(); return; }
            if (Player.m_localPlayer != null)
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
            VABuildUiCategoryTags.RequestBuildMenuRefresh();
        }

        // Live-retag a piece's m_category by prefab name. Auto-registers a
        // not-yet-known tab and grows the PieceTable's category lists to fit.
        // Returns the number of pieces re-tagged.
        public static int RetagPieceCategoryByName(string prefabName, string tabName)
        {
            if (string.IsNullOrEmpty(prefabName) || string.IsNullOrEmpty(tabName)) return 0;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return 0;

            var cat = VACategoryRegistry.GetOrAdd(tabName);
            if (!pieceTable.m_categories.Contains(cat))
            {
                pieceTable.m_categories.Add(cat);
                pieceTable.m_categoryLabels.Add(VAHammerTabOrganizer.TryGetDisplayName(tabName, out var dnTab2) ? dnTab2 : tabName);
                int needed = (int)cat + 1;
                while (PieceTableAccess.ByCategory(pieceTable).Count < needed)
                    PieceTableAccess.ByCategory(pieceTable).Add(new List<Piece>());
            }

            int matched = 0;
            foreach (var pieceObject in pieceTable.m_pieces)
            {
                if ((UnityEngine.Object)pieceObject == (UnityEngine.Object)null) continue;
                if (!string.Equals(pieceObject.name, prefabName, StringComparison.Ordinal)) continue;
                var piece = pieceObject.GetComponent<Piece>();
                if (piece == null) continue;
                piece.m_category = cat;
                matched++;
            }
            if (matched > 0 && Player.m_localPlayer != null)
            {
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
                VABuildUiCategoryTags.RequestBuildMenuRefresh();
            }
            return matched;
        }

        // Re-runs the stand-in registration against the current hammer
        // piece table. Called from VAExternalBundleLoader after
        // RegisterClutterFromManifests has drained additional bundle
        // entries into ClutterSystem.m_clutter, so late-arriving clutter
        // shows up in the hammer without a world reload. Idempotent.
        public static int RefreshClutterStandins()
        {
            if (!_initialized) return 0;
            var pieceTable = GetHammerPieceTable();
            if (pieceTable == null) return 0;
            VAClutterRegistry.Invalidate();
            int added = RegisterClutterAsStandins(pieceTable);
            if (added > 0)
            {
                if (Player.m_localPlayer != null)
                    VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
                if (ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.Log($"{VaPieces.PluginName}: RefreshClutterStandins added {added} late-arriving clutter stand-in(s).");
            }
            return added;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Admin gate
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Returns true if the local player may use the full hammer extension.
        // Deliberately avoids ZNet.LocalPlayerIsAdminOrHost() (its synced admin
        // list isn't reliably populated client-side when we check); instead
        // trusts IsServer() and the ConfigSync.IsAdmin lockExempt flag, both
        // set reliably during the initial config-sync handshake.
        // Server policy: put FAP's pieces on every client's hammer table even when the player
        // may not build with them, so mods that only recognise registered build pieces (and the
        // clones they make from them) work for everyone.
        private static bool ShouldRegisterPiecesForNonAdmins()
        {
            var config = ConfigManager.Instance;
            return config != null
                && config.configRegisterPiecesForNonAdmins != null
                && config.configRegisterPiecesForNonAdmins.Value;
        }

        public static bool IsAdminGateOpen()
        {
            // Config off -> gate is fully open, no admin check needed.
            if (!ConfigManager.Instance.configAdminOnlyMode.Value) return true;

            // 1.1.316 (Fire 10-23: "yes" - the admin-only hammer tabs follow the one admin answer): on a server WITHOUT FAP the tabs
            // need debugmode (was: open for everyone). VAFapServerPresence.LocalIsAdmin is that answer (host / debugmode / fap-server).
            if (VAFapServerPresence.IsClientOnly) return VAFapServerPresence.LocalIsAdmin(out _);

            // THE canonical Fires local-admin check. Unions IsServer(),
            // Valheim's own server-synced admin list, and Core's lockExempt
            // push, so whichever signal lands first grants admin. This gate
            // used to read ONLY our private ServerSync instance's IsAdmin  - 
            // a different ConfigSync from Core's, which on a real server can
            // simply never receive the grant even while Valheim and Core both
            // have it. That left a genuine admin gated out of their own hammer.
            try { if (FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return true; }
            catch { /* Core missing or ZNet mid-teardown -> fall through */ }

            // Our own ServerSync instance is a SEPARATE ConfigSync from Core's,
            // so keep it as one more signal rather than dropping a grant this
            // gate used to honour.
            try
            {
                var configSync = VaPieces.Instance?.configSync;
                if (configSync != null && configSync.IsAdmin) return true;
            }
            catch { }

            return false;
        }

        // Which of IsAdminGateOpen's signals holds it open, first match in its own order, for the logs.
        internal static string DescribeOpenGate()
        {
            if (ConfigManager.Instance == null || !ConfigManager.Instance.configAdminOnlyMode.Value) return "AdminOnlyMode is off";
            if (VAFapServerPresence.IsClientOnly) return VAFapServerPresence.LocalIsAdmin(out _) ? "client-only (no FAP on the server): debugmode is on" : "closed: client-only (no FAP on the server) and debugmode is off";
            try
            {
                if (FiresCore.Sync.AdminSyncing.IsLocalAdmin())
                    return ZNet.instance != null && ZNet.instance.IsServer() ? "this machine is the server" : "Core's admin check";
            }
            catch { }
            try
            {
                var configSync = VaPieces.Instance?.configSync;
                if (configSync != null && configSync.IsAdmin) return "FiresAdminPrefabs' own ConfigSync admin grant";
            }
            catch { }
            return "closed";
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Live re-categorize (triggered by config toggle changes)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Re-runs AutoCategorize on every eligible piece and refreshes the
        // piece list so tabs re-bucket without a world reload. Called when an
        // EWP-mode toggle flips, since the Modded vs Vanilla fx_/vfx_ split
        // depends on it.
        public static void RecategorizeEligiblePieces()
        {
            if (!_initialized || _eligibleByName.Count == 0) return;
            // 1.1.277: nothing to re-bucket while the world is going away (no local player / ObjectDB / hammer table)
            if (Player.m_localPlayer == null || GetHammerPieceTable() == null) return;

            // Re-bucketing the PIECES is not enough: which TABS exist is decided by
            // VanillaFriendly and the hide list too, and those live only in
            // m_categories. Without this the Clutter tab could never appear when
            // VanillaFriendly was turned off mid-session - the pieces moved into the
            // category and the menu repainted, but the category itself was still
            // missing from the table, so there was no tab to show them on.
            VACategoryRegistry.SyncCategories(GetHammerPieceTable());

            int moved = 0;
            foreach (var entry in _eligibleByName)
            {
                var prefab = entry.Value;
                if (prefab == null) continue;
                var piece = prefab.GetComponent<Piece>();
                if (piece == null) continue;
                var newCat = AutoCategorize(prefab);
                if (piece.m_category != newCat)
                {
                    piece.m_category = newCat;
                    moved++;
                }
            }
            // Rebuilding the lists is only half of it: BuildUi repaints when the piece count it
            // last saw changed, and a setting that only re-buckets or hides pieces can leave that
            // count identical - so the recorded count is cleared as well.
            if (Player.m_localPlayer != null)
                VerdantsAscent.Patches.HammerCategoryUI.RebuildPieceListIfHammer(Player.m_localPlayer, "VAVanillaPieceManager");
            Patches.VABuildUiCategoryTags.RequestBuildMenuRefresh();
            if (moved > 0 && ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: RecategorizeEligiblePieces moved {moved} pieces between tabs after config toggle.");
        }

        // Queues each eligible Piece for async icon rendering, pulling the
        // Piece off the GameObject fresh in case another mod rebound the
        // component reference between scan and now.
        private static void QueueIconRenders()
        {
            if (ConfigManager.Instance?.configRedrawSharedIcons?.Value == true) VAPieceIconRenderer.RedrawSharedIcons(new List<GameObject>(_eligibleByName.Values), GetHammerPieceTable()?.m_pieces);   // [saddles] 11-09
            int queued = 0;
            foreach (var prefab in _eligibleByName.Values)
            {
                if (prefab == null) continue;
                var piece = prefab.GetComponent<Piece>();
                if (piece == null || piece.m_icon != null) continue;
                VAPieceIconRenderer.Enqueue(piece);
                queued++;
            }

            // Clutter stand-ins aren't in _eligibleByName (they're FAP-owned
            // GameObjects with no ZNetScene presence), so enqueue them here.
            foreach (var entry in VAClutterStandinFactory.AllStandins)
            {
                var standin = entry.Value;
                if (standin == null) continue;
                var piece = standin.GetComponent<Piece>();
                if (piece == null || piece.m_icon != null) continue;
                VAPieceIconRenderer.Enqueue(piece);
                queued++;
            }

            // Monster stand-ins - same pattern as clutter; without this loop
            // the Monsters tab shows only vanilla's shield-icon fallback.
            foreach (var entry in VerdantsAscent.Utilities.VAMonsterStandinFactory.AllStandins)
            {
                var standin = entry.Value;
                if (standin == null) continue;
                var piece = standin.GetComponent<Piece>();
                if (piece == null || piece.m_icon != null) continue;
                VAPieceIconRenderer.Enqueue(piece);
                queued++;
            }

            if (queued > 0)
                Debug.Log($"{VaPieces.PluginName}: Queued {queued} pieces for icon render");

            EmitStandinSummary();
        }

        // The three stand-in factories all populate during this load phase, so their
        // totals are reported together once the icon queue is built.
        private static void EmitStandinSummary()
        {
            int items = 0, monsters = 0, clutter = 0;
            try
            {
                foreach (var _ in VerdantsAscent.Utilities.VAItemStandStandinFactory.AllStandins) items++;
                foreach (var _ in VerdantsAscent.Utilities.VAMonsterStandinFactory.AllStandins) monsters++;
                clutter = VerdantsAscent.Utilities.VAClutterPresetStore.All.Count;
            }
            catch { return; }

            if (items == 0 && monsters == 0 && clutter == 0) return;
            VerdantsAscent.Utilities.VAFapLoadSummary.EmitStandins(items, monsters, clutter);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Curated MVBP-parity injects
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Vanilla prefabs guaranteed onto the hammer regardless of the
        // generic scan (MoreVanillaBuildPrefabs parity, user-requested).
        // The user's explicit Ignore list still wins.
        private static readonly string[] _curatedVanillaInjects =
        {
            "rock4_coast", "rock4_forest", "rock4_heath", "stone_floor",
        };

        private static void EnsureCuratedVanillaInjects(
            ZNetScene scene, PieceTable pieceTable, ref int registered, ref int injected)
        {
            foreach (var name in _curatedVanillaInjects)
            {
                try
                {
                    var prefab = scene.GetPrefab(name);
                    if (prefab == null)
                    {
                        Debug.LogWarning($"{VaPieces.PluginName}: curated inject '{name}' - prefab not found in ZNetScene; skipped.");
                        continue;
                    }
                    if (VAHammerTabOrganizer.IsIgnored(name))
                    {
                        VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: curated inject '{name}' - on the user's Ignore list; respected.");
                        continue;
                    }
                    if (pieceTable.m_pieces.Contains(prefab)) continue;

                    if (ProcessEligibleForHammer(prefab, pieceTable, out bool fresh)) registered++;
                    if (fresh) injected++;
                    VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: curated inject '{name}' - added to the hammer (generic scan had missed it).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: curated inject '{name}' threw: {ex.Message}");
                }
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Filter logic
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Returns true if this prefab should be exposed as a hammer-buildable:
        // must have a ZNetView and must not be an "active entity" kind (mobs,
        // projectiles, item drops, world-system anchors). Also honors the
        // user's Ignore list, which hides a prefab from the hammer while
        // keeping it registered in ZNetScene.
        private static bool IsEligible(GameObject prefab, HashSet<string> existingBuildables)
            => IsEligible(prefab, existingBuildables, out _);

        // 1.1.317 ([fgn] for Fire 10-26: "make sure that fap is truly adding every buildable prefab to the Hammer"; the
        // Ashlands fortress spikes were missing). why = the rule that rejected it (null = eligible), for hammer_skipped.txt.
        private static bool IsEligible(GameObject prefab, HashSet<string> existingBuildables, out string why)
        {
            why = null;
            if ((UnityEngine.Object)prefab == (UnityEngine.Object)null) { why = "null"; return false; }

            // Ignore gate - checked early so we don't waste the rest
            // of the filter on a prefab the user has explicitly hidden.
            if (VAHammerTabOrganizer.IsIgnored(prefab.name)) { why = "on the HammerTabs Ignore list"; return false; }

            // 1.1.433: the FiresGlass windows are normal player pieces with a cost (VAFiresGlass puts them on the Hammer), never admin-hammer entries
            if (VAFiresGlass.IsWindow(prefab)) { why = "FiresGlass window (a player piece of its own, VAFiresGlass)"; return false; }

            // RemovedPrefabPlaceholders stubs (the removed castle tower) keep old saves loading; they are not pieces.
            if (VAExternalBundleLoader.IsRemovedPrefabPlaceholder(prefab)) { why = "removed-prefab placeholder"; return false; }

            // Skip-bundle gate - bundles whose tab folder was renamed to "Skip"
            // have all their prefabs filtered out, including future ones.
            if (VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out var srcBundleForSkip)
                && VAHammerTabOrganizer.IsBundleSkipped(srcBundleForSkip))
            {
                why = "its bundle's tab folder is named Skip";
                return false;
            }

            // Must be a root prefab - child GameObjects in the prefab list
            // are usually broken references and never make sense to place.
            if (prefab.transform.parent != null) { why = "not a root prefab"; return false; }

            // Skip anything already on a hammer/cultivator/hoe table.
            if (existingBuildables.Contains(prefab.name)) { why = "already on a hammer/hoe/cultivator table"; return false; }

            // Hard name rejects.
            if (_hardSkipNames.Contains(prefab.name)) { why = "hard skip name"; return false; }

            // Prefix rejects.
            foreach (var prefix in _skipPrefixes)
            {
                if (prefab.name.StartsWith(prefix, StringComparison.Ordinal))
                { why = "name starts with '" + prefix + "'"; return false; }
            }
            foreach (var needle in _skipContains)
            {
                if (prefab.name.IndexOf(needle, StringComparison.Ordinal) >= 0)
                { why = "name contains '" + needle + "'"; return false; }
            }

            // ZNetView is non-negotiable. Without it the placed piece has no
            // network identity and would simply vanish on the next zone reload.
            if (prefab.GetComponent<ZNetView>() == null) { why = "no ZNetView on the root"; return false; }

            // FAP-baked bundle prefabs carry VABakeParentLoader and intentionally
            // attach live interactive children (Container, Chair, ItemDrop) that
            // the per-component reject list below would otherwise screen out, so
            // short-circuit them to eligible.
            if (prefab.GetComponent<Utilities.VABakeParentLoader>() != null) return true;

            // ENTITY rejects walk the FULL hierarchy: bundle and modded prefabs nest creatures/fish on child GameObjects, and a
            // root-only check would let one slip through and run entity + Piece logic together (NRE spam every frame).
            if (HasAny<Humanoid>(prefab)) { why = "has Humanoid (in its hierarchy)"; return false; }
            if (HasAny<AnimalAI>(prefab)) { why = "has AnimalAI (in its hierarchy)"; return false; }
            if (HasAny<Character>(prefab)) { why = "has Character (in its hierarchy)"; return false; }
            // 1.1.350 (Fire 10-31: "a spawner placed from the hammer SHOULD spawn mobs"): a ROOT CreatureSpawner is a placeable
            // spawner (Spawner_Draugr ...); one nested in a bundle/modded prefab is still the NRE case above.
            if (!HasRoot<CreatureSpawner>(prefab) && HasAny<CreatureSpawner>(prefab)) { why = "has CreatureSpawner (nested, not the root)"; return false; }
            if (HasAny<Fish>(prefab)) { why = "has Fish (in its hierarchy)"; return false; }
            if (HasAny<RandomFlyingBird>(prefab)) { why = "has RandomFlyingBird (in its hierarchy)"; return false; }
            if (HasAny<Ragdoll>(prefab)) { why = "has Ragdoll (in its hierarchy)"; return false; }
            if (HasAny<TombStone>(prefab)) { why = "has TombStone (in its hierarchy)"; return false; }
            if (HasAny<Trader>(prefab)) { why = "has Trader (in its hierarchy)"; return false; }

            // 1.1.317: SYSTEM/EFFECT rejects check the ROOT only, like MoreVanillaBuildPrefabs (ShouldIgnorePrefab uses
            // prefab.GetComponent). Vanilla building pieces carry these on CHILDREN as part of the piece: the Ashlands fortress
            // spikes and the dvergr stakewalls have a damage Aoe child, siege pieces a CamShaker child, some pieces a
            // TerrainModifier child. The full-hierarchy check dropped all of those from the Hammer.
            if (HasRoot<Projectile>(prefab)) { why = "has Projectile (root)"; return false; }
            // 1.1.350 (Fire 10-31 "Add nests too"): SpawnArea (nests, bone piles) and TriggerSpawner are spawners, on the Hammer.
            if (HasRoot<MusicLocation>(prefab)) { why = "has MusicLocation (root)"; return false; }
            if (HasRoot<Aoe>(prefab)) { why = "has Aoe (root)"; return false; }
            if (HasRoot<ItemDrop>(prefab)) { why = "has ItemDrop (root)"; return false; }
            if (HasRoot<DungeonGenerator>(prefab)) { why = "has DungeonGenerator (root)"; return false; }
            if (HasRoot<TerrainModifier>(prefab)) { why = "has TerrainModifier (root)"; return false; }
            // 1.1.351 (Fire 10-31): event zones are placeable (VAEventZonePresets sets their event + radius).
            if (HasRoot<LocationProxy>(prefab)) { why = "has LocationProxy (root)"; return false; }
            if (HasRoot<LootSpawner>(prefab)) { why = "has LootSpawner (root)"; return false; }
            if (HasRoot<Mister>(prefab)) { why = "has Mister (root)"; return false; }
            if (HasRoot<LiquidVolume>(prefab)) { why = "has LiquidVolume (root)"; return false; }
            if (HasRoot<Gibber>(prefab) && !VerdantsAscent.Patches.VALootPieceMode.Debris.Contains(prefab.name)) { why = "has Gibber (root)"; return false; }   // 1.1.394: the 2 Morkhalla debris pieces are allowed (VALootPieceMode decor keeps them whole)
            if (HasRoot<ShipConstructor>(prefab)) { why = "has ShipConstructor (root)"; return false; }
            if (HasRoot<TeleportAbility>(prefab)) { why = "has TeleportAbility (root)"; return false; }
            if (HasRoot<CamShaker>(prefab)) { why = "has CamShaker (root)"; return false; }

            return true;
        }

        private static readonly List<string> s_skipped = new List<string>();

        // One file per scan, every prefab the Hammer scan rejected and why (already-buildable and child entries left out),
        // grouped by reason, plus one Debug line with the counts. Team cache convention: BepInEx\cache\<Mod>.
        private static void WriteSkippedReport(int scanned)
        {
            try
            {
                var byWhy = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var line in s_skipped)
                {
                    int tab = line.IndexOf('\t');
                    string why = line.Substring(0, tab), name = line.Substring(tab + 1);
                    if (!byWhy.TryGetValue(why, out var l)) byWhy[why] = l = new List<string>();
                    l.Add(name);
                }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# FiresAdminPrefabs " + global::VerdantsAscent.VaPieces.PluginVersion + $": prefabs NOT added to the Hammer and why ({s_skipped.Count} of {scanned} scanned; already-buildable and child prefabs not listed)");
                var counts = new List<string>();
                foreach (var kv in byWhy)
                {
                    kv.Value.Sort(StringComparer.OrdinalIgnoreCase);
                    sb.AppendLine().AppendLine($"## {kv.Key} ({kv.Value.Count})");
                    foreach (var n in kv.Value) sb.AppendLine(n);
                    counts.Add($"{kv.Key} {kv.Value.Count}");
                }
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresAdminPrefabs");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "hammer_skipped.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
                Debug.Log($"[FAP] hammer scan: {s_skipped.Count} prefabs not added ({string.Join("; ", counts)}); full list {path}");
                s_skipped.Clear();
            }
            catch (Exception ex) { Debug.LogWarning("[FAP] hammer_skipped.txt not written: " + ex.Message); }
        }

        // 1.1.350 (Fire 10-31, a player: "when placing the spiked walls only 3-4 of them place"). Vanilla Player.PieceRayTest
        // raycasts m_placeRayMask without a QueryTriggerInteraction and Valheim's physics settings hit triggers, so a placed
        // Ashlands_Fortress_Wall_Spikes ('HIT AREA' on layer piece) or dvergrtown_wood_stakewall (layer Default) catches the
        // ray with its invisible damage box and the next spike won't go beside it. Vanilla's own buildable spikes
        // (piece_dvergr_sharpstakes, dvergrprops_wood_stakewall) keep that trigger on character_trigger, outside the mask:
        // the same layer here. Aoe still damages through OnTriggerStay (character vs character_trigger, as sharpstakes).
        private static readonly string[] s_placeRayLayers = { "Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle" };   // Player.m_placeRayMask
        private static int s_spikePieces, s_spikeTriggers;

        private static void FixSpikeTriggerLayers(GameObject prefab)
        {
            var config = ConfigManager.Instance;
            if (config?.configSpikeTriggerLayerFix == null || !config.configSpikeTriggerLayerFix.Value) return;
            int target = LayerMask.NameToLayer("character_trigger");
            if (target < 0) return;
            int mask = LayerMask.GetMask(s_placeRayLayers);
            int moved = 0;
            foreach (var aoe in prefab.GetComponentsInChildren<Aoe>(true))
            {
                if (aoe == null || aoe.gameObject == prefab || !aoe.m_useTriggers) continue;
                var go = aoe.gameObject;
                if ((mask & (1 << go.layer)) == 0) continue;
                bool trigger = false;
                foreach (var c in go.GetComponents<Collider>()) if (c != null && c.isTrigger) { trigger = true; break; }
                if (!trigger) continue;
                VaPieces.Log?.LogDebug($"[FAP] spike trigger: {prefab.name}/{go.name} layer {LayerMask.LayerToName(go.layer)} -> character_trigger (in the placement ray mask; vanilla sharpstakes layer)");
                go.layer = target;
                moved++;
            }
            if (moved == 0) return;
            s_spikePieces++; s_spikeTriggers += moved;
        }

        // One Info proof line per scan (called after the scan loop).
        private static void ReportSpikeTriggerLayers()
        {
            if (s_spikePieces == 0) return;
            Debug.Log($"[FAP] spike triggers: {s_spikePieces} piece(s), {s_spikeTriggers} trigger(s) moved to character_trigger (vanilla sharpstakes layer), so the placement ray no longer lands on them");
            s_spikePieces = 0; s_spikeTriggers = 0;
        }

        // 1.1.350: placeable spawners (CreatureSpawner, SpawnArea nests / bone piles, TriggerSpawner) on the root.
        internal static bool IsSpawnerPrefab(GameObject prefab)
            => prefab != null && (HasRoot<CreatureSpawner>(prefab) || HasRoot<SpawnArea>(prefab) || HasRoot<TriggerSpawner>(prefab));

        private static bool HasRoot<T>(GameObject prefab) where T : Component
            => (UnityEngine.Object)prefab.GetComponent<T>() != (UnityEngine.Object)null;

        // True if the type appears anywhere in the prefab hierarchy.
        // includeInactive=true because many prefabs ship with inactive
        // GameObjects that the default GetComponentInChildren would skip.
        private static bool HasAny<T>(GameObject prefab) where T : Component
            => (UnityEngine.Object)prefab.GetComponentInChildren<T>(includeInactive: true) != (UnityEngine.Object)null;

        // Names of every prefab already registered as buildable on any active
        // PieceTable, so the scan doesn't add duplicates.
        // Map of "<name>_frac" -> base prefab for every fractured prefab whose
        // non-fractured counterpart is also in ZNetScene. Populated at scan
        // time, queried at placement time by HammerFracSwapPatch - a direct
        // lookup avoids GetPrefab's method dispatch on the hot path.
        private static readonly Dictionary<string, GameObject> _fracToBaseMap
            = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public static bool TryGetNonFracSwap(string fracName, out GameObject basePrefab)
            => _fracToBaseMap.TryGetValue(fracName, out basePrefab);

        // Builds _fracToBaseMap by scanning ZNetScene for every "<name>_frac"
        // entry whose non-_frac sibling is also present. Single O(n) pass
        // over m_prefabs (~3600 entries), runs once at init.
        private static void BuildFracToBaseMap(ZNetScene scene)
        {
            _fracToBaseMap.Clear();
            if (scene == null || scene.m_prefabs == null) return;

            var allByName = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var prefab in scene.m_prefabs)
            {
                if ((UnityEngine.Object)prefab != (UnityEngine.Object)null && !string.IsNullOrEmpty(prefab.name))
                    allByName[prefab.name] = prefab;
            }

            const string FracSuffix = "_frac";
            foreach (var entry in allByName)
            {
                var name = entry.Key;
                if (!name.EndsWith(FracSuffix, StringComparison.Ordinal)) continue;
                var baseName = name.Substring(0, name.Length - FracSuffix.Length);
                if (allByName.TryGetValue(baseName, out var basePrefab))
                    _fracToBaseMap[name] = basePrefab;
            }

            if (_fracToBaseMap.Count > 0)
                Debug.Log($"{VaPieces.PluginName}: built _frac->base swap map with {_fracToBaseMap.Count} entries");
        }

        private static HashSet<string> CollectAlreadyBuildablePrefabs()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in Resources.FindObjectsOfTypeAll<PieceTable>())
            {
                if (table == null || table.m_pieces == null) continue;
                foreach (var prefab in table.m_pieces)
                {
                    if ((UnityEngine.Object)prefab != (UnityEngine.Object)null)
                        set.Add(prefab.name);
                }
            }
            return set;
        }

        // 1.1.277 (NRE on world exit: the config live-reapply fired during ZNet teardown): "?." does not see a DESTROYED Unity object, so
        // ObjectDB / the Hammer prefab are checked with Unity's own null test; no ObjectDB or no hammer = no table.
        private static PieceTable GetHammerPieceTable()
        {
            try
            {
                var db = ObjectDB.instance;
                if (db == null) return null;
                var hammer = db.GetItemPrefab("Hammer");
                if (hammer == null) return null;
                var drop = hammer.GetComponent<ItemDrop>();
                if (drop == null) return null;
                return drop.m_itemData?.m_shared?.m_buildPieces;
            }
            catch (Exception) { return null; }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Piece injection - the main fix vs MVBP
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Adds a Piece (with placement-ghost-friendly flags) if the prefab
        // lacks one. Returns true if a fresh Piece was added; an existing
        // author's Piece keeps its flags.
        private static bool EnsurePieceComponent(GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            if ((UnityEngine.Object)piece != (UnityEngine.Object)null)
            {
                // Author already specified a Piece - respect their flags
                // entirely. We still apply our category default if they
                // forgot to set one (Max means "uninitialized").
                if (VerdantsAscent.Utilities.VAPieceCategoryRuntime.IsHidden(piece.m_category))
                    piece.m_category = Piece.PieceCategory.Misc;

                // Re-apply the user's disk tab layout on top of the authored
                // category every load - it's authoritative, and without this
                // re-application a move/hide reverts to the prefab's authored
                // category on the next world load. Resolves custom tabs AND
                // vanilla-category folder names.
                if (VAHammerTabOrganizer.TryGetTabOverride(prefab.name, out var overrideTab)
                    && VAHammerTabOrganizer.TryResolveTabCategory(overrideTab, out var overrideCat))
                {
                    piece.m_category = overrideCat;
                }
                else if (VAHammerTabOrganizer.IsIgnored(prefab.name))
                {
                    // User hid this prefab via Ignore/ or Hidden_<tab>/.
                    // Max is vanilla's "skip in UpdateAvailable" sentinel
                    // - the piece stays registered (functional via
                    // spawn commands, ZDO sync, etc.) but doesn't show
                    // in any hammer tab.
                    piece.m_category = VerdantsAscent.Utilities.VAPieceCategoryRuntime.Hidden;
                }
                return false;
            }

            piece = prefab.AddComponent<Piece>();

            // The injected-piece defaults. The two clip flags are the
            // load-bearing change vs MVBP - see the class header comment.
            piece.m_name = prefab.name;
            piece.m_description = BuildPieceDescription(prefab);
            piece.m_enabled = true;
            piece.m_category = AutoCategorize(prefab);
            // 1.1.328: a sound has nothing to render; give it the shared speaker icon now (PlanBuild mirrors every hammer piece and Jotunn
            // rejects one without an icon). Effects that can render keep getting their rendered icon.
            if (prefab.name.StartsWith("sfx_", StringComparison.Ordinal) && piece.m_icon == null) piece.m_icon = VerdantsAscent.Managers.VAPlaceholderIcons.Sound();

            piece.m_clipGround = true;
            piece.m_clipEverything = true;

            // Allow rotation overlap so chained snap-target placements
            // (e.g. stacking ferns into a hedge) don't get rejected on
            // bounding-box overlap with the previous piece.
            piece.m_allowRotatedOverlap = true;

            // Removable by default so the player can undo placements.
            piece.m_canBeRemoved = true;

            // Default these to safe values - none of the surface-restriction
            // flags should be set for a generic injected piece, otherwise
            // the player gets unexplained "can't build here" messages.
            piece.m_groundOnly = false;
            piece.m_groundPiece = false;
            piece.m_cultivatedGroundOnly = false;
            piece.m_waterPiece = false;
            piece.m_noInWater = false;
            piece.m_notOnWood = false;
            piece.m_notOnTiltingSurface = false;
            piece.m_inCeilingOnly = false;
            piece.m_notOnFloor = false;
            piece.m_onlyInTeleportArea = false;
            piece.m_allowedInDungeons = false;
            piece.m_repairPiece = false;
            piece.m_onlyInBiome = Heightmap.Biome.None;
            piece.m_randomTarget = false;

            // No crafting station required - placeable bare-handed once
            // recipes are configured (config layer to come).
            piece.m_craftingStation = null;

            // Empty requirements until per-prefab config gets wired in. An
            // empty array means "free placement", matching how vanilla
            // CreatorShop pieces work for admin builds.
            piece.m_resources = Array.Empty<Piece.Requirement>();

            return true;
        }

        // Forces ZNetView.m_persistent=true on injected pieces and FX so the
        // placement survives a zone reload (vanilla FX ships transient). EXCEPT
        // clutter, which must stay non-persistent - it's rendered via the
        // TerrainComp replay pipeline, and persisting it spawns invalid renamed
        // ZDOs that NRE-cascade on return-to-zone.
        private static void EnsurePersistentZNetView(GameObject prefab)
        {
            var nview = prefab.GetComponent<ZNetView>();
            if ((UnityEngine.Object)nview == (UnityEngine.Object)null) return;

            // Clutter exception - keep m_persistent=false. Belt-and-suspenders
            // setting (rather than just early-return) defends against any
            // other code path that might have flipped it back to true.
            if (IsClutterPrefab(prefab))
            {
                if (nview.m_persistent) nview.m_persistent = false;
                return;
            }

            if (nview.m_persistent) return;

            // 1.1.398 (Fire 11-07: phantom wet drops): a prefab FAP doesn't own (vanilla or another
            // mod's) is never changed. Flipping the shared vfx_Wet made every rain/swim wet effect a
            // saved ZDO that outlived its creature. The PLACED copy is made persistent instead, on its
            // own ZDO (PersistPlacedZdo), which ZNetView keeps on every reload.
            if (!IsFapBundlePrefab(prefab))
            {
                _persistOnPlaceHashes.Add(prefab.name.GetStableHashCode());
                return;
            }
            nview.m_persistent = true;
        }

        private static readonly HashSet<int> _persistOnPlaceHashes = new HashSet<int>();
        private static bool _persistOnPlaceLogged;

        // FAP's own bundle / baked prefabs: FAP owns them, so their prefab flag may change.
        private static bool IsFapBundlePrefab(GameObject prefab)
            => VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out _)
               || Utilities.VABakeBundle.TryGetCacheKeyForPrefab(prefab.name) != null;

        // True when a placement of this prefab must be saved although its shared prefab is
        // transient (see EnsurePersistentZNetView).
        public static bool PersistsOnPlace(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && _persistOnPlaceHashes.Contains(prefabName.GetStableHashCode());

        // Makes one placed object's ZDO persistent. Owner only; returns true when it changed.
        public static bool PersistPlacedZdo(ZDO zdo, string prefabName)
        {
            if (zdo == null || zdo.Persistent || !PersistsOnPlace(prefabName)) return false;
            zdo.Persistent = true;
            if (!_persistOnPlaceLogged)
            {
                _persistOnPlaceLogged = true;
                Debug.Log($"{VaPieces.PluginName}: placed '{prefabName}' saved on its own ZDO; {_persistOnPlaceHashes.Count} shared vanilla/mod prefab(s) on the hammer stay unchanged (transient when the game spawns them).");
            }
            return true;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Category auto-assignment
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Buckets a freshly-injected piece into a hammer tab: fx_/vfx_/sfx_ ->
        // FX/VFX (split Vanilla vs Modded by IsVanillaCompatibleEffect), nature
        // prefabs -> Nature, everything else -> CreatorShop. Runs the vanilla-
        // compat check before EnsurePersistentZNetView mutates the prefab, so
        // it sees the untouched state a vanilla client would.
        private static Piece.PieceCategory AutoCategorize(GameObject prefab)
        {
            if (prefab == null) return Piece.PieceCategory.Misc;
            var name = prefab.name;

            // Disk-driven override (HIGHEST priority). A prefab the user listed
            // in a HammerTabs/<TabName>/prefabs.txt file routes to that tab,
            // overriding every classifier below.
            if (VAHammerTabOrganizer.TryGetTabOverride(name, out var overrideTab)
                && VAHammerTabOrganizer.TryResolveTabCategory(overrideTab, out var overrideCat))
            {
                return overrideCat;
            }

            // Baked-mesh routing - second priority. The VABakeParentLoader
            // component is unique to baked bundle prefabs, so it's a reliable
            // type-based identifier that can't collide with a name prefix. Sits
            // above clutter/SortHammerByMod/FX so a baked blueprint is never
            // mis-routed by a coincidental match.
            if (prefab.GetComponent<Utilities.VABakeParentLoader>() != null
                && VACategoryRegistry.TryGet(VACategoryRegistry.CatBakedMeshes, out var bakedCat))
            {
                return bakedCat;
            }

            // Clutter routing - third priority. Clutter-identified prefabs land
            // in the dedicated Clutter tab.
            if (IsClutterPrefab(prefab)
                && VACategoryRegistry.TryGet(VACategoryRegistry.CatClutter, out var clutterCat))
            {
                return clutterCat;
            }

            // 1.1.350 (Fire 10-31: "we have a whole monsters tab"): spawners and nests go in the Monsters tab.
            if (IsSpawnerPrefab(prefab)
                && VACategoryRegistry.TryGet(VACategoryRegistry.CatMonsters, out var monstersTab))
            {
                return monstersTab;
            }

            // Sort-hammer-by-mod override: with the toggle on, a bundle prefab
            // routes to a tab named after its source bundle (honoring any user
            // rename via the bundle alias). Vanilla prefabs are unaffected.
            if (ConfigManager.Instance.configSortHammerByMod.Value
                && VAExternalBundleLoader.TryGetSourceBundle(name, out var bundleName)
                && !string.IsNullOrEmpty(bundleName))
            {
                string targetTab = bundleName;
                if (VAHammerTabOrganizer.TryGetBundleAlias(bundleName, out var aliased)
                    && !string.IsNullOrEmpty(aliased))
                {
                    targetTab = aliased;
                }

                // Hidden-tab gate. Returning PieceCategory.Max (which vanilla's
                // UpdateAvailable skips) keeps a hidden tab hidden across
                // sessions instead of SortHammerByMod recreating it on reload.
                if (VAHammerTabOrganizer.IsTabHidden(targetTab))
                    return VerdantsAscent.Utilities.VAPieceCategoryRuntime.Hidden;

                if (VACategoryRegistry.TryGet(targetTab, out var bundleCat))
                    return bundleCat;
            }

            bool vanillaFriendly = ConfigManager.Instance.configVanillaFriendly.Value;

            // FX/VFX routing - one tab per effect class. Vanilla-friendly also files effects a
            // vanilla client can't keep under Max; the build menu asks IsVanillaUnsafeEffect
            // itself, because the disk tab layout re-stamps categories on every UpdateAvailable.
            if (name.StartsWith("vfx_", StringComparison.Ordinal))
            {
                if (vanillaFriendly && IsVanillaUnsafeEffect(prefab))
                    return VerdantsAscent.Utilities.VAPieceCategoryRuntime.Hidden;
                if (VACategoryRegistry.TryGet(VACategoryRegistry.CatVFX, out var vfx))
                    return vfx;
                return Piece.PieceCategory.Misc;
            }

            if (name.StartsWith("fx_", StringComparison.Ordinal) ||
                name.StartsWith("sfx_", StringComparison.Ordinal))
            {
                if (vanillaFriendly && IsVanillaUnsafeEffect(prefab))
                    return VerdantsAscent.Utilities.VAPieceCategoryRuntime.Hidden;
                if (VACategoryRegistry.TryGet(VACategoryRegistry.CatFX, out var fx))
                    return fx;
                return Piece.PieceCategory.Misc;
            }

            if (IsNaturePrefab(prefab))
            {
                if (VACategoryRegistry.TryGet(VACategoryRegistry.CatNature, out var nature))
                    return nature;
                return Piece.PieceCategory.Misc;
            }

            if (VACategoryRegistry.TryGet(VACategoryRegistry.CatCreatorShop, out var shop))
                return shop;
            return Piece.PieceCategory.Misc;
        }

        // Play-tested effect prefabs that ARE vanilla-friendly despite failing
        // the strict component check below - their primary visual loops fine on
        // vanilla even when a sub-particle burst doesn't.
        private static readonly HashSet<string> _vanillaFriendlyEffectAllowlist =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "fx_fallenvalkyrie_death",
                "fx_Lightning",
                "vfx_Burning",
                "vfx_Cold",
                "vfx_darkland_groundfog",
                "vfx_edge_clouds",
                "vfx_Freezing",
                "vfx_Frost",
                "vfx_GoblinShield",
                "vfx_mistlands_mist",
                "vfx_ocean_clouds",
                "vfx_Smoked",
                "vfx_StaffShield",
                "vfx_swamp_mist",
                "vfx_Tared",
                "vfx_TrollPheromones",
                "vfx_UndeadBurn",
                "vfx_Wet",
            };

        // True if an effect prefab will display correctly on a vanilla client.
        // Tier 1 is the user allowlist above; tier 2 rejects the two things a
        // vanilla client can't compensate for - a TimedDestruction in the
        // subtree (would self-destruct) or any non-looping finite-duration
        // ParticleSystem (would stop emitting after its duration).
        // The one answer to "would this effect still be there for a client without FAP" - the
        // categoriser, the piece description and the build menu all ask through here. False for
        // anything that isn't an effect, and false once the server persists effects via EWP,
        // which replays them for every client regardless.
        internal static bool IsVanillaUnsafeEffect(GameObject prefab)
        {
            if (prefab == null) return false;
            if (!IsEffectPrefabName(prefab.name)) return false;
            if (EwpHandlesPersistence()) return false;
            return !IsVanillaCompatibleEffect(prefab);
        }

        private static bool IsEffectPrefabName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && (name.StartsWith("vfx_", StringComparison.Ordinal)
                    || name.StartsWith("fx_", StringComparison.Ordinal)
                    || name.StartsWith("sfx_", StringComparison.Ordinal));
        }

        private static bool EwpHandlesPersistence()
        {
            var config = ConfigManager.Instance;
            return config != null
                && config.configUseEwpForPersistence != null
                && config.configUseEwpForPersistence.Value
                && VerdantsAscent.Utilities.VAEwpSoftDep.For(config.configEwpEffects);   // 1.1.279: per-kind toggle
        }

        private static HashSet<string> _effectAllowlistCache;

        // The built-in list plus whatever the server added in VanillaFriendlyEffectAllowlist. A
        // name written with a leading '-' drops a built-in, for an effect that turns out not to
        // survive on a vanilla client after all.
        private static HashSet<string> VanillaFriendlyEffects()
        {
            if (_effectAllowlistCache != null) return _effectAllowlistCache;

            var effects = new HashSet<string>(_vanillaFriendlyEffectAllowlist, StringComparer.Ordinal);
            string configured = ConfigManager.Instance?.configVanillaFriendlyEffectAllowlist?.Value;
            if (!string.IsNullOrEmpty(configured))
            {
                foreach (var token in configured.Split(','))
                {
                    string name = token.Trim();
                    if (name.Length == 0) continue;
                    if (name[0] == '-') effects.Remove(name.Substring(1).Trim());
                    else effects.Add(name);
                }
            }

            _effectAllowlistCache = effects;
            return effects;
        }

        public static void InvalidateVanillaFriendlyEffects() => _effectAllowlistCache = null;

        private static bool IsVanillaCompatibleEffect(GameObject prefab)
        {
            if (prefab == null) return false;

            // Tier 1: allowlist short-circuit.
            if (VanillaFriendlyEffects().Contains(prefab.name))
                return true;

            // Tier 2: strict component check.
            if (prefab.GetComponentInChildren<TimedDestruction>(includeInactive: true) != null)
                return false;

            var particles = prefab.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < particles.Length; i++)
            {
                var particleSystem = particles[i];
                if (particleSystem == null) continue;
                var main = particleSystem.main;
                if (!main.loop && main.duration > 0f) return false;
            }

            return true;
        }

        // True if any ParticleSystem is non-looping with a finite duration  - 
        // i.e. on a vanilla client it stops emitting after its duration. Gates
        // the m_description hover note about partial-loop behavior.
        private static bool HasNonLoopingParticleSystem(GameObject prefab)
        {
            if (prefab == null) return false;
            var particles = prefab.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
            for (int i = 0; i < particles.Length; i++)
            {
                var particleSystem = particles[i];
                if (particleSystem == null) continue;
                var main = particleSystem.main;
                if (!main.loop && main.duration > 0f) return true;
            }
            return false;
        }

        // Builds a piece's m_description. Most get an empty string; the
        // exceptions are advisory (yellow) notes flagging vanilla-compatibility
        // concerns - clutter, modded-only effects, and effects whose particles
        // may not loop on clients without this mod.
        private static string BuildPieceDescription(GameObject prefab)
        {
            if (prefab == null) return string.Empty;

            string name = prefab.name;
            // Clutter - painted positions live in our TerrainComp ZDO bytes
            // that vanilla clients don't read. Vanilla clutter still spawns
            // naturally there (just not our exact patches); modded clutter
            // has no such fallback.
            if (IsClutterPrefab(prefab))
            {
                bool isVanillaClutter = IsRegisteredInClutterSystem(prefab);
                if (isVanillaClutter)
                {
                    return "<color=#ffaa00>Vanilla clutter. Painted positions visible to FiresAdminPrefabs users only. " +
                           "Vanilla clients still see this clutter via natural biome spawning, but not your exact patches.</color>";
                }
                return "<color=#ffaa00>Modded clutter. Requires FiresAdminPrefabs on all clients to display. " +
                       "Vanilla clients will not see this clutter.</color>";
            }

            bool isEffectName =
                name.StartsWith("vfx_", StringComparison.Ordinal) ||
                name.StartsWith("fx_", StringComparison.Ordinal) ||
                name.StartsWith("sfx_", StringComparison.Ordinal);

            if (!isEffectName) return string.Empty;

            bool vanillaCompat = IsVanillaCompatibleEffect(prefab);
            if (!vanillaCompat)
            {
                return "<color=#ffaa00>Requires FiresAdminPrefabs to display correctly. " +
                       "Vanilla clients will not see this effect persist.</color>";
            }

            if (HasNonLoopingParticleSystem(prefab))
            {
                return "<color=#ffaa00>Vanilla-friendly. " +
                       "Note: some particles may not loop on clients without FiresAdminPrefabs.</color>";
            }

            return string.Empty;
        }

        // True if the prefab is in vanilla's ClutterSystem.m_clutter list.
        // Used by BuildPieceDescription to pick the right vanilla-compat hint.
        private static bool IsRegisteredInClutterSystem(GameObject prefab)
        {
            if (prefab == null) return false;
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return false;
            foreach (var entry in clutterSystem.m_clutter)
            {
                if (entry?.m_prefab != null && entry.m_prefab == prefab) return true;
            }
            return false;
        }

        // Lazily-built cache of ClutterSystem biome-clutter prefab names, keyed
        // by ClutterSystem.instance.GetInstanceID so a rebuild (EWD et al.)
        // re-populates it.
        private static HashSet<string> _clutterSystemNames;
        private static int _clutterSystemSourceId = -1;

        // Public alias for cross-file callers (the placement patch asks "is this
        // clutter?" at click time). Delegates to the private classifier.
        public static bool IsClutterPrefab_Public(GameObject prefab)
            => IsClutterPrefab(prefab);

        // Cross-file invalidation entry point. Called after late entries are
        // appended to ClutterSystem.m_clutter, so the lazy cache doesn't miss
        // them and route a prefab to the wrong hammer tab.
        public static void InvalidateClutterCache()
        {
            _clutterSystemNames = null;
            _clutterSystemSourceId = -1;
            VAClutterRegistry.Invalidate();
            // Idempotent + safe pre-init; picks up late bundle clutter so the
            // hammer Clutter tab stays in sync with ClutterSystem.m_clutter.
            RefreshClutterStandins();
        }

        private static bool IsClutterPrefab(GameObject prefab)
        {
            if (prefab == null) return false;

            // 1.1.199 (Fire 10-09: benches, paintings, rugs, tables in the Clutter tab): Clutter is ONLY instanced
            // vegetation, i.e. an InstanceRenderer or a ClutterSystem entry. A bundle's "clutter" manifest category or the
            // BundleClutterAllOf list no longer pulls ordinary objects in; those go to CreatorShop like any other prefab.

            // 1. InstanceRenderer anywhere in the hierarchy (GetComponentInChildren
            //    so nested bundle renderers count, matching the router's own
            //    detection). Reached for vanilla and non-Clutter-routed bundles.
            if (prefab.GetComponentInChildren<InstanceRenderer>(includeInactive: true) != null) return true;

            // 2. ClutterSystem membership
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem != null && clutterSystem.m_clutter != null)
            {
                int clutterSourceId = clutterSystem.GetInstanceID();
                if (_clutterSystemNames == null || _clutterSystemSourceId != clutterSourceId)
                {
                    _clutterSystemSourceId = clutterSourceId;
                    _clutterSystemNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in clutterSystem.m_clutter)
                    {
                        if (entry?.m_prefab != null && !string.IsNullOrEmpty(entry.m_prefab.name))
                            _clutterSystemNames.Add(entry.m_prefab.name);
                    }
                }
                if (_clutterSystemNames.Contains(prefab.name)) return true;
            }

            return false;
        }

        // Heuristic for "this is a nature prefab" - structural signals
        // (Pickable/MineRock/Plant/Tree components) first because they're more
        // reliable than the name-keyword fallback, then name keywords.
        internal static bool IsNaturePrefab(GameObject prefab)
        {
            if (prefab.GetComponent<Pickable>() != null) return true;
            if (prefab.GetComponent<MineRock>() != null) return true;
            if (prefab.GetComponent<MineRock5>() != null) return true;
            if (prefab.GetComponent<Plant>() != null) return true;

            // Whole-rock prefabs (rock4_coast etc.) carry Destructible only  - 
            // the MineRock5 lives on their _frac counterpart - so route by the
            // vanilla rock naming too.
            if (prefab.name.StartsWith("rock", StringComparison.OrdinalIgnoreCase)) return true;

            // TreeBase / TreeLog - bundle trees usually carry one or both.
            // GetComponentInChildren in case the author nested them under a
            // child node (missed by a root-only check on some bundle trees).
            if (prefab.GetComponentInChildren<TreeBase>(includeInactive: true) != null) return true;
            if (prefab.GetComponentInChildren<TreeLog>(includeInactive: true) != null) return true;

            if (prefab.GetComponent<Destructible>() != null
                && prefab.name.StartsWith("Stub", StringComparison.OrdinalIgnoreCase))
                return true;

            var prefabName = prefab.name;
            string[] needles = { "tree", "Tree", "bush", "Bush", "flower", "Flower",
                "plant", "Plant", "berry", "Berry", "mushroom", "Mushroom",
                "Branch", "Log", "Stump", "Sapling", "Seed",
                // Common tree species names (bundle trees often omit "Tree").
                // Matched case-sensitively so "Oak" in "OakStump" hits but
                // "oak" in "smokestack" doesn't.
                "Oak", "Birch", "Pine", "Fir", "Spruce", "Maple",
                "Cedar", "Willow", "Cypress", "Cherry", "Aspen",
                "Beech", "Ash", "Elm", "Walnut", "Linden", "Chestnut",
                "Sycamore", "Poplar", "Hawthorn", "Acacia", "Palm",
                "Bamboo", "Vine", "Ivy", "Fern", "Moss", "Reed",
                "Grass", "Foliage", "Leaf", "Leaves" };
            foreach (var needle in needles)
            {
                if (prefabName.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Procedural snap points
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Derives snap points procedurally from the combined renderer bounds
        // (bottom-face corners + center, top-face corners) so scanned prefabs
        // and modded vegetation can chain placements without hand-tuning. Skips
        // any prefab that already has author-placed snap points.
        // Guards bundle Fireplaces shipping m_canRefill=true with a null
        // m_fuelItem (NREs on hover) by injecting a fallback fuel rather than
        // disabling refill. Walks the full subtree; idempotent.
        private static void NeutralizeBrokenFireplace(GameObject prefab)
        {
            if (prefab == null) return;
            var fireplaces = prefab.GetComponentsInChildren<Fireplace>(includeInactive: true);
            for (int i = 0; i < fireplaces.Length; i++)
            {
                NeutralizeBrokenFireplaceInstance(fireplaces[i], prefab.name);
            }
        }

        // Default fuel order - Resin first (lantern-like, the most
        // common bundle case), Coal second, Wood third. Resolved once
        // per session and cached so the per-instance Awake patch
        // doesn't re-walk ObjectDB on every Fireplace spawn.
        private static readonly string[] _defaultFuelOrder = { "Resin", "Coal", "Wood" };
        private static ItemDrop _cachedFallbackFuel;
        private static bool _fallbackFuelLookupAttempted;

        private static ItemDrop ResolveFallbackFuelItem()
        {
            if (_cachedFallbackFuel != null) return _cachedFallbackFuel;
            // Re-attempt resolution if ObjectDB has woken up since
            // the last failed attempt - cheap retry, ObjectDB is a
            // single field read.
            if (_fallbackFuelLookupAttempted && _cachedFallbackFuel == null)
            {
                // Allow re-try on each call until we get a hit, but
                // bail fast if ObjectDB still isn't ready.
                if (ObjectDB.instance == null) return null;
            }
            _fallbackFuelLookupAttempted = true;
            var objectDb = ObjectDB.instance;
            if (objectDb == null) return null;
            for (int i = 0; i < _defaultFuelOrder.Length; i++)
            {
                var fuelPrefab = objectDb.GetItemPrefab(_defaultFuelOrder[i]);
                if (fuelPrefab == null) continue;
                var drop = fuelPrefab.GetComponent<ItemDrop>();
                if (drop == null) continue;
                if (drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;
                _cachedFallbackFuel = drop;
                return drop;
            }
            return null;
        }

        // Shared per-component fix used by both the prefab-time pass and the
        // Awake-time safety net (Fireplace_Awake_VANullGuard_Patch). Both are
        // needed: prefab mutation only covers future instantiations, while
        // already-placed instances need the per-instance Awake patch.
        internal static void NeutralizeBrokenFireplaceInstance(Fireplace fp, string ownerName)
        {
            if (fp == null) return;
            if (!fp.m_canRefill) return;
            if (fp.m_fuelItem != null
                && fp.m_fuelItem.m_itemData != null
                && fp.m_fuelItem.m_itemData.m_shared != null)
            {
                return;  // data is healthy - leave alone
            }

            // Inject a fallback fuel item so the piece stays refillable. If
            // ObjectDB isn't ready yet, bail without mutating - the Awake
            // postfix retries per-instance once ObjectDB is live.
            var fallback = ResolveFallbackFuelItem();
            if (fallback != null)
            {
                fp.m_fuelItem = fallback;
                if (ConfigManager.Instance != null
                    && ConfigManager.Instance.configVerboseLogging.Value)
                {
                    Debug.Log($"{VaPieces.PluginName}: NeutralizeBrokenFireplaceInstance - '{ownerName}' had Fireplace with null m_fuelItem; injected fallback '{fallback.gameObject.name}' so refill/hover works without NRE.");
                }
                return;
            }

            // Last-resort: if no fallback fuel resolved (extremely unusual),
            // flip refill off so the piece is at least non-crashing.
            fp.m_canRefill = false;
            if (ConfigManager.Instance != null
                && ConfigManager.Instance.configVerboseLogging.Value)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: NeutralizeBrokenFireplaceInstance - '{ownerName}' had Fireplace with null m_fuelItem AND no fallback fuel resolvable from ObjectDB (Resin/Coal/Wood missing). Flipped m_canRefill=false as last resort.");
            }
        }

        // The generic pass, for prefabs with no snap layout. This one KEEPS
        // the "already has snaps" bail: inventing bounds corners on a piece
        // someone authored snaps for would fight the author. The layouts in
        // 1.1.320 (Fire 10-26, Searica parity: "ONE centre Origin point"): a prop with no layout and no snap points of its own gets
        // exactly what ExtraSnapPointsMadeEasy gives it, one "Origin" point at the pivot (ExtraSnapsAdder: Origin at (0,0,0)), so its
        // starting snap point is the pivot, not an invented bounds corner. The old bounds-corner pass is kept below, unused.
        private static void EnsureSnapPoints(GameObject prefab)
        {
            Snapping.VASnapGeometry.Apply(prefab);
        }

        private static void EnsureBoundsSnapPoints(GameObject prefab)
        {
            if (HasExistingSnapPoints(prefab)) return;

            var bounds = ComputeLocalBounds(prefab);
            if (bounds.size.sqrMagnitude < MinSnapBoundsSqrSize) return;

            // Half-extents along each axis, used to position corners
            // relative to the prefab's root pivot.
            var halfX = bounds.extents.x;
            var halfY = bounds.extents.y;
            var halfZ = bounds.extents.z;
            var centerY = bounds.center.y;

            float yMin = centerY - halfY;
            float yMax = centerY + halfY;

            // Bottom face corners - these are the ones that matter for
            // ground-snapping and floor-edge chains.
            AddSnapPoint(prefab, new Vector3(-halfX, yMin, -halfZ));
            AddSnapPoint(prefab, new Vector3(halfX, yMin, -halfZ));
            AddSnapPoint(prefab, new Vector3(-halfX, yMin, halfZ));
            AddSnapPoint(prefab, new Vector3(halfX, yMin, halfZ));
            AddSnapPoint(prefab, new Vector3(0f, yMin, 0f));

            // Top face corners - only emit if the prefab actually has
            // vertical span (skip ground-flat decals).
            if (halfY > TopSnapMinHalfHeight)
            {
                AddSnapPoint(prefab, new Vector3(-halfX, yMax, -halfZ));
                AddSnapPoint(prefab, new Vector3(halfX, yMax, -halfZ));
                AddSnapPoint(prefab, new Vector3(-halfX, yMax, halfZ));
                AddSnapPoint(prefab, new Vector3(halfX, yMax, halfZ));
            }
        }

        private static bool HasExistingSnapPoints(GameObject prefab)
        {
            int childCount = prefab.transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var child = prefab.transform.GetChild(i);
                if (child != null && child.CompareTag("snappoint"))
                    return true;
            }
            return false;
        }

        // Combined bounds of every renderer on this prefab, in PREFAB-LOCAL
        // space (so the values are stable regardless of where the prefab
        // template currently sits in the inactive prefab container).
        private static Bounds ComputeLocalBounds(GameObject prefab)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers == null || renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.zero);

            var rootInverse = prefab.transform.worldToLocalMatrix;
            bool initialized = false;
            var combined = new Bounds(Vector3.zero, Vector3.zero);

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                // Skip particle systems - their bounds balloon out into
                // emit volumes and would produce nonsense snap points.
                if (renderer is ParticleSystemRenderer) continue;

                var worldBounds = renderer.bounds;
                var localCenter = rootInverse.MultiplyPoint3x4(worldBounds.center);
                // World-space size used as-is - snap points don't need sub-pixel
                // accuracy, so the rotation error from skipping the AABB-of-AABB
                // transform is acceptable.
                var localBounds = new Bounds(localCenter, worldBounds.size);

                if (!initialized)
                {
                    combined = localBounds;
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(localBounds);
                }
            }

            return combined;
        }

        private static void AddSnapPoint(GameObject prefab, Vector3 localPosition, string name = "_snappoint")
        {
            var snap = new GameObject(name);
            snap.tag = "snappoint";
            snap.transform.SetParent(prefab.transform, worldPositionStays: false);
            snap.transform.localPosition = localPosition;
            snap.transform.localRotation = Quaternion.identity;
            // Snap points are tag-found, not visibility-found - leaving
            // them active is the convention in vanilla and costs nothing.
            snap.SetActive(true);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Helpers
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Returns the prefab name with the "(Clone)" suffix Unity tacks on
        // when Instantiate fires, so callers can compare instance names to
        // prefab dictionary keys uniformly.
        private static string StripCloneSuffix(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return instanceName;
            int idx = instanceName.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? instanceName.Substring(0, idx).TrimEnd() : instanceName;
        }
    }
}
