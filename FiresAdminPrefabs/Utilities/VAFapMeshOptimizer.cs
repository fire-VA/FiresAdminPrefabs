using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Utilities
{
    // Lossless clean-up for combined bake meshes, a runtime port of MeshCombineEditor's pipeline.
    // Mesh.CombineMeshes concatenates naively, so every seam between pieces carries two to six
    // copies of its vertices — roughly 30% redundancy on a large blueprint. Welding removes 25-40%
    // of the vertices and degenerate clean-up a few percent of the triangles, which often brings a
    // part under the UInt16 index limit. Welding costs about 100ms per million vertices, paid once
    // at bake time.
    public static class VAFapMeshOptimizer
    {
        // Close enough to catch float drift at piece seams, far below any intentional detail.
        public const float DefaultWeldThreshold = 0.0001f;

        private const int MaxUInt16IndexedVertices = 65535;
        private const float DegenerateTriangleCrossSqrThreshold = 1e-7f;
        private const float NormalQuantizationScale = 100f;
        private const float UvQuantizationScale = 10000f;
        private const long FnvOffsetBasis = 1469598103934665603L;
        private const long FnvPrime = 1099511628211L;

        public struct Result
        {
            public int NewVertexCount;
            public int NewTriangleCount;

            // The original index of each submesh that survived, in order.
            public int[] KeptSubmeshIndices;
        }

        // Welds colocated vertices, drops degenerate triangles, unused vertices and empty
        // submeshes, then reorders for the GPU vertex cache. Dropping an empty submesh shifts the
        // ones after it, so callers remap their materials through KeptSubmeshIndices.
        public static Result Optimize(Mesh mesh, float weldThreshold = DefaultWeldThreshold)
        {
            var result = new Result();
            if (mesh == null) return result;

            WeldDuplicateVertices(mesh, weldThreshold);
            RemoveDegenerateTriangles(mesh);
            RemoveUnusedVertices(mesh);
            result.KeptSubmeshIndices = RemoveEmptySubmeshes(mesh);
            mesh.Optimize();
            mesh.RecalculateBounds();

            result.NewVertexCount = mesh.vertexCount;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                result.NewTriangleCount += (int)mesh.GetIndexCount(submesh) / 3;
            return result;
        }

        // A vertex's identity is its quantized position, normal and UV. Normal and UV are part of
        // it because welding across a hard edge breaks shading and across a UV island breaks the
        // texture seam; two wall pieces meeting at a seam share all three, so they weld.
        private static void WeldDuplicateVertices(Mesh mesh, float weldThreshold)
        {
            var streams = new VertexStreams(mesh);
            var indexByIdentity = new Dictionary<long, int>(streams.SourceCount);
            var remap = new int[streams.SourceCount];

            for (int vertex = 0; vertex < streams.SourceCount; vertex++)
            {
                long identity = VertexIdentityHash(streams.Position(vertex), streams.Normal(vertex), streams.Uv(vertex), weldThreshold);
                if (!indexByIdentity.TryGetValue(identity, out int weldedIndex))
                {
                    weldedIndex = streams.Append(vertex);
                    indexByIdentity[identity] = weldedIndex;
                }
                remap[vertex] = weldedIndex;
            }

            streams.ReplaceMeshData(mesh, RemapTriangles(mesh, remap));
        }

        private static void RemoveDegenerateTriangles(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var keptTriangles = new int[mesh.subMeshCount][];
            for (int submesh = 0; submesh < keptTriangles.Length; submesh++)
            {
                int[] triangles = mesh.GetTriangles(submesh);
                var kept = new List<int>(triangles.Length);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                    if (a == b || b == c || a == c) continue;
                    if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude < DegenerateTriangleCrossSqrThreshold) continue;
                    kept.Add(a);
                    kept.Add(b);
                    kept.Add(c);
                }
                keptTriangles[submesh] = kept.ToArray();
            }

            for (int submesh = 0; submesh < keptTriangles.Length; submesh++)
                mesh.SetTriangles(keptTriangles[submesh], submesh);
        }

        private static void RemoveUnusedVertices(Mesh mesh)
        {
            var streams = new VertexStreams(mesh);
            var used = new bool[streams.SourceCount];
            int usedCount = 0;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                foreach (int index in mesh.GetTriangles(submesh))
                {
                    if (used[index]) continue;
                    used[index] = true;
                    usedCount++;
                }
            }
            if (usedCount == streams.SourceCount) return;

            var remap = new int[streams.SourceCount];
            for (int vertex = 0; vertex < streams.SourceCount; vertex++)
                remap[vertex] = used[vertex] ? streams.Append(vertex) : -1;

            streams.ReplaceMeshData(mesh, RemapTriangles(mesh, remap));
        }

        // An empty submesh still costs a draw call.
        private static int[] RemoveEmptySubmeshes(Mesh mesh)
        {
            var keptIndices = new List<int>(mesh.subMeshCount);
            var keptTriangles = new List<int[]>(mesh.subMeshCount);
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                int[] triangles = mesh.GetTriangles(submesh);
                if (triangles.Length == 0) continue;
                keptIndices.Add(submesh);
                keptTriangles.Add(triangles);
            }

            if (keptIndices.Count != mesh.subMeshCount)
            {
                mesh.subMeshCount = keptTriangles.Count;
                for (int submesh = 0; submesh < keptTriangles.Count; submesh++)
                    mesh.SetTriangles(keptTriangles[submesh], submesh);
            }
            return keptIndices.ToArray();
        }

        private static int[][] RemapTriangles(Mesh mesh, int[] remap)
        {
            var remapped = new int[mesh.subMeshCount][];
            for (int submesh = 0; submesh < remapped.Length; submesh++)
            {
                int[] triangles = mesh.GetTriangles(submesh);
                for (int i = 0; i < triangles.Length; i++) triangles[i] = remap[triangles[i]];
                remapped[submesh] = triangles;
            }
            return remapped;
        }

        // Quantized vertex identity mixed into 64 bits with FNV-1a. A collision would weld two
        // distinct vertices, but even at ten million vertices the odds are a few in a million.
        private static long VertexIdentityHash(Vector3 position, Vector3 normal, Vector2 uv, float weldThreshold)
        {
            unchecked
            {
                long hash = FnvOffsetBasis;
                hash = (hash ^ Mathf.RoundToInt(position.x / weldThreshold)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(position.y / weldThreshold)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(position.z / weldThreshold)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(normal.x * NormalQuantizationScale)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(normal.y * NormalQuantizationScale)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(normal.z * NormalQuantizationScale)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(uv.x * UvQuantizationScale)) * FnvPrime;
                hash = (hash ^ Mathf.RoundToInt(uv.y * UvQuantizationScale)) * FnvPrime;
                return hash;
            }
        }

        // A mesh's per-vertex streams, compacted by appending the source vertices to keep in order.
        // Streams whose length doesn't match the vertex count are treated as absent.
        private sealed class VertexStreams
        {
            private readonly Vector3[] _positions;
            private readonly Vector3[] _normals;
            private readonly Vector2[] _uv;
            private readonly Vector2[] _uv2;
            private readonly Vector4[] _tangents;
            private readonly Color[] _colors;

            private readonly List<Vector3> _keptPositions = new List<Vector3>();
            private readonly List<Vector3> _keptNormals = new List<Vector3>();
            private readonly List<Vector2> _keptUv = new List<Vector2>();
            private readonly List<Vector2> _keptUv2 = new List<Vector2>();
            private readonly List<Vector4> _keptTangents = new List<Vector4>();
            private readonly List<Color> _keptColors = new List<Color>();

            public VertexStreams(Mesh mesh)
            {
                _positions = mesh.vertices;
                _normals = MatchingOrNull(mesh.normals);
                _uv = MatchingOrNull(mesh.uv);
                _uv2 = MatchingOrNull(mesh.uv2);
                _tangents = MatchingOrNull(mesh.tangents);
                _colors = MatchingOrNull(mesh.colors);
            }

            public int SourceCount => _positions.Length;

            public Vector3 Position(int vertex) => _positions[vertex];
            public Vector3 Normal(int vertex) => _normals != null ? _normals[vertex] : Vector3.zero;
            public Vector2 Uv(int vertex) => _uv != null ? _uv[vertex] : Vector2.zero;

            public int Append(int vertex)
            {
                _keptPositions.Add(_positions[vertex]);
                if (_normals != null) _keptNormals.Add(_normals[vertex]);
                if (_uv != null) _keptUv.Add(_uv[vertex]);
                if (_uv2 != null) _keptUv2.Add(_uv2[vertex]);
                if (_tangents != null) _keptTangents.Add(_tangents[vertex]);
                if (_colors != null) _keptColors.Add(_colors[vertex]);
                return _keptPositions.Count - 1;
            }

            // Also picks the smallest index format that fits the kept vertices.
            public void ReplaceMeshData(Mesh mesh, int[][] submeshTriangles)
            {
                mesh.Clear();
                mesh.indexFormat = _keptPositions.Count > MaxUInt16IndexedVertices ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.vertices = _keptPositions.ToArray();
                if (_normals != null) mesh.normals = _keptNormals.ToArray();
                if (_uv != null) mesh.uv = _keptUv.ToArray();
                if (_uv2 != null) mesh.uv2 = _keptUv2.ToArray();
                if (_tangents != null) mesh.tangents = _keptTangents.ToArray();
                if (_colors != null) mesh.colors = _keptColors.ToArray();
                mesh.subMeshCount = submeshTriangles.Length;
                for (int submesh = 0; submesh < submeshTriangles.Length; submesh++)
                    mesh.SetTriangles(submeshTriangles[submesh], submesh);
            }

            private T[] MatchingOrNull<T>(T[] stream) =>
                stream != null && stream.Length == _positions.Length ? stream : null;
        }
    }
}
