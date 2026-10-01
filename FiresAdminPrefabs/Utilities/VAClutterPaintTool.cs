using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

using FiresCore.Net;

namespace VerdantsAscent.Utilities
{
    // VAClutterPaintTool — radius-brush clutter painter (mirrors
    // FiresRPGmaker's ClutterPaintTool.SpawnInstancedClutter).
    //
    //   * Instanced (m_instanced=true)  — ONE Instantiate at brush center →
    //     N AddInstance on its shared InstanceRenderer (one batched draw).
    //   * Non-instanced (m_instanced=false) — N Instantiates at sample positions.
    //
    // Persistence is the TerrainComp ZDO byte-array (LoadEntries/SaveEntries);
    // runtime clones are ephemeral, m_forceDisableInit-suppressed so they never
    // create competing ZDOs. TerrainComp.Load postfix → RestoreFromZDO rebuilds
    // the FAP_PaintedClutter_* children from the byte-array (idempotent).
    //
    // Multiplayer: only the ZDO owner writes the array. Paint() writes locally
    // (single-player + host); OnRpcPaintClutter is wired but unused, left for a
    // future client→owner dispatch.
    public static class VAClutterPaintTool
    {
        // ZDO key for our clutter-paint array. Matches what we read in the
        // TerrainComp.Load postfix on the same client or any peer that
        // streams the zone in.
        private const string ZDO_KEY_RAW = "fap_clutter_paints";
        private static readonly int ZDO_KEY = ZDO_KEY_RAW.GetStableHashCode();

        // Serialized-layout version. Bump on layout change so old clients
        // don't deserialize garbage from a newer save.
        //   v1 — stroke-level: (X, Y, Z, Radius, Density, ClutterType); per-stroke clear.
        //   v2 — per-sample:   (X, Y, Z, Scale, RotY, ClutterType); per-sample clear.
        private const int DATA_VERSION = 2;

        // Set during TerrainComp.Load restoration so any code path the
        // restore touches knows not to re-record (we're replaying, not
        // creating new entries).
        public static bool IsRestoring { get; private set; }

        // RPC registered on every TerrainComp's ZNetView (Awake postfix below),
        // mirroring vanilla's "ApplyOperation". Not dispatched from Paint()
        // today — kept wired for a future client→owner route.
        private const string RPC_NAME = "FAP_PaintClutter";

        // Brush-scale multiplier (shift+scroll on the clutter standin).
        // Multiplies the per-sample [scaleMin, scaleMax] range before each
        // random pick and is persisted into each ClutterEntry's scale.
        // Session-only state (resets on world unload).
        public const float MinBrushScale = 0.1f;
        public const float MaxBrushScale = 10f;
        public static float BrushScale = 1f;

        private const float MinDensity = 0.05f;
        private const float DefaultScaleMin = 0.8f;
        private const float DefaultScaleMax = 1.2f;
        private const float DefaultMaxTilt = 30f;
        private const float MinLodMaxDistance = 80f;
        private const float InstancedSpacingBase = 0.5f;
        private const float NonInstancedSpacingBase = 0.6f;
        private const float BrushDiameterFactor = 2f;
        private const float FullRotationDegrees = 360f;
        private const float GridJitterFraction = 0.3f;
        private const float SampleRayStartHeight = 100f;
        private const float SampleRayMaxDistance = 200f;

        private const float V1SeedPrimeX = 7919f;
        private const float V1SeedPrimeZ = 6577f;

        // First-paint-per-prefab dedup for the [PaintDiag] dump in
        // SpawnInstanced — diagnoses "AddInstance succeeds but invisible"
        // without requiring verbose logging.
        private static readonly HashSet<string> _diagnosedSpawnedPrefabIds =
            new HashSet<string>(StringComparer.Ordinal);

        // ────────────────────────────────────────────────────────────────
        // Public API
        // ────────────────────────────────────────────────────────────────

        // Paints clutter at `center`: finds/creates the TerrainComp, spawns the
        // visuals, and appends entries to the persistence byte-array. Returns
        // the spawned count (0 = nothing placed, no comp, or invalid input).
        public static int Paint(GameObject prefab, Vector3 center, float radius, float density = 1f)
        {
            // Bracket the whole brush-stroke into ONE WorldEditCommands undo
            // record (every ZNetView.Awake in this call appends to the same
            // UndoAction). No-op when WEC isn't installed.
            using (VAUndoBridge.Scope())
            {
                return PaintInner(prefab, center, radius, density);
            }
        }

