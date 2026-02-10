using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IPrivStackRuntime _service;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IAppSettingsService _appSettings;
    private readonly IWorkspaceService _workspaceService;
    private readonly Dictionary<string, PrivStack.Sdk.ViewModelBase> _pluginViewModelCache = new();

    /// <summary>
    /// Gets the current ViewModel for the active plugin tab.
    /// The ViewLocator resolves the corresponding View from the ViewModel's assembly.
    /// </summary>
    [ObservableProperty]
    private PrivStack.Sdk.ViewModelBase? _currentViewModel;

    /// <summary>
    /// Gets the navigation items from all active plugins.
    /// </summary>
    public ObservableCollection<NavigationItem> NavigationItems =>
        _pluginRegistry.NavigationItemsObservable;

    [ObservableProperty]
    private string _statusMessage = "Initializing...";

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(IsInfoPanelAvailable))]
    [NotifyPropertyChangedFor(nameof(ShowInfoPanelTab))]
    private string _selectedTab = "Calendar";

    public string WindowTitle => $"PrivStack - {SelectedTab}";

    /// <summary>
    /// InfoPanel is not available for plugins that show global views (e.g., Graph/Nexus).
    /// </summary>
    public bool IsInfoPanelAvailable =>
        _pluginRegistry.GetPluginForNavItem(SelectedTab)?.Metadata.SupportsInfoPanel ?? true;

    /// <summary>
    /// Show the collapsed InfoPanel tab when: available AND panel is closed.
    /// </summary>
    public bool ShowInfoPanelTab => IsInfoPanelAvailable && !(InfoPanelVM?.IsOpen ?? false);

    // ============================================================
    // Timer Forwarding — discovered via IMultiTimerBehavior / ITimerBehavior
    // ============================================================

    private ITimerBehavior? _activeTimer;
    private IMultiTimerBehavior? _multiTimer;

    // Legacy single-timer façade (backward compat for any consumers)
    public bool IsTaskTimerActive => _activeTimer?.IsTimerActive ?? false;
    public string TaskTimerDisplay => _activeTimer?.TimerDisplay ?? "00:00:00";
    public string? TimedTaskTitle => _activeTimer?.TimedItemTitle;
    public bool IsTaskTimerRunning => _activeTimer?.IsTimerRunning ?? false;
    public bool IsTaskTimerPaused => IsTaskTimerActive && !IsTaskTimerRunning;

    // Multi-timer properties for sidebar
    public IReadOnlyList<ActiveTimerInfo> AllActiveTimers => _multiTimer?.ActiveTimers ?? [];
    public int ActiveTimerCount => _multiTimer?.ActiveTimerCount ?? (IsTaskTimerActive ? 1 : 0);
    public bool HasActiveTimers => ActiveTimerCount > 0;
    public bool HasMultipleTimers => ActiveTimerCount > 1;
    public string AdditionalTimerText => ActiveTimerCount > 1 ? $"+{ActiveTimerCount - 1}" : "";

    // ============================================================
    // Shell ViewModels (not plugins)
    // ============================================================

    private SyncViewModel? _syncVM;
    public SyncViewModel SyncVM => _syncVM ??= new SyncViewModel(
        App.Services.GetRequiredService<ISyncService>(),
        App.Services.GetRequiredService<IPairingService>(),
        App.Services.GetRequiredService<IUiDispatcher>());

    private CommandPaletteViewModel? _commandPaletteVM;
    public CommandPaletteViewModel CommandPaletteVM
    {
        get
        {
            if (_commandPaletteVM == null)
            {
                var prefetchService = App.Services.GetService<ViewStatePrefetchService>();
                _commandPaletteVM = new CommandPaletteViewModel(this, _pluginRegistry, prefetchService);
                WireCommandPaletteDelegates(_commandPaletteVM);
            }
            return _commandPaletteVM;
        }
    }

    private UpdateViewModel? _updateVM;
    public UpdateViewModel UpdateVM => _updateVM ??= new UpdateViewModel(
        App.Services.GetRequiredService<Services.Abstractions.IUpdateService>(),
        App.Services.GetRequiredService<IDialogService>(),
        App.Services.GetRequiredService<IUiDispatcher>(),
        _appSettings);

    private SettingsViewModel? _settingsVM;
    public SettingsViewModel SettingsVM
    {
        get
        {
            if (_settingsVM == null)
            {
                _settingsVM = App.Services.GetRequiredService<SettingsViewModel>();
                _settingsVM.SwitchWorkspaceRequested += (_, _) =>
                {
                    IsSettingsPanelOpen = false;
                    WorkspaceSwitcherVM.OpenCommand.Execute(null);
                };
            }
            return _settingsVM;
        }
    }

    private SpeechRecordingViewModel? _speechRecordingVM;
    public SpeechRecordingViewModel SpeechRecordingVM => _speechRecordingVM ??= new SpeechRecordingViewModel(_appSettings);

    private WorkspaceSwitcherViewModel? _workspaceSwitcherVM;
    public WorkspaceSwitcherViewModel WorkspaceSwitcherVM => _workspaceSwitcherVM ??= new WorkspaceSwitcherViewModel(_workspaceService);

    private InfoPanelViewModel? _infoPanelVM;
    public InfoPanelViewModel InfoPanelVM
    {
        get
        {
            if (_infoPanelVM == null)
            {
                _infoPanelVM = new InfoPanelViewModel(
                    App.Services.GetRequiredService<Services.InfoPanelService>(),
                    App.Services.GetRequiredService<Services.BacklinkService>(),
                    App.Services.GetRequiredService<Services.EntityMetadataService>(),
                    _appSettings,
                    _pluginRegistry);
                _infoPanelVM.NavigateToItemRequested += (linkType, itemId) =>
                    NavigateToLinkedItemAsync(linkType, itemId);
                _infoPanelVM.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(InfoPanelViewModel.IsOpen))
                        OnPropertyChanged(nameof(ShowInfoPanelTab));
                };
            }
            return _infoPanelVM;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayPanelOpen))]
    private bool _isSyncPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayPanelOpen))]
    private bool _isUpdatePanelOpen;

    [ObservableProperty]
    private bool _isUserMenuOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayPanelOpen))]
    private bool _isSettingsPanelOpen;

    public bool IsAnyOverlayPanelOpen =>
        IsSyncPanelOpen || IsUpdatePanelOpen || IsSettingsPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarCollapseTooltip))]
    [NotifyPropertyChangedFor(nameof(PanelLeftMargin))]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isFocusMode;

    public string UserDisplayName => _appSettings.Settings.UserDisplayName
        ?? Environment.UserName
        ?? "User";

    public string UserInitial => string.IsNullOrEmpty(UserDisplayName)
        ? "U"
        : UserDisplayName[0].ToString().ToUpperInvariant();

    public string SidebarCollapseTooltip => IsSidebarCollapsed ? "Expand sidebar" : "Collapse sidebar";

    /// <summary>
    /// Left margin for floating panels that attach to the sidebar.
    /// Adjusts based on whether the sidebar is collapsed (56px) or expanded (220px).
    /// </summary>
    public Thickness PanelLeftMargin => IsSidebarCollapsed
        ? new Thickness(56, 0, 0, 0)
        : new Thickness(220, 0, 0, 0);

    public string CurrentWorkspaceName
    {
        get
        {
            var ws = _workspaceService.GetActiveWorkspace();
            return ws?.Name ?? "PrivStack";
        }
    }

    public string Version => $"v{PrivStackService.Version}";

    public bool IsInitialized => _service.IsInitialized;

    public MainWindowViewModel(
        IPrivStackRuntime nativeService,
        IPluginRegistry pluginRegistry,
        IAppSettingsService appSettings,
        IWorkspaceService workspaceService)
    {
        _service = nativeService;
        _pluginRegistry = pluginRegistry;
        _appSettings = appSettings;
        _workspaceService = workspaceService;

        if (_service.IsInitialized)
        {
            StatusMessage = "Ready";
        }

        _pluginRegistry.SetMainViewModel(this);

        IsSidebarCollapsed = _appSettings.Settings.SidebarCollapsed;

        // Discover and bind timer behavior from plugins
        BindTimerBehavior();

        // Recover persisted timer state from a previous session
        var savedTimers = _appSettings.Settings.ActiveTimers;
        if (savedTimers.Count > 0)
        {
            foreach (var state in savedTimers)
                _activeTimer?.RestoreState(state);
            _appSettings.Settings.ActiveTimers = [];
            _appSettings.SaveDebounced();
        }
        else if (_appSettings.Settings.ActiveTimer != null)
        {
            // Legacy single-timer migration
            _activeTimer?.RestoreState(_appSettings.Settings.ActiveTimer);
            _appSettings.Settings.ActiveTimer = null;
            _appSettings.SaveDebounced();
        }

        // Subscribe to focus mode changes from plugins
        var focusService = App.Services.GetRequiredService<IFocusModeService>();
        focusService.FocusModeChanged += enabled =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsFocusMode = enabled);

        // Subscribe to workspace changes for hot-swap
        _workspaceService.WorkspaceChanged += OnWorkspaceChanged;

        // Register plugin command palettes
        RegisterPluginPalettes();

        // Navigate to the last active tab or the first available plugin
        var lastTab = _appSettings.Settings.LastActiveTab;
        if (!string.IsNullOrEmpty(lastTab) && _pluginRegistry.GetPluginForNavItem(lastTab) != null)
        {
            _ = SelectTab(lastTab);
        }
        else if (_pluginRegistry.NavigationItems.Count > 0)
        {
            _ = SelectTab(_pluginRegistry.NavigationItems[0].Id);
        }

        // Start auto-check for updates (checks setting internally)
        UpdateVM.StartAutoCheck(TimeSpan.FromHours(4));
    }

    private void WireCommandPaletteDelegates(CommandPaletteViewModel palette)
    {
        // Get the cached link provider service for display name and icon lookups
        var linkProviderCache = App.Services.GetService<LinkProviderCacheService>();

        palette.LinkableItemSearcher = async (query, maxResults) =>
        {
            var results = new List<SearchResultItem>();

            // Parse prefix filter (e.g., "journal:", "notes:", "tasks:")
            // Also supports just the type name without colon (e.g., "journal" shows all journal entries)
            string? filterLinkType = null;
            var searchQuery = query;
            var providers = linkProviderCache?.GetAll() ?? [];

            var colonIndex = query.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = query[..colonIndex].ToLowerInvariant();
                // Check if prefix matches a known link type
                var matchedProvider = providers.FirstOrDefault(p =>
                    p.LinkType.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    p.DisplayName.Equals(prefix, StringComparison.OrdinalIgnoreCase));

                if (matchedProvider != null)
                {
                    filterLinkType = matchedProvider.LinkType;
                    searchQuery = colonIndex < query.Length - 1 ? query[(colonIndex + 1)..].TrimStart() : "";
                }
            }
            else
            {
                // No colon - check if entire query matches a link type or display name
                var trimmedQuery = query.Trim().ToLowerInvariant();
                var exactMatch = providers.FirstOrDefault(p =>
                    p.LinkType.Equals(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                    p.DisplayName.Equals(trimmedQuery, StringComparison.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    filterLinkType = exactMatch.LinkType;
                    searchQuery = ""; // Show all items of this type
                }
            }

            // Build icon lookup dictionary for link types
            var iconByLinkType = (linkProviderCache?.GetAll() ?? [])
                .ToDictionary(p => p.LinkType, p => p.Icon, StringComparer.OrdinalIgnoreCase);

            // Search native C# plugins via ILinkableItemProvider
            var nativeProviders = _pluginRegistry.GetCapabilityProviders<ILinkableItemProvider>();
            foreach (var provider in nativeProviders)
            {
                // Skip if filtering to a specific type that doesn't match
                if (filterLinkType != null && !provider.LinkType.Equals(filterLinkType, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var items = await provider.SearchItemsAsync(searchQuery, maxResults);
                    var plugin = _pluginRegistry.ActivePlugins.FirstOrDefault(p => p is ILinkableItemProvider lp && lp.LinkType == provider.LinkType);

                    foreach (var item in items)
                    {
                        results.Add(new SearchResultItem(
                            Id: item.Id,
                            LinkType: item.LinkType,
                            LinkTypeDisplayName: provider.LinkTypeDisplayName,
                            Title: item.Title,
                            Subtitle: item.Subtitle,
                            Icon: provider.LinkTypeIcon,
                            PluginId: plugin?.Metadata.Id));

                        if (results.Count >= maxResults) break;
                    }
                }
                catch { /* ignore search errors from individual providers */ }

                if (results.Count >= maxResults) break;
            }

            // If we already have enough results from native plugins, return early
            if (results.Count >= maxResults)
                return results;

            // Also search FFI (WASM plugins)
            await Task.Run(() =>
            {
                var resultPtr = Native.NativeLibrary.PluginSearchItems(searchQuery, maxResults * 2);
                if (resultPtr == nint.Zero) return;

                var json = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resultPtr);
                Native.NativeLibrary.FreeString(resultPtr);
                if (string.IsNullOrEmpty(json)) return;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var linkType = item.GetProperty("link_type").GetString() ?? "";

                        // Apply link type filter if specified
                        if (filterLinkType != null && !linkType.Equals(filterLinkType, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var displayName = linkProviderCache?.GetDisplayNameForLinkType(linkType) ?? linkType;
                        var icon = iconByLinkType.TryGetValue(linkType, out var typeIcon) ? typeIcon : null;

                        results.Add(new SearchResultItem(
                            Id: item.GetProperty("id").GetString() ?? "",
                            LinkType: linkType,
                            LinkTypeDisplayName: displayName,
                            Title: item.GetProperty("title").GetString() ?? "",
                            Subtitle: item.TryGetProperty("subtitle", out var sub) ? sub.GetString() : null,
                            Icon: icon,
                            PluginId: item.TryGetProperty("plugin_id", out var pid) ? pid.GetString() : null));

                        if (results.Count >= maxResults) break;
                    }
                }
                catch { /* ignore parse errors */ }
            });

            return results;
        };

        palette.LinkableItemNavigator = async (pluginId, itemId) =>
        {
            // Find the plugin's nav item and switch to its tab
            var plugin = _pluginRegistry.ActivePlugins
                .FirstOrDefault(p => p.Metadata.Id == pluginId);

            string? navItemId = plugin?.NavigationItem?.Id;
            if (navItemId != null)
            {
                await SelectTab(navItemId);
            }

            // Invoke the deep-link-target on the plugin (mutates plugin state)
            await Task.Run(() => Native.NativeLibrary.PluginNavigateToItem(pluginId, itemId));

            // Refresh the view so the UI reflects the navigated-to item
            if (navItemId != null
                && _pluginViewModelCache.TryGetValue(navItemId, out var vm)
                && vm is WasmViewModelProxy wasmVm)
            {
                wasmVm.RefreshViewState();
            }
        };
    }

    /// <summary>
    /// Navigate to a specific item in a WASM plugin by switching tabs and invoking deep-link-target.
    /// Called by WasmPluginView for cross-plugin internal link navigation.
    /// </summary>
    public async Task NavigateToPluginItemAsync(string pluginId, string itemId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] START pluginId={PluginId}, itemId={ItemId}",
            sw.ElapsedMilliseconds, pluginId, itemId);

        var plugin = _pluginRegistry.ActivePlugins
            .FirstOrDefault(p => p.Metadata.Id == pluginId);

        Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Found plugin={Found}, name={Name}",
            sw.ElapsedMilliseconds, plugin != null, plugin?.Metadata.Name ?? "(null)");

        string? navItemId = plugin?.NavigationItem?.Id;
        if (navItemId != null)
        {
            // Use SelectTabForEntityNavigation to avoid double-render
            // (normal SelectTab triggers OnNavigatedToAsync which refreshes, then we'd refresh again)
            Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Switching tab to navItemId={NavItemId}",
                sw.ElapsedMilliseconds, navItemId);
            await SelectTabForEntityNavigation(navItemId);
            Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Tab switch complete", sw.ElapsedMilliseconds);
        }
        else
        {
            Serilog.Log.Warning("NavigateToPluginItem: [T+{T}ms] No navItemId - plugin has no NavigationItem!", sw.ElapsedMilliseconds);
        }

        // Check prefetch cache - use cached view data to render immediately
        var prefetchService = App.Services.GetService<ViewStatePrefetchService>();
        var cached = prefetchService?.TryGetCached(pluginId, itemId);
        Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Prefetch cache check: hit={CacheHit}",
            sw.ElapsedMilliseconds, cached != null);

        // Fire off PluginNavigateToItem in the background to update plugin state (selected_page_id, etc.)
        // Don't await - we'll use cached data for immediate render, state update happens in parallel
        Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Firing FFI PluginNavigateToItem (background)", sw.ElapsedMilliseconds);
        _ = Task.Run(() => Native.NativeLibrary.PluginNavigateToItem(pluginId, itemId));

        if (navItemId != null
            && _pluginViewModelCache.TryGetValue(navItemId, out var vm)
            && vm is WasmViewModelProxy wasmVm)
        {
            if (cached != null)
            {
                // Cache hit: use prefetched data for instant render
                Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Using prefetched data, len={Len}",
                    sw.ElapsedMilliseconds, cached.ViewStateJson.Length);
                wasmVm.RefreshViewStateFromCache(cached.ViewStateJson, pluginId, itemId);
                Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] RefreshViewStateFromCache returned", sw.ElapsedMilliseconds);
            }
            else
            {
                // Cache miss: fetch fresh data via FFI
                Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] Cache miss, calling wasmVm.RefreshViewState", sw.ElapsedMilliseconds);
                wasmVm.RefreshViewState(pluginId, itemId);
                Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] RefreshViewState returned", sw.ElapsedMilliseconds);
            }
        }
        else
        {
            Serilog.Log.Warning("NavigateToPluginItem: [T+{T}ms] Could not find WasmViewModelProxy for navItemId={NavItemId}",
                sw.ElapsedMilliseconds, navItemId);
        }

        Serilog.Log.Debug("NavigateToPluginItem: [T+{T}ms] END", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Switches to a tab without triggering OnNavigatedToAsync refresh.
    /// Used by NavigateToPluginItemAsync to avoid double-render when navigating to a specific entity.
    /// </summary>
    private Task SelectTabForEntityNavigation(string tabName)
    {
        var previousPlugin = _pluginRegistry.GetPluginForNavItem(SelectedTab);
        previousPlugin?.OnNavigatedFrom();

        SelectedTab = tabName;
        _appSettings.UpdateLastActiveTab(tabName);
        _pluginRegistry.UpdateSelectedNavItem(tabName);

        if (!_service.IsInitialized)
        {
            StatusMessage = "Not initialized";
            return Task.CompletedTask;
        }

        var plugin = _pluginRegistry.GetPluginForNavItem(tabName);
        if (plugin == null)
        {
            StatusMessage = $"Plugin not found for: {tabName}";
            return Task.CompletedTask;
        }

        // Track active plugin for prefetch service
        App.Services.GetService<ViewStatePrefetchService>()?.SetActivePlugin(plugin.Metadata.Id);

        // Notify InfoPanel which plugin is active
        App.Services.GetRequiredService<Services.InfoPanelService>().SetActivePlugin(plugin.Metadata.Id);

        CurrentViewModel = GetOrCreatePluginViewModel(tabName, plugin);

        // Skip OnNavigatedToAsync - caller will handle refresh after entity navigation
        // This avoids double-render when navigating to a specific entity

        StatusMessage = "Ready";
        return Task.CompletedTask;
    }

    private void RegisterPluginPalettes()
    {
        foreach (var plugin in _pluginRegistry.ActivePlugins)
        {
            if (plugin is Services.Plugin.WasmPluginProxy wasmProxy && wasmProxy.CommandPalettes is { Count: > 0 })
            {
                CommandPaletteVM.RegisterPalettes(wasmProxy.Metadata.Id, wasmProxy.CommandPalettes);
            }
        }
    }

    /// <summary>
    /// Discovers IMultiTimerBehavior (preferred) or ITimerBehavior and wires property change forwarding.
    /// </summary>
    private void BindTimerBehavior()
    {
        // Unsubscribe from previous timer if any
        if (_activeTimer is INotifyPropertyChanged oldNpc)
        {
            oldNpc.PropertyChanged -= OnTimerPropertyChanged;
        }

        var providers = _pluginRegistry.GetCapabilityProviders<ITimerBehavior>();
        _multiTimer = providers.OfType<IMultiTimerBehavior>().FirstOrDefault();
        _activeTimer = _multiTimer ?? providers.FirstOrDefault();

        if (_activeTimer is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += OnTimerPropertyChanged;
        }

        NotifyTimerProperties();
    }

    private void OnTimerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyTimerProperties();
    }

    private void NotifyTimerProperties()
    {
        OnPropertyChanged(nameof(IsTaskTimerActive));
        OnPropertyChanged(nameof(IsTaskTimerRunning));
        OnPropertyChanged(nameof(IsTaskTimerPaused));
        OnPropertyChanged(nameof(TimedTaskTitle));
        OnPropertyChanged(nameof(TaskTimerDisplay));
        OnPropertyChanged(nameof(AllActiveTimers));
        OnPropertyChanged(nameof(ActiveTimerCount));
        OnPropertyChanged(nameof(HasActiveTimers));
        OnPropertyChanged(nameof(HasMultipleTimers));
        OnPropertyChanged(nameof(AdditionalTimerText));
    }

    [RelayCommand]
    private void Initialize()
    {
        if (!_service.IsInitialized)
        {
            StatusMessage = "Service not initialized";
            return;
        }

        StatusMessage = "Ready";
    }

    [RelayCommand]
    private async Task ToggleSyncPanel()
    {
        IsSyncPanelOpen = !IsSyncPanelOpen;
        IsUpdatePanelOpen = false;
        if (IsSyncPanelOpen)
        {
            await SyncVM.RefreshStatus();
            SyncVM.StartRefreshTimer();
        }
        else
        {
            SyncVM.StopRefreshTimer();
        }
    }

    [RelayCommand]
    private void ToggleUpdatePanel()
    {
        IsUpdatePanelOpen = !IsUpdatePanelOpen;
        IsSyncPanelOpen = false;
        IsSettingsPanelOpen = false;
    }

    [RelayCommand]
    private void ToggleUserMenu()
    {
        IsUserMenuOpen = !IsUserMenuOpen;
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        _appSettings.Settings.SidebarCollapsed = IsSidebarCollapsed;
        _appSettings.SaveDebounced();
    }

    [RelayCommand]
    private void ToggleInfoPanel()
    {
        InfoPanelVM.Toggle();
    }

    [RelayCommand]
    private void CloseUserMenu()
    {
        IsUserMenuOpen = false;
    }

    [RelayCommand]
    private async Task OpenSyncFromMenu()
    {
        IsUserMenuOpen = false;
        IsSyncPanelOpen = true;
        IsUpdatePanelOpen = false;
        IsSettingsPanelOpen = false;
        await SyncVM.RefreshStatus();
        SyncVM.StartRefreshTimer();
    }

    [RelayCommand]
    private void OpenUpdateFromMenu()
    {
        IsUserMenuOpen = false;
        IsUpdatePanelOpen = true;
        IsSyncPanelOpen = false;
        IsSettingsPanelOpen = false;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsUserMenuOpen = false;
        IsSettingsPanelOpen = true;
        IsSyncPanelOpen = false;
        IsUpdatePanelOpen = false;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsPanelOpen = false;
    }

    [RelayCommand]
    private void CloseAllPanels()
    {
        if (IsSyncPanelOpen)
        {
            IsSyncPanelOpen = false;
            SyncVM.StopRefreshTimer();
        }
        IsUpdatePanelOpen = false;
        IsSettingsPanelOpen = false;
        IsUserMenuOpen = false;
    }

    [RelayCommand]
    private async Task NavigateToTimedTask()
    {
        if (_activeTimer == null || !_activeTimer.IsTimerActive)
            return;

        // Find the plugin that provides the timer behavior and navigate to it
        var timerPlugin = _pluginRegistry.ActivePlugins
            .FirstOrDefault(p => p is ITimerBehavior ||
                _pluginRegistry.GetCapabilityProviders<ITimerBehavior>().Any(t => ReferenceEquals(t, _activeTimer)));

        if (timerPlugin?.NavigationItem != null)
        {
            await SelectTab(timerPlugin.NavigationItem.Id);
        }
    }

    [RelayCommand]
    private void PauseTaskTimer()
    {
        _activeTimer?.PauseTimer();
    }

    [RelayCommand]
    private void ResumeTaskTimer()
    {
        _activeTimer?.ResumeTimer();
    }

    [RelayCommand]
    private void StopTaskTimer()
    {
        _activeTimer?.StopTimer();
    }

    // Per-timer commands for multi-timer sidebar
    [RelayCommand]
    private void PauseSpecificTimer(string? itemId)
    {
        if (itemId != null) _multiTimer?.PauseTimer(itemId);
    }

    [RelayCommand]
    private void ResumeSpecificTimer(string? itemId)
    {
        if (itemId != null) _multiTimer?.ResumeTimer(itemId);
    }

    [RelayCommand]
    private void StopSpecificTimer(string? itemId)
    {
        if (itemId != null) _multiTimer?.StopTimer(itemId);
    }

    [RelayCommand]
    private async Task NavigateToSpecificTimer(string? itemId)
    {
        if (itemId == null || _activeTimer == null) return;

        var timerPlugin = _pluginRegistry.ActivePlugins
            .FirstOrDefault(p => p is ITimerBehavior ||
                _pluginRegistry.GetCapabilityProviders<ITimerBehavior>().Any(t => ReferenceEquals(t, _activeTimer)));

        if (timerPlugin?.NavigationItem != null)
        {
            await SelectTab(timerPlugin.NavigationItem.Id);
            // Deep-link to the specific task via IDeepLinkTarget
            if (timerPlugin is IDeepLinkTarget deepLink)
            {
                await deepLink.NavigateToItemAsync(itemId);
            }
        }
    }

    /// <summary>
    /// Navigates to the specified tab/plugin using the plugin lifecycle.
    /// </summary>
    [RelayCommand]
    private async Task SelectTab(string tabName)
    {
        var previousPlugin = _pluginRegistry.GetPluginForNavItem(SelectedTab);
        previousPlugin?.OnNavigatedFrom();

        // Clear info panel active item during tab transition;
        // the new plugin will re-set it via OnNavigatedToAsync
        var infoPanelService = App.Services.GetRequiredService<Services.InfoPanelService>();
        infoPanelService.ClearActiveItem();

        SelectedTab = tabName;

        _appSettings.UpdateLastActiveTab(tabName);

        _pluginRegistry.UpdateSelectedNavItem(tabName);

        if (!_service.IsInitialized)
        {
            StatusMessage = "Not initialized";
            return;
        }

        var plugin = _pluginRegistry.GetPluginForNavItem(tabName);
        if (plugin == null)
        {
            StatusMessage = $"Plugin not found for: {tabName}";
            return;
        }

        // Track active plugin for prefetch service (avoids same-plugin entity prefetch)
        App.Services.GetService<ViewStatePrefetchService>()?.SetActivePlugin(plugin.Metadata.Id);

        // Notify InfoPanel which plugin is active (hides Graph tab when Graph plugin is active)
        infoPanelService.SetActivePlugin(plugin.Metadata.Id);

        CurrentViewModel = GetOrCreatePluginViewModel(tabName, plugin);

        await plugin.OnNavigatedToAsync();

        // Aggressively reclaim memory from the previous tab's stale objects.
        // Also run DuckDB maintenance (WAL checkpoint + vacuum) to release native buffers.
        // Run on a background thread to avoid blocking the UI.
        _ = Task.Run(async () =>
        {
            // Compacting GC decommits unused memory pages back to the OS,
            // reducing WorkingSet rather than just freeing managed heap.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            try
            {
                var sdk = App.Services.GetService<IPrivStackSdk>();
                if (sdk != null)
                {
                    await sdk.RunDatabaseMaintenance();
                    Serilog.Log.Information("[TabSwitch] DbMaintenance completed | Heap={Heap}MB | WS={WS}MB",
                        GC.GetTotalMemory(false) / 1024 / 1024,
                        System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[TabSwitch] DbMaintenance failed (non-critical)");
            }
        });

        Serilog.Log.Information("[TabSwitch] → {Tab} | Heap={Heap}MB | WS={WS}MB",
            tabName,
            GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024,
            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024);

        StatusMessage = "Ready";
    }

    private PrivStack.Sdk.ViewModelBase GetOrCreatePluginViewModel(string tabName, IAppPlugin plugin)
    {
        if (_pluginViewModelCache.TryGetValue(tabName, out var cached))
        {
            return cached;
        }

        var viewModel = plugin.CreateViewModel();
        _pluginViewModelCache[tabName] = viewModel;
        return viewModel;
    }

    /// <summary>
    /// Evicts a plugin's cached ViewModel and re-navigates to it if it's the active tab.
    /// Used after a plugin reset to force a full reload.
    /// </summary>
    public async Task ReloadPluginAsync(string navItemId)
    {
        _pluginViewModelCache.Remove(navItemId);

        if (SelectedTab == navItemId)
        {
            await SelectTab(navItemId);
        }
    }

    [RelayCommand]
    private void OpenWorkspaceSwitcher()
    {
        WorkspaceSwitcherVM.OpenCommand.Execute(null);
    }

    private void OnWorkspaceChanged(object? sender, Workspace workspace)
    {
        // Stop reminder timer before teardown — prevents polls against uninitialized native lib
        try { App.Services.GetRequiredService<ReminderSchedulerService>().Stop(); }
        catch { /* Ignore if not registered */ }

        _pluginViewModelCache.Clear();
        _syncVM = null;

        // Clear prefetch cache on workspace change
        App.Services.GetService<ViewStatePrefetchService>()?.Clear();

        // Invalidate backlink cache on workspace change
        _infoPanelVM?.InvalidateCache();

        // Invalidate link provider cache — stale metadata from old workspace
        App.Services.GetService<LinkProviderCacheService>()?.Invalidate();

        // Full teardown and rediscovery — Rust core was re-initialized with new DB
        _pluginRegistry.Reinitialize();

        // Re-bind timer behavior after workspace switch
        BindTimerBehavior();

        OnPropertyChanged(nameof(CurrentWorkspaceName));

        if (_pluginRegistry.NavigationItems.Count > 0)
        {
            _ = SelectTab(_pluginRegistry.NavigationItems[0].Id);
        }

        // Restart reminder timer with fresh state for new workspace
        try { App.Services.GetRequiredService<ReminderSchedulerService>().Start(); }
        catch { /* Ignore if not registered */ }

        StatusMessage = "Ready";
    }

    public void Cleanup()
    {
        // Notify all IShutdownAware providers (this logs time for active timers)
        foreach (var aware in _pluginRegistry.GetCapabilityProviders<IShutdownAware>())
        {
            aware.OnShutdown();
        }

        // Clear persisted timer state on graceful shutdown (time was already logged)
        _appSettings.Settings.ActiveTimer = null;
        _appSettings.Settings.ActiveTimers = [];
        _appSettings.SaveDebounced();

        // Save timer state if active (backward compat)
        _activeTimer?.SaveOnShutdown();

        // Stop reminder scheduler
        try { App.Services.GetRequiredService<ReminderSchedulerService>().Dispose(); }
        catch { /* Ignore if not registered */ }

        _infoPanelVM?.Cleanup();
        _syncVM?.StopRefreshTimer();
        _updateVM?.Cleanup();
        _service.Dispose();
    }

    /// <summary>
    /// Navigates to a linked item by finding the appropriate IDeepLinkTarget plugin.
    /// </summary>
    public async Task NavigateToLinkedItemAsync(string linkType, string itemId)
    {
        var target = _pluginRegistry.GetCapabilityProvider<IDeepLinkTarget>(
            linkType, t => t.LinkType);

        if (target == null)
        {
            StatusMessage = $"Unknown link type: {linkType}";
            return;
        }

        // Find the plugin that implements this deep link target
        IAppPlugin? targetPlugin = null;
        foreach (var plugin in _pluginRegistry.ActivePlugins)
        {
            if (plugin is IDeepLinkTarget dt && dt.LinkType == linkType)
            {
                targetPlugin = plugin;
                break;
            }
        }

        if (targetPlugin?.NavigationItem != null)
        {
            await SelectTab(targetPlugin.NavigationItem.Id);
        }

        await target.NavigateToItemAsync(itemId);
    }
}
