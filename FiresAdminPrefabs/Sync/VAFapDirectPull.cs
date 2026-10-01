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

            public void Serialize(ref ZPackage pkg)
            {
                pkg.Write(RelPath ?? string.Empty);
                pkg.Write(Index);
                pkg.Write(Bytes);
            }

            public void Deserialize(ref ZPackage pkg)
            {
                RelPath = pkg.ReadString();
                Index = pkg.ReadInt();
                Bytes = pkg.ReadByteArray();
            }
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
                Guarded("DirectFileBlock", () => VAFapAssetSync.Client_HandleFileChunk(block.RelPath, block.Index, block.Bytes)));
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
