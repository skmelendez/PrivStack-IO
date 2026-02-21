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
    private const string DefaultModel = "gemini-2.5-flash";

    private static readonly AiModelInfo[] Models =
    [
        new() { Id = "gemini-2.5-pro", DisplayName = "Gemini 2.5 Pro", ContextWindowTokens = 1_000_000 },
        new() { Id = "gemini-2.5-flash", DisplayName = "Gemini 2.5 Flash", ContextWindowTokens = 1_000_000 },
        new() { Id = "gemini-2.5-flash-lite", DisplayName = "Gemini 2.5 Flash Lite", ContextWindowTokens = 1_000_000 },
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

        var contents = BuildContents(request);
        var payload = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = request.SystemPrompt } }
            },
            contents,
            generationConfig = new
            {
                maxOutputTokens = request.MaxTokens,
                temperature = request.Temperature
            }
        };

        // Gemini uses API key in URL, no auth header needed
        using var doc = await PostJsonAsync(url, payload, new Dictionary<string, string>(), ct);
        var root = doc.RootElement;

        // Handle error responses (quota exceeded, invalid key, etc.)
        if (root.TryGetProperty("error", out var errorProp))
        {
            var errorMsg = errorProp.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() : "Unknown API error";
            return new AiResponse { Success = false, ErrorMessage = errorMsg, ProviderUsed = Id, ModelUsed = model };
        }

        // Handle missing or empty candidates (safety block, empty response)
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.GetArrayLength() == 0)
        {
            var reason = "No candidates returned";
            if (root.TryGetProperty("promptFeedback", out var feedback)
                && feedback.TryGetProperty("blockReason", out var blockReason))
                reason = $"Blocked: {blockReason.GetString()}";

            return new AiResponse { Success = false, ErrorMessage = reason, ProviderUsed = Id, ModelUsed = model };
        }

        var candidate = candidates[0];
        string? content = null;

        if (candidate.TryGetProperty("content", out var contentProp)
            && contentProp.TryGetProperty("parts", out var parts)
            && parts.GetArrayLength() > 0
            && parts[0].TryGetProperty("text", out var textProp))
        {
            content = textProp.GetString();
        }

        // Check for finish reason indicating blocked content
        if (string.IsNullOrEmpty(content)
            && candidate.TryGetProperty("finishReason", out var finishReason)
            && finishReason.GetString() != "STOP")
        {
            return new AiResponse
            {
                Success = false,
                ErrorMessage = $"Generation stopped: {finishReason.GetString()}",
                ProviderUsed = Id,
                ModelUsed = model,
            };
        }

        var tokensUsed = root.TryGetProperty("usageMetadata", out var usage)
            && usage.TryGetProperty("totalTokenCount", out var tokenCount)
            ? tokenCount.GetInt32() : 0;

        return new AiResponse
        {
            Success = !string.IsNullOrEmpty(content),
            Content = content,
            ErrorMessage = string.IsNullOrEmpty(content) ? "Empty response" : null,
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

    private static List<object> BuildContents(AiRequest request)
    {
        var contents = new List<object>();

        if (request.ConversationHistory is { Count: > 0 })
        {
            foreach (var msg in request.ConversationHistory)
            {
                // Gemini uses "model" instead of "assistant"
                var role = msg.Role == "assistant" ? "model" : msg.Role;
                contents.Add(new
                {
                    role,
                    parts = new[] { new { text = msg.Content } }
                });
            }
        }

        contents.Add(new
        {
            role = "user",
            parts = new[] { new { text = request.UserPrompt } }
        });

        return contents;
    }
}
