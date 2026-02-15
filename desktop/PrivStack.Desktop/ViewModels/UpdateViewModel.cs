using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for managing application updates via the PrivStack registry API.
/// Drives both the status bar indicator and the centered update modal.
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _updateService;
    private readonly IDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppSettingsService _appSettings;
    private System.Timers.Timer? _autoCheckTimer;
    private System.Timers.Timer? _upToDateDismissTimer;
    private bool _startupCheckComplete;

    [ObservableProperty]
    private string _currentVersion = "0.0.0";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateVersion = string.Empty;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _updateReady;

    [ObservableProperty]
    private bool _needsAuthentication;

    // ── Status bar properties ──────────────────────────────────────────

    [ObservableProperty]
    private string _statusBarText = "Check for updates";

    [ObservableProperty]
    private bool _isStatusBarHighlighted;

    [ObservableProperty]
    private bool _isUpdateModalOpen;

    [ObservableProperty]
    private string? _releaseNotes;

    public UpdateViewModel(
        IUpdateService updateService,
        IDialogService dialogService,
        IUiDispatcher dispatcher,
        IAppSettingsService appSettings)
    {
        _updateService = updateService;
        _dialogService = dialogService;
        _dispatcher = dispatcher;
        _appSettings = appSettings;

        CurrentVersion = _updateService.CurrentVersion;

        _updateService.UpdateFound += OnUpdateFound;
        _updateService.UpdateError += OnUpdateError;
    }

    private void OnUpdateFound(object? sender, LatestReleaseInfo release)
    {
        _dispatcher.Post(() =>
        {
            UpdateAvailable = true;
            UpdateVersion = release.Version;
            ReleaseNotes = release.ReleaseNotes;
            StatusMessage = $"Version {release.Version} available";
            StatusBarText = "Update available";
            IsStatusBarHighlighted = true;
            CancelDismissTimer();
        });
    }

    private void OnUpdateError(object? sender, Exception ex)
    {
        _dispatcher.Post(() =>
        {
            IsChecking = false;
            IsDownloading = false;
            StatusMessage = $"Update error: {ex.Message}";
            StatusBarText = "Check for updates";
            IsStatusBarHighlighted = false;
        });
    }

    /// <summary>
    /// Status bar click: when no update is available triggers a check,
    /// when update is available opens the modal.
    /// </summary>
    [RelayCommand]
    private async Task StatusBarClickAsync()
    {
        if (UpdateAvailable)
        {
            OpenModal();
            return;
        }

        if (IsChecking) return;

        await CheckForUpdatesManualAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsChecking) return;

        IsChecking = true;
        StatusMessage = "Checking for updates...";
        StatusBarText = "Checking...";

        try
        {
            var release = await _updateService.CheckForUpdatesAsync();

            if (release == null)
            {
                StatusMessage = "You're up to date";
                UpdateAvailable = false;

                // On startup auto-check: stay as "Check for updates" silently
                if (_startupCheckComplete)
                {
                    StatusBarText = $"On latest version (v{CurrentVersion})";
                    IsStatusBarHighlighted = true;
                    StartDismissTimer();
                }
                else
                {
                    StatusBarText = "Check for updates";
                    IsStatusBarHighlighted = false;
                }
            }
        }
        finally
        {
            IsChecking = false;
            _startupCheckComplete = true;
        }
    }

    private async Task CheckForUpdatesManualAsync()
    {
        // Mark startup as complete so the "up to date" message shows
        _startupCheckComplete = true;
        await CheckForUpdatesAsync();
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (IsDownloading || !UpdateAvailable) return;

        // Check for stored access token
        if (string.IsNullOrEmpty(_appSettings.Settings.AccessToken))
        {
            NeedsAuthentication = true;
            StatusMessage = "Sign in required to download updates";
            return;
        }

        NeedsAuthentication = false;
        IsDownloading = true;
        DownloadProgress = 0;

        try
        {
            var progress = new Progress<int>(p =>
            {
                _dispatcher.Post(() =>
                {
                    DownloadProgress = p;
                    StatusMessage = $"Downloading... {p}%";
                });
            });

            var filePath = await _updateService.DownloadUpdateAsync(progress);

            if (filePath != null)
            {
                UpdateReady = true;
                StatusMessage = "Installing update...";

                // Auto-fire install after successful download
                var success = await _updateService.ApplyUpdateAndRestartAsync();

                if (!success)
                {
                    StatusMessage = "Update ready — please restart PrivStack";
                    await _dialogService.ShowConfirmationAsync(
                        "Restart Required",
                        "The update has been downloaded. Please restart PrivStack to apply the update.",
                        "OK");
                }
            }
            else
            {
                StatusMessage = "Download failed";
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void OpenModal()
    {
        IsUpdateModalOpen = true;
    }

    [RelayCommand]
    private void CloseModal()
    {
        IsUpdateModalOpen = false;
    }

    // ── Auto-check timer ───────────────────────────────────────────────

    public void StartAutoCheck(TimeSpan interval)
    {
        if (!_appSettings.Settings.AutoCheckForUpdates)
            return;

        StopAutoCheck();

        _autoCheckTimer = new System.Timers.Timer(interval.TotalMilliseconds);
        _autoCheckTimer.Elapsed += async (_, _) =>
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                await CheckForUpdatesAsync();
            });
        };
        _autoCheckTimer.AutoReset = true;
        _autoCheckTimer.Start();

        // Also check immediately (startup check — silent)
        _ = CheckForUpdatesAsync();
    }

    public void StopAutoCheck()
    {
        _autoCheckTimer?.Stop();
        _autoCheckTimer?.Dispose();
        _autoCheckTimer = null;
    }

    // ── 5-second "up to date" dismiss timer ────────────────────────────

    private void StartDismissTimer()
    {
        CancelDismissTimer();

        _upToDateDismissTimer = new System.Timers.Timer(5000);
        _upToDateDismissTimer.AutoReset = false;
        _upToDateDismissTimer.Elapsed += (_, _) =>
        {
            _dispatcher.Post(() =>
            {
                if (!UpdateAvailable)
                {
                    StatusBarText = "Check for updates";
                    IsStatusBarHighlighted = false;
                }
            });
        };
        _upToDateDismissTimer.Start();
    }

    private void CancelDismissTimer()
    {
        _upToDateDismissTimer?.Stop();
        _upToDateDismissTimer?.Dispose();
        _upToDateDismissTimer = null;
    }

    public void Cleanup()
    {
        StopAutoCheck();
        CancelDismissTimer();
        _updateService.UpdateFound -= OnUpdateFound;
        _updateService.UpdateError -= OnUpdateError;
    }
}
