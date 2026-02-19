using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for a single editable slot in the intent slot editor.
/// </summary>
public partial class SlotEditorFieldViewModel : ObservableObject
{
    public IntentSlot Slot { get; }

    [ObservableProperty]
    private string _value;

    public string Name => Slot.Name;
    public string DisplayName => Slot.DisplayName;
    public string Description => Slot.Description;
    public bool IsRequired => Slot.Required;
    public IntentSlotType Type => Slot.Type;

    public SlotEditorFieldViewModel(IntentSlot slot, string initialValue)
    {
        Slot = slot;
        _value = initialValue;
    }
}

/// <summary>
/// ViewModel for the intent slot editor overlay.
/// Allows the user to review and edit extracted slot values before execution.
/// </summary>
public partial class IntentSlotEditorViewModel : ViewModelBase
{
    private static readonly ILogger _log = Log.ForContext<IntentSlotEditorViewModel>();

    private readonly IIntentEngine _intentEngine;
    private IntentSuggestion? _suggestion;

    public IntentSlotEditorViewModel(IIntentEngine intentEngine)
    {
        _intentEngine = intentEngine;
    }

    // ── Properties ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationErrors))]
    private bool _isVisible;

    [ObservableProperty]
    private string _intentDisplayName = "";

    [ObservableProperty]
    private string? _intentIcon;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string? _sourceContext;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<SlotEditorFieldViewModel> Fields { get; } = [];

    public bool HasValidationErrors => Fields.Any(f => f.IsRequired && string.IsNullOrWhiteSpace(f.Value));

    // ── Methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens the editor for a specific suggestion.
    /// </summary>
    public void Open(IntentSuggestion suggestion)
    {
        _suggestion = suggestion;
        IntentDisplayName = suggestion.MatchedIntent.DisplayName;
        IntentIcon = suggestion.MatchedIntent.Icon;
        Summary = suggestion.Summary;
        ErrorMessage = null;

        SourceContext = suggestion.SourceSignal.EntityTitle != null
            ? $"From: {suggestion.SourceSignal.EntityTitle} ({suggestion.SourceSignal.SourcePluginId})"
            : $"From: {suggestion.SourceSignal.SourcePluginId}";

        Fields.Clear();
        foreach (var slot in suggestion.MatchedIntent.Slots)
        {
            var value = suggestion.ExtractedSlots.TryGetValue(slot.Name, out var v) ? v : slot.DefaultValue ?? "";
            Fields.Add(new SlotEditorFieldViewModel(slot, value));
        }

        IsVisible = true;
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_suggestion == null) return;

        if (HasValidationErrors)
        {
            ErrorMessage = "Please fill in all required fields.";
            return;
        }

        IsExecuting = true;
        ErrorMessage = null;

        try
        {
            var slotOverrides = Fields.ToDictionary(f => f.Name, f => f.Value);
            var result = await _intentEngine.ExecuteAsync(_suggestion.SuggestionId, slotOverrides);

            if (result.Success)
            {
                IsVisible = false;
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Execution failed.";
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Slot editor execution failed");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsVisible = false;
        _suggestion = null;
    }
}
