using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Patches
{
    // Deletes orphan ZDOs whose prefab name matches a ClutterSystem entry.
    // Vanilla regenerates clutter procedurally on every zone load, so any
    // persisted ZDO is by definition garbage — typically left behind by an
    // older mod version that gave a clutter prefab a transient ZNetView.
    //
    // Runs once per session, server-side only; the destroy IS the cleanup
    // (clutter respawns from ClutterSystem on next zone load).
    [HarmonyPatch(typeof(ZoneSystem), "Start")]
    public static class ZoneSystem_Start_VAClutterOrphanCleanup_Patch
    {
        private const string LogPrefix = "[FAP-OrphanClutter]";
        private const double FrameBudgetMs = 2.0;
        private static bool _ranThisSession;

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (_ranThisSession) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ClutterSystem.instance == null) return;
            if (ZDOMan.instance == null) return;

            _ranThisSession = true;

            // Defer two frames so sibling ClutterSystem.Awake postfixes
            // (e.g. Balrond's WorldGenPatches) finish appending entries
            // before we snapshot the name list.
            if (VaPieces.Instance != null)
                VaPieces.Instance.StartCoroutine(RunDelayed());
            else
                RunCleanup();
        }

        // Each name is a full sweep of the world's ZDOs (vanilla's iterative search, 400 sectors a step). Run back to back
        // they froze one frame for 3.5 s on a 3.75M-ZDO world, so the steps are spread over frames within a budget.
        private static IEnumerator RunDelayed()
        {
            yield return null;
            yield return null;
            var zm = ZDOMan.instance;
            var clutterNames = ClutterNames();
            if (zm == null || clutterNames.Count == 0) yield break;

            var temp = new List<ZDO>();
            var watch = Stopwatch.StartNew();
            int totalDestroyed = 0;
            int nameMatchesWithOrphans = 0;
            for (int n = 0; n < clutterNames.Count; n++)
            {
                temp.Clear();
                int index = 0;
                while (!zm.GetAllZDOsWithPrefabIterative(clutterNames[n], temp, ref index))
                {
                    if (watch.Elapsed.TotalMilliseconds < FrameBudgetMs) continue;
                    yield return null;
                    if (ZDOMan.instance != zm) yield break;
                    watch.Restart();
                }
                int destroyed = DestroyAll(zm, temp);
                if (destroyed > 0) nameMatchesWithOrphans++;
                totalDestroyed += destroyed;
            }
            Report(totalDestroyed, nameMatchesWithOrphans, clutterNames.Count);
        }

        public static int RunCleanup()
        {
            var zm = ZDOMan.instance;
            var clutterNames = ClutterNames();
            if (zm == null || clutterNames.Count == 0) return 0;

            int totalDestroyed = 0;
            int nameMatchesWithOrphans = 0;
            var temp = new List<ZDO>();
            for (int n = 0; n < clutterNames.Count; n++)
            {
                temp.Clear();
                int index = 0;
                while (!zm.GetAllZDOsWithPrefabIterative(clutterNames[n], temp, ref index)) { }
                int destroyed = DestroyAll(zm, temp);
                if (destroyed > 0) nameMatchesWithOrphans++;
                totalDestroyed += destroyed;
            }
            Report(totalDestroyed, nameMatchesWithOrphans, clutterNames.Count);
            return totalDestroyed;
        }

        private static List<string> ClutterNames()
        {
            var cs = ClutterSystem.instance;
            var clutterNames = new List<string>();
            if (cs == null || cs.m_clutter == null) return clutterNames;
            for (int i = 0; i < cs.m_clutter.Count; i++)
            {
                var c = cs.m_clutter[i];
                if (c?.m_prefab == null) continue;
                string n = c.m_prefab.name;
                if (!string.IsNullOrEmpty(n)) clutterNames.Add(n);
            }
            return clutterNames;
        }

        private static int DestroyAll(ZDOMan zm, List<ZDO> zdos)
        {
            long myId = ZDOMan.GetSessionID();
            int destroyed = 0;
            for (int i = 0; i < zdos.Count; i++)
            {
                var z = zdos[i];
                if (z == null) continue;

                if (!z.IsOwner())
                {
                    try { z.SetOwner(myId); } catch { }
                }

                try { zm.DestroyZDO(z); destroyed++; }
                catch { }
            }
            return destroyed;
        }

        private static void Report(int totalDestroyed, int nameMatchesWithOrphans, int scanned)
        {
            if (totalDestroyed > 0)
                Debug.Log($"{LogPrefix} purged {totalDestroyed} orphan clutter ZDO(s) across {nameMatchesWithOrphans} prefab(s) (scanned {scanned} ClutterSystem entries).");
        }
    }
}
