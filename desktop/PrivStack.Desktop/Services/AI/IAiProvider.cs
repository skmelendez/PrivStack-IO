using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Internal provider interface for AI backends.
/// Each provider handles a specific API (OpenAI, Anthropic, Gemini, local LLM).
/// </summary>
internal interface IAiProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }
    bool IsLocal { get; }
    IReadOnlyList<AiModelInfo> AvailableModels { get; }

    Task<AiResponse> CompleteAsync(AiRequest request, string? modelOverride, CancellationToken ct);
    Task<bool> ValidateAsync(CancellationToken ct = default);
}
