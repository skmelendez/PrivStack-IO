using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Type of data storage location.
/// </summary>
public enum DataDirectoryType
{
    Default,
    Custom,
    GoogleDrive,
    ICloud
}

/// <summary>
/// Per-workspace sync location configuration.
/// Determines where encrypted event files are written for file-based sync.
/// The database always stays local; this controls the event store path.
/// </summary>
public record StorageLocation
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Default";

    [JsonPropertyName("custom_path")]
    public string? CustomPath { get; init; }
}
