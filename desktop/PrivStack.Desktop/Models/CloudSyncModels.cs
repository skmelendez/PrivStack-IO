using System.Text.Json.Serialization;
using Avalonia.Media;
using PrivStack.Desktop.Services;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Sync tier for a workspace — determines sync transport.
/// </summary>
public enum SyncTier
{
    /// <summary>Data stays on this device only.</summary>
    LocalOnly,

    /// <summary>Sync via relay server (enter URL).</summary>
    NetworkRelay,

    /// <summary>PrivStack Cloud — S3-backed with 10 GB free.</summary>
    PrivStackCloud
}

/// <summary>
/// Cloud sync status reported from the Rust core.
/// </summary>
public record CloudSyncStatus
{
    [JsonPropertyName("is_syncing")]
    public bool IsSyncing { get; init; }

    [JsonPropertyName("is_authenticated")]
    public bool IsAuthenticated { get; init; }

    [JsonPropertyName("active_workspace")]
    public string? ActiveWorkspace { get; init; }

    [JsonPropertyName("pending_upload_count")]
    public int PendingUploadCount { get; init; }

    [JsonPropertyName("last_sync_at")]
    public DateTime? LastSyncAt { get; init; }

    [JsonPropertyName("connected_devices")]
    public int ConnectedDevices { get; init; }
}

/// <summary>
/// Storage quota info from the cloud API.
/// </summary>
public record CloudQuota
{
    [JsonPropertyName("storage_used_bytes")]
    public ulong StorageUsedBytes { get; init; }

    [JsonPropertyName("storage_quota_bytes")]
    public ulong StorageQuotaBytes { get; init; }

    [JsonPropertyName("usage_percent")]
    public double UsagePercent { get; init; }

    public string UsedDisplay => FormatBytes(StorageUsedBytes);
    public string QuotaDisplay => FormatBytes(StorageQuotaBytes);
    public string Summary => $"{UsedDisplay} / {QuotaDisplay}";

    public IBrush SeverityBrush => UsagePercent switch
    {
        > 95 => ThemeHelper.GetBrush("ThemeDangerBrush", Brushes.Red),
        > 80 => ThemeHelper.GetBrush("ThemeWarningBrush", Brushes.Yellow),
        _ => ThemeHelper.GetBrush("ThemeSuccessBrush", Brushes.Green)
    };

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}

/// <summary>
/// Cloud-registered workspace info.
/// </summary>
public record CloudWorkspaceInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; init; } = string.Empty;

    [JsonPropertyName("workspace_name")]
    public string WorkspaceName { get; init; } = string.Empty;

    [JsonPropertyName("s3_prefix")]
    public string S3Prefix { get; init; } = string.Empty;

    [JsonPropertyName("storage_used_bytes")]
    public ulong StorageUsedBytes { get; init; }

    [JsonPropertyName("storage_quota_bytes")]
    public ulong StorageQuotaBytes { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    public string StorageDisplay => $"{FormatBytes(StorageUsedBytes)} used";

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}

/// <summary>
/// Auth tokens returned from cloud API authentication.
/// </summary>
public record CloudAuthTokens
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// Share info for an entity.
/// </summary>
public record CloudShareInfo
{
    [JsonPropertyName("share_id")]
    public long ShareId { get; init; }

    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("entity_type")]
    public string EntityType { get; init; } = string.Empty;

    [JsonPropertyName("entity_name")]
    public string? EntityName { get; init; }

    [JsonPropertyName("recipient_email")]
    public string RecipientEmail { get; init; } = string.Empty;

    [JsonPropertyName("permission")]
    public string Permission { get; init; } = "read";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("accepted_at")]
    public DateTime? AcceptedAt { get; init; }
}

/// <summary>
/// Entity shared with the current user.
/// </summary>
public record SharedWithMeInfo
{
    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("entity_type")]
    public string EntityType { get; init; } = string.Empty;

    [JsonPropertyName("entity_name")]
    public string? EntityName { get; init; }

    [JsonPropertyName("owner_user_id")]
    public long OwnerUserId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; init; } = string.Empty;

    [JsonPropertyName("permission")]
    public string Permission { get; init; } = "read";
}

/// <summary>
/// Blob metadata for cloud-synced file attachments.
/// </summary>
public record CloudBlobMeta
{
    [JsonPropertyName("blob_id")]
    public string BlobId { get; init; } = string.Empty;

    [JsonPropertyName("entity_id")]
    public string? EntityId { get; init; }

    [JsonPropertyName("s3_key")]
    public string S3Key { get; init; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public ulong SizeBytes { get; init; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; init; }
}

/// <summary>
/// Device info for cloud-registered devices.
/// </summary>
public record CloudDeviceInfo
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("last_seen_at")]
    public DateTime? LastSeenAt { get; init; }
}
