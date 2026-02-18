namespace PrivStack.Sdk.Services;

/// <summary>
/// Host-provided AI completion service.
/// Plugins use this to request AI-generated content (summaries, transformations, etc.).
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Whether an AI provider is configured and ready to accept requests.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Display name of the currently active provider (e.g. "OpenAI", "Anthropic", "Local").
    /// Null when no provider is configured.
    /// </summary>
    string? ActiveProviderName { get; }

    /// <summary>
    /// Sends a completion request to the active AI provider.
    /// </summary>
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata about all registered providers and their configuration state.
    /// </summary>
    IReadOnlyList<AiProviderInfo> GetProviders();
}
