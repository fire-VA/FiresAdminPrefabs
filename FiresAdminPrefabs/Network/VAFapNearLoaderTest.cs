using System.Collections;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Network
{
    // 1.1.385 (Fire 11-05: "if the tower loader needs to be tested ... get it tested"): fap_near_loader_test: places a ConsMassiveTower 80 m away, waits for the chunks, teleports 700 m off,
    // waits for the release, comes back, waits again, then removes the tower it placed. Refuses VA DN and any world but RPGWorld / SuperFlat. No visual check: counts the loader's own events.
    internal static class VAFapNearLoaderTest
    {
        private const string Tag = "[FapNearLoaderTest]";
        private const float StepSeconds = 90f;
        private static bool s_running;

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fap_near_loader_test", "[F5 test] [FAP] castle tower near loader: place, near, far, back, remove (RPGWorld / SuperFlat only)",
                args =>
                {
                    var ctx = args.Context;
                    if (s_running || VaPieces.Instance == null) return;
                    string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "";
                    if (world != "RPGWorld" && !world.StartsWith("SuperFlat", System.StringComparison.Ordinal)) { VaPieces.Log?.LogWarning($"{Tag} refused: world '{world}'"); ctx?.AddString($"{Tag} refused: world '{world}'"); return; }
                    if (Player.m_localPlayer == null || ZNetScene.instance == null) { VaPieces.Log?.LogWarning($"{Tag} refused: no player"); return; }
                    VaPieces.Instance.StartCoroutine(Run(ctx));
                }, isCheat: true);
        }

        private static IEnumerator Run(Terminal console)
        {
            s_running = true;
            void Say(string line) { VaPieces.Log?.LogMessage(line); console?.AddString(line); }
            Say($"{Tag} BEGIN {VaPieces.PluginName} {VaPieces.PluginVersion}");
            int passed = 0, failed = 0;
            void Check(string what, bool ok) { if (ok) passed++; else failed++; Say($"{Tag} {(ok ? "PASS" : "FAIL")} {what}"); }
            var me = Player.m_localPlayer;
            Vector3 home = me.transform.position; Quaternion rot = me.transform.rotation;
            var prefab = ZNetScene.instance.GetPrefab(VATowerNearChunks.PrefabName);
            GameObject tower = null;
            if (prefab == null) { Check("tower prefab exists", false); }
            else
            {
                Vector3 at = home + me.transform.forward * 80f;
                at.y = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(at) : at.y;
                tower = Object.Instantiate(prefab, at, Quaternion.identity);
                yield return Wait(() => VATowerNearChunks.ShellOffCount >= 1);
                Check($"near: wanted {VATowerNearChunks.WantedCount}, shell off {VATowerNearChunks.ShellOffCount}", VATowerNearChunks.ShellOffCount >= 1);

                me.TeleportTo(at + Vector3.right * 700f, rot, true);
                yield return Wait(() => VATowerNearChunks.ReleasedCount >= 1);
                Check($"far: released {VATowerNearChunks.ReleasedCount}", VATowerNearChunks.ReleasedCount >= 1);

                me.TeleportTo(home, rot, true);
                yield return Wait(() => VATowerNearChunks.ShellOffCount >= 2);
                Check($"back: wanted {VATowerNearChunks.WantedCount}, shell off {VATowerNearChunks.ShellOffCount}", VATowerNearChunks.ShellOffCount >= 2);

                var nview = tower != null ? tower.GetComponent<ZNetView>() : null;
                if (nview != null && nview.IsValid()) { nview.ClaimOwnership(); ZNetScene.instance.Destroy(tower); }
                Say($"{Tag} tower removed");
            }
            Say($"{Tag} END {(failed == 0 && passed > 0 ? "PASS" : "FAIL")} {passed} passed, {failed} failed");
            s_running = false;
        }

        private static IEnumerator Wait(System.Func<bool> done)
        {
            float until = Time.realtimeSinceStartup + StepSeconds;
            while (!done() && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(1f);
        }
    }
}
