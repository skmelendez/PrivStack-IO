using PrivStack.Sdk.Capabilities;

namespace PrivStack.Sdk.Services;

/// <summary>
/// Service for pushing content suggestion cards into the global AI tray.
/// Plugins use this to display AI-generated suggestions (summaries, rewrites, etc.)
/// in the shell's unified suggestion panel.
/// </summary>
public interface IAiSuggestionService
{
    /// <summary>
    /// Pushes a new suggestion card into the global AI tray.
    /// </summary>
    void Push(ContentSuggestionCard card);

    /// <summary>
    /// Updates an existing suggestion card's state, content, or error message.
    /// </summary>
    void Update(string suggestionId, string pluginId,
        ContentSuggestionState? newState = null,
        string? newContent = null,
        string? errorMessage = null,
        IReadOnlyList<SuggestionAction>? newActions = null);

    /// <summary>
    /// Removes a suggestion card from the global AI tray.
    /// </summary>
    void Remove(string suggestionId, string pluginId);
}
