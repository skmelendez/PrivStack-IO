using System.Text.Json;
using PrivStack.Sdk;
using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Anthropic Messages API provider.
/// API key stored encrypted in vault ("ai-vault", "anthropic-api-key").
/// </summary>
internal sealed class AnthropicProvider : AiProviderBase
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string VaultId = "ai-vault";
    private const string BlobId = "anthropic-api-key";
    private const string DefaultModel = "claude-sonnet-4-20250514";
    private const string ApiVersion = "2023-06-01";

    private static readonly AiModelInfo[] Models =
    [
        new() { Id = "claude-sonnet-4-20250514", DisplayName = "Claude Sonnet 4" },
        new() { Id = "claude-haiku-4-5-20251001", DisplayName = "Claude Haiku 4.5" },
        new() { Id = "claude-sonnet-4-5-20250929", DisplayName = "Claude Sonnet 4.5" },
    ];

    private readonly IPrivStackSdk _sdk;
    private string? _cachedApiKey;

    public AnthropicProvider(IPrivStackSdk sdk) => _sdk = sdk;

    public override string Id => "anthropic";
    public override string DisplayName => "Anthropic";
    public override bool IsConfigured => GetApiKeySync() != null;
    public override IReadOnlyList<AiModelInfo> AvailableModels => Models;

    public override async Task<bool> ValidateAsync(CancellationToken ct)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrEmpty(key)) return false;

        try
        {
            var payload = new
            {
                model = DefaultModel,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "Hi" } }
            };
            var headers = new Dictionary<string, string>
            {
                ["x-api-key"] = key,
                ["anthropic-version"] = ApiVersion
            };
            using var doc = await PostJsonAsync(ApiUrl, payload, headers, ct);
            return true;
        }
        catch { return false; }
    }

    protected override async Task<AiResponse> ExecuteCompletionAsync(
        AiRequest request, string? modelOverride, CancellationToken ct)
    {
        var apiKey = await GetApiKeyAsync(ct)
            ?? throw new InvalidOperationException("Anthropic API key not configured");

        var model = modelOverride ?? DefaultModel;
        var payload = new
        {
            model,
            system = request.SystemPrompt,
            messages = new[] { new { role = "user", content = request.UserPrompt } },
            max_tokens = request.MaxTokens,
            temperature = request.Temperature
        };

        var headers = new Dictionary<string, string>
        {
            ["x-api-key"] = apiKey,
            ["anthropic-version"] = ApiVersion
        };

        using var doc = await PostJsonAsync(ApiUrl, payload, headers, ct);
        var root = doc.RootElement;

        // Anthropic returns content as an array of content blocks
        var contentBlocks = root.GetProperty("content");
        var textContent = string.Join("", contentBlocks.EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString()));

        var tokensUsed = root.TryGetProperty("usage", out var usage)
            ? usage.GetProperty("input_tokens").GetInt32() + usage.GetProperty("output_tokens").GetInt32()
            : 0;

        return new AiResponse
        {
            Success = true,
            Content = textContent,
            ProviderUsed = Id,
            ModelUsed = model,
            TokensUsed = tokensUsed
        };
    }

    private string? GetApiKeySync()
    {
        if (_cachedApiKey != null) return _cachedApiKey;
        try
        {
            var task = _sdk.VaultBlobRead(VaultId, BlobId);
            if (task.Wait(TimeSpan.FromSeconds(2)))
            {
                _cachedApiKey = System.Text.Encoding.UTF8.GetString(task.Result);
                return _cachedApiKey;
            }
        }
        catch { /* vault locked or not initialized */ }
        return null;
    }

    private async Task<string?> GetApiKeyAsync(CancellationToken ct)
    {
        if (_cachedApiKey != null) return _cachedApiKey;
        try
        {
            var bytes = await _sdk.VaultBlobRead(VaultId, BlobId, ct);
            _cachedApiKey = System.Text.Encoding.UTF8.GetString(bytes);
            return _cachedApiKey;
        }
        catch { return null; }
    }

    public void ClearCachedKey() => _cachedApiKey = null;
}
