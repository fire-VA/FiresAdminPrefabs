using System;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Sync
{
    // The pull's file blocks ride each peer's own ZRpc instead of ZRoutedRpc (FAP 1.1.139). A routed block is copied four
    // to five times on its way out, each copy into a stream that grows by doubling: about 4 MB of large-object garbage per
    // 250 KB block, which held the dedi's heap at 92-99% with 2-3.5 s stalls while a first join pulled the 3 GB library
    // (2026-09-27). A direct block is written straight into ZRpc's reused per-peer package and copied once, by the socket.
    // Begin and End stay routed; both travel the same socket, so the order holds. The server says hello on the direct
    // channel before the delta list and a client asks for direct blocks only after hearing it, so either side may be older.
    public static class VAFapDirectPull
    {
        public const string RPC_HELLO = "FiresAdminPrefabs AssetSync_DirectHello";
        public const string RPC_REQUEST = "FiresAdminPrefabs AssetSync_DirectFileRequest";
        public const string RPC_BLOCK = "FiresAdminPrefabs AssetSync_DirectFileBlock";

        public static bool ServerOffersDirect { get; private set; }

        public sealed class Block : ISerializableParameter
        {
            public string RelPath;
            public int Index;
            public byte[] Bytes;
            // 1.1.246 ([fgn], Fire's profiler: this RPC was 41 % of all managed allocation): on receive, Bytes is a REUSED
            // buffer and only the first Count bytes are this block. Valid until the handler returns (main thread, one at a time).
            public int Count = -1;

            public void Serialize(ref ZPackage pkg)
            {
                pkg.Write(RelPath ?? string.Empty);
                pkg.Write(Index);
                pkg.Write(Bytes);   // int length + bytes, the same wire format ReadByteArray reads
            }

            public void Deserialize(ref ZPackage pkg)
            {
                RelPath = pkg.ReadString();
                Index = pkg.ReadInt();
                var reader = Reader != null ? Reader(pkg) : null;
                if (reader == null) { Bytes = pkg.ReadByteArray(); Count = Bytes?.Length ?? 0; return; }
                int count = reader.ReadInt32();
                if (count < 0) count = 0;
                if (s_pool == null || s_pool.Length < count) s_pool = new byte[Math.Max(count, 256 * 1024)];
                int read = 0;
                while (read < count)
                {
                    int n = reader.Read(s_pool, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
                Bytes = s_pool;
                Count = read;
            }
        }

        // Safe to share only because the RPC handler (Client_HandleFileChunk -> StreamedFileReceiver.Append) writes the block to
        // the FileStream and the hasher synchronously before returning: no queue, no async write holds this array, and ZRpc
        // deserialises the next block only after this handler returned. Keep it that way, or copy before deferring.
        private static byte[] s_pool;
        private static readonly AccessTools.FieldRef<ZPackage, System.IO.BinaryReader> Reader = TryReader();
        private static AccessTools.FieldRef<ZPackage, System.IO.BinaryReader> TryReader()
        {
            try { return AccessTools.FieldRefAccess<ZPackage, System.IO.BinaryReader>("m_reader"); } catch { return null; }
        }

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"),
                postfix: new HarmonyMethod(typeof(VAFapDirectPull), nameof(ZNet_OnNewConnection_Postfix)));
        }

        private static void ZNet_OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (peer?.m_rpc == null) return;
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<string>(RPC_REQUEST, (rpc, relPath) =>
                    Guarded("DirectFileRequest", () => VAFapAssetSync.Server_HandleFileRequest(peer.m_uid, relPath, direct: true)));
                return;
            }
            ServerOffersDirect = false;
            peer.m_rpc.Register<bool>(RPC_HELLO, (rpc, offered) =>
            {
                ServerOffersDirect = offered;
                Debug.Log($"[FAP Sync] Server {(offered ? "offers" : "declines")} direct file blocks.");
            });
            peer.m_rpc.Register<Block>(RPC_BLOCK, (rpc, block) =>
                Guarded("DirectFileBlock", () => VAFapAssetSync.Client_HandleFileChunk(block.RelPath, block.Index, block.Bytes,
                    block.Count >= 0 ? block.Count : block.Bytes?.Length ?? 0)));
        }

        private static void Guarded(string handlerName, Action handle)
        {
            try { handle(); }
            catch (Exception ex) { Debug.LogWarning($"[FAP Sync] {handlerName} threw: {ex.Message}"); }
        }

        public static void SayHello(ZNetPeer peer)
        {
            if (peer?.m_rpc != null && peer.m_socket != null && peer.m_socket.IsConnected()) peer.m_rpc.Invoke(RPC_HELLO, true);
        }

        public static void Request(ZNetPeer serverPeer, string relPath) => serverPeer?.m_rpc?.Invoke(RPC_REQUEST, relPath ?? string.Empty);

        public static void Send(ZNetPeer peer, Block block) => peer.m_rpc.Invoke(RPC_BLOCK, block);
    }
}
