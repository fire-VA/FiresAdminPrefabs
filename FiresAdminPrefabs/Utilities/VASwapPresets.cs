using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // 1.1.303 (Fire: "our object swapper for fap should give us the option to swap out prefabs with ones that have saved presets as
    // well... Even allowing us to swap one preset for another on the same object"): the per-piece presets the Radius Swapper knows.
    //   Sound      VASoundPresets templates covering the prefab's sources; carried = fap_sound_name stamp, else fap_sound equals it.
    //   Light      VALightPresetStore (a prefab with a Light); carried = va_light on and va_light_preset is the name, else the values equal.
    //   Chest      VAChestPresetStore with ContainerPrefab = the prefab; carried = va_chest_preset.
    //   Item stand VAItemStandPresetStore with StandPrefab = the prefab; carried = va_stand_preset.
    // Applying reuses each preset's own call: VASoundPresets.SaveToPiece, VALightApplier.Stamp (new piece, pre-Awake, as a queued
    // placement does) / SetLive (placed piece), VAChestConfigApplier.ApplyToPlaced, VAItemStandApplier.Apply.
    public enum SwapPresetKind { None, Sound, Light, Chest, Stand }

    public sealed class SwapPick
    {
        public readonly string Prefab;
        public readonly SwapPresetKind Kind;
        public readonly string Preset;

        public SwapPick(string prefab, SwapPresetKind kind = SwapPresetKind.None, string preset = null)
        {
            Prefab = prefab;
            Kind = string.IsNullOrEmpty(preset) ? SwapPresetKind.None : kind;
            Preset = Kind == SwapPresetKind.None ? null : preset;
        }

        public bool HasPreset => Kind != SwapPresetKind.None;

        public string PresetLabel => HasPreset ? $"{VASwapPresets.KindLabel(Kind)}: {Preset}" : null;

        public bool SameAs(SwapPick o) => o != null && string.Equals(Prefab, o.Prefab, StringComparison.Ordinal) && Kind == o.Kind
            && string.Equals(Preset ?? "", o.Preset ?? "", StringComparison.Ordinal);

        // "Wood chest (Chest: Loot 1)"; noPresetText: "(no preset)" when there is none.
        public string Text(bool noPresetText)
        {
            string n = VARadiusSwapTool.DisplayName(Prefab);
            if (HasPreset) return $"{n} ({PresetLabel})";
            return noPresetText ? $"{n} (no preset)" : n;
        }

        public string LogText => HasPreset ? $"{Prefab} ({PresetLabel})" : Prefab;
    }

    public static class VASwapPresets
    {
        public static string KindLabel(SwapPresetKind k)
        {
            switch (k)
            {
                case SwapPresetKind.Sound: return "Sound";
                case SwapPresetKind.Light: return "Light";
                case SwapPresetKind.Chest: return "Chest";
                case SwapPresetKind.Stand: return "Item stand";
                default: return "";
            }
        }

        // Every saved preset that applies to the prefab, in kind order then name.
        public static List<SwapPick> For(string prefabName)
        {
            var list = new List<SwapPick>();
            var prefab = ZNetScene.instance != null && !string.IsNullOrEmpty(prefabName) ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null) return list;
            try
            {
                foreach (var n in VASoundPresets.TemplatesFor(prefabName))
                    list.Add(new SwapPick(prefabName, SwapPresetKind.Sound, n));
            }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[Swapper] sound templates for {prefabName}: {ex.Message}"); }
            if (VALightApplier.HasLight(prefab))
                foreach (var p in VALightPresetStore.All.Where(p => p != null && !string.IsNullOrEmpty(p.name)).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase))
                    list.Add(new SwapPick(prefabName, SwapPresetKind.Light, p.name));
            if (prefab.GetComponent<Container>() != null)
                foreach (var p in VAChestPresetStore.All.Values.Where(p => p != null && !string.IsNullOrEmpty(p.name)
                             && string.Equals(p.ContainerPrefab, prefabName, StringComparison.Ordinal)).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase))
                    list.Add(new SwapPick(prefabName, SwapPresetKind.Chest, p.name));
            if (prefab.GetComponent<ItemStand>() != null)
                foreach (var p in VAItemStandPresetStore.All.Values.Where(p => p != null && !string.IsNullOrEmpty(p.name)
                             && string.Equals(p.StandPrefab, prefabName, StringComparison.Ordinal)).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase))
                    list.Add(new SwapPick(prefabName, SwapPresetKind.Stand, p.name));
            return list;
        }

        // Does the preset still exist (it may have been deleted since it was picked)?
        public static bool Exists(SwapPick pick)
        {
            if (pick == null || !pick.HasPreset) return true;
            switch (pick.Kind)
            {
                case SwapPresetKind.Sound: return VASoundPresets.Template(pick.Preset) != null;
                case SwapPresetKind.Light: return VALightPresetStore.TryGet(pick.Preset, out _);
                case SwapPresetKind.Chest: return VAChestPresetStore.TryGet(pick.Preset, out _);
                case SwapPresetKind.Stand: return VAItemStandPresetStore.TryGet(pick.Preset, out _);
                default: return false;
            }
        }

        // Does this placed piece carry the picked preset?
        public static bool Carries(ZNetView nv, SwapPick pick)
        {
            if (pick == null || !pick.HasPreset) return true;
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            if (zdo == null) return false;
            switch (pick.Kind)
            {
                case SwapPresetKind.Sound:
                    return VASoundPresets.Carries(nv, pick.Preset);
                case SwapPresetKind.Light:
                {
                    if (zdo.GetInt(VALightApplier.KeyOn) != 1) return false;
                    string stamp = zdo.GetString(VALightApplier.KeyPreset);
                    if (string.Equals(stamp, pick.Preset, StringComparison.Ordinal)) return true;
                    // An unnamed or since-renamed stamp: compare the stored values with the preset's.
                    if (!string.IsNullOrEmpty(stamp) && VALightPresetStore.TryGet(stamp, out _)) return false;
                    return VALightPresetStore.TryGet(pick.Preset, out var p) && LightValuesEqual(zdo, p);
                }
                case SwapPresetKind.Chest:
                    return string.Equals(zdo.GetString(VAChestConfigApplier.KeyAppliedPresetName), pick.Preset, StringComparison.Ordinal);
                case SwapPresetKind.Stand:
                    return string.Equals(zdo.GetString(VAItemStandApplier.KeyPreset), pick.Preset, StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        private static bool Near(float a, float b) => Mathf.Abs(a - b) <= 0.0005f + Mathf.Abs(b) * 1e-5f;

        // The values VALightApplier.Stamp writes (clamped as it clamps them) against what the ZDO holds.
        private static bool LightValuesEqual(ZDO zdo, VALightPreset p)
        {
            var c = VALightApplier.Read(zdo);
            if (c == null || p == null) return false;
            return VALightApplier.Hex(c.Color) == VALightApplier.Hex(p.Color)
                && Near(c.Intensity, Mathf.Max(0f, p.Intensity)) && Near(c.Range, Mathf.Max(0.1f, p.Range))
                && c.Shadows == p.Shadows && c.Flicker == p.Flicker
                && Near(c.FlickerAmount, Mathf.Max(0f, p.FlickerAmount)) && Near(c.FlickerSpeed, Mathf.Max(0f, p.FlickerSpeed))
                && Near(c.FlickerMovement, Mathf.Max(0f, p.FlickerMovement))
                && Near(c.LightDistance, Mathf.Max(1f, p.LightDistance)) && Near(c.ShadowDistance, Mathf.Max(1f, p.ShadowDistance))
                && VALightApplier.Hex(c.FlameTint) == VALightApplier.Hex(p.FlameTint) && VALightApplier.Hex(c.Emissive) == VALightApplier.Hex(p.Emissive);
        }

        // ---- a NEW piece (the swap's TO), on its pre-Awake ZDO after the FROM piece's data was copied onto it ----

        // Clears a kind's preset from the copied data, so the new piece starts without it.
        internal static void ClearOnNewZdo(ZDO zdo, SwapPresetKind kind)
        {
            if (zdo == null) return;
            switch (kind)
            {
                case SwapPresetKind.Sound:
                    zdo.Set(VASoundPresets.ZdoKey, "");
                    zdo.Set(VASoundPresets.ZdoNameKey, "");
                    break;
                case SwapPresetKind.Light:
                    ClearLightRaw(zdo);
                    break;
                case SwapPresetKind.Chest:
                    VAChestConfigApplier.ClearPresetFromZdo(zdo);
                    break;
                case SwapPresetKind.Stand:
                    ClearStandRaw(zdo);
                    break;
            }
        }

        // What VALightApplier.WriteAndBroadcast writes for "no preset", plus the name.
        private static void ClearLightRaw(ZDO zdo)
        {
            zdo.Set(VALightApplier.KeyOn, 0);
            zdo.Set(VALightApplier.KeyPreset, "");
            zdo.Set("HasFieldsLightFlicker", false);
            zdo.Set("HasFieldsLightLod", false);
            zdo.Set("HasFieldsFireplace", false);
        }

        private static void ClearStandRaw(ZDO zdo)
        {
            zdo.Set(VAItemStandApplier.KeyPreset, "");
            zdo.Set(VAItemStandApplier.KeyNoInteract, false);
            zdo.Set(VAItemStandApplier.KeyRespawn, 0f);
            zdo.Set(VAItemStandApplier.KeyRespawnMax, 0f);
            zdo.Set(VAItemStandApplier.KeyEwp, false);
            zdo.Set(VAItemStandEwp.KeyStand, 0);
            zdo.Set(VAItemStandApplier.KeyCore, false);
        }

        // Pre-Awake part of the TO preset: a light is stamped before Awake, as a queued light placement is (Piece.SetCreator).
        internal static void StampOnNewZdo(ZDO zdo, SwapPick to)
        {
            if (zdo == null || to == null || to.Kind != SwapPresetKind.Light) return;
            if (VALightPresetStore.TryGet(to.Preset, out var p)) VALightApplier.Stamp(zdo, p);
        }

        // After the new piece exists (and after SetCreator / OnPlaced): the rest of the TO preset, through each preset's own call.
        internal static bool ApplyToNew(ZNetView nv, SwapPick to, out string why)
        {
            why = null;
            if (to == null || !to.HasPreset) return true;
            if (nv == null || !nv.IsValid()) { why = "the new piece is gone"; return false; }
            switch (to.Kind)
            {
                case SwapPresetKind.Sound:
                {
                    var values = VASoundPresets.TemplateFor(to.Preset, VASoundPresets.Sources(to.Prefab));
                    if (values == null) { why = $"sound template '{to.Preset}' has no source of {to.Prefab}"; return false; }
                    return VASoundPresets.SaveToPiece(nv, values, out why, to.Preset);
                }
                case SwapPresetKind.Light:
                    nv.GetComponent<VALightState>()?.ApplyFromZdo();   // stamped pre-Awake; Start applies it too
                    return true;
                case SwapPresetKind.Chest:
                {
                    var container = nv.GetComponent<Container>();
                    if (container == null) { why = "not a container"; return false; }
                    if (!VAChestPresetStore.TryGet(to.Preset, out var p)) { why = $"chest preset '{to.Preset}' is gone"; return false; }
                    VAChestConfigApplier.ApplyToPlaced(p, container, true);
                    return true;
                }
                case SwapPresetKind.Stand:
                {
                    var stand = nv.GetComponent<ItemStand>();
                    if (stand == null) { why = "not an item stand"; return false; }
                    if (!VAItemStandPresetStore.TryGet(to.Preset, out var p)) { why = $"item stand preset '{to.Preset}' is gone"; return false; }
                    if (!VAItemStandApplier.Apply(p, stand)) { why = $"{p.Item} can't hang on it"; return false; }
                    return true;
                }
            }
            return true;
        }

        // ---- a PLACED piece, same prefab FROM and TO: its preset changes in place (health, position, contents kept) ----
        internal static bool ChangeInPlace(ZNetView nv, SwapPick from, SwapPick to, out string why)
        {
            why = null;
            if (nv == null || !nv.IsValid()) { why = "the piece is gone"; return false; }
            var zdo = nv.GetZDO();
            // FROM's preset goes when TO is another kind or none (a same-kind TO overwrites it below).
            if (from != null && from.HasPreset && (to == null || to.Kind != from.Kind))
                if (!ClearInPlace(nv, zdo, from.Kind, out why)) return false;
            if (to == null || !to.HasPreset) return true;
            switch (to.Kind)
            {
                case SwapPresetKind.Sound:
                {
                    var values = VASoundPresets.TemplateFor(to.Preset, VASoundPresets.Sources(to.Prefab));
                    if (values == null) { why = $"sound template '{to.Preset}' has no source of {to.Prefab}"; return false; }
                    return VASoundPresets.SaveToPiece(nv, values, out why, to.Preset);
                }
                case SwapPresetKind.Light:
                {
                    if (!VALightPresetStore.TryGet(to.Preset, out var p)) { why = $"light preset '{to.Preset}' is gone"; return false; }
                    VALightApplier.SetLive(nv, p);
                    return true;
                }
                case SwapPresetKind.Chest:
                {
                    var container = nv.GetComponent<Container>();
                    if (container == null) { why = "not a container"; return false; }
                    if (!VAChestPresetStore.TryGet(to.Preset, out var p)) { why = $"chest preset '{to.Preset}' is gone"; return false; }
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    VAChestConfigApplier.ClearPresetFromZdo(zdo);
                    VAChestConfigApplier.ApplyToPlaced(p, container, false);   // contents kept
                    return true;
                }
                case SwapPresetKind.Stand:
                {
                    var stand = nv.GetComponent<ItemStand>();
                    if (stand == null) { why = "not an item stand"; return false; }
                    if (!VAItemStandPresetStore.TryGet(to.Preset, out var p)) { why = $"item stand preset '{to.Preset}' is gone"; return false; }
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    if (!VAItemStandApplier.Apply(p, stand)) { why = $"{p.Item} can't hang on it"; return false; }
                    return true;
                }
            }
            return true;
        }

        private static bool ClearInPlace(ZNetView nv, ZDO zdo, SwapPresetKind kind, out string why)
        {
            why = null;
            switch (kind)
            {
                case SwapPresetKind.Sound:
                    return VASoundPresets.SaveToPiece(nv, null, out why);   // "" = vanilla sound, the stamp cleared with it
                case SwapPresetKind.Light:
                    VALightApplier.SetLive(nv, null);
                    return true;
                case SwapPresetKind.Chest:
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    VAChestConfigApplier.ClearPresetFromZdo(zdo);
                    try { nv.LoadFields(); } catch { }
                    return true;
                case SwapPresetKind.Stand:
                    if (!nv.IsOwner()) nv.ClaimOwnership();
                    ClearStandRaw(zdo);
                    return true;
            }
            return true;
        }
    }
}
