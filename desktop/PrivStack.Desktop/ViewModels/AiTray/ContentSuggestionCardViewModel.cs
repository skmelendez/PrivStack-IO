using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;

namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// ViewModel for a single content suggestion card in the unified AI tray.
/// Wraps a <see cref="ContentSuggestionCard"/> and sends action/dismiss messages back to the owning plugin.
/// </summary>
public partial class ContentSuggestionCardViewModel : ViewModelBase, IAiTrayCardViewModel
{
    private ContentSuggestionCard _card;

    public ContentSuggestionCardViewModel(ContentSuggestionCard card)
    {
        _card = card;
        Actions = new ObservableCollection<SuggestionAction>(card.Actions);
    }

    // ── IAiTrayCardViewModel ──────────────────────────────────────────

    public string CardId => _card.SuggestionId;
    public string Title => _card.Title;
    public string? Summary => PreviewText;
    public string SourcePluginId => _card.PluginId;
    public DateTimeOffset CreatedAt => _card.CreatedAt;
    public AiTrayCardType CardType => AiTrayCardType.Content;

    // ── Display Properties ────────────────────────────────────────────

    public string? Content => _card.Content;
    public string? SourceEntityTitle => _card.SourceEntityTitle;

    public string PreviewText => _card.Content?.Length > 80
        ? _card.Content[..80] + "..."
        : _card.Content ?? "";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private ContentSuggestionState _state;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<SuggestionAction> Actions { get; }

    public bool IsLoading => State == ContentSuggestionState.Loading;
    public bool IsReady => State == ContentSuggestionState.Ready;
    public bool IsError => State == ContentSuggestionState.Error;
    public bool IsApplied => State == ContentSuggestionState.Applied;

    // ── Lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies an update message from the owning plugin.
    /// </summary>
    public void ApplyUpdate(ContentSuggestionUpdatedMessage message)
    {
        if (message.NewState.HasValue)
        {
            State = message.NewState.Value;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsApplied));
        }

        if (message.NewContent != null)
        {
            _card = _card with { Content = message.NewContent };
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(Summary));
        }

        if (message.ErrorMessage != null)
        {
            ErrorMessage = message.ErrorMessage;
        }

        if (message.NewActions != null)
        {
            Actions.Clear();
            foreach (var action in message.NewActions)
                Actions.Add(action);
        }
    }

    partial void OnStateChanged(ContentSuggestionState value)
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsApplied));
    }

    // ── Commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ExecuteAction(SuggestionAction action)
    {
        WeakReferenceMessenger.Default.Send(new ContentSuggestionActionRequestedMessage
        {
            SuggestionId = _card.SuggestionId,
            PluginId = _card.PluginId,
            ActionId = action.ActionId
        });
    }

    [RelayCommand]
    private void Dismiss()
    {
        WeakReferenceMessenger.Default.Send(new ContentSuggestionDismissedMessage
        {
            SuggestionId = _card.SuggestionId,
            PluginId = _card.PluginId
        });
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
