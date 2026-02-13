using ProtoBuf;

namespace PrivStack.Desktop.Models;

/// <summary>
/// On-disk encrypted envelope for a full workspace snapshot.
/// Contains all entity data encrypted with AES-256-GCM.
/// Written to {snapshot_dir}/{peer_id}.snap on app close;
/// read by other peers on app open to bootstrap full state.
/// </summary>
[ProtoContract]
public sealed class SyncSnapshot
{
    [ProtoMember(1)]
    public int Version { get; set; } = 1;

    [ProtoMember(2)]
    public string PeerId { get; set; } = string.Empty;

    [ProtoMember(3)]
    public long Timestamp { get; set; }

    /// <summary>
    /// AES-GCM nonce (12 bytes, raw binary).
    /// </summary>
    [ProtoMember(4)]
    public byte[] Nonce { get; set; } = [];

    /// <summary>
    /// AES-GCM ciphertext with appended 16-byte auth tag.
    /// Plaintext is a protobuf-serialized <see cref="SyncSnapshotData"/>.
    /// </summary>
    [ProtoMember(5)]
    public byte[] Ciphertext { get; set; } = [];
}
