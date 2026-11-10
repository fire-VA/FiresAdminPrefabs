using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Api
{
    // 1.1.339 ([lead] part B: the Studio as a god client with NO game client - the dedi has no Hud, so no BuildUi):
    // a HEADLESS set of vanilla's own 4 build-menu list objects per player (ByUsagePieceList, ByMaterialPieceList, RecentPieceList,
    // FavoritePieceList - plain data classes, no UI), used where Hud.instance is null. Two vanilla gaps on a dedi are closed with the
    // same bytes vanilla writes: Player.Load reads the character's build-menu block and hands it to Hud.instance?.m_buildUi (dropped
    // without a Hud) and Player.Save writes new byte[0] without a Hud - so a server-saved God profile LOST its favourites / recent.
    // Here Load's block goes into the player's headless set and Save writes that set back (BuildUi.SaveToBinary's own format:
    // Recent then Favorites), so the profile stays compatible both ways. Clients (Hud present) are untouched.
    internal static class FapBuildLists
    {
        internal sealed class Set
        {
            internal ByUsagePieceList Usage = new ByUsagePieceList("$hud_byusage");
            internal ByMaterialPieceList Material;
            internal RecentPieceList Recent = new RecentPieceList("$hud_recent", 20);
            internal FavoritePieceList Favorites = new FavoritePieceList("$hud_favorites");
            internal List<IPieceList> All() => new List<IPieceList> { Usage, Material ?? (Material = new ByMaterialPieceList("$hud_bymaterial", MajorMaterials())), Recent, Favorites };
        }
        private static readonly Dictionary<long, Set> s_sets = new Dictionary<long, Set>();
        internal static Set For(long playerId) { if (!s_sets.TryGetValue(playerId, out var s)) s_sets[playerId] = s = new Set(); return s; }

        // ── the major materials: BuildUi.m_majorMaterials lives on the HUD prefab (none on a dedi): a client's snapshot, kept on disk ──
        private static string SnapshotPath => Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminPrefabs", "build_major_materials.txt");
        private static string s_snapshot;
        internal static void SetSnapshot(string csv)
        {
            s_snapshot = csv ?? "";
            try { Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)); File.WriteAllText(SnapshotPath, s_snapshot); } catch (Exception ex) { VaPieces.Log?.LogWarning($"[FapBuildLists] snapshot not saved: {ex.Message}"); }
            foreach (var s in s_sets.Values) s.Material = null;   // rebuilt with the new list on next use
            VaPieces.Log?.LogInfo($"[FapBuildLists] major materials snapshot set: {s_snapshot.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length} item(s) (1.1.339)");
        }
        internal static bool HaveSnapshot => !string.IsNullOrEmpty(Snapshot());
        private static string Snapshot()
        {
            if (s_snapshot == null) { try { s_snapshot = File.Exists(SnapshotPath) ? File.ReadAllText(SnapshotPath) : ""; } catch { s_snapshot = ""; } }
            return s_snapshot;
        }
        private static List<ItemDrop> MajorMaterials()
        {
            var list = new List<ItemDrop>();
            if (ObjectDB.instance == null) return list;
            foreach (var n in Snapshot().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var go = ObjectDB.instance.GetItemPrefab(n.Trim());
                var d = go != null ? go.GetComponent<ItemDrop>() : null;
                if (d != null) list.Add(d);
            }
            return list;
        }

        // ── the player's available pieces for a tool, as vanilla's Player.UpdateAvailablePiecesList computes them ──
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> s_known = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly PropertyInfo s_shadow = AccessTools.TypeByName("FiresCore.Npc.Studio.GodProfile")?.GetProperty("Shadow", BindingFlags.Public | BindingFlags.Static);
        internal static Player FindPlayer(long playerId)
        {
            foreach (var p in Player.GetAllPlayers()) if (p != null && p.GetPlayerID() == playerId) return p;
            // 1.1.349 ([visual]: FGC passes the dedi God profile's Player - Core's FiresCore.Npc.Studio.GodProfile.Shadow - which is not
            // in Player.GetAllPlayers): that Player too, when its id matches
            var shadow = s_shadow?.GetValue(null, null) as Player;
            return shadow != null && shadow.GetPlayerID() == playerId ? shadow : null;
        }
        // 1.1.344 ([lead]: the God clone's /v1/hammer had 0 pieces; Fire: "we are a god" = no placement cost + everything known):
        // vanilla's own call (Player.UpdateAvailablePiecesList) passes noPlacementCost || the AllPiecesUnlocked key - now matched - and
        // the SERVER's God clone (a Player on the dedi that is not a local player) gets vanilla's no-placement-cost flag set once, so
        // every enabled piece is available, as in debug mode. The piece count logs once per tool (Info + the reason when it is 0).
        private static readonly AccessTools.FieldRef<Player, bool> s_noCost = AccessTools.FieldRefAccess<Player, bool>("m_noPlacementCost");
        private static readonly AccessTools.FieldRef<PieceTable, HashSet<Piece>> s_avail = AccessTools.FieldRefAccess<PieceTable, HashSet<Piece>>("m_availablePieces");
        private static readonly HashSet<string> s_said = new HashSet<string>(StringComparer.Ordinal);
        internal static void FillAvailable(PieceTable t, Player p)
        {
            bool clone = ZNet.instance != null && ZNet.instance.IsServer() && p != Player.m_localPlayer;
            if (clone && !s_noCost(p)) { s_noCost(p) = true; VaPieces.Log?.LogInfo($"[FapBuildLists] God clone {p.GetPlayerName()} ({p.GetPlayerID()}): no placement cost ON (every piece known, as vanilla's debug flag) (1.1.344)"); }
            bool unlocked = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.AllPiecesUnlocked);
            var known = s_known(p);
            t.UpdateAvailable(known, p, false, p.PlacementCostDisabled || unlocked);
            int n = s_avail(t)?.Count ?? 0;
            string key = t.name + "|" + p.GetPlayerID() + "|" + (n > 0);
            if (s_said.Add(key))
            {
                if (n == 0) VaPieces.Log?.LogInfo($"[FapBuildLists] {t.name} for {p.GetPlayerName()}: 0 pieces available - known recipes {known?.Count ?? 0}, no placement cost {p.PlacementCostDisabled}, AllPiecesUnlocked {unlocked}, table entries {t.m_pieces.Count} (1.1.344)");
                else VaPieces.Log?.LogDebug($"[FapBuildLists] {t.name} for {p.GetPlayerName()}: {n} pieces available (1.1.344)");
            }
        }

        // ── Player.Load: the character's build-menu block also reaches the headless set when there is no Hud ──
        internal static void OnLoad(Player p, byte[] data)
        {
            if ((Hud.instance != null && !(ZNet.instance != null && ZNet.instance.IsDedicated())) || p == null || data == null || data.Length == 0) return;
            var s = For(p.GetPlayerID());
            try
            {
                var zpg = new ZPackage(data); var enc = new System.Text.UTF8Encoding();
                s.Recent.Deserialize(zpg, enc);
                AccessTools.Method(typeof(FavoritePieceList), "Deserialize").Invoke(s.Favorites, new object[] { zpg, enc });
                VaPieces.Log?.LogInfo($"[FapBuildLists] {p.GetPlayerName()} ({p.GetPlayerID()}): build-menu block loaded without a Hud ({data.Length} bytes) (1.1.339)");
            }
            catch (Exception ex) { VaPieces.Log?.LogWarning($"[FapBuildLists] build-menu block not read for {p.GetPlayerName()}: {(ex.InnerException ?? ex).Message}"); }
        }
        // ── Player.Save: with no Hud, write the headless set in BuildUi.SaveToBinary's own format instead of an empty block ──
        internal static byte[] SaveFromUi(BuildUi ui, Player p)
            => ZNet.instance != null && ZNet.instance.IsDedicated() ? SaveBytes(p) : ui.SaveToBinary();

        internal static byte[] SaveBytes(Player p)
        {
            if (p == null || !s_sets.TryGetValue(p.GetPlayerID(), out var s)) return new byte[0];
            try
            {
                var pkg = new ZPackage(); var enc = new System.Text.UTF8Encoding();
                s.Recent.Serialize(pkg, enc);
                AccessTools.Method(typeof(FavoritePieceList), "Serialize").Invoke(s.Favorites, new object[] { pkg, enc });
                var arr = pkg.GetArray();
                VaPieces.Log?.LogDebug($"[FapBuildLists] {p.GetPlayerName()} ({p.GetPlayerID()}): build-menu block saved without a Hud ({arr.Length} bytes) (1.1.339)");
                return arr;
            }
            catch (Exception ex) { VaPieces.Log?.LogWarning($"[FapBuildLists] build-menu block not written for {p.GetPlayerName()}: {(ex.InnerException ?? ex).Message}"); return new byte[0]; }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Load))]
        private static class Load_Patch
        {
            // before `Hud.instance?.m_buildUi.LoadFromBinary(data)`: OnLoad(this, data)
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var list = code.ToList();
                var load = AccessTools.Method(typeof(BuildUi), nameof(BuildUi.LoadFromBinary));
                // 1.1.344 (slot 653 logs, client + dedi: "AccessTools.Field: Could not find field for type Hud and name instance" then
                // LoadsField(null) threw, the patch was skipped): Hud.instance is a PROPERTY - match its getter call
                var hud = AccessTools.PropertyGetter(typeof(Hud), nameof(Hud.instance));
                int call = list.FindIndex(c => c.Calls(load));
                if (call < 0 || hud == null) { VaPieces.Log?.LogWarning("[FapBuildLists] Player.Load: LoadFromBinary call / Hud.instance getter not found - dedi favourites stay vanilla (1.1.344)"); return list; }
                int dataLd = -1; for (int i = call - 1; i >= 0; i--) if (list[i].IsLdloc()) { dataLd = i; break; }
                int hudAt = -1; for (int i = call - 1; i >= 0; i--) if (list[i].Calls(hud)) { hudAt = i; break; }
                if (dataLd < 0 || hudAt < 0) { VaPieces.Log?.LogWarning("[FapBuildLists] Player.Load: shape not as expected - dedi favourites stay vanilla (1.1.344)"); return list; }
                var inject = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(list[dataLd].opcode, list[dataLd].operand), CodeInstruction.Call(typeof(FapBuildLists), nameof(OnLoad)) };
                inject[0].labels.AddRange(list[hudAt].labels); list[hudAt].labels.Clear();
                list.InsertRange(hudAt, inject);
                VaPieces.Log?.LogInfo("[FapBuildLists] Player.Load patched: the character's build-menu block reaches the headless lists when there is no Hud (1.1.344)");
                return list;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        private static class Save_Patch
        {
            // the no-Hud branch's `new byte[0]` becomes SaveBytes(this)
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var list = code.ToList();
                var save = AccessTools.Method(typeof(BuildUi), nameof(BuildUi.SaveToBinary));
                int call = list.FindIndex(c => c.Calls(save));
                int arr = -1;
                if (call >= 0) for (int i = call + 1; i < list.Count; i++) if (list[i].opcode == OpCodes.Newarr && (Type)list[i].operand == typeof(byte)) { arr = i; break; }
                if (arr < 0) { VaPieces.Log?.LogWarning("[FapBuildLists] Player.Save: empty build-menu block not found - dedi favourites stay vanilla"); return list; }
                list.InsertRange(arr + 1, new[] { new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ldarg_0), CodeInstruction.Call(typeof(FapBuildLists), nameof(SaveBytes)) });
                // 1.1.348 ([lead]: the God profile's favourites must persist; the dedi HAS a Hud object, so vanilla took the Hud branch and
                // wrote the Hud's empty lists): the Hud branch's `ui.SaveToBinary()` becomes SaveFromUi(ui, this) - the Hud's own bytes on a
                // client, the headless set on a dedicated server
                list[call] = new CodeInstruction(OpCodes.Ldarg_0) { labels = list[call].labels, blocks = list[call].blocks };
                list.Insert(call + 1, CodeInstruction.Call(typeof(FapBuildLists), nameof(SaveFromUi)));
                VaPieces.Log?.LogInfo("[FapBuildLists] Player.Save patched: a dedicated server writes the headless build-menu set (favourites / recent) (1.1.348)");
                return list;
            }
        }
    }
}
