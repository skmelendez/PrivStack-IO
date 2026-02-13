using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Drives the storage-state pill in the status bar.
/// Reflects the actual storage/sync configuration as a colored label.
/// </summary>
public partial class StorageStatePillViewModel : ViewModelBase
{
    private readonly IAppSettingsService _appSettings;
    private readonly SyncViewModel _syncVM;
    private readonly IFileEventSyncService _fileEventSync;

    [ObservableProperty] private string _pillText = "Local";
    [ObservableProperty] private IBrush _pillColor = Brushes.Gray;

    public StorageStatePillViewModel(
        IAppSettingsService appSettings,
        SyncViewModel syncVM,
        IFileEventSyncService fileEventSync)
    {
        _appSettings = appSettings;
        _syncVM = syncVM;
        _fileEventSync = fileEventSync;

        _syncVM.PropertyChanged += OnSyncPropertyChanged;
        Refresh();
    }

    private void OnSyncPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SyncViewModel.IsSyncRunning)
                           or nameof(SyncViewModel.StatusMessage))
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        var settings = _appSettings.Settings;

        // Read storage location from active workspace (per-workspace)
        var workspaceService = App.Services.GetService<IWorkspaceService>();
        var activeWorkspace = workspaceService?.GetActiveWorkspace();
        var storageLocation = activeWorkspace?.StorageLocation;
        var dirType = storageLocation?.Type ?? "Default";
        var fileSyncActive = _fileEventSync.IsActive;

        // Cloud provider with active file sync
        if (dirType is "GoogleDrive" or "ICloud" && fileSyncActive)
        {
            PillText = "File Sync";
            PillColor = ResolveBrush("ThemeSuccessBrush");
            return;
        }

        // Custom path on a network mount with active file sync
        if (dirType == "Custom" && fileSyncActive)
        {
            PillText = NetworkPathDetector.IsNetworkPath(storageLocation?.CustomPath)
                ? "File Sync (NAS)"
                : "File Sync";
            PillColor = ResolveBrush("ThemeSuccessBrush");
            return;
        }

        // Non-default storage configured but file sync not active yet
        if (dirType is "GoogleDrive" or "ICloud" or "Custom" && !fileSyncActive)
        {
            PillText = "File Sync (Starting)";
            PillColor = ResolveBrush("ThemeWarningBrush");
            return;
        }

        // Local storage — state depends on P2P sync
        var syncRunning = _syncVM.IsSyncRunning;
        var syncAutoStart = settings.SyncAutoStart;

        if (syncRunning)
        {
            PillText = "P2P Sync";
            var msg = _syncVM.StatusMessage ?? "";
            PillColor = msg.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? ResolveBrush("ThemeDangerBrush")
                : ResolveBrush("ThemeSuccessBrush");
            return;
        }

        if (syncAutoStart)
        {
            PillText = "P2P Sync";
            PillColor = ResolveBrush("ThemeWarningBrush");
            return;
        }

        PillText = "Local";
        PillColor = ResolveBrush("ThemeSuccessBrush");
    }

    private static IBrush ResolveBrush(string key)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var res) == true
            && res is IBrush brush)
        {
            return brush;
        }
        return Brushes.Gray;
    }
}
