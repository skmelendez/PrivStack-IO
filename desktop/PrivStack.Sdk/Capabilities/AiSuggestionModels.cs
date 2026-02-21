namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// State of a content suggestion card in the global AI tray.
/// </summary>
public enum ContentSuggestionState
{
    Loading,
    Ready,
    Error,
    Applied
}

/// <summary>
/// An action button that can be displayed on a content suggestion card.
/// </summary>
public sealed record SuggestionAction
{
    /// <summary>Unique identifier for this action (e.g., "replace", "insert_above", "undo").</summary>
    public required string ActionId { get; init; }

    /// <summary>Button label shown to the user.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether this is the primary (highlighted) action.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>Whether this action is destructive (shown in danger color).</summary>
    public bool IsDestructive { get; init; }
}

/// <summary>
/// A content suggestion card pushed by a plugin into the global AI tray.
/// Plugins create these via <see cref="Services.IAiSuggestionService"/> and the shell renders them.
/// </summary>
public sealed record ContentSuggestionCard
{
    /// <summary>Unique identifier for this suggestion.</summary>
    public required string SuggestionId { get; init; }

    /// <summary>Plugin that owns this suggestion.</summary>
    public required string PluginId { get; init; }

    /// <summary>Card header title.</summary>
    public required string Title { get; init; }

    /// <summary>Short summary or preview text.</summary>
    public string? Summary { get; init; }

    /// <summary>Full content of the suggestion (e.g., AI-generated text).</summary>
    public string? Content { get; init; }

    /// <summary>Entity ID the suggestion relates to (e.g., block ID, page ID).</summary>
    public string? SourceEntityId { get; init; }

    /// <summary>Entity type (e.g., "page", "block").</summary>
    public string? SourceEntityType { get; init; }

    /// <summary>Human-readable title for the source entity.</summary>
    public string? SourceEntityTitle { get; init; }

    /// <summary>Current state of the suggestion.</summary>
    public ContentSuggestionState State { get; init; } = ContentSuggestionState.Loading;

    /// <summary>Error message when State is Error.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the suggestion was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Available actions for this card (e.g., Replace, Insert Above, Dismiss).</summary>
    public IReadOnlyList<SuggestionAction> Actions { get; init; } = [];

    /// <summary>
    /// Human-readable label for the user request that triggered this suggestion
    /// (e.g., "Summarize this block"). Used by the chat-style tray to render the user bubble.
    /// When null, the shell falls back to synthesizing from <see cref="Title"/>.
    /// </summary>
    public string? UserPromptLabel { get; init; }
}
