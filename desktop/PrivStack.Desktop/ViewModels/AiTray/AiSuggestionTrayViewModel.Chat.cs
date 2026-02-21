using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Free-form chat input logic for the AI suggestion tray.
/// </summary>
public partial class AiSuggestionTrayViewModel
{
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
            var tier = AiPersona.Classify(text);
            var request = new AiRequest
            {
                SystemPrompt = AiPersona.GetSystemPrompt(tier),
                UserPrompt = text,
                MaxTokens = AiPersona.MaxTokensFor(tier),
                Temperature = 0.4,
                FeatureId = "tray.chat"
            };

            var response = await _aiService.CompleteAsync(request);

            if (response.Success && !string.IsNullOrEmpty(response.Content))
            {
                assistantMsg.Content = response.Content;
                assistantMsg.State = ChatMessageState.Ready;
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
}
