using System.Text.Json;
using PrivStack.Sdk;
using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// OpenAI chat completion provider using the REST API.
/// API key stored encrypted in vault ("ai-vault", "openai-api-key").
/// </summary>
internal sealed class OpenAiProvider : AiProviderBase
{
    private const string ApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string VaultId = "ai-vault";
    private const string BlobId = "openai-api-key";
    private const string DefaultModel = "gpt-4o-mini";

    private static readonly AiModelInfo[] Models =
    [
        new() { Id = "gpt-4o", DisplayName = "GPT-4o" },
        new() { Id = "gpt-4o-mini", DisplayName = "GPT-4o Mini" },
        new() { Id = "gpt-4-turbo", DisplayName = "GPT-4 Turbo" },
    ];

    private readonly IPrivStackSdk _sdk;
    private string? _cachedApiKey;

    public OpenAiProvider(IPrivStackSdk sdk) => _sdk = sdk;

    public override string Id => "openai";
    public override string DisplayName => "OpenAI";
    public override bool IsConfigured => GetApiKeySync() != null;
    public override IReadOnlyList<AiModelInfo> AvailableModels => Models;

    public override async Task<bool> ValidateAsync(CancellationToken ct)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrEmpty(key)) return false;

        try
        {
            var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<AiResponse> ExecuteCompletionAsync(
        AiRequest request, string? modelOverride, CancellationToken ct)
    {
        var apiKey = await GetApiKeyAsync(ct)
            ?? throw new InvalidOperationException("OpenAI API key not configured");

        var model = modelOverride ?? DefaultModel;
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            max_tokens = request.MaxTokens,
            temperature = request.Temperature
        };

        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" };
        using var doc = await PostJsonAsync(ApiUrl, payload, headers, ct);
        var root = doc.RootElement;

        var content = root.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        var tokensUsed = root.TryGetProperty("usage", out var usage)
            ? usage.GetProperty("total_tokens").GetInt32() : 0;

        return new AiResponse
        {
            Success = true,
            Content = content,
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
