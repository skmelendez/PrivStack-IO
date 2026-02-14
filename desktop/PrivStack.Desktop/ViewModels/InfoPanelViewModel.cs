using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using PrivStack.UI.Adaptive.Models;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Groups property value VMs under a named header for display in the info panel.
/// </summary>
public partial class PropertyGroupViewModel : ObservableObject
{
    public string? GroupId { get; }
    public string GroupName { get; }
    public ObservableCollection<PropertyValueViewModel> Properties { get; }

    public PropertyGroupViewModel(string? groupId, string groupName, IEnumerable<PropertyValueViewModel> properties)
    {
        GroupId = groupId;
        GroupName = groupName;
        Properties = new ObservableCollection<PropertyValueViewModel>(properties);
    }
}

/// <summary>
/// Represents a link type filter toggle for the graph settings overlay.
/// </summary>
public partial class GraphLinkTypeFilter : ObservableObject
{
    public string LinkType { get; }
    public string DisplayName { get; }
    public string Icon { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public GraphLinkTypeFilter(string linkType, string displayName, string icon, bool isEnabled)
    {
        LinkType = linkType;
        DisplayName = displayName;
        Icon = icon;
        _isEnabled = isEnabled;
    }
}

public partial class InfoPanelViewModel : ViewModelBase
{
    private static readonly ILogger _log = Serilog.Log.ForContext<InfoPanelViewModel>();

