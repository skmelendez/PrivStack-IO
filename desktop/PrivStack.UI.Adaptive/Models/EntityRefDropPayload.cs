// ============================================================================
// File: EntityRefDropPayload.cs
// Description: JSON payload for the privstack/entity-ref drag format.
// ============================================================================

using System.Text.Json.Serialization;

namespace PrivStack.UI.Adaptive.Models;

/// <summary>
/// Serialization payload for cross-plugin entity reference drag-and-drop.
/// </summary>
public sealed record EntityRefDropPayload
{
    [JsonPropertyName("entity_type")]
    public string EntityType { get; init; } = "";

    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }
}
