using ProtoBuf;

namespace PrivStack.Desktop.Models;

/// <summary>
/// On-disk DTO for encrypted event files written to the shared event store.
/// Each file represents a single entity mutation encrypted with AES-256-GCM.
/// Serialized as Protocol Buffers for minimal size over network filesystems.
/// </summary>
[ProtoContract]
public sealed class FileEventEnvelope
{
    [ProtoMember(1)]
    public int Version { get; set; } = 1;

    [ProtoMember(2)]
    public string PeerId { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string EventId { get; set; } = string.Empty;

    [ProtoMember(4)]
    public long Timestamp { get; set; }

    [ProtoMember(5)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// AES-GCM nonce (12 bytes, raw binary).
    /// </summary>
    [ProtoMember(6)]
    public byte[] Nonce { get; set; } = [];

    /// <summary>
    /// AES-GCM ciphertext with appended 16-byte auth tag (raw binary).
    /// </summary>
    [ProtoMember(7)]
    public byte[] Ciphertext { get; set; } = [];
}
