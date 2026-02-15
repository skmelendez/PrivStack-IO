using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Item displayed in the workspace switcher list.
/// </summary>
public partial class WorkspaceItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private DateTime _createdAt;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string _storageLabel = string.Empty;
}

/// <summary>
/// Selectable plugin entry shown during workspace creation.
/// </summary>
public partial class PluginPickerItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Icon { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// ViewModel for the workspace switcher overlay (command-palette style).
/// </summary>
public partial class WorkspaceSwitcherViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WorkspaceSwitcherViewModel>();

    private readonly IWorkspaceService _workspaceService;
    private readonly ICloudSyncService _cloudSync;

    public WorkspaceSwitcherViewModel(IWorkspaceService workspaceService, ICloudSyncService cloudSync)
    {
        _workspaceService = workspaceService;
        _cloudSync = cloudSync;
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredWorkspaces))]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _newWorkspaceName = string.Empty;

    /// <summary>
    /// True when the storage location picker is shown after entering workspace name.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingWorkspaceList))]
    private bool _isSelectingStorageLocation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    [NotifyPropertyChangedFor(nameof(IsCustomDirectorySelected))]
    private DataDirectoryType _selectedDirectoryType = DataDirectoryType.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    private string _customDataDirectory = string.Empty;

    public bool IsGoogleDriveAvailable => !string.IsNullOrEmpty(Services.CloudPathResolver.GetGoogleDrivePath());
    public bool IsICloudAvailable => !string.IsNullOrEmpty(Services.CloudPathResolver.GetICloudPath());
    public bool IsCustomDirectorySelected => SelectedDirectoryType == DataDirectoryType.Custom;

    [ObservableProperty]
    private SyncTier _selectedSyncTier = SyncTier.LocalOnly;

    public bool IsCloudSyncAvailable => _cloudSync.IsAuthenticated;

    public string DataDirectoryDisplay => SelectedDirectoryType switch
    {
        DataDirectoryType.Default => DataPaths.BaseDir,
        DataDirectoryType.Custom => string.IsNullOrEmpty(CustomDataDirectory) ? "Select a folder..." : CustomDataDirectory,
        DataDirectoryType.GoogleDrive => Services.CloudPathResolver.GetGoogleDrivePath() ?? "Not available",
        DataDirectoryType.ICloud => Services.CloudPathResolver.GetICloudPath() ?? "Not available",
        _ => DataPaths.BaseDir
    };

    /// <summary>
    /// True when the plugin picker step is shown after creating a workspace.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingWorkspaceList))]
    private bool _isSelectingPlugins;

    /// <summary>
    /// The workspace ID just created, pending plugin selection before switching.
    /// </summary>
    private string? _pendingWorkspaceId;

    /// <summary>
    /// The pending workspace name, saved between name entry and storage location steps.
    /// </summary>
    private string? _pendingWorkspaceName;

    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];

    public ObservableCollection<PluginPickerItem> AvailablePlugins { get; } = [];

    public IEnumerable<WorkspaceItem> FilteredWorkspaces
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchQuery))
                return Workspaces;

            return Workspaces.Where(w =>
                w.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsShowingWorkspaceList => !IsSelectingPlugins && !IsSelectingStorageLocation;

    [RelayCommand]
    private void Open()
    {
        RefreshWorkspaces();
        SearchQuery = string.Empty;
        IsCreating = false;
        IsSelectingStorageLocation = false;
        IsSelectingPlugins = false;
        NewWorkspaceName = string.Empty;
        SelectedDirectoryType = DataDirectoryType.Default;
        SelectedSyncTier = SyncTier.LocalOnly;
        CustomDataDirectory = string.Empty;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        IsCreating = false;
        IsSelectingStorageLocation = false;
        IsSelectingPlugins = false;
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
    private void SwitchWorkspace(string workspaceId)
    {
        IsOpen = false;
        _workspaceService.SwitchWorkspace(workspaceId);
    }

    [RelayCommand]
    private void StartCreating()
    {
        IsCreating = true;
        NewWorkspaceName = string.Empty;
    }

    [RelayCommand]
    private void CancelCreating()
    {
        IsCreating = false;
        NewWorkspaceName = string.Empty;
    }

    [RelayCommand]
    private void CreateWorkspace()
    {
        if (string.IsNullOrWhiteSpace(NewWorkspaceName))
            return;

        _pendingWorkspaceName = NewWorkspaceName.Trim();
        IsCreating = false;
        NewWorkspaceName = string.Empty;

        // Show storage location picker before creating
        SelectedDirectoryType = DataDirectoryType.Default;
        CustomDataDirectory = string.Empty;
        IsSelectingStorageLocation = true;
    }

    [RelayCommand]
    private void SelectStorageLocationType(DataDirectoryType type)
    {
        SelectedDirectoryType = type;
        SelectedSyncTier = SyncTier.LocalOnly;
    }

    [RelayCommand]
    private void SelectCloudSync()
    {
        SelectedSyncTier = SyncTier.PrivStackCloud;
        SelectedDirectoryType = DataDirectoryType.Default;
    }

    [RelayCommand]
    private void ConfirmStorageLocation()
    {
        if (_pendingWorkspaceName == null) return;

        // Build storage location from selections
        StorageLocation? storageLocation = SelectedDirectoryType == DataDirectoryType.Default
            ? null
            : new StorageLocation
            {
                Type = SelectedDirectoryType.ToString(),
                CustomPath = SelectedDirectoryType == DataDirectoryType.Custom ? CustomDataDirectory : null
            };

        var workspace = _workspaceService.CreateWorkspace(_pendingWorkspaceName, storageLocation);
        _pendingWorkspaceId = workspace.Id;
        _pendingWorkspaceName = null;

        // Register with PrivStack Cloud if selected
        if (SelectedSyncTier == SyncTier.PrivStackCloud)
        {
            try
            {
                var cloudWsId = Guid.NewGuid().ToString();
                _cloudSync.RegisterWorkspace(cloudWsId, workspace.Name);
                workspace = workspace with
                {
                    CloudWorkspaceId = cloudWsId,
                    SyncTier = SyncTier.PrivStackCloud,
                };
                _workspaceService.UpdateWorkspace(workspace);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to register workspace with cloud");
            }
        }

        // Initialize whitelist config with core-only defaults before showing picker
        var settingsService = App.Services.GetRequiredService<IAppSettingsService>();
        settingsService.InitializeWorkspacePluginConfig(workspace.Id);

        IsSelectingStorageLocation = false;
        PopulateAvailablePlugins();
        IsSelectingPlugins = true;
    }

    [RelayCommand]
    private void CancelStorageLocation()
    {
        IsSelectingStorageLocation = false;
        _pendingWorkspaceName = null;
    }

    [RelayCommand]
    private void ConfirmPluginSelection()
    {
        if (_pendingWorkspaceId == null) return;

        var selectedIds = AvailablePlugins
            .Where(p => p.IsSelected)
            .Select(p => p.Id);

        var settingsService = App.Services.GetRequiredService<IAppSettingsService>();
        settingsService.InitializeWorkspacePluginConfig(_pendingWorkspaceId, selectedIds);

        FinishWorkspaceCreation();
    }

    [RelayCommand]
    private void SkipPluginSelection()
    {
        // Core-only config is already set — just switch
        FinishWorkspaceCreation();
    }

    [RelayCommand]
    private void ConfirmDelete(WorkspaceItem? item)
    {
        if (item == null || item.IsActive) return;
        item.IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete(WorkspaceItem? item)
    {
        if (item != null)
            item.IsConfirmingDelete = false;
    }

    [RelayCommand]
    private void DeleteWorkspace(WorkspaceItem? item)
    {
        if (item == null || item.IsActive) return;

        _workspaceService.DeleteWorkspace(item.Id);
        RefreshWorkspaces();
    }

    private void FinishWorkspaceCreation()
    {
        if (_pendingWorkspaceId == null) return;

        var wsId = _pendingWorkspaceId;
        _pendingWorkspaceId = null;
        IsSelectingPlugins = false;
        IsOpen = false;

        _workspaceService.SwitchWorkspace(wsId);

        // Auto-start cloud sync if workspace was created with PrivStack Cloud
        var workspace = _workspaceService.GetActiveWorkspace();
        if (workspace?.SyncTier == SyncTier.PrivStackCloud && workspace.CloudWorkspaceId != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Setup keypair if needed
                    if (!_cloudSync.HasKeypair)
                    {
                        var passwordCache = App.Services.GetRequiredService<IMasterPasswordCache>();
                        var vaultPassword = passwordCache.Get();
                        if (!string.IsNullOrEmpty(vaultPassword))
                            _cloudSync.SetupUnifiedRecovery(vaultPassword);
                    }

                    _cloudSync.StartSync(workspace.CloudWorkspaceId);
                    var count = _cloudSync.PushAllEntities();
                    Log.Information("Cloud sync auto-started for new workspace {Id} (pushed {Count} entities)",
                        workspace.Id, count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to auto-start cloud sync for new workspace");
                }
            });
        }
    }

    private void PopulateAvailablePlugins()
    {
        AvailablePlugins.Clear();

        var registry = App.Services.GetRequiredService<IPluginRegistry>();
        foreach (var plugin in registry.Plugins)
        {
            // Skip core/hard-locked plugins — they're always enabled
            if (plugin.Metadata.IsHardLocked) continue;
            if (!plugin.Metadata.CanDisable) continue;

            AvailablePlugins.Add(new PluginPickerItem
            {
                Id = plugin.Metadata.Id,
                Name = plugin.Metadata.Name,
                Description = plugin.Metadata.Description,
                Icon = plugin.Metadata.Icon,
                IsSelected = false,
            });
        }
    }

    private void RefreshWorkspaces()
    {
        Workspaces.Clear();
        var active = _workspaceService.GetActiveWorkspace();

        foreach (var ws in _workspaceService.ListWorkspaces())
        {
            Workspaces.Add(new WorkspaceItem
            {
                Id = ws.Id,
                Name = ws.Name,
                IsActive = ws.Id == active?.Id,
                CreatedAt = ws.CreatedAt,
                StorageLabel = GetStorageLabel(ws.StorageLocation, ws.SyncTier)
            });
        }

        OnPropertyChanged(nameof(FilteredWorkspaces));
    }

    private static string GetStorageLabel(StorageLocation? location, SyncTier syncTier = SyncTier.LocalOnly)
    {
        if (syncTier == SyncTier.PrivStackCloud) return "Cloud";
        if (location == null || location.Type == "Default") return "Local";
        return location.Type switch
        {
            "GoogleDrive" => "Google Drive",
            "ICloud" => "iCloud",
            "Custom" => "Custom",
            _ => "Local"
        };
    }
}
