using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Represents a workspace with its own isolated database.
/// </summary>
public record Workspace
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("has_password")]
    public bool HasPassword { get; init; }

    /// <summary>
    /// Per-workspace sync location. Determines where encrypted event files are written.
    /// Null = local only (no file-based sync). DB always stays at local path.
    /// </summary>
    [JsonPropertyName("storage_location")]
    public StorageLocation? StorageLocation { get; init; }

    /// <summary>
    /// Sync tier — determines transport for this workspace.
    /// </summary>
    [JsonPropertyName("sync_tier")]
    public SyncTier SyncTier { get; init; } = SyncTier.LocalOnly;

    /// <summary>
    /// Cloud workspace ID if registered with PrivStack Cloud.
    /// Null for local-only or relay workspaces.
    /// </summary>
    [JsonPropertyName("cloud_workspace_id")]
    public string? CloudWorkspaceId { get; init; }
}

/// <summary>
/// Registry of all workspaces and the currently active one.
/// </summary>
public record WorkspaceRegistry
{
    [JsonPropertyName("workspaces")]
    public List<Workspace> Workspaces { get; init; } = [];

    [JsonPropertyName("active_workspace_id")]
    public string? ActiveWorkspaceId { get; init; }
}
