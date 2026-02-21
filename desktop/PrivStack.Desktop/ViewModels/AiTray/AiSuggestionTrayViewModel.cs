using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Unified ViewModel for the global AI chat tray.
/// Renders intent and content suggestions as conversation bubbles (User → Assistant),
/// and supports free-form chat input at the bottom.
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
    internal readonly IAiService _aiService;

    /// <summary>Maps SuggestionId → Assistant MessageId for update routing.</summary>
    private readonly Dictionary<string, string> _suggestionToAssistantId = new();

    /// <summary>Maps SuggestionId → User MessageId for removal.</summary>
    private readonly Dictionary<string, string> _suggestionToUserMsgId = new();

    /// <summary>
    /// Set by MainWindowViewModel to enable source entity navigation without coupling.
    /// </summary>
    public Func<string, string, Task>? NavigateToLinkedItemFunc { get; set; }

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

        // Subscribe to IntentEngine events
        _intentEngine.SuggestionAdded += OnIntentSuggestionAdded;
        _intentEngine.SuggestionRemoved += OnIntentSuggestionRemoved;
        _intentEngine.SuggestionsCleared += OnIntentSuggestionsCleared;

        // Subscribe to messenger messages
        WeakReferenceMessenger.Default.Register<IntentSettingsChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionPushedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<ContentSuggestionRemovedMessage>(this);

        // Load existing intent suggestions as assistant messages
        foreach (var suggestion in _intentEngine.PendingSuggestions)
            AddIntentAsAssistantMessage(suggestion);
        UpdateCounts();
    }

    // ── Properties ───────────────────────────────────────────────────

    public ObservableCollection<AiChatMessageViewModel> Messages { get; } = [];

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

        // Dismiss all content-linked assistant messages via messenger
        var contentMsgs = Messages
            .Where(m => m is { Role: ChatMessageRole.Assistant, SuggestionId: not null, SourcePluginId: not null })
            .ToList();
        foreach (var msg in contentMsgs)
            msg.DismissCommand.Execute(null);

        Messages.Clear();
        _suggestionToAssistantId.Clear();
        _suggestionToUserMsgId.Clear();
        UpdateCounts();
    }

    // ── Intent Engine Event Handlers ─────────────────────────────────

    private void OnIntentSuggestionAdded(object? sender, IntentSuggestion suggestion)
    {
        _dispatcher.Post(() =>
        {
            AddIntentAsAssistantMessage(suggestion);
            UpdateCounts();
            ShowBalloon($"I noticed something: {suggestion.Summary}");
        });
    }

    private void OnIntentSuggestionRemoved(object? sender, string suggestionId)
    {
        _dispatcher.Post(() =>
        {
            RemoveMessageBySuggestionId(suggestionId);
            UpdateCounts();
        });
    }

    private void OnIntentSuggestionsCleared(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            var intentMsgs = Messages.Where(m => m.SuggestionId?.StartsWith("intent:") == true).ToList();
            foreach (var msg in intentMsgs)
                Messages.Remove(msg);
            UpdateCounts();
        });
    }

    private void AddIntentAsAssistantMessage(IntentSuggestion suggestion)
    {
        var assistantMsg = new AiChatMessageViewModel(ChatMessageRole.Assistant)
        {
            SuggestionId = $"intent:{suggestion.SuggestionId}",
            Content = suggestion.Summary,
            State = ChatMessageState.Ready,
            SourcePluginId = suggestion.MatchedIntent.PluginId,
            SourceEntityId = suggestion.SourceSignal.EntityId,
            SourceEntityType = suggestion.SourceSignal.EntityType,
            SourceEntityTitle = suggestion.SourceSignal.EntityTitle,
            NavigateToSourceFunc = NavigateToLinkedItemFunc
        };

        // Intent suggestions don't have standard SuggestionAction buttons —
        // the IntentSuggestionCardViewModel handled accept/edit/dismiss via IntentEngine.
        // For now we show intent messages as read-only assistant bubbles.
        Messages.Add(assistantMsg);
        RequestScrollToBottom();
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
            var card = message.Card;

            // Create user bubble
            var userLabel = card.UserPromptLabel ?? $"Hey PrivStack, {card.Title}";
            var userMsg = new AiChatMessageViewModel(ChatMessageRole.User)
            {
                SuggestionId = card.SuggestionId,
                UserLabel = userLabel,
                SourceEntityId = card.SourceEntityId,
                SourceEntityType = card.SourceEntityType,
                SourceEntityTitle = card.SourceEntityTitle,
                SourcePluginId = card.PluginId,
                NavigateToSourceFunc = NavigateToLinkedItemFunc
            };
            Messages.Add(userMsg);
            _suggestionToUserMsgId[card.SuggestionId] = userMsg.MessageId;

            // Create assistant bubble
            var assistantMsg = new AiChatMessageViewModel(ChatMessageRole.Assistant)
            {
                SuggestionId = card.SuggestionId,
                Content = card.Content,
                State = AiChatMessageViewModel.MapState(card.State),
                SourcePluginId = card.PluginId,
                SourceEntityId = card.SourceEntityId,
                SourceEntityType = card.SourceEntityType,
                SourceEntityTitle = card.SourceEntityTitle,
                NavigateToSourceFunc = NavigateToLinkedItemFunc
            };
            foreach (var action in card.Actions)
                assistantMsg.Actions.Add(action);

            Messages.Add(assistantMsg);
            _suggestionToAssistantId[card.SuggestionId] = assistantMsg.MessageId;

            UpdateCounts();
            RequestScrollToBottom();

            if (card.State == ContentSuggestionState.Loading)
                ShowBalloon($"Working on your request...");
        });
    }

    public void Receive(ContentSuggestionUpdatedMessage message)
    {
        _dispatcher.Post(() =>
        {
            if (!_suggestionToAssistantId.TryGetValue(message.SuggestionId, out var assistantMsgId))
                return;

            var assistantMsg = Messages.FirstOrDefault(m => m.MessageId == assistantMsgId);
            if (assistantMsg == null) return;

            assistantMsg.ApplyUpdate(message);

            if (message.NewState == ContentSuggestionState.Ready)
                ShowBalloon("Your result is ready!");
        });
    }

    public void Receive(ContentSuggestionRemovedMessage message)
    {
        _dispatcher.Post(() =>
        {
            RemoveMessageBySuggestionId(message.SuggestionId);
            UpdateCounts();
        });
    }

    // ── Internals ────────────────────────────────────────────────────

    private void RemoveMessageBySuggestionId(string suggestionId)
    {
        // Remove user message
        if (_suggestionToUserMsgId.TryGetValue(suggestionId, out var userMsgId))
        {
            var userMsg = Messages.FirstOrDefault(m => m.MessageId == userMsgId);
            if (userMsg != null) Messages.Remove(userMsg);
            _suggestionToUserMsgId.Remove(suggestionId);
        }

        // Remove assistant message
        if (_suggestionToAssistantId.TryGetValue(suggestionId, out var assistantMsgId))
        {
            var assistantMsg = Messages.FirstOrDefault(m => m.MessageId == assistantMsgId);
            if (assistantMsg != null) Messages.Remove(assistantMsg);
            _suggestionToAssistantId.Remove(suggestionId);
        }

        // Also check intent-prefixed IDs
        var intentKey = $"intent:{suggestionId}";
        var intentMsg = Messages.FirstOrDefault(m => m.SuggestionId == intentKey);
        if (intentMsg != null) Messages.Remove(intentMsg);
    }

    private void UpdateCounts()
    {
        PendingCount = Messages.Count;
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
