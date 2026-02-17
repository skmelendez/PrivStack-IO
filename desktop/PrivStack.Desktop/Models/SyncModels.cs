using System.Text.Json.Serialization;
using Avalonia.Media;
using PrivStack.Desktop.Services;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Represents a discovered peer in the P2P network.
/// </summary>
public class DiscoveredPeer
{
    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("discovery_method")]
    public string DiscoveryMethod { get; set; } = string.Empty;

    [JsonPropertyName("addresses")]
    public List<string> Addresses { get; set; } = [];

    /// <summary>
    /// Gets a display-friendly name for the peer.
    /// </summary>
    public string DisplayName => DeviceName ?? $"Peer {PeerId[..8]}...";

    /// <summary>
    /// Gets a brush based on discovery method.
    /// </summary>
    public IBrush DiscoveryBrush => DiscoveryMethod switch
    {
        "Mdns" => ThemeHelper.GetBrush("ThemeSuccessBrush", Brushes.Green),
        "Dht" => ThemeHelper.GetBrush("ThemePrimaryBrush", Brushes.DeepSkyBlue),
        _ => ThemeHelper.GetBrush("ThemeTextMutedBrush", Brushes.Gray)
    };
}

/// <summary>
/// Current sync status.
/// </summary>
public class SyncStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("local_peer_id")]
    public string LocalPeerId { get; set; } = string.Empty;

    [JsonPropertyName("discovered_peers")]
    public List<DiscoveredPeer> DiscoveredPeers { get; set; } = [];
}

// ============================================================================
// Pairing Models
// ============================================================================

/// <summary>
/// A sync code used for pairing devices.
/// </summary>
public class SyncCode
{
    /// <summary>
    /// The human-readable sync code (e.g., "APPLE-BANANA-CHERRY-DELTA").
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the code (used for DHT namespace).
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;
}

/// <summary>
/// Status of a pairing request.
/// </summary>
public enum PairingStatus
{
    /// <summary>Peer discovered, awaiting local approval.</summary>
    PendingLocalApproval,
    /// <summary>We approved, waiting for remote approval.</summary>
    PendingRemoteApproval,
    /// <summary>Both sides approved, fully trusted.</summary>
    Trusted,
    /// <summary>Pairing was rejected.</summary>
    Rejected
}

/// <summary>
/// Information about a discovered peer during pairing.
/// </summary>
public class PairingPeerInfo
{
    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("discovered_at")]
    public long DiscoveredAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("addresses")]
    public List<string> Addresses { get; set; } = [];

    /// <summary>
    /// Gets the pairing status enum value.
    /// </summary>
    public PairingStatus PairingStatus => Status switch
    {
        "PendingLocalApproval" => PairingStatus.PendingLocalApproval,
        "PendingRemoteApproval" => PairingStatus.PendingRemoteApproval,
        "Trusted" => PairingStatus.Trusted,
        "Rejected" => PairingStatus.Rejected,
        _ => PairingStatus.PendingLocalApproval
    };

    /// <summary>
    /// Gets a display-friendly short peer ID.
    /// </summary>
    public string ShortPeerId => PeerId.Length > 12 ? $"{PeerId[..12]}..." : PeerId;

    /// <summary>
    /// Gets the discovery time as a DateTime.
    /// </summary>
    public DateTime DiscoveredTime => DateTimeOffset.FromUnixTimeSeconds(DiscoveredAt).LocalDateTime;
}

/// <summary>
/// A trusted peer that has completed the pairing process.
/// </summary>
public class TrustedPeer
{
    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("approved_at")]
    public long ApprovedAt { get; set; }

    [JsonPropertyName("last_synced")]
    public long? LastSynced { get; set; }

    [JsonPropertyName("addresses")]
    public List<string> Addresses { get; set; } = [];

    /// <summary>
    /// Gets a display-friendly short peer ID.
    /// </summary>
    public string ShortPeerId => PeerId.Length > 12 ? $"{PeerId[..12]}..." : PeerId;

    /// <summary>
    /// Gets the approval time as a DateTime.
    /// </summary>
    public DateTime ApprovedTime => DateTimeOffset.FromUnixTimeSeconds(ApprovedAt).LocalDateTime;

    /// <summary>
    /// Gets the last sync time as a DateTime, or null if never synced.
    /// </summary>
    public DateTime? LastSyncedTime => LastSynced.HasValue
        ? DateTimeOffset.FromUnixTimeSeconds(LastSynced.Value).LocalDateTime
        : null;

    /// <summary>
    /// Gets a display-friendly last sync string.
    /// </summary>
    public string LastSyncedDisplay => LastSyncedTime?.ToString("g") ?? "Never";

    /// <summary>
    /// Whether this peer is currently online/connected.
    /// </summary>
    public bool IsOnline { get; set; }
}

/// <summary>
/// A sync event received from the native sync engine.
/// </summary>
public class SyncEventDto
{
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("peer_id")]
    public string? PeerId { get; set; }

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("document_id")]
    public string? DocumentId { get; set; }

    [JsonPropertyName("events_sent")]
    public int? EventsSent { get; set; }

    [JsonPropertyName("events_received")]
    public int? EventsReceived { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("entity_type")]
    public string? EntityType { get; set; }

    [JsonPropertyName("json_data")]
    public string? JsonData { get; set; }
}
