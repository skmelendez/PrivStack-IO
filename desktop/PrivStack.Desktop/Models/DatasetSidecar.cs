using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Metadata sidecar file for dataset imports stored in the shared files directory.
/// Written alongside each CSV as {filename}.privstack so other peers can detect
/// and auto-import the dataset. JSON format for human readability.
/// </summary>
public sealed record DatasetSidecar
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("dataset_id")]
    public string DatasetId { get; init; } = string.Empty;

    [JsonPropertyName("dataset_name")]
    public string DatasetName { get; init; } = string.Empty;

    [JsonPropertyName("source_filename")]
    public string SourceFilename { get; init; } = string.Empty;

    [JsonPropertyName("file_size")]
    public long FileSize { get; init; }

    [JsonPropertyName("file_hash")]
    public string FileHash { get; init; } = string.Empty;

    [JsonPropertyName("imported_at")]
    public DateTimeOffset ImportedAt { get; init; }

    [JsonPropertyName("peer_id")]
    public string PeerId { get; init; } = string.Empty;
}
