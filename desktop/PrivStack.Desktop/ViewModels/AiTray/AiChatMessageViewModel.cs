using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;

namespace PrivStack.Desktop.ViewModels.AiTray;

public enum ChatMessageRole { User, Assistant }

public enum ChatMessageState { Loading, Ready, Error, Applied }

/// <summary>
/// Represents a single message bubble in the AI chat tray.
/// User bubbles show the request label; Assistant bubbles show AI-generated content.
/// </summary>
public partial class AiChatMessageViewModel : ViewModelBase
{
    public string MessageId { get; }
    public ChatMessageRole Role { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>Suggestion ID linking this message to the SDK card lifecycle. Null for free-form chat.</summary>
    public string? SuggestionId { get; init; }

    // ── User Bubble Properties ───────────────────────────────────────

    /// <summary>The human-readable request label (e.g., "Summarize this block").</summary>
    public string? UserLabel { get; init; }

    // ── Source Entity (clickable link in user bubbles) ────────────────

    public string? SourceEntityId { get; init; }
    public string? SourceEntityType { get; init; }
    public string? SourceEntityTitle { get; init; }
    public string? SourcePluginId { get; init; }

    public bool HasSourceLink => !string.IsNullOrEmpty(SourceEntityId) && !string.IsNullOrEmpty(SourceEntityType);

    // ── Assistant Bubble Properties ──────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(IsApplied))]
    private ChatMessageState _state;

    [ObservableProperty]
    private string? _content;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<SuggestionAction> Actions { get; } = [];

    public bool IsUser => Role == ChatMessageRole.User;
    public bool IsAssistant => Role == ChatMessageRole.Assistant;
    public bool IsLoading => State == ChatMessageState.Loading;
    public bool IsReady => State == ChatMessageState.Ready;
    public bool IsError => State == ChatMessageState.Error;
    public bool IsApplied => State == ChatMessageState.Applied;

    // ── Navigation callback ──────────────────────────────────────────

    /// <summary>
    /// Set by the tray VM to wire navigation without coupling to MainWindowViewModel.
    /// </summary>
    public Func<string, string, Task>? NavigateToSourceFunc { get; init; }

    public AiChatMessageViewModel(ChatMessageRole role)
    {
        MessageId = Guid.NewGuid().ToString();
        Role = role;
        Timestamp = DateTimeOffset.UtcNow;
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void ExecuteAction(SuggestionAction action)
    {
        if (string.IsNullOrEmpty(SuggestionId) || string.IsNullOrEmpty(SourcePluginId)) return;
        WeakReferenceMessenger.Default.Send(new ContentSuggestionActionRequestedMessage
        {
            SuggestionId = SuggestionId,
            PluginId = SourcePluginId,
            ActionId = action.ActionId
        });
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (string.IsNullOrEmpty(SuggestionId) || string.IsNullOrEmpty(SourcePluginId)) return;
        WeakReferenceMessenger.Default.Send(new ContentSuggestionDismissedMessage
        {
            SuggestionId = SuggestionId,
            PluginId = SourcePluginId
        });
    }

    [RelayCommand]
    private async Task NavigateToSourceAsync()
    {
        if (NavigateToSourceFunc == null || string.IsNullOrEmpty(SourceEntityType) || string.IsNullOrEmpty(SourceEntityId))
            return;
        await NavigateToSourceFunc(SourceEntityType, SourceEntityId);
    }

    /// <summary>
    /// Applies an update from a ContentSuggestionUpdatedMessage to this assistant bubble.
    /// </summary>
    public void ApplyUpdate(ContentSuggestionUpdatedMessage message)
    {
        if (message.NewState.HasValue)
            State = MapState(message.NewState.Value);

        if (message.NewContent != null)
            Content = message.NewContent;

        if (message.ErrorMessage != null)
            ErrorMessage = message.ErrorMessage;

        if (message.NewActions != null)
        {
            Actions.Clear();
            foreach (var action in message.NewActions)
                Actions.Add(action);
        }
    }

    internal static ChatMessageState MapState(ContentSuggestionState state) => state switch
    {
        ContentSuggestionState.Loading => ChatMessageState.Loading,
        ContentSuggestionState.Ready => ChatMessageState.Ready,
        ContentSuggestionState.Error => ChatMessageState.Error,
        ContentSuggestionState.Applied => ChatMessageState.Applied,
        _ => ChatMessageState.Ready
    };
}
