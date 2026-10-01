using System.Globalization;
using UnityEngine;
using VerdantsAscent.Managers;
using VerdantsAscent.Patches;

namespace VerdantsAscent.Utilities
{
    // Turns a monster stand-in placement click into a real creature
    // instantiation. Stamps the queued config preset onto the new ZDO
    // before remote replication so peers apply the override on first load,
    // and drops a persistent anchor (when the preset declares a respawn
    // delay) so the respawn cycle survives restarts.
    public static class VAMonsterSpawner
    {
        private const float GroupSpreadRadius = 1.5f;

        public static int SpawnAt(string creaturePrefabName, Vector3 pos, Quaternion rot, Player player)
        {
            if (string.IsNullOrEmpty(creaturePrefabName)) return 0;
            var scene = ZNetScene.instance;
            if (scene == null) return 0;
            var prefab = scene.GetPrefab(creaturePrefabName);
            if (prefab == null)
            {
                if (player != null)
                {
                    player.Message(MessageHud.MessageType.Center,
                        $"Monster prefab '{creaturePrefabName}' not in ZNetScene");
                }
                return 0;
            }

            Debug.Log($"{VaPieces.PluginName}: [MonsterSpawn] SpawnAt ENTER creature='{creaturePrefabName}' pos={pos} — registering destroy-watch.");
            VerdantsAscent.Patches.VAFapDestroyWatchPatch.WatchName(creaturePrefabName);

            int groupSize = VAMonsterConfigQueue.ResolveSpawnCount(creaturePrefabName);
            if (groupSize < 1) groupSize = 1;

            int spawned = 0;
            using (VAUndoBridge.Scope())
            {
                for (int i = 0; i < groupSize; i++)
                {
                    Vector3 spawnPos = pos;
                    if (groupSize > 1)
                    {
                        var ring = Random.insideUnitCircle * GroupSpreadRadius;
                        spawnPos.x += ring.x;
                        spawnPos.z += ring.y;
                    }

                    var instance = UnityEngine.Object.Instantiate(prefab, spawnPos, rot);
                    spawned++;
                    Debug.Log($"{VaPieces.PluginName}: [MonsterSpawn] Instantiated '{creaturePrefabName}' #{spawned} id={instance.GetInstanceID()} active={instance.activeInHierarchy} at {spawnPos}");

                    string presetForAnchor = null;
                    VAMonsterConfigQueue.TryGetQueuedPresetName(creaturePrefabName, out presetForAnchor);

                    VAMonsterConfigQueue.ApplyToSpawnedInstance(creaturePrefabName, instance);

                    if (!string.IsNullOrEmpty(presetForAnchor)
                        && VAMonsterPresetStore.TryGet(presetForAnchor, out var preset))
                    {
                        float respawn = ParseFloat(preset.TryGetFloatStr("va_respawn_seconds"), 0f);
                        if (respawn > 0f)
                            VAMonsterAnchor.SpawnAnchorUnder(spawnPos, presetForAnchor);
                    }
                }
            }

            if (ConfigManager.Instance.configVerboseLogging.Value)
                Debug.Log($"{VaPieces.PluginName}: VAMonsterSpawner spawned {spawned}× '{creaturePrefabName}' at {pos}.");

            if (spawned > 0)
                VAMonsterEwpRuleWriter.RegenerateRules();

            return spawned;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
