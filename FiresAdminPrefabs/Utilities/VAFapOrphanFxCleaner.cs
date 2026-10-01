using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace VerdantsAscent.Utilities
{
    // Sweeps orphaned, NON-networked vanilla status-effect FX (wet, frost, etc.)
    // that get left behind in the world.
    //
    // Why these exist (vanilla EffectList.Create): a "follow" status effect is
    // Instantiated UNPARENTED at the scene root and tracks its character through a
    // ParentConstraint — it is never made a child of anything. When that character
    // is hard-destroyed on zone unload (or logout / death without a clean Stop),
    // the FX is a child of nothing, has no ZNetView for ZoneSystem to unload, and
    // no TimedDestruction (it's a continuous status effect). So it sits forever
    // with a now-dead constraint source. The normal FX clear brush walks
    // ZNetScene.m_instances (networked objects only), so it can't see these.
    //
    // Detection is false-positive-proof: a ParentConstraint whose sources are ALL
    // dead (sourceTransform == null) on a non-networked particle object. A LIVE
    // follow effect's constraint still points at a live character, so the player's
    // own active wet/frost FX is never a candidate.
    public static class VAFapOrphanFxCleaner
    {
        public static void Run(Vector3 center, float radius, bool destroy, Action<string> log)
        {
            float radiusSq = radius * radius;
            var particles = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            var seenRoots = new HashSet<GameObject>();
            var orphanRoots = new List<GameObject>();
            int suspiciousCount = 0;
            const int SuspiciousListCap = 20;
            var suspiciousLines = new List<string>();

            // Networked FX orphans (e.g. vfx_Wet) are NOT in ZNetScene.m_instances —
            // EffectList.Create instantiates them directly, so they never register
            // there. FindObjectsOfType is the only thing that sees them, so they're
            // handled inline below instead of via an m_instances sweep.
            var netOrphans = new List<GameObject>();
            var seenNet = new HashSet<GameObject>();

            foreach (var ps in particles)
            {
                if (ps == null) continue;
                GameObject root = ps.transform.root.gameObject;
                if (!seenRoots.Add(root)) continue;

                Vector3 pos = root.transform.position;
                float dx = pos.x - center.x;
                float dz = pos.z - center.z;
                if (dx * dx + dz * dz > radiusSq) continue;

                // An effect attached under a LIVE Character (a wet creature, the
                // player) dies with its owner — leave it.
                if (root.GetComponentInParent<Character>(true) != null) continue;

                // Networked particle object. We target FX prefabs (fx_/vfx_/sfx_)
                // that aren't a hammer-placed decoration — i.e. a stranded status/hit
                // effect like vfx_Wet that inherited the prefab's persistent flag.
                var znv = root.GetComponentInParent<ZNetView>(true);
                if (znv != null)
                {
                    GameObject netGo = znv.gameObject;
                    if (!IsFxName(netGo.name)) continue;                          // networked non-FX — leave it
                    var piece = netGo.GetComponent<Piece>();
                    if (piece != null && piece.IsPlacedByPlayer()) continue;     // your decoration — leave it
                    if (seenNet.Add(netGo))
                    {
                        netOrphans.Add(netGo);
                        log?.Invoke($"  [networked-orphan] '{CleanName(netGo.name)}' {Fmt(netGo.transform.position)} — FX ZNetView, persistent={znv.m_persistent}, not player-placed");
                    }
                    continue;
                }

                // Non-networked from here — the dead-constraint follow-FX case.
                var constraint = root.GetComponentInChildren<ParentConstraint>(true);
                if (constraint != null && !AllSourcesDead(constraint))
                    continue; // live follow effect (e.g. the player's own wet/frost) — leave it

                if (constraint != null)
                {
                    orphanRoots.Add(root);
                    log?.Invoke($"  [orphan] '{CleanName(root.name)}' {Fmt(pos)} — dead-constraint follow FX");
                }
                else
                {
                    suspiciousCount++;
                    if (suspiciousLines.Count < SuspiciousListCap)
                        suspiciousLines.Add($"  [suspicious] '{CleanName(root.name)}' {Fmt(pos)} — scene particle, no ZNetView/Character/constraint");
                }
            }

            log?.Invoke($"[FAP] Orphan-FX scan within {radius:F0}m: {orphanRoots.Count} dead-constraint, {netOrphans.Count} networked FX, {suspiciousCount} other non-networked particle object(s).");
            foreach (var line in suspiciousLines) log?.Invoke(line);
            if (suspiciousCount > suspiciousLines.Count)
                log?.Invoke($"  …and {suspiciousCount - suspiciousLines.Count} more suspicious item(s) not listed.");

            if (!destroy)
            {
                log?.Invoke("[FAP] Scan only — nothing destroyed. Run 'fap_clear_orphan_fx confirm' to remove the dead-constraint + networked-persistent orphans.");
                return;
            }

            int destroyed = 0;
            foreach (var go in orphanRoots)
            {
                if (go == null) continue;
                try { UnityEngine.Object.Destroy(go); destroyed++; }
                catch (Exception ex) { log?.Invoke($"  destroy failed on '{CleanName(go.name)}': {ex.Message}"); }
            }
            int netDestroyed = 0;
            foreach (var go in netOrphans)
            {
                if (go == null) continue;
                try { FiresCore.Net.NetworkObjectHelper.SafeDestroy(go); netDestroyed++; }
                catch (Exception ex) { log?.Invoke($"  net-destroy failed on '{CleanName(go.name)}': {ex.Message}"); }
            }
            log?.Invoke($"[FAP] Destroyed {destroyed} non-networked + {netDestroyed} networked orphan FX. Suspicious items were left alone.");
        }

        // Diagnostic: dump EVERY renderer-bearing GameObject near a point, with NO
        // filtering, so we can see what an FX actually is and why a targeted scan
        // missed it (wrong renderer type, ZNetView/Character ancestor, etc.). Uses
        // Renderer (the base of ParticleSystemRenderer / Line / Trail / Mesh /
        // SkinnedMesh) so nothing visible can hide from it.
        public static void Dump(Vector3 center, float radius, Action<string> log)
        {
            float radiusSq = radius * radius;
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var seen = new HashSet<GameObject>();
            var rows = new List<KeyValuePair<float, string>>();
            GameObject localPlayer = Player.m_localPlayer != null ? Player.m_localPlayer.gameObject : null;

            foreach (var r in renderers)
            {
                if (r == null) continue;
                GameObject go = r.gameObject;
                float d2 = (r.transform.position - center).sqrMagnitude;
                if (d2 > radiusSq) continue;
                if (!seen.Add(go)) continue;

                GameObject root = go.transform.root.gameObject;
                var znv = go.GetComponentInParent<ZNetView>(true);
                var chr = go.GetComponentInParent<Character>(true);
                var pc = go.GetComponentInParent<ParentConstraint>(true);
                string pcState = pc == null ? "no-constraint" : (AllSourcesDead(pc) ? "constraint:DEAD" : "constraint:live");
                bool isPlayer = localPlayer != null && root == localPlayer;
                float dist = Mathf.Sqrt(d2);

                rows.Add(new KeyValuePair<float, string>(dist,
                    $"  d={dist:F1} '{CleanName(go.name)}' [{r.GetType().Name}] root='{CleanName(root.name)}'"
                    + $" znv={(znv != null ? "Y" : "n")} char={(chr != null ? "Y" : "n")} {pcState}"
                    + $" layer={LayerMask.LayerToName(go.layer)}{(isPlayer ? " <YOU>" : "")}"));
            }

            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            log?.Invoke($"[FAP] fx_dump: {rows.Count} renderer object(s) within {radius:F0}m (closest first):");
            const int Cap = 60;
            for (int i = 0; i < rows.Count && i < Cap; i++) log?.Invoke(rows[i].Value);
            if (rows.Count > Cap) log?.Invoke($"  …and {rows.Count - Cap} more — lower the radius to narrow it.");
        }

        private static bool AllSourcesDead(ParentConstraint constraint)
        {
            int count = constraint.sourceCount;
            if (count == 0) return true;
            for (int i = 0; i < count; i++)
            {
                if (constraint.GetSource(i).sourceTransform != null) return false;
            }
            return true;
        }

        private static bool IsFxName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("vfx_", StringComparison.Ordinal)
                || name.StartsWith("fx_", StringComparison.Ordinal)
                || name.StartsWith("sfx_", StringComparison.Ordinal);
        }

        private static string CleanName(string name) =>
            string.IsNullOrEmpty(name) ? "?" : name.Replace("(Clone)", "");

        private static string Fmt(Vector3 v) => $"({v.x:F0},{v.y:F0},{v.z:F0})";
    }
}
