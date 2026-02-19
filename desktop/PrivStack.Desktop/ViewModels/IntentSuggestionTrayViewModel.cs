using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for the global intent suggestion tray (flyout from toolbar).
/// Observes IntentEngine events and maintains the suggestion card collection.
/// </summary>
public partial class IntentSuggestionTrayViewModel : ViewModelBase
{
    private static readonly ILogger _log = Log.ForContext<IntentSuggestionTrayViewModel>();

    private readonly IIntentEngine _intentEngine;
    private readonly IUiDispatcher _dispatcher;

    public IntentSuggestionTrayViewModel(IIntentEngine intentEngine, IUiDispatcher dispatcher)
    {
        _intentEngine = intentEngine;
        _dispatcher = dispatcher;

        _intentEngine.SuggestionAdded += OnSuggestionAdded;
        _intentEngine.SuggestionRemoved += OnSuggestionRemoved;
        _intentEngine.SuggestionsCleared += OnSuggestionsCleared;

        // Load any existing suggestions
        foreach (var suggestion in _intentEngine.PendingSuggestions)
        {
            Suggestions.Add(new IntentSuggestionCardViewModel(suggestion, _intentEngine));
        }
        UpdateCounts();
    }

    // ── Properties ───────────────────────────────────────────────────

    public ObservableCollection<IntentSuggestionCardViewModel> Suggestions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions))]
    private int _pendingCount;

    public bool HasSuggestions => PendingCount > 0;

    [ObservableProperty]
    private bool _isOpen;

    public bool IsEnabled => _intentEngine.IsEnabled;

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void ClearAll()
    {
        _intentEngine.ClearAll();
    }

    // ── Event Handlers ───────────────────────────────────────────────

    private void OnSuggestionAdded(object? sender, IntentSuggestion suggestion)
    {
        _dispatcher.Post(() =>
        {
            Suggestions.Add(new IntentSuggestionCardViewModel(suggestion, _intentEngine));
            UpdateCounts();
        });
    }

    private void OnSuggestionRemoved(object? sender, string suggestionId)
    {
        _dispatcher.Post(() =>
        {
            var card = Suggestions.FirstOrDefault(c => c.SuggestionId == suggestionId);
            if (card != null) Suggestions.Remove(card);
            UpdateCounts();
        });
    }

    private void OnSuggestionsCleared(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            Suggestions.Clear();
            UpdateCounts();
        });
    }

    private void UpdateCounts()
    {
        PendingCount = Suggestions.Count;
    }
}
