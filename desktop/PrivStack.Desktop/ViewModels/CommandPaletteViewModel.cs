using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Plugin;

namespace PrivStack.Desktop.ViewModels;

public enum PaletteMode
{
    Commands,
    PluginPalette,
}

public partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _mainVm;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ViewStatePrefetchService? _prefetchService;
    private readonly List<ICommandProvider> _providers = [];
    private List<CommandItem> _coreCommands = [];
    private List<CommandItem>? _cachedCommands;

    // Plugin palette storage: pluginId -> list of palette definitions
    private readonly Dictionary<string, List<PluginPaletteDefinition>> _pluginPalettes = [];

    private CancellationTokenSource? _searchCts;

    // Track last prefetched item to avoid duplicate prefetch requests
    private (string? PluginId, string? ItemId) _lastPrefetchedItem;

    /// <summary>
    /// Delegate to search linkable items across all plugins.
    /// Args: (query, maxResults) → list of results.
    /// </summary>
    public Func<string, int, Task<List<SearchResultItem>>>? LinkableItemSearcher { get; set; }

    /// <summary>
    /// Delegate to navigate to a linkable item within a plugin.
    /// Args: (pluginId, itemId, searchQuery).
    /// </summary>
    public Func<string, string, string?, Task>? LinkableItemNavigator { get; set; }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private CommandItem? _selectedCommand;

    [ObservableProperty]
    private PaletteMode _mode = PaletteMode.Commands;

    [ObservableProperty]
    private string _palettePlaceholder = "Search or type a command...";

    [ObservableProperty]
    private string? _paletteTitle;

    /// <summary>Plugin scope filter: display name shown in the pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPluginFilter))]
    private string? _filterPluginDisplayName;

    /// <summary>Plugin scope filter: link type key passed to the search delegate.</summary>
    [ObservableProperty]
    private string? _filterLinkType;

    /// <summary>Whether a plugin scope filter is active.</summary>
    public bool HasPluginFilter => FilterPluginDisplayName is not null;

    /// <summary>
    /// Currently active plugin palette's plugin ID (when in PluginPalette mode).
    /// </summary>
    public string? ActivePluginId { get; private set; }

    /// <summary>
    /// Currently active palette definition ID (when in PluginPalette mode).
    /// </summary>
    public string? ActivePaletteId { get; private set; }

    /// <summary>
    /// Fired when a plugin palette item is executed.
    /// Args: (pluginId, command, argsJson)
    /// </summary>
    public event Action<string, string, string>? PluginCommandRequested;

    public ObservableCollection<CommandItem> FilteredCommands { get; } = [];

    public CommandPaletteViewModel(MainWindowViewModel mainVm, IPluginRegistry pluginRegistry, ViewStatePrefetchService? prefetchService = null)
    {
        _mainVm = mainVm;
        _pluginRegistry = pluginRegistry;
        _prefetchService = prefetchService;
        InitializeCoreCommands();

        // Rebuild navigation commands when plugins are enabled/disabled
        _pluginRegistry.NavigationItemsChanged += (_, _) =>
        {
            InitializeCoreCommands();
        };
    }

    partial void OnSelectedCommandChanged(CommandItem? value)
    {
        // Prefetch linkable items when selected in the palette (via keyboard navigation or hover)
        if (value?.IsLinkableItem == true && value.PluginId != null && value.ItemId != null)
        {
            var current = (value.PluginId, value.ItemId);
            if (current != _lastPrefetchedItem)
            {
                _lastPrefetchedItem = current;
                // Use LinkType as the link type for prefetch (maps to plugin ID in the prefetch service)
                _prefetchService?.RequestPrefetch(value.PluginId, value.ItemId);
            }
        }
    }

    /// <summary>
    /// Registers a command provider. Providers inject their own commands into the palette.
    /// </summary>
    public void RegisterProvider(ICommandProvider provider)
    {
        if (!_providers.Contains(provider))
        {
            _providers.Add(provider);
            _providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            InvalidateCache();
        }
    }

    /// <summary>
    /// Unregisters a command provider.
    /// </summary>
    public void UnregisterProvider(ICommandProvider provider)
    {
        if (_providers.Remove(provider))
            InvalidateCache();
    }

    /// <summary>
    /// Eagerly rebuilds the cached command list. Call when plugin data
    /// changes in a way that affects command definitions (e.g. tasks added/removed).
    /// </summary>
    public void InvalidateCache() => _cachedCommands = BuildCommandCache();

    /// <summary>
    /// Registers plugin palette definitions for a given plugin.
    /// </summary>
    public void RegisterPalettes(string pluginId, List<PluginPaletteDefinition> palettes)
    {
        _pluginPalettes[pluginId] = palettes;
    }

    /// <summary>
    /// Unregisters all palette definitions for a given plugin.
    /// </summary>
    public void UnregisterPalettes(string pluginId)
    {
        _pluginPalettes.Remove(pluginId);
    }

    /// <summary>
    /// Clears the active plugin scope filter and re-runs the current search.
    /// </summary>
    public void ClearPluginFilter()
    {
        FilterPluginDisplayName = null;
        FilterLinkType = null;
        PalettePlaceholder = "Search or type a command...";

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var query = SearchQuery.Trim();
        if (query.Length < 2 || LinkableItemSearcher is null)
            FilterCommands();
        else
            _ = FilterCommandsAsync(query, _searchCts.Token);
    }

    /// <summary>
    /// Opens a specific plugin palette by plugin ID and palette ID.
    /// </summary>
    public void OpenPluginPalette(string pluginId, string paletteId)
    {
        if (!_pluginPalettes.TryGetValue(pluginId, out var palettes))
            return;

        var palette = palettes.Find(p => p.Id == paletteId);
        if (palette is null)
            return;

        ActivePluginId = pluginId;
        ActivePaletteId = paletteId;
        Mode = PaletteMode.PluginPalette;
        PaletteTitle = palette.Title;
        PalettePlaceholder = palette.Placeholder;
        SearchQuery = string.Empty;
        IsOpen = true;
    }

    /// <summary>
    /// Opens a plugin palette for the currently active plugin (based on SelectedTab).
    /// </summary>
    public void OpenPluginPaletteForActivePlugin(string paletteId)
    {
        var plugin = _pluginRegistry.GetPluginForNavItem(_mainVm.SelectedTab);
        if (plugin is null) return;

        OpenPluginPalette(plugin.Metadata.Id, paletteId);
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        var query = value.Trim();

        // When a plugin filter is active, always use async path (prepends filter prefix)
        if (HasPluginFilter && LinkableItemSearcher is not null)
        {
            _ = FilterCommandsAsync(query, ct);
            return;
        }

        if (query.Length < 2 || LinkableItemSearcher is null)
        {
            FilterCommands();
            return;
        }

        // Debounce: wait 150ms, then run async search
        _ = FilterCommandsAsync(query, ct);
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            // If opening in default Commands mode, reset palette state
            if (Mode == PaletteMode.Commands)
            {
                PalettePlaceholder = "Search or type a command...";
                PaletteTitle = null;
                ActivePluginId = null;
                ActivePaletteId = null;
            }
            SearchQuery = string.Empty;
            FilterCommands();
            SelectedCommand = FilteredCommands.FirstOrDefault();
        }
        else
        {
            // Reset mode and plugin filter on close
            FilterPluginDisplayName = null;
            FilterLinkType = null;
            Mode = PaletteMode.Commands;
            PaletteTitle = null;
            ActivePluginId = null;
            ActivePaletteId = null;
        }
    }

    private void InitializeCoreCommands()
    {
        _coreCommands =
        [
            // Navigation — generated dynamically from plugin registry
            ..BuildNavigationCommands(),

            // Sync (core responsibility)
            new CommandItem("Open Sync Panel", "Open the sync settings panel", "sync settings", "Sync", () => {
                if (!_mainVm.IsSyncPanelOpen)
                    _mainVm.ToggleSyncPanelCommand.Execute(null);
            }),
            new CommandItem("Close Sync Panel", "Close the sync settings panel", "sync close", "Sync", () => {
                if (_mainVm.IsSyncPanelOpen)
                    _mainVm.ToggleSyncPanelCommand.Execute(null);
            }),
            new CommandItem("Start Sync", "Start P2P sync", "sync start", "Sync", () => _mainVm.SyncVM.StartSyncCommand.Execute(null)),
            new CommandItem("Stop Sync", "Stop P2P sync", "sync stop", "Sync", () => _mainVm.SyncVM.StopSyncCommand.Execute(null)),
        ];

        InvalidateCache();
    }

    private IEnumerable<CommandItem> BuildNavigationCommands()
    {
        foreach (var navItem in _pluginRegistry.NavigationItems)
        {
            var id = navItem.Id;
            var name = navItem.DisplayName;
            yield return new CommandItem(
                $"Go to {name}",
                $"Navigate to {name} tab",
                name.ToLowerInvariant(),
                "Navigation",
                () => _mainVm.SelectTabCommand.Execute(id));
        }
    }

    private List<CommandItem> BuildCommandCache()
    {
        var commands = new List<CommandItem>(_coreCommands);

        foreach (var provider in _providers)
        {
            try
            {
                foreach (var def in provider.GetCommands())
                {
                    commands.Add(new CommandItem(
                        def.Name,
                        def.Description,
                        def.Keywords,
                        def.Category,
                        def.Execute
                    ));
                }
            }
            catch
            {
                // Isolate provider failures
            }
        }

        return commands;
    }

    private void FilterCommands()
    {
        FilteredCommands.Clear();

        if (Mode == PaletteMode.PluginPalette)
        {
            FilterPluginPaletteItems();
            return;
        }

        _cachedCommands ??= BuildCommandCache();

        var query = SearchQuery.Trim();

        // Plugin scope items appear at top when query matches a plugin name (and no filter active)
        if (!string.IsNullOrEmpty(query) && !HasPluginFilter)
            AddPluginScopeItems(query);

        // When filter is active, skip commands (linkable results come from async path)
        if (!HasPluginFilter)
        {
            var filtered = string.IsNullOrEmpty(query)
                ? _cachedCommands
                : _cachedCommands.Where(c =>
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Category.Contains(query, StringComparison.OrdinalIgnoreCase));

            foreach (var cmd in filtered.Take(12))
            {
                FilteredCommands.Add(cmd);
            }
        }

        SelectedCommand = FilteredCommands.FirstOrDefault();
    }

    private void FilterPluginPaletteItems()
    {
        if (ActivePluginId is null || ActivePaletteId is null) return;
        if (!_pluginPalettes.TryGetValue(ActivePluginId, out var palettes)) return;

        var palette = palettes.Find(p => p.Id == ActivePaletteId);
        if (palette is null) return;

        var query = SearchQuery.Trim();

        var filtered = string.IsNullOrEmpty(query)
            ? palette.Items
            : palette.Items.Where(item =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var item in filtered.Take(20))
        {
            FilteredCommands.Add(new CommandItem(
                item.Name,
                item.Description,
                item.Keywords,
                "Block",
                null)); // Action is handled via PluginCommandRequested
        }

        SelectedCommand = FilteredCommands.FirstOrDefault();
    }

    private void AddPluginScopeItems(string query)
    {
        var cache = App.Services.GetService<LinkProviderCacheService>();
        if (cache is null) return;

        foreach (var provider in cache.GetAll())
        {
            if (provider.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCommands.Add(new CommandItem(
                    provider.DisplayName,
                    $"Search within {provider.DisplayName}",
                    provider.LinkType,
                    "Filter",
                    null)
                {
                    IsPluginScope = true,
                    LinkType = provider.LinkType,
                    PluginId = provider.PluginId,
                    Icon = provider.Icon,
                });
            }
        }
    }

    private async Task FilterCommandsAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;

        // When filter is active, skip commands and increase max results
        List<CommandItem>? matchingCommands = null;
        if (!HasPluginFilter)
        {
            _cachedCommands ??= BuildCommandCache();
            matchingCommands = _cachedCommands
                .Where(c =>
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToList();
        }

        // Search linkable items — prepend filter prefix when plugin scope is active
        var maxResults = HasPluginFilter ? 12 : 8;
        List<SearchResultItem>? linkableResults = null;
        if (LinkableItemSearcher is not null)
        {
            try
            {
                var effectiveQuery = FilterLinkType != null
                    ? $"{FilterLinkType}:{query}"
                    : query;
                linkableResults = await LinkableItemSearcher(effectiveQuery, maxResults);
            }
            catch
            {
                // Isolate search failures
            }
        }

        if (ct.IsCancellationRequested) return;

        FilteredCommands.Clear();

        // Plugin scope items first (only when no filter active)
        if (!HasPluginFilter && !string.IsNullOrEmpty(query))
            AddPluginScopeItems(query);

        if (matchingCommands is not null)
        {
            foreach (var cmd in matchingCommands)
            {
                FilteredCommands.Add(cmd);
            }
        }

        if (linkableResults is not null)
        {
            // Rank by title relevance: exact > starts-with > contains > other
            if (!string.IsNullOrEmpty(query) && linkableResults.Count > 1)
            {
                int ScoreTitle(string title)
                {
                    if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
                    if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
                    if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
                    return 3;
                }
                linkableResults.Sort((a, b) => ScoreTitle(a.Title).CompareTo(ScoreTitle(b.Title)));
            }

            foreach (var item in linkableResults)
            {
                FilteredCommands.Add(new CommandItem(
                    item.Title,
                    item.Subtitle ?? "",
                    item.LinkType,
                    item.LinkTypeDisplayName,
                    null)
                {
                    LinkType = item.LinkType,
                    ItemId = item.Id,
                    PluginId = item.PluginId,
                    Icon = item.Icon,
                });
            }
        }

        SelectedCommand = FilteredCommands.FirstOrDefault();
    }

    [RelayCommand]
    private void Open()
    {
        Mode = PaletteMode.Commands;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    [RelayCommand]
    private void ExecuteSelected()
    {
        if (SelectedCommand is null) return;
        if (ExecuteCommandItem(SelectedCommand))
            Close();
    }

    [RelayCommand]
    private void ExecuteCommand(CommandItem? command)
    {
        if (command is null) return;
        if (ExecuteCommandItem(command))
            Close();
    }

    /// <returns>true if the palette should close after execution.</returns>
    private bool ExecuteCommandItem(CommandItem command)
    {
        if (Mode == PaletteMode.PluginPalette)
        {
            ExecutePluginPaletteItem(command);
            return true;
        }

        // Plugin scope filter — set filter and stay open
        if (command.IsPluginScope)
        {
            FilterPluginDisplayName = command.Name;
            FilterLinkType = command.LinkType;
            PalettePlaceholder = $"Search {command.Name}...";
            SearchQuery = "";
            return false;
        }

        // Linkable item result — navigate via delegate (pass search query for pre-filtering)
        if (command.PluginId is not null && command.ItemId is not null && LinkableItemNavigator is not null)
        {
            var query = SearchQuery?.Trim();
            _ = LinkableItemNavigator(command.PluginId, command.ItemId, string.IsNullOrEmpty(query) ? null : query);
            return true;
        }

        command.Action?.Invoke();
        return true;
    }

    private void ExecutePluginPaletteItem(CommandItem command)
    {
        if (ActivePluginId is null || ActivePaletteId is null) return;
        if (!_pluginPalettes.TryGetValue(ActivePluginId, out var palettes)) return;

        var palette = palettes.Find(p => p.Id == ActivePaletteId);
        if (palette is null) return;

        // Find the matching palette item by name
        var item = palette.Items.Find(i => i.Name == command.Name);
        if (item is null) return;

        PluginCommandRequested?.Invoke(ActivePluginId, item.Command, item.ArgsJson);
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (FilteredCommands.Count == 0) return;

        var currentIndex = SelectedCommand != null ? FilteredCommands.IndexOf(SelectedCommand) : -1;
        var nextIndex = (currentIndex + 1) % FilteredCommands.Count;
        SelectedCommand = FilteredCommands[nextIndex];
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (FilteredCommands.Count == 0) return;

        var currentIndex = SelectedCommand != null ? FilteredCommands.IndexOf(SelectedCommand) : 0;
        var prevIndex = currentIndex <= 0 ? FilteredCommands.Count - 1 : currentIndex - 1;
        SelectedCommand = FilteredCommands[prevIndex];
    }

}

