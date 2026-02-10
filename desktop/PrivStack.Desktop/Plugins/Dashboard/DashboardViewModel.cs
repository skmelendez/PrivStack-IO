using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models.PluginRegistry;
using PrivStack.Desktop.Plugins.Dashboard.Models;
using PrivStack.Desktop.Plugins.Dashboard.Services;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Plugins.Dashboard;

public enum DashboardTab
{
    Overview,
    Data
}

/// <summary>
/// ViewModel for the Dashboard plugin — manages system metrics, data metrics,
/// the official plugin catalog, and install/update/uninstall flows.
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private static readonly ILogger _log = Serilog.Log.ForContext<DashboardViewModel>();

    private readonly IPluginInstallService _installService;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly SystemMetricsService _metricsService;
    private readonly IPrivStackSdk _sdk;
    private List<OfficialPluginInfo> _serverPlugins = [];

    internal DashboardViewModel(
        IPluginInstallService installService,
        IPluginRegistry pluginRegistry,
        SystemMetricsService metricsService,
        IPrivStackSdk sdk)
    {
        _installService = installService;
        _pluginRegistry = pluginRegistry;
        _metricsService = metricsService;
        _sdk = sdk;
    }

    // --- Tab State ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewTab))]
    [NotifyPropertyChangedFor(nameof(IsDataTab))]
    private DashboardTab _activeTab = DashboardTab.Overview;

    public bool IsOverviewTab => ActiveTab == DashboardTab.Overview;
    public bool IsDataTab => ActiveTab == DashboardTab.Data;

    // --- Plugin Marketplace ---

    public ObservableCollection<DashboardPluginItem> AllPlugins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredPlugins))]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredPlugins))]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isOffline;

    /// <summary>
    /// True after the first successful refresh. Prevents automatic network calls on every tab switch.
    /// </summary>
    public bool HasLoadedOnce { get; private set; }

    [ObservableProperty]
    private string? _statusMessage;

    // --- System Metrics (Overview tab) ---

    [ObservableProperty]
    private string _appShellSize = "—";

    [ObservableProperty]
    private string _pluginBinariesTotal = "—";

    [ObservableProperty]
    private string _dataStorageTotal = "—";

    [ObservableProperty]
    private string _memoryUsage = "—";

    [ObservableProperty]
    private string _memoryGcHeap = "—";

    [ObservableProperty]
    private bool _isStorageExpanded;

    public ObservableCollection<PluginSizeInfo> PluginSizes { get; } = [];

    // --- Data Metrics (Data tab) ---

    [ObservableProperty]
    private string _totalDatabaseSize = "—";

    [ObservableProperty]
    private string _totalFilesSize = "—";

    [ObservableProperty]
    private string _totalVaultSize = "—";

    [ObservableProperty]
    private string _totalStorageSize = "—";

    [ObservableProperty]
    private bool _isRunningMaintenance;

    public ObservableCollection<PluginDataInfo> PluginDataItems { get; } = [];

    // --- Computed ---

    public int InstalledCount => AllPlugins.Count(p => p.IsInstalled);
    public int AvailableCount => AllPlugins.Count(p => !p.IsInstalled);
    public int UpdateCount => AllPlugins.Count(p => p.HasUpdate);

    public string[] Categories { get; } =
    [
        "All", "Productivity", "Utility", "Finance", "Communication", "Developer"
    ];

    public IEnumerable<DashboardPluginItem> FilteredPlugins
    {
        get
        {
            var query = AllPlugins.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                var search = SearchQuery.Trim();
                query = query.Where(p =>
                    p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (p.Tagline?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (SelectedCategory != "All")
            {
                query = query.Where(p =>
                    p.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            return query
                .OrderByDescending(p => p.IsInstalled)
                .ThenByDescending(p => p.HasUpdate)
                .ThenBy(p => p.Name);
        }
    }

    // =========================================================================
    // Tab switching
    // =========================================================================

    [RelayCommand]
    private async Task SwitchTabAsync(DashboardTab tab)
    {
        if (ActiveTab == tab) return;
        ActiveTab = tab;

        if (tab == DashboardTab.Data)
        {
            await LoadDataMetricsAsync();
        }
    }

    // =========================================================================
    // Refresh (Overview tab — marketplace + system metrics)
    // =========================================================================

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = null;

        try
        {
            var online = await _installService.IsOnlineAsync();
            IsOffline = !online;

            if (online)
            {
                _serverPlugins = (await _installService.GetAvailablePluginsAsync()).ToList();
            }

            var installedVersions = _installService.GetInstalledVersions();

            AllPlugins.Clear();

            foreach (var sp in _serverPlugins)
            {
                var installed = installedVersions.TryGetValue(sp.PluginId, out var localVersion);
                var hasUpdate = installed
                    && Version.TryParse(sp.Version, out var serverVer)
                    && localVersion < serverVer;

                AllPlugins.Add(new DashboardPluginItem
                {
                    Id = sp.PluginId,
                    Name = sp.Name,
                    Description = sp.Description,
                    Tagline = sp.Tagline,
                    Author = sp.Author,
                    LatestVersion = sp.Version,
                    InstalledVersion = localVersion?.ToString(),
                    Category = sp.Category,
                    Icon = sp.Icon ?? "Package",
                    IsInstalled = installed,
                    HasUpdate = hasUpdate,
                    TrustTier = "Official",
                    PackageSizeBytes = sp.PackageSizeBytes
                });
            }

            foreach (var (pluginId, version) in installedVersions)
            {
                if (_serverPlugins.Any(s => s.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                AllPlugins.Add(new DashboardPluginItem
                {
                    Id = pluginId,
                    Name = pluginId.Replace("privstack.", "").Replace(".", " "),
                    Description = "Installed locally",
                    InstalledVersion = version.ToString(),
                    LatestVersion = version.ToString(),
                    IsInstalled = true,
                    TrustTier = "Official"
                });
            }

            NotifyCountsChanged();

            if (IsOffline && AllPlugins.Count == 0)
            {
                StatusMessage = "No connection to plugin registry. Install plugins when you're back online.";
            }

            await LoadSystemMetricsAsync();

            HasLoadedOnce = true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to refresh Dashboard plugin list");
            StatusMessage = $"Failed to load plugins: {ex.Message}";
            IsOffline = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // =========================================================================
    // System metrics (Overview tab)
    // =========================================================================

    [RelayCommand]
    private async Task LoadSystemMetricsAsync()
    {
        try
        {
            var shellSize = await _metricsService.GetAppShellSizeBytesAsync();
            AppShellSize = SystemMetricsHelper.FormatBytes(shellSize);

            var pluginSizes = await _metricsService.GetPluginBinarySizesAsync(_pluginRegistry);
            PluginSizes.Clear();
            long totalBinaries = 0;
            foreach (var ps in pluginSizes)
            {
                PluginSizes.Add(ps);
                totalBinaries += ps.SizeBytes;
            }
            PluginBinariesTotal = SystemMetricsHelper.FormatBytes(totalBinaries);

            var sizeLookup = pluginSizes.ToDictionary(
                p => p.PluginId,
                p => p.SizeBytes,
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in AllPlugins)
            {
                item.DiskSizeBytes = sizeLookup.TryGetValue(item.Id, out var size) ? size : null;
            }

            var (workingSet, gcHeap) = _metricsService.GetMemoryMetrics();
            MemoryUsage = SystemMetricsHelper.FormatBytes(workingSet);
            MemoryGcHeap = SystemMetricsHelper.FormatBytes(gcHeap);

            // Also refresh data storage totals for the overview card
            var dataResult = await _metricsService.GetDataMetricsAsync(_pluginRegistry);
            DataStorageTotal = SystemMetricsHelper.FormatBytes(dataResult.TotalStorageBytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load system metrics");
        }
    }

    [RelayCommand]
    private void ToggleStorageExpanded()
    {
        IsStorageExpanded = !IsStorageExpanded;
    }

    // =========================================================================
    // Data metrics (Data tab)
    // =========================================================================

    [RelayCommand]
    private async Task LoadDataMetricsAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = null;

            var dataResult = await _metricsService.GetDataMetricsAsync(_pluginRegistry);

            PluginDataItems.Clear();
            foreach (var item in dataResult.PluginDataItems)
            {
                PluginDataItems.Add(item);
            }

            TotalDatabaseSize = SystemMetricsHelper.FormatBytes(dataResult.TotalDatabaseBytes);
            TotalFilesSize = SystemMetricsHelper.FormatBytes(dataResult.TotalFilesBytes);
            TotalVaultSize = SystemMetricsHelper.FormatBytes(dataResult.TotalVaultBytes);
            TotalStorageSize = SystemMetricsHelper.FormatBytes(dataResult.TotalStorageBytes);
            DataStorageTotal = TotalStorageSize;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load data metrics");
            StatusMessage = $"Failed to load data metrics: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void TogglePluginExpanded(PluginDataInfo? item)
    {
        if (item == null) return;
        var expanding = !item.IsExpanded;
        // Accordion: collapse all others
        foreach (var plugin in PluginDataItems)
            plugin.IsExpanded = false;
        item.IsExpanded = expanding;
    }

    [RelayCommand]
    private async Task NavigateToTableParentAsync(DataTableInfo? table)
    {
        if (table?.ParentId == null || table.PluginId == null)
            return;

        try
        {
            var mainVm = _pluginRegistry.GetMainViewModel();
            if (mainVm != null)
            {
                await mainVm.NavigateToPluginItemAsync(table.PluginId, table.ParentId);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Navigation failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunDatabaseMaintenanceAsync()
    {
        try
        {
            IsRunningMaintenance = true;
            StatusMessage = null;
            await _sdk.RunDatabaseMaintenance();
            await LoadDataMetricsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Maintenance failed: {ex.Message}";
        }
        finally
        {
            IsRunningMaintenance = false;
        }
    }

    // =========================================================================
    // Plugin install/update/uninstall/reload
    // =========================================================================

    [ObservableProperty]
    private bool _isUpdatingAll;

    [RelayCommand]
    private async Task UpdateAllPluginsAsync()
    {
        if (IsUpdatingAll) return;

        var updatable = AllPlugins.Where(p => p.HasUpdate && !p.IsInstalling).ToList();
        if (updatable.Count == 0) return;

        IsUpdatingAll = true;
        try
        {
            foreach (var item in updatable)
            {
                await UpdatePluginAsync(item);
            }
        }
        finally
        {
            IsUpdatingAll = false;
        }
    }

    [RelayCommand]
    private async Task InstallPluginAsync(DashboardPluginItem item)
    {
        if (item.IsInstalling) return;

        var serverPlugin = _serverPlugins.FirstOrDefault(s =>
            s.PluginId.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        if (serverPlugin == null) return;

        item.IsInstalling = true;
        item.InstallProgress = 0;

        try
        {
            var progress = new Progress<double>(p => item.InstallProgress = p);
            var result = await _installService.InstallPluginAsync(serverPlugin, progress);

            if (result.Success)
            {
                item.IsInstalled = true;
                item.InstalledVersion = serverPlugin.Version;
                item.HasUpdate = false;
                NotifyCountsChanged();
                await EvictPluginCacheAsync(item.Id);
            }
            else
            {
                StatusMessage = $"Install failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to install plugin {PluginId}", item.Id);
            StatusMessage = $"Install failed: {ex.Message}";
        }
        finally
        {
            item.IsInstalling = false;
            item.InstallProgress = 0;
        }
    }

    [RelayCommand]
    private async Task UpdatePluginAsync(DashboardPluginItem item)
    {
        if (item.IsInstalling) return;

        var serverPlugin = _serverPlugins.FirstOrDefault(s =>
            s.PluginId.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        if (serverPlugin == null) return;

        item.IsInstalling = true;
        item.InstallProgress = 0;

        try
        {
            var progress = new Progress<double>(p => item.InstallProgress = p);
            var result = await _installService.UpdatePluginAsync(item.Id, serverPlugin, progress);

            if (result.Success)
            {
                item.InstalledVersion = serverPlugin.Version;
                item.HasUpdate = false;
                NotifyCountsChanged();
                await EvictPluginCacheAsync(item.Id);
            }
            else
            {
                StatusMessage = $"Update failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to update plugin {PluginId}", item.Id);
            StatusMessage = $"Update failed: {ex.Message}";
        }
        finally
        {
            item.IsInstalling = false;
            item.InstallProgress = 0;
        }
    }

    [RelayCommand]
    private async Task UninstallPluginAsync(DashboardPluginItem item)
    {
        try
        {
            var success = await _installService.UninstallPluginAsync(item.Id);
            if (success)
            {
                item.IsInstalled = false;
                item.InstalledVersion = null;
                item.HasUpdate = false;
                NotifyCountsChanged();
            }
            else
            {
                StatusMessage = "Failed to uninstall plugin.";
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to uninstall plugin {PluginId}", item.Id);
            StatusMessage = $"Uninstall failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ReloadPluginAsync(DashboardPluginItem item)
    {
        try
        {
            var plugin = _pluginRegistry.GetPlugin(item.Id);
            var navId = plugin?.NavigationItem?.Id;
            if (navId == null) return;

            var pluginDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".privstack", "plugins", item.Id);

            _pluginRegistry.UnloadPlugin(item.Id);

            if (Directory.Exists(pluginDir))
            {
                await _pluginRegistry.LoadPluginFromDirectoryAsync(pluginDir);
            }

            var mainVm = _pluginRegistry.GetMainViewModel();
            if (mainVm != null)
            {
                await mainVm.ReloadPluginAsync(navId);
            }

            _log.Information("Reloaded plugin {PluginId}", item.Id);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to reload plugin {PluginId}", item.Id);
            StatusMessage = $"Reload failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectCategory(string category)
    {
        SelectedCategory = category;
    }

    private async Task EvictPluginCacheAsync(string pluginId)
    {
        try
        {
            var mainVm = _pluginRegistry.GetMainViewModel();
            var plugin = _pluginRegistry.GetPlugin(pluginId);
            var navId = plugin?.NavigationItem?.Id;
            if (navId != null && mainVm != null)
            {
                await mainVm.ReloadPluginAsync(navId);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to evict cache for plugin {PluginId}", pluginId);
        }
    }

    private void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(FilteredPlugins));
    }
}
