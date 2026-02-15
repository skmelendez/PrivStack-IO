using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Cloud Sync section in Settings.
/// Uses OAuth PKCE for authentication, then allows per-workspace cloud sync enablement.
/// </summary>
public partial class CloudSyncSettingsViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CloudSyncSettingsViewModel>();

    private readonly ICloudSyncService _cloudSync;
    private readonly IWorkspaceService _workspaceService;
    private readonly OAuthLoginService _oauthService;
    private readonly PrivStackApiClient _apiClient;
    private readonly IAppSettingsService _appSettings;
    private CancellationTokenSource? _oauthCts;

    public CloudSyncSettingsViewModel(
        ICloudSyncService cloudSync,
        IWorkspaceService workspaceService,
        OAuthLoginService oauthService,
        PrivStackApiClient apiClient,
        IAppSettingsService appSettings)
    {
        _cloudSync = cloudSync;
        _workspaceService = workspaceService;
        _oauthService = oauthService;
        _apiClient = apiClient;
        _appSettings = appSettings;

        LoadState();
    }

    // ========================================
    // Visibility States
    // ========================================

    public bool ShowConnectButton => !IsAuthenticated;
    public bool ShowPassphraseSetup => IsAuthenticated && !HasKeypair
                                       && !NeedsPassphraseEntry && !ShowRecoveryForm;
    public bool ShowPassphraseEntry => NeedsPassphraseEntry && !ShowRecoveryForm;
    public bool ShowDashboard => IsAuthenticated && HasKeypair
                                 && !NeedsPassphraseEntry && !ShowRecoveryForm;

    /// <summary>
    /// True when cloud sync is connected + keypair ready, but the active workspace
    /// is NOT yet registered for cloud sync.
    /// </summary>
    public bool ShowEnableForWorkspace
    {
        get
        {
            if (!ShowDashboard) return false;
            var ws = _workspaceService.GetActiveWorkspace();
            return ws?.SyncTier != SyncTier.PrivStackCloud;
        }
    }

    /// <summary>
    /// True when the active workspace is cloud-enabled (show full sync dashboard).
    /// </summary>
    public bool IsWorkspaceCloudEnabled
    {
        get
        {
            if (!ShowDashboard) return false;
            var ws = _workspaceService.GetActiveWorkspace();
            return ws?.SyncTier == SyncTier.PrivStackCloud;
        }
    }

    // ========================================
    // Authentication State
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
    [NotifyPropertyChangedFor(nameof(ShowEnableForWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceCloudEnabled))]
    private bool _isAuthenticated;

    [ObservableProperty]
    private bool _isAuthenticating;

    [ObservableProperty]
    private bool _isWaitingForBrowser;

    [ObservableProperty]
    private string? _authError;

    // ========================================
    // Passphrase Setup State
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
    [NotifyPropertyChangedFor(nameof(ShowEnableForWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceCloudEnabled))]
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
    // Passphrase Entry State
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseEntry))]
    [NotifyPropertyChangedFor(nameof(ShowPassphraseSetup))]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
    [NotifyPropertyChangedFor(nameof(ShowEnableForWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceCloudEnabled))]
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
    [NotifyPropertyChangedFor(nameof(ShowPassphraseEntry))]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
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
    // OAuth Connect
    // ========================================

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsAuthenticating) return;

        IsAuthenticating = true;
        IsWaitingForBrowser = true;
        AuthError = null;
        _oauthCts = new CancellationTokenSource();

        try
        {
            var codeVerifier = OAuthLoginService.GenerateCodeVerifier();
            var codeChallenge = OAuthLoginService.ComputeCodeChallenge(codeVerifier);
            var state = OAuthLoginService.GenerateState();

            var authorizeUrl = $"{PrivStackApiClient.ApiBaseUrl}/connect/authorize" +
                $"?client_id=privstack-desktop" +
                $"&response_type=code" +
                $"&scope=cloud_sync" +
                $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                $"&code_challenge_method=S256" +
                $"&state={Uri.EscapeDataString(state)}";

            var callback = await _oauthService.AuthorizeAsync(
                authorizeUrl, state, _oauthCts.Token);

            IsWaitingForBrowser = false;

            var tokenResult = await _apiClient.ExchangeCodeForTokenAsync(
                callback.Code, codeVerifier, callback.RedirectUri, _oauthCts.Token);

            // Extract user ID from JWT access token
            var userId = ExtractUserIdFromJwt(tokenResult.AccessToken);

            // Authenticate the Rust cloud sync core with these tokens
            await Task.Run(() => _cloudSync.AuthenticateWithTokens(
                tokenResult.AccessToken,
                tokenResult.RefreshToken ?? string.Empty,
                userId));

            IsAuthenticated = true;
            HasKeypair = _cloudSync.HasKeypair;
            if (HasKeypair)
                NeedsPassphraseEntry = true;

            Log.Information("Cloud sync connected via OAuth (userId={UserId})", userId);
        }
        catch (OperationCanceledException)
        {
            // User cancelled
        }
        catch (OAuthException ex)
        {
            AuthError = ex.Message;
            Log.Warning(ex, "Cloud sync OAuth failed");
        }
        catch (Exception ex)
        {
            AuthError = $"Connection failed: {ex.Message}";
            Log.Error(ex, "Cloud sync OAuth error");
        }
        finally
        {
            IsAuthenticating = false;
            IsWaitingForBrowser = false;
            _oauthCts?.Dispose();
            _oauthCts = null;
        }
    }

    [RelayCommand]
    private void CancelConnect()
    {
        _oauthCts?.Cancel();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await Task.Run(() => _cloudSync.Logout());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cloud sync disconnect error");
        }
        finally
        {
            IsAuthenticated = false;
            HasKeypair = false;
            NeedsPassphraseEntry = false;
            ShowRecoveryForm = false;
            IsSyncing = false;
            Quota = null;
            Devices.Clear();
        }
    }

    // ========================================
    // Passphrase Commands
    // ========================================

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
        }
        catch (Exception ex)
        {
            RecoveryError = $"Recovery failed: {ex.Message}";
            Log.Error(ex, "Mnemonic recovery failed");
        }
    }

    // ========================================
    // Enable for Workspace
    // ========================================

    [RelayCommand]
    private async Task EnableForWorkspaceAsync()
    {
        try
        {
            var workspace = _workspaceService.GetActiveWorkspace();
            if (workspace == null) return;

            await Task.Run(() => _cloudSync.RegisterWorkspace(workspace.Id, workspace.Name));
            await Task.Run(() => _cloudSync.StartSync(workspace.Id));

            IsSyncing = true;
            OnPropertyChanged(nameof(ShowEnableForWorkspace));
            OnPropertyChanged(nameof(IsWorkspaceCloudEnabled));
            await RefreshStatusAsync();

            Log.Information("Cloud sync enabled for workspace {WorkspaceId}", workspace.Id);
        }
        catch (Exception ex)
        {
            AuthError = $"Failed to enable: {ex.Message}";
            Log.Error(ex, "Failed to enable cloud sync for workspace");
        }
    }

    // ========================================
    // Sync Dashboard Commands
    // ========================================

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
                Quota = await Task.Run(() => _cloudSync.GetQuota(workspace.Id));

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

    // ========================================
    // Helpers
    // ========================================

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

    /// <summary>
    /// Extracts the user ID (sub claim) from a JWT access token without a JWT library.
    /// </summary>
    internal static long ExtractUserIdFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            throw new InvalidOperationException("Invalid JWT format");

        var payload = parts[1];
        // Fix base64url padding
        payload = payload.Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        foreach (var claimName in new[] { "sub", "id", "user_id" })
        {
            if (doc.RootElement.TryGetProperty(claimName, out var claim))
            {
                if (claim.ValueKind == JsonValueKind.Number)
                    return claim.GetInt64();
                if (long.TryParse(claim.GetString(), out var id))
                    return id;
            }
        }

        throw new InvalidOperationException("JWT does not contain a user ID (sub, id, or user_id claim)");
    }
}
