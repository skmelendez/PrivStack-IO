using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Wraps a PropertyTemplateEntry + its resolved PropertyDefinition for display in the template dialog.
/// </summary>
public partial class TemplateEntryViewModel : ObservableObject
{
    private readonly PropertyTemplateDialogViewModel _parent;

    public PropertyDefinition Definition { get; }
    public string PropertyDefId => Definition.Id;

    [ObservableProperty]
    private string? _defaultValue;

    public TemplateEntryViewModel(PropertyDefinition definition, string? defaultValue, PropertyTemplateDialogViewModel parent)
    {
        Definition = definition;
        _defaultValue = defaultValue;
        _parent = parent;
    }

    partial void OnDefaultValueChanged(string? value)
    {
        _parent.OnEntryDefaultValueChanged(this);
    }
}

public partial class PropertyTemplateDialogViewModel : ViewModelBase
{
    private static readonly ILogger _log = Serilog.Log.ForContext<PropertyTemplateDialogViewModel>();

    private readonly EntityMetadataService _metadataService;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private ObservableCollection<PropertyTemplate> _templates = [];

    [ObservableProperty]
    private PropertyTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = "";

    [ObservableProperty]
    private string _selectedTemplateName = "";

    [ObservableProperty]
    private ObservableCollection<PropertyDefinition> _allPropertyDefs = [];

    [ObservableProperty]
    private ObservableCollection<TemplateEntryViewModel> _selectedTemplateEntries = [];

    [ObservableProperty]
    private string _customPropertyName = "";

    [ObservableProperty]
    private PropertyType _customPropertyType = PropertyType.Text;

    [ObservableProperty]
    private bool _isCreatingCustomProperty;

    public static IReadOnlyList<PropertyType> PropertyTypeOptions { get; } =
        Enum.GetValues<PropertyType>().ToList();

    public PropertyTemplateDialogViewModel(EntityMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    [RelayCommand]
    private async Task Open()
    {
        IsOpen = true;
        await _metadataService.SeedDefaultTemplatesAsync();
        await _metadataService.SeedDefaultPropertyGroupsAsync();
        await LoadTemplatesAsync();
        await LoadPropertyDefsAsync();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        SelectedTemplate = null;
    }

    private async Task LoadTemplatesAsync()
    {
        try
        {
            var templates = await _metadataService.GetPropertyTemplatesAsync();
            Templates = new ObservableCollection<PropertyTemplate>(templates);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load templates");
        }
    }

    private async Task LoadPropertyDefsAsync()
    {
        try
        {
            var defs = await _metadataService.GetPropertyDefinitionsAsync();
            AllPropertyDefs = new ObservableCollection<PropertyDefinition>(defs);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load property definitions");
        }
    }

    [RelayCommand]
    private async Task CreateTemplate()
    {
        var name = NewTemplateName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var template = await _metadataService.CreatePropertyTemplateAsync(
                new PropertyTemplate { Name = name });
            Templates.Add(template);
            NewTemplateName = "";
            SelectedTemplate = template;
            UpdateSelectedTemplateEntries();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to create template '{Name}'", name);
        }
    }

