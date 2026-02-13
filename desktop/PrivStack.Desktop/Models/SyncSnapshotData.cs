using ProtoBuf;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Inner plaintext data for a workspace snapshot.
/// Serialized as protobuf, then encrypted and wrapped in <see cref="SyncSnapshot"/>.
/// Contains all entities grouped by type.
/// </summary>
[ProtoContract]
public sealed class SyncSnapshotData
{
    [ProtoMember(1)]
    public int Version { get; set; } = 1;

    [ProtoMember(2)]
    public string WorkspaceId { get; set; } = string.Empty;

    [ProtoMember(3)]
    public long Timestamp { get; set; }

    /// <summary>
    /// Entity data grouped by type. Each entry contains all entities of that type as JSON strings.
    /// </summary>
    [ProtoMember(4)]
    public List<EntityTypeSnapshot> EntityTypes { get; set; } = [];
}

/// <summary>
/// All entities of a single type within a snapshot.
/// </summary>
[ProtoContract]
public sealed class EntityTypeSnapshot
{
    /// <summary>
    /// Entity type identifier (e.g. "page", "task", "contact").
    /// </summary>
    [ProtoMember(1)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Each entity serialized as a JSON string. Protobuf handles string lists efficiently.
    /// </summary>
    [ProtoMember(2)]
    public List<string> Items { get; set; } = [];
}
