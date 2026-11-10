using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Animations;

namespace VerdantsAscent.Utilities
{
    // 1.1.400 (lead 11-07: Fire's phantom wet drops are on Verdant's Ascent DN, whose census at 14:52 held 0 status-FX ZDOs, so they
    // are local instances, not saved ZDOs). Debug only: follows every status-effect FX from the moment it exists to the moment it
    // should end, and names the first one that outlives its reason, with what it still holds.
    //   - StatusEffect.TriggerStartEffects (vanilla's only start-FX spawn): each instance, its character, its ZDO.
    //   - ZNetScene.CreateObject: a peer's replica of another machine's status FX ZDO (no ParentConstraint, no SE here).
    //   - StatusEffect.RemoveStartEffects: vanilla destroys an instance only while its ZNetView IsValid(); one that already lost
    //     its ZDO is skipped and stays in the scene. Each skip is logged.
    // Every 5 s: an instance alive 5 s after its character is gone, or a replica alive past ReplicaMaxSec, is logged ONCE as left
    // behind; its final destroy (if any) is logged too. A 60 s summary counts created / ended / left behind.
    [HarmonyPatch]
    internal static class VAStatusFxTrace
    {
        private sealed class Entry
        {
            public GameObject Go;
            public string Name;
            public float Born;
            public Character Owner;          // null for a replica
            public string OwnerName;
            public bool Replica;
            public float OwnerGoneAt = -1f;
            public bool Reported;
            public float ReportedAt;
        }

        private const int Cap = 512;
        private const float Grace = 5f;
        private const float ReplicaMaxSec = 600f;

        private static readonly AccessTools.FieldRef<StatusEffect, GameObject[]> StartInstances =
            AccessTools.FieldRefAccess<StatusEffect, GameObject[]>("m_startEffectInstances");

        private static readonly List<Entry> s_live = new List<Entry>();
        private static int s_created, s_replicas, s_ended, s_leftBehind, s_skipped;
        private static bool s_loop;

        private static void Track(GameObject go, Character owner, bool replica)
        {
            if (go == null) return;
            if (s_live.Count >= Cap)
            {
                int drop = s_live.FindIndex(e => !e.Reported);
                s_live.RemoveAt(drop >= 0 ? drop : 0);
            }
            s_live.Add(new Entry
            {
                Go = go, Name = Utils.GetPrefabName(go), Born = Time.time, Owner = owner,
                OwnerName = owner != null ? owner.m_name + " " + Utils.GetPrefabName(owner.gameObject) : "-", Replica = replica,
            });
            if (replica) s_replicas++; else s_created++;
            if (!s_loop && VaPieces.Instance != null) { s_loop = true; VaPieces.Instance.StartCoroutine(Loop()); }
        }

