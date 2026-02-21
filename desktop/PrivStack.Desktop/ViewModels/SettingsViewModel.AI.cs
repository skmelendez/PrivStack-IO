using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.AI;
using PrivStack.Sdk;
using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Broadcast when AI intent engine settings change (AiEnabled or AiIntentEnabled toggled).
/// </summary>
public sealed record IntentSettingsChangedMessage;

/// <summary>
/// Represents an AI provider option for the settings dropdown.
/// </summary>
public record AiProviderOption(string Id, string DisplayName);

/// <summary>
/// Represents a local AI model option for the settings dropdown.
/// </summary>
public record AiLocalModelOption(string Id, string DisplayName, string SizeText, bool IsDownloaded);

/// <summary>
/// Represents a saved API key entry for display in the settings panel.
/// </summary>
public record SavedApiKeyEntry(string ProviderId, string ProviderDisplayName, string KeyHint);

/// <summary>
/// AI settings section of the Settings panel.
/// </summary>
public partial class SettingsViewModel
{
    // ── AI Properties ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiApiKeyInput))]
    [NotifyPropertyChangedFor(nameof(ShowAiCloudModelSelect))]
    [NotifyPropertyChangedFor(nameof(ShowAiLocalModelSection))]
    private bool _aiEnabled;

    [ObservableProperty]
    private string? _aiApiKey;

    [ObservableProperty]
    private string? _aiApiKeyStatus;

    [ObservableProperty]
    private string _aiApiKeySaveLabel = "Save";

