using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // Names the prefab behind a baked blueprint's stray geometry — the long thin shapes that
    // cut through a finished bake and still have collision.
    //
    // Measured per contributed mesh while the bake collects it, so the report names the source
    // prefab instead of the combined part it ended up in: a few vertices sit far outside the bulk
    // of their own mesh, so the combine draws triangles from the piece out to wherever they are.
    //
    // A transform-scale check used to run alongside this and was dropped — blueprints scale
    // pieces on purpose, so beams at 16x and 36x filled the report with builds that were fine.
    internal static class VAFapBakeGeometryDiagnostic
    {
        private const float SpikeToBulkRatio = 8f;
        private const float MinimumSpikeMeters = 6f;
        private const int MaxReportedPrefabs = 12;

        private sealed class MeshMeasurement
        {
            public float MeanVertexDistance;
            public float MaxVertexDistance;
            public bool IsSpiky;
        }

        private sealed class PrefabFinding
        {
            public string PrefabName;
            public string MeshName;
            public int VertexCount;
            public float MeanVertexDistance;
            public float MaxVertexDistance;
            public bool MeshWasRebuiltForReading;
            public Vector3 SampleBlueprintPosition;
            public int Occurrences;
        }

        private static readonly Dictionary<Mesh, MeshMeasurement> _measuredMeshes =
            new Dictionary<Mesh, MeshMeasurement>();
        private static readonly Dictionary<string, PrefabFinding> _findingsByPrefab =
            new Dictionary<string, PrefabFinding>(StringComparer.Ordinal);

        internal static void BeginBake()
        {
            _measuredMeshes.Clear();
            _findingsByPrefab.Clear();
        }

        internal static void InspectContribution(
            GameObject prefab,
            Mesh mesh,
            bool meshWasRebuiltForReading,
            Matrix4x4 contributionMatrix)
        {
            if (prefab == null || mesh == null) return;

            var measurement = Measure(mesh);
            if (!measurement.IsSpiky) return;

            string prefabName = prefab.name;
            if (!_findingsByPrefab.TryGetValue(prefabName, out var finding))
            {
                finding = new PrefabFinding
                {
                    PrefabName = prefabName,
                    MeshName = mesh.name,
                    VertexCount = mesh.vertexCount,
                    MeanVertexDistance = measurement.MeanVertexDistance,
                    MaxVertexDistance = measurement.MaxVertexDistance,
                    MeshWasRebuiltForReading = meshWasRebuiltForReading,
                    SampleBlueprintPosition = contributionMatrix.MultiplyPoint3x4(Vector3.zero),
                };
                _findingsByPrefab[prefabName] = finding;
            }
            finding.Occurrences++;
        }

        internal static void EmitReport(string cacheKey)
        {
            if (_findingsByPrefab.Count == 0)
            {
                Emit($"[BakeGeometry] '{cacheKey}': {_measuredMeshes.Count} distinct mesh(es) measured, "
                    + "all stay inside their own piece. Stray geometry in this bake did not come from a source mesh.",
                    isWarning: false);
                return;
            }

            var findings = new List<PrefabFinding>(_findingsByPrefab.Values);
            findings.Sort((left, right) => right.MaxVertexDistance.CompareTo(left.MaxVertexDistance));

            var report = new StringBuilder();
            report.Append($"[BakeGeometry] '{cacheKey}': {findings.Count} prefab(s) contributed geometry that reaches outside the piece. ");
            report.AppendLine("These are what render as long thin shapes with collision through the finished bake:");

            int reported = Mathf.Min(findings.Count, MaxReportedPrefabs);
            for (int i = 0; i < reported; i++)
            {
                var finding = findings[i];
                var position = finding.SampleBlueprintPosition;
                report.Append($"  {finding.PrefabName}  mesh '{finding.MeshName}' ({finding.VertexCount} verts)");
                report.Append($"  bulk {finding.MeanVertexDistance:F2}m but reaches {finding.MaxVertexDistance:F2}m");
                if (finding.MeshWasRebuiltForReading)
                    report.Append("  [mesh was rebuilt for reading]");
                report.AppendLine($"  x{finding.Occurrences}, first near ({position.x:F1}, {position.y:F1}, {position.z:F1})");
            }
            if (findings.Count > reported)
                report.AppendLine($"  ... and {findings.Count - reported} more prefab(s).");

            Emit(report.ToString(), isWarning: true);
        }

        // Routed through FAP's BepInEx source rather than Debug.Log. The 2026-09-16 rebake sweep
        // ran EmitReport 220 times and not one line reached the BepInEx log or Player.log, while
        // a Debug.LogWarning from the calling method did — so the report goes out on the same
        // source the load summaries use, which survived that sweep intact.
        private static void Emit(string message, bool isWarning)
        {
            var log = VaPieces.Log;
            if (log == null)
            {
                string fallback = $"{VaPieces.PluginName}: {message}";
                if (isWarning) Debug.LogWarning(fallback);
                else Debug.Log(fallback);
                return;
            }
            if (isWarning) log.LogWarning(message);
            else log.LogInfo(message);
        }

        private static MeshMeasurement Measure(Mesh mesh)
        {
            if (_measuredMeshes.TryGetValue(mesh, out var cached)) return cached;

            var measurement = new MeshMeasurement();
            _measuredMeshes[mesh] = measurement;

            Vector3[] vertices;
            try { vertices = mesh.vertices; }
            catch (Exception ex)
            {
                Emit($"[BakeGeometry] reading vertices of '{mesh.name}' threw: {ex.Message}", isWarning: true);
                return measurement;
            }
            if (vertices == null || vertices.Length == 0) return measurement;

            Vector3 centre = mesh.bounds.center;
            double distanceSum = 0.0;
            float maxDistance = 0f;
            for (int i = 0; i < vertices.Length; i++)
            {
                float distance = (vertices[i] - centre).magnitude;
                distanceSum += distance;
                if (distance > maxDistance) maxDistance = distance;
            }

            measurement.MeanVertexDistance = (float)(distanceSum / vertices.Length);
            measurement.MaxVertexDistance = maxDistance;
            measurement.IsSpiky = maxDistance > MinimumSpikeMeters
                && maxDistance > measurement.MeanVertexDistance * SpikeToBulkRatio;
            return measurement;
        }

    }
}
