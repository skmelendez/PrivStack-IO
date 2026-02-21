using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Free-form chat input logic for the AI suggestion tray.
/// Branches between local (minimal prompt, no history) and cloud (rich prompt, history, memory).
/// </summary>
public partial class AiSuggestionTrayViewModel
{
    private const int MaxHistoryMessages = 20;

    public string ChatWatermark { get; } = $"Ask {AiPersona.Name}...";

    [ObservableProperty]
    private string? _chatInputText;

    [ObservableProperty]
    private bool _isSendingChat;

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendChatMessageAsync()
    {
        var text = ChatInputText?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        ChatInputText = null;
        IsSendingChat = true;

        // User bubble
        var userMsg = new AiChatMessageViewModel(ChatMessageRole.User)
        {
            UserLabel = text
        };
        Messages.Add(userMsg);

        // Assistant bubble (loading)
        var assistantMsg = new AiChatMessageViewModel(ChatMessageRole.Assistant)
        {
            State = ChatMessageState.Loading
        };
        Messages.Add(assistantMsg);
        UpdateCounts();
        RequestScrollToBottom();

        try
        {
            var userName = _appSettings.Settings.UserDisplayName
                ?? Environment.UserName ?? "there";
            var tier = AiPersona.Classify(text);
            var isCloud = !IsActiveProviderLocal();

            AiRequest request;
            if (isCloud)
            {
                var memoryContext = _memoryService.FormatForPrompt();
                request = new AiRequest
                {
                    SystemPrompt = AiPersona.GetCloudSystemPrompt(tier, userName, memoryContext),
                    UserPrompt = text,
                    MaxTokens = AiPersona.CloudMaxTokensFor(tier),
                    Temperature = 0.4,
                    FeatureId = "tray.chat",
                    ConversationHistory = BuildConversationHistory()
                };
            }
            else
            {
                request = new AiRequest
                {
                    SystemPrompt = AiPersona.GetSystemPrompt(tier, userName),
                    UserPrompt = text,
                    MaxTokens = AiPersona.MaxTokensFor(tier),
                    Temperature = 0.4,
                    FeatureId = "tray.chat"
                };
            }

            var response = await _aiService.CompleteAsync(request);

            if (response.Success && !string.IsNullOrEmpty(response.Content))
            {
                var content = isCloud
                    ? response.Content
                    : AiPersona.Sanitize(response.Content, tier);

                assistantMsg.Content = content;
                assistantMsg.State = ChatMessageState.Ready;

                // Fire-and-forget memory extraction for cloud responses
                if (isCloud)
                    _ = _memoryExtractor.EvaluateAsync(text, content);
            }
            else
            {
                assistantMsg.ErrorMessage = response.ErrorMessage ?? "AI request failed";
                assistantMsg.State = ChatMessageState.Error;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Free-form chat request failed");
            assistantMsg.ErrorMessage = $"Error: {ex.Message}";
            assistantMsg.State = ChatMessageState.Error;
        }
        finally
        {
            IsSendingChat = false;
            RequestScrollToBottom();
        }
    }

    private bool CanSendChat() => !string.IsNullOrWhiteSpace(ChatInputText) && !IsSendingChat;

    partial void OnChatInputTextChanged(string? value) => SendChatMessageCommand.NotifyCanExecuteChanged();
    partial void OnIsSendingChatChanged(bool value) => SendChatMessageCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Checks whether the currently active AI provider is a local model.
    /// </summary>
    private bool IsActiveProviderLocal()
    {
        var providerId = _appSettings.Settings.AiProvider;
        if (string.IsNullOrEmpty(providerId) || providerId == "none")
            return true; // default to local behavior

        var providers = _aiService.GetProviders();
        var active = providers.FirstOrDefault(p => p.Id == providerId);
        return active?.IsLocal ?? true;
    }

    /// <summary>
    /// Builds conversation history from the last N free-form chat messages (skip suggestion-linked).
    /// Excludes the current user message (it will be the UserPrompt).
    /// </summary>
    private IReadOnlyList<AiChatMessage>? BuildConversationHistory()
    {
        var chatMessages = Messages
            .Where(m => m.SuggestionId == null && m.State != ChatMessageState.Loading)
            .TakeLast(MaxHistoryMessages)
            .ToList();

        if (chatMessages.Count == 0) return null;

        // Exclude the last user message (it's the one being sent now as UserPrompt)
        if (chatMessages.Count > 0 && chatMessages[^1].Role == ChatMessageRole.User)
            chatMessages.RemoveAt(chatMessages.Count - 1);

        if (chatMessages.Count == 0) return null;

        return chatMessages.Select(m => new AiChatMessage
        {
            Role = m.Role == ChatMessageRole.User ? "user" : "assistant",
            Content = m.Role == ChatMessageRole.User ? m.UserLabel ?? "" : m.Content ?? ""
        }).ToList();
    }
}
