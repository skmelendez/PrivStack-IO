using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.EmergencyKit;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Cloud Sync section in Settings.
/// Uses OAuth PKCE for authentication, then allows per-workspace cloud sync enablement.
/// Encryption keypair is derived automatically from the vault password.
/// </summary>
public partial class CloudSyncSettingsViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CloudSyncSettingsViewModel>();

    private readonly ICloudSyncService _cloudSync;
    private readonly IWorkspaceService _workspaceService;
    private readonly OAuthLoginService _oauthService;
    private readonly PrivStackApiClient _apiClient;
    private readonly IAppSettingsService _appSettings;
    private readonly IMasterPasswordCache _passwordCache;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _oauthCts;

    public CloudSyncSettingsViewModel(
        ICloudSyncService cloudSync,
        IWorkspaceService workspaceService,
        OAuthLoginService oauthService,
        PrivStackApiClient apiClient,
        IAppSettingsService appSettings,
        IMasterPasswordCache passwordCache,
        IDialogService dialogService)
    {
        _cloudSync = cloudSync;
        _workspaceService = workspaceService;
        _oauthService = oauthService;
        _apiClient = apiClient;
        _appSettings = appSettings;
        _passwordCache = passwordCache;
        _dialogService = dialogService;

        LoadState();
    }

    // ========================================
    // Visibility States
    // ========================================

    public bool ShowConnectButton => !IsAuthenticated;
    public bool ShowDashboard => IsAuthenticated;

    /// <summary>
    /// True when cloud sync is connected but the active workspace
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

    [ObservableProperty]
    private bool _isEnabling;

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
    // Recovery Kit State (shown after first enable)
    // ========================================

    /// <summary>
    /// True when the recovery kit is being shown after first-time keypair creation.
    /// User must save the PDF before syncing starts.
    /// </summary>
    [ObservableProperty]
    private bool _showRecoveryKit;

    [ObservableProperty]
    private string[] _recoveryWords = [];

    [ObservableProperty]
    private bool _hasDownloadedRecoveryKit;

    // ========================================
    // Recovery Form State (manual recovery entry)
    // ========================================

    [ObservableProperty]
    private bool _showRecoveryForm;

    [ObservableProperty]
    private string? _recoveryMnemonic;

    [ObservableProperty]
    private string? _recoveryError;

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

            // Configure cloud sync engine with server-provided S3/API settings
            if (tokenResult.CloudConfig != null)
            {
                var configJson = JsonSerializer.Serialize(tokenResult.CloudConfig);
                await Task.Run(() => _cloudSync.Configure(configJson));
            }

            // Extract user ID from JWT access token
            var userId = ExtractUserIdFromJwt(tokenResult.AccessToken);

            // Authenticate the Rust cloud sync core with these tokens
            await Task.Run(() => _cloudSync.AuthenticateWithTokens(
                tokenResult.AccessToken,
                tokenResult.RefreshToken ?? string.Empty,
                userId));

            IsAuthenticated = true;
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
            IsSyncing = false;
            Quota = null;
            Devices.Clear();
        }
    }

    // ========================================
    // Enable for Workspace
    // ========================================

    [RelayCommand]
    private async Task EnableForWorkspaceAsync()
    {
        if (IsEnabling) return;
        IsEnabling = true;
        AuthError = null;

        try
        {
            var workspace = _workspaceService.GetActiveWorkspace();
            if (workspace == null) return;

            // 1. Register workspace (creates S3 prefix)
            await Task.Run(() => _cloudSync.RegisterWorkspace(workspace.Id, workspace.Name));

            // 2. Auto-setup encryption keypair using vault password
            if (!_cloudSync.HasKeypair)
            {
                var vaultPassword = _passwordCache.Get();
                if (string.IsNullOrEmpty(vaultPassword))
                {
                    AuthError = "Vault is locked. Please unlock the app first.";
                    return;
                }

                var mnemonic = await Task.Run(() => _cloudSync.SetupUnifiedRecovery(vaultPassword));
                RecoveryWords = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                ShowRecoveryKit = true;
                HasDownloadedRecoveryKit = false;
                Log.Information("Cloud encryption keypair created — showing recovery kit");

                // Don't start sync yet — user must save recovery kit first
                return;
            }

            // 3. Start syncing (keypair already exists from prior setup)
            await StartSyncForWorkspace(workspace);
        }
        catch (Exception ex)
        {
            AuthError = $"Failed to enable: {ex.Message}";
            Log.Error(ex, "Failed to enable cloud sync for workspace");
        }
        finally
        {
            IsEnabling = false;
        }
    }

    /// <summary>
    /// Called after user saves the recovery kit PDF. Starts sync for the workspace.
    /// </summary>
    [RelayCommand]
    private async Task AcknowledgeRecoveryKitAsync()
    {
        ShowRecoveryKit = false;
        RecoveryWords = [];

        try
        {
            var workspace = _workspaceService.GetActiveWorkspace();
            if (workspace == null) return;

            await StartSyncForWorkspace(workspace);
        }
        catch (Exception ex)
        {
            AuthError = $"Failed to start sync: {ex.Message}";
            Log.Error(ex, "Failed to start sync after recovery kit acknowledgment");
        }
    }

    [RelayCommand]
    private async Task SaveRecoveryKitPdfAsync()
    {
        if (RecoveryWords.Length == 0) return;

        try
        {
            var path = await _dialogService.ShowSaveFileDialogAsync(
                "Save PrivStack Recovery Kit",
                $"PrivStack-Recovery-Kit-{DateTime.Now:yyyy-MM-dd}",
                [("PDF", "pdf")]);

            if (path == null) return;

            var workspaceName = _workspaceService.GetActiveWorkspace()?.Name ?? "PrivStack";
            UnifiedRecoveryKitPdfService.Generate(RecoveryWords, workspaceName, path);
            HasDownloadedRecoveryKit = true;
            Log.Information("Cloud workspace recovery kit PDF saved");
        }
        catch (Exception ex)
        {
            AuthError = $"Failed to save PDF: {ex.Message}";
            Log.Error(ex, "Failed to save cloud recovery kit PDF");
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

            // Auto-unlock keypair if needed
            if (_cloudSync.HasKeypair)
            {
                var vaultPassword = _passwordCache.Get();
                if (!string.IsNullOrEmpty(vaultPassword))
                {
                    try { await Task.Run(() => _cloudSync.EnterPassphrase(vaultPassword)); }
                    catch { /* Already unlocked or wrong key — continue */ }
                }
            }

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
    // Recovery Commands (manual mnemonic entry)
    // ========================================

    [RelayCommand]
    private void ShowRecovery()
    {
        ShowRecoveryForm = true;
        RecoveryError = null;
        RecoveryMnemonic = null;
    }

    [RelayCommand]
    private void CancelRecovery()
    {
        ShowRecoveryForm = false;
        RecoveryError = null;
        RecoveryMnemonic = null;
    }

    [RelayCommand]
    private async Task RecoverFromMnemonicAsync()
    {
        if (string.IsNullOrWhiteSpace(RecoveryMnemonic))
        {
            RecoveryError = "Please enter your recovery words.";
            return;
        }

        RecoveryError = null;

        try
        {
            await Task.Run(() => _cloudSync.RecoverFromMnemonic(RecoveryMnemonic.Trim()));
            ShowRecoveryForm = false;
            Log.Information("Cloud encryption key recovered from mnemonic");
        }
        catch (Exception ex)
        {
            RecoveryError = $"Recovery failed: {ex.Message}";
            Log.Error(ex, "Cloud key recovery from mnemonic failed");
        }
    }

    // ========================================
    // Helpers
    // ========================================

    private async Task StartSyncForWorkspace(Workspace workspace)
    {
        await Task.Run(() => _cloudSync.StartSync(workspace.Id));
        IsSyncing = true;
        OnPropertyChanged(nameof(ShowEnableForWorkspace));
        OnPropertyChanged(nameof(IsWorkspaceCloudEnabled));
        await RefreshStatusAsync();
        Log.Information("Cloud sync enabled for workspace {WorkspaceId}", workspace.Id);
    }

    private void LoadState()
    {
        try
        {
            IsAuthenticated = _cloudSync.IsAuthenticated;
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