        [HarmonyPatch(typeof(StatusEffect), "TriggerStartEffects"), HarmonyPostfix]
        private static void TriggerStartEffects_Postfix(StatusEffect __instance)
        {
            try
            {
                var arr = StartInstances(__instance);
                if (arr == null) return;
                foreach (var go in arr)
                    if (go != null && go.GetComponent<ZNetView>() != null) Track(go, __instance.m_character, false);
            }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[StatusFx] trace start threw: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyPostfix]
        private static void CreateObject_Postfix(ZDO zdo, GameObject __result)
        {
            if (__result == null || zdo == null || zdo.IsOwner()) return;   // our own spawn is traced at TriggerStartEffects
            try { if (VAStatusFxOrphans.StatusFxPrefabs().ContainsKey(zdo.GetPrefab())) Track(__result, null, true); }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[StatusFx] trace replica threw: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(StatusEffect), "RemoveStartEffects"), HarmonyPrefix]
        private static void RemoveStartEffects_Prefix(StatusEffect __instance)
        {
            try
            {
                var arr = StartInstances(__instance);
                if (arr == null || ZNetScene.instance == null) return;
                foreach (var go in arr)
                {
                    if (go == null) continue;
                    var nv = go.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid()) continue;
                    s_skipped++;
                    VaPieces.Log?.LogDebug($"[StatusFx] RemoveStartEffects SKIPS {Utils.GetPrefabName(go)} #{go.GetInstanceID()} of '{__instance.m_name}' on "
                        + $"{(__instance.m_character != null ? Utils.GetPrefabName(__instance.m_character.gameObject) : "a gone character")} at {go.transform.position:F1}: "
                        + $"ZNetView {(nv == null ? "missing" : "has no ZDO")}, so vanilla leaves it in the scene (1.1.400).");
                }
            }
            catch (Exception ex) { VaPieces.Log?.LogDebug($"[StatusFx] trace remove threw: {ex.Message}"); }
        }

        private static IEnumerator Loop()
        {
            float nextSummary = Time.time + 60f;
            var wait = new WaitForSeconds(5f);
            while (true)
            {
                yield return wait;
                try { Check(); } catch (Exception ex) { VaPieces.Log?.LogDebug($"[StatusFx] trace check threw: {ex.Message}"); }
                if (Time.time >= nextSummary)
                {
                    nextSummary = Time.time + 60f;
                    if (s_created + s_replicas + s_ended + s_skipped > 0)
                        VaPieces.Log?.LogDebug($"[StatusFx] trace last 60 s: {s_created} started here, {s_replicas} replica(s) of other peers', {s_ended} ended, "
                            + $"{s_skipped} skipped by RemoveStartEffects, {s_leftBehind} left behind so far, {s_live.Count} followed now (1.1.400).");
                    s_created = s_replicas = s_ended = s_skipped = 0;
                }
            }
        }

        private static void Check()
        {
            float now = Time.time;
            for (int i = s_live.Count - 1; i >= 0; i--)
            {
                var e = s_live[i];
                if (e.Go == null)
                {
                    if (e.Reported) VaPieces.Log?.LogDebug($"[StatusFx] left-behind {e.Name} (of {e.OwnerName}) destroyed after all, {now - e.ReportedAt:0} s after it was reported (1.1.400).");
                    else s_ended++;
                    s_live.RemoveAt(i);
                    continue;
                }
                if (e.Reported) continue;
                string why = null;
                if (e.Replica)
                {
                    if (now - e.Born > ReplicaMaxSec) why = $"a replica alive {now - e.Born:0} s";
                }
                else
                {
                    bool ownerAlive = e.Owner != null && e.Owner.gameObject.activeInHierarchy && !e.Owner.IsDead();
                    if (ownerAlive) e.OwnerGoneAt = -1f;
                    else if (e.OwnerGoneAt < 0f) e.OwnerGoneAt = now;
                    else if (now - e.OwnerGoneAt >= Grace) why = $"its character {(e.Owner == null ? "is destroyed" : e.Owner.IsDead() ? "is dead" : "is inactive")} for {now - e.OwnerGoneAt:0} s";
                }
                if (why == null) continue;
                e.Reported = true; e.ReportedAt = now; s_leftBehind++;
                VaPieces.Log?.LogDebug($"[StatusFx] LEFT BEHIND {e.Name} #{e.Go.GetInstanceID()} of {e.OwnerName}: {why}. {Describe(e.Go)} (1.1.400)");
            }
        }

        private static string Describe(GameObject go)
        {
            var nv = go.GetComponent<ZNetView>();
            string zdoText = "no ZNetView";
            if (nv != null)
            {
                var zdo = nv.IsValid() ? nv.GetZDO() : null;
                if (zdo == null) zdoText = "ZNetView with no ZDO (local only, no save, no other peer sees it)";
                else
                {
                    long owner = zdo.GetOwner();
                    bool mine = owner == ZDOMan.GetSessionID();
                    bool connected = mine || (ZNet.instance != null && ZNet.instance.GetPeer(owner) != null);
                    zdoText = $"ZDO {zdo.m_uid} persistent {zdo.Persistent}, owner {(mine ? "this machine" : owner.ToString())}{(connected ? "" : " (not connected)")}, "
                        + $"ZDO at {zdo.GetPosition():F1}";
                }
            }
            var pc = go.GetComponent<ParentConstraint>();
            string pcText = pc == null ? "no ParentConstraint" : $"ParentConstraint active {pc.constraintActive}, source {(pc.sourceCount > 0 && pc.GetSource(0).sourceTransform != null ? pc.GetSource(0).sourceTransform.name : "gone")}";
            string parent = go.transform.parent != null ? go.transform.parent.name : "no parent";
            return $"At {go.transform.position:F1}, {zdoText}; {pcText}; {parent}; active {go.activeInHierarchy}; server {(ZNet.instance != null && ZNet.instance.IsServer())}.";
        }
    }
}
