using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace VerdantsAscent.Sync
{
    // Transport for the asset-sync RPCs: fragments arbitrary byte[] payloads over
    // routed RPCs (manifests, delta lists, sentinels) and streams whole files
    // disk-to-disk in either direction. Same chunk size and reassembly TTL as the
    // live ConfigSync chunker, but carrying raw payloads instead of config shapes.
    //
    // Fragment wire format:  byte flags | long packageId | int index | int count | byte[] payload
    // Single-shot format:    byte flags | byte[] payload
    public static class VAFapSyncChunker
    {
        private const byte FLAG_COMPRESSED = 1;
        private const byte FLAG_FRAGMENTED = 2;

        public const int ChunkSize = 250000;
        private const int CompressionThreshold = 10000;
        private const int FileReadBufferBytes = 1048576;

        // ISocket.GetSendQueueSize is NOT a local backlog — ZSteamSocket adds
        // m_cbSentUnackedReliable, i.e. bytes already on the wire, which is the
        // bandwidth-delay product and grows with the link rate. FGN moves that rate
        // (2 MB/s by default, 16-32 MB/s on the AutoTune tiers, against vanilla's
        // 150 KB/s) and moves Steam's SendBufferSize with it, so ANY fixed byte mark
        // is a guess against a moving target: the old 131072 sat below the in-flight
        // floor of a fast link and could never be reached, which is what aborted
        // healthy transfers. The gate is relative instead — see AwaitDrain.

        // Below one block there is nothing worth waiting for; waiting would idle the wire.
        public const int StreamMinOutstandingBytes = ChunkSize;

        // The queue fell and then levelled off: that plateau IS the in-flight floor,
        // and no amount of waiting removes bytes that are already moving.
        private const float StreamFloorSettleSec = 2f;

        // Only fires when the queue has not dropped AT ALL for this long. Sitting
        // high is backpressure and is normal; not moving at all is a dead socket.
        public const float StreamStallTimeoutSec = 60f;

        // Small payloads (manifests, delta lists, sentinels) only need to avoid
        // piling onto a busy socket; they proceed regardless on timeout.
        public const int SmallPayloadHighWaterBytes = ChunkSize;

        private const float StreamProgressIntervalSec = 10f;
        private const long StreamProgressMinBytes = 8 * 1024 * 1024;

        private const long ReassemblyTtlTicks = 60 * TimeSpan.TicksPerSecond;

        private static readonly Dictionary<string, SortedDictionary<int, byte[]>> _reassemblyCache
            = new Dictionary<string, SortedDictionary<int, byte[]>>();
        private static readonly List<KeyValuePair<long, string>> _cacheExpirations
            = new List<KeyValuePair<long, string>>();

        private static long _packageCounter = 0;

        // Deflate helps JSON manifests (5-10x) and is wasted on AssetBundles, which
        // are already LZ4 inside — the caller decides.
        public static IEnumerator SendToPeer(ZNetPeer peer, string rpcName, byte[] payload, bool compress)
        {
            if (peer == null || payload == null) yield break;
            if (!peer.IsReady()) yield break;

            byte[] data = payload;
            bool didCompress = false;

            if (compress && data.Length > CompressionThreshold)
            {
                try
                {
                    data = Deflate(data);
                    didCompress = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FAP Sync] Compress failed for {rpcName}: {ex.Message} — sending uncompressed.");
                    didCompress = false;
                    data = payload;
                }
            }

            if (data.Length > ChunkSize)
            {
                int count = (data.Length + ChunkSize - 1) / ChunkSize;
                long packageId = ++_packageCounter;

                for (int i = 0; i < count; i++)
                {
                    foreach (bool wait in WaitForQueue(peer))
                        yield return wait;

                    if (!peer.m_socket.IsConnected()) yield break;

                    var pkg = new ZPackage();
                    byte flags = FLAG_FRAGMENTED;
                    if (didCompress) flags |= FLAG_COMPRESSED;
                    pkg.Write(flags);
                    pkg.Write(packageId);
                    pkg.Write(i);
                    pkg.Write(count);

                    int offset = ChunkSize * i;
                    int sliceLen = Math.Min(ChunkSize, data.Length - offset);
                    byte[] slice = new byte[sliceLen];
                    Buffer.BlockCopy(data, offset, slice, 0, sliceLen);
                    pkg.Write(slice);

                    InvokeRpc(peer, rpcName, pkg);

                    if (i < count - 1) yield return true;
                }
            }
            else
            {
                foreach (bool wait in WaitForQueue(peer))
                    yield return wait;

                var pkg = new ZPackage();
                byte flags = 0;
                if (didCompress) flags |= FLAG_COMPRESSED;
                pkg.Write(flags);
                pkg.Write(data);
                InvokeRpc(peer, rpcName, pkg);
            }
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    deflate.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        internal static byte[] Inflate(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var output = new MemoryStream())
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        // What a caller needs to know after the stream ends. A torn transfer used to
        // be indistinguishable from a complete one, so a failed push was reported as
        // a success and never retried.
        public sealed class StreamOutcome
        {
            public bool Completed;
            public long BytesSent;
            public float Seconds;
            public string Failure;
        }

        // Streams a file as BEGIN / CHUNK* / END, one block read and enqueued per
        // frame so neither side holds the file in memory and the keep-alive ping
        // keeps flowing. Blocks go raw: re-Deflating LZ4 bundles was a main-thread
        // stall that disconnected players. A dead socket aborts THIS file only.
        //
        // The RPC triple defaults to the pull direction; the admin push passes its
        // own gated handlers. 'cancelled' lets the far side stop a doomed transfer
        // instead of taking hundreds of MB it is going to discard.
        public static IEnumerator StreamFileToPeer(ZNetPeer peer, string relPath, string sha256, string absPath,
                                                   string rpcBegin = null, string rpcChunk = null, string rpcEnd = null,
                                                   StreamOutcome outcome = null, Func<bool> cancelled = null,
                                                   bool direct = false)
        {
            rpcBegin = rpcBegin ?? VAFapSyncRpcs.RPC_FILE_BEGIN;
            rpcChunk = rpcChunk ?? VAFapSyncRpcs.RPC_FILE_CHUNK;
            rpcEnd = rpcEnd ?? VAFapSyncRpcs.RPC_FILE_END;

            if (outcome != null) { outcome.Completed = false; outcome.BytesSent = 0; outcome.Seconds = 0f; outcome.Failure = null; }

            if (peer == null || peer.m_socket == null || !peer.m_socket.IsConnected())
            {
                if (outcome != null) outcome.Failure = "not connected";
                yield break;
            }

            FileStream fileStream = null;
            long size = 0;
            int chunkCount = 0;
            try
            {
                fileStream = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                            FileReadBufferBytes, FileOptions.SequentialScan);
                size = fileStream.Length;
                chunkCount = size == 0 ? 0 : (int)((size + ChunkSize - 1) / ChunkSize);
            }
            catch (Exception ex)
            {
                if (fileStream != null) { try { fileStream.Dispose(); } catch { } }
                Debug.LogWarning($"[FAP Sync] Stream open failed '{absPath}': {ex.Message}");
                if (outcome != null) outcome.Failure = "open failed: " + ex.Message;
                SendFileEnd(peer, rpcEnd, relPath, ok: false);
                yield break;
            }

            string failure = null;
            long sentBytes = 0;
            float startedAt = Time.realtimeSinceStartup;
            using (fileStream)
            {
                var begin = new ZPackage();
                begin.Write(relPath ?? string.Empty);
                begin.Write(sha256 ?? string.Empty);
                begin.Write(size);
                begin.Write(chunkCount);
                InvokeRpc(peer, rpcBegin, begin);

                var buffer = new byte[ChunkSize];
                var block = direct ? new VAFapDirectPull.Block { RelPath = relPath } : null;
                var timedOut = new bool[1];
                int index = 0;
                float nextReport = startedAt + StreamProgressIntervalSec;

                // Nothing is outstanding yet, so the first block goes straight out;
                // peak int.MaxValue keeps the floor-plateau exit armed from the start.
                int drainTarget = StreamMinOutstandingBytes;
                int drainPeak = int.MaxValue;
                while (true)
                {
                    int read;
                    try { read = fileStream.Read(buffer, 0, ChunkSize); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[FAP Sync] Stream read failed '{relPath}': {ex.Message}");
                        failure = "read failed: " + ex.Message; break;
                    }
                    if (read <= 0) break;

                    timedOut[0] = false;
                    var wait = AwaitDrain(peer, drainTarget, drainPeak, timedOut);
                    while (wait.MoveNext()) yield return wait.Current;
                    if (timedOut[0]) { failure = "send queue stopped draining"; break; }
                    if (peer.m_socket == null || !peer.m_socket.IsConnected()) { failure = "peer disconnected"; break; }
                    if (cancelled != null && cancelled()) { failure = "cancelled by the receiver"; break; }

                    int queuedBefore = SafeQueueSize(peer);

                    byte[] payload = buffer;
                    if (read < ChunkSize)
                    {
                        payload = new byte[read];
                        Buffer.BlockCopy(buffer, 0, payload, 0, read);
                    }
                    if (direct)
                    {
                        block.Index = index;
                        block.Bytes = payload;
                        VAFapDirectPull.Send(peer, block);
                    }
                    else
                    {
                        var chunk = new ZPackage();
                        chunk.Write(relPath ?? string.Empty);
                        chunk.Write(index);
                        chunk.Write(payload);
                        InvokeRpc(peer, rpcChunk, chunk);
                    }
                    index++;
                    sentBytes += read;

                    drainPeak = SafeQueueSize(peer);
                    drainTarget = Math.Max(StreamMinOutstandingBytes, queuedBefore);

                    if (size >= StreamProgressMinBytes && Time.realtimeSinceStartup >= nextReport)
                    {
                        nextReport = Time.realtimeSinceStartup + StreamProgressIntervalSec;
                        ReportProgress(relPath, sentBytes, size, Time.realtimeSinceStartup - startedAt);
                    }

                    yield return null;
                }
            }

            float seconds = Time.realtimeSinceStartup - startedAt;
            if (outcome != null)
            {
                outcome.Completed = failure == null;
                outcome.BytesSent = sentBytes;
                outcome.Seconds = seconds;
                outcome.Failure = failure;
            }

            if (failure == null && size >= StreamProgressMinBytes)
                Debug.Log($"[FAP Sync] '{relPath}' sent — {VAFapAssetSync.FormatBytes(sentBytes)} in "
                    + $"{FormatDuration(seconds)} ({FormatRate(sentBytes, seconds)}, {(direct ? "direct" : "routed")} blocks).");

            if (peer.m_socket != null && peer.m_socket.IsConnected())
                SendFileEnd(peer, rpcEnd, relPath, ok: failure == null);
        }

        private static void ReportProgress(string relPath, long sent, long size, float elapsed)
        {
            double perSec = elapsed > 0.001f ? sent / elapsed : 0.0;
            string eta = perSec > 1.0
                ? FormatDuration((float)((size - sent) / perSec)) + " left"
                : "no estimate yet";
            Debug.Log($"[FAP Sync] '{relPath}' {VAFapAssetSync.FormatBytes(sent)} of "
                + $"{VAFapAssetSync.FormatBytes(size)} ({(100.0 * sent / Math.Max(1L, size)):F1}%), "
                + $"{FormatRate(sent, elapsed)}, {eta}.");
        }

        private static string FormatRate(long bytes, float seconds) =>
            seconds > 0.001f ? $"{VAFapAssetSync.FormatBytes((long)(bytes / seconds))}/s" : "—";

        private static string FormatDuration(float seconds)
        {
            if (seconds < 90f) return $"{seconds:F0}s";
            if (seconds < 5400f) return $"{seconds / 60f:F0}m";
            return $"{seconds / 3600f:F1}h";
        }

        // Rate-matching without knowing the rate: 'target' is what the queue measured
        // immediately BEFORE the previous block went in, so returning to it means the
        // socket has absorbed a block's worth and we may add another. Measured, not
        // assumed, so it survives FGN compressing a block to 89% of its size and any
        // send rate FGN's tiers pick.
        //
        // Three exits. Reaching the target is the normal one. A queue that falls and
        // then plateaus has hit the link's in-flight floor, which the target may sit
        // below on a fast link — proceed, because those bytes are moving. A queue that
        // never falls at all is the only real wedge.
        private static IEnumerator AwaitDrain(ZNetPeer peer, int target, int peak, bool[] timedOut)
        {
            int lowest = int.MaxValue;
            float lastDrop = Time.time;
            while (peer != null && peer.m_socket != null && peer.m_socket.IsConnected())
            {
                int queued = peer.m_socket.GetSendQueueSize();
                if (queued <= target) yield break;

                if (queued < lowest)
                {
                    lowest = queued;
                    lastDrop = Time.time;
                }
                else if (lowest < peak && Time.time - lastDrop > StreamFloorSettleSec)
                {
                    yield break;
                }
                else if (Time.time - lastDrop > StreamStallTimeoutSec)
                {
                    Debug.LogWarning($"[FAP Sync] Peer {peer.m_uid} send queue stuck at {queued} bytes with no drain "
                        + $"for {StreamStallTimeoutSec:F0}s; aborting this file (player stays connected).");
                    timedOut[0] = true;
                    yield break;
                }
                yield return null;
            }
        }

        private static int SafeQueueSize(ZNetPeer peer)
        {
            try
            {
                if (peer?.m_socket == null || !peer.m_socket.IsConnected()) return 0;
                return peer.m_socket.GetSendQueueSize();
            }
            catch { return 0; }
        }

        // A request the sender cannot serve at all still owes the receiver an answer:
        // with no End the receiver's per-file loop has nothing to advance on.
        public static void SendFileUnavailable(ZNetPeer peer, string relPath)
            => SendFileEnd(peer, VAFapSyncRpcs.RPC_FILE_END, relPath, ok: false);

        private static void SendFileEnd(ZNetPeer peer, string rpcEnd, string relPath, bool ok)
        {
            try
            {
                var end = new ZPackage();
                end.Write(relPath ?? string.Empty);
                end.Write(ok);
                InvokeRpc(peer, rpcEnd, end);
            }
            catch (Exception ex) { Debug.LogWarning($"[FAP Sync] SendFileEnd failed '{relPath}': {ex.Message}"); }
        }

        // On timeout the small payloads proceed best-effort; force-disconnecting
        // here was a root cause of the sync disconnects.
        private static IEnumerable<bool> WaitForQueue(ZNetPeer peer)
        {
            float timeout = Time.time + StreamStallTimeoutSec;
            while (peer.m_socket != null && peer.m_socket.GetSendQueueSize() > SmallPayloadHighWaterBytes)
            {
                if (Time.time > timeout)
                {
                    Debug.LogWarning($"[FAP Sync] Peer {peer.m_uid} send queue slow to drain "
                        + $"(>{StreamStallTimeoutSec:F0}s); proceeding without disconnect.");
                    yield break;
                }
                yield return false;
            }
        }

        // Handlers live on ZRoutedRpc, so both directions MUST send through it — a
        // direct peer.m_rpc.Invoke is dropped as an unknown key. The client reaches
        // the server at target 0L; the server targets a client by uid.
        private static void InvokeRpc(ZNetPeer peer, string rpcName, ZPackage pkg)
        {
            try
            {
                if (peer == null || ZRoutedRpc.instance == null) return;
                long target = peer.m_server ? 0L : peer.m_uid;
                ZRoutedRpc.instance.InvokeRoutedRPC(target, rpcName, pkg);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] InvokeRpc failed ({rpcName} → {peer?.m_uid}): {ex.Message}");
            }
        }

        // Returns the complete payload once every fragment has arrived, or null
        // while more are expected. Reassembly state is scoped per sender.
        public static byte[] ReceiveFragment(long sender, ZPackage package)
        {
            byte[] payload = ReceiveFragmentRaw(sender, package, out bool compressed);
            if (payload == null || !compressed) return payload;
            try { return Inflate(payload); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] Decompress failed: {ex.Message} — discarding payload.");
                return null;
            }
        }

        // The same, still compressed when it came compressed, for a caller that inflates it on a worker.
        public static byte[] ReceiveFragmentRaw(long sender, ZPackage package, out bool compressed)
        {
            compressed = false;
            if (package == null) return null;

            ExpireStaleReassemblies();

            byte flags = package.ReadByte();

            byte[] payload;
            if ((flags & FLAG_FRAGMENTED) != 0)
            {
                long packageId = package.ReadLong();
                string cacheKey = sender.ToString() + ":" + packageId.ToString();

                if (!_reassemblyCache.TryGetValue(cacheKey, out var fragments))
                {
                    fragments = new SortedDictionary<int, byte[]>();
                    _reassemblyCache[cacheKey] = fragments;
                    _cacheExpirations.Add(new KeyValuePair<long, string>(
                        DateTimeOffset.Now.Ticks + ReassemblyTtlTicks, cacheKey));
                }

                int index = package.ReadInt();
                int count = package.ReadInt();
                fragments[index] = package.ReadByteArray();

                if (fragments.Count < count) return null;

                _reassemblyCache.Remove(cacheKey);
                payload = Concatenate(fragments.Values);
            }
            else
            {
                payload = package.ReadByteArray();
            }

            compressed = (flags & FLAG_COMPRESSED) != 0;
            return payload;
        }

        // The first DeflateStream in the process loads Mono's native zlib (MonoPosixHelper) and JITs the stream code. Run
        // once on a worker when the server's manifest is built, so a first join never pays it on the main thread
        // ([perf] R22b: the bot's manifest froze the dedi 1,028 ms, blocked with 16 ms of CPU, the disk busy with the bot).
        public static double WarmCompression()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Inflate(Deflate(new byte[4096]));
            return watch.Elapsed.TotalMilliseconds;
        }

        // A client's manifest is reassembled here on the main thread, inside the join's routed RPC. HR2's dedi froze 902 ms in
        // it at the first join, blocked with 0 ms of CPU and no GC (round_20261001-153130_dedi.log:1533), the same first use
        // as R22b's 1,028 ms; the second join cost nothing. Run once at server start, before anyone joins, it pages this code
        // and its first-use runtime state in (SortedDictionary<int, byte[]>, DateTimeOffset.Now's local time zone, the ZPackage
        // reads, Concatenate) while the server is idle: two fragments and one whole payload under a sender id no peer has.
        public static double WarmReassembly()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            const long WarmSender = long.MinValue, WarmPackage = long.MinValue;
            byte[] joined = null;
            for (int index = 0; index < 2; index++)
            {
                var fragment = new ZPackage();
                fragment.Write(FLAG_FRAGMENTED);
                fragment.Write(WarmPackage);
                fragment.Write(index);
                fragment.Write(2);
                fragment.Write(new byte[16]);
                fragment.SetPos(0);
                joined = ReceiveFragmentRaw(WarmSender, fragment, out _);
            }
            var whole = new ZPackage();
            whole.Write((byte)0);
            whole.Write(new byte[16]);
            whole.SetPos(0);
            byte[] single = ReceiveFragmentRaw(WarmSender, whole, out _);
            _reassemblyCache.Remove(WarmSender.ToString() + ":" + WarmPackage.ToString());
            if (joined == null || joined.Length != 32 || single == null || single.Length != 16)
                throw new InvalidOperationException("the warm-up payloads did not come back whole");
            return watch.Elapsed.TotalMilliseconds;
        }

        private static void ExpireStaleReassemblies()
        {
            long now = DateTimeOffset.Now.Ticks;
            _cacheExpirations.RemoveAll(expiration =>
            {
                if (expiration.Key >= now) return false;
                _reassemblyCache.Remove(expiration.Value);
                return true;
            });
        }

        private static byte[] Concatenate(IEnumerable<byte[]> fragments)
        {
            int totalLength = 0;
            foreach (var fragment in fragments) totalLength += fragment.Length;

            var result = new byte[totalLength];
            int writePos = 0;
            foreach (var fragment in fragments)
            {
                Buffer.BlockCopy(fragment, 0, result, writePos, fragment.Length);
                writePos += fragment.Length;
            }
            return result;
        }

        // Vanilla keeps ZRoutedRpc.m_peers private.
        public static List<ZNetPeer> GetRoutedPeers()
        {
            if (ZRoutedRpc.instance == null) return null;
            try
            {
                var field = AccessTools.Field(typeof(ZRoutedRpc), "m_peers");
                if (field == null) return null;
                return (List<ZNetPeer>)field.GetValue(ZRoutedRpc.instance);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] GetRoutedPeers reflection failed: {ex.Message}");
                return null;
            }
        }
    }
}
