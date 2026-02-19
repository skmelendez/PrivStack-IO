using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for a single intent suggestion card.
/// Displays the suggestion summary, confidence, extracted slots, and action buttons.
/// </summary>
public partial class IntentSuggestionCardViewModel : ViewModelBase
{
    private static readonly ILogger _log = Log.ForContext<IntentSuggestionCardViewModel>();

    private readonly IIntentEngine _intentEngine;
    private readonly IntentSuggestion _suggestion;

    public IntentSuggestionCardViewModel(IntentSuggestion suggestion, IIntentEngine intentEngine)
    {
        _suggestion = suggestion;
        _intentEngine = intentEngine;
    }

    // ── Display Properties ───────────────────────────────────────────

    public string SuggestionId => _suggestion.SuggestionId;
    public string Summary => _suggestion.Summary;
    public string IntentDisplayName => _suggestion.MatchedIntent.DisplayName;
    public string PluginId => _suggestion.MatchedIntent.PluginId;
    public string? Icon => _suggestion.MatchedIntent.Icon;
    public double Confidence => _suggestion.Confidence;
    public string ConfidenceText => $"{_suggestion.Confidence:P0}";
    public string SourcePluginId => _suggestion.SourceSignal.SourcePluginId;
    public string? SourceEntityTitle => _suggestion.SourceSignal.EntityTitle;
    public DateTimeOffset CreatedAt => _suggestion.CreatedAt;

    public IReadOnlyList<IntentSlot> Slots => _suggestion.MatchedIntent.Slots;
    public IReadOnlyDictionary<string, string> ExtractedSlots => _suggestion.ExtractedSlots;

    // ── Slot Editor State ────────────────────────────────────────────

    [ObservableProperty]
    private bool _isEditingSlots;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private string? _resultMessage;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _resultSuccess;

    /// <summary>
    /// Mutable slot values for the slot editor. Initialized from extracted slots.
    /// </summary>
    public Dictionary<string, string> EditableSlots { get; } = new();

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AcceptAsync()
    {
        IsExecuting = true;
        ResultMessage = null;

        try
        {
            var overrides = IsEditingSlots && EditableSlots.Count > 0
                ? EditableSlots.AsReadOnly()
                : null;

            var result = await _intentEngine.ExecuteAsync(_suggestion.SuggestionId, overrides);

            ResultMessage = result.Success
                ? result.Summary ?? "Action completed."
                : result.ErrorMessage ?? "Action failed.";
            ResultSuccess = result.Success;
            HasResult = true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to execute intent suggestion {Id}", _suggestion.SuggestionId);
            ResultMessage = $"Error: {ex.Message}";
            ResultSuccess = false;
            HasResult = true;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private void EditSlots()
    {
        if (!IsEditingSlots)
        {
            EditableSlots.Clear();
            foreach (var (key, value) in _suggestion.ExtractedSlots)
                EditableSlots[key] = value;
        }
        IsEditingSlots = !IsEditingSlots;
    }

    [RelayCommand]
    private void Dismiss()
    {
        _intentEngine.Dismiss(_suggestion.SuggestionId);
    }
}
