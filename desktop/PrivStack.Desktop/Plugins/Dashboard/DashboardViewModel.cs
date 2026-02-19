using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly EntityMetadataService _entityMetadataService;
    private readonly LinkProviderCacheService _linkProviderCache;
    private readonly IWorkspaceService _workspaceService;
    private readonly Native.IPrivStackRuntime _runtime;
    private List<OfficialPluginInfo> _serverPlugins = [];

    internal DashboardViewModel(
        IPluginInstallService installService,
        IPluginRegistry pluginRegistry,
        SystemMetricsService metricsService,
        IPrivStackSdk sdk,
        EntityMetadataService entityMetadataService,
        LinkProviderCacheService linkProviderCache,
        IWorkspaceService workspaceService,
        Native.IPrivStackRuntime runtime)
    {
        _installService = installService;
        _pluginRegistry = pluginRegistry;
        _metricsService = metricsService;
        _sdk = sdk;
        _entityMetadataService = entityMetadataService;
        _linkProviderCache = linkProviderCache;
        _workspaceService = workspaceService;
        _runtime = runtime;
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
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
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
    private string _totalDatabaseEstimate = string.Empty;

    [ObservableProperty]
    private string _totalFilesEstimate = string.Empty;

    [ObservableProperty]
    private string _totalVaultEstimate = string.Empty;

    [ObservableProperty]
    private string _totalStorageEstimate = string.Empty;

    [ObservableProperty]
    private bool _isRunningMaintenance;

    [ObservableProperty]
    private bool _isValidatingMetadata;

    [ObservableProperty]
    private string? _validationStatus;

    [ObservableProperty]
    private bool _isLoadingDiagnostics;

    public ObservableCollection<DbFileDiagnostic> DiagnosticsFiles { get; } = [];

    public ObservableCollection<PluginDataInfo> PluginDataItems { get; } = [];

    // --- Workspace Plugins ---

    public ObservableCollection<WorkspacePluginEntry> WorkspacePlugins { get; } = [];

    [ObservableProperty]
    private bool _isWorkspacePluginsExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInactivePlugins))]
    private int _activeWorkspacePluginCount;

    public bool HasInactivePlugins => WorkspacePlugins.Count > 0 &&
        WorkspacePlugins.Any(p => !p.IsActivated);

    // --- Computed ---

    public int InstalledCount => AllPlugins.Count(p => p.IsInstalled);
    public int AvailableCount => AllPlugins.Count(p => !p.IsInstalled);
    public int UpdateCount => AllPlugins.Count(p => p.HasUpdate);

    public string SubtitleText
    {
        get
        {
            var parts = new List<string>(4);
            parts.Add($"{InstalledCount} installed");
            parts.Add($"{AvailableCount} available");
            if (UpdateCount > 0)
                parts.Add($"{UpdateCount} update{(UpdateCount == 1 ? "" : "s")}");
            parts.Add($"{MemoryUsage} RAM");
            return string.Join(" \u00b7 ", parts);
        }
    }

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
                    PackageSizeBytes = sp.PackageSizeBytes,
                    ReleaseStage = sp.ReleaseStage ?? "release"
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
            LoadWorkspacePlugins();

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
            var dataResult = await _metricsService.GetDataMetricsAsync(_pluginRegistry, _workspaceService);
            DataStorageTotal = SystemMetricsHelper.FormatBytes(dataResult.TotalStorageBytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load system metrics");
        }
    }

    // =========================================================================
    // Workspace plugins
    // =========================================================================

    [RelayCommand]
    private void ToggleWorkspacePluginsExpanded()
    {
        IsWorkspacePluginsExpanded = !IsWorkspacePluginsExpanded;
    }

    [RelayCommand]
    private void LoadWorkspacePlugins()
    {
        WorkspacePlugins.Clear();

        foreach (var plugin in _pluginRegistry.Plugins)
        {
            if (plugin.Metadata.IsHardLocked) continue;
            if (!plugin.Metadata.CanDisable) continue;

            WorkspacePlugins.Add(new WorkspacePluginEntry
            {
                Id = plugin.Metadata.Id,
                Name = plugin.Metadata.Name,
                Description = plugin.Metadata.Description,
                Icon = plugin.Metadata.Icon,
                Category = plugin.Metadata.Category,
                ReleaseStage = plugin.Metadata.ReleaseStage,
                IsActivated = _pluginRegistry.IsPluginEnabled(plugin.Metadata.Id),
            });
        }

        ActiveWorkspacePluginCount = WorkspacePlugins.Count(p => p.IsActivated);
        OnPropertyChanged(nameof(HasInactivePlugins));
    }

    [RelayCommand]
    private void ToggleWorkspacePlugin(WorkspacePluginEntry? entry)
    {
        if (entry == null) return;

        if (entry.IsActivated)
        {
            _pluginRegistry.DisablePlugin(entry.Id);
            entry.IsActivated = false;
        }
        else
        {
            _pluginRegistry.EnablePlugin(entry.Id);
            entry.IsActivated = true;
        }

        ActiveWorkspacePluginCount = WorkspacePlugins.Count(p => p.IsActivated);
        OnPropertyChanged(nameof(HasInactivePlugins));
    }

    [RelayCommand]
    private void ToggleStorageExpanded()
    {
        IsStorageExpanded = !IsStorageExpanded;
    }

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(bundledDir))
        {
            Process.Start(new ProcessStartInfo(bundledDir) { UseShellExecute = true });
            return;
        }

        var devDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins"));
        if (Directory.Exists(devDir))
        {
            Process.Start(new ProcessStartInfo(devDir) { UseShellExecute = true });
            return;
        }

        var userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".privstack", "plugins");
        if (Directory.Exists(userDir))
            Process.Start(new ProcessStartInfo(userDir) { UseShellExecute = true });
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

            var dataResult = await _metricsService.GetDataMetricsAsync(_pluginRegistry, _workspaceService);

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

            // Set estimate subtitles (only shown when different from actual)
            TotalDatabaseEstimate = dataResult.EstimatedDatabaseBytes > 0
                && dataResult.EstimatedDatabaseBytes != dataResult.TotalDatabaseBytes
                ? $"(~{SystemMetricsHelper.FormatBytes(dataResult.EstimatedDatabaseBytes)} uncompressed)"
                : string.Empty;
            TotalFilesEstimate = string.Empty; // Files are already measured from disk
            TotalVaultEstimate = dataResult.EstimatedVaultBytes > 0
                && dataResult.EstimatedVaultBytes != dataResult.TotalVaultBytes
                ? $"(~{SystemMetricsHelper.FormatBytes(dataResult.EstimatedVaultBytes)} uncompressed)"
                : string.Empty;
            TotalStorageEstimate = dataResult.EstimatedStorageBytes > 0
                && dataResult.EstimatedStorageBytes != dataResult.TotalStorageBytes
                ? $"(~{SystemMetricsHelper.FormatBytes(dataResult.EstimatedStorageBytes)} uncompressed)"
                : string.Empty;
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

            var dbDir = Path.GetDirectoryName(_workspaceService.GetActiveDataPath());
            var entityDb = dbDir != null ? Path.Combine(dbDir, "data.entities.duckdb") : null;
            var sizeBefore = entityDb != null && File.Exists(entityDb) ? new FileInfo(entityDb).Length : 0;

            // 1. Clean orphaned auxiliary rows + transient data + CHECKPOINT
            StatusMessage = "Cleaning orphaned data...";
            await _sdk.RunDatabaseMaintenance();

            // 2. Detect and delete orphan entities (types not matching any registered plugin)
            StatusMessage = "Checking for orphan entities...";
            var validTypesJson = BuildValidEntityTypesJson();
            _log.Information("Valid entity types: {Json}", validTypesJson);

            var orphansJson = await Task.Run(() => _sdk.FindOrphanEntities(validTypesJson));
            _log.Information("Orphan entities found: {Json}", orphansJson);

            int orphansDeleted = 0;
            var orphanDoc = System.Text.Json.JsonDocument.Parse(orphansJson);
            if (orphanDoc.RootElement.GetArrayLength() > 0)
            {
                _log.Information("Deleting {Count} orphan entity type(s)", orphanDoc.RootElement.GetArrayLength());
                StatusMessage = "Deleting orphan entities...";
                var deleteResultJson = await Task.Run(() => _sdk.DeleteOrphanEntities(validTypesJson));
                var deleteDoc = System.Text.Json.JsonDocument.Parse(deleteResultJson);
                if (deleteDoc.RootElement.TryGetProperty("deleted", out var del))
                    orphansDeleted = del.GetInt32();
                _log.Information("Deleted {Count} orphan entities", orphansDeleted);
            }

            // 3. Report results
            var sizeAfter = entityDb != null && File.Exists(entityDb) ? new FileInfo(entityDb).Length : 0;
            var parts = new List<string>();
            if (orphansDeleted > 0)
                parts.Add($"removed {orphansDeleted} orphan entit{(orphansDeleted == 1 ? "y" : "ies")}");
            parts.Add("cleaned auxiliary tables");
            parts.Add($"checkpoint complete ({FormatBytes(sizeBefore)} → {FormatBytes(sizeAfter)})");
            var msg = string.Join(", ", parts);
            StatusMessage = msg.Length > 0 ? char.ToUpper(msg[0]) + msg[1..] : msg;

            await LoadDataMetricsAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Database maintenance failed");
            StatusMessage = $"Maintenance failed: {ex.Message}";
        }
        finally
        {
            IsRunningMaintenance = false;
        }
    }

    /// <summary>
    /// Builds a JSON array of all valid (plugin_id, entity_type) pairs from registered plugins
    /// plus system-level entity types. Used to detect orphan entities.
    /// </summary>
    private string BuildValidEntityTypesJson()
    {
        var types = new List<object>();

        // System-level entity types (owned by the shell, not any plugin)
        foreach (var sysType in new[] { "entity_metadata", "property_template", "property_definition", "property_group" })
        {
            types.Add(new { plugin_id = "privstack.system", entity_type = sysType });
        }

        // All registered plugin entity types
        foreach (var plugin in _pluginRegistry.Plugins)
        {
            foreach (var schema in plugin.EntitySchemas)
            {
                types.Add(new { plugin_id = plugin.Metadata.Id, entity_type = schema.EntityType });
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(types);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
    };

    [RelayCommand]
    private async Task ValidateMetadataAsync()
    {
        try
        {
            IsValidatingMetadata = true;
            ValidationStatus = null;

            var result = await _entityMetadataService.ValidateAndCleanOrphansAsync(_linkProviderCache);

            ValidationStatus = result.OrphansRemoved > 0
                ? $"Cleaned {result.OrphansRemoved} orphaned metadata record{(result.OrphansRemoved == 1 ? "" : "s")} (scanned {result.TotalScanned})"
                : $"No orphans found — all {result.TotalScanned} metadata records are valid";

            if (result.OrphansRemoved > 0)
                await LoadDataMetricsAsync();
        }
        catch (Exception ex)
        {
            ValidationStatus = $"Validation failed: {ex.Message}";
        }
        finally
        {
            IsValidatingMetadata = false;
        }
    }

    [RelayCommand]
    private async Task RunDatabaseDiagnosticsAsync()
    {
        try
        {
            IsLoadingDiagnostics = true;
            DiagnosticsFiles.Clear();

            var json = await Task.Run(() => _sdk.GetDatabaseDiagnostics());
            _log.Information("Raw diagnostics JSON: {Json}", json);

            var doc = System.Text.Json.JsonDocument.Parse(json);
            var dbDir = Path.GetDirectoryName(_workspaceService.GetActiveDataPath());

            foreach (var dbEntry in doc.RootElement.EnumerateObject())
            {
                var dbLabel = dbEntry.Name;
                var dbVal = dbEntry.Value;

                // Get actual file size from disk
                long fileSize = 0;
                var fileName = $"data.{dbLabel}.duckdb";
                if (dbVal.TryGetProperty("file_size", out var fs))
                    fileSize = fs.GetInt64();
                else if (dbDir != null)
                {
                    var diskPath = Path.Combine(dbDir, fileName);
                    if (File.Exists(diskPath))
                        fileSize = new FileInfo(diskPath).Length;
                }

                // Parse tables
                long totalRows = 0;
                var tableItems = new List<DbTableDiagnostic>();
                if (dbVal.TryGetProperty("tables", out var tables))
                {
                    foreach (var table in tables.EnumerateArray())
                    {
                        var name = table.GetProperty("table").GetString() ?? "unknown";
                        var rowCount = table.GetProperty("row_count").GetInt64();
                        var estimatedSize = table.GetProperty("estimated_size").GetInt64();
                        var columnCount = table.GetProperty("column_count").GetInt64();

                        tableItems.Add(new DbTableDiagnostic(
                            dbLabel, name, rowCount, estimatedSize, columnCount));
                        totalRows += rowCount;
                    }
                }

                var fileDiag = new DbFileDiagnostic(dbLabel, fileName, fileSize, totalRows);

                // Parse block info from databases array (entity store)
                if (dbVal.TryGetProperty("databases", out var dbs) && dbs.GetArrayLength() > 0)
                {
                    var first = dbs[0];
                    fileDiag.TotalBlocks = first.TryGetProperty("total_blocks", out var tb) ? tb.GetInt64() : 0;
                    fileDiag.UsedBlocks = first.TryGetProperty("used_blocks", out var ub) ? ub.GetInt64() : 0;
                    fileDiag.FreeBlocks = first.TryGetProperty("free_blocks", out var fb) ? fb.GetInt64() : 0;
                }

                foreach (var t in tableItems)
                    fileDiag.Tables.Add(t);

                DiagnosticsFiles.Add(fileDiag);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Database diagnostics failed");
            ValidationStatus = $"Diagnostics failed: {ex.Message}";
        }
        finally
        {
            IsLoadingDiagnostics = false;
        }
    }

    [RelayCommand]
    private void ToggleDiagnosticsExpanded(DbFileDiagnostic item)
    {
        item.IsExpanded = !item.IsExpanded;
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
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(FilteredPlugins));
    }
}
