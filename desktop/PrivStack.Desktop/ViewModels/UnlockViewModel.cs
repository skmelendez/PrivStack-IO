using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for the app unlock screen.
/// </summary>
public partial class UnlockViewModel : ViewModelBase
{
    private static readonly ILogger _log = Serilog.Log.ForContext<UnlockViewModel>();

    private readonly IAuthService _service;
    private readonly IPrivStackRuntime _runtime;
    private readonly IWorkspaceService _workspaceService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlock))]
    private string _masterPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isAppLoading;

    [ObservableProperty]
    private string _loadingMessage = string.Empty;

    [ObservableProperty]
    private bool _showResetConfirmation;

    /// <summary>
    /// Whether there's an error to display (for red border styling).
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool CanUnlock => !string.IsNullOrWhiteSpace(MasterPassword) && !IsLoading;

    /// <summary>
    /// Event raised when the app is successfully unlocked.
    /// </summary>
    public event EventHandler? AppUnlocked;

    /// <summary>
    /// Event raised when the user requests to lock the app.
    /// </summary>
    public event EventHandler? LockRequested;

    /// <summary>
    /// Event raised when the user wipes data and needs to go through setup again.
    /// </summary>
    public event EventHandler? DataResetRequested;

    public UnlockViewModel(IAuthService service, IPrivStackRuntime runtime, IWorkspaceService workspaceService)
    {
        _service = service;
        _runtime = runtime;
        _workspaceService = workspaceService;
    }

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        if (!CanUnlock) return;

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var password = MasterPassword;
            await Task.Run(() => _service.UnlockApp(password));

            // Cache password for seamless workspace switching before clearing
            App.Services.GetService<IMasterPasswordCache>()?.Set(password);

            // Clear password from memory
            MasterPassword = string.Empty;

            AppUnlocked?.Invoke(this, EventArgs.Empty);
        }
        catch (PrivStackException)
        {
            ErrorMessage = "Incorrect password. Please try again.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to unlock: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnMasterPasswordChanged(string value)
    {
        // Clear error when user starts typing
        if (HasError)
        {
            ErrorMessage = string.Empty;
        }
        UnlockCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        UnlockCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ShowResetPrompt()
    {
        ShowResetConfirmation = true;
    }

    [RelayCommand]
    private void CancelReset()
    {
        ShowResetConfirmation = false;
    }

    [RelayCommand]
    private void ConfirmResetData()
    {
        ShowResetConfirmation = false;
        ErrorMessage = string.Empty;

        try
        {
            var dbPath = _workspaceService.GetActiveDataPath();
            _log.Warning("[Reset] User requested data wipe for: {Path}", dbPath);

            if (_runtime.IsInitialized)
            {
                _runtime.Shutdown();
            }

            // Delete all files in the workspace directory
            var dir = Path.GetDirectoryName(dbPath)!;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    File.Delete(file);
                }
                _log.Information("[Reset] Deleted all files in: {Dir}", dir);
            }

            // Delete settings so app returns to setup wizard
            var settingsPath = Path.Combine(DataPaths.BaseDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
                _log.Information("[Reset] Deleted settings.json");
            }

            _log.Information("[Reset] Data wipe complete — requesting setup wizard");
            DataResetRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[Reset] Failed to wipe data");
            ErrorMessage = $"Failed to reset data: {ex.Message}";
        }
    }

    /// <summary>
    /// Locks the app from anywhere in the application.
    /// </summary>
    public void RequestLock()
    {
        try
        {
            _service.LockApp();
            App.Services.GetService<IMasterPasswordCache>()?.Clear();
            MasterPassword = string.Empty;
            ErrorMessage = string.Empty;
            LockRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to lock: {ex.Message}";
        }
    }
}
