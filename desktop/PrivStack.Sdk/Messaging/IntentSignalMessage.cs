namespace PrivStack.Sdk.Messaging;

/// <summary>
/// Broadcast when a plugin produces content that may contain actionable intents.
/// The shell-side IntentEngine subscribes to these via the Messenger bus.
/// </summary>
public sealed record IntentSignalMessage
{
    /// <summary>Plugin that emitted the signal.</summary>
    public required string SourcePluginId { get; init; }

    /// <summary>Classification of the signal trigger.</summary>
    public required IntentSignalType SignalType { get; init; }

    /// <summary>Text content to analyze for intents.</summary>
    public required string Content { get; init; }

    /// <summary>Entity type that produced the signal (e.g. "page", "task").</summary>
    public string? EntityType { get; init; }

    /// <summary>Entity ID that produced the signal.</summary>
    public string? EntityId { get; init; }

    /// <summary>Human-readable title for the source entity.</summary>
    public string? EntityTitle { get; init; }

    /// <summary>When the signal was emitted.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optional key-value metadata for signal context.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Classification of what triggered an intent signal.
/// </summary>
public enum IntentSignalType
{
    /// <summary>General text content (notes, journal entries).</summary>
    TextContent,

    /// <summary>New entity was saved.</summary>
    EntityCreated,

    /// <summary>Existing entity was modified.</summary>
    EntityUpdated,

    /// <summary>Incoming email received.</summary>
    EmailReceived,

    /// <summary>Explicit user-triggered analysis ("analyze this").</summary>
    UserRequest
}
