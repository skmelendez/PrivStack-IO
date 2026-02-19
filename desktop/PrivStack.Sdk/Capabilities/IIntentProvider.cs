namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Capability interface for plugins that declare actionable intents.
/// The shell-side IntentEngine collects descriptors from all providers,
/// uses AI to classify signals against them, and calls ExecuteIntentAsync
/// when the user approves a suggestion.
/// </summary>
public interface IIntentProvider
{
    /// <summary>
    /// Returns all intents this plugin can execute.
    /// Called once at activation and cached by the engine.
    /// </summary>
    IReadOnlyList<IntentDescriptor> GetSupportedIntents();

    /// <summary>
    /// Executes an intent with pre-extracted slot values.
    /// Called after the user approves a suggestion (with optional slot edits).
    /// </summary>
    Task<IntentResult> ExecuteIntentAsync(IntentRequest request, CancellationToken ct = default);
}
