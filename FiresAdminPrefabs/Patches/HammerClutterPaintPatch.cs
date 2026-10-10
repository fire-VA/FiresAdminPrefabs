using System;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Patches
{
    // Diverts Player.PlacePiece into the matching FAP tool when the player
    // has one of our stand-in pieces selected: clutter paint, FX/spawner/
    // container clears, item stand, monster spawner. Returns false to skip
    // vanilla so the stand-in is never Instantiated and no ZDO is created.
    // Falls through to vanilla for anything that isn't one of our stand-ins.
    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class Player_PlacePiece_VAClutterPaint_Patch
    {
        // Which FAP tool owns a placement. None means "not ours, let vanilla have it".
        internal enum PlaceBranch
        {
            None,
            FxClear,
            SpawnerClear,
            ContainerClear,
            ClutterClear,
            ItemStand,
            Monster,
            ClutterStandin,
            ClutterPrefab,
        }

        // THE branch decision, in the order the prefix applies it. Pure: no side
        // effects, safe to call outside a placement. Both the live prefix and
        // fap_selftest_place go through this, so a test can never pass while the
        // real placement path disagrees with it.
        internal static PlaceBranch Classify(GameObject go, out string detail)
        {
            detail = null;
            if (go == null) return PlaceBranch.None;
            string n = go.name;

            if (VAFapEwpClearTool.IsStandinName(n)) return PlaceBranch.FxClear;
            if (VAFapMonsterClearTool.IsStandinName(n)) return PlaceBranch.SpawnerClear;
            if (VAFapContainerClearTool.IsStandinName(n)) return PlaceBranch.ContainerClear;
            if (VAClutterClearTool.IsStandinName(n)) return PlaceBranch.ClutterClear;

            if (VAItemStandStandinFactory.TryResolve(go, out var itemName, out var orientation))
            {
                detail = itemName + " (" + (orientation == VAItemStandStandinFactory.Orientation.Vertical ? "V" : "H") + ")";
                return PlaceBranch.ItemStand;
            }

            if (VAMonsterStandinFactory.IsStandinName(n))
            {
                detail = VAMonsterStandinFactory.ResolveCreatureNameFromStandin(go);
                return PlaceBranch.Monster;
            }

            string realName = VAClutterStandinFactory.ResolveRealNameFromStandin(go);
            if (realName != null)
            {
                detail = realName;
                return PlaceBranch.ClutterStandin;
            }

            if (VAVanillaPieceManager.IsClutterPrefab_Public(go)) return PlaceBranch.ClutterPrefab;
            return PlaceBranch.None;
        }

        [HarmonyPrefix]
        public static bool Prefix(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            if (__instance == null || piece == null) return true;
            // NEVER intercept an Infinity Hammer / World Edit placement: IH does
            // its real placement work INSIDE vanilla PlacePiece (via transpiler),
            // so returning false here swallows the whole op and the frozen
            // selection vanishes. Defer to vanilla/IH whenever IH owns placement.
            var ghostGo = piece.gameObject;
            var branch = Classify(ghostGo, out string detail);
            // 1.1.203 (Arbby: item stands never place with Infinity Hammer): an item-stand stand-in is FAP's own inactive piece,
            // never an IH selection; deferring it to vanilla instantiated it INACTIVE (invisible). FAP still places that one; every
            // other placement is IH's.
            // 1.1.206 (Fire 10-09: FAP monsters didn't place with IH's hammer; "make the mods work together properly"): every FAP
            // STAND-IN (the clear tools, item stands, monsters / spawners, clutter stand-ins) is FAP's own inactive, non-networked
            // piece - IH can't copy or place one, deferring it only spawned an invisible inactive clone. FAP places all of them
            // under either hammer. Real objects (a real clutter prefab, any normal piece, every IH copy / selection) stay IH's.
            if (VAInfinityHammerCompat.IsInfinityHammerActive() && (branch == PlaceBranch.None || branch == PlaceBranch.ClutterPrefab))
                return Trace("infinity-hammer-active", ghostGo, false);

            switch (branch)
            {
                case PlaceBranch.FxClear:
                {
                    float fxRadius = ConfigManager.Instance.configFxClearRadius.Value;
                    float fxDuration = ConfigManager.Instance.configFxClearDurationSec.Value;
                    int cleared = VAFapEwpClearTool.ClearArea(pos, fxRadius, fxDuration);
                    __instance.Message(MessageHud.MessageType.TopLeft,
                        $"FX clear: removed {cleared} (watching {fxDuration:F0}s for respawns, r={fxRadius:F1})");
                    return Trace("fx-clear", ghostGo, true);
                }

                case PlaceBranch.SpawnerClear:
                {
                    float spRadius = ConfigManager.Instance.configFxClearRadius.Value;
                    float spDuration = ConfigManager.Instance.configFxClearDurationSec.Value;
                    int cleared = VAFapMonsterClearTool.ClearArea(pos, spRadius, spDuration);
                    __instance.Message(MessageHud.MessageType.TopLeft,
                        $"Spawner clear: destroyed {cleared} creature(s) (watching {spDuration:F0}s for respawns, r={spRadius:F1})");
                    return Trace("spawner-clear", ghostGo, true);
                }

                case PlaceBranch.ContainerClear:
                {
                    float ccRadius = ConfigManager.Instance.configFxClearRadius.Value;
                    float ccDuration = ConfigManager.Instance.configFxClearDurationSec.Value;
                    int cleared = VAFapContainerClearTool.ClearArea(pos, ccRadius, ccDuration);
                    __instance.Message(MessageHud.MessageType.TopLeft,
                        $"Container clear: destroyed {cleared} chest(s) (watching {ccDuration:F0}s for respawns, r={ccRadius:F1})");
                    return Trace("container-clear", ghostGo, true);
                }

                case PlaceBranch.ClutterClear:
                {
                    float cRadius = VAClutterClearTool.Radius;
                    int cleared = VAClutterClearTool.ClearArea(pos, cRadius, VAClutterClearTool.HeightBounds);
                    __instance.Message(MessageHud.MessageType.TopLeft,
                        $"Clutter clear: removed {cleared} sample(s) (r={cRadius:F1})");
                    return Trace("clutter-clear", ghostGo, true);
                }

                case PlaceBranch.ItemStand:
                {
                    VAItemStandStandinFactory.TryResolve(ghostGo, out var itemName, out var orientation);
                    int spawned = VAItemStandSpawner.SpawnAt(itemName, orientation, pos, rot, __instance);
                    string standMsg = spawned > 0 ? "Placed stand" : "Failed to place stand";
                    __instance.Message(MessageHud.MessageType.TopLeft,
                        $"{standMsg}: {itemName} ({(orientation == VAItemStandStandinFactory.Orientation.Vertical ? "V" : "H")})");
                    return Trace($"item-stand item='{itemName}' spawned={spawned}", ghostGo, true);
                }

                case PlaceBranch.Monster:
                {
                    string creatureName = detail;
                    int spawned = VAMonsterSpawner.SpawnAt(creatureName, pos, rot, __instance);
                    string monsterMsg = spawned > 0
                        ? $"Spawned {spawned}x {creatureName}"
                        : $"Failed to spawn {creatureName}";
                    __instance.Message(MessageHud.MessageType.TopLeft, monsterMsg);
                    return Trace($"monster creature='{creatureName}' spawned={spawned}", ghostGo, true);
                }

                case PlaceBranch.ClutterStandin:
                {
                    var resolved = VAClutterRegistry.ResolvePrefab(detail);
                    if (resolved == null)
                    {
                        __instance.Message(MessageHud.MessageType.TopLeft,
                            $"Clutter '{detail}' not found in ClutterSystem (mod load order?)");
                        return Trace($"clutter-standin UNRESOLVED real='{detail}'", ghostGo, true);
                    }
                    return Trace(PaintClutter(__instance, resolved, pos), ghostGo, true);
                }

                case PlaceBranch.ClutterPrefab:
                    return Trace(PaintClutter(__instance, ghostGo, pos), ghostGo, true);

                default:
                    return Trace("not-a-FAP-standin", ghostGo, false);
            }
        }

        private static string PaintClutter(Player player, GameObject realPrefab, Vector3 pos)
        {
            float radius = ConfigManager.Instance.configClutterPaintRadius.Value;
            float density = ConfigManager.Instance.configClutterPaintDensity.Value;

            int dispatched = VAClutterPaintTool.Paint(realPrefab, pos, radius, density);

            string status = dispatched > 0 ? "Painted" : "Failed to paint";
            player.Message(MessageHud.MessageType.TopLeft,
                $"{status} {realPrefab.name} (r={radius:F1}, n={dispatched})");
            return $"clutter-paint real='{realPrefab.name}' n={dispatched}";
        }

        // One line per hammer click naming the branch that claimed the piece, so
        // "hammer swings but nothing placed" becomes a readable fact instead of a
        // guess. Every return point is labelled. A FAP stand-in that reports
        // FELL THROUGH is the defect: vanilla then Instantiates the stand-in, and
        // because every stand-in is built inactive under an inactive root the copy
        // is inactive too - invisible - while vanilla still plays the swing.
        // Deduped per piece name so holding the paint button cannot flood the log.
        private const float TraceDedupeSeconds = 2f;
        private static string _lastTraced;
        private static float _lastTraceTime;

        // Returns Harmony's prefix flag, the inverse of skipVanilla, on every path.
        private static bool Trace(string branch, GameObject go, bool skipVanilla)
        {
            bool runVanilla = !skipVanilla;
            try
            {
                string name = go != null ? go.name : "<null>";
                bool isFapStandin = Classify(go, out _) != PlaceBranch.None;

                // Vanilla building is the overwhelming majority of clicks and is not
                // what anyone is debugging, so it stays behind verbose; a FAP stand-in
                // always reports, because that is the case under investigation.
                if (!isFapStandin && !VAFapLoadSummary.VerboseEnabled) return runVanilla;

                if (string.Equals(name, _lastTraced, StringComparison.Ordinal)
                    && Time.time - _lastTraceTime < TraceDedupeSeconds)
                    return runVanilla;
                _lastTraced = name;
                _lastTraceTime = Time.time;

                string verdict = skipVanilla
                    ? "FAP handled, vanilla skipped"
                    : (isFapStandin
                        ? "FELL THROUGH TO VANILLA <-- BUG: a FAP stand-in reached vanilla, which Instantiates it INACTIVE (invisible) and still swings"
                        : "fell through to vanilla (normal for a real build piece)");

                // 1.1.203 (Arbby: "3 log lines per placed piece"): a per-placement trace is Debug (Fire's rule: Message-level
                // defaults); only the real fault - a stand-in reaching vanilla - stays a Warning.
                if (isFapStandin && !skipVanilla) VaPieces.Log?.LogWarning($"PlacePiece '{name}' -> branch={branch}; {verdict}");
                else VaPieces.Log?.LogDebug($"PlacePiece '{name}' -> branch={branch}; {verdict}");
            }
            catch { }
            return runVanilla;
        }
    }
}
