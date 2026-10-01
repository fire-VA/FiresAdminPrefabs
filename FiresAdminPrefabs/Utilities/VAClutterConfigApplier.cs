using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Applies a clutter preset by adding a tuned copy of the vanilla ClutterSystem entry beside it,
    // so the original clutter keeps rendering and the preset's variant renders with it. Each preset
    // owns at most one copy: re-applying replaces it, and RemoveAddedEntry drops it. ApplyAllActive
    // restores every active preset at world load and on hot reload.
    //
    // Only the grass patches already loaded around the player are regenerated afterwards. The sway
    // and push values go onto the prefab's renderers through a MaterialPropertyBlock, so they reach
    // the original entry too but never persist into the shared grass material.
    public static class VAClutterConfigApplier
    {
        private static readonly int PropSwaySpeed = Shader.PropertyToID("_SwaySpeed");
        private static readonly int PropSwayDistance = Shader.PropertyToID("_SwayDistance");
        private static readonly int PropPushDistance = Shader.PropertyToID("_PushDistance");

        private static readonly Dictionary<string, ClutterSystem.Clutter> _addedEntries =
            new Dictionary<string, ClutterSystem.Clutter>(StringComparer.Ordinal);

        // Returns false when no vanilla entry matches the preset's clutter prefab, which usually
        // means a load-order problem or a typo in the preset.
        public static bool Apply(VAClutterPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.clutterPrefab)) return false;
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return false;

            var original = FindOriginalEntry(clutterSystem, preset.clutterPrefab);
            if (original == null)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAClutterConfigApplier — no ClutterSystem entry matches preset '{preset.name}' (clutterPrefab='{preset.clutterPrefab}').");
                return false;
            }

            RemoveAddedEntry(preset.name, regenerate: false);

            var copy = new ClutterSystem.Clutter
            {
                m_name = $"{original.m_name} [VA:{preset.name}]",
                m_prefab = original.m_prefab,
                m_biome = original.m_biome,
            };
            WriteFieldsToClutter(preset, copy);
            clutterSystem.m_clutter.Add(copy);
            _addedEntries[preset.name] = copy;

            ApplyShaderProperties(preset, copy);
            RegenerateLoadedPatches(clutterSystem);
            return true;
        }

        public static bool RemoveAddedEntry(string presetName, bool regenerate = true)
        {
            if (string.IsNullOrEmpty(presetName) || !_addedEntries.TryGetValue(presetName, out var copy)) return false;

            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem != null && clutterSystem.m_clutter != null) clutterSystem.m_clutter.Remove(copy);
            _addedEntries.Remove(presetName);
            if (regenerate && clutterSystem != null) RegenerateLoadedPatches(clutterSystem);
            return true;
        }

        // Copies the live entry's values into a preset, so a blank config form starts from what the
        // clutter really uses rather than hardcoded defaults (a default m_instanced=false would break
        // most grass). Shader values aren't captured: they live on the material, and the preset's
        // defaults match vanilla grass. Returns false when no entry matches.
        public static bool SnapshotCurrentValuesTo(string clutterPrefab, VAClutterPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(clutterPrefab)) return false;
            var clutterSystem = ClutterSystem.instance;
            if (clutterSystem == null || clutterSystem.m_clutter == null) return false;

            var source = FindOriginalEntry(clutterSystem, clutterPrefab);
            if (source == null) return false;

            preset.enabled = source.m_enabled;
            preset.amount = source.m_amount;
            preset.snapToWater = source.m_snapToWater;
            preset.onUncleared = source.m_onUncleared;
            preset.onCleared = source.m_onCleared;
            preset.terrainTilt = source.m_terrainTilt;
            preset.instanced = source.m_instanced;
            preset.randomOffset = source.m_randomOffset;
            preset.minAlt = source.m_minAlt;
            preset.maxAlt = source.m_maxAlt;
            preset.minOceanDepth = source.m_minOceanDepth;
            preset.maxOceanDepth = source.m_maxOceanDepth;
            preset.minTilt = source.m_minTilt;
            preset.maxTilt = source.m_maxTilt;
            preset.minVegetation = source.m_minVegetation;
            preset.maxVegetation = source.m_maxVegetation;
            preset.scaleMin = source.m_scaleMin;
            preset.scaleMax = source.m_scaleMax;
            preset.fractalScale = source.m_fractalScale;
            preset.fractalOffset = source.m_fractalOffset;
            preset.fractalTresholdMin = source.m_fractalTresholdMin;
            preset.fractalTresholdMax = source.m_fractalTresholdMax;
            preset.inForest = source.m_inForest;
            preset.forestTresholdMin = source.m_forestTresholdMin;
            preset.forestTresholdMax = source.m_forestTresholdMax;
            preset.biome = source.m_biome.ToString();
            return true;
        }

        public static int ApplyAllActive()
        {
            if (ClutterSystem.instance == null) return 0;

            int applied = 0;
            foreach (var preset in VAClutterPresetStore.All.Values)
            {
                if (preset == null || string.IsNullOrEmpty(preset.clutterPrefab)) continue;
                string activeName = VAClutterPresetStore.GetActiveNameFor(preset.clutterPrefab);
                if (!string.Equals(activeName, preset.name, StringComparison.Ordinal)) continue;
                if (Apply(preset)) applied++;
            }
            if (applied > 0)
                Debug.Log($"{VaPieces.PluginName}: VAClutterConfigApplier — applied {applied} active clutter preset(s).");
            return applied;
        }

        // Skips this class's own copies, so re-applying a preset still copies the vanilla entry.
        private static ClutterSystem.Clutter FindOriginalEntry(ClutterSystem clutterSystem, string clutterPrefab)
        {
            foreach (var clutter in clutterSystem.m_clutter)
            {
                if (clutter == null || clutter.m_prefab == null || _addedEntries.ContainsValue(clutter)) continue;
                if (string.Equals(clutter.m_prefab.name, clutterPrefab, StringComparison.Ordinal)) return clutter;
            }
            return null;
        }

        // Every loaded patch lies within the draw distance of the player, so resetting that radius
        // plus one patch regenerates exactly the loaded set. Without a local player every patch is
        // cleared instead.
        private static void RegenerateLoadedPatches(ClutterSystem clutterSystem)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                clutterSystem.ClearAll();
                return;
            }
            clutterSystem.ResetGrass(player.transform.position, clutterSystem.m_distance + clutterSystem.m_grassPatchSize);
        }

        // An empty biome list keeps the entry's current biomes, so a preset that doesn't name any
        // can't silently disable its clutter everywhere.
        private static void WriteFieldsToClutter(VAClutterPreset preset, ClutterSystem.Clutter target)
        {
            target.m_enabled = preset.enabled;
            target.m_amount = preset.amount;
            target.m_snapToWater = preset.snapToWater;
            target.m_onUncleared = preset.onUncleared;
            target.m_onCleared = preset.onCleared;
            target.m_terrainTilt = preset.terrainTilt;
            target.m_instanced = preset.instanced;
            target.m_randomOffset = preset.randomOffset;
            target.m_minAlt = preset.minAlt;
            target.m_maxAlt = preset.maxAlt;
            target.m_minOceanDepth = preset.minOceanDepth;
            target.m_maxOceanDepth = preset.maxOceanDepth;
            target.m_minTilt = preset.minTilt;
            target.m_maxTilt = preset.maxTilt;
            target.m_minVegetation = preset.minVegetation;
            target.m_maxVegetation = preset.maxVegetation;
            target.m_scaleMin = preset.scaleMin;
            target.m_scaleMax = preset.scaleMax;
            target.m_fractalScale = preset.fractalScale;
            target.m_fractalOffset = preset.fractalOffset;
            target.m_fractalTresholdMin = preset.fractalTresholdMin;
            target.m_fractalTresholdMax = preset.fractalTresholdMax;
            target.m_inForest = preset.inForest;
            target.m_forestTresholdMin = preset.forestTresholdMin;
            target.m_forestTresholdMax = preset.forestTresholdMax;

            var biomes = ParseBiomeMask(preset.biome);
            if (biomes.HasValue) target.m_biome = biomes.Value;
        }

        // Accepts biome names or their integer values, comma-separated, matching what
        // Heightmap.Biome.ToString produces for both vanilla and modded biome bits.
        private static Heightmap.Biome? ParseBiomeMask(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            var mask = Heightmap.Biome.None;
            bool any = false;
            foreach (var rawName in csv.Split(','))
            {
                string biomeName = rawName.Trim();
                if (string.IsNullOrEmpty(biomeName)) continue;
                if (Enum.TryParse<Heightmap.Biome>(biomeName, true, out var biome))
                {
                    mask |= biome;
                    any = true;
                }
                else
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAClutterConfigApplier — unknown biome '{biomeName}' in preset.");
                }
            }
            return any ? mask : (Heightmap.Biome?)null;
        }

        private static void ApplyShaderProperties(VAClutterPreset preset, ClutterSystem.Clutter target)
        {
            if (target.m_prefab == null) return;

            var propertyBlock = new MaterialPropertyBlock();
            foreach (var renderer in target.m_prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetFloat(PropSwaySpeed, preset.swaySpeed);
                propertyBlock.SetFloat(PropSwayDistance, preset.swayDistance);
                propertyBlock.SetFloat(PropPushDistance, preset.pushDistance);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }
    }
}
