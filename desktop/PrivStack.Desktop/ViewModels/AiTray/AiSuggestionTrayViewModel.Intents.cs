using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Intent and content suggestion handlers — operates on <see cref="IntentMessages"/>.
/// </summary>
public partial class AiSuggestionTrayViewModel
{
    /// <summary>Maps SuggestionId → Assistant MessageId for update routing.</summary>
    private readonly Dictionary<string, string> _suggestionToAssistantId = new();

    /// <summary>Maps SuggestionId → User MessageId for removal.</summary>
    private readonly Dictionary<string, string> _suggestionToUserMsgId = new();

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
            var intentMsgs = IntentMessages
                .Where(m => m.SuggestionId?.StartsWith("intent:") == true).ToList();
            foreach (var msg in intentMsgs)
                IntentMessages.Remove(msg);
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

        IntentMessages.Add(assistantMsg);
        RequestScrollToBottom();
    }

    // ── Content Suggestion Messenger Handlers ────────────────────────

    public void Receive(ContentSuggestionPushedMessage message)
    {
        _dispatcher.Post(() =>
        {
            var card = message.Card;

            var userLabel = card.UserPromptLabel ?? $"Hey {AiPersona.Name}, {card.Title}";
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
            IntentMessages.Add(userMsg);
            _suggestionToUserMsgId[card.SuggestionId] = userMsg.MessageId;

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

            IntentMessages.Add(assistantMsg);
            _suggestionToAssistantId[card.SuggestionId] = assistantMsg.MessageId;

            UpdateCounts();
            RequestScrollToBottom();

            if (card.State == ContentSuggestionState.Loading)
                ShowBalloon("Working on your request...");
        });
    }

    public void Receive(ContentSuggestionUpdatedMessage message)
    {
        _dispatcher.Post(() =>
        {
            if (!_suggestionToAssistantId.TryGetValue(message.SuggestionId, out var assistantMsgId))
                return;

            var assistantMsg = IntentMessages.FirstOrDefault(m => m.MessageId == assistantMsgId);
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

    public void Receive(ContentSuggestionDismissedMessage message)
    {
        _dispatcher.Post(() =>
        {
            RemoveMessageBySuggestionId(message.SuggestionId);
            UpdateCounts();
        });
    }

    private void RemoveMessageBySuggestionId(string suggestionId)
    {
        if (_suggestionToUserMsgId.TryGetValue(suggestionId, out var userMsgId))
        {
            var userMsg = IntentMessages.FirstOrDefault(m => m.MessageId == userMsgId);
            if (userMsg != null) IntentMessages.Remove(userMsg);
            _suggestionToUserMsgId.Remove(suggestionId);
        }

        if (_suggestionToAssistantId.TryGetValue(suggestionId, out var assistantMsgId))
        {
            var assistantMsg = IntentMessages.FirstOrDefault(m => m.MessageId == assistantMsgId);
            if (assistantMsg != null) IntentMessages.Remove(assistantMsg);
            _suggestionToAssistantId.Remove(suggestionId);
        }

        var intentKey = $"intent:{suggestionId}";
        var intentMsg = IntentMessages.FirstOrDefault(m => m.SuggestionId == intentKey);
        if (intentMsg != null) IntentMessages.Remove(intentMsg);
    }
}
