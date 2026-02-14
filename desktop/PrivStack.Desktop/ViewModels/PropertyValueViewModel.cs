using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Wraps a PropertyDefinition + current value for a specific entity,
/// providing type-appropriate editing and persistence.
/// </summary>
public partial class PropertyValueViewModel : ObservableObject
{
    private static readonly ILogger _log = Serilog.Log.ForContext<PropertyValueViewModel>();

    private readonly EntityMetadataService _metadataService;
    private readonly string _linkType;
    private readonly string _entityId;
    private bool _isSaving;

    public PropertyDefinition Definition { get; }

    public string Name => Definition.Name;
    public PropertyType Type => Definition.Type;
    public string? Icon => Definition.Icon;
    public string? GroupId => Definition.GroupId;
    public List<string>? Options => Definition.Options;

    // --- Typed value properties ---

    [ObservableProperty]
    private string _textValue = "";

    [ObservableProperty]
    private double _numberValue;

    [ObservableProperty]
    private DateTimeOffset? _dateValue;

    [ObservableProperty]
    private bool _checkboxValue;

    [ObservableProperty]
    private string? _selectedOption;

    [ObservableProperty]
    private List<string> _selectedOptions = [];

    [ObservableProperty]
    private string _urlValue = "";

    // --- Relation properties ---

    /// <summary>
    /// Delegate for searching linkable entities. Set by the parent (InfoPanelViewModel).
    /// Parameters: query, allowedLinkTypes, maxResults.
    /// </summary>
    public Func<string, List<string>?, int, Task<List<LinkableItem>>>? EntitySearcher { get; set; }

    /// <summary>
    /// Delegate for resolving a single entity by link type + ID. Set by the parent.
    /// </summary>
    public Func<string, string, Task<LinkableItem?>>? EntityResolver { get; set; }

    /// <summary>
    /// Resolved display entries for the relation property.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LinkableItem> _relationItems = [];

    /// <summary>
    /// Source-of-truth entry list serialized to/from JSON.
    /// </summary>
    private List<RelationEntry> _relationEntries = [];

    /// <summary>
    /// Invoked after the property is successfully removed from the entity.
    /// The parent should use this to update its collections.
    /// </summary>
    public Action<PropertyValueViewModel>? OnRemoved { get; set; }

    public PropertyValueViewModel(
        PropertyDefinition definition,
        JsonElement? currentValue,
        EntityMetadataService metadataService,
        string linkType,
        string entityId)
    {
        Definition = definition;
        _metadataService = metadataService;
        _linkType = linkType;
        _entityId = entityId;

        LoadValue(currentValue);
    }

