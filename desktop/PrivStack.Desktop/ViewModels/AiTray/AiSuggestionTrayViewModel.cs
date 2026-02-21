using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Unified ViewModel for the global AI suggestion tray.
/// Aggregates intent suggestion cards (from IntentEngine) and content suggestion cards
/// (pushed by plugins via IAiSuggestionService) into a single collection.
/// </summary>
public partial class AiSuggestionTrayViewModel : ViewModelBase,
    IRecipient<IntentSettingsChangedMessage>,
    IRecipient<ContentSuggestionPushedMessage>,
    IRecipient<ContentSuggestionUpdatedMessage>,
    IRecipient<ContentSuggestionRemovedMessage>
{
    private static readonly ILogger _log = Log.ForContext<AiSuggestionTrayViewModel>();

    private readonly IIntentEngine _intentEngine;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppSettingsService _appSettings;
    private readonly IAiService _aiService;

    public AiSuggestionTrayViewModel(
        IIntentEngine intentEngine,
        IUiDispatcher dispatcher,
        IAppSettingsService appSettings,
        IAiService aiService)
    {
        _intentEngine = intentEngine;
        _dispatcher = dispatcher;
        _appSettings = appSettings;
        _aiService = aiService;

        // Subscribe to IntentEngine events for intent cards
        _intentEngine.SuggestionAdded += OnIntentSuggestionAdded;
        _intentEngine.SuggestionRemoved += OnIntentSuggestionRemoved;
        _intentEngine.SuggestionsCleared += OnIntentSuggestionsCleared;

        // Subscribe to messenger messages
        WeakReferenceMessenger.Default.Register<IntentSettingsChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionPushedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionRemovedMessage>(this);

        // Load existing intent suggestions
        foreach (var suggestion in _intentEngine.PendingSuggestions)
        {
            Cards.Add(new IntentSuggestionCardViewModel(suggestion, _intentEngine));
        }
        UpdateCounts();
    }

    // ── Properties ───────────────────────────────────────────────────

    public ObservableCollection<IAiTrayCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCards))]
    private int _pendingCount;

    public bool HasCards => PendingCount > 0;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// True when there are active suggestion cards — drives the gold spin animation on the status bar icon.
    /// </summary>
    [ObservableProperty]
    private bool _hasActiveNotification;

    /// <summary>
    /// The tray is visible when any AI feature is enabled (not just intents).
    /// </summary>
    public bool IsEnabled => _appSettings.Settings.AiEnabled && _aiService.IsAvailable;

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void ClearAll()
    {
        _intentEngine.ClearAll();

        // Dismiss all content cards via messenger so plugins can clean up
        var contentCards = Cards.OfType<ContentSuggestionCardViewModel>().ToList();
        foreach (var card in contentCards)
        {
            card.DismissCommand.Execute(null);
        }
    }

    // ── Intent Engine Event Handlers ─────────────────────────────────

    private void OnIntentSuggestionAdded(object? sender, IntentSuggestion suggestion)
    {
        _dispatcher.Post(() =>
        {
            Cards.Insert(0, new IntentSuggestionCardViewModel(suggestion, _intentEngine));
            UpdateCounts();
        });
    }

    private void OnIntentSuggestionRemoved(object? sender, string suggestionId)
    {
        _dispatcher.Post(() =>
        {
            var card = Cards.FirstOrDefault(c => c.CardId == suggestionId);
            if (card != null) Cards.Remove(card);
            UpdateCounts();
        });
    }

    private void OnIntentSuggestionsCleared(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            var intentCards = Cards.Where(c => c.CardType == AiTrayCardType.Intent).ToList();
            foreach (var card in intentCards)
                Cards.Remove(card);
            UpdateCounts();
        });
    }

    // ── Messenger Handlers ───────────────────────────────────────────

    public void Receive(IntentSettingsChangedMessage message)
    {
        _dispatcher.Post(() => OnPropertyChanged(nameof(IsEnabled)));
    }

    public void Receive(ContentSuggestionPushedMessage message)
    {
        _dispatcher.Post(() =>
        {
            var vm = new ContentSuggestionCardViewModel(message.Card)
            {
                State = message.Card.State,
                IsExpanded = true
            };
            Cards.Insert(0, vm);
            UpdateCounts();
        });
    }

    public void Receive(ContentSuggestionUpdatedMessage message)
    {
        _dispatcher.Post(() =>
        {
            var card = Cards
                .OfType<ContentSuggestionCardViewModel>()
                .FirstOrDefault(c => c.CardId == message.SuggestionId);
            card?.ApplyUpdate(message);
        });
    }

    public void Receive(ContentSuggestionRemovedMessage message)
    {
        _dispatcher.Post(() =>
        {
            var card = Cards.FirstOrDefault(c => c.CardId == message.SuggestionId);
            if (card != null) Cards.Remove(card);
            UpdateCounts();
        });
    }

    // ── Internals ────────────────────────────────────────────────────

    private void UpdateCounts()
    {
        PendingCount = Cards.Count;
        HasActiveNotification = Cards.Count > 0;
    }
}
