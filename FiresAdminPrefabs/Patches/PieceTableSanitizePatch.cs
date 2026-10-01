using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Patches
{
    // Removes Unity-destroyed entries from PieceTable.m_pieces before
    // vanilla's UpdateAvailable iterates them. Without this prefix a
    // single destroyed reference NREs every frame the player is in build
    // mode.
    //
    // First detection per table also logs a diagnostic that compares the
    // surviving entries against FAP's blueprint-stub registry so we can
    // distinguish "we destroyed our own templates" from "another mod
    // destroyed something we don't own."
    [HarmonyPatch(typeof(PieceTable), "UpdateAvailable")]
    public static class PieceTable_UpdateAvailable_SanitizePatch
    {
        private const int MaxIndicesInLog = 20;
        private const int MaxDestroyedStubsInLog = 5;

        private static readonly HashSet<int> _diagnosedTables = new HashSet<int>();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(PieceTable __instance)
        {
            if (__instance == null || __instance.m_pieces == null) return;

            int beforeCount = __instance.m_pieces.Count;

            var nullIndices = new List<int>();
            for (int i = 0; i < __instance.m_pieces.Count; i++)
            {
                var p = __instance.m_pieces[i];
                if (p == null) nullIndices.Add(i);
            }

            if (nullIndices.Count == 0) return;

            int tableId = __instance.GetInstanceID();
            if (_diagnosedTables.Add(tableId))
                DiagnoseNullPieces(__instance, nullIndices, beforeCount);

            __instance.m_pieces.RemoveAll(p => p == null);
        }

        // Can't recover the name of a destroyed GameObject, but we can
        // walk our blueprint-stub registry and ask each tracked name
        // "are you still alive in the table?" — absences imply a FAP-owned
        // destroy.
        private static void DiagnoseNullPieces(PieceTable table, List<int> nullIndices, int totalCount)
        {
            int fapStubCount = 0;
            int otherCount = 0;
            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                var p = table.m_pieces[i];
                if (p == null) continue;
                string n;
                try { n = p.name ?? string.Empty; } catch { continue; }
                if (n.StartsWith("BP_", StringComparison.Ordinal)) fapStubCount++;
                else otherCount++;
            }

            int fapStubsDestroyed = 0;
            var destroyedStubNames = new List<string>();
            try
            {
                foreach (var stubName in VABlueprintHammerIntegration.RegisteredBlueprintPrefabNames)
                {
                    bool foundAlive = false;
                    for (int i = 0; i < table.m_pieces.Count; i++)
                    {
                        var p = table.m_pieces[i];
                        if (p == null) continue;
                        string n;
                        try { n = p.name; } catch { continue; }
                        if (n == stubName) { foundAlive = true; break; }
                    }
                    if (!foundAlive)
                    {
                        fapStubsDestroyed++;
                        if (destroyedStubNames.Count < MaxDestroyedStubsInLog)
                            destroyedStubNames.Add(stubName);
                    }
                }
            }
            catch { }

            int aliveAfter = totalCount - nullIndices.Count;
            int indicesToShow = Math.Min(nullIndices.Count, MaxIndicesInLog);
            string firstFewIndices = string.Join(",", nullIndices.GetRange(0, indicesToShow).ConvertAll(i => i.ToString()));

            Debug.LogWarning(
                $"{VaPieces.PluginName}: PieceTable.UpdateAvailable — detected {nullIndices.Count} destroyed GameObject reference(s) in m_pieces.\n" +
                $"  Table InstanceID: {table.GetInstanceID()} ({table.name})\n" +
                $"  m_pieces count: {totalCount} total → {aliveAfter} alive after filter\n" +
                $"  Null indices: [{firstFewIndices}{(nullIndices.Count > MaxIndicesInLog ? "..." : "")}]\n" +
                $"  Alive pieces by category: BP_* (FAP blueprint stubs)={fapStubCount}, other={otherCount}\n" +
                $"  FAP blueprint stubs registered: {VABlueprintHammerIntegration.RegisteredCount}, destroyed-since-registration: {fapStubsDestroyed}\n" +
                $"  Sample destroyed FAP stubs (first {MaxDestroyedStubsInLog}): [{string.Join(", ", destroyedStubNames)}]\n" +
                $"  → If fapStubsDestroyed > 0: a FAP code path is destroying our own blueprint stubs (investigate).\n" +
                $"  → If fapStubsDestroyed == 0 AND nulls > 0: external (vanilla / other mod) is destroying non-FAP pieces.\n" +
                $"  This diagnostic logs ONCE per PieceTable instance per session. Defensive cleanup runs on every call regardless.");
        }
    }
}
