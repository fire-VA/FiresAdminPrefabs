using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAZdoDataBlob — self-contained reader for the per-piece `data` column of
    // .blueprint files (the WorldEditCommands / ExpandWorldData DataEntry wire
    // format: a base64 ZPackage). Parsing it ourselves removes the EWD soft-dep
    // from the PLACEMENT path — blueprint data fidelity must not depend on
    // which optional mods are installed.
    //
    // Wire format (WorldEditCommands Data/DataEntry.cs Write/Load, verified
    // against source):
    //
    //   int flags
    //   0x001 floats      byte count, then count × { int keyHash; float }
    //   0x002 vec3s       byte count, then count × { int keyHash; Vector3 }
    //   0x004 quats       byte count, then count × { int keyHash; Quaternion }
    //   0x008 ints        byte count, then count × { int keyHash; int }
    //                     (ints + hashes + bools share this one section)
    //   0x040 longs       byte count, then count × { int keyHash; long }
    //   0x010 strings     byte count, then count × { int keyHash; string }
    //   0x080 byteArrays  byte count, then count × { int keyHash; byte[] }
    //   0x100 connection  byte type; int hash
    //   0x200 persistent  bool
    //   0x400 distant     bool
    //   0x800 priority    byte (ZDO.ObjectType)
    //
    // Read order is floats, vecs, quats, ints, LONGS, strings, byteArrays —
    // longs sit between ints and strings on the wire even though their flag
    // bit (0x40) is higher than strings' (0x10). Matches WEC exactly.
    //
    // Values are already resolved at save time (IH evaluates parameters before
    // serializing), so plain reads are lossless for blueprint data.
    public sealed class VAZdoDataBlob
    {
        public Dictionary<int, float> Floats;
        public Dictionary<int, Vector3> Vecs;
        public Dictionary<int, Quaternion> Quats;
        public Dictionary<int, int> Ints;
        public Dictionary<int, long> Longs;
        public Dictionary<int, string> Strings;
        public Dictionary<int, byte[]> ByteArrays;
        public ZDOExtraData.ConnectionType ConnectionType = ZDOExtraData.ConnectionType.None;
        public int ConnectionHash;
        public bool? Persistent;
        public bool? Distant;
        public ZDO.ObjectType? Priority;

        // Same two support keys VABundleDataApplier strips: the saved support
        // cache is stale for the new location, and vanilla recomputes it from
        // actual world conditions (Structure Tweaks re-grants 1e19 support for
        // infinite-health pieces on the first UpdateSupport tick).
        private static readonly int HashSupport = "support".GetStableHashCode();
        private static readonly int HashWntSupport = "WearNTear.support".GetStableHashCode();

        public static bool TryParse(string base64, out VAZdoDataBlob blob)
        {
            blob = null;
            if (string.IsNullOrEmpty(base64)) return false;
            try
            {
                var pkg = new ZPackage(base64);
                pkg.SetPos(0);
                var b = new VAZdoDataBlob();
                int flags = pkg.ReadInt();

                if ((flags & 0x001) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.Floats = new Dictionary<int, float>(count);
                    for (int i = 0; i < count; i++)
                        b.Floats[pkg.ReadInt()] = pkg.ReadSingle();
                }
                if ((flags & 0x002) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.Vecs = new Dictionary<int, Vector3>(count);
                    for (int i = 0; i < count; i++)
                        b.Vecs[pkg.ReadInt()] = pkg.ReadVector3();
                }
                if ((flags & 0x004) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.Quats = new Dictionary<int, Quaternion>(count);
                    for (int i = 0; i < count; i++)
                        b.Quats[pkg.ReadInt()] = pkg.ReadQuaternion();
                }
                if ((flags & 0x008) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.Ints = new Dictionary<int, int>(count);
                    for (int i = 0; i < count; i++)
                        b.Ints[pkg.ReadInt()] = pkg.ReadInt();
                }
                if ((flags & 0x040) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.Longs = new Dictionary<int, long>(count);
                    for (int i = 0; i < count; i++)
                        b.Longs[pkg.ReadInt()] = pkg.ReadLong();
                }
                if ((flags & 0x010) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.Strings = new Dictionary<int, string>(count);
                    for (int i = 0; i < count; i++)
                        b.Strings[pkg.ReadInt()] = pkg.ReadString();
                }
                if ((flags & 0x080) != 0)
                {
                    byte count = pkg.ReadByte();
                    b.ByteArrays = new Dictionary<int, byte[]>(count);
                    for (int i = 0; i < count; i++)
                        b.ByteArrays[pkg.ReadInt()] = pkg.ReadByteArray();
                }
                if ((flags & 0x100) != 0)
                {
                    b.ConnectionType = (ZDOExtraData.ConnectionType)pkg.ReadByte();
                    b.ConnectionHash = pkg.ReadInt();
                }
                if ((flags & 0x200) != 0)
                    b.Persistent = pkg.ReadBool();
                if ((flags & 0x400) != 0)
                    b.Distant = pkg.ReadBool();
                if ((flags & 0x800) != 0)
                    b.Priority = (ZDO.ObjectType)pkg.ReadByte();

                blob = b;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAZdoDataBlob.TryParse failed ({ex.Message}) — falling back to EWD applier if present.");
                blob = null;
                return false;
            }
        }

        // Writes every stored value onto the ZDO. Meant to run on
        // ZNetView.m_initZDO BEFORE Instantiate so component Awake (WearNTear
        // health/support, Structure Tweaks override_* reads, vanilla
        // LoadFields "HasFields" overrides, sign text, container payloads)
        // sees the blueprint's saved state — the post-Awake apply this
        // replaces is exactly why infinite-health floating builds collapsed.
        public void WriteTo(ZDO zdo)
        {
            if (zdo == null) return;

            if (Floats != null)
            {
                foreach (var kv in Floats)
                {
                    if (kv.Key == HashSupport || kv.Key == HashWntSupport) continue;
                    zdo.Set(kv.Key, kv.Value);
                }
            }
            if (Vecs != null)
                foreach (var kv in Vecs) zdo.Set(kv.Key, kv.Value);
            if (Quats != null)
                foreach (var kv in Quats) zdo.Set(kv.Key, kv.Value);
            if (Ints != null)
                foreach (var kv in Ints) zdo.Set(kv.Key, kv.Value);
            if (Longs != null)
                foreach (var kv in Longs) zdo.Set(kv.Key, kv.Value);
            if (Strings != null)
                foreach (var kv in Strings) zdo.Set(kv.Key, kv.Value ?? string.Empty);
            if (ByteArrays != null)
                foreach (var kv in ByteArrays) zdo.Set(kv.Key, kv.Value);

            VAPre10ZdoUpgrade.Apply(zdo);
        }

        // Port of WEC DataEntry.HandleHashConnection: registers this ZDO's
        // hash-keyed connection, then pairs it with the counterpart ZDO whose
        // opposite-endpoint entry carries the same hash (portals saved
        // together in one blueprint re-link on placement).
        public void ApplyHashConnection(ZDO ownZdo)
        {
            if (ownZdo == null) return;
            if (ConnectionHash == 0 || ConnectionType == ZDOExtraData.ConnectionType.None) return;

            try
            {
                ZDOID uid = ownZdo.m_uid;
                ZDOExtraData.SetConnectionData(uid, ConnectionType, ConnectionHash);

                var otherType = (ZDOExtraData.ConnectionType)(ConnectionType ^ ZDOExtraData.ConnectionType.Target);
                bool ownIsSource = (ConnectionType & ZDOExtraData.ConnectionType.Target) == ZDOExtraData.ConnectionType.None;

                ZDOID matchId = ZDOID.None;
                foreach (var candidate in ZDOExtraData.GetAllConnectionZDOIDs(otherType))
                {
                    var hashData = ZDOExtraData.GetConnectionHashData(candidate, ConnectionType);
                    if (hashData != null && hashData.m_hash == ConnectionHash)
                    {
                        matchId = candidate;
                        break;
                    }
                }
                if (matchId == ZDOID.None) return;

                var other = ZDOMan.instance?.GetZDO(matchId);
                if (other == null) return;

                if ((ConnectionType & ZDOExtraData.ConnectionType.Spawned) != ZDOExtraData.ConnectionType.None)
                    (ownIsSource ? ownZdo : other).SetConnection(ZDOExtraData.ConnectionType.Spawned, ownIsSource ? matchId : uid);
                if ((ConnectionType & ZDOExtraData.ConnectionType.SyncTransform) != ZDOExtraData.ConnectionType.None)
                    (ownIsSource ? ownZdo : other).SetConnection(ZDOExtraData.ConnectionType.SyncTransform, ownIsSource ? matchId : uid);
                if ((ConnectionType & ZDOExtraData.ConnectionType.Portal) != ZDOExtraData.ConnectionType.None)
                {
                    other.SetConnection(ZDOExtraData.ConnectionType.Portal, uid);
                    ownZdo.SetConnection(ZDOExtraData.ConnectionType.Portal, matchId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{VaPieces.PluginName}: VAZdoDataBlob.ApplyHashConnection threw: {ex.Message}");
            }
        }
    }
}