    [ObservableProperty]
    private double _aiTemperature = 0.7;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiApiKeyInput))]
    [NotifyPropertyChangedFor(nameof(ShowAiCloudModelSelect))]
    [NotifyPropertyChangedFor(nameof(ShowAiLocalModelSection))]
    [NotifyPropertyChangedFor(nameof(CanDownloadAiLocalModel))]
    private AiProviderOption? _selectedAiProvider;

    [ObservableProperty]
    private AiModelInfo? _selectedAiCloudModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadAiLocalModel))]
    [NotifyPropertyChangedFor(nameof(AiLocalModelDownloadLabel))]
    private AiLocalModelOption? _selectedAiLocalModel;

    [ObservableProperty]
    private bool _isAiLocalModelDownloading;

    [ObservableProperty]
    private double _aiLocalModelDownloadProgress;

    [ObservableProperty]
    private string? _aiLocalModelDownloadStatus;

    [ObservableProperty]
    private bool _aiIntentEnabled;

    [ObservableProperty]
    private bool _aiIntentAutoAnalyze = true;

    public ObservableCollection<AiProviderOption> AiProviderOptions { get; } = [];
    public ObservableCollection<AiModelInfo> AiCloudModels { get; } = [];
    public ObservableCollection<AiLocalModelOption> AiLocalModels { get; } = [];
    public ObservableCollection<SavedApiKeyEntry> SavedApiKeys { get; } = [];

    // ── Computed Properties ────────────────────────────────────────────

    public bool ShowAiApiKeyInput =>
        AiEnabled && SelectedAiProvider is { Id: "openai" or "anthropic" or "gemini" };

    public bool ShowAiCloudModelSelect =>
        AiEnabled && SelectedAiProvider is { Id: "openai" or "anthropic" or "gemini" };

    public bool ShowAiLocalModelSection =>
        AiEnabled && SelectedAiProvider is { Id: "local" };

    public bool CanDownloadAiLocalModel =>
        SelectedAiLocalModel != null && !SelectedAiLocalModel.IsDownloaded && !IsAiLocalModelDownloading;

    public string AiLocalModelDownloadLabel =>
        SelectedAiLocalModel?.IsDownloaded == true ? "Downloaded" : "Download Model";

    // ── Initialization ─────────────────────────────────────────────────

    private void LoadAiSettings()
    {
        var settings = _settingsService.Settings;
        AiEnabled = settings.AiEnabled;
        AiTemperature = settings.AiTemperature;

        // Populate provider options
        AiProviderOptions.Clear();
        AiProviderOptions.Add(new AiProviderOption("none", "None (Disabled)"));
        AiProviderOptions.Add(new AiProviderOption("openai", "OpenAI"));
        AiProviderOptions.Add(new AiProviderOption("anthropic", "Anthropic"));
        AiProviderOptions.Add(new AiProviderOption("gemini", "Google Gemini"));
        AiProviderOptions.Add(new AiProviderOption("local", "Local (LLamaSharp)"));

        SelectedAiProvider = AiProviderOptions.FirstOrDefault(p => p.Id == settings.AiProvider)
                             ?? AiProviderOptions[0];

        // Populate local models
        RefreshAiLocalModels();

        // Load cloud models for active provider
        RefreshAiCloudModels();

        // Load cloud model selection
        if (!string.IsNullOrEmpty(settings.AiModel))
        {
            SelectedAiCloudModel = AiCloudModels.FirstOrDefault(m => m.Id == settings.AiModel);
        }

        // Load local model selection
        if (!string.IsNullOrEmpty(settings.AiLocalModel))
        {
            SelectedAiLocalModel = AiLocalModels.FirstOrDefault(m => m.Id == settings.AiLocalModel);
        }

        AiIntentEnabled = settings.AiIntentEnabled;
        AiIntentAutoAnalyze = settings.AiIntentAutoAnalyze;

        LoadSavedApiKeys();
    }

    private void RefreshAiCloudModels()
    {
        AiCloudModels.Clear();
        if (SelectedAiProvider == null) return;

        try
        {
            var aiService = App.Services.GetRequiredService<AiService>();
            var provider = aiService.GetProvider(SelectedAiProvider.Id);
            if (provider == null) return;

            foreach (var model in provider.AvailableModels)
                AiCloudModels.Add(model);

            SelectedAiCloudModel ??= AiCloudModels.FirstOrDefault();
        }
        catch { /* AI service may not be ready */ }
    }

    private void RefreshAiLocalModels()
    {
        AiLocalModels.Clear();
        try
        {
            var modelManager = App.Services.GetRequiredService<AiModelManager>();
            foreach (var modelName in modelManager.AvailableModels)
            {
                AiLocalModels.Add(new AiLocalModelOption(
                    modelName,
                    modelName,
                    modelManager.GetModelSizeDisplay(modelName),
                    modelManager.IsModelDownloaded(modelName)));
            }
        }
        catch { /* model manager may not be ready */ }
    }

    private static readonly Dictionary<string, (string DisplayName, string BlobId)> ProviderKeyMap = new()
    {
        ["openai"] = ("OpenAI", "openai-api-key"),
        ["anthropic"] = ("Anthropic", "anthropic-api-key"),
        ["gemini"] = ("Google Gemini", "gemini-api-key"),
    };

    private void LoadSavedApiKeys()
    {
        SavedApiKeys.Clear();
        var hints = _settingsService.Settings.AiSavedKeyHints;
        foreach (var (providerId, hint) in hints)
        {
            var displayName = ProviderKeyMap.TryGetValue(providerId, out var info)
                ? info.DisplayName : providerId;
            SavedApiKeys.Add(new SavedApiKeyEntry(providerId, displayName, hint));
        }
    }

    // ── Change Handlers ────────────────────────────────────────────────

    partial void OnAiEnabledChanged(bool value)
    {
        _settingsService.Settings.AiEnabled = value;
        _settingsService.SaveDebounced();
        WeakReferenceMessenger.Default.Send(new IntentSettingsChangedMessage());
    }

    partial void OnSelectedAiProviderChanged(AiProviderOption? value)
    {
        if (value == null) return;
        _settingsService.Settings.AiProvider = value.Id;
        _settingsService.SaveDebounced();

        // Reset API key display
        AiApiKey = null;
        AiApiKeyStatus = null;
        AiApiKeySaveLabel = "Save";

        RefreshAiCloudModels();
    }

    partial void OnSelectedAiCloudModelChanged(AiModelInfo? value)
    {
        if (value == null) return;
        _settingsService.Settings.AiModel = value.Id;
        _settingsService.SaveDebounced();
    }

    partial void OnSelectedAiLocalModelChanged(AiLocalModelOption? value)
    {
        if (value == null) return;
        _settingsService.Settings.AiLocalModel = value.Id;
        _settingsService.SaveDebounced();
    }

    partial void OnAiTemperatureChanged(double value)
    {
        _settingsService.Settings.AiTemperature = value;
        _settingsService.SaveDebounced();
    }

    partial void OnAiIntentEnabledChanged(bool value)
    {
        _settingsService.Settings.AiIntentEnabled = value;
        _settingsService.SaveDebounced();
        WeakReferenceMessenger.Default.Send(new IntentSettingsChangedMessage());
    }

    partial void OnAiIntentAutoAnalyzeChanged(bool value)
    {
        _settingsService.Settings.AiIntentAutoAnalyze = value;
        _settingsService.SaveDebounced();
    }

    // ── Commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAiApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(AiApiKey) || SelectedAiProvider == null)
            return;

        var blobId = SelectedAiProvider.Id switch
        {
            "openai" => "openai-api-key",
            "anthropic" => "anthropic-api-key",
            "gemini" => "gemini-api-key",
            _ => null
        };

        if (blobId == null) return;

        try
        {
            var sdk = App.Services.GetRequiredService<IPrivStackSdk>();

            // Ensure vault exists and is unlocked.
            // RequestVaultUnlockAsync handles both initialization (if needed) and unlock.
            var isUnlocked = await sdk.VaultIsUnlocked("ai-vault");
            if (!isUnlocked)
            {
                var unlocked = await sdk.RequestVaultUnlockAsync("ai-vault");
                if (!unlocked) { AiApiKeyStatus = "Vault unlock required"; return; }
            }

            var keyBytes = System.Text.Encoding.UTF8.GetBytes(AiApiKey.Trim());
            await sdk.VaultBlobStore("ai-vault", blobId, keyBytes);

            // Store hint (last 4 chars) for display
            var trimmedKey = AiApiKey.Trim();
            var hint = trimmedKey.Length >= 4 ? trimmedKey[^4..] : trimmedKey;
            _settingsService.Settings.AiSavedKeyHints[SelectedAiProvider.Id] = hint;
            _settingsService.SaveDebounced();

            AiApiKeyStatus = "API key saved to vault";
            AiApiKeySaveLabel = "Saved";
            AiApiKey = null;

            LoadSavedApiKeys();

            // Clear cached key in provider
            var aiService = App.Services.GetRequiredService<AiService>();
            if (aiService.GetProvider(SelectedAiProvider.Id) is OpenAiProvider oai) oai.ClearCachedKey();
            if (aiService.GetProvider(SelectedAiProvider.Id) is AnthropicProvider ant) ant.ClearCachedKey();
            if (aiService.GetProvider(SelectedAiProvider.Id) is GeminiProvider gem) gem.ClearCachedKey();
        }
        catch (Exception ex)
        {
            AiApiKeyStatus = $"Failed to save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadAiLocalModelAsync()
    {
        if (SelectedAiLocalModel == null || SelectedAiLocalModel.IsDownloaded)
            return;

        try
        {
            IsAiLocalModelDownloading = true;
            AiLocalModelDownloadStatus = $"Downloading {SelectedAiLocalModel.DisplayName}...";

            var modelManager = App.Services.GetRequiredService<AiModelManager>();
            modelManager.PropertyChanged += OnAiModelManagerPropertyChanged;

            await modelManager.DownloadModelAsync(SelectedAiLocalModel.Id);

            AiLocalModelDownloadStatus = "Download complete";
            RefreshAiLocalModels();

            // Re-select the model
            SelectedAiLocalModel = AiLocalModels.FirstOrDefault(m => m.Id == SelectedAiLocalModel?.Id);
        }
        catch (OperationCanceledException)
        {
            AiLocalModelDownloadStatus = "Download cancelled";
        }
        catch (Exception ex)
        {
            AiLocalModelDownloadStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsAiLocalModelDownloading = false;
            var modelManager = App.Services.GetRequiredService<AiModelManager>();
            modelManager.PropertyChanged -= OnAiModelManagerPropertyChanged;
        }
    }

    [RelayCommand]
    private async Task DeleteAiApiKeyAsync(SavedApiKeyEntry? entry)
    {
        if (entry == null) return;

        if (!ProviderKeyMap.TryGetValue(entry.ProviderId, out var info)) return;

        try
        {
            var sdk = App.Services.GetRequiredService<IPrivStackSdk>();

            var isUnlocked = await sdk.VaultIsUnlocked("ai-vault");
            if (!isUnlocked)
            {
                var unlocked = await sdk.RequestVaultUnlockAsync("ai-vault");
                if (!unlocked) { AiApiKeyStatus = "Vault unlock required"; return; }
            }

            await sdk.VaultBlobDelete("ai-vault", info.BlobId);

            _settingsService.Settings.AiSavedKeyHints.Remove(entry.ProviderId);
            _settingsService.SaveDebounced();

            LoadSavedApiKeys();

            // Clear cached key in the provider
            var aiService = App.Services.GetRequiredService<AiService>();
            var provider = aiService.GetProvider(entry.ProviderId);
            if (provider is OpenAiProvider oai) oai.ClearCachedKey();
            if (provider is AnthropicProvider ant) ant.ClearCachedKey();
            if (provider is GeminiProvider gem) gem.ClearCachedKey();

            AiApiKeyStatus = $"{entry.ProviderDisplayName} API key deleted";
        }
        catch (Exception ex)
        {
            AiApiKeyStatus = $"Failed to delete: {ex.Message}";
        }
    }

    private void OnAiModelManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiModelManager.DownloadProgress))
        {
            var modelManager = App.Services.GetRequiredService<AiModelManager>();
            AiLocalModelDownloadProgress = modelManager.DownloadProgress;
            AiLocalModelDownloadStatus = $"Downloading... {modelManager.DownloadProgress:F0}%";
        }
    }
}