public class CommandItem
{
    public string Name { get; }
    public string Description { get; }
    public string Keywords { get; }
    public string Category { get; }
    public Action? Action { get; }

    /// <summary>Link type display name for linkable item results.</summary>
    public string? LinkType { get; init; }

    /// <summary>Entity item ID for linkable item results.</summary>
    public string? ItemId { get; init; }

    /// <summary>Source plugin ID for linkable item results.</summary>
    public string? PluginId { get; init; }

    /// <summary>Icon identifier for linkable item results.</summary>
    public string? Icon { get; init; }

    /// <summary>Whether selecting this item sets a plugin scope filter instead of navigating.</summary>
    public bool IsPluginScope { get; init; }

    /// <summary>Whether this item is a linkable search result (vs a command or scope filter).</summary>
    public bool IsLinkableItem => PluginId is not null && !IsPluginScope;

    /// <summary>Whether to show the icon (linkable items and plugin scope items).</summary>
    public bool ShowIcon => Icon is not null;

    public CommandItem(string name, string description, string keywords, string category, Action? action)
    {
        Name = name;
        Description = description;
        Keywords = keywords;
        Category = category;
        Action = action;
    }

    // Legacy constructor for compatibility
    public CommandItem(string name, string description, string keywords, Action? action)
        : this(name, description, keywords, "General", action)
    {
    }
}

/// <summary>
/// DTO for linkable item search results returned by the search delegate.
/// </summary>
public sealed record SearchResultItem(
    string Id,
    string LinkType,
    string LinkTypeDisplayName,
    string Title,
    string? Subtitle,
    string? Icon,
    string? PluginId);
