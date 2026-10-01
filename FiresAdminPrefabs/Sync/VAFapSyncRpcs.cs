using BepInEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Sync
{
    // Registers the asset-sync RPCs and forwards decoded payloads to VAFapAssetSync.
    // No orchestration lives here.
    //
    // Pull (server → every client, un-gated):
    //   ClientManifest  client → server   the client's Manifest
    //   DeltaList       server → client   Manifest holding only the entries the client needs
    //   FileRequest     client → server   relPath
    //   FileBegin/Chunk/End                streamed file blocks
    //   TransferDone    server → client   sentinel
    //
    // Admin push (client → server, admin-gated and path-allowlisted server-side):
    //   AdminPushBegin/Chunk/End           streamed file blocks
    //   AdminPushDone   client → server   file count, triggers the server reload
    //   AdminPushAck    server → client   ok + message
    //
    // ZRoutedRpc (not peer.m_rpc) so the server can target one client by uid after
    // the handshake, and the client→server case works the same way.
    public static class VAFapSyncRpcs
    {
        public const string RPC_CLIENT_MANIFEST  = "FiresAdminPrefabs AssetSync_ClientManifest";
        public const string RPC_DELTA_LIST       = "FiresAdminPrefabs AssetSync_DeltaList";
        public const string RPC_FILE_REQUEST     = "FiresAdminPrefabs AssetSync_FileRequest";
        public const string RPC_FILE_BEGIN       = "FiresAdminPrefabs AssetSync_FileBegin";
        public const string RPC_FILE_CHUNK       = "FiresAdminPrefabs AssetSync_FileChunk";
        public const string RPC_FILE_END         = "FiresAdminPrefabs AssetSync_FileEnd";
        public const string RPC_TRANSFER_DONE    = "FiresAdminPrefabs AssetSync_TransferDone";
        public const string RPC_ADMIN_PUSH_BEGIN = "FiresAdminPrefabs AssetSync_AdminPushBegin";
        public const string RPC_ADMIN_PUSH_CHUNK = "FiresAdminPrefabs AssetSync_AdminPushChunk";
        public const string RPC_ADMIN_PUSH_END   = "FiresAdminPrefabs AssetSync_AdminPushEnd";
        public const string RPC_ADMIN_PUSH_DONE  = "FiresAdminPrefabs AssetSync_AdminPushDone";
        public const string RPC_ADMIN_PUSH_ACK   = "FiresAdminPrefabs AssetSync_AdminPushAck";
        public const string RPC_BAKED_INDEX      = "FiresAdminPrefabs AssetSync_BakedIndex";

        private const byte TransferDoneSentinel = 0;

        // ZRoutedRpc is recreated on every (re)connect with an empty handler table,
        // so the guard is the instance itself, not a process-wide bool.
        private static ZRoutedRpc _registeredOn;

        public static void RegisterAll()
        {
            var routedRpc = ZRoutedRpc.instance;
            if (routedRpc == null) return;
            if (ReferenceEquals(_registeredOn, routedRpc)) return;

            try
            {
                routedRpc.Register<ZPackage>(RPC_CLIENT_MANIFEST, OnClientManifest);
                routedRpc.Register<ZPackage>(RPC_DELTA_LIST, OnDeltaList);
                routedRpc.Register<ZPackage>(RPC_FILE_REQUEST, OnFileRequest);
                routedRpc.Register<ZPackage>(RPC_FILE_BEGIN, OnFileBegin);
                routedRpc.Register<ZPackage>(RPC_FILE_CHUNK, OnFileChunk);
                routedRpc.Register<ZPackage>(RPC_FILE_END, OnFileEnd);
                routedRpc.Register<ZPackage>(RPC_TRANSFER_DONE, OnTransferDone);
                routedRpc.Register<ZPackage>(RPC_ADMIN_PUSH_BEGIN, OnAdminPushBegin);
                routedRpc.Register<ZPackage>(RPC_ADMIN_PUSH_CHUNK, OnAdminPushChunk);
                routedRpc.Register<ZPackage>(RPC_ADMIN_PUSH_END, OnAdminPushEnd);
                routedRpc.Register<ZPackage>(RPC_ADMIN_PUSH_DONE, OnAdminPushDone);
                routedRpc.Register<ZPackage>(RPC_ADMIN_PUSH_ACK, OnAdminPushAck);
                routedRpc.Register<ZPackage>(RPC_BAKED_INDEX, OnBakedIndex);
                _registeredOn = routedRpc;
                Debug.Log($"[FAP Sync] Registered asset-sync RPCs (ZRoutedRpc #{routedRpc.GetHashCode()}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] RPC register failed: {ex.Message}");
            }
        }

        // ---- Server-side handlers ----

        // Reassembled here; inflated, parsed and compared on a worker (VAFapAssetSync.Server_HandleClientManifest).
        private static void OnClientManifest(long sender, ZPackage pkg)
        {
            if (!IsServer()) return;
            Forward("OnClientManifest", sender, () =>
            {
                byte[] raw = VAFapSyncChunker.ReceiveFragmentRaw(sender, pkg, out bool compressed);
                if (raw != null) VAFapAssetSync.Server_HandleClientManifest(sender, raw, compressed);
            });
        }

        private static void OnFileRequest(long sender, ZPackage pkg)
        {
            if (!IsServer()) return;
            ForwardReassembled("OnFileRequest", sender, pkg, payload =>
                VAFapAssetSync.Server_HandleFileRequest(sender, new ZPackage(payload).ReadString()));
        }

        private static void OnAdminPushBegin(long sender, ZPackage pkg)
        {
            if (!IsServer()) return;
            Forward("OnAdminPushBegin", sender, () =>
            {
                string relPath = pkg.ReadString();
                string sha256 = pkg.ReadString();
                long size = pkg.ReadLong();
                int chunkCount = pkg.ReadInt();
                VAFapAssetSync.Server_HandleAdminPushBegin(sender, relPath, sha256, size, chunkCount);
            });
        }

        private static void OnAdminPushChunk(long sender, ZPackage pkg)
        {
            if (!IsServer()) return;
            Forward("OnAdminPushChunk", sender, () =>
            {
                string relPath = pkg.ReadString();
                int index = pkg.ReadInt();
                byte[] bytes = pkg.ReadByteArray();
                VAFapAssetSync.Server_HandleAdminPushChunk(sender, relPath, index, bytes);
            });
        }

        private static void OnAdminPushEnd(long sender, ZPackage pkg)
        {
            if (!IsServer()) return;
            Forward("OnAdminPushEnd", sender, () =>
            {
                string relPath = pkg.ReadString();
                bool ok = pkg.ReadBool();
                VAFapAssetSync.Server_HandleAdminPushEnd(sender, relPath, ok);
            });
        }

        private static void OnAdminPushDone(long sender, ZPackage pkg)
        {
            if (!IsServer()) return;
            ForwardReassembled("OnAdminPushDone", sender, pkg, payload =>
                VAFapAssetSync.Server_HandleAdminPushDone(sender, new ZPackage(payload).ReadInt()));
        }

        // ---- Client-side handlers ----

        private static void OnDeltaList(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            ForwardReassembled("OnDeltaList", sender, pkg, payload =>
                VAFapAssetSync.Client_HandleDeltaList(VAFapSyncManifest.Deserialize(payload).Entries));
        }

        private static void OnFileBegin(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            Forward("OnFileBegin", sender, () =>
            {
                string relPath = pkg.ReadString();
                string sha256 = pkg.ReadString();
                long size = pkg.ReadLong();
                int chunkCount = pkg.ReadInt();
                VAFapAssetSync.Client_HandleFileBegin(relPath, sha256, size, chunkCount);
            });
        }

        private static void OnFileChunk(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            Forward("OnFileChunk", sender, () =>
            {
                string relPath = pkg.ReadString();
                int index = pkg.ReadInt();
                byte[] bytes = pkg.ReadByteArray();
                VAFapAssetSync.Client_HandleFileChunk(relPath, index, bytes);
            });
        }

        private static void OnFileEnd(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            Forward("OnFileEnd", sender, () =>
            {
                string relPath = pkg.ReadString();
                bool ok = pkg.ReadBool();
                VAFapAssetSync.Client_HandleFileEnd(relPath, ok);
            });
        }

        private static void OnTransferDone(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            Forward("OnTransferDone", sender, VAFapAssetSync.Client_HandleTransferDone);
        }

        private static void OnAdminPushAck(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            ForwardReassembled("OnAdminPushAck", sender, pkg, payload =>
            {
                var inner = new ZPackage(payload);
                bool ok = inner.ReadBool();
                string message = inner.ReadString();
                VAFapAssetSync.Client_HandleAdminPushAck(ok, message);
            });
        }

        private static void OnBakedIndex(long sender, ZPackage pkg)
        {
            if (IsServer()) return;
            ForwardReassembled("OnBakedIndex", sender, pkg, payload =>
                VAFapAssetSync.Client_HandleBakedIndex(VAFapSyncManifest.Deserialize(payload).Entries));
        }

        // Reassembles a fragmented payload first; returns silently while fragments
        // are still outstanding.
        private static void ForwardReassembled(string handlerName, long sender, ZPackage pkg, Action<byte[]> handle)
        {
            byte[] payload = VAFapSyncChunker.ReceiveFragment(sender, pkg);
            if (payload == null) return;
            Forward(handlerName, sender, () => handle(payload));
        }

        private static void Forward(string handlerName, long sender, Action handle)
        {
            try { handle(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FAP Sync] {handlerName} from {sender} threw: {ex.Message}");
            }
        }

        // ---- Outbound ----

        public static IEnumerator Client_SendManifest(ZNetPeer serverPeer, VAFapSyncManifest.Manifest manifest)
        {
            byte[] data = VAFapSyncManifest.Serialize(manifest);
            return VAFapSyncChunker.SendToPeer(serverPeer, RPC_CLIENT_MANIFEST, data, compress: true);
        }

        public static IEnumerator Client_RequestFile(ZNetPeer serverPeer, string relPath)
        {
            var inner = new ZPackage();
            inner.Write(relPath ?? string.Empty);
            return VAFapSyncChunker.SendToPeer(serverPeer, RPC_FILE_REQUEST, inner.GetArray(), compress: false);
        }

        // The delta rides in a Manifest so both directions share one wire shape.
        public static IEnumerator Server_SendDeltaList(ZNetPeer clientPeer, List<VAFapSyncManifest.ManifestEntry> delta)
        {
            var wire = new VAFapSyncManifest.Manifest
            {
                Entries = delta ?? new List<VAFapSyncManifest.ManifestEntry>(),
            };
            byte[] data = VAFapSyncManifest.Serialize(wire);
            return VAFapSyncChunker.SendToPeer(clientPeer, RPC_DELTA_LIST, data, compress: true);
        }

        public static IEnumerator Server_SendBakedIndex(ZNetPeer clientPeer, List<VAFapSyncManifest.ManifestEntry> bundles)
        {
            var wire = new VAFapSyncManifest.Manifest
            {
                Entries = bundles ?? new List<VAFapSyncManifest.ManifestEntry>(),
            };
            return VAFapSyncChunker.SendToPeer(clientPeer, RPC_BAKED_INDEX, VAFapSyncManifest.Serialize(wire), compress: true);
        }

        public static IEnumerator Server_SendTransferDone(ZNetPeer clientPeer)
        {
            var inner = new ZPackage();
            inner.Write(TransferDoneSentinel);
            return VAFapSyncChunker.SendToPeer(clientPeer, RPC_TRANSFER_DONE, inner.GetArray(), compress: false);
        }

        public static IEnumerator Client_StreamAdminFile(ZNetPeer serverPeer, string relPath, string sha256, string absPath,
                                                        VAFapSyncChunker.StreamOutcome outcome, Func<bool> cancelled)
        {
            return VAFapSyncChunker.StreamFileToPeer(serverPeer, relPath, sha256, absPath,
                RPC_ADMIN_PUSH_BEGIN, RPC_ADMIN_PUSH_CHUNK, RPC_ADMIN_PUSH_END, outcome, cancelled);
        }

        public static IEnumerator Client_SendAdminPushDone(ZNetPeer serverPeer, int fileCount)
        {
            var inner = new ZPackage();
            inner.Write(fileCount);
            return VAFapSyncChunker.SendToPeer(serverPeer, RPC_ADMIN_PUSH_DONE, inner.GetArray(), compress: false);
        }

        public static IEnumerator Server_SendAdminPushAck(ZNetPeer clientPeer, bool ok, string message)
        {
            var inner = new ZPackage();
            inner.Write(ok);
            inner.Write(message ?? string.Empty);
            return VAFapSyncChunker.SendToPeer(clientPeer, RPC_ADMIN_PUSH_ACK, inner.GetArray(), compress: false);
        }

        private static bool IsServer()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        public static ZNetPeer GetServerPeer()
        {
            var peers = VAFapSyncChunker.GetRoutedPeers();
            if (peers == null) return null;
            for (int i = 0; i < peers.Count; i++)
                if (peers[i] != null && peers[i].m_server) return peers[i];
            return null;
        }

        public static ZNetPeer GetPeerByUid(long uid)
        {
            var peers = VAFapSyncChunker.GetRoutedPeers();
            if (peers == null) return null;
            for (int i = 0; i < peers.Count; i++)
                if (peers[i] != null && peers[i].m_uid == uid) return peers[i];
            return null;
        }
    }
}
