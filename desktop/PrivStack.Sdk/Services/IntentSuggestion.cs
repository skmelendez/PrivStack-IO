using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;

namespace PrivStack.Sdk.Services;

/// <summary>
/// An actionable suggestion produced by the IntentEngine after classifying a signal.
/// Contains the matched intent, extracted slot values, and confidence score.
/// </summary>
public sealed record IntentSuggestion
{
    /// <summary>Unique identifier for this suggestion instance.</summary>
    public required string SuggestionId { get; init; }

    /// <summary>The intent descriptor that was matched.</summary>
    public required IntentDescriptor MatchedIntent { get; init; }

    /// <summary>Human-readable summary of the suggested action.</summary>
    public required string Summary { get; init; }

    /// <summary>AI confidence score (0.0–1.0).</summary>
    public required double Confidence { get; init; }

    /// <summary>The signal that triggered this suggestion.</summary>
    public required IntentSignalMessage SourceSignal { get; init; }

    /// <summary>Slot name → extracted string value pairs.</summary>
    public required IReadOnlyDictionary<string, string> ExtractedSlots { get; init; }

    /// <summary>When the suggestion was produced.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
