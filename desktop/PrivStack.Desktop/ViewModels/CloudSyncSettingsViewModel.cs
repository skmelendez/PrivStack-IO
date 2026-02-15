using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Cloud Sync section in Settings.
/// Manages authentication, passphrase setup, sync status, quota, and devices.
/// </summary>
public partial class CloudSyncSettingsViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CloudSyncSettingsViewModel>();

    private readonly ICloudSyncService _cloudSync;
    private readonly IWorkspaceService _workspaceService;

    public CloudSyncSettingsViewModel(
        ICloudSyncService cloudSync,
        IWorkspaceService workspaceService)
    {
        _cloudSync = cloudSync;
        _workspaceService = workspaceService;

        LoadState();
    }

    // ========================================
    // Visibility States
    // ========================================

    public bool ShowAuthForm => !IsAuthenticated;
    public bool ShowPassphraseSetup => IsAuthenticated && !HasKeypair && !NeedsPassphraseEntry && !ShowRecoveryForm;
    public bool ShowSyncDashboard => IsAuthenticated && HasKeypair && !NeedsPassphraseEntry && !ShowRecoveryForm;

    // ========================================
    // Authentication State
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAuthForm))]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowSyncDashboard))]
    private bool _isAuthenticated;

    [ObservableProperty]
    private string? _authenticatedEmail;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isAuthenticating;

    [ObservableProperty]
    private string? _authError;

    // ========================================
    // Passphrase Setup State
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowSyncDashboard))]
    private bool _hasKeypair;

    [ObservableProperty]
    private string _passphrase = string.Empty;

    [ObservableProperty]
    private string _confirmPassphrase = string.Empty;

    [ObservableProperty]
    private string? _mnemonicWords;

    [ObservableProperty]
    private bool _showMnemonic;

    [ObservableProperty]
    private string? _passphraseError;

    // ========================================
    // Passphrase Entry State (unlock existing keypair)
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowSyncDashboard))]
    private bool _needsPassphraseEntry;

    [ObservableProperty]
    private string _enterPassphrase = string.Empty;

    [ObservableProperty]
    private string? _enterPassphraseError;

    // ========================================
    // Recovery State
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowSyncDashboard))]
    private bool _showRecoveryForm;

    [ObservableProperty]
    private string _recoveryMnemonic = string.Empty;

    [ObservableProperty]
    private string? _recoveryError;

    // ========================================
    // Sync Dashboard State
    // ========================================

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private int _pendingUploadCount;

    [ObservableProperty]
    private string _lastSyncDisplay = "Never";

    [ObservableProperty]
    private int _connectedDeviceCount;

    [ObservableProperty]
    private CloudQuota? _quota;

    public ObservableCollection<CloudDeviceInfo> Devices { get; } = [];

    // ========================================
    // Commands
    // ========================================

    [RelayCommand]
    private async Task AuthenticateAsync()
    {
        if (IsAuthenticating) return;
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password)) return;

        IsAuthenticating = true;
        AuthError = null;

        try
        {
            var tokens = await Task.Run(() => _cloudSync.Authenticate(Email, Password));
            AuthenticatedEmail = tokens.Email;
            IsAuthenticated = true;
            Email = string.Empty;
            Password = string.Empty;

            // Check if keypair exists; if so we need passphrase entry
            HasKeypair = _cloudSync.HasKeypair;
            if (HasKeypair)
                NeedsPassphraseEntry = true;
        }
        catch (PrivStackException ex)
        {
            AuthError = ex.Message;
            Log.Warning(ex, "Cloud sync authentication failed");
        }
        catch (Exception ex)
        {
            AuthError = $"Authentication failed: {ex.Message}";
            Log.Error(ex, "Cloud sync authentication error");
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await Task.Run(() => _cloudSync.Logout());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cloud sync logout error");
        }
        finally
        {
            IsAuthenticated = false;
            HasKeypair = false;
            NeedsPassphraseEntry = false;
            ShowRecoveryForm = false;
            AuthenticatedEmail = null;
            IsSyncing = false;
            Quota = null;
            Devices.Clear();
        }
    }

    [RelayCommand]
    private async Task SetupPassphraseAsync()
    {
        PassphraseError = null;

        if (string.IsNullOrWhiteSpace(Passphrase) || Passphrase.Length < 8)
        {
            PassphraseError = "Passphrase must be at least 8 characters.";
            return;
        }

        if (Passphrase != ConfirmPassphrase)
        {
            PassphraseError = "Passphrases do not match.";
            return;
        }

        try
        {
            var mnemonic = await Task.Run(() => _cloudSync.SetupPassphrase(Passphrase));
            MnemonicWords = mnemonic;
            ShowMnemonic = true;
            HasKeypair = true;
            Passphrase = string.Empty;
            ConfirmPassphrase = string.Empty;
        }
        catch (Exception ex)
        {
            PassphraseError = $"Setup failed: {ex.Message}";
            Log.Error(ex, "Passphrase setup failed");
        }
    }

    [RelayCommand]
    private void DismissMnemonic()
    {
        ShowMnemonic = false;
        MnemonicWords = null;
    }

    [RelayCommand]
    private async Task EnterPassphraseAsync()
    {
        EnterPassphraseError = null;

        if (string.IsNullOrWhiteSpace(EnterPassphrase))
        {
            EnterPassphraseError = "Please enter your passphrase.";
            return;
        }

        try
        {
            await Task.Run(() => _cloudSync.EnterPassphrase(EnterPassphrase));
            NeedsPassphraseEntry = false;
            EnterPassphrase = string.Empty;
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            EnterPassphraseError = $"Invalid passphrase: {ex.Message}";
            Log.Warning(ex, "Passphrase entry failed");
        }
    }

    [RelayCommand]
    private void ShowRecovery()
    {
        ShowRecoveryForm = true;
        NeedsPassphraseEntry = false;
        RecoveryError = null;
        RecoveryMnemonic = string.Empty;
    }

    [RelayCommand]
    private void CancelRecovery()
    {
        ShowRecoveryForm = false;
        NeedsPassphraseEntry = HasKeypair;
    }

    [RelayCommand]
    private async Task RecoverFromMnemonicAsync()
    {
        RecoveryError = null;

        if (string.IsNullOrWhiteSpace(RecoveryMnemonic))
        {
            RecoveryError = "Please enter your recovery words.";
            return;
        }

        try
        {
            await Task.Run(() => _cloudSync.RecoverFromMnemonic(RecoveryMnemonic));
            ShowRecoveryForm = false;
            NeedsPassphraseEntry = false;
            HasKeypair = true;
            RecoveryMnemonic = string.Empty;
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            RecoveryError = $"Recovery failed: {ex.Message}";
            Log.Error(ex, "Mnemonic recovery failed");
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await Task.Run(() => _cloudSync.GetStatus());
            IsSyncing = status.IsSyncing;
            PendingUploadCount = status.PendingUploadCount;
            ConnectedDeviceCount = status.ConnectedDevices;
            LastSyncDisplay = status.LastSyncAt?.ToLocalTime().ToString("MMM d, h:mm tt") ?? "Never";

            var workspace = _workspaceService.GetActiveWorkspace();
            if (workspace?.CloudWorkspaceId != null)
            {
                Quota = await Task.Run(() => _cloudSync.GetQuota(workspace.Id));
            }

            var devices = await Task.Run(() => _cloudSync.ListDevices());
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh cloud sync status");
        }
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        try
        {
            var workspace = _workspaceService.GetActiveWorkspace();
            if (workspace == null) return;

            await Task.Run(() => _cloudSync.StartSync(workspace.Id));
            IsSyncing = true;
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start cloud sync");
        }
    }

    [RelayCommand]
    private async Task StopSyncAsync()
    {
        try
        {
            await Task.Run(() => _cloudSync.StopSync());
            IsSyncing = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop cloud sync");
        }
    }

    private void LoadState()
    {
        try
        {
            IsAuthenticated = _cloudSync.IsAuthenticated;
            if (!IsAuthenticated) return;

            HasKeypair = _cloudSync.HasKeypair;
            if (HasKeypair)
                NeedsPassphraseEntry = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load cloud sync state");
        }
    }
}