    private void LoadValue(JsonElement? value)
    {
        if (value == null || value.Value.ValueKind == JsonValueKind.Undefined || value.Value.ValueKind == JsonValueKind.Null)
        {
            ApplyDefault();
            return;
        }

        var v = value.Value;
        switch (Definition.Type)
        {
            case PropertyType.Text:
                TextValue = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
                break;
            case PropertyType.Number:
                NumberValue = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                break;
            case PropertyType.Date:
                if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dt))
                    DateValue = dt;
                break;
            case PropertyType.Checkbox:
                CheckboxValue = v.ValueKind == JsonValueKind.True;
                break;
            case PropertyType.Select:
                SelectedOption = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                break;
            case PropertyType.MultiSelect:
                if (v.ValueKind == JsonValueKind.Array)
                    SelectedOptions = v.EnumerateArray()
                        .Select(e => e.GetString() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                break;
            case PropertyType.Url:
                UrlValue = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                break;
            case PropertyType.Relation:
                if (v.ValueKind == JsonValueKind.Array)
                {
                    _relationEntries = JsonSerializer.Deserialize<List<RelationEntry>>(v.GetRawText()) ?? [];
                    _ = ResolveRelationItemsAsync();
                }
                break;
        }
    }

    private void ApplyDefault()
    {
        if (string.IsNullOrEmpty(Definition.DefaultValue)) return;

        switch (Definition.Type)
        {
            case PropertyType.Text:
                TextValue = Definition.DefaultValue;
                break;
            case PropertyType.Number:
                if (double.TryParse(Definition.DefaultValue, CultureInfo.InvariantCulture, out var n))
                    NumberValue = n;
                break;
            case PropertyType.Checkbox:
                CheckboxValue = bool.TryParse(Definition.DefaultValue, out var b) && b;
                break;
            case PropertyType.Select:
                SelectedOption = Definition.DefaultValue;
                break;
            case PropertyType.Url:
                UrlValue = Definition.DefaultValue;
                break;
        }
    }

    /// <summary>
    /// Converts the current typed value to a JsonElement for storage.
    /// </summary>
    private JsonElement ToJsonElement()
    {
        return Definition.Type switch
        {
            PropertyType.Text => JsonSerializer.SerializeToElement(TextValue),
            PropertyType.Number => JsonSerializer.SerializeToElement(NumberValue),
            PropertyType.Date => DateValue.HasValue
                ? JsonSerializer.SerializeToElement(DateValue.Value.UtcDateTime.ToString("O"))
                : JsonSerializer.SerializeToElement((string?)null),
            PropertyType.Checkbox => JsonSerializer.SerializeToElement(CheckboxValue),
            PropertyType.Select => JsonSerializer.SerializeToElement(SelectedOption),
            PropertyType.MultiSelect => JsonSerializer.SerializeToElement(SelectedOptions),
            PropertyType.Url => JsonSerializer.SerializeToElement(UrlValue),
            PropertyType.Relation => JsonSerializer.SerializeToElement(_relationEntries),
            _ => JsonSerializer.SerializeToElement((string?)null),
        };
    }

    // --- Save on change ---

    partial void OnTextValueChanged(string value) => SaveIfNotLoading();
    partial void OnNumberValueChanged(double value) => SaveIfNotLoading();
    partial void OnDateValueChanged(DateTimeOffset? value) => SaveIfNotLoading();
    partial void OnCheckboxValueChanged(bool value) => SaveIfNotLoading();
    partial void OnSelectedOptionChanged(string? value) => SaveIfNotLoading();
    partial void OnUrlValueChanged(string value) => SaveIfNotLoading();

    private void SaveIfNotLoading()
    {
        if (_isSaving) return;
        _ = SaveAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_isSaving) return;
        _isSaving = true;
        try
        {
            var jsonValue = ToJsonElement();
            await _metadataService.UpdatePropertyAsync(_linkType, _entityId, Definition.Id, jsonValue);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save property '{Name}' for {LinkType}:{EntityId}",
                Definition.Name, _linkType, _entityId);
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// For MultiSelect: toggles a specific option on/off and saves.
    /// </summary>
    public async Task ToggleMultiSelectOptionAsync(string option)
    {
        var list = new List<string>(SelectedOptions);
        if (list.Contains(option))
            list.Remove(option);
        else
            list.Add(option);

        SelectedOptions = list;

        _isSaving = true;
        try
        {
            var jsonValue = ToJsonElement();
            await _metadataService.UpdatePropertyAsync(_linkType, _entityId, Definition.Id, jsonValue);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save multi-select property '{Name}'", Definition.Name);
        }
        finally
        {
            _isSaving = false;
        }
    }

    [RelayCommand]
    private async Task RemoveProperty()
    {
        try
        {
            await _metadataService.RemovePropertyAsync(_linkType, _entityId, Definition.Id);
            OnRemoved?.Invoke(this);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to remove property '{Name}'", Definition.Name);
        }
    }

    // --- Relation helpers ---

    /// <summary>
    /// Resolves display titles for all stored relation entries via EntityResolver.
    /// </summary>
    internal async Task ResolveRelationItemsAsync()
    {
        if (EntityResolver == null || _relationEntries.Count == 0)
        {
            RelationItems = [];
            return;
        }

        var resolved = new List<LinkableItem>();
        foreach (var entry in _relationEntries)
        {
            try
            {
                var item = await EntityResolver(entry.LinkType, entry.EntityId);
                if (item != null)
                    resolved.Add(item);
                else
                    resolved.Add(new LinkableItem
                    {
                        Id = entry.EntityId,
                        LinkType = entry.LinkType,
                        Title = $"{entry.LinkType}:{entry.EntityId[..Math.Min(8, entry.EntityId.Length)]}...",
                    });
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Failed to resolve relation entry {LinkType}:{EntityId}", entry.LinkType, entry.EntityId);
                resolved.Add(new LinkableItem
                {
                    Id = entry.EntityId,
                    LinkType = entry.LinkType,
                    Title = $"{entry.LinkType}:{entry.EntityId[..Math.Min(8, entry.EntityId.Length)]}...",
                });
            }
        }
        RelationItems = new ObservableCollection<LinkableItem>(resolved);
    }

    /// <summary>
    /// Adds a linkable item to the relation and persists.
    /// </summary>
    public async Task AddRelationAsync(LinkableItem item)
    {
        if (_relationEntries.Any(e => e.EntityId == item.Id && e.LinkType == item.LinkType))
            return;

        _relationEntries.Add(new RelationEntry { LinkType = item.LinkType, EntityId = item.Id });
        RelationItems.Add(item);
        await SaveRelationAsync();
    }

    /// <summary>
    /// Removes a linkable item from the relation and persists.
    /// </summary>
    public async Task RemoveRelationAsync(LinkableItem item)
    {
        var idx = _relationEntries.FindIndex(e => e.EntityId == item.Id && e.LinkType == item.LinkType);
        if (idx < 0) return;

        _relationEntries.RemoveAt(idx);
        RelationItems.Remove(item);
        await SaveRelationAsync();
    }

    private async Task SaveRelationAsync()
    {
        _isSaving = true;
        try
        {
            var jsonValue = JsonSerializer.SerializeToElement(_relationEntries);
            await _metadataService.UpdatePropertyAsync(_linkType, _entityId, Definition.Id, jsonValue);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save relation property '{Name}'", Definition.Name);
        }
        finally
        {
            _isSaving = false;
        }
    }
}
