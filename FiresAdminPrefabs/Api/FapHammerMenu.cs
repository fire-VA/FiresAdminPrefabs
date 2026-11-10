using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Api
{
    // 1.1.331 (Fire 10-30: Studio walk mode gets "the actual in game hammer menue ... (integrate it with fap since it handles hammer
    // menues)"; FGC reads this by reflection, [visual] serves it to the web, [classes-ui] draws it). A read-only snapshot of a build
    // tool's piece table exactly as the in-game build menu shows it AFTER FAP organised it: the tabs in the menu's order with their
    // labels, each tab's pieces in the menu's order, and per piece its name, cost, station and icon. Main thread only. Add-only.
    public sealed class FapHammerPiece
    {
        public string Prefab;            // ZNetScene prefab name (what to place)
        public string Name;              // localized display name
        public int Category;             // Piece.PieceCategory value (FAP's own tabs are values past the vanilla ones)
        public string Station;           // required crafting station prefab name, "" = none
        public string[] ResourceItems;   // cost: item prefab names ...
        public int[] ResourceAmounts;    // ... and amounts (first-level), same order
        public bool Available;           // the local player could pick it right now (known recipe / no-cost / admin rules)
        public bool HasIcon;
    }

    public sealed class FapHammerTab
    {
        public int Category;
        public string Label;             // the label the menu shows (localized)
        public FapHammerPiece[] Pieces;
    }

    public static class FapHammerMenu
    {
        /// <summary>Changes whenever the tool's table changes (pieces added / moved / hidden, tabs renamed); cheap to poll.</summary>
        public static int Revision(string tool = "Hammer")
        {
            var t = Table(tool);
            if (t == null) return 0;
            unchecked
            {
                int h = 17;
                h = h * 31 + t.m_pieces.Count;
                foreach (var go in t.m_pieces) if (go != null) { var p = go.GetComponent<Piece>(); h = h * 31 + go.name.GetHashCode() + (p != null ? (int)p.m_category * 7 : 0); }
                for (int i = 0; i < t.m_categoryLabels.Count; i++) h = h * 31 + (t.m_categoryLabels[i] ?? "").GetHashCode();
                return h;
            }
        }

        /// <summary>The tool's tabs and pieces in the build menu's order. tool = the item prefab holding the table ("Hammer", "Hoe",
        /// "Cultivator", ...). Empty when there is no ObjectDB / no such tool.</summary>
        public static FapHammerTab[] Tabs(string tool = "Hammer")
        {
            var t = Table(tool);
            if (t == null) return new FapHammerTab[0];
            var loc = Localization.instance;
            var tabs = new List<FapHammerTab>();
            for (int i = 0; i < t.m_categories.Count; i++)
            {
                var cat = t.m_categories[i];
                string label = i < t.m_categoryLabels.Count ? t.m_categoryLabels[i] : cat.ToString();
                if (loc != null && !string.IsNullOrEmpty(label)) label = loc.Localize(label);
                var pieces = new List<FapHammerPiece>();
                foreach (var go in t.m_pieces)
                {
                    var p = go != null ? go.GetComponent<Piece>() : null;
                    if (p == null || p.m_category != cat) continue;
                    var items = new List<string>(); var amounts = new List<int>();
                    if (p.m_resources != null)
                        foreach (var r in p.m_resources)
                            if (r?.m_resItem != null) { items.Add(r.m_resItem.gameObject.name); amounts.Add(r.m_amount); }
                    pieces.Add(new FapHammerPiece
                    {
                        Prefab = go.name,
                        Name = loc != null ? loc.Localize(p.m_name) : p.m_name,
                        Category = (int)cat,
                        Station = p.m_craftingStation != null ? p.m_craftingStation.gameObject.name : "",
                        ResourceItems = items.ToArray(), ResourceAmounts = amounts.ToArray(),
                        Available = t.m_availablePieces.Contains(p),
                        HasIcon = p.m_icon != null,
                    });
                }
                if (pieces.Count > 0) tabs.Add(new FapHammerTab { Category = (int)cat, Label = label, Pieces = pieces.ToArray() });
            }
            // 1.1.346 (round 8b: /v1/hammer?tool=Hoe / Cultivator came back with no pieces): every table piece whose category is not one of
            // the table's tabs (a single-page tool like the hoe or cultivator) goes into ONE tab, in the table's order - the page the game
            // shows. The Info line below (once per tool) carries the table's categories + each piece's category, which proves the cause.
            var listed = new HashSet<Piece.PieceCategory>(t.m_categories);
            var rest = new List<FapHammerPiece>(); var restCats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in t.m_pieces)
            {
                var p = go != null ? go.GetComponent<Piece>() : null;
                if (p == null || listed.Contains(p.m_category)) continue;
                restCats.Add(p.m_category.ToString());
                rest.Add(new FapHammerPiece
                {
                    Prefab = go.name, Name = loc != null ? loc.Localize(p.m_name) : p.m_name, Category = (int)p.m_category,
                    Station = p.m_craftingStation != null ? p.m_craftingStation.gameObject.name : "",
                    ResourceItems = (p.m_resources ?? new Piece.Requirement[0]).Where(r => r?.m_resItem != null).Select(r => r.m_resItem.gameObject.name).ToArray(),
                    ResourceAmounts = (p.m_resources ?? new Piece.Requirement[0]).Where(r => r?.m_resItem != null).Select(r => r.m_amount).ToArray(),
                    Available = t.m_availablePieces.Contains(p), HasIcon = p.m_icon != null,
                });
            }
            if (rest.Count > 0) tabs.Add(new FapHammerTab { Category = rest[0].Category, Label = tabs.Count == 0 ? (loc != null ? loc.Localize(tool) : tool) : "Other", Pieces = rest.ToArray() });
            if (s_loggedTools.Add(tool ?? ""))
            {
                int n = 0; foreach (var x in tabs) n += x.Pieces.Length;
                VaPieces.Log?.LogInfo($"[FapHammerMenu] '{tool}': table categories {t.m_categories.Count} [{string.Join(",", t.m_categories)}], table pieces {t.m_pieces.Count}, "
                    + $"in those tabs {n - rest.Count}, outside them {rest.Count} (categories {string.Join(",", restCats)}) -> {tabs.Count} tab(s), {n} piece(s) (1.1.346)");
            }
            return tabs.ToArray();
        }
        private static readonly HashSet<string> s_loggedTools = new HashSet<string>(StringComparer.Ordinal);

        // ── 1.1.338 ([classes-ui] / [lead], Fire: the Studio's walk-mode build menu must match the in-game 1.0 menu) ──────────────────
        // The in-game menu's two levels are VANILLA's: BuildUi's 4 IPieceLists (By usage / By material / Recent / Favorites) and each
        // one's tags (By usage = Piece.m_usage tags + Core's BuildMenuTabs rows, i.e. FAP's tabs; By material = the major materials).
        // ListsJson READS those list objects (UpdateAvailableTags + GetTagDisplayName / GetTagIdByIndex + GetAvailablePiecesWithTag,
        // tag -1 = Show all), so the Studio shows exactly what the game builds - no second categoriser. Client side (Hud's own lists);
        // the dedi half (a headless list set per player) is 1.1.339. Main thread.
        private static readonly HarmonyLib.AccessTools.FieldRef<BuildUi, List<IPieceList>> s_lists = HarmonyLib.AccessTools.FieldRefAccess<BuildUi, List<IPieceList>>("m_pieceLists");
        private static readonly HarmonyLib.AccessTools.FieldRef<BuildUi, FavoritePieceList> s_favs = HarmonyLib.AccessTools.FieldRefAccess<BuildUi, FavoritePieceList>("m_favoritePieceList");
        private static readonly HarmonyLib.AccessTools.FieldRef<BuildUi, List<ItemDrop>> s_major = HarmonyLib.AccessTools.FieldRefAccess<BuildUi, List<ItemDrop>>("m_majorMaterials");
        private static readonly string[] ListIds = { "usage", "material", "recent", "favorites" };
        private static readonly System.Reflection.MethodInfo s_refill = HarmonyLib.AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
        private static readonly HashSet<string> s_listsSaid = new HashSet<string>(StringComparer.Ordinal);
        // vanilla PieceTable.UpdateAvailable with noPlacementCost = true and no player (no known recipes, no season): a piece that can be
        // rock-jade needs m_enabled, every other piece is available
        private static void FillGod(PieceTable t)
        {
            t.m_enabledPieces.Clear(); t.m_availablePieces.Clear();
            foreach (var go in t.m_pieces)
            {
                var p = go != null ? go.GetComponent<Piece>() : null;
                if (p == null) continue;
                if (p.m_canRockJade && !p.m_enabled) continue;
                t.m_enabledPieces.Add(p);
                if (!p.m_canRockJade) t.m_availablePieces.Add(p);
            }
        }
        private static int s_availCount(PieceTable t) { var f = HarmonyLib.Traverse.Create(t).Field("m_availablePieces").GetValue() as System.Collections.ICollection; return f != null ? f.Count : -1; }

        private static string J(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

        /// <summary>The in-game build menu's lists for a tool, read from vanilla's own BuildUi list objects (client). JSON:
        /// {"lists":[{"id":"usage"|"material"|"recent"|"favorites","name","separator","tags":[{"id","name","token","pieces":[prefab]}]}],
        ///  "pieces":{prefab:{"description","recent":rank|null,"favorite":bool}}}. Tag id -1 = Show all (first in every list).
        /// playerId: 0 / the local player here (the dedi side comes in 1.1.339).</summary>
        public static string ListsJson(string tool, long playerId)
        {
            var t = Table(tool);
            var ui = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (t == null) return "{\"lists\":[],\"pieces\":{},\"error\":\"no such tool\"}";
            // 1.1.339: no Hud (the dedi / the server God client): the player's HEADLESS set of the same vanilla list objects (FapBuildLists)
            // 1.1.347 (round 8 live: every list "Show all" with pieces [] on BOTH the dedi and the client): the dedi HAS a Hud object, so
            // it took the UI path; and on either side vanilla's lists read pieceTable.m_availablePieces, which only Player
            // .UpdateAvailablePiecesList fills, for the tool in the player's HAND. So this call now always fills the asked table for the
            // player first (vanilla PieceTable.UpdateAvailable, FapBuildLists.FillAvailable), and the dedi always uses the headless set.
            FapBuildLists.Set head = null;
            bool dedi = ZNet.instance != null && ZNet.instance.IsDedicated();
            if (dedi) ui = null;
            var who = FapBuildLists.FindPlayer(playerId) ?? (ui != null ? Player.m_localPlayer : null);
            if (who == null && !dedi)
            {
                if (s_listsSaid.Add("noplayer|" + tool)) VaPieces.Log?.LogInfo($"[FapHammerMenu] ListsJson '{tool}': no loaded player {playerId} and no local player - empty lists (1.1.349)");
                return "{\"lists\":[],\"pieces\":{},\"error\":\"no build menu here and no loaded player " + playerId + "\"}";
            }
            // 1.1.349 (round 10: no ListsJson line on the dedi = the "no loaded player" return - the Studio reads the menu before / without
            // a God clone): a dedicated server with no such player fills the table god-style (PieceTable.UpdateAvailable's own
            // noPlacementCost branch: every piece, Rock Jade ones only when enabled) and uses that playerId's headless list set
            head = ui == null ? FapBuildLists.For(who != null ? who.GetPlayerID() : playerId) : null;
            if (who != null) FapBuildLists.FillAvailable(t, who); else FillGod(t);
            var loc = Localization.instance;
            var lists = head != null ? head.All() : s_lists(ui);
            var tmp = new List<Piece>();
            var desc = new Dictionary<string, string>(StringComparer.Ordinal);
            var recent = new Dictionary<string, int>(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder("{\"lists\":[");
            for (int li = 0; li < lists.Count; li++)
            {
                var pl = lists[li];
                pl.UpdateAvailableTags(t);
                if (li > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J(li < ListIds.Length ? ListIds[li] : "list" + li)).Append(",\"name\":").Append(J(loc != null ? loc.Localize(pl.DisplayName) : pl.DisplayName))
                  .Append(",\"separator\":").Append(pl.TagSeparatorIndex).Append(",\"tags\":[");
                int n = pl.ShowTags ? pl.TagCount : 0;
                for (int ti = -1; ti < n; ti++)
                {
                    int id = ti < 0 ? -1 : pl.GetTagIdByIndex(ti);
                    string token = ti < 0 ? "$hud_showall" : pl.GetTagDisplayName(ti) ?? "";
                    string name = ti < 0 ? "Show all" : (loc != null ? loc.Localize(token) : token);
                    name = System.Text.RegularExpressions.Regex.Replace(name ?? "", "<[^>]*>", "").Trim();   // 1.1.347: no rich-text markup in labels
                    tmp.Clear(); pl.GetAvailablePiecesWithTag(id, t, tmp);
                    if (ti >= 0 && tmp.Count == 0) continue;   // 1.1.347: a tag with no piece here (e.g. Core's "+ 14 tools on the hoe" hint row) is not a tab
                    if (ti >= 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(id).Append(",\"name\":").Append(J(name)).Append(",\"token\":").Append(J(token)).Append(",\"pieces\":[");
                    bool firstPiece = true;
                    for (int k = 0; k < tmp.Count; k++)
                    {
                        var p = tmp[k]; if (p == null) continue;
                        string pf = p.gameObject.name;
                        if (!firstPiece) sb.Append(','); firstPiece = false;
                        sb.Append(J(pf));
                        if (!desc.ContainsKey(pf)) desc[pf] = loc != null ? loc.Localize(p.m_description) : p.m_description;
                        if (li == 2 && ti < 0 && !recent.ContainsKey(pf)) recent[pf] = k;   // Recent: vanilla's own order, 0 = newest
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
            // put the open tool's table + tags back the way the menu had them (vanilla's own refill for the tool in hand)
            var cur = Player.m_localPlayer != null ? Player.m_localPlayer.m_buildPieces : null;
            if (head == null && cur != null && cur != t) { s_refill?.Invoke(Player.m_localPlayer, null); foreach (var pl in lists) pl.UpdateAvailableTags(cur); }
            if (s_listsSaid.Add(tool ?? ""))
                VaPieces.Log?.LogInfo($"[FapHammerMenu] ListsJson '{tool}' for {(who != null ? who.GetPlayerName() : "no player (god fill)")} ({(dedi ? "dedi, headless lists" : ui != null ? "client, menu lists" : "headless lists")}): {s_availCount(t)} piece(s) available, {desc.Count} listed (1.1.349)");
            var fav = head != null ? head.Favorites : s_favs(ui);
            sb.Append("],\"pieces\":{");
            bool first = true;
            foreach (var kv in desc)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(J(kv.Key)).Append(":{\"description\":").Append(J(kv.Value)).Append(",\"recent\":").Append(recent.TryGetValue(kv.Key, out int r) ? r.ToString() : "null")
                  .Append(",\"favorite\":").Append(fav != null && fav.IsFavorite(kv.Key) ? "true" : "false").Append('}');
            }
            return sb.Append("}}").ToString();
        }

        /// <summary>Add / remove a piece in the player's Favorites (vanilla FavoritePieceList.AddPiece / RemovePiece; saved in the
        /// character by the game as usual). Client side in 1.1.338.</summary>
        public static bool Favorite(string tool, string prefab, bool on, long playerId, out string report)
        {
            var ui = Hud.instance != null && !(ZNet.instance != null && ZNet.instance.IsDedicated()) ? Hud.instance.m_buildUi : null;   // 1.1.347: the dedi has a Hud object; its favourites are the headless set
            var fav = ui != null ? s_favs(ui) : (FapBuildLists.FindPlayer(playerId) != null ? FapBuildLists.For(playerId).Favorites : null);   // 1.1.339: the dedi's headless set
            if (fav == null) { report = "no build menu here and no loaded player " + playerId; return false; }
            if (string.IsNullOrEmpty(prefab) || ZNetScene.instance == null || ZNetScene.instance.GetPrefab(prefab) == null) { report = $"no prefab '{prefab}'"; return false; }
            if (on) fav.AddPiece(prefab); else fav.RemovePiece(prefab);
            report = on ? $"{prefab} added to Favorites" : $"{prefab} removed from Favorites";
            return true;
        }

        /// <summary>The build menu's major materials (BuildUi.m_majorMaterials, a field on the HUD prefab - not on a dedi) as item
        /// prefab names, comma separated, for the dedi's copy (1.1.339 SetMaterialsSnapshot). Client side.</summary>
        /// <summary>1.1.339 (dedi): the major materials a client's MaterialsSnapshot() returned (comma-separated item prefab names);
        /// kept on disk (config\FiresAdminPrefabs\build_major_materials.txt) and used by the headless "material" list.</summary>
        public static void SetMaterialsSnapshot(string csv) => FapBuildLists.SetSnapshot(csv);

        public static string MaterialsSnapshot()
        {
            var ui = Hud.instance != null ? Hud.instance.m_buildUi : null;
            var m = ui != null ? s_major(ui) : null;
            return m == null ? "" : string.Join(",", m.Where(i => i != null).Select(i => i.gameObject.name));
        }

        /// <summary>The piece's menu icon as a PNG (size x size, transparent background), or null. Copies the sprite through a
        /// RenderTexture, so non-readable icons work too. Main thread.</summary>
        public static byte[] IconPng(string prefab, int size = 64)
        {
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                var sp = go != null ? go.GetComponent<Piece>()?.m_icon : null;
                if (sp == null || sp.texture == null) return null;
                size = Mathf.Clamp(size, 16, 256);
                var r = sp.textureRect; var tex = sp.texture;
                var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt, new Vector2(r.width / tex.width, r.height / tex.height), new Vector2(r.x / tex.width, r.y / tex.height));
                RenderTexture.active = rt;
                var outTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                outTex.ReadPixels(new Rect(0, 0, size, size), 0, 0); outTex.Apply(false);
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                byte[] png = VerdantsAscent.Utilities.VAPieceIconRenderer.EncodeTextureToPNG(outTex);
                UnityEngine.Object.Destroy(outTex);
                return png;
            }
            catch (Exception e) { VaPieces.Log?.LogDebug($"[FapHammerMenu] icon '{prefab}' failed: {e.Message}"); return null; }
        }

        internal static PieceTable Table(string tool)
        {
            var db = ObjectDB.instance;
            var item = db != null ? db.GetItemPrefab(string.IsNullOrEmpty(tool) ? "Hammer" : tool) : null;
            return item != null ? item.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        }
    }
}
