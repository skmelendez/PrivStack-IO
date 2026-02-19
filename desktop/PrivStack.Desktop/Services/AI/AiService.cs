using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Orchestrator implementing IAiService. Reads AppSettings.AiProvider to select the
/// active provider, lazily constructs providers on first use, routes requests.
/// </summary>
internal sealed class AiService : IAiService
{
    private static readonly ILogger _log = Log.ForContext<AiService>();
    private readonly IAppSettingsService _appSettings;
    private readonly IPrivStackSdk _sdk;
    private readonly AiModelManager _modelManager;

    private Dictionary<string, IAiProvider>? _providers;

    public AiService(IAppSettingsService appSettings, IPrivStackSdk sdk, AiModelManager modelManager)
    {
        _appSettings = appSettings;
        _sdk = sdk;
        _modelManager = modelManager;
    }

    public bool IsAvailable
    {
        get
        {
            if (!_appSettings.Settings.AiEnabled) return false;
            var provider = GetActiveProvider();
            return provider?.IsConfigured == true;
        }
    }

    public string? ActiveProviderName => GetActiveProvider()?.DisplayName;

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        if (!_appSettings.Settings.AiEnabled)
            return AiResponse.Failure("AI is disabled in settings");

        var provider = GetActiveProvider();
        if (provider == null)
            return AiResponse.Failure("No AI provider configured");

        if (!provider.IsConfigured)
        {
            // Try to unlock vault for cloud providers
            if (!provider.IsLocal)
            {
                var unlocked = await _sdk.RequestVaultUnlockAsync("ai-vault", ct);
                if (!unlocked || !provider.IsConfigured)
                    return AiResponse.Failure($"{provider.DisplayName} API key not configured. Check Settings > AI.");
            }
            else
            {
                return AiResponse.Failure("No local model downloaded. Check Settings > AI.");
            }
        }

        var modelOverride = provider.IsLocal
            ? _appSettings.Settings.AiLocalModel
            : _appSettings.Settings.AiModel;

        _log.Information("AI request via {Provider} (feature: {Feature})",
            provider.Id, request.FeatureId ?? "unknown");

        var response = await provider.CompleteAsync(request, modelOverride, ct);

        if (response.Success)
        {
            _log.Information("AI response: {Tokens} tokens in {Duration}ms via {Provider}/{Model}",
                response.TokensUsed, response.Duration.TotalMilliseconds,
                response.ProviderUsed, response.ModelUsed);
        }
        else
        {
            _log.Warning("AI request failed: {Error}", response.ErrorMessage);
        }

        return response;
    }

    public IReadOnlyList<AiProviderInfo> GetProviders()
    {
        EnsureProviders();
        return _providers!.Values.Select(p => new AiProviderInfo
        {
            Id = p.Id,
            DisplayName = p.DisplayName,
            IsConfigured = p.IsConfigured,
            IsLocal = p.IsLocal,
            AvailableModels = p.AvailableModels
        }).ToList();
    }

    internal IAiProvider? GetProvider(string id)
    {
        EnsureProviders();
        return _providers!.GetValueOrDefault(id);
    }

    private IAiProvider? GetActiveProvider()
    {
        var providerId = _appSettings.Settings.AiProvider;
        if (string.IsNullOrEmpty(providerId) || providerId == "none")
            return null;

        EnsureProviders();
        return _providers!.GetValueOrDefault(providerId);
    }

    private void EnsureProviders()
    {
        if (_providers != null) return;

        _providers = new Dictionary<string, IAiProvider>
        {
            ["openai"] = new OpenAiProvider(_sdk),
            ["anthropic"] = new AnthropicProvider(_sdk),
            ["gemini"] = new GeminiProvider(_sdk),
            ["local"] = new LocalLlamaProvider(_modelManager),
        };
    }
}
