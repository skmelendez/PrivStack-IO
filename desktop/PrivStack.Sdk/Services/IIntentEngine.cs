using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;

namespace PrivStack.Sdk.Services;

/// <summary>
/// Shell-provided intent classification and execution engine.
/// Plugins use this to trigger on-demand analysis or query available intents.
/// The engine subscribes to IntentSignalMessage for background analysis.
/// </summary>
public interface IIntentEngine
{
    /// <summary>Whether the engine is enabled (AI + intent settings both on).</summary>
    bool IsEnabled { get; }

    /// <summary>Current pending suggestions awaiting user action.</summary>
    IReadOnlyList<IntentSuggestion> PendingSuggestions { get; }

    /// <summary>Raised when a new suggestion is produced by classification.</summary>
    event EventHandler<IntentSuggestion>? SuggestionAdded;

    /// <summary>Raised when a suggestion is dismissed or executed (by ID).</summary>
    event EventHandler<string>? SuggestionRemoved;

    /// <summary>Raised when all suggestions are cleared.</summary>
    event EventHandler? SuggestionsCleared;

    /// <summary>
    /// Analyzes a signal for actionable intents. Results arrive via SuggestionAdded.
    /// </summary>
    Task AnalyzeAsync(IntentSignalMessage signal, CancellationToken ct = default);

    /// <summary>
    /// Executes a suggestion by ID, optionally with user-edited slot values.
    /// </summary>
    Task<IntentResult> ExecuteAsync(
        string suggestionId,
        IReadOnlyDictionary<string, string>? slotOverrides = null,
        CancellationToken ct = default);

    /// <summary>Dismisses a suggestion without executing.</summary>
    void Dismiss(string suggestionId);

    /// <summary>Clears all pending suggestions.</summary>
    void ClearAll();

    /// <summary>Returns all intent descriptors from all active providers.</summary>
    IReadOnlyList<IntentDescriptor> GetAllAvailableIntents();
}
