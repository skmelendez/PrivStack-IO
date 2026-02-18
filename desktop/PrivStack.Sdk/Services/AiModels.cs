namespace PrivStack.Sdk.Services;

/// <summary>
/// Request payload for an AI completion.
/// </summary>
public sealed record AiRequest
{
    /// <summary>System-level instruction for the model.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>User-level prompt content.</summary>
    public required string UserPrompt { get; init; }

    /// <summary>Maximum tokens to generate. Default 1024.</summary>
    public int MaxTokens { get; init; } = 1024;

    /// <summary>Sampling temperature (0.0–1.0). Default 0.7.</summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>Identifies the calling feature for telemetry/logging (e.g. "notes.summarize").</summary>
    public string? FeatureId { get; init; }
}

/// <summary>
/// Response from an AI completion request.
/// </summary>
public sealed record AiResponse
{
    public required bool Success { get; init; }
    public string? Content { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ProviderUsed { get; init; }
    public string? ModelUsed { get; init; }
    public int TokensUsed { get; init; }
    public TimeSpan Duration { get; init; }

    public static AiResponse Failure(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}

/// <summary>
/// Metadata about a registered AI provider.
/// </summary>
public sealed record AiProviderInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsConfigured { get; init; }
    public bool IsLocal { get; init; }
    public IReadOnlyList<AiModelInfo> AvailableModels { get; init; } = [];
}

/// <summary>
/// Metadata about a single AI model within a provider.
/// </summary>
public sealed record AiModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDownloaded { get; init; }
}