        private static int PaintInner(GameObject prefab, Vector3 center, float radius, float density)
        {
            if (prefab == null || radius <= 0f) return 0;
            if (density < MinDensity) density = MinDensity;

            var terrainComp = GetOrCreateTerrainCompAt(center);
            var data = ResolveClutterData(prefab);
            bool isInstanced = data != null
                ? data.m_instanced
                : prefab.GetComponentInChildren<InstanceRenderer>(includeInactive: true) != null;

            float scaleMin = data != null ? data.m_scaleMin : DefaultScaleMin;
            float scaleMax = data != null ? data.m_scaleMax : DefaultScaleMax;
            float maxTilt = data != null ? data.m_maxTilt : DefaultMaxTilt;

            float brushScale = Mathf.Clamp(BrushScale, MinBrushScale, MaxBrushScale);
            scaleMin *= brushScale;
            scaleMax *= brushScale;

            // One byte-array entry per surviving sample so the clear brush
            // can act at sample granularity.
            var samples = new List<ClutterEntry>();
            if (isInstanced)
            {
                SpawnInstanced(prefab, center, radius, density, scaleMin, scaleMax, maxTilt,
                               terrainComp != null ? terrainComp.transform : null, samples);
            }
            else
            {
                SpawnNonInstanced(prefab, center, radius, density, scaleMin, scaleMax, maxTilt,
                                   terrainComp != null ? terrainComp.transform : null, samples);
            }
            int spawned = samples.Count;

            bool persisted = false;
            if (terrainComp != null && spawned > 0)
            {
                var entries = LoadEntries(terrainComp);
                entries.AddRange(samples);
                SaveEntries(terrainComp, entries);
                persisted = true;
            }

            if (ConfigManager.Instance.configVerboseLogging.Value)
            {
                string mode = isInstanced ? "instanced (batched)" : "non-instanced (per-sample)";
                Debug.Log($"{VaPieces.PluginName}: Painted {spawned} {mode} sample(s) of '{prefab.name}' at {center} (persisted={persisted}).");
            }

            return spawned;
        }

        // Called from the TerrainComp.Load postfix (zone stream-in or ZDO
        // revision bump). Idempotent: destroys existing FAP_PaintedClutter_*
        // children, then re-spawns the full entry set from the ZDO — no
        // in-memory tracking cache needed.
        public static void RestoreFromZDO(TerrainComp tc)
        {
            if (tc == null) return;

            // Destroy prior-spawn FAP children (naming-prefix match — vanilla
            // / other-mod content parented here is untouched).
            int parentChildCount = tc.transform.childCount;
            for (int i = parentChildCount - 1; i >= 0; i--)
            {
                var child = tc.transform.GetChild(i);
                if (child == null) continue;
                if (!child.name.StartsWith("FAP_PaintedClutter_", StringComparison.Ordinal))
                    continue;
                NetworkObjectHelper.SafeDestroy(child.gameObject);
            }

            var entries = LoadEntries(tc);
            if (entries.Count == 0) return;

            IsRestoring = true;
            try
            {
                int restored = ReplayEntriesByType(entries, tc.transform);
                if (restored > 0 && ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.Log($"{VaPieces.PluginName}: VAClutterPaintTool.Restore — replayed {restored} clutter sample(s) at TerrainComp {tc.transform.position}.");
            }
            finally
            {
                IsRestoring = false;
            }
        }

        // Groups per-sample entries by ClutterType and spawns ONE container
        // per type (for InstanceRenderer batching). Containers/clones are
        // parented to the TerrainComp so zone unload destroys them; clones
        // are ZDO-suppressed (byte array is the persistence source).
        private static int ReplayEntriesByType(List<ClutterEntry> entries, Transform parent)
        {
            if (entries == null || entries.Count == 0) return 0;

            var byType = new Dictionary<string, List<ClutterEntry>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.ClutterType)) continue;
                if (!byType.TryGetValue(entry.ClutterType, out var groupList))
                {
                    groupList = new List<ClutterEntry>();
                    byType[entry.ClutterType] = groupList;
                }
                groupList.Add(entry);
            }

