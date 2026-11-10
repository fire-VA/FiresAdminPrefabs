using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // 1.1.319 ([fgn], Fire 10-26: "fap placed pieces ... should respect our infinity hammer settings so if we're set for infinite
    // health it is placed with it"): a FAP-added Hammer piece the local player places gets Infinity Hammer's own after-placement
    // step (health override, no creator, no target, no physics, no remove, scale sync), through Core's one implementation
    // FiresCore.Pieces.InfinityHammerPlacement.Apply. Found by name: an older Core or no Infinity Hammer = placed as before.
    // Vanilla pieces FAP didn't add are left to IH itself (IH doesn't post-process a plain Hammer placement either).
    //
    // The new instance is caught where vanilla PlacePiece stamps its creator (Piece.SetCreator, Player.PlacePiece only), and IH
    // runs right after PlacePiece returns, so it overrides the creator vanilla just wrote, as IH does.
    //
    // Debug evidence for the spiked walls (a player: "only 3-4 of them place"): for a placed piece carrying an Aoe child (Ashlands
    // spikes, dvergr stakewalls) one line 2 s later: health %, support vs needed, the shown state (new/worn/broken), whether IH
    // ran. And while such a piece is the ghost, each placement-status change logs what the placement ray hits (trigger or not).
    [HarmonyPatch]
    internal static class VAPlacedPieceIH
    {
        private static bool s_inPlace;
        private static string s_placingName;
        private static GameObject s_placed;

        private static bool s_looked;
        private static Func<GameObject, bool> s_apply;

        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece)), HarmonyPrefix]
        private static void BeforePlace(Player __instance, Piece piece)
        {
            s_placed = null;
            s_inPlace = __instance == Player.m_localPlayer && piece != null;
            s_placingName = s_inPlace ? piece.name : null;
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator)), HarmonyPostfix]
        private static void OnCreator(Piece __instance)
        {
            if (s_inPlace && s_placed == null && __instance != null) s_placed = __instance.gameObject;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece)), HarmonyFinalizer]
        private static void AfterPlace()
        {
            var placed = s_placed; var name = s_placingName;
            s_inPlace = false; s_placed = null; s_placingName = null;
            if (placed == null || name == null) return;
            bool injected;
            try { injected = VAVanillaPieceManager.WasInjectedByUs(name); } catch { injected = false; }
            if (!injected) return;
            try { SpawnerWhy(placed, name); } catch { }   // 1.1.350
            try { VerdantsAscent.Utilities.VAPlacedSpawnerBoxes.OnPlaced(placed); } catch { }   // 1.1.352
            try { VerdantsAscent.Utilities.VAEventZonePresets.OnPlaced(placed, name); } catch (Exception ex) { VaPieces.Log?.LogWarning($"[FAP] event zone preset on '{name}' failed: {ex.Message}"); }   // 1.1.351
            bool ih = false;
            try { ih = Apply(placed); }
            catch (Exception ex) { VaPieces.Log?.LogWarning($"[FAP] Infinity Hammer settings on '{name}' failed ({ex.Message}); placed as is."); }
            // hosted on ZNetScene so the line still comes when the piece breaks within the 2 s
            if (placed.GetComponentInChildren<Aoe>(true) != null && VaPieces.Log != null && ZNetScene.instance != null)
                ZNetScene.instance.StartCoroutine(Evidence(placed, name, ih));
        }

        // 1.1.350 (Fire 10-31 "Keep vanilla rule"): a placed spawner keeps vanilla's values, so name what will stop it spawning
        // (CreatureSpawner.UpdateSpawner: m_spawnInPlayerBase false inside a PlayerBase area, a required / blocking global key).
        private static void SpawnerWhy(GameObject placed, string name)
        {
            var cs = placed.GetComponent<CreatureSpawner>();
            var sa = placed.GetComponent<SpawnArea>();
            if (cs == null && sa == null) return;
            bool inBase = EffectArea.IsPointInsideArea(placed.transform.position, EffectArea.Type.PlayerBase) != null;
            string why;
            if (cs != null)
            {
                if (inBase && !cs.m_spawnInPlayerBase) why = "inside a player base: vanilla m_spawnInPlayerBase=false, it will not spawn here";
                else if (!string.IsNullOrEmpty(cs.m_requiredGlobalKey) && ZoneSystem.instance != null && !ZoneSystem.instance.GetGlobalKey(cs.m_requiredGlobalKey)) why = $"needs global key '{cs.m_requiredGlobalKey}' (vanilla), not set";
                else why = $"spawns {(cs.m_creaturePrefab != null ? cs.m_creaturePrefab.name : "?")} within {cs.m_triggerDistance:0} m (respawn {cs.m_respawnTimeMinuts:0} min, day {cs.m_spawnAtDay}, night {cs.m_spawnAtNight})";
            }
            else why = inBase ? "nest inside a player base (vanilla SpawnArea rules apply)" : $"nest: {sa.m_prefabs?.Count ?? 0} creature type(s), max near {sa.m_maxNear}";
            VaPieces.Log?.LogDebug($"[FAP] spawner {name} placed: {why}");
        }

        private static bool Apply(GameObject placed)
        {
            if (!s_looked)
            {
                s_looked = true;
                var t = AccessTools.TypeByName("FiresCore.Pieces.InfinityHammerPlacement");
                var m = t?.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                if (m != null) s_apply = (Func<GameObject, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, bool>), m);
            }
            return s_apply != null && s_apply(placed);
        }

        private static IEnumerator Evidence(GameObject placed, string name, bool ih)
        {
            yield return new WaitForSeconds(2f);
            if (placed == null) { VaPieces.Log?.LogDebug($"[FAP] placed {name}: GONE within 2 s (broke or was removed); IH ran {ih}"); yield break; }
            var wnt = placed.GetComponent<WearNTear>();
            string state = "no WearNTear";
            if (wnt != null)
            {
                string shown = wnt.m_broken != null && wnt.m_broken.activeSelf ? "broken" : wnt.m_worn != null && wnt.m_worn.activeSelf ? "worn" : "new";
                object support = null;
                try { support = AccessTools.Method(typeof(WearNTear), "GetSupport")?.Invoke(wnt, null); } catch { }
                state = $"health {wnt.GetHealthPercentage() * 100f:0}%, support {(support is float s ? s.ToString("0.0") : "?")}, shows '{shown}'";
            }
            VaPieces.Log?.LogDebug($"[FAP] placed {name}: {state}; IH ran {ih}");
        }

        // Ghost side: what the placement ray hits while an Aoe-carrying piece is selected (does a neighbour's trigger take the ray?)
        private static readonly AccessTools.FieldRef<Player, int> s_rayMask = AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        private static readonly AccessTools.FieldRef<Player, GameObject> s_ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly FieldInfo s_status = AccessTools.Field(typeof(Player), "m_placementStatus");
        private static string s_lastStatus;

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost"), HarmonyPostfix]
        private static void AfterGhost(Player __instance)
        {
            if (__instance != Player.m_localPlayer || VaPieces.Log == null) return;
            try
            {
                var ghost = s_ghost(__instance);
                if (ghost == null || ghost.GetComponentInChildren<Aoe>(true) == null) { s_lastStatus = null; return; }
                string status = s_status?.GetValue(__instance)?.ToString() ?? "?";
                if (status == s_lastStatus) return;
                s_lastStatus = status;
                string hit = "nothing";
                var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
                if (cam != null && Physics.Raycast(cam.position, cam.forward, out RaycastHit h, 50f, s_rayMask(__instance), QueryTriggerInteraction.Collide))
                    hit = $"'{h.collider.name}' on '{h.collider.transform.root.name}' (trigger {h.collider.isTrigger}, layer {LayerMask.LayerToName(h.collider.gameObject.layer)}, {h.distance:0.0} m)";
                VaPieces.Log.LogDebug($"[FAP] ghost {ghost.name.Replace("(Clone)", "")}: placement {status}; ray hits {hit}");
            }
            catch (Exception) { }
        }
    }
}
