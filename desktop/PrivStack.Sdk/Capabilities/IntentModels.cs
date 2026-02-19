namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Describes a single actionable intent that a plugin can execute.
/// The AI classifier uses Description to match natural language content.
/// </summary>
public sealed record IntentDescriptor
{
    /// <summary>Unique intent identifier, e.g. "calendar.create_event".</summary>
    public required string IntentId { get; init; }

    /// <summary>Human-readable name, e.g. "Create Calendar Event".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Natural language description for AI prompt context.</summary>
    public required string Description { get; init; }

    /// <summary>Owning plugin identifier, e.g. "privstack.calendar".</summary>
    public required string PluginId { get; init; }

    /// <summary>Optional icon name for UI display.</summary>
    public string? Icon { get; init; }

    /// <summary>Typed parameter slots for this intent.</summary>
    public IReadOnlyList<IntentSlot> Slots { get; init; } = [];
}

/// <summary>
/// A typed parameter for an intent. The AI extracts slot values from signal content.
/// </summary>
public sealed record IntentSlot
{
    /// <summary>Machine name, e.g. "title".</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable label, e.g. "Event Title".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Description for the AI prompt, e.g. "The name/subject of the event".</summary>
    public required string Description { get; init; }

    /// <summary>Data type for validation and UI rendering.</summary>
    public required IntentSlotType Type { get; init; }

    /// <summary>Whether the slot must have a value for execution.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Default value when none is extracted.</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>
/// Slot data types. Used by the slot editor to render appropriate input controls.
/// </summary>
public enum IntentSlotType
{
    String,
    Text,
    DateTime,
    Date,
    Time,
    Duration,
    Integer,
    Boolean,
    Email,
    Url,
    EntityReference
}

/// <summary>
/// Execution request with extracted slot values, sent to IIntentProvider.ExecuteIntentAsync.
/// </summary>
public sealed record IntentRequest
{
    /// <summary>The intent to execute.</summary>
    public required string IntentId { get; init; }

    /// <summary>Slot name → string value pairs extracted by AI or edited by user.</summary>
    public required IReadOnlyDictionary<string, string> Slots { get; init; }

    /// <summary>Entity that triggered the original signal, if any.</summary>
    public string? SourceEntityId { get; init; }

    /// <summary>Plugin that emitted the signal.</summary>
    public string? SourcePluginId { get; init; }

    /// <summary>Link type for cross-plugin entity linking.</summary>
    public string? SourceLinkType { get; init; }
}

/// <summary>
/// Result of an intent execution.
/// </summary>
public sealed record IntentResult
{
    public required bool Success { get; init; }
    public string? CreatedEntityId { get; init; }
    public string? CreatedEntityType { get; init; }
    public string? NavigationLinkType { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Summary { get; init; }

    public static IntentResult Failure(string error) => new()
        { Success = false, ErrorMessage = error };
}
