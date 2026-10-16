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
    /// (config\FiresAdminPrefabs\SnapDefaults\&lt;player id&gt;.txt, "prefab=index" lines), never synced.
    /// </summary>
    [HarmonyPatch]
    public static class VASnapDefaults
    {
        private static readonly AccessTools.FieldRef<Player, int> ManualSnap = AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        private static readonly Dictionary<string, int> _defaults = new Dictionary<string, int>(StringComparer.Ordinal);
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
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.LastIndexOf('=');
                    if (eq > 0 && int.TryParse(line.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                        _defaults[line.Substring(0, eq)] = idx;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: snap defaults could not be read: {ex.Message}"); }
        }

        private static void Save(Player p)
        {
            try
            {
                string path = FileFor(p.GetPlayerID());
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                foreach (var kv in _defaults) lines.Add(kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: snap defaults could not be saved: {ex.Message}"); }
        }

        /// <summary>The saved default for a piece prefab, or -1.</summary>
        public static int GetDefault(Player p, string prefab)
        {
            if (p == null || string.IsNullOrEmpty(prefab)) return -1;
            EnsureLoaded(p);
            return _defaults.TryGetValue(prefab, out int i) ? i : -1;
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
            if (current < 0) msg = $"Pick a snap point first (cycle with the snap keys) to set the default for {prefab}";
            else if (_defaults.TryGetValue(prefab, out int saved) && saved == current)
            {
                _defaults.Remove(prefab);
                Save(p);
                msg = $"Default snap cleared: {prefab}";
            }
            else
            {
                _defaults[prefab] = current;
                Save(p);
                msg = $"Default snap set: {prefab} #{current + 1}";
            }
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, msg);
            Debug.Log($"{VaPieces.PluginName}: [SnapDefault] {msg} (index {current}, player {p.GetPlayerID()})");
            return msg;
        }

        // The key, checked while a piece is in hand (KeyBinding: live config, silent while a text field has focus).
        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Player_Update(Player __instance)
        {
            if (__instance != Player.m_localPlayer || Ghost(__instance) == null) return;
            var key = ConfigManager.Instance?.configSnapDefaultKey;
            if (key != null && key.IsPressed()) Toggle(__instance);
        }

        // Taking a piece out (vanilla resets m_manualSnapPoint to -1 here): the player's default for it is selected.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost"), HarmonyPostfix]
        private static void Player_SetupPlacementGhost(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var ghost = Ghost(__instance);
            if (ghost == null) return;
            string prefab = Utils.GetPrefabName(ghost);
            int saved = GetDefault(__instance, prefab);
            if (saved < 0) return;
            var piece = ghost.GetComponent<Piece>();
            _points.Clear();
            if (piece != null) piece.GetSnapPoints(_points);
            if (saved >= _points.Count)
            {
                Debug.Log($"{VaPieces.PluginName}: [SnapDefault] {prefab}: saved snap #{saved + 1} no longer exists ({_points.Count} now); vanilla snapping");
                return;
            }
            ManualSnap(__instance) = saved;
        }
    }
}
