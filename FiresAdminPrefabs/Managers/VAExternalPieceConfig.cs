using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Jotunn-style mods ship their build pieces as a bundle plus a table of
    // piece settings, and hand both to PieceManager.AddPiece. Once SteamyDumps
    // has yoinked the bundle, FAP already registers the prefabs — what is left
    // is the table, which is nothing Jotunn is needed for: every field it holds
    // maps onto a vanilla Piece.
    //
    // So the table is read straight from JSON of the same shape the source mod
    // embedded, one file per mod under Config/FiresAdminPrefabs/PieceConfigs/:
    //
    //   [ { "Prefab": "wood_finerstepladder",
    //       "Config": { "Name": "$piece_x", "Description": "$piece_x_description",
    //                   "PieceTable": "Hammer", "Category": "BuildingWorkbench",
    //                   "CraftingStation": "piece_workbench",
    //                   "AllowedInDungeons": false,
    //                   "Requirements": [ { "Item": "Wood", "Amount": 1, "Recover": true } ] } } ]
    //
    // Registering the prefab is what makes a saved ZDO resolve; this pass is
    // what makes the piece buildable again. They are deliberately separate, so
    // a world keeps its existing pieces even if no config is present.
    internal static class VAExternalPieceConfig
    {
        private const string DefaultPieceTable = "Hammer";
        private const string ConfigFolderName = "PieceConfigs";

        private static string ConfigDir =>
            Path.Combine(Paths.ConfigPath, "FiresAdminPrefabs", ConfigFolderName);

        [Serializable]
        private class PieceConfigEntry
        {
            public string Prefab;
            public PieceSettings Config;
        }

        [Serializable]
        private class PieceSettings
        {
            public string Name;
            public string Description;
            public string PieceTable;
            public string Category;
            public string CraftingStation;
            public bool AllowedInDungeons;
            public List<RequirementSettings> Requirements;
        }

        [Serializable]
        private class RequirementSettings
        {
            public string Item;
            public int Amount = 1;
            public bool Recover = true;
        }

        internal static void ApplyAll()
        {
            if (ZNetScene.instance == null || ObjectDB.instance == null) return;
            if (!Directory.Exists(ConfigDir)) return;

            var files = Directory.GetFiles(ConfigDir, "*.json");
            if (files.Length == 0) return;

            int configured = 0, missingItem = 0;
            var unregistered = new List<string>();
            foreach (var file in files)
            {
                List<PieceConfigEntry> entries;
                try { entries = JsonConvert.DeserializeObject<List<PieceConfigEntry>>(File.ReadAllText(file)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAExternalPieceConfig could not read '{Path.GetFileName(file)}': {ex.Message}");
                    continue;
                }
                if (entries == null) continue;

                foreach (var entry in entries)
                {
                    if (entry?.Config == null || string.IsNullOrEmpty(entry.Prefab)) continue;
                    var result = ApplyOne(entry);
                    if (result == ApplyResult.NoPrefab) unregistered.Add(entry.Prefab);
                    else if (result == ApplyResult.Configured) configured++;
                    if (result == ApplyResult.MissingRequirement) missingItem++;
                }
            }

            Debug.Log($"{VaPieces.PluginName}: VAExternalPieceConfig configured {configured} external piece(s) from " +
                      $"{files.Length} file(s) ({unregistered.Count} prefab(s) not registered, {missingItem} with an unresolved requirement item). " +
                      "A prefab that is registered but unconfigured still loads from the save; it just is not buildable.");

            foreach (var name in unregistered) ReportWhereItWent(name);
        }

        // GetPrefab is a hash lookup into m_namedPrefabs, so a null tells us the
        // hash is absent and nothing else. Saying which of the three stages lost
        // the prefab - the bundle, the injection, or the named-prefab map - turns
        // one null into an answer instead of a guess.
        private static void ReportWhereItWent(string prefabName)
        {
            int hash = prefabName.GetStableHashCode();

            bool inExternals = VAExternalBundleLoader.ExternalPrefabs
                .Any(p => p != null && p.name == prefabName);

            var scene = ZNetScene.instance;
            var inScenePrefabs = scene.m_prefabs.FirstOrDefault(p => p != null && p.name == prefabName);
            var byHash = scene.GetPrefab(hash);

            string owner = byHash == null
                ? "nothing"
                : $"'{byHash.name}' (hash {byHash.name.GetStableHashCode()})";

            Debug.LogWarning($"{VaPieces.PluginName}: VAExternalPieceConfig '{prefabName}' (hash {hash}) is unresolved. " +
                             $"In ExternalPrefabs: {inExternals}. In ZNetScene.m_prefabs: {inScenePrefabs != null}. " +
                             $"m_namedPrefabs[{hash}] holds {owner}. " +
                             (inScenePrefabs != null
                                ? "It IS in m_prefabs but not under its own hash - ZNetScene.Awake builds m_namedPrefabs with Dictionary.Add, so a duplicate hash earlier in the list aborts the rest of that loop."
                                : inExternals
                                    ? "It came out of the bundle but never reached m_prefabs - the injection skipped it."
                                    : "It never came out of the bundle at all."));
        }

        private enum ApplyResult { Configured, NoPrefab, MissingRequirement }

        private static ApplyResult ApplyOne(PieceConfigEntry entry)
        {
            var prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            if (prefab == null) return ApplyResult.NoPrefab;

            var piece = prefab.GetComponent<Piece>();
            if (piece == null) piece = prefab.AddComponent<Piece>();

            var settings = entry.Config;
            if (!string.IsNullOrEmpty(settings.Name)) piece.m_name = settings.Name;
            if (!string.IsNullOrEmpty(settings.Description)) piece.m_description = settings.Description;
            piece.m_allowedInDungeons = settings.AllowedInDungeons;
            piece.m_category = ParseCategory(settings.Category);

            var station = ResolveCraftingStation(settings.CraftingStation);
            if (station != null) piece.m_craftingStation = station;

            bool allRequirementsResolved = TryBuildRequirements(settings.Requirements, out var requirements);
            if (requirements != null) piece.m_resources = requirements;

            AddToPieceTable(string.IsNullOrEmpty(settings.PieceTable) ? DefaultPieceTable : settings.PieceTable, prefab);

            return allRequirementsResolved ? ApplyResult.Configured : ApplyResult.MissingRequirement;
        }

        private static Piece.PieceCategory ParseCategory(string category)
        {
            if (!string.IsNullOrEmpty(category) && Enum.TryParse(category, ignoreCase: true, out Piece.PieceCategory parsed))
                return parsed;
            return Piece.PieceCategory.Misc;
        }

        private static CraftingStation ResolveCraftingStation(string stationPrefabName)
        {
            if (string.IsNullOrEmpty(stationPrefabName)) return null;
            var stationPrefab = ZNetScene.instance.GetPrefab(stationPrefabName);
            return stationPrefab != null ? stationPrefab.GetComponent<CraftingStation>() : null;
        }

        private static bool TryBuildRequirements(List<RequirementSettings> requirements, out Piece.Requirement[] built)
        {
            built = null;
            if (requirements == null || requirements.Count == 0)
            {
                built = Array.Empty<Piece.Requirement>();
                return true;
            }

            bool allResolved = true;
            var list = new List<Piece.Requirement>(requirements.Count);
            foreach (var requirement in requirements)
            {
                if (requirement == null || string.IsNullOrEmpty(requirement.Item)) continue;
                var itemPrefab = ObjectDB.instance.GetItemPrefab(requirement.Item);
                var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (itemDrop == null)
                {
                    allResolved = false;
                    continue;
                }
                list.Add(new Piece.Requirement
                {
                    m_resItem = itemDrop,
                    m_amount = requirement.Amount,
                    m_recover = requirement.Recover,
                });
            }
            built = list.ToArray();
            return allResolved;
        }

        private static void AddToPieceTable(string pieceTableName, GameObject prefab)
        {
            var table = ResolvePieceTable(pieceTableName);
            if (table == null || table.m_pieces.Contains(prefab)) return;
            table.m_pieces.Add(prefab);
        }

        // A piece table is reached through the tool that carries it, the same
        // way vanilla does it — the Hammer's ItemDrop holds m_buildPieces.
        private static PieceTable ResolvePieceTable(string pieceTableName)
        {
            var toolPrefab = ObjectDB.instance.GetItemPrefab(pieceTableName);
            var toolItem = toolPrefab != null ? toolPrefab.GetComponent<ItemDrop>() : null;
            if (toolItem?.m_itemData?.m_shared?.m_buildPieces != null)
                return toolItem.m_itemData.m_shared.m_buildPieces;

            return Resources.FindObjectsOfTypeAll<PieceTable>()
                .FirstOrDefault(t => t != null && t.name == pieceTableName);
        }
    }
}
