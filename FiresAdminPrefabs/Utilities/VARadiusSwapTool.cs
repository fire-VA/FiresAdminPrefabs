using System;
using System.Collections.Generic;
using Splatform;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Utilities
{
    // 1.1.291 (Fire: Radius Swapper): one build-menu tool that swaps every FROM piece inside a radius + height cylinder for the
    // TO piece, keeping each one's position and rotation. The stand-in (icon, hidden inactive piece, list position) is the FAP
    // brush stand-in (reused from VAAreaClearBrush.CreateStandin / CreateIcon, smallest example VAFapContainerClearTool); the
    // click runs here instead of placing (HammerClutterPaintPatch PlaceBranch.Swap); the ring, radius and height come from
    // VAClutterBrushIndicator + VARadiusWheel (reused from FiresAdminTerrain ZoneModeAreaTool). Picking the pair and the
    // presets live in VASwapUi. Admin only: the stand-in is registered only when VAVanillaPieceManager.IsAdminGateOpen().
    public static class VARadiusSwapTool
    {
        public const string StandinName = "FAP_RadiusSwapTool";
        public const float MinRadius = 1f;
        public const float MaxRadius = 30f;
        public const float RadiusStep = 1f;

        public static float Radius = 4f;
        public static string FromName;
        public static string ToName;

        private static readonly Color32 IconRingColor = new Color32(80, 150, 240, 255);
        private static readonly Color32 IconFillColor = new Color32(40, 90, 170, 200);
        private static GameObject _standin;
        private static bool _loggedNoUndo;

        public static bool IsStandinName(string name) => name == StandinName;

        public static Piece StandinPiece => _standin != null ? _standin.GetComponent<Piece>() : null;

        public static bool HasPair => !string.IsNullOrEmpty(FromName) && !string.IsNullOrEmpty(ToName);

        public static GameObject EnsureStandin(Piece.PieceCategory category)
        {
            if (_standin != null) return _standin;
            var icon = VAAreaClearBrush.CreateIcon("FAP_RadiusSwap_Icon", IconRingColor, IconFillColor, IsIconMark);
            _standin = VAAreaClearBrush.CreateStandin(StandinName, StandinName, "Radius Swapper",
                "<color=#5aa0ff>Radius Swapper</color> - click it to pick the prefab to swap and the prefab to swap it to, then click "
                + "in the world to swap every one inside the ring. Scroll = radius, Shift+Scroll = height. "
                + "Presets: the FAP context menu on this tool, Setup swap preset.", category, icon);
            return _standin;
        }

        // 1.1.303 (Fire: preset-aware swapping): the full pick, prefab + optional preset (FromName / ToName stay the prefabs).
        public static SwapPick From;
        public static SwapPick To;

        public static bool HasPresets => (From != null && From.HasPreset) || (To != null && To.HasPreset);

        public static void Queue(string from, string to) => Queue(new SwapPick(from), new SwapPick(to));

        public static void Queue(SwapPick from, SwapPick to)
        {
            From = from;
            To = to;
            FromName = from?.Prefab;
            ToName = to?.Prefab;
            VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] queued {from?.LogText} -> {to?.LogText}");
        }

        // The confirm card / hint wording: "<Prefab> (Sound: x)", "(no preset)" on TO when FROM has a preset.
        public static string FromText(SwapPick from, SwapPick to) => from?.Text(false) ?? "";
        public static string ToText(SwapPick from, SwapPick to) => to?.Text(from != null && from.HasPreset) ?? "";

        public static string DisplayName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return "";
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
                var piece = go != null ? go.GetComponent<Piece>() : null;
                string n = piece != null && Localization.instance != null ? Localization.instance.Localize(piece.m_name) : null;
                return string.IsNullOrEmpty(n) || n.StartsWith("[", StringComparison.Ordinal) ? prefabName : n;
            }
            catch { return prefabName; }
        }

        public static string PairText() => !HasPair
            ? "No swap queued: click the Radius Swapper in the build menu"
            : HasPresets
                ? $"Swap {FromText(From, To)} → {ToText(From, To)}{(FromName == ToName ? " (in place)" : "")}"
                : $"Swap {DisplayName(FromName)} ({FromName}) → {DisplayName(ToName)} ({ToName})";

        // Closes the build menu and selects the swapper, so the radius tool is in hand with the queued pair.
        public static void CloseMenuAndSelect()
        {
            var player = Player.m_localPlayer;
            var piece = StandinPiece;
            bool selected = player != null && piece != null && player.SetSelectedPiece(piece);
            Hud.CloseBuildUi();
            VerdantsAscent.VaPieces.Log?.LogDebug($"[Swapper] build menu closed, swapper {(selected ? "selected" : "NOT selected (not on this table)")}: {FromName} -> {ToName}");
            if (player != null)
                player.Message(MessageHud.MessageType.Center, selected ? PairText() : "Radius Swapper is not on this hammer's table");
        }

        // Swaps every networked Piece named fromName inside the cylinder for toName at the same position and rotation, the way
        // vanilla Player.PlacePiece places one (SetCreator + WearNTear.OnPlaced), then destroys the old one with ZNetScene.Destroy
        // after claiming ownership: no drops, no resources, no duplicates. One click = one WorldEditCommands undo when WEC is there.
        public static int SwapPiecesInArea(Vector3 center, float radius, float yMin, float yMax, string fromName, string toName, out string error)
            => SwapPiecesInArea(center, radius, yMin, yMax, new SwapPick(fromName), new SwapPick(toName), out error, out _);

        // 1.1.303 (Fire: preset-aware swapping). FROM with a preset: only pieces of that prefab that CARRY it. TO with a preset: the new
        // piece gets it through the preset's own call. FROM with a preset and a plain TO: the result has none (that kind cleared).
        // Same prefab with a different preset (or preset -> none, none -> preset): no replacement, the preset changes in place.
        // No preset on either side: exactly the 1.1.296 swap. Returns swapped + changed in place; summary = the counts for the HUD.
        public static int SwapPiecesInArea(Vector3 center, float radius, float yMin, float yMax, SwapPick from, SwapPick to, out string error, out string summary)
        {
            error = null;
            summary = null;
            var scene = ZNetScene.instance;
            if (scene == null) { error = "no world loaded"; return 0; }
            string fromName = from?.Prefab, toName = to?.Prefab;
            if (string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toName)) { error = "no swap queued"; return 0; }
            var toPrefab = scene.GetPrefab(toName);
            if (toPrefab == null) { error = $"there is no prefab named {toName}"; return 0; }
            if (toPrefab.GetComponent<Piece>() == null) { error = $"{toName} is not a build piece"; return 0; }
            bool samePrefab = string.Equals(fromName, toName, StringComparison.Ordinal);
            if (samePrefab && from.SameAs(to)) { error = from.HasPreset ? "FROM and TO are the same prefab and preset" : "FROM and TO are the same prefab"; return 0; }
            if (!VASwapPresets.Exists(from)) { error = $"the FROM preset {from.PresetLabel} no longer exists"; return 0; }
            if (!VASwapPresets.Exists(to)) { error = $"the TO preset {to.PresetLabel} no longer exists"; return 0; }
            bool presetAware = from.HasPreset || to.HasPreset;

            var targets = new List<ZNetView>();
            int withoutPreset = 0, alreadyHad = 0;
            foreach (var netView in VAAreaClearBrush.FindNetViewsInCylinder(center, radius, yMin, yMax))
            {
                if (netView.GetComponent<Piece>() == null) continue;
                if (!string.Equals(netView.GetPrefabName(), fromName, StringComparison.Ordinal)) continue;
                if (from.HasPreset && !VASwapPresets.Carries(netView, from)) { withoutPreset++; continue; }
                if (samePrefab && !from.HasPreset && to.HasPreset && VASwapPresets.Carries(netView, to)) { alreadyHad++; continue; }
                targets.Add(netView);
            }
            string label = $"{from.LogText} -> {to.LogText}";
            if (targets.Count == 0)
            {
                summary = Summary(0, 0, 0, withoutPreset, alreadyHad, 0);
                if (presetAware) VaPieces.Log?.LogInfo($"[Swapper] {label}: {summary} at {center} r={radius:0.#}");
                return 0;
            }

            if (samePrefab)
            {
                int changed = 0, failedInPlace = 0;
                foreach (var nv in targets)
                {
                    try
                    {
                        if (VASwapPresets.ChangeInPlace(nv, from, to, out string why)) changed++;
                        else { failedInPlace++; VaPieces.Log?.LogDebug($"[Swapper] in-place change on {nv?.name} failed: {why}"); }
                    }
                    catch (Exception ex) { failedInPlace++; Debug.LogWarning($"{VaPieces.PluginName}: [Swapper] in-place change on '{nv?.name}' threw: {ex.Message}"); }
                }
                summary = Summary(targets.Count, 0, changed, withoutPreset, alreadyHad, failedInPlace);
                VaPieces.Log?.LogInfo($"[Swapper] {label}: {summary} at {center} r={radius:0.#}");
                return changed;
            }

            if (!VAUndoBridge.IsAvailable && !_loggedNoUndo)
            {
                _loggedNoUndo = true;
                VaPieces.Log?.LogInfo("[Swapper] WorldEditCommands is not installed: swaps have no undo");
            }

            var player = Player.m_localPlayer;
            int swapped = 0, failed = 0, presetFailed = 0;
            using (VAUndoBridge.Scope())
            {
                foreach (var old in targets)
                {
                    try
                    {
                        if (old == null || !old.IsValid()) { failed++; continue; }
                        var t = old.transform;
                        // 1.1.296 (Fire: swapped IH-invulnerable floors / stakes collapsed): the TO piece is spawned on a ZDO that
                        // already carries the FROM piece's data (VAZdoFirstSpawner = ZNetView.m_initZDO, reused from the blueprint
                        // path), so WearNTear.Awake / Structure Tweaks' first UpdateSupport read the infinite health, not defaults.
                        var oldZdo = old.GetZDO();
                        int copied = 0;
                        var go = VAZdoFirstSpawner.Instantiate(toPrefab, t.position, t.rotation, null, null,
                            zdo =>
                            {
                                copied = CopyZdoData(oldZdo, zdo, toPrefab);
                                // 1.1.303: FROM's preset kind and TO's are cleared from the copied data, then a TO light is stamped
                                // pre-Awake (a no-preset swap skips this: 1.1.296 exactly).
                                if (!presetAware) return;
                                if (from.HasPreset) VASwapPresets.ClearOnNewZdo(zdo, from.Kind);
                                if (to.HasPreset && to.Kind != from.Kind) VASwapPresets.ClearOnNewZdo(zdo, to.Kind);
                                VASwapPresets.StampOnNewZdo(zdo, to);
                            }, out _);
                        if (go == null) { failed++; continue; }
                        if (copied > 0) VaPieces.Log?.LogDebug($"[Swapper] {fromName} -> {toName}: {copied} ZDO value(s) carried over");
                        var piece = go.GetComponent<Piece>();
                        if (piece != null && player != null)
                        {
                            try { piece.SetCreator(player.GetPlayerID(), PlatformManager.DistributionPlatform.LocalUser.PlatformUserID); }
                            catch (Exception ex) { VaPieces.Log?.LogDebug($"[Swapper] SetCreator on {toName}: {ex.Message}"); }
                        }
                        go.GetComponent<WearNTear>()?.OnPlaced();
                        // 1.1.303: the TO preset through its own call (after SetCreator, so it wins over a preset queued on the hammer).
                        if (to.HasPreset && !VASwapPresets.ApplyToNew(go.GetComponent<ZNetView>(), to, out string presetWhy))
                        {
                            presetFailed++;
                            VaPieces.Log?.LogDebug($"[Swapper] {to.LogText} on the new piece: {presetWhy}");
                        }

                        VAUndoBridge.RecordRemove(old.GetZDO());
                        old.ClaimOwnership();
                        scene.Destroy(old.gameObject);
                        swapped++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Debug.LogWarning($"{VaPieces.PluginName}: [Swapper] swap of '{old?.name}' threw: {ex.Message}");
                    }
                }
            }
            if (!presetAware)   // 1.1.296's line, unchanged for a swap without presets
                VaPieces.Log?.LogInfo($"[Swapper] swapped {swapped}/{targets.Count} {fromName} -> {toName} at {center} r={radius:0.#} y={yMin:0.#}..{yMax:0.#}");
            summary = Summary(targets.Count, swapped, 0, withoutPreset, alreadyHad, failed);
            if (presetFailed > 0) summary += $", {presetFailed} swapped but the TO preset did not apply";
            if (presetAware) VaPieces.Log?.LogInfo($"[Swapper] {label}: {summary} at {center} r={radius:0.#}");
            return swapped;
        }

        // "N matched, N swapped, N preset-changed in place, N skipped (why)"
        private static string Summary(int matched, int swapped, int inPlace, int withoutPreset, int alreadyHad, int failed)
        {
            var why = new List<string>();
            if (withoutPreset > 0) why.Add($"{withoutPreset} without the FROM preset");
            if (alreadyHad > 0) why.Add($"{alreadyHad} already had the TO preset");
            if (failed > 0) why.Add($"{failed} failed");
            int skipped = withoutPreset + alreadyHad + failed;
            return $"{matched} matched, {swapped} swapped, {inPlace} preset-changed in place, {skipped} skipped"
                + (why.Count > 0 ? $" ({string.Join(", ", why.ToArray())})" : "");
        }

        // 1.1.296: every ZDO entry of the FROM piece onto the TO piece's pre-Awake ZDO. Position / rotation / prefab are not
        // extra data (VAZdoFirstSpawner sets them). Keys this carries (rig DLL strings, see the 1.1.296 report): vanilla "health" /
        // "support" (ZDOVars), Infinity Hammer + Structure Tweaks "wear" / "fall" / "growth" / "collision" / "interact" / "restrict" /
        // "damage", WorldEditCommands "override_*" (override_health, override_wear, override_fall ...), scale. Health and support
        // are only carried when the FROM piece is invulnerable (health far above any vanilla max): a normal wood floor's 400 hp
        // on a stone floor would read as damage, and a normal support value is recomputed by the TO piece itself.
        internal const float InfiniteHealth = 1e6f;
        private static readonly int HealthHash = "health".GetStableHashCode();
        private static readonly int SupportHash = "support".GetStableHashCode();

        internal static int CopyZdoData(ZDO from, ZDO to, GameObject toPrefab)
        {
            if (from == null || to == null) return 0;
            ZDOExtraData.GetData(from.m_uid, out var floats, out var vecs, out var quats, out var ints, out var longs, out var strings, out var bytes, out _);
            float health = from.GetFloat(HealthHash, 0f);
            var wnt = toPrefab != null ? toPrefab.GetComponent<WearNTear>() : null;
            bool infinite = health >= InfiniteHealth || (wnt != null && health > wnt.m_health * 10f);
            int n = 0;
            for (int i = 0; i < floats.Count; i++)   // 1.0.30: GetData hands back FastIterableKeyValues (Count + indexer, no enumerator)
            {
                var kv = floats[i];
                if ((kv.Key == HealthHash || kv.Key == SupportHash) && !infinite) continue;
                to.Set(kv.Key, kv.Value); n++;
            }
            for (int i = 0; i < vecs.Count; i++) { var kv = vecs[i]; to.Set(kv.Key, kv.Value); n++; }
            for (int i = 0; i < quats.Count; i++) { var kv = quats[i]; to.Set(kv.Key, kv.Value); n++; }
            for (int i = 0; i < ints.Count; i++) { var kv = ints[i]; to.Set(kv.Key, kv.Value); n++; }
            for (int i = 0; i < longs.Count; i++) { var kv = longs[i]; to.Set(kv.Key, kv.Value); n++; }
            for (int i = 0; i < strings.Count; i++) { var kv = strings[i]; to.Set(kv.Key, kv.Value); n++; }
            for (int i = 0; i < bytes.Count; i++) { var kv = bytes[i]; to.Set(kv.Key, kv.Value); n++; }
            if (infinite) VaPieces.Log?.LogDebug($"[Swapper] invulnerable FROM piece (health {health:G3}): health + support carried over");
            return n;
        }

        // Two arrows, one each way.
        private static bool IsIconMark(float offsetX, float offsetY)
        {
            const float h = 3f;
            if (VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(-28f, 12f), new Vector2(28f, 12f), h)) return true;
            if (VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(28f, 12f), new Vector2(16f, 24f), h)) return true;
            if (VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(28f, 12f), new Vector2(16f, 0f), h)) return true;
            if (VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(28f, -12f), new Vector2(-28f, -12f), h)) return true;
            if (VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(-28f, -12f), new Vector2(-16f, 0f), h)) return true;
            return VAAreaClearBrush.IsNearIconSegment(offsetX, offsetY, new Vector2(-28f, -12f), new Vector2(-16f, -24f), h);
        }
    }
}
