using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Snapping
{
    /// <summary>
    /// A player's default snap point per piece (Fire 10-13: personal preference; one player builds from the centre, another
    /// from a corner). Q/E cycle snap points as before; BuildMenu > SnapDefaultKey (default Shift+Q) makes the CURRENT snap
    /// point this player's default for that piece, and pressing it again on the same point clears it. Each time the piece is
    /// taken out (the placement ghost is set up), the saved point is selected; a saved index the piece no longer has falls
    /// back to vanilla's automatic snapping. Client-only: one small file per player in FiresAdminPrefabs' config folder
    /// (config\FiresAdminPrefabs\SnapDefaults\&lt;player id&gt;.txt, "version=2" then "prefab=x,y,z" lines: the point's position, so new layouts or spacing can't make it mean another point), never synced.
    /// </summary>
    [HarmonyPatch]
    public static class VASnapDefaults
    {
        private static readonly AccessTools.FieldRef<Player, int> ManualSnap = AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        private static readonly Dictionary<string, Vector3> _defaults = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        private const float SameSpot = 0.05f;
        private static long _loadedFor;
        private static readonly List<Transform> _points = new List<Transform>();

        private static string FileFor(long playerId) =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", "SnapDefaults", playerId.ToString(CultureInfo.InvariantCulture) + ".txt");

        private static void EnsureLoaded(Player p)
        {
            long id = p.GetPlayerID();
            if (_loadedFor == id) return;
            _loadedFor = id;
            _defaults.Clear();
            try
            {
                string path = FileFor(id);
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                if (lines.Length > 0 && lines[0].Trim() == "version=2")
                {
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int eq = lines[i].LastIndexOf('=');
                        var v = eq > 0 ? lines[i].Substring(eq + 1).Split(',') : null;
                        if (v != null && v.Length == 3 && float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                            && float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                            _defaults[lines[i].Substring(0, eq)] = new Vector3(x, y, z);
                    }
                    return;
                }
                ConvertOldFile(p, lines);
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: snap defaults could not be read: {ex.Message}"); }
        }

        // 1.1.379: old "prefab=index" lines. A piece with its own points still has them in the same order, so the index is still good; a piece whose
        // points now come from the rule can't be mapped. GetPrefab realizes a lazy piece, once, for the few a player saved.
        private static void ConvertOldFile(Player p, string[] lines)
        {
            int dropped = 0;
            foreach (var line in lines)
            {
                int eq = line.LastIndexOf('=');
                if (eq <= 0 || !int.TryParse(line.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;
                string prefab = line.Substring(0, eq);
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                var piece = go != null ? go.GetComponent<Piece>() : null;
                _points.Clear();
                if (piece != null) piece.GetSnapPoints(_points);
                if (piece == null || VASnapGeometry.MadeByRule(prefab) || idx < 0 || idx >= _points.Count)
                {
                    dropped++;
                    VaPieces.Log?.LogDebug($"[SnapDefault] {prefab}: saved #{idx + 1} reset, the piece's snap points changed");
                    continue;
                }
                _defaults[prefab] = _points[idx].localPosition;
            }
            Save(p);
            if (dropped > 0) MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"{dropped} saved snap default(s) were reset because snap points changed");
        }

        private static void Save(Player p)
        {
            try
            {
                string path = FileFor(p.GetPlayerID());
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string> { "version=2" };
                foreach (var kv in _defaults)
                    lines.Add(kv.Key + "=" + string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####}", kv.Value.x, kv.Value.y, kv.Value.z));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: snap defaults could not be saved: {ex.Message}"); }
        }

        // The index of the piece's point at the saved position, or -1.
        private static int IndexOf(Piece piece, Vector3 saved)
        {
            _points.Clear();
            if (piece != null) piece.GetSnapPoints(_points);
            for (int i = 0; i < _points.Count; i++)
                if (Vector3.Distance(_points[i].localPosition, saved) <= SameSpot) return i;
            return -1;
        }

        /// <summary>The saved default for a piece prefab, or -1.</summary>
        public static int GetDefault(Player p, string prefab)
        {
            if (p == null || string.IsNullOrEmpty(prefab)) return -1;
            EnsureLoaded(p);
            if (!_defaults.TryGetValue(prefab, out var saved)) return -1;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            return go != null ? IndexOf(go.GetComponent<Piece>(), saved) : -1;
        }

        /// <summary>
        /// Makes the current snap point the default for the piece in hand, or clears it when it already is. Returns the
        /// message shown (also used by fap_snap_default_test), or null when there is nothing to set.
        /// </summary>
        public static string Toggle(Player p)
        {
            var ghost = p != null ? Ghost(p) : null;
            if (ghost == null) return null;
            string prefab = Utils.GetPrefabName(ghost);
            int current = ManualSnap(p);
            EnsureLoaded(p);
            string msg;
            if (EspmeOwnsSelection()) msg = "Snap defaults need ExtraSnapPointsMadeEasy's vanilla manual snapping turned on";
            else if (current < 0) msg =$"Pick a snap point first (cycle with the snap keys) to set the default for {prefab}";
            else if (_defaults.TryGetValue(prefab, out var saved) && IndexOf(ghost.GetComponent<Piece>(), saved) == current)
            {
                _defaults.Remove(prefab);
                Save(p);
                msg = $"Default snap cleared: {prefab}";
            }
            else
            {
                IndexOf(ghost.GetComponent<Piece>(), Vector3.zero);   // fills _points
                _defaults[prefab] = _points[current].localPosition;
                Save(p);
                msg = $"Default snap set: {prefab} #{current + 1}";
            }
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, msg);
            Debug.Log($"{VaPieces.PluginName}: [SnapDefault] {msg} (index {current}, player {p.GetPlayerID()})");
            return msg;
        }

        // 1.1.320: the snap point selected when this frame's Player.Update began. Vanilla's "previous snap" is TabLeft (Q) with no
        // modifier check (Player.UpdatePlacementGhost), and it runs inside Player.Update before this postfix, so Shift+Q used to
        // step back one point first and then save THAT point. The postfix puts the selection back before saving it.
        private static int s_snapAtFrameStart = -1;

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPrefix]
        private static void Player_Update_Prefix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) s_snapAtFrameStart = ManualSnap(__instance);
        }

        // The key, checked while a piece is in hand (KeyBinding: live config, silent while a text field has focus).
        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Player_Update(Player __instance)
        {
            if (__instance != Player.m_localPlayer || Ghost(__instance) == null) return;
            var key = ConfigManager.Instance?.configSnapDefaultKey;
            if (key == null || !key.IsPressed()) return;
            if (ManualSnap(__instance) != s_snapAtFrameStart)
            {
                VerdantsAscent.VaPieces.Log?.LogDebug($"{VaPieces.PluginName}: [SnapDefault] the default key also moved the snap point ({s_snapAtFrameStart} -> {ManualSnap(__instance)}); kept {s_snapAtFrameStart}");
                ManualSnap(__instance) = s_snapAtFrameStart;
            }
            Toggle(__instance);
        }

        // 1.1.320: ExtraSnapPointsMadeEasy (Searica) switches vanilla manual snapping off unless its own "vanilla manual snap" setting
        // is on: it resets m_manualSnapPoint to Auto every frame, so a saved default can never stay selected. Selection is left to it
        // (Fire 10-26: no ESPME modes in FAP); the player is told why instead of silently getting Auto.
        private static bool s_espmeLogged;
        internal static bool EspmeOwnsSelection()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("Searica.Valheim.ExtraSnapPointsMadeEasy", out var info) || info.Instance == null) return false;
                var entry = AccessTools.Field(info.Instance.GetType(), "VanillaManualSnapEnabled")?.GetValue(info.Instance)
                            ?? AccessTools.Property(info.Instance.GetType(), "VanillaManualSnapEnabled")?.GetValue(info.Instance);
                bool vanillaOn = entry is BepInEx.Configuration.ConfigEntry<bool> b && b.Value;
                if (!vanillaOn && !s_espmeLogged)
                {
                    s_espmeLogged = true;
                    Debug.Log($"{VaPieces.PluginName}: [SnapDefault] ExtraSnapPointsMadeEasy has vanilla manual snapping off: saved snap defaults are inactive (turn its vanilla manual snap setting on to use them)");
                }
                return !vanillaOn;
            }
            catch { return false; }
        }

        // Taking a piece out (vanilla resets m_manualSnapPoint to -1 here): the player's default for it is selected.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPostfix]
        private static void Player_SetupPlacementGhost(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var ghost = Ghost(__instance);
            if (ghost == null) return;
            string prefab = Utils.GetPrefabName(ghost);
            EnsureLoaded(__instance);
            if (!_defaults.TryGetValue(prefab, out var saved) || EspmeOwnsSelection()) return;
            int index = IndexOf(ghost.GetComponent<Piece>(), saved);
            if (index < 0)
            {
                VaPieces.Log?.LogDebug($"[SnapDefault] {prefab}: no snap point at the saved spot now; Auto");
                return;
            }
            ManualSnap(__instance) = index;

        }
    }
}
