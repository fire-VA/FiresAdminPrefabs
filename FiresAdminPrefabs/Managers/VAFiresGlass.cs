using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.1.433 ([ghost], lead 11-16; Fire: FTS goes, placed windows must not vanish; "fap should be able to handle this cleanly no matter what mods
    // are or arent installed"): the 34 FiresGlass stained-glass windows move from FiresTossinShade into FAP, reused from OUR FTS code
    // (FiresTossinShade Modules\CustomWater\Rendering\FiresGlassPiece.cs, read 1-279 of 279: the window list, SetupPiece, ApplyCost, MakeSquareIcon).
    //   ART: [visual]'s linear "firesglass" bundle (+ "firesglass_linux"), shipped in plugins\VerdantsAscent-FiresAdminPrefabs\FapBundles\ and loaded
    //     like every mod bundle by VAExternalBundleLoader (one of the pair per platform). Same prefab names = the same hashes as FTS, so saved windows load.
    //   REGISTRATION: VAExternalBundleLoader's ZNetScene.Awake PREFIX adds them before vanilla builds m_namedPrefabs and skips any name already there;
    //     FTS's postfix then skips its own copies (FiresGlassPiece.cs:215). FAP alone, FAP + FTS in either order, FTS alone: one prefab per name.
    //   SETUP (here, per window during that injection): a box collider where none exists, the Piece exactly as FTS sets it, the square icon.
    //     The router already gave each one a persistent ZNetView (VABundleAssetRouter "Other").
    //   HAMMER: a NORMAL player piece with FTS's cost (2 Crystal + 1 Fine wood at the workbench), added to the Hammer here; FAP's admin scan never takes
    //     them (VAVanillaPieceManager.IsEligible). With FTS installed its own objects are removed from the Hammer, so each window shows once.
    //   No projector: panes only (FTS's light projector is not part of this move).
    internal static class VAFiresGlass
    {
        internal const string BundleWindows = "firesglass", BundleLinux = "firesglass_linux";
        private const string FtsGuid = "com.Fire.FiresTossinShade";

        private struct Def { public string prefab, display, icon; public Def(string p, string d, string i) { prefab = p; display = d; icon = i; } }

        // FiresGlassPiece.cs:33-69 (names, display names, icon textures; the projector fields are not used here)
        private static readonly Def[] Windows =
        {
            new Def("FiresGlassWindow", "Stained Glass Pane", "StainedEmit1"),
            new Def("FiresGlass_Aristophanes", "Aristophanes Window", "aristophanes"),
            new Def("FiresGlass_Moses", "Moses Window", "moses"),
            new Def("FiresGlass_Biology", "Biology Window", "biology"),
            new Def("FiresGlass_BigRoche", "Great Roche Window", "bigRoche"),
            new Def("FiresGlass_Saint", "Saint Window", "StainedSaint"),
            new Def("FiresGlass_Viking", "Viking Window", "StainedViking"),
            new Def("FiresGlass_Odin", "Odin Window", "StainedOdin"),
            new Def("FiresGlass_Thor", "Thor Window", "StainedThor"),
            new Def("FiresGlass_Freyja", "Freyja Window", "StainedFreyja"),
            new Def("FiresGlass_Shieldmaiden", "Shieldmaiden Window", "StainedShieldmaiden"),
            new Def("FiresGlass_Frigg", "Frigg Window", "StainedFrigg"),
            new Def("FiresGlass_Serpent", "Serpent Window", "StainedSerpent"),
            new Def("FiresGlass_Yggdrasil", "Yggdrasil Window", "StainedYggdrasil"),
            new Def("FiresGlass_Valkyrie", "Valkyrie Window", "StainedValkyrie"),
            new Def("FiresGlass_Berserker", "Berserker Window", "StainedBerserker"),
            new Def("FiresGlass_Elements", "Elemental Rose Window", "StainedElements"),
            new Def("FiresGlass_Sigil", "Sigil Window", "StainedSigil"),
            new Def("FiresGlass_Ring", "Ring Rose Window", "StainedRing"),
            new Def("FiresGlass_EmeraldArch", "Emerald Arch Window", "StainedEmeraldArch"),
            new Def("FiresGlass_Rosette", "Jeweled Rosette Window", "StainedRosette"),
            new Def("FiresGlass_SkullCrown", "Skull Crown Window", "StainedSkullCrown"),
            new Def("FiresGlass_Huskarl", "Huskarl Window", "StainedHuskarl"),
            new Def("FiresGlass_Spearmaiden", "Spearmaiden Window", "StainedSpearmaiden"),
            new Def("FiresGlass_AxeShield", "Axe Shield Window", "StainedAxeShield"),
            new Def("FiresGlass_GreatAxe", "Great Axe Window", "StainedGreatAxe"),
            new Def("FiresGlass_Mjolnir", "Mjolnir Window", "StainedMjolnir"),
            new Def("FiresGlass_WarShield", "War Shield Window", "StainedWarShield"),
            new Def("FiresGlass_Runestone", "Runestone Window", "StainedRunestone"),
            new Def("FiresGlass_RuneSword", "Rune Sword Window", "StainedRuneSword"),
            new Def("FiresGlass_Guardian", "Guardian Window", "StainedGuardian"),
            new Def("FiresGlass_Chieftain", "Chieftain Window", "StainedChieftain"),
            new Def("FiresGlass_SpearShield", "Spear Shield Window", "StainedSpearShield"),
            new Def("FiresGlass_DvergrDenNeon", "Dvergr Den Neon Sign", "DvergrDenSignNeon"),
        };

        private static Dictionary<string, Def> s_byName;
        private static Dictionary<string, Def> ByName
        {
            get
            {
                if (s_byName != null) return s_byName;
                s_byName = new Dictionary<string, Def>(StringComparer.Ordinal);
                foreach (var w in Windows) s_byName[w.prefab] = w;
                return s_byName;
            }
        }

        // FAP's own set-up window prefabs (they stay loaded for the session, like every bundle prefab)
        private static readonly Dictionary<string, GameObject> s_ours = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        internal static bool IsOurBundle(string bundleLabel) =>
            string.Equals(bundleLabel, BundleWindows, StringComparison.OrdinalIgnoreCase) || string.Equals(bundleLabel, BundleLinux, StringComparison.OrdinalIgnoreCase);

        /// <summary>A FiresGlass window prefab that came from FAP's firesglass bundle.</summary>
        internal static bool IsWindow(GameObject prefab) =>
            prefab != null && ByName.ContainsKey(prefab.name) && VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out string label) && IsOurBundle(label);

        private static bool FtsLoaded => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FtsGuid);

        /// <summary>VAExternalBundleLoader's injection, right after the router (the window already has its one persistent ZNetView), before it joins
        /// ZNetScene.m_prefabs. FiresGlassPiece.SetupPiece (:96-142) minus the projector component.</summary>
        internal static void Setup(GameObject prefab)
        {
            if (!IsWindow(prefab) || s_ours.ContainsKey(prefab.name)) return;
            var w = ByName[prefab.name];
            try
            {
                if (prefab.GetComponentInChildren<Collider>(true) == null)
                {
                    // from the meshes' own bounds in root space (works on meshes that dropped their CPU copy)
                    var bc = prefab.AddComponent<BoxCollider>();
                    if (Snapping.VASnapGeometry.TryLocalBounds(prefab, out Bounds b) && b.size.sqrMagnitude > 1e-4f) { bc.center = b.center; bc.size = b.size; }
                    else bc.size = new Vector3(2f, 3f, 0.1f);
                }

                var piece = prefab.GetComponent<Piece>() ?? prefab.AddComponent<Piece>();
                piece.m_name = w.display;
                piece.m_description = "A stained-glass window.";
                piece.m_category = Piece.PieceCategory.Furniture;
                piece.m_enabled = true;
                piece.m_canBeRemoved = true;
                piece.m_allowedInDungeons = true;
                piece.m_clipEverything = true;
                piece.m_clipGround = true;
                piece.m_groundPiece = false;
                piece.m_groundOnly = false;
                piece.m_cultivatedGroundOnly = false;
                piece.m_waterPiece = false;
                piece.m_noInWater = false;
                piece.m_notOnFloor = false;
                piece.m_notOnTiltingSurface = false;
                piece.m_allowRotatedOverlap = true;
                piece.m_placeEffect = new EffectList();
                ApplyCost(piece);   // again from AddToHammer when ObjectDB / the workbench were not ready yet

                VAExternalBundleLoader.TryGetSourceBundle(prefab.name, out string label);
                Texture2D tex = null;
                try { tex = VAExternalBundleLoader.GetOpenBundle(label)?.LoadAsset<Texture2D>(w.icon); } catch { }
                if (tex != null) piece.m_icon = MakeSquareIcon(tex);
                else VaPieces.Log?.LogDebug($"[FiresGlass] {prefab.name}: icon texture '{w.icon}' not in '{label}'");

                s_ours[prefab.name] = prefab;
            }
            catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [FiresGlass] setting up '{prefab.name}' failed: {ex.Message}"); }
        }

        // FiresGlassPiece.ApplyCost (:86-94): 2 Crystal + 1 Fine wood at the workbench
        private static void ApplyCost(Piece piece)
        {
            var crystal = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Crystal")?.GetComponent<ItemDrop>() : null;
            var fineWood = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("FineWood")?.GetComponent<ItemDrop>() : null;
            if (crystal != null && fineWood != null)
                piece.m_resources = new[] { new Piece.Requirement { m_resItem = crystal, m_amount = 2, m_recover = true }, new Piece.Requirement { m_resItem = fineWood, m_amount = 1, m_recover = true } };
            var bench = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>() : null;
            if (bench != null) piece.m_craftingStation = bench;
        }

        // FiresGlassPiece.MakeSquareIcon (:172-195): the build menu stretches a non-square sprite, so the window is letterboxed into a transparent
        // square by a GPU blit (works on non-readable bundle textures).
        private static Sprite MakeSquareIcon(Texture2D tex)
        {
            const int S = 128;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            var rt = RenderTexture.GetTemporary(S, S, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            int w = aspect >= 1f ? S : Mathf.Max(1, Mathf.RoundToInt(S * aspect));
            int h = aspect >= 1f ? Mathf.Max(1, Mathf.RoundToInt(S / aspect)) : S;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, S, S, 0);
            Graphics.DrawTexture(new Rect((S - w) / 2, (S - h) / 2, w, h), tex);
            GL.PopMatrix();
            var icon = new Texture2D(S, S, TextureFormat.RGBA32, false);
            icon.ReadPixels(new Rect(0, 0, S, S), 0, 0);
            icon.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return Sprite.Create(icon, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f));
        }

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            return hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        }

        /// <summary>Puts FAP's windows on the Hammer with their cost (FiresGlassPiece.AddToHammer :221-248), then drops FTS's own copies when FTS is
        /// installed. Called at the end of ZNetScene.Awake (after FTS's postfix) and at Player.OnSpawned (after FTS's). Idempotent.</summary>
        internal static void AddToHammer(string when)
        {
            if (s_ours.Count == 0 || ZNetScene.instance == null) return;
            var table = HammerTable();
            if (table == null) return;
            table.m_pieces.RemoveAll(p => p == null);
            int added = 0;
            foreach (var kv in s_ours)
            {
                var prefab = kv.Value;
                if (prefab == null || ZNetScene.instance.GetPrefab(kv.Key) != prefab) continue;   // another mod owns that name in this world
                var piece = prefab.GetComponent<Piece>();
                if (piece != null && (piece.m_resources == null || piece.m_resources.Length == 0 || piece.m_craftingStation == null)) ApplyCost(piece);
                if (!table.m_pieces.Contains(prefab)) { table.m_pieces.Add(prefab); added++; }
            }
            int removed = Dedupe(table);
            if (added > 0 || removed > 0) VaPieces.Log?.LogDebug($"[FiresGlass] Hammer ({when}): {added} window(s) added, {removed} FTS duplicate(s) removed");
        }

        private static bool s_dedupeSaid;

        // FTS adds its OWN cached objects to the Hammer (FiresGlassPiece.cs:241-243, Contains compares objects), so with FTS installed a window would
        // show twice. An entry named like a window whose object is not the one ZNetScene resolves that name to is FTS's and goes.
        private static int Dedupe(PieceTable table)
        {
            if (table == null || !FtsLoaded || ZNetScene.instance == null) return 0;
            int removed = table.m_pieces.RemoveAll(go => go != null && ByName.ContainsKey(go.name) && ZNetScene.instance.GetPrefab(go.name) != go);
            if (removed > 0 && !s_dedupeSaid) { s_dedupeSaid = true; VaPieces.Log?.LogDebug($"[FiresGlass] removed {removed} FTS duplicate FiresGlass hammer entr{(removed == 1 ? "y" : "ies")} (FAP's windows stay)"); }
            return removed;
        }

        /// <summary>One Info line per world load: which of the 34 names FAP serves, which another mod registered first, which are missing.</summary>
        internal static void Report()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return;
            int ours = 0;
            var other = new List<string>(); var missing = new List<string>();
            foreach (var w in Windows)
            {
                var got = scene.GetPrefab(w.prefab);
                if (got == null) missing.Add(w.prefab);
                else if (s_ours.TryGetValue(w.prefab, out var mine) && mine == got) ours++;
                else other.Add(w.prefab);
            }
            if (ours == 0 && other.Count == 0 && s_ours.Count == 0) { VaPieces.Log?.LogDebug("[FiresGlass] no firesglass bundle loaded and no other mod registers the windows"); return; }
            Debug.Log($"{VaPieces.PluginName}: [FiresGlass] {ours}/{Windows.Length} windows registered from FAP's firesglass bundle"
                + (other.Count > 0 ? $", {other.Count} already registered by another mod (kept theirs: {string.Join(", ", other.ToArray())})" : "")
                + (missing.Count > 0 ? $", {missing.Count} MISSING ({string.Join(", ", missing.ToArray())})" : "")
                + (FtsLoaded ? "; FiresTossinShade is installed: its duplicate Hammer entries are removed" : ""));
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake_FiresGlass
        {
            [HarmonyPriority(Priority.Last)]
            [HarmonyPostfix]
            private static void Postfix()
            {
                try { AddToHammer("ZNetScene.Awake"); Report(); }
                catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [FiresGlass] ZNetScene.Awake step failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class Player_OnSpawned_FiresGlass
        {
            [HarmonyPriority(Priority.Last)]
            [HarmonyPostfix]
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                try { AddToHammer("Player.OnSpawned"); } catch (Exception ex) { Debug.LogWarning($"{VaPieces.PluginName}: [FiresGlass] spawn step failed: {ex.Message}"); }
            }
        }

        // The build menu's list is rebuilt from m_pieces here (menu open / refresh, never per frame): FTS re-adding its copies at any time is cleaned
        // before the player sees them. Only with FTS installed and FAP serving windows; otherwise one bool check.
        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
        private static class PieceTable_UpdateAvailable_FiresGlass
        {
            [HarmonyPrefix]
            private static void Prefix(PieceTable __instance)
            {
                if (s_ours.Count == 0 || !FtsLoaded) return;
                try { Dedupe(__instance); } catch { }
            }
        }
    }
}
