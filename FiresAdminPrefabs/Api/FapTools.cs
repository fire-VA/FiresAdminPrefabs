using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Api
{
    // 1.1.340 ([lead], Fire: "our walk mode tools should be working like our in-game tools" - hammer / hoe / cultivator / Chaos Hammer,
    // also on the server God client with no game client). ONE path, vanilla's own code run as that player:
    //   UseTool: a FAT tool entry (Chaos Hammer chunk tools ...) -> FatApi.UseToolEntryAt / UseToolEntry; every other entry of the
    //            tool's table -> vanilla's public Player.PlacePiece(piece, pos, rot, doAttack: false) on that Player - it instantiates
    //            with TerrainModifier.SetTriggerOnPlaced(true), so the hoe's level / raise / pave / dig and the cultivator's entries
    //            fire their terrain ops exactly as a click does, and pieces get SetCreator / PrivateArea / WearNTear / IPlaced as in game.
    //   Repair / Remove: the bodies of vanilla's private Player.Repair / Player.RemovePiece, which act on the HOVERED piece, aimed at
    //            the given piece instead: the same checks and the same component calls, in vanilla's order.
    // Requirements / costs follow the player's own PlacementCostDisabled (as in game). Main thread. Add-only.
    public static class FapTools
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static Player PlayerFor(long playerId, out string why)
        {
            why = null;
            if (playerId == 0 && Player.m_localPlayer != null) return Player.m_localPlayer;
            foreach (var p in Player.GetAllPlayers()) if (p != null && p.GetPlayerID() == playerId) return p;
            why = playerId == 0 ? "no local player (pass the God clone's player id on the server)" : $"no loaded player {playerId}";
            return null;
        }
        private static bool Allowed(Player p, out string why)
        {
            why = null;
            bool server = ZNet.instance != null && ZNet.instance.IsServer();
            if (server && p != Player.m_localPlayer) return true;   // the server acting for its own God clone (FGC on the dedi is admin-gated)
            if (!Managers.VAVanillaPieceManager.IsAdminGateOpen()) { why = "admins only"; return false; }
            return true;
        }
        private static Piece Entry(string tool, string entryPrefab)
        {
            var t = FapHammerMenu.Table(tool);
            if (t == null || string.IsNullOrEmpty(entryPrefab)) return null;
            foreach (var go in t.m_pieces) if (go != null && go.name == entryPrefab) return go.GetComponent<Piece>();
            return null;
        }

        // FAT's tool entries (soft: no FAT = no FAT entries)
        private static MethodInfo s_isTool, s_use, s_useAt; private static bool s_fatLooked;
        private static void LookFat()
        {
            if (s_fatLooked) return; s_fatLooked = true;
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { try { t = a.GetType("VerdantsAscent.FatApi", false); } catch { } if (t != null) break; }
            s_isTool = t?.GetMethod("IsToolEntry", new[] { typeof(string) });
            s_use = t?.GetMethod("UseToolEntry");
            s_useAt = t?.GetMethod("UseToolEntryAt");
        }

        /// <summary>Use one entry of a build tool's menu at pos / rot as that player's click would. playerId 0 = the local player
        /// (client); on the server = the God clone's Player.GetPlayerID(). JSON {ok, report}.</summary>
        public static string UseTool(string tool, string entryPrefab, Vector3 pos, Quaternion rot, long playerId)
        {
            var p = PlayerFor(playerId, out string why);
            if (p == null) return No(why);
            if (!Allowed(p, out why)) return No(why);
            LookFat();
            if (s_isTool != null && (bool)s_isTool.Invoke(null, new object[] { entryPrefab }))
            {
                bool server = ZNet.instance != null && ZNet.instance.IsServer() && p != Player.m_localPlayer;
                var args = server ? new object[] { tool, entryPrefab, pos, rot, null, 0L, null } : new object[] { tool, entryPrefab, pos, rot, null, null };
                var m = server ? s_useAt : s_use;
                if (m == null) return No("this FAT has no " + (server ? "UseToolEntryAt" : "UseToolEntry"));
                bool ok = (bool)m.Invoke(null, args);
                return ok ? Ok((string)args[args.Length - 1]) : No((string)args[args.Length - 1]);
            }
            var piece = Entry(tool, entryPrefab);
            if (piece == null) return No($"no entry '{entryPrefab}' on {tool}");
            if (!p.PlacementCostDisabled && !p.HaveRequirements(piece, Player.RequirementMode.CanBuild)) return No("missing requirements (as in game)");
            try
            {
                s_near.Clear(); global::Piece.GetAllPiecesInRadius(pos, 0.5f, s_near);
                s_before.Clear(); foreach (var n in s_near) if (n) s_before.Add(n.GetInstanceID());
                bool clone = ZNet.instance != null && ZNet.instance.IsServer() && p != Player.m_localPlayer;
                Action place = () => { if (clone) PlaceAsClone(p, piece, pos, rot); else p.PlacePiece(piece, pos, rot, doAttack: false); };
                // 1.1.344 ([lead], hoe / cultivator undo): a terrain entry runs inside FAT's TerrainUse (TerrainEdit's own snapshot
                // halves), so the reply carries a snapshot FatApi.Restore undoes; no FAT 1293 = placed as before, no snapshot
                var op = piece.GetComponent<TerrainOp>();
                snapshot = null;
                if (op != null && LookTerrainUse())
                {
                    float r = Mathf.Max(op.m_settings.GetRadius(), 0.5f) + 1f;
                    var args = new object[] { pos, r, place, true, null, null };
                    if (!(bool)s_terrainUse.Invoke(null, args)) return No("terrain: " + (string)args[5]);
                    snapshot = SnapJson(args[4]);
                }
                else place();
                if (!p.PlacementCostDisabled) p.ConsumeResources(piece.m_resources, 0);
            }
            catch (Exception ex)
            {
                // vanilla PlacePiece is written for a client (it reads the local profile / platform user); on a dedi that may throw AFTER
                // the piece exists - reported, not hidden
                VaPieces.Log?.LogWarning($"[FapTools] PlacePiece {entryPrefab} for {p.GetPlayerName()} threw: {ex.GetType().Name}: {ex.Message} (1.1.340)");
                return No($"placed with an error: {ex.GetType().Name}: {ex.Message}");
            }
            VaPieces.Log?.LogDebug($"[FapTools] {p.GetPlayerName()} used {tool} / {entryPrefab} at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) (1.1.340)");
            // the placed piece's ZDOID for the Studio undo row ([lead] 10-30); "none" for terrain-only entries (hoe / cultivator ops)
            string zdoid = "none";
            s_near.Clear(); global::Piece.GetAllPiecesInRadius(pos, 0.5f, s_near);
            foreach (var n in s_near)
            {
                if (!n || s_before.Contains(n.GetInstanceID()) || !n.name.StartsWith(entryPrefab, StringComparison.Ordinal)) continue;
                var znv = n.GetComponent<ZNetView>();
                if (znv != null && znv.IsValid()) { var id = znv.GetZDO().m_uid; zdoid = id.UserID.ToString(Inv) + ":" + id.ID.ToString(Inv); break; }
            }
            // field left out when nothing was placed: FGC reads "absent" as "no inverse"
            return "{\"ok\":true,\"report\":" + Q($"{entryPrefab} used") + (zdoid == "none" ? "" : ",\"zdoid\":" + Q(zdoid)) + (snapshot != null ? ",\"snapshot\":" + snapshot : "") + "}";
        }

        // Vanilla Player.PlacePiece's body, line for line, for the server God clone: its client-only reads (the local PlatformUser in
        // SetCreator, Game's local PlayerProfile for PrivateArea.Setup and the build-height stats) are swapped for the clone's identity.
        // Each guarded read logs one Info line the first time (Fire 10-30 via [lead]).
        private static readonly List<global::Piece> s_near = new List<global::Piece>();
        private static readonly HashSet<int> s_before = new HashSet<int>();
        private static readonly List<IPlaced> s_placed = new List<IPlaced>();
        private static bool s_saidCreator, s_saidWard, s_saidStats;
        private static void PlaceAsClone(Player p, Piece piece, Vector3 pos, Quaternion rot)
        {
            TerrainModifier.SetTriggerOnPlaced(trigger: true);
            GameObject go = UnityEngine.Object.Instantiate(piece.gameObject, pos, rot);
            TerrainModifier.SetTriggerOnPlaced(trigger: false);
            var station = go.GetComponentInChildren<CraftingStation>();
            if (station) p.AddKnownStation(station);
            var placed = go.GetComponent<Piece>();
            if (placed)
            {
                placed.SetCreator(p.GetPlayerID(), default);
                if (!s_saidCreator) { s_saidCreator = true; VaPieces.Log?.LogInfo($"[FapTools] guarded read: SetCreator uses the clone's player id {p.GetPlayerID()} with no platform user (dedi has no LocalUser) (1.1.340)"); }
            }
            var ward = go.GetComponent<PrivateArea>();
            if (ward != null)
            {
                ward.Setup(p.GetPlayerName());
                if (!s_saidWard) { s_saidWard = true; VaPieces.Log?.LogInfo($"[FapTools] guarded read: ward owner name = clone's name '{p.GetPlayerName()}', not Game's local profile (1.1.340)"); }
            }
            go.GetComponent<WearNTear>()?.OnPlaced();
            go.GetComponent<ItemDrop>()?.MakePiece(sendRPC: true);
            s_placed.Clear();
            go.GetComponents(s_placed);
            foreach (var ip in s_placed) ip.OnPlaced();
            piece.m_placeEffect.Create(pos, rot, go.transform, 1f, -1, p.GetZDOID());
            if (!s_saidStats) { s_saidStats = true; VaPieces.Log?.LogInfo("[FapTools] guarded read: build-height profile stats skipped for the clone (they live in a client's PlayerProfile) (1.1.340)"); }
            ZLog.Log(go.name + " placed at world height " + pos.y);
        }

        // FAT 0.2.1293 FatApi.TerrainUse(Vector3, float, Action, bool, out FatSnapshot, out string) - soft, by reflection
        private static MethodInfo s_terrainUse; private static bool s_terrainLooked;
        private static string snapshot;
        private static bool LookTerrainUse()
        {
            if (!s_terrainLooked)
            {
                s_terrainLooked = true;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null; try { t = a.GetType("VerdantsAscent.FatApi", false); } catch { }
                    if (t != null) { s_terrainUse = t.GetMethod("TerrainUse"); break; }
                }
                VaPieces.Log?.LogInfo($"[FapTools] terrain undo: {(s_terrainUse != null ? "FAT TerrainUse found, hoe / cultivator uses carry a snapshot" : "no FAT TerrainUse (FAT < 0.2.1293), terrain uses have no undo")} (1.1.344)");
            }
            return s_terrainUse != null;
        }
        // {id, kind, version, hash, bytes} - hash + bytes base64, as FGC's SnapshotStore keeps a brush edit's
        private static string SnapJson(object s)
        {
            if (s == null) return null;
            var t = s.GetType();
            string F(string n) => t.GetField(n)?.GetValue(s) as string ?? "";
            byte[] B(string n) => t.GetField(n)?.GetValue(s) as byte[] ?? new byte[0];
            object v = t.GetField("Version")?.GetValue(s);
            return "{\"id\":" + Q(F("Id")) + ",\"kind\":" + Q(F("Kind")) + ",\"version\":" + Convert.ToString(v ?? 0, Inv) + ",\"hash\":" + Q(Convert.ToBase64String(B("Hash"))) + ",\"bytes\":" + Q(Convert.ToBase64String(B("Bytes"))) + "}";
        }

        private static ZNetView Piece(string zdoid, out string why)
        {
            why = null;
            var s = (zdoid ?? "").Split(':');
            if (s.Length != 2 || !long.TryParse(s[0], NumberStyles.Integer, Inv, out long user) || !uint.TryParse(s[1], NumberStyles.Integer, Inv, out uint id)) { why = "zdoid must be \"userid:id\""; return null; }
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(new ZDOID(user, id)) : null;
            var nv = go != null ? go.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid()) { why = "that piece is not loaded here"; return null; }
            return nv;
        }

        /// <summary>The hammer's repair on that piece (vanilla Player.Repair's body: WearNTear.Repair). JSON {ok, report}.</summary>
        public static string Repair(string zdoid, long playerId)
        {
            var p = PlayerFor(playerId, out string why);
            if (p == null) return No(why);
            if (!Allowed(p, out why)) return No(why);
            var nv = Piece(zdoid, out why);
            if (nv == null) return No(why);
            var piece = nv.GetComponent<Piece>();
            var wnt = nv.GetComponent<WearNTear>();
            if (piece == null || wnt == null) return No("not a repairable piece");
            if (!PrivateArea.CheckAccess(piece.transform.position, 0f, flash: false)) return No("$msg_privatezone");
            return wnt.Repair() ? Ok($"{piece.m_name} repaired") : Ok($"{piece.m_name} does not need repair");
        }

        private static readonly MethodInfo s_canRemove = AccessTools.Method(typeof(Player), "CheckCanRemovePiece");
        /// <summary>The hammer's remove on that piece (vanilla Player.RemovePiece's body, aimed at it: the same checks, IRemoved,
        /// WearNTear.Remove / Character damage / drop + destroy). JSON {ok, report}.</summary>
        public static string Remove(string zdoid, long playerId)
        {
            var p = PlayerFor(playerId, out string why);
            if (p == null) return No(why);
            if (!Allowed(p, out why)) return No(why);
            var nv = Piece(zdoid, out why);
            if (nv == null) return No(why);
            var piece = nv.GetComponent<Piece>();
            if (piece == null) return No("not a piece");
            if (!piece.m_canBeRemoved) return No("this piece cannot be removed");
            if (Location.IsInsideNoBuildLocation(piece.transform.position)) return No("$msg_nobuildzone");
            if (!PrivateArea.CheckAccess(piece.transform.position, 0f, flash: false)) return No("$msg_privatezone");
            if (s_canRemove != null && !(bool)s_canRemove.Invoke(p, new object[] { piece })) return No("$msg_missingstation");
            if (!piece.CanBeRemoved()) return No("$msg_cantremovenow");
            piece.GetComponent<IRemoved>()?.OnRemoved();
            var wnt = piece.GetComponent<WearNTear>();
            if (wnt != null) wnt.Remove();
            else
            {
                var ch = piece.GetComponent<Character>();
                if (ch != null) ch.Damage(new HitData(1E+10f));
                else { nv.ClaimOwnership(); piece.DropResources(); ZNetScene.instance.Destroy(piece.gameObject); }
            }
            return Ok($"{piece.m_name} removed");
        }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static string Ok(string r) => "{\"ok\":true,\"report\":" + Q(r) + "}";
        private static string No(string r) => "{\"ok\":false,\"report\":" + Q(r) + "}";
    }
}
