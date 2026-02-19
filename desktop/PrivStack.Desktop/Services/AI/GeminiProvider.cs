using System.Text.Json;
using PrivStack.Sdk;
using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Google Gemini generateContent API provider.
/// API key stored encrypted in vault ("ai-vault", "gemini-api-key").
/// </summary>
internal sealed class GeminiProvider : AiProviderBase
{
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models";
    private const string VaultId = "ai-vault";
    private const string BlobId = "gemini-api-key";
    private const string DefaultModel = "gemini-2.0-flash";

    private static readonly AiModelInfo[] Models =
    [
        new() { Id = "gemini-2.0-flash", DisplayName = "Gemini 2.0 Flash" },
        new() { Id = "gemini-2.0-flash-lite", DisplayName = "Gemini 2.0 Flash Lite" },
        new() { Id = "gemini-2.5-pro-preview-05-06", DisplayName = "Gemini 2.5 Pro" },
    ];

    private readonly IPrivStackSdk _sdk;
    private string? _cachedApiKey;

    public GeminiProvider(IPrivStackSdk sdk) => _sdk = sdk;

    public override string Id => "gemini";
    public override string DisplayName => "Google Gemini";
    public override bool IsConfigured => GetApiKeySync() != null;
    public override IReadOnlyList<AiModelInfo> AvailableModels => Models;

    public override async Task<bool> ValidateAsync(CancellationToken ct)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrEmpty(key)) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{ApiBase}?key={key}");
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task<AiResponse> ExecuteCompletionAsync(
        AiRequest request, string? modelOverride, CancellationToken ct)
    {
        var apiKey = await GetApiKeyAsync(ct)
            ?? throw new InvalidOperationException("Gemini API key not configured");

        var model = modelOverride ?? DefaultModel;
        var url = $"{ApiBase}/{model}:generateContent?key={apiKey}";

        var payload = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = request.SystemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = request.UserPrompt } }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = request.MaxTokens,
                temperature = request.Temperature
            }
        };

        // Gemini uses API key in URL, no auth header needed
        using var doc = await PostJsonAsync(url, payload, new Dictionary<string, string>(), ct);
        var root = doc.RootElement;

        var content = root
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        var tokensUsed = root.TryGetProperty("usageMetadata", out var usage)
            ? usage.GetProperty("totalTokenCount").GetInt32() : 0;

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