    private readonly InfoPanelService _infoPanelService;
    private readonly BacklinkService _backlinkService;
    private readonly EntityMetadataService _entityMetadataService;
    private readonly IAppSettingsService _appSettings;
    private readonly IPluginRegistry _pluginRegistry;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveItem))]
    private string? _activeItemTitle;

    [ObservableProperty]
    private string? _activeItemLinkType;

    [ObservableProperty]
    private string? _activeItemId;

    // --- Metadata properties ---

    [ObservableProperty]
    private string? _activeEntityTypeDisplay;

    [ObservableProperty]
    private string? _activeEntityTypeIcon;

    [ObservableProperty]
    private DateTimeOffset? _activeItemCreatedAt;

    [ObservableProperty]
    private DateTimeOffset? _activeItemModifiedAt;

    [ObservableProperty]
    private string? _activeItemPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParent))]
    private string? _activeItemParentTitle;

    public bool HasParent => !string.IsNullOrEmpty(ActiveItemParentTitle);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private ObservableCollection<InfoPanelDetailField> _activeItemDetails = [];

    public bool HasDetails => ActiveItemDetails.Count > 0;

    [ObservableProperty]
    private ObservableCollection<string> _activeItemTags = [];

    [ObservableProperty]
    private string _tagInput = "";

    [ObservableProperty]
    private ObservableCollection<string> _tagSuggestions = [];

    [ObservableProperty]
    private bool _isBacklinksExpanded = true;

    // --- Custom Properties ---

    [ObservableProperty]
    private ObservableCollection<PropertyValueViewModel> _activeItemProperties = [];

    [ObservableProperty]
    private bool _isPropertiesExpanded = true;

    [ObservableProperty]
    private ObservableCollection<PropertyGroupViewModel> _activeItemPropertyGroups = [];

    [ObservableProperty]
    private ObservableCollection<PropertyDefinition> _availablePropertyDefs = [];

    [ObservableProperty]
    private bool _isAddPropertyOpen;

    [ObservableProperty]
    private PropertyType _newPropertyType = PropertyType.Text;

    public static PropertyType[] AllPropertyTypes => Enum.GetValues<PropertyType>();

    // --- Property Definition Editing ---

    [ObservableProperty]
    private PropertyDefinition? _editingPropertyDef;

    [ObservableProperty]
    private string _editingDefName = "";

    [ObservableProperty]
    private PropertyType _editingDefType = PropertyType.Text;

    [ObservableProperty]
    private string _editingDefOptions = "";

    // --- Templates ---

    [ObservableProperty]
    private ObservableCollection<PropertyTemplate> _availableTemplates = [];

    [ObservableProperty]
    private bool _isApplyTemplateOpen;

    public PropertyTemplateDialogViewModel TemplateDialog { get; }

    // --- Forward Links ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForwardLinks))]
    [NotifyPropertyChangedFor(nameof(ForwardLinkCountText))]
    private ObservableCollection<BacklinkEntry> _forwardLinks = [];

    [ObservableProperty]
    private bool _isLinksExpanded = true;

    public bool HasForwardLinks => ForwardLinks.Count > 0;
    public string ForwardLinkCountText => ForwardLinks.Count > 0 ? $"({ForwardLinks.Count})" : "";

    // --- Backlinks + Graph ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBacklinks))]
    [NotifyPropertyChangedFor(nameof(BacklinkCountText))]
    private ObservableCollection<BacklinkEntry> _backlinks = [];

    [ObservableProperty]
    private GraphData? _localGraphData;

    [ObservableProperty]
    private IReadOnlyList<JsonElement>? _graphNodes;

    [ObservableProperty]
    private IReadOnlyList<JsonElement>? _graphEdges;

    [ObservableProperty]
    private string? _graphCenterId;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _activeTab = "Info";

    [ObservableProperty]
    private double _panelWidth = 320;

    [ObservableProperty]
    private int _graphDepth = 1;

    [ObservableProperty]
    private bool _isGraphSettingsOpen;

    private ObservableCollection<GraphLinkTypeFilter> _graphLinkTypeFilters = [];
    public ObservableCollection<GraphLinkTypeFilter> GraphLinkTypeFilters
    {
        get => _graphLinkTypeFilters;
        set => SetProperty(ref _graphLinkTypeFilters, value);
    }

    public bool HasActiveItem => ActiveItemTitle != null;
    public bool HasBacklinks => Backlinks.Count > 0;
    public string BacklinkCountText => Backlinks.Count > 0 ? $"({Backlinks.Count})" : "";

    /// <summary>
    /// Hides the Graph tab when the Graph plugin is the active plugin
    /// (the main view IS the graph, so a mini-graph in the info panel is redundant).
    /// </summary>
    public bool ShowGraphTab => _infoPanelService.ActivePluginId != "privstack.graph";

    /// <summary>
    /// Fired when the user clicks a backlink or graph node to navigate to it.
    /// MainWindowViewModel subscribes to handle cross-plugin navigation.
    /// </summary>
    public event Func<string, string, Task>? NavigateToItemRequested;

    // All link types with display metadata — derived from the shared EntityTypeMap + virtual tag type.
    private static readonly Dictionary<string, (string DisplayName, string Icon)> AllLinkTypeMeta =
        EntityTypeMap.All
            .ToDictionary(e => e.LinkType, e => (e.DisplayName, e.Icon))
            .Concat(new[] { KeyValuePair.Create("tag", ("Tags", "Tag")) })
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    public InfoPanelViewModel(
        InfoPanelService infoPanelService,
        BacklinkService backlinkService,
        EntityMetadataService entityMetadataService,
        IAppSettingsService appSettings,
        IPluginRegistry pluginRegistry)
    {
        _infoPanelService = infoPanelService;
        _backlinkService = backlinkService;
        _entityMetadataService = entityMetadataService;
        _appSettings = appSettings;
        _pluginRegistry = pluginRegistry;

        TemplateDialog = new PropertyTemplateDialogViewModel(entityMetadataService);

        // Restore persisted state — migrate "Backlinks" → "Info"
        IsOpen = _appSettings.Settings.InfoPanelOpen;
        var savedTab = _appSettings.Settings.InfoPanelTab;
        ActiveTab = savedTab == "Backlinks" ? "Info" : savedTab;
        PanelWidth = _appSettings.Settings.InfoPanelWidth;
        GraphDepth = Math.Clamp(_appSettings.Settings.InfoPanelGraphDepth, 1, 5);

        // Subscribe to active item changes
        _infoPanelService.ActiveItemChanged += OnActiveItemChanged;
        _infoPanelService.ContentChanged += OnContentChanged;
        _infoPanelService.ActivePluginChanged += OnActivePluginChanged;
    }

    partial void OnIsOpenChanged(bool value)
    {
        _appSettings.Settings.InfoPanelOpen = value;
        _appSettings.SaveDebounced();

        // If opening and we have an active item, refresh data
        if (value && _infoPanelService.ActiveLinkType != null)
            OnActiveItemChanged();
    }

    partial void OnActiveTabChanged(string value)
    {
        // Migrate persisted "Backlinks" → "Info"
        if (value == "Backlinks")
        {
            ActiveTab = "Info";
            return; // OnActiveTabChanged will fire again with "Info"
        }
        _appSettings.Settings.InfoPanelTab = value;
        _appSettings.SaveDebounced();
    }

    partial void OnPanelWidthChanged(double value)
    {
        _appSettings.Settings.InfoPanelWidth = value;
        _appSettings.SaveDebounced();
    }

    partial void OnGraphDepthChanged(int value)
    {
        _appSettings.Settings.InfoPanelGraphDepth = value;
        _appSettings.SaveDebounced();
        ReloadGraphIfActive();
    }

    partial void OnTagInputChanged(string value)
    {
        _ = UpdateTagSuggestionsAsync(value);
    }

    private async Task UpdateTagSuggestionsAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 1)
        {
            TagSuggestions = [];
            return;
        }

        try
        {
            var allTags = await _entityMetadataService.GetAllTagsAsync();
            var currentTags = new HashSet<string>(ActiveItemTags, StringComparer.OrdinalIgnoreCase);
            var filtered = allTags
                .Where(t => t.Contains(input, StringComparison.OrdinalIgnoreCase) && !currentTags.Contains(t))
                .Take(10)
                .ToList();
            TagSuggestions = new ObservableCollection<string>(filtered);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to update tag suggestions");
        }
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GraphLinkTypeFilter.IsEnabled)) return;

        // Persist hidden types
        _appSettings.Settings.InfoPanelGraphHiddenTypes = GraphLinkTypeFilters
            .Where(f => !f.IsEnabled)
            .Select(f => f.LinkType)
            .ToList();
        _appSettings.SaveDebounced();
        ReloadGraphIfActive();
    }

    [RelayCommand]
    private void ToggleGraphSettings()
    {
        IsGraphSettingsOpen = !IsGraphSettingsOpen;
    }

    [RelayCommand]
    private void ToggleLinks()
    {
        IsLinksExpanded = !IsLinksExpanded;
    }

    [RelayCommand]
    private void ToggleBacklinks()
    {
        IsBacklinksExpanded = !IsBacklinksExpanded;
    }

    [RelayCommand]
    private void ToggleProperties()
    {
        IsPropertiesExpanded = !IsPropertiesExpanded;
    }

    [RelayCommand]
    private void ToggleAddProperty()
    {
        IsAddPropertyOpen = !IsAddPropertyOpen;
        if (IsAddPropertyOpen)
            _ = LoadAvailablePropertyDefsAsync();
    }

    [RelayCommand]
    private void ToggleApplyTemplate()
    {
        IsApplyTemplateOpen = !IsApplyTemplateOpen;
        if (IsApplyTemplateOpen)
            _ = LoadAvailableTemplatesAsync();
    }

    [RelayCommand]
    private async Task ApplyTemplate(PropertyTemplate? template)
    {
        if (template == null) return;
        if (ActiveItemLinkType == null || ActiveItemId == null) return;

        IsApplyTemplateOpen = false;

        try
        {
            await _entityMetadataService.ApplyTemplateAsync(ActiveItemLinkType, ActiveItemId, template);

            // Reload properties to reflect the stamped values
            var metadata = await _entityMetadataService.GetMetadataAsync(ActiveItemLinkType, ActiveItemId);
            await BuildPropertyViewModelsAsync(ActiveItemLinkType, ActiveItemId, metadata.Properties, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply template '{Name}' to {LinkType}:{EntityId}",
                template.Name, ActiveItemLinkType, ActiveItemId);
        }
    }

    [RelayCommand]
    private async Task OpenTemplateManager()
    {
        IsApplyTemplateOpen = false;
        await TemplateDialog.OpenCommand.ExecuteAsync(null);
    }

    private async Task LoadAvailableTemplatesAsync()
    {
        try
        {
            await _entityMetadataService.SeedDefaultTemplatesAsync();
            await _entityMetadataService.SeedDefaultPropertyGroupsAsync();
            var templates = await _entityMetadataService.GetPropertyTemplatesAsync();
            AvailableTemplates = new ObservableCollection<PropertyTemplate>(templates);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to load available templates");
        }
    }

    [RelayCommand]
    private async Task AddPropertyToItem(PropertyDefinition? definition)
    {
        if (definition == null) return;
        if (ActiveItemLinkType == null || ActiveItemId == null) return;

        IsAddPropertyOpen = false;

        // Create a PropertyValueViewModel with default value
        var vm = new PropertyValueViewModel(definition, null, _entityMetadataService, ActiveItemLinkType, ActiveItemId);
        vm.OnRemoved = OnPropertyRemoved;
        WireRelationDelegates(vm);
        ActiveItemProperties.Add(vm);

        // Save the default value to persist the property assignment
        try
        {
            var defaultValue = JsonSerializer.SerializeToElement(definition.DefaultValue ?? "");
            await _entityMetadataService.UpdatePropertyAsync(
                ActiveItemLinkType, ActiveItemId, definition.Id, defaultValue);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to add property '{Name}' to entity", definition.Name);
            ActiveItemProperties.Remove(vm);
        }
    }

    [RelayCommand]
    private async Task CreatePropertyDefinition(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var def = await _entityMetadataService.CreatePropertyDefinitionAsync(
                new PropertyDefinition { Name = name, Type = NewPropertyType });

            // If we have an active entity, add this property to it immediately
            if (ActiveItemLinkType != null && ActiveItemId != null)
            {
                IsAddPropertyOpen = false;
                var vm = new PropertyValueViewModel(def, null, _entityMetadataService, ActiveItemLinkType, ActiveItemId);
                vm.OnRemoved = OnPropertyRemoved;
                WireRelationDelegates(vm);
                ActiveItemProperties.Add(vm);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to create property definition '{Name}'", name);
        }
    }

    [RelayCommand]
    private void EditPropertyDefinition(PropertyValueViewModel? propVm)
    {
        if (propVm == null) return;

        EditingPropertyDef = propVm.Definition;
        EditingDefName = propVm.Definition.Name;
        EditingDefType = propVm.Definition.Type;
        EditingDefOptions = propVm.Definition.Options != null
            ? string.Join(", ", propVm.Definition.Options)
            : "";
    }

    [RelayCommand]
    private void CancelEditPropertyDefinition()
    {
        EditingPropertyDef = null;
    }

    [RelayCommand]
    private async Task SavePropertyDefinition()
    {
        if (EditingPropertyDef == null) return;
        var name = EditingDefName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var options = EditingDefType is PropertyType.Select or PropertyType.MultiSelect
                ? EditingDefOptions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList()
                : null;

            var updated = EditingPropertyDef with
            {
                Name = name,
                Type = EditingDefType,
                Options = options?.Count > 0 ? options : null,
            };

            await _entityMetadataService.UpdatePropertyDefinitionAsync(updated);
            EditingPropertyDef = null;

            // Reload properties to reflect the updated definition
            if (ActiveItemLinkType != null && ActiveItemId != null)
            {
                var metadata = await _entityMetadataService.GetMetadataAsync(ActiveItemLinkType, ActiveItemId);
                await BuildPropertyViewModelsAsync(ActiveItemLinkType, ActiveItemId, metadata.Properties, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to update property definition '{Name}'", name);
        }
    }

    private async Task LoadAvailablePropertyDefsAsync()
    {
        try
        {
            var allDefs = await _entityMetadataService.GetPropertyDefinitionsAsync();
            // Exclude properties already on this entity
            var existingIds = new HashSet<string>(ActiveItemProperties.Select(p => p.Definition.Id));
            var available = allDefs.Where(d => !existingIds.Contains(d.Id)).ToList();
            AvailablePropertyDefs = new ObservableCollection<PropertyDefinition>(available);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to load available property definitions");
        }
    }

    [RelayCommand]
    private async Task AddTag()
    {
        var tag = TagInput.Trim();
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (ActiveItemTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
        if (ActiveItemLinkType == null || ActiveItemId == null) return;

        ActiveItemTags.Add(tag);
        TagInput = "";
        TagSuggestions = [];

        try
        {
            await _entityMetadataService.UpdateTagsAsync(
                ActiveItemLinkType, ActiveItemId, ActiveItemTags.ToList());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to add tag '{Tag}' to {LinkType}:{EntityId}", tag, ActiveItemLinkType, ActiveItemId);
            ActiveItemTags.Remove(tag);
        }
    }

    /// <summary>
    /// Adds a tag from autocomplete selection (called from code-behind).
    /// </summary>
    public async Task AddTagFromAutoComplete(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (ActiveItemTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
        if (ActiveItemLinkType == null || ActiveItemId == null) return;

        ActiveItemTags.Add(tag);
        TagInput = "";
        TagSuggestions = [];

        try
        {
            await _entityMetadataService.UpdateTagsAsync(
                ActiveItemLinkType, ActiveItemId, ActiveItemTags.ToList());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to add tag '{Tag}' to {LinkType}:{EntityId}", tag, ActiveItemLinkType, ActiveItemId);
            ActiveItemTags.Remove(tag);
        }
    }

    [RelayCommand]
    private async Task RemoveTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (ActiveItemLinkType == null || ActiveItemId == null) return;
        if (!ActiveItemTags.Remove(tag)) return;

        try
        {
            await _entityMetadataService.UpdateTagsAsync(
                ActiveItemLinkType, ActiveItemId, ActiveItemTags.ToList());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to remove tag '{Tag}' from {LinkType}:{EntityId}", tag, ActiveItemLinkType, ActiveItemId);
            ActiveItemTags.Add(tag); // rollback
        }
    }

    private void ReloadGraphIfActive()
    {
        if (!IsOpen || ActiveItemLinkType == null || ActiveItemId == null) return;
        _ = LoadGraphDataAsync(ActiveItemLinkType, ActiveItemId);
    }

    private HashSet<string>? GetAllowedLinkTypes()
    {
        if (GraphLinkTypeFilters.Count == 0)
        {
            // Filters not built yet — use persisted hidden types directly
            var hidden = _appSettings.Settings.InfoPanelGraphHiddenTypes;
            if (hidden.Count == 0) return null;
            var all = AllLinkTypeMeta.Keys.ToHashSet();
            all.ExceptWith(hidden);
            return all;
        }

        var enabled = GraphLinkTypeFilters.Where(f => f.IsEnabled).Select(f => f.LinkType).ToHashSet();
        return enabled.Count == GraphLinkTypeFilters.Count ? null : enabled;
    }

    /// <summary>
    /// Rebuilds the filter checkbox list to only include link types that exist in the data.
    /// Preserves persisted hidden/enabled state.
    /// </summary>
    private async Task RebuildFiltersAsync(CancellationToken ct)
    {
        var availableTypes = await _backlinkService.GetAvailableLinkTypesAsync(ct);
        var hiddenTypes = new HashSet<string>(_appSettings.Settings.InfoPanelGraphHiddenTypes);

        // Build the set of types we need
        var currentTypes = GraphLinkTypeFilters.Select(f => f.LinkType).ToHashSet();

        // If the set hasn't changed, no rebuild needed
        if (currentTypes.SetEquals(availableTypes)) return;

        // Unsub from old filters
        foreach (var f in GraphLinkTypeFilters)
            f.PropertyChanged -= OnFilterPropertyChanged;

        var newFilters = new ObservableCollection<GraphLinkTypeFilter>();
        foreach (var (lt, meta) in AllLinkTypeMeta)
        {
            if (!availableTypes.Contains(lt)) continue;
            var filter = new GraphLinkTypeFilter(lt, meta.DisplayName, meta.Icon, !hiddenTypes.Contains(lt));
            filter.PropertyChanged += OnFilterPropertyChanged;
            newFilters.Add(filter);
        }
        GraphLinkTypeFilters = newFilters;
    }

    private void OnActivePluginChanged()
    {
        OnPropertyChanged(nameof(ShowGraphTab));

        // If Graph tab is hidden but was active, switch to Info tab
        if (!ShowGraphTab && ActiveTab == "Graph")
            ActiveTab = "Info";
    }

    private void OnContentChanged()
    {
        if (!IsOpen) return;
        if (ActiveItemLinkType == null || ActiveItemId == null) return;

        // Invalidate cached backlink index so we pick up new/removed links
        _backlinkService.Invalidate();
        _ = LoadDataAsync(ActiveItemLinkType, ActiveItemId);
    }

    private void OnActiveItemChanged()
    {
        var linkType = _infoPanelService.ActiveLinkType;
        var itemId = _infoPanelService.ActiveItemId;
        var title = _infoPanelService.ActiveItemTitle;

        ActiveItemLinkType = linkType;
        ActiveItemId = itemId;
        ActiveItemTitle = title;

        // Update plugin-provided detail fields
        var details = _infoPanelService.ActiveItemDetails;
        ActiveItemDetails = details != null
            ? new ObservableCollection<InfoPanelDetailField>(details)
            : [];

        // Update type display info
        if (linkType != null)
        {
            var info = EntityTypeMap.GetByLinkType(linkType);
            ActiveEntityTypeDisplay = info?.DisplayName;
            ActiveEntityTypeIcon = info?.Icon;
        }
        else
        {
            ActiveEntityTypeDisplay = null;
            ActiveEntityTypeIcon = null;
        }

        if (!IsOpen) return;

        if (linkType == null || itemId == null)
        {
            _loadCts?.Cancel();
            IsLoading = false;
            ForwardLinks.Clear();
            OnPropertyChanged(nameof(HasForwardLinks));
            OnPropertyChanged(nameof(ForwardLinkCountText));
            Backlinks.Clear();
            OnPropertyChanged(nameof(HasBacklinks));
            OnPropertyChanged(nameof(BacklinkCountText));
            ActiveItemTags.Clear();
            ActiveItemDetails.Clear();
            ActiveItemProperties.Clear();
            ActiveItemPropertyGroups.Clear();
            ActiveItemCreatedAt = null;
            ActiveItemModifiedAt = null;
            ActiveItemPreview = null;
            ActiveItemParentTitle = null;
            GraphNodes = null;
            GraphEdges = null;
            GraphCenterId = null;
            return;
        }

        _ = LoadDataAsync(linkType, itemId);
    }

    private async Task LoadDataAsync(string linkType, string itemId)
    {
        // Cancel any previous load
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        try
        {
            var backlinkTask = _backlinkService.GetBacklinksAsync(linkType, itemId, cts.Token);
            var forwardLinkTask = _backlinkService.GetForwardLinksAsync(linkType, itemId, cts.Token);
            var graphTask = _backlinkService.GetLocalGraphAsync(
                linkType, itemId, GraphDepth, GetAllowedLinkTypes(), cts.Token);
            var metadataTask = _entityMetadataService.GetMetadataAsync(linkType, itemId, cts.Token);

            await Task.WhenAll(backlinkTask, forwardLinkTask, graphTask, metadataTask);

            if (cts.Token.IsCancellationRequested) return;

            // Forward links
            var fwdLinks = await forwardLinkTask;
            ForwardLinks = new ObservableCollection<BacklinkEntry>(fwdLinks);
            OnPropertyChanged(nameof(HasForwardLinks));
            OnPropertyChanged(nameof(ForwardLinkCountText));

            // Backlinks
            var backlinks = await backlinkTask;
            Backlinks = new ObservableCollection<BacklinkEntry>(backlinks);
            OnPropertyChanged(nameof(HasBacklinks));
            OnPropertyChanged(nameof(BacklinkCountText));

            // Graph
            var (nodes, edges) = await graphTask;
            GraphCenterId = $"{linkType}:{itemId}";
            GraphNodes = nodes;
            GraphEdges = edges;

            // Metadata (timestamps + tags + properties + preview)
            var metadata = await metadataTask;
            ActiveItemCreatedAt = metadata.CreatedAt;
            ActiveItemModifiedAt = metadata.ModifiedAt;
            ActiveItemPreview = metadata.Preview;
            ActiveItemParentTitle = metadata.ParentTitle;
            ActiveItemTags = new ObservableCollection<string>(metadata.Tags);

            // Build property value VMs for any properties on this entity
            await BuildPropertyViewModelsAsync(linkType, itemId, metadata.Properties, cts.Token);

            await RebuildFiltersAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when item changes quickly
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load info panel data for {LinkType}:{ItemId}", linkType, itemId);
        }
        finally
        {
            if (_loadCts == cts)
                IsLoading = false;
        }
    }

    private async Task BuildPropertyViewModelsAsync(
        string linkType, string itemId,
        Dictionary<string, JsonElement> propertyValues,
        CancellationToken ct)
    {
        try
        {
            await _entityMetadataService.SeedDefaultPropertyGroupsAsync(ct);
            var allDefs = await _entityMetadataService.GetPropertyDefinitionsAsync(ct);
            var groups = await _entityMetadataService.GetPropertyGroupsAsync(ct);
            var groupLookup = groups.ToDictionary(g => g.Id, g => g.Name);

            var propVms = new ObservableCollection<PropertyValueViewModel>();

            foreach (var def in allDefs)
            {
                if (!propertyValues.TryGetValue(def.Id, out var value))
                    continue;

                var vm = new PropertyValueViewModel(def, value, _entityMetadataService, linkType, itemId);
                vm.OnRemoved = OnPropertyRemoved;
                WireRelationDelegates(vm);
                propVms.Add(vm);
            }

            ActiveItemProperties = propVms;
            _log.Debug("BuildPropertyViewModelsAsync: {PropCount} properties matched from {TotalDefs} defs and {ValueCount} values",
                propVms.Count, allDefs.Count, propertyValues.Count);

            // Build grouped display
            var grouped = propVms
                .GroupBy(vm => vm.GroupId ?? "")
                .OrderBy(g => g.Key == "" ? int.MaxValue : groups.FindIndex(gr => gr.Id == g.Key))
                .Select(g =>
                {
                    var groupName = g.Key != "" && groupLookup.TryGetValue(g.Key, out var name) ? name : "";
                    return new PropertyGroupViewModel(g.Key == "" ? null : g.Key, groupName, g);
                })
                .ToList();

            // If there's only one group with no name (all ungrouped), skip group headers
            ActiveItemPropertyGroups = new ObservableCollection<PropertyGroupViewModel>(grouped);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to build property VMs");
            ActiveItemProperties = [];
            ActiveItemPropertyGroups = [];
        }
    }

    private void WireRelationDelegates(PropertyValueViewModel vm)
    {
        if (vm.Type != PropertyType.Relation) return;

        vm.EntitySearcher = async (query, allowedTypes, max) =>
        {
            var providers = _pluginRegistry.GetCapabilityProviders<ILinkableItemProvider>();
            if (allowedTypes is { Count: > 0 })
                providers = providers.Where(p => allowedTypes.Contains(p.LinkType)).ToList();

            var tasks = providers.Select(p => p.SearchItemsAsync(query, max));
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(r => r).OrderByDescending(i => i.ModifiedAt).Take(max).ToList();
        };

        vm.EntityResolver = async (linkType, entityId) =>
        {
            var provider = _pluginRegistry.GetCapabilityProviders<ILinkableItemProvider>()
                .FirstOrDefault(p => p.LinkType == linkType);
            return provider != null ? await provider.GetItemByIdAsync(entityId) : null;
        };

        // Re-resolve now that delegates are wired (items may have loaded before delegates were set)
        _ = vm.ResolveRelationItemsAsync();
    }

    private void OnPropertyRemoved(PropertyValueViewModel removed)
    {
        ActiveItemProperties.Remove(removed);

        foreach (var group in ActiveItemPropertyGroups)
            group.Properties.Remove(removed);

        // Remove empty groups
        var emptyGroups = ActiveItemPropertyGroups.Where(g => g.Properties.Count == 0).ToList();
        foreach (var g in emptyGroups)
            ActiveItemPropertyGroups.Remove(g);
    }

    /// <summary>
    /// Reloads only the graph data (used when depth/filter changes — backlinks don't need reloading).
    /// </summary>
    private async Task LoadGraphDataAsync(string linkType, string itemId)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        try
        {
            var (nodes, edges) = await _backlinkService.GetLocalGraphAsync(
                linkType, itemId, GraphDepth, GetAllowedLinkTypes(), cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            GraphCenterId = $"{linkType}:{itemId}";
            GraphNodes = nodes;
            GraphEdges = edges;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to reload graph for {LinkType}:{ItemId}", linkType, itemId);
        }
        finally
        {
            if (_loadCts == cts)
                IsLoading = false;
        }
    }

    [RelayCommand]
    public void Toggle()
    {
        IsOpen = !IsOpen;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    [RelayCommand]
    private void SwitchTab(string tab)
    {
        ActiveTab = tab;
    }

    [RelayCommand]
    private async Task NavigateToActiveItem()
    {
        if (ActiveItemLinkType == null || ActiveItemId == null) return;
        if (NavigateToItemRequested != null)
            await NavigateToItemRequested(ActiveItemLinkType, ActiveItemId);
    }

    [RelayCommand]
    private async Task NavigateToBacklink(BacklinkEntry? entry)
    {
        if (entry == null) return;
        if (NavigateToItemRequested != null)
            await NavigateToItemRequested(entry.SourceLinkType, entry.SourceId);
    }

    [RelayCommand]
    private async Task NavigateToForwardLink(BacklinkEntry? entry)
    {
        if (entry == null) return;
        if (NavigateToItemRequested != null)
            await NavigateToItemRequested(entry.SourceLinkType, entry.SourceId);
    }

    /// <summary>
    /// Called from the NeuronGraphControl's NodeClicked event.
    /// Node IDs are in "linkType:entityId" format.
    /// </summary>
    public async Task NavigateToGraphNode(string nodeId)
    {
        var parts = nodeId.Split(':', 2);
        if (parts.Length != 2) return;
        if (NavigateToItemRequested != null)
            await NavigateToItemRequested(parts[0], parts[1]);
    }

    /// <summary>
    /// Invalidates the backlink cache (e.g. on workspace switch).
    /// </summary>
    public void InvalidateCache()
    {
        _backlinkService.Invalidate();
        _entityMetadataService.InvalidateAll();
        ForwardLinks.Clear();
        OnPropertyChanged(nameof(HasForwardLinks));
        OnPropertyChanged(nameof(ForwardLinkCountText));
        Backlinks.Clear();
        OnPropertyChanged(nameof(HasBacklinks));
        OnPropertyChanged(nameof(BacklinkCountText));
        ActiveItemTags.Clear();
        ActiveItemProperties.Clear();
        ActiveItemPropertyGroups.Clear();
        ActiveItemCreatedAt = null;
        ActiveItemModifiedAt = null;
        ActiveItemPreview = null;
        ActiveItemParentTitle = null;
        GraphNodes = null;
        GraphEdges = null;
        GraphCenterId = null;
        ActiveItemTitle = null;
        ActiveItemLinkType = null;
        ActiveItemId = null;
    }

    public void Cleanup()
    {
        _infoPanelService.ActiveItemChanged -= OnActiveItemChanged;
        _infoPanelService.ContentChanged -= OnContentChanged;
        _infoPanelService.ActivePluginChanged -= OnActivePluginChanged;
        foreach (var filter in GraphLinkTypeFilters)
            filter.PropertyChanged -= OnFilterPropertyChanged;
        _loadCts?.Cancel();
    }
}