    [RelayCommand]
    private async Task DeleteTemplate(PropertyTemplate? template)
    {
        if (template == null) return;

        try
        {
            await _metadataService.DeletePropertyTemplateAsync(template.Id);
            Templates.Remove(template);
            if (SelectedTemplate?.Id == template.Id)
            {
                SelectedTemplate = null;
                SelectedTemplateEntries = [];
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to delete template '{Name}'", template.Name);
        }
    }

    partial void OnSelectedTemplateChanged(PropertyTemplate? value)
    {
        SelectedTemplateName = value?.Name ?? "";
        UpdateSelectedTemplateEntries();
    }

    private void UpdateSelectedTemplateEntries()
    {
        if (SelectedTemplate == null)
        {
            SelectedTemplateEntries = [];
            return;
        }

        var defLookup = AllPropertyDefs.ToDictionary(d => d.Id);
        var entries = new ObservableCollection<TemplateEntryViewModel>();

        foreach (var entry in SelectedTemplate.Entries)
        {
            if (defLookup.TryGetValue(entry.PropertyDefId, out var def))
            {
                entries.Add(new TemplateEntryViewModel(def, entry.DefaultValue, this));
            }
        }

        SelectedTemplateEntries = entries;
    }

    /// <summary>
    /// Called by TemplateEntryViewModel when a default value changes.
    /// Rebuilds the template entries list and persists.
    /// </summary>
    internal async void OnEntryDefaultValueChanged(TemplateEntryViewModel changed)
    {
        if (SelectedTemplate == null) return;

        try
        {
            var updatedEntries = SelectedTemplate.Entries.Select(e =>
                e.PropertyDefId == changed.PropertyDefId
                    ? e with { DefaultValue = changed.DefaultValue }
                    : e).ToList();

            var updated = SelectedTemplate with { Entries = updatedEntries };
            await _metadataService.UpdatePropertyTemplateAsync(updated);

            var idx = Templates.IndexOf(SelectedTemplate);
            if (idx >= 0) Templates[idx] = updated;
            SelectedTemplate = updated;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to update default value for property in template");
        }
    }

    /// <summary>
    /// Saves a renamed template name.
    /// </summary>
    [RelayCommand]
    private async Task SaveTemplateName()
    {
        if (SelectedTemplate == null) return;
        var name = SelectedTemplateName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == SelectedTemplate.Name) return;

        try
        {
            var updated = SelectedTemplate with { Name = name };
            await _metadataService.UpdatePropertyTemplateAsync(updated);

            var idx = Templates.IndexOf(SelectedTemplate);
            if (idx >= 0) Templates[idx] = updated;
            SelectedTemplate = updated;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to rename template to '{Name}'", name);
        }
    }

    /// <summary>
    /// Adds a property definition to the selected template.
    /// </summary>
    [RelayCommand]
    private async Task AddDefToTemplate(PropertyDefinition? def)
    {
        if (def == null || SelectedTemplate == null) return;
        if (SelectedTemplate.Entries.Any(e => e.PropertyDefId == def.Id)) return;

        try
        {
            var updatedEntries = new List<PropertyTemplateEntry>(SelectedTemplate.Entries)
            {
                new() { PropertyDefId = def.Id }
            };
            var updated = SelectedTemplate with { Entries = updatedEntries };
            await _metadataService.UpdatePropertyTemplateAsync(updated);

            var idx = Templates.IndexOf(SelectedTemplate);
            if (idx >= 0) Templates[idx] = updated;
            SelectedTemplate = updated;
            UpdateSelectedTemplateEntries();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to add property to template");
        }
    }

    /// <summary>
    /// Removes a property definition from the selected template.
    /// </summary>
    [RelayCommand]
    private async Task RemoveEntryFromTemplate(TemplateEntryViewModel? entry)
    {
        if (entry == null || SelectedTemplate == null) return;

        try
        {
            var updatedEntries = SelectedTemplate.Entries
                .Where(e => e.PropertyDefId != entry.PropertyDefId)
                .ToList();
            var updated = SelectedTemplate with { Entries = updatedEntries };
            await _metadataService.UpdatePropertyTemplateAsync(updated);

            var idx = Templates.IndexOf(SelectedTemplate);
            if (idx >= 0) Templates[idx] = updated;
            SelectedTemplate = updated;
            UpdateSelectedTemplateEntries();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to remove property from template");
        }
    }

    [RelayCommand]
    private void ToggleCustomPropertyForm()
    {
        IsCreatingCustomProperty = !IsCreatingCustomProperty;
        if (!IsCreatingCustomProperty)
        {
            CustomPropertyName = "";
            CustomPropertyType = PropertyType.Text;
        }
    }

    [RelayCommand]
    private async Task CreateCustomProperty()
    {
        var name = CustomPropertyName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var def = await _metadataService.CreatePropertyDefinitionAsync(
                new PropertyDefinition
                {
                    Name = name,
                    Type = CustomPropertyType,
                    SortOrder = AllPropertyDefs.Count > 0
                        ? AllPropertyDefs.Max(d => d.SortOrder) + 10
                        : 10
                });

            await LoadPropertyDefsAsync();

            if (SelectedTemplate != null)
                await AddDefToTemplate(def);

            CustomPropertyName = "";
            CustomPropertyType = PropertyType.Text;
            IsCreatingCustomProperty = false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to create custom property '{Name}'", name);
        }
    }
}