            int total = 0;
            foreach (var typeGroup in byType)
            {
                var prefab = ResolvePrefab(typeGroup.Key);
                if (prefab == null)
                {
                    if (ConfigManager.Instance.configVerboseLogging.Value)
                        Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPaintTool.Replay — prefab '{typeGroup.Key}' not found, skipping {typeGroup.Value.Count} sample(s).");
                    continue;
                }

                var data = ResolveClutterData(prefab);
                bool isInstanced = data != null
                    ? data.m_instanced
                    : prefab.GetComponentInChildren<InstanceRenderer>(includeInactive: true) != null;

                if (isInstanced)
                    total += ReplayInstancedGroup(prefab, typeGroup.Value, parent);
                else
                    total += ReplayNonInstancedGroup(prefab, typeGroup.Value, parent);
            }
            return total;
        }

        private static int ReplayInstancedGroup(GameObject prefab, List<ClutterEntry> samples, Transform parent)
        {
            if (samples.Count == 0) return 0;

            bool prevForceDisableInit = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            GameObject container;
            try
            {
                // Container position is cosmetic — InstanceRenderer uses the
                // absolute world positions from AddInstance.
                var origin = new Vector3(samples[0].X, samples[0].Y, samples[0].Z);
                container = UnityEngine.Object.Instantiate(prefab, origin, Quaternion.identity);
            }
            finally
            {
                ZNetView.m_forceDisableInit = prevForceDisableInit;
            }
            container.name = $"FAP_PaintedClutter_{prefab.name}_{Time.frameCount}";
            container.SetActive(true);
            if (parent != null)
                container.transform.SetParent(parent, worldPositionStays: true);

            var ir = container.GetComponentInChildren<InstanceRenderer>(includeInactive: true);
            if (ir == null)
            {
                NetworkObjectHelper.SafeDestroy(container);
                return 0;
            }

            // Same activation-walk as SpawnInstanced: a bundle IR shipped on
            // an inactive child silently swallows AddInstance (nothing renders).
            for (Transform ancestor = ir.transform; ancestor != null && ancestor != container.transform.parent; ancestor = ancestor.parent)
            {
                if (!ancestor.gameObject.activeSelf) ancestor.gameObject.SetActive(true);
            }

            ir.Clear();
            ir.m_lodMinDistance = 0f;
            if (ir.m_lodMaxDistance < MinLodMaxDistance) ir.m_lodMaxDistance = MinLodMaxDistance;

            if (ir.m_mesh == null)
                Debug.LogWarning($"{VaPieces.PluginName}: ReplayInstancedGroup — '{prefab.name}' InstanceRenderer has NULL m_mesh; instances will be invisible.");
            if (ir.m_material == null)
                Debug.LogWarning($"{VaPieces.PluginName}: ReplayInstancedGroup — '{prefab.name}' InstanceRenderer has NULL m_material; instances will be invisible.");
            else if (!ir.m_material.enableInstancing)
                ir.m_material.enableInstancing = true;

            int added = 0;
            foreach (var entry in samples)
            {
                var pos = new Vector3(entry.X, entry.Y, entry.Z);
                var rot = Quaternion.Euler(0f, entry.RotY, 0f);
                ir.AddInstance(pos, rot, entry.Scale);
                added++;
            }
            if (added == 0) NetworkObjectHelper.SafeDestroy(container);
            return added;
        }

        private static int ReplayNonInstancedGroup(GameObject prefab, List<ClutterEntry> samples, Transform parent)
        {
            int spawned = 0;
            bool prevForceDisableInit = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            try
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    var entry = samples[i];
                    var pos = new Vector3(entry.X, entry.Y, entry.Z);
                    var rot = Quaternion.Euler(0f, entry.RotY, 0f);
                    var clone = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    clone.name = $"FAP_PaintedClutter_{prefab.name}_{spawned}";
                    clone.transform.localScale = Vector3.one * entry.Scale;
                    if (parent != null)
                        clone.transform.SetParent(parent, worldPositionStays: true);
                    spawned++;
                }
            }
            finally
            {
                ZNetView.m_forceDisableInit = prevForceDisableInit;
            }
            return spawned;
        }

        // ────────────────────────────────────────────────────────────────
        // RPC handler (registered, unused by Paint() today)
        // ────────────────────────────────────────────────────────────────

        // Per-sample RPC payload (v2 schema): one entry per fire, packing
        // (X, Y, Z, Scale, RotY, ClutterType). Dead code today — Paint()
        // writes locally on the owner; kept for a future client→owner dispatch.
        private static void OnRpcPaintClutter(TerrainComp tc, long sender, ZPackage pkg)
        {
            if (tc == null) return;
            var nview = tc.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            if (!nview.IsOwner()) return;

            float x = pkg.ReadSingle();
            float y = pkg.ReadSingle();
            float z = pkg.ReadSingle();
            float scale = pkg.ReadSingle();
            float rotY = pkg.ReadSingle();
            string type = pkg.ReadString();

            var entries = LoadEntries(tc);
            entries.Add(new ClutterEntry
            {
                X = x, Y = y, Z = z, Scale = scale, RotY = rotY, ClutterType = type,
            });
            SaveEntries(tc, entries);
        }

        // ────────────────────────────────────────────────────────────────
        // TerrainComp acquisition
        // ────────────────────────────────────────────────────────────────

        // Returns the TerrainComp at `pos`, creating one via vanilla's
        // Heightmap.GetAndCreateTerrainCompiler if none exists. Same path
        // the hoe's terrain ops follow on first edit.
        private static TerrainComp GetOrCreateTerrainCompAt(Vector3 pos)
        {
            var existing = TerrainComp.FindTerrainCompiler(pos);
            if (existing != null) return existing;

            var hmap = Heightmap.FindHeightmap(pos);
            if (hmap == null) return null;

            return hmap.GetAndCreateTerrainCompiler();
        }

        // ────────────────────────────────────────────────────────────────
        // Prefab resolution (for restore-from-ZDO)
        // ────────────────────────────────────────────────────────────────

        // Resolves a prefab name: VAClutterRegistry first (clutter exposed via
        // ClutterSystem.m_clutter), then ZNetScene.GetPrefab for bundle prefabs
        // that landed in m_namedPrefabs but not m_clutter.
        private static GameObject ResolvePrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var fromRegistry = VAClutterRegistry.ResolvePrefab(name);
            if (fromRegistry != null) return fromRegistry;

            var scene = ZNetScene.instance;
            if (scene != null)
            {
                var fromScene = scene.GetPrefab(name);
                if (fromScene != null) return fromScene;
            }
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // Spawn helpers
        // ────────────────────────────────────────────────────────────────

        // ONE Instantiate at brush center → N AddInstance calls on the
        // container's shared InstanceRenderer (mirrors RPGmaker's
        // SpawnInstancedClutter). The container is purely runtime; its
        // ZNetView is suppressed via m_forceDisableInit so no ZDO is created.
        // The TerrainComp's own ZDO byte-array is the authoritative persistence.
        private static int SpawnInstanced(GameObject prefab, Vector3 center, float radius,
                                          float density, float scaleMin, float scaleMax, float maxTilt,
                                          Transform parentTransform, List<ClutterEntry> sampleSink)
        {
            // m_forceDisableInit makes ZNetView.Awake on the clone a no-op so
            // the runtime container doesn't compete with the byte-array (else:
            // "Double ZNetview" cascades + stale-ZDO accumulation). Save/restore
            // the static so we don't leak the suppression to other Instantiates.
            bool prevForceDisableInit = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            GameObject container;
            try
            {
                container = UnityEngine.Object.Instantiate(prefab, center, Quaternion.identity);
            }
            finally
            {
                ZNetView.m_forceDisableInit = prevForceDisableInit;
            }
            container.name = $"FAP_PaintedClutter_{prefab.name}_{Time.frameCount}";
            container.SetActive(true);
            if (parentTransform != null)
                container.transform.SetParent(parentTransform, worldPositionStays: true);

            var ir = container.GetComponentInChildren<InstanceRenderer>(includeInactive: true);
            if (ir == null)
            {
                NetworkObjectHelper.SafeDestroy(container);
                return 0;
            }

            // Activate every ancestor up to the IR's node. A bundle IR baked
            // inactive at export clones disabled: AddInstance succeeds but
            // OnEnable never fires, so it never joins the static Instances
            // list and renders nothing while the byte-array persists fine.
            for (Transform ancestor = ir.transform; ancestor != null && ancestor != container.transform.parent; ancestor = ancestor.parent)
            {
                if (!ancestor.gameObject.activeSelf) ancestor.gameObject.SetActive(true);
            }

            ir.Clear();
            ir.m_lodMinDistance = 0f;
            if (ir.m_lodMaxDistance < MinLodMaxDistance) ir.m_lodMaxDistance = MinLodMaxDistance;

            // Surface renderability blockers that produce "AddInstance succeeds
            // but nothing renders". Logged unconditionally — the symptom is
            // otherwise silent (no Unity warnings when these trip).
            if (ir.m_mesh == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: SpawnInstanced — '{prefab.name}' InstanceRenderer has NULL m_mesh; instances will be invisible. Bundle export likely dropped the mesh ref.");
            }
            if (ir.m_material == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: SpawnInstanced — '{prefab.name}' InstanceRenderer has NULL m_material; instances will be invisible.");
            }
            else if (!ir.m_material.enableInstancing)
            {
                // Graphics.DrawMeshInstanced silently skips materials with this
                // off — same invisible symptom as the inactive-child case.
                ir.m_material.enableInstancing = true;
            }

            // First-paint-per-prefab diagnostic dump (always-on; one log per
            // prefab per session). Covers every IR field that can silently
            // zero-volume the draw when "AddInstance succeeds but invisible".
            if (prefab != null && _diagnosedSpawnedPrefabIds.Add(prefab.name))
            {
                string meshName = ir.m_mesh != null ? ir.m_mesh.name : "<null>";
                int meshTris = ir.m_mesh != null ? ir.m_mesh.triangles.Length / 3 : 0;
                int meshVerts = ir.m_mesh != null ? ir.m_mesh.vertexCount : 0;
                var meshBounds = ir.m_mesh != null ? ir.m_mesh.bounds : new Bounds();
                string matName = ir.m_material != null ? ir.m_material.name : "<null>";
                string shaderName = ir.m_material?.shader != null ? ir.m_material.shader.name : "<null>";
                bool instOn = ir.m_material != null && ir.m_material.enableInstancing;
                bool contActive = container.activeInHierarchy;
                bool irActive = ir.gameObject.activeInHierarchy;
                bool irEnabled = ir.enabled;
                bool inIrInstances = InstanceRenderer.Instances.Contains(ir);
                int irLayer = ir.gameObject.layer;
                string irLayerName = LayerMask.LayerToName(irLayer);
                Debug.Log($"{VaPieces.PluginName}: [PaintDiag] '{prefab.name}' — container.activeInHierarchy={contActive}, ir.gameObject.activeInHierarchy={irActive}, ir.enabled={irEnabled}, ir.inInstancesList={inIrInstances}, ir.layer={irLayer}('{irLayerName}'), m_scale={ir.m_scale}, mesh='{meshName}' tris={meshTris} verts={meshVerts} bounds={meshBounds.size}, material='{matName}' shader='{shaderName}' enableInstancing={instOn}, useLod={ir.m_useLod} lod=[{ir.m_lodMinDistance}..{ir.m_lodMaxDistance}], frustumCull={ir.m_frustumCull}, shadowCasting={ir.m_shadowCasting}.");
            }

            float spacing = InstancedSpacingBase / density;
            int gridSize = Mathf.Max(1, Mathf.RoundToInt(radius * BrushDiameterFactor / spacing));
            int terrainMask = LayerMask.GetMask("terrain", "Default");

            int spawned = 0;
            for (int x = 0; x < gridSize; x++)
            {
                for (int z = 0; z < gridSize; z++)
                {
                    if (!TrySampleGrid(center, x, z, gridSize, spacing, radius,
                                       terrainMask, maxTilt, out Vector3 spawnPos)) continue;

                    float rotY = UnityEngine.Random.value * FullRotationDegrees;
                    Quaternion rot = Quaternion.Euler(0f, rotY, 0f);
                    float scale = UnityEngine.Random.Range(scaleMin, scaleMax);
                    ir.AddInstance(spawnPos, rot, scale);
                    spawned++;

                    // Per-sample entry so clearing targets this exact position,
                    // not the whole brush stroke.
                    if (sampleSink != null)
                    {
                        sampleSink.Add(new ClutterEntry
                        {
                            X = spawnPos.x, Y = spawnPos.y, Z = spawnPos.z,
                            Scale = scale, RotY = rotY,
                            ClutterType = prefab.name,
                        });
                    }
                }
            }
            if (spawned == 0) NetworkObjectHelper.SafeDestroy(container);
            return spawned;
        }

        // N Instantiates of the clutter prefab at sample positions (the
        // m_instanced=false path — bundle prefabs, a few vanilla entries).
        // Same m_forceDisableInit suppression as SpawnInstanced.
        private static int SpawnNonInstanced(GameObject prefab, Vector3 center, float radius,
                                             float density, float scaleMin, float scaleMax, float maxTilt,
                                             Transform parentTransform, List<ClutterEntry> sampleSink)
        {
            float spacing = NonInstancedSpacingBase / density;
            int gridSize = Mathf.Max(1, Mathf.RoundToInt(radius * BrushDiameterFactor / spacing));
            int terrainMask = LayerMask.GetMask("terrain", "Default");
            int spawned = 0;

            bool prevForceDisableInit = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            try
            {
                for (int x = 0; x < gridSize; x++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        if (!TrySampleGrid(center, x, z, gridSize, spacing, radius,
                                           terrainMask, maxTilt, out Vector3 spawnPos)) continue;

                        float rotY = UnityEngine.Random.value * FullRotationDegrees;
                        Quaternion rot = Quaternion.Euler(0f, rotY, 0f);
                        float scale = UnityEngine.Random.Range(scaleMin, scaleMax);
                        var clone = UnityEngine.Object.Instantiate(prefab, spawnPos, rot);
                        clone.name = $"FAP_PaintedClutter_{prefab.name}_{spawned}";
                        clone.transform.localScale = Vector3.one * scale;
                        if (parentTransform != null)
                            clone.transform.SetParent(parentTransform, worldPositionStays: true);
                        spawned++;

                        if (sampleSink != null)
                        {
                            sampleSink.Add(new ClutterEntry
                            {
                                X = spawnPos.x, Y = spawnPos.y, Z = spawnPos.z,
                                Scale = scale, RotY = rotY,
                                ClutterType = prefab.name,
                            });
                        }
                    }
                }
            }
            finally
            {
                ZNetView.m_forceDisableInit = prevForceDisableInit;
            }
            return spawned;
        }

        private static bool TrySampleGrid(Vector3 center, int gx, int gz, int gridSize, float spacing,
                                          float radius, int terrainMask, float maxTilt, out Vector3 spawnPos)
        {
            spawnPos = Vector3.zero;

            float offsetX = (gx - gridSize * 0.5f) * spacing
                          + UnityEngine.Random.Range(-spacing * GridJitterFraction, spacing * GridJitterFraction);
            float offsetZ = (gz - gridSize * 0.5f) * spacing
                          + UnityEngine.Random.Range(-spacing * GridJitterFraction, spacing * GridJitterFraction);

            float distSq = offsetX * offsetX + offsetZ * offsetZ;
            if (distSq > radius * radius) return false;

            Vector3 samplePos = center + new Vector3(offsetX, 0f, offsetZ);
            Vector3 rayStart = samplePos + new Vector3(0f, SampleRayStartHeight, 0f);
            if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, SampleRayMaxDistance, terrainMask))
                return false;

            float tilt = Vector3.Angle(hit.normal, Vector3.up);
            if (tilt > maxTilt) return false;

            spawnPos = hit.point;
            return true;
        }

        private static ClutterSystem.Clutter ResolveClutterData(GameObject prefab)
        {
            if (prefab == null) return null;
            if (VAClutterRegistry.TryGet(prefab.name, out var data)) return data;
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // ZDO serialization
        // ────────────────────────────────────────────────────────────────

        // Compressed ZPackage. Schema v2:
        //   int version (= 2)
        //   int count
        //   { float X, float Y, float Z, float Scale, float RotY, string ClutterType } × count
        //
        // Each entry IS one rendered sample (not a brush stroke). Replay
        // groups samples by ClutterType for one InstanceRenderer container
        // per type per zone.
        private struct ClutterEntry
        {
            public float X;
            public float Y;
            public float Z;
            public float Scale;
            public float RotY;
            public string ClutterType;
        }

        private static List<ClutterEntry> LoadEntries(TerrainComp tc)
        {
            var entries = new List<ClutterEntry>();
            try
            {
                var nview = tc.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return entries;
                var zdo = nview.GetZDO();
                if (zdo == null) return entries;

                byte[] data = zdo.GetByteArray(ZDO_KEY);
                if (data == null || data.Length == 0) return entries;

                byte[] decompressed = Utils.Decompress(data);
                var pkg = new ZPackage(decompressed);

                int version = pkg.ReadInt();
                if (version == 2)
                {
                    int count = pkg.ReadInt();
                    for (int i = 0; i < count; i++)
                    {
                        entries.Add(new ClutterEntry
                        {
                            X = pkg.ReadSingle(),
                            Y = pkg.ReadSingle(),
                            Z = pkg.ReadSingle(),
                            Scale = pkg.ReadSingle(),
                            RotY = pkg.ReadSingle(),
                            ClutterType = pkg.ReadString(),
                        });
                    }
                }
                else if (version == 1)
                {
                    // Legacy stroke-level entries: expand each into deterministic
                    // grid samples (old paints survive, approximate). Next
                    // SaveEntries writes v2 — one-time migration.
                    int count = pkg.ReadInt();
                    for (int i = 0; i < count; i++)
                    {
                        float x = pkg.ReadSingle();
                        float y = pkg.ReadSingle();
                        float z = pkg.ReadSingle();
                        float radius  = pkg.ReadSingle();
                        float density = pkg.ReadSingle();
                        string type   = pkg.ReadString();
                        ExpandV1StrokeIntoSamples(entries,
                            new Vector3(x, y, z), radius, density, type);
                    }
                    if (entries.Count > 0)
                    {
                        Debug.Log($"{VaPieces.PluginName}: VAClutterPaintTool.LoadEntries migrated v1 stroke-level entries to {entries.Count} v2 per-sample entries on TerrainComp {tc.transform.position}.");
                    }
                }
                else
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPaintTool.LoadEntries — unknown schema version {version}, ignoring.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPaintTool.LoadEntries failed: {ex.Message}");
            }
            return entries;
        }

        // Migrates one v1 stroke-level entry into v2 per-sample entries by
        // re-running deterministic grid sampling within the stroke's radius.
        // Seeded from the stroke center + type so positions are stable across
        // reloads; uses the original Y directly (no raycast).
        private static void ExpandV1StrokeIntoSamples(
            List<ClutterEntry> outEntries, Vector3 center, float radius, float density, string clutterType)
        {
            if (string.IsNullOrEmpty(clutterType) || radius <= 0f || density <= 0f) return;

            int seed = unchecked(
                (int)(center.x * V1SeedPrimeX) ^ (int)(center.z * V1SeedPrimeZ) ^ (clutterType.GetHashCode()));
            var rng = new System.Random(seed);

            float spacing = InstancedSpacingBase / density;
            int gridSize = Mathf.Max(1, Mathf.RoundToInt(radius * BrushDiameterFactor / spacing));
            for (int gx = 0; gx < gridSize; gx++)
            {
                for (int gz = 0; gz < gridSize; gz++)
                {
                    float offsetX = (gx - gridSize * 0.5f) * spacing + (float)(rng.NextDouble() - 0.5) * spacing * 0.6f;
                    float offsetZ = (gz - gridSize * 0.5f) * spacing + (float)(rng.NextDouble() - 0.5) * spacing * 0.6f;
                    if (offsetX * offsetX + offsetZ * offsetZ > radius * radius) continue;
                    outEntries.Add(new ClutterEntry
                    {
                        X = center.x + offsetX,
                        Y = center.y,
                        Z = center.z + offsetZ,
                        Scale = 1f,
                        RotY = (float)(rng.NextDouble() * 360.0),
                        ClutterType = clutterType,
                    });
                }
            }
        }

        // Removes byte-array entries whose center sits on a cleared
        // (non-default) paint pixel. NO LONGER auto-fired — the old
        // TerrainComp.Save postfix that drove this caused the "vanilla
        // cultivator clears my whole clutter patch" bug; VAClutterDirtPaintCleanup's
        // radius-scoped ResetGrass hook handles paint-op cleanup now.
        // KEPT public for a future admin console command (e.g.
        // "fap_scrub_cleared_paint"); not called in normal play.
        public static int RemoveEntriesOnClearedPaint(TerrainComp tc)
        {
            if (tc == null) return 0;
            var nview = tc.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return 0;

            var entries = LoadEntries(tc);
            int before = entries.Count;
            if (before == 0) return 0;

            entries.RemoveAll(entry =>
            {
                var pos = new Vector3(entry.X, entry.Y, entry.Z);
                var hmap = Heightmap.FindHeightmap(pos);
                if (hmap == null) return false;
                return hmap.IsCleared(pos);
            });

            int removed = before - entries.Count;
            if (removed > 0)
            {
                SaveEntries(tc, entries);
                if (ConfigManager.Instance.configVerboseLogging.Value)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAClutterPaintTool.RemoveEntriesOnClearedPaint — pruned {removed} clutter paint(s) now on cleared terrain at TerrainComp {tc.transform.position}.");
                }
            }
            return removed;
        }

        // RemoveEntriesInRadius plus a ±heightBounds vertical-reach limit (the
        // Clutter Clear brush's shift+wheel). Pass a huge heightBounds to behave
        // identically to RemoveEntriesInRadius (no Y filter).
        public static int RemoveEntriesInRadiusAndHeight(
            TerrainComp tc, Vector3 center, float radius, float heightBounds)
        {
            if (tc == null) return 0;
            var nview = tc.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return 0;

            var entries = LoadEntries(tc);
            int before = entries.Count;
            if (before == 0) return 0;

            float radiusSq = radius * radius;
            float heightAbs = Mathf.Abs(heightBounds);
            entries.RemoveAll(entry =>
            {
                float deltaX = entry.X - center.x;
                float deltaZ = entry.Z - center.z;
                if (deltaX * deltaX + deltaZ * deltaZ > radiusSq) return false;
                if (Mathf.Abs(entry.Y - center.y) > heightAbs) return false;
                return true;
            });

            int removed = before - entries.Count;
            if (removed > 0)
            {
                SaveEntries(tc, entries);
                if (ConfigManager.Instance.configVerboseLogging.Value)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAClutterPaintTool.RemoveEntriesInRadiusAndHeight — removed {removed} clutter sample(s) inside r={radius:F1} h=±{heightBounds:F1} of {center} on TerrainComp {tc.transform.position}.");
                }
            }
            return removed;
        }

        public static int RemoveEntriesInRadius(TerrainComp tc, Vector3 center, float radius)
        {
            if (tc == null) return 0;
            var nview = tc.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return 0;

            var entries = LoadEntries(tc);
            int before = entries.Count;
            if (before == 0) return 0;

            // Per-sample clear: each entry is one rendered sample (v2 schema),
            // removed iff its exact position falls inside the click circle —
            // a painted path slices through, both halves of the patch survive.
            float radiusSq = radius * radius;
            entries.RemoveAll(entry =>
            {
                float deltaX = entry.X - center.x;
                float deltaZ = entry.Z - center.z;
                return deltaX * deltaX + deltaZ * deltaZ <= radiusSq;
            });

            int removed = before - entries.Count;
            if (removed > 0)
            {
                SaveEntries(tc, entries);
                if (ConfigManager.Instance.configVerboseLogging.Value)
                {
                    Debug.Log($"{VaPieces.PluginName}: VAClutterPaintTool.RemoveEntriesInRadius — removed {removed} clutter paint(s) inside {radius:F1}m of {center} on TerrainComp {tc.transform.position}.");
                }
            }
            return removed;
        }

        private static void SaveEntries(TerrainComp tc, List<ClutterEntry> entries)
        {
            try
            {
                var nview = tc.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                var zdo = nview.GetZDO();
                if (zdo == null) return;

                var pkg = new ZPackage();
                pkg.Write(DATA_VERSION);
                pkg.Write(entries.Count);
                foreach (var entry in entries)
                {
                    pkg.Write(entry.X);
                    pkg.Write(entry.Y);
                    pkg.Write(entry.Z);
                    pkg.Write(entry.Scale);
                    pkg.Write(entry.RotY);
                    pkg.Write(entry.ClutterType ?? string.Empty);
                }

                byte[] compressed = Utils.Compress(pkg.GetArray());
                zdo.Set(ZDO_KEY, compressed);

                // A pristine comp (no heightmap deltas, no paint edits) has no
                // TCData on its ZDO and vanilla can evict it, taking our byte
                // array with it. Mark it modified + Save so vanilla keeps it.
                EnsureTerrainCompPersisted(tc);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPaintTool.SaveEntries failed: {ex.Message}");
            }
        }

        // Reflection cache for TerrainComp internals. Vanilla doesn't
        // expose these as public; we touch them once per painted comp
        // to flip the "is modified" bit + flush the comp state to ZDO.
        private static bool _tcReflectionCached;
        private static FieldInfo _tcPaintMaskField;
        private static FieldInfo _tcModifiedPaintField;
        private static FieldInfo _tcHmapField;
        private static MethodInfo _tcSaveMethod;

        private static void EnsureTcReflectionCached()
        {
            if (_tcReflectionCached) return;
            _tcReflectionCached = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            try
            {
                _tcPaintMaskField     = typeof(TerrainComp).GetField("m_paintMask", flags);
                _tcModifiedPaintField = typeof(TerrainComp).GetField("m_modifiedPaint", flags);
                _tcHmapField          = typeof(TerrainComp).GetField("m_hmap", flags);
                _tcSaveMethod         = typeof(TerrainComp).GetMethod("Save", flags);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterPaintTool reflection cache failed: {ex.Message}");
            }
        }

        // Flips m_modifiedPaint[0]=true (no visual change — m_paintMask[0]
        // stays default) so vanilla treats the comp as edited, then Save()s
        // its TCData to the ZDO so it persists through save + zone reload.
        private static void EnsureTerrainCompPersisted(TerrainComp tc)
        {
            if (tc == null) return;
            EnsureTcReflectionCached();
            if (_tcModifiedPaintField == null || _tcSaveMethod == null) return;

            try
            {
                var paintMask = _tcPaintMaskField?.GetValue(tc) as Color[];
                int maskLen = 0;
                if (paintMask == null)
                {
                    var hmap = _tcHmapField?.GetValue(tc) as Heightmap;
                    if (hmap == null) return;
                    int side = hmap.m_width + 1;
                    maskLen = side * side;
                    paintMask = new Color[maskLen];
                    for (int i = 0; i < maskLen; i++) paintMask[i] = Color.black;
                    _tcPaintMaskField.SetValue(tc, paintMask);
                }
                else
                {
                    maskLen = paintMask.Length;
                }

                var modifiedPaint = _tcModifiedPaintField.GetValue(tc) as bool[];
                if (modifiedPaint == null || modifiedPaint.Length != maskLen)
                {
                    modifiedPaint = new bool[maskLen];
                    _tcModifiedPaintField.SetValue(tc, modifiedPaint);
                }

                if (modifiedPaint.Length > 0 && !modifiedPaint[0])
                {
                    modifiedPaint[0] = true;
                    _tcSaveMethod.Invoke(tc, null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: EnsureTerrainCompPersisted failed: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // TerrainComp lifecycle patches
        // ────────────────────────────────────────────────────────────────

        // Register our RPC on every TerrainComp's ZNetView (vanilla does the
        // same for "ApplyOperation"). InvokeRPC from any client routes to the
        // owner, where the handler runs.
        [HarmonyPatch(typeof(TerrainComp), "Awake")]
        public static class TerrainComp_Awake_VAClutterRPCRegister_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(TerrainComp __instance)
            {
                if (__instance == null) return;
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null) return;
                var captured = __instance;
                nview.Register<ZPackage>(RPC_NAME, (long sender, ZPackage pkg) =>
                {
                    OnRpcPaintClutter(captured, sender, pkg);
                });
            }
        }

        // Postfix runs after vanilla Load, so our byte array reads from a
        // fully-synced ZDO with the transform placed. Fires regardless of
        // Load's return value — a clutter-only comp has no TCData (vanilla
        // returns false) but we still read our own byte array.
        [HarmonyPatch(typeof(TerrainComp), "Load")]
        public static class TerrainComp_Load_VAClutterRestore_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(TerrainComp __instance)
            {
                RestoreFromZDO(__instance);
            }
        }

    }
}
