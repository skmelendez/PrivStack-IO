using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.AI;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Unified ViewModel for the global AI chat tray.
/// Split into partial files: Chat, Intents, History.
/// </summary>
public partial class AiSuggestionTrayViewModel : ViewModelBase,
    IRecipient<IntentSettingsChangedMessage>,
    IRecipient<ContentSuggestionPushedMessage>,
    IRecipient<ContentSuggestionUpdatedMessage>,
    IRecipient<ContentSuggestionRemovedMessage>
{
    private static readonly ILogger _log = Serilog.Log.ForContext<AiSuggestionTrayViewModel>();

    private readonly IIntentEngine _intentEngine;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppSettingsService _appSettings;
    internal readonly AiService _aiService;
    private readonly AiMemoryService _memoryService;
    private readonly AiMemoryExtractor _memoryExtractor;
    private readonly AiConversationStore _conversationStore;
    private readonly InfoPanelService _infoPanelService;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IPrivStackSdk _sdk;

    /// <summary>
    /// Set by MainWindowViewModel to enable source entity navigation without coupling.
    /// </summary>
    public Func<string, string, Task>? NavigateToLinkedItemFunc { get; set; }

    internal AiSuggestionTrayViewModel(
        IIntentEngine intentEngine,
        IUiDispatcher dispatcher,
        IAppSettingsService appSettings,
        AiService aiService,
        AiMemoryService memoryService,
        AiMemoryExtractor memoryExtractor,
        AiConversationStore conversationStore,
        InfoPanelService infoPanelService,
        IPluginRegistry pluginRegistry,
        IPrivStackSdk sdk)
    {
        _intentEngine = intentEngine;
        _dispatcher = dispatcher;
        _appSettings = appSettings;
        _aiService = aiService;
        _memoryService = memoryService;
        _memoryExtractor = memoryExtractor;
        _conversationStore = conversationStore;
        _infoPanelService = infoPanelService;
        _pluginRegistry = pluginRegistry;
        _sdk = sdk;

        // Subscribe to IntentEngine events
        _intentEngine.SuggestionAdded += OnIntentSuggestionAdded;
        _intentEngine.SuggestionRemoved += OnIntentSuggestionRemoved;
        _intentEngine.SuggestionsCleared += OnIntentSuggestionsCleared;

        // Subscribe to messenger messages
        WeakReferenceMessenger.Default.Register<IntentSettingsChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionPushedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionRemovedMessage>(this);

        // Subscribe to active item changes for context injection
        _infoPanelService.ActiveItemChanged += OnActiveItemChanged;

        // Load existing intent suggestions
        foreach (var suggestion in _intentEngine.PendingSuggestions)
            AddIntentAsAssistantMessage(suggestion);
        UpdateCounts();
    }

    // ── Collections ──────────────────────────────────────────────────

    public ObservableCollection<AiChatMessageViewModel> ChatMessages { get; } = [];
    public ObservableCollection<AiChatMessageViewModel> IntentMessages { get; } = [];

    // ── Tab Selection ────────────────────────────────────────────────

    [ObservableProperty]
    private int _selectedTabIndex;

    // ── Properties ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCards))]
    private int _pendingCount;

    public bool HasCards => PendingCount > 0;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBalloonMessage))]
    private string? _balloonMessage;

    public bool HasBalloonMessage => !string.IsNullOrEmpty(BalloonMessage);

    private CancellationTokenSource? _balloonDismissCts;

    public bool IsEnabled => _appSettings.Settings.AiEnabled && _aiService.IsAvailable;

    /// <summary>Raised when the view should scroll to the bottom.</summary>
    public event EventHandler? ScrollToBottomRequested;

    // ── Active Item Context ──────────────────────────────────────────

    private string? _activeItemContextShort;
    private string? _activeItemContextFull;

    private void OnActiveItemChanged()
    {
        var linkType = _infoPanelService.ActiveLinkType;
        var itemId = _infoPanelService.ActiveItemId;
        var title = _infoPanelService.ActiveItemTitle;

        if (string.IsNullOrEmpty(linkType) || string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(title))
        {
            _activeItemContextShort = null;
            _activeItemContextFull = null;
            return;
        }

        _activeItemContextShort = $"Currently viewing: {title} ({linkType})";
        _activeItemContextFull = _activeItemContextShort; // default until entity loads

        _ = FetchActiveItemEntityAsync(linkType, itemId, title);
    }

    private async Task FetchActiveItemEntityAsync(string linkType, string itemId, string title)
    {
        try
        {
            var displayName = EntityTypeMap.GetDisplayName(linkType) ?? linkType;
            string? json = null;

            // Try SDK entity read for mapped types
            var entityType = EntityTypeMap.GetEntityType(linkType);
            if (entityType != null)
            {
                json = await FetchEntityViaSDkAsync(entityType, itemId);

                // For notes, append embedded dataset/table content
                if (json != null && linkType == "page")
                    json = await AppendEmbeddedDatasetContentAsync(json, itemId);
            }

            // Fallback: query via IPluginDataSourceProvider for unmapped types (e.g. dataset_row)
            if (json == null)
                json = await FetchEntityViaDataSourceProviderAsync(linkType, itemId);

            if (json == null || _infoPanelService.ActiveItemId != itemId) return;

            const int maxContextChars = 8000;
            if (json.Length > maxContextChars)
                json = json[..maxContextChars] + "\n... (truncated)";

            _activeItemContextFull =
                $"The user is currently viewing a {displayName} item: \"{title}\"\n" +
                $"Full entity data (JSON):\n```json\n{json}\n```";
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to fetch active item entity for context: {LinkType}:{ItemId}", linkType, itemId);
        }
    }

    private async Task<string?> FetchEntityViaSDkAsync(string entityType, string itemId)
    {
        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "privstack.graph",
            Action = SdkAction.Read,
            EntityType = entityType,
            EntityId = itemId,
        });

        if (!response.Success || response.Data.ValueKind == JsonValueKind.Undefined) return null;
        return JsonSerializer.Serialize(response.Data, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string?> FetchEntityViaDataSourceProviderAsync(string linkType, string itemId)
    {
        var providers = _pluginRegistry.GetCapabilityProviders<PrivStack.Sdk.Capabilities.IPluginDataSourceProvider>();
        var provider = providers.FirstOrDefault(p => p.NavigationLinkType == linkType);
        if (provider == null) return null;

        try
        {
            // Query the provider filtering by ID — fetch a small page and serialize
            var result = await provider.QueryItemAsync("all", page: 0, pageSize: 50, filterText: itemId);
            if (result.Rows.Count == 0) return null;

            // Build a readable representation: column headers + matching rows
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Columns: {string.Join(", ", result.Columns)}");
            foreach (var row in result.Rows)
            {
                var fields = new List<string>();
                for (var i = 0; i < Math.Min(row.Count, result.Columns.Count); i++)
                    fields.Add($"{result.Columns[i]}: {row[i]}");
                sb.AppendLine(string.Join(", ", fields));
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "DataSourceProvider query failed for {LinkType}:{ItemId}", linkType, itemId);
            return null;
        }
    }

    private async Task<string> AppendEmbeddedDatasetContentAsync(string noteJson, string itemId)
    {
        try
        {
            // Parse the note JSON to find embedded table/dataset references
            using var doc = JsonDocument.Parse(noteJson);
            var root = doc.RootElement;

            // Look for content field that may contain table block references
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                return noteJson;

            var contentStr = content.GetString() ?? "";

            // Find dataset_id references in the note content (tables reference datasets by ID)
            var datasetIds = new HashSet<string>();
            var idx = 0;
            while ((idx = contentStr.IndexOf("dataset_id", idx, StringComparison.Ordinal)) >= 0)
            {
                // Simple extraction: find the value after dataset_id
                var start = contentStr.IndexOf('"', idx + 10);
                if (start < 0) { idx++; continue; }
                start++; // skip opening quote
                var end = contentStr.IndexOf('"', start);
                if (end > start && end - start < 100)
                    datasetIds.Add(contentStr[start..end]);
                idx = end > 0 ? end : idx + 1;
            }

            if (datasetIds.Count == 0) return noteJson;

            // Fetch each dataset's first rows via IPluginDataSourceProvider
            var providers = _pluginRegistry.GetCapabilityProviders<PrivStack.Sdk.Capabilities.IPluginDataSourceProvider>();
            var dataProvider = providers.FirstOrDefault(p => p.NavigationLinkType == "dataset_row");
            if (dataProvider == null) return noteJson;

            var sb = new System.Text.StringBuilder(noteJson);
            foreach (var dsId in datasetIds.Take(3)) // cap at 3 datasets
            {
                try
                {
                    var result = await dataProvider.QueryItemAsync(dsId, page: 0, pageSize: 20);
                    if (result.Rows.Count == 0) continue;

                    sb.AppendLine();
                    sb.AppendLine($"\n--- Embedded Dataset ({dsId}) ---");
                    sb.AppendLine($"Columns: {string.Join(", ", result.Columns)}");
                    sb.AppendLine($"Total rows: {result.TotalCount}");
                    foreach (var row in result.Rows)
                    {
                        var fields = new List<string>();
                        for (var i = 0; i < Math.Min(row.Count, result.Columns.Count); i++)
                            fields.Add($"{result.Columns[i]}: {row[i]}");
                        sb.AppendLine(string.Join(" | ", fields));
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Failed to fetch embedded dataset {DatasetId} for note {NoteId}", dsId, itemId);
                }
            }
            return sb.ToString();
        }
        catch
        {
            return noteJson; // If parsing fails, just return the original
        }
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen) BalloonMessage = null;
    }

    [RelayCommand]
    private void DismissBalloon() => BalloonMessage = null;

    [RelayCommand]
    private void ClearAll()
    {
        _intentEngine.ClearAll();

        var contentMsgs = IntentMessages
            .Where(m => m is { Role: ChatMessageRole.Assistant, SuggestionId: not null, SourcePluginId: not null })
            .ToList();
        foreach (var msg in contentMsgs)
            msg.DismissCommand.Execute(null);

        ChatMessages.Clear();
        IntentMessages.Clear();
        _suggestionToAssistantId.Clear();
        _suggestionToUserMsgId.Clear();
        UpdateCounts();
    }

    // ── Messenger Handler ────────────────────────────────────────────

    public void Receive(IntentSettingsChangedMessage message)
    {
        _dispatcher.Post(() => OnPropertyChanged(nameof(IsEnabled)));
    }

    // ── Internals ────────────────────────────────────────────────────

    private void UpdateCounts()
    {
        PendingCount = ChatMessages.Count + IntentMessages.Count;
    }

    internal void RequestScrollToBottom()
    {
        ScrollToBottomRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowBalloon(string message)
    {
        _balloonDismissCts?.Cancel();
        BalloonMessage = message;

        var cts = new CancellationTokenSource();
        _balloonDismissCts = cts;

        _ = Task.Delay(TimeSpan.FromSeconds(6), cts.Token).ContinueWith(_ =>
        {
            _dispatcher.Post(() => BalloonMessage = null);
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
