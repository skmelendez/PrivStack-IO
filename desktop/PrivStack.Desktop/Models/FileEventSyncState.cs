using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Local tracking state for file-based event sync.
/// Stores the last-processed filename per peer for high-water mark tracking.
/// </summary>
public sealed record FileEventSyncState
{
    [JsonPropertyName("processed_watermarks")]
    public Dictionary<string, string> ProcessedWatermarks { get; init; } = new();
}
