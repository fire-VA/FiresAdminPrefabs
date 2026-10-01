using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Utilities
{
    // Rebuilds CPU-readable copies of meshes whose import-time Read/Write flag
    // is off (many vanilla Mistlands/Ashlands building assets), so the bake's
    // CombineMeshes can consume them. Each distinct mesh is read back from the
    // GPU once via AsyncGPUReadback and the raw vertex/index bytes are copied
    // into a new mesh with the same vertex layout, then cached and reused.
    public static class VAFapReadableMeshProvider
    {
        private const MeshUpdateFlags SilentMeshWrite =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers
            | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds;

        private static readonly Dictionary<Mesh, Mesh> _readableCopies = new Dictionary<Mesh, Mesh>();
        private static readonly HashSet<Mesh> _unrecoverable = new HashSet<Mesh>();

        public static bool IsSupported => SystemInfo.supportsAsyncGPUReadback;

        // Returns the source if already readable, the warmed copy if one exists,
        // else null (caller skips the mesh).
        public static Mesh GetReadable(Mesh source)
        {
            if (source == null) return null;
            if (source.isReadable) return source;
            return _readableCopies.TryGetValue(source, out var copy) ? copy : null;
        }

        // Builds + caches a readable copy for every non-readable mesh in the set.
        // Fires all GPU readbacks, waits for them together, then reconstructs.
        public static IEnumerator WarmCoroutine(ICollection<Mesh> meshes, Action<int, int> onProgress = null)
        {
            if (meshes == null || meshes.Count == 0) yield break;

            var pending = new List<Mesh>();
            var seen = new HashSet<Mesh>();
            foreach (var mesh in meshes)
            {
                if (mesh == null || mesh.isReadable) continue;
                if (!seen.Add(mesh)) continue;
                if (_readableCopies.ContainsKey(mesh) || _unrecoverable.Contains(mesh)) continue;
                pending.Add(mesh);
            }
            if (pending.Count == 0) { onProgress?.Invoke(0, 0); yield break; }

            if (!IsSupported)
            {
                foreach (var mesh in pending) _unrecoverable.Add(mesh);
                Debug.LogWarning($"{VaPieces.PluginName}: VAFapReadableMeshProvider — AsyncGPUReadback unsupported on this system; {pending.Count} non-readable mesh(es) cannot be baked.");
                onProgress?.Invoke(0, pending.Count);
                yield break;
            }

            var jobs = new List<ReadbackJob>(pending.Count);
            foreach (var mesh in pending)
            {
                ReadbackJob job = null;
                try { job = BeginReadback(mesh); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapReadableMeshProvider — BeginReadback threw for '{mesh.name}': {ex.Message}");
                }
                if (job == null) { _unrecoverable.Add(mesh); continue; }
                jobs.Add(job);
            }

            bool anyPending = true;
            while (anyPending)
            {
                anyPending = false;
                for (int i = 0; i < jobs.Count; i++)
                    if (!jobs[i].AllDone()) { anyPending = true; break; }
                if (anyPending) yield return null;
            }

            int built = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                Mesh copy = null;
                try { copy = job.BuildCopy(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{VaPieces.PluginName}: VAFapReadableMeshProvider — rebuild failed for '{job.Source.name}': {ex.Message}");
                }
                finally { job.Dispose(); }

                if (copy != null) { _readableCopies[job.Source] = copy; built++; }
                else _unrecoverable.Add(job.Source);
            }

            Debug.Log($"{VaPieces.PluginName}: VAFapReadableMeshProvider — warmed {built}/{pending.Count} non-readable mesh(es) via GPU readback (cache={_readableCopies.Count}, failed={_unrecoverable.Count}).");
            onProgress?.Invoke(built, pending.Count);
        }

        private static ReadbackJob BeginReadback(Mesh source)
        {
            int streamCount = source.vertexBufferCount;
            if (streamCount <= 0 || source.vertexCount <= 0) return null;

            var job = new ReadbackJob
            {
                Source = source,
                VertexCount = source.vertexCount,
                Attributes = source.GetVertexAttributes(),
                IndexFormat = source.indexFormat,
                VertexStreams = new AsyncGPUReadbackRequest[streamCount],
                VertexBuffers = new GraphicsBuffer[streamCount],
            };

            for (int stream = 0; stream < streamCount; stream++)
            {
                var vertexBuffer = source.GetVertexBuffer(stream);
                if (vertexBuffer == null) { job.Dispose(); return null; }
                job.VertexBuffers[stream] = vertexBuffer;
                job.VertexStreams[stream] = AsyncGPUReadback.Request(vertexBuffer);
            }

            var indexBuffer = source.GetIndexBuffer();
            if (indexBuffer == null) { job.Dispose(); return null; }
            job.IndexBuffer = indexBuffer;
            job.IndexCount = indexBuffer.count;
            job.IndexStream = AsyncGPUReadback.Request(indexBuffer);
            return job;
        }

        // Drops the rebuilt copies and frees their GPU memory (ZNetScene teardown).
        public static void Clear()
        {
            foreach (var kv in _readableCopies)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _readableCopies.Clear();
            _unrecoverable.Clear();
        }

        // Owns the in-flight readback requests + the GPU buffers they read from,
        // kept alive until the readbacks complete.
        private sealed class ReadbackJob
        {
            public Mesh Source;
            public int VertexCount;
            public VertexAttributeDescriptor[] Attributes;
            public IndexFormat IndexFormat;
            public int IndexCount;

            public AsyncGPUReadbackRequest[] VertexStreams;
            public GraphicsBuffer[] VertexBuffers;
            public AsyncGPUReadbackRequest IndexStream;
            public GraphicsBuffer IndexBuffer;

            public bool AllDone()
            {
                for (int stream = 0; stream < VertexStreams.Length; stream++)
                    if (!VertexStreams[stream].done) return false;
                return IndexStream.done;
            }

            // Reconstructs a readable mesh from the raw readback bytes using the
            // source's vertex layout; null if any readback errored.
            public Mesh BuildCopy()
            {
                if (IndexStream.hasError) return null;
                for (int stream = 0; stream < VertexStreams.Length; stream++)
                    if (VertexStreams[stream].hasError) return null;

                var copy = new Mesh { name = (Source.name ?? "mesh") + "_readable" };
                copy.indexFormat = IndexFormat;

                copy.SetVertexBufferParams(VertexCount, Attributes);
                for (int stream = 0; stream < VertexStreams.Length; stream++)
                {
                    var vertexBytes = VertexStreams[stream].GetData<byte>();
                    copy.SetVertexBufferData(vertexBytes, 0, 0, vertexBytes.Length, stream, SilentMeshWrite);
                }

                var indexBytes = IndexStream.GetData<byte>();
                copy.SetIndexBufferParams(IndexCount, IndexFormat);
                copy.SetIndexBufferData(indexBytes, 0, 0, indexBytes.Length, SilentMeshWrite);

                int subMeshCount = Source.subMeshCount;
                copy.subMeshCount = subMeshCount;
                for (int sub = 0; sub < subMeshCount; sub++)
                    copy.SetSubMesh(sub, Source.GetSubMesh(sub), SilentMeshWrite);

                copy.bounds = Source.bounds;
                return copy;
            }

            public void Dispose()
            {
                if (VertexBuffers != null)
                {
                    for (int stream = 0; stream < VertexBuffers.Length; stream++)
                    {
                        try { VertexBuffers[stream]?.Dispose(); } catch { }
                        VertexBuffers[stream] = null;
                    }
                }
                try { IndexBuffer?.Dispose(); } catch { }
                IndexBuffer = null;
            }
        }
    }
}
