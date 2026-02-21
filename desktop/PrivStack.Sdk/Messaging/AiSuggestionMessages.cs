using PrivStack.Sdk.Capabilities;

namespace PrivStack.Sdk.Messaging;

/// <summary>
/// Sent when a plugin pushes a new content suggestion card into the global AI tray.
/// </summary>
public sealed record ContentSuggestionPushedMessage
{
    public required ContentSuggestionCard Card { get; init; }
}

/// <summary>
/// Sent when a plugin updates an existing content suggestion card (state, content, error).
/// </summary>
public sealed record ContentSuggestionUpdatedMessage
{
    public required string SuggestionId { get; init; }
    public required string PluginId { get; init; }
    public ContentSuggestionState? NewState { get; init; }
    public string? NewContent { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SuggestionAction>? NewActions { get; init; }
}

/// <summary>
/// Sent when a plugin removes its own content suggestion card.
/// </summary>
public sealed record ContentSuggestionRemovedMessage
{
    public required string SuggestionId { get; init; }
    public required string PluginId { get; init; }
}

/// <summary>
/// Sent by the shell when the user clicks an action on a content suggestion card.
/// The owning plugin subscribes and executes the domain-specific operation.
/// </summary>
public sealed record ContentSuggestionActionRequestedMessage
{
    public required string SuggestionId { get; init; }
    public required string PluginId { get; init; }
    public required string ActionId { get; init; }
}

/// <summary>
/// Sent by the shell when the user dismisses a content suggestion card.
/// The owning plugin can clean up any associated state.
/// </summary>
public sealed record ContentSuggestionDismissedMessage
{
    public required string SuggestionId { get; init; }
    public required string PluginId { get; init; }
}
