using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // 1.1.260 (Arbby via Fire 10-15: "the persistence effect doesn't seem to translate to BPs"): the mirror of the FX Clear Brush.
    // Click -> every FX / VFX-tab or self-destroying (TTL) object in the brush radius gets FAP's persist rule as if it had been placed
    // with "Place with Persistence": exactly what Infinity Hammer pastes (NoCreator skips FAP's placement hook), blueprints saved
    // from never-tagged pieces, and IH-placed effects need. The current persistence mode decides what is written:
    //   Ewp  - FAP_persist = 1 (EWP's universal rule respawns it)
    //   Fap  - fap_respawn = 1 and the TTL cancelled (vap_effect_no_ttl + the live override), FAP respawns it
    //   None - nothing written; the brush says why (VanillaFriendly without EWP, or the FAP respawner off)
    // Every object also gets fap_piece = 1. Explicit by design: FAP never auto-persists effects that appear without a placer
    // (that was the old bug: rain wetness and troll dust inheriting respawn). One Info line per object + a total.
    public static class VAFapPersistBrushTool
    {
        public const string NamePrefix = "FAP_FxPersistTool";

        private static readonly Color32 IconRingColor = new Color32(60, 200, 90, 255);
        private static readonly Color32 IconFillColor = new Color32(40, 150, 70, 200);
        private static readonly int FapPersistKey = "FAP_persist".GetStableHashCode();
        private static readonly int FapPieceKey = "fap_piece".GetStableHashCode();

        private const string Description =
            "<color=#55dd77>Make Persistent Brush.</color> Click to make every FAP effect (FX / VFX tab) and every self-destroying "
            + "piece in the brush radius persistent, as if it had been placed with 'Place with Persistence': for pasted blueprints "
            + "and Infinity Hammer placements. Uses the current persistence mode (EWP respawn, or FAP's respawner); with neither "
            + "available it changes nothing and says why. Scroll = radius, Shift+Scroll = height (the FX brush radius).";

        private static readonly Dictionary<Piece.PieceCategory, GameObject> _standinsByCategory = new Dictionary<Piece.PieceCategory, GameObject>();
        private static Sprite _icon;

        public static bool IsStandinName(string name) => !string.IsNullOrEmpty(name) && name.StartsWith(NamePrefix, StringComparison.Ordinal);

        public static IEnumerable<KeyValuePair<Piece.PieceCategory, GameObject>> AllStandins => _standinsByCategory;

        public static GameObject EnsureStandinFor(Piece.PieceCategory category)
        {
            if (_standinsByCategory.TryGetValue(category, out var existing) && existing != null) return existing;
            if (_icon == null) _icon = VAAreaClearBrush.CreateIcon("FAP_FxPersistIcon", IconRingColor, IconFillColor, IsIconPlus);
            var standin = VAAreaClearBrush.CreateStandin(NamePrefix, $"{NamePrefix}_{(int)category}", "Make Persistent Brush", Description, category, _icon);
            _standinsByCategory[category] = standin;
            return standin;
        }

        // Returns (made persistent, already persistent, skipped as not eligible); reason != null when the mode allows nothing.
        public static (int Made, int Already, int Skipped) PersistArea(Vector3 center, float radius, out string reason)
        {
            var mode = VerdantsAscent.Patches.VAFapRespawner.Mode(out reason);
            int made = 0, already = 0, skipped = 0;
            if (mode == VerdantsAscent.Patches.VAPersistMode.None)
            {
                VaPieces.Log?.LogInfo($"[FAP Persist] brush at {center:F1} r={radius:F1}: nothing changed ({reason})");
                return (0, 0, 0);
            }
            foreach (var netView in VAAreaClearBrush.FindNetViewsInRadius(center, radius))
            {
                try
                {
                    var piece = netView.GetComponent<Piece>();
                    bool inFxTab = IsFxTab(piece);
                    if (!VerdantsAscent.Patches.VAFapRespawner.IsEligible(piece, inFxTab)) { skipped++; continue; }
                    var zdo = netView.GetZDO();
                    string name = Utils.GetPrefabName(netView.gameObject);
                    bool had = mode == VerdantsAscent.Patches.VAPersistMode.Ewp
                        ? zdo.GetInt(FapPersistKey, 0) == 1
                        : zdo.GetInt(VerdantsAscent.Patches.VAFapRespawner.RespawnKey, 0) == 1;
                    if (had) { already++; continue; }
                    if (!netView.IsOwner()) netView.ClaimOwnership();
                    zdo.Set(FapPieceKey, 1);
                    if (mode == VerdantsAscent.Patches.VAPersistMode.Ewp) zdo.Set(FapPersistKey, 1);
                    else
                    {
                        VerdantsAscent.Patches.VAFapRespawner.TagIfFapMode(zdo, name);
                        if (VerdantsAscent.Patches.VAEffectTtlOverride.HasTimedComponents(netView.gameObject))
                        {
                            zdo.Set(VerdantsAscent.Patches.VAEffectTtlKeys.OverrideHash, true);
                            VerdantsAscent.Patches.VAEffectTtlOverride.Apply(netView.gameObject);
                        }
                    }
                    made++;
                    VerdantsAscent.VaPieces.Log?.LogDebug($"[FAP Persist] brush made '{name}' at {zdo.GetPosition():F1} persistent (mode {mode})");
                }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [FAP Persist] brush failed on '{netView.name}': {ex.Message}"); }
            }
            VaPieces.Log?.LogInfo($"[FAP Persist] brush at {center:F1} r={radius:F1}, mode {mode}: {made} made persistent, {already} already were, {skipped} not eligible (not FX / no TTL)");
            return (made, already, skipped);
        }

        private static bool IsFxTab(Piece piece)
        {
            if (piece == null) return false;
            var c = piece.m_category;
            return (VerdantsAscent.Patches.VACategoryRegistry.TryGet(VerdantsAscent.Patches.VACategoryRegistry.CatFX, out var fx) && c == fx)
                || (VerdantsAscent.Patches.VACategoryRegistry.TryGet(VerdantsAscent.Patches.VACategoryRegistry.CatVFX, out var vfx) && c == vfx);
        }

        // A plus sign inside the brush icon's disc (the clear brush has a slash).
        private static bool IsIconPlus(float offsetX, float offsetY) =>
            VAAreaClearBrush.IsInsideIconFill(offsetX, offsetY) && (Mathf.Abs(offsetX) <= 6f || Mathf.Abs(offsetY) <= 6f);
    }
}
