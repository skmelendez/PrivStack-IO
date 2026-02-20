using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Services.Connections;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Item representing a connected OAuth account (Google or Microsoft).
/// </summary>
public record OAuthConnectionItem(
    string ConnectionId,
    string Provider,
    string? Email,
    string ScopesSummary,
    string ConnectedAt)
{
    /// <summary>Composite key for disconnect command: "provider:connectionId".</summary>
    public string CompositeKey => $"{Provider}:{ConnectionId}";
}

/// <summary>
/// ViewModel for the Connections section in Settings.
/// Manages GitHub device flow, Google OAuth, and Microsoft OAuth connections.
/// </summary>
public partial class ConnectionsViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConnectionsViewModel>();

    private readonly ConnectionService _connectionService;
    private readonly GitHubDeviceFlowService _deviceFlowService;
    private CancellationTokenSource? _pollCts;

    public ConnectionsViewModel(
        ConnectionService connectionService,
        GitHubDeviceFlowService deviceFlowService)
    {
        _connectionService = connectionService;
        _deviceFlowService = deviceFlowService;
        _connectionService.ConnectionChanged += OnConnectionChanged;

        LoadGitHubState();
        LoadGoogleConnections();
        LoadMicrosoftConnections();
    }

    // ========================================
    // GitHub Connection State
    // ========================================

    [ObservableProperty]
    private bool _isGitHubConnected;

    [ObservableProperty]
    private string? _gitHubUsername;

    [ObservableProperty]
    private string? _gitHubAvatarUrl;

    [ObservableProperty]
    private string _gitHubScopes = string.Empty;

    [ObservableProperty]
    private string? _gitHubConnectedAt;

    // ========================================
    // GitHub Device Flow State
    // ========================================

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string? _deviceUserCode;

    [ObservableProperty]
    private string _deviceVerificationUri = "https://github.com/login/device";

    [ObservableProperty]
    private string? _connectionError;

    // ========================================
    // Google Connection State
    // ========================================

    [ObservableProperty]
    private ObservableCollection<OAuthConnectionItem> _googleConnections = [];

    [ObservableProperty]
    private bool _isGoogleConnecting;

    // ========================================
    // Microsoft Connection State
    // ========================================

    [ObservableProperty]
    private ObservableCollection<OAuthConnectionItem> _microsoftConnections = [];

    [ObservableProperty]
    private bool _isMicrosoftConnecting;

    // ========================================
    // GitHub Commands
    // ========================================

    [RelayCommand]
    private async Task ConnectGitHubAsync()
    {
        if (IsConnecting) return;

        IsConnecting = true;
        ConnectionError = null;
        _pollCts = new CancellationTokenSource();

        try
        {
            var deviceCode = await _deviceFlowService.RequestDeviceCodeAsync(_pollCts.Token);
            DeviceUserCode = deviceCode.UserCode;
            DeviceVerificationUri = deviceCode.VerificationUri;

            var tokenResponse = await _deviceFlowService.PollForTokenAsync(
                deviceCode, _pollCts.Token);

            var (username, avatarUrl) = await _deviceFlowService.GetUserInfoAsync(
                tokenResponse.AccessToken, _pollCts.Token);

            var scopes = tokenResponse.Scope.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _connectionService.ConnectGitHubAsync(
                tokenResponse.AccessToken, scopes, username, avatarUrl, _pollCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException ex)
        {
            ConnectionError = ex.Message;
            Log.Warning("GitHub device flow timed out");
        }
        catch (Exception ex)
        {
            ConnectionError = $"Connection failed: {ex.Message}";
            Log.Error(ex, "GitHub device flow failed");
        }
        finally
        {
            IsConnecting = false;
            DeviceUserCode = null;
            _pollCts?.Dispose();
            _pollCts = null;
        }
    }

    [RelayCommand]
    private void CancelConnect()
    {
        _pollCts?.Cancel();
    }

    [RelayCommand]
    private async Task DisconnectGitHubAsync()
    {
        try
        {
            await _connectionService.DisconnectAsync("github");
        }
        catch (Exception ex)
        {
            ConnectionError = $"Disconnect failed: {ex.Message}";
            Log.Error(ex, "Failed to disconnect GitHub");
        }
    }

    [RelayCommand]
    private void OpenVerificationUri()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = DeviceVerificationUri,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open verification URI");
        }
    }

    // ========================================
    // Google Commands
    // ========================================

    [RelayCommand]
    private async Task ConnectGoogleAsync()
    {
        if (IsGoogleConnecting) return;

        IsGoogleConnecting = true;
        ConnectionError = null;

        try
        {
            await _connectionService.ConnectOAuthAsync(OAuthProviderConfig.Google);
        }
        catch (Exception ex)
        {
            ConnectionError = $"Google connection failed: {ex.Message}";
            Log.Error(ex, "Google OAuth flow failed");
        }
        finally
        {
            IsGoogleConnecting = false;
        }
    }

    // ========================================
    // Microsoft Commands
    // ========================================

    [RelayCommand]
    private async Task ConnectMicrosoftAsync()
    {
        if (IsMicrosoftConnecting) return;

        IsMicrosoftConnecting = true;
        ConnectionError = null;

        try
        {
            await _connectionService.ConnectOAuthAsync(OAuthProviderConfig.Microsoft);
        }
        catch (Exception ex)
        {
            ConnectionError = $"Microsoft connection failed: {ex.Message}";
            Log.Error(ex, "Microsoft OAuth flow failed");
        }
        finally
        {
            IsMicrosoftConnecting = false;
        }
    }

    // ========================================
    // Disconnect OAuth (Google / Microsoft)
    // ========================================

    [RelayCommand]
    private async Task DisconnectOAuthAsync(string compositeKey)
    {
        try
        {
            var parts = compositeKey.Split(':', 2);
            if (parts.Length != 2) return;

            await _connectionService.DisconnectByIdAsync(parts[0], parts[1]);
        }
        catch (Exception ex)
        {
            ConnectionError = $"Disconnect failed: {ex.Message}";
            Log.Error(ex, "Failed to disconnect OAuth connection {Key}", compositeKey);
        }
    }

    // ========================================
    // State Loading
    // ========================================

    private void OnConnectionChanged(string provider)
    {
        switch (provider)
        {
            case "github":
                LoadGitHubState();
                break;
            case "google":
                LoadGoogleConnections();
                break;
            case "microsoft":
                LoadMicrosoftConnections();
                break;
        }
    }

    private async void LoadGitHubState()
    {
        try
        {
            var info = await _connectionService.GetConnectionAsync("github");
            IsGitHubConnected = info != null;
            GitHubUsername = info?.Username;
            GitHubAvatarUrl = info?.AvatarUrl;
            GitHubScopes = info != null ? string.Join(", ", info.Scopes) : string.Empty;
            GitHubConnectedAt = info?.ConnectedAt.LocalDateTime.ToString("MMM d, yyyy");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load GitHub connection state");
        }
    }

    private async void LoadGoogleConnections()
    {
        try
        {
            var connections = await _connectionService.GetConnectionsAsync("google");
            GoogleConnections = new ObservableCollection<OAuthConnectionItem>(
                connections.Select(c => ToOAuthItem("google", c)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load Google connections");
        }
    }

    private async void LoadMicrosoftConnections()
    {
        try
        {
            var connections = await _connectionService.GetConnectionsAsync("microsoft");
            MicrosoftConnections = new ObservableCollection<OAuthConnectionItem>(
                connections.Select(c => ToOAuthItem("microsoft", c)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load Microsoft connections");
        }
    }

    private static OAuthConnectionItem ToOAuthItem(string provider, Sdk.ConnectionInfo info)
    {
        var scopesSummary = SummarizeScopes(provider, info.Scopes);
        return new OAuthConnectionItem(
            ConnectionId: info.ConnectionId ?? "",
            Provider: provider,
            Email: info.Username,
            ScopesSummary: scopesSummary,
            ConnectedAt: info.ConnectedAt.LocalDateTime.ToString("MMM d, yyyy"));
    }

    private static string SummarizeScopes(string provider, IReadOnlyList<string> scopes)
    {
        var parts = new List<string>();

        if (provider == "google")
        {
            if (scopes.Any(s => s.Contains("mail.google.com"))) parts.Add("Gmail");
            if (scopes.Any(s => s.Contains("calendar"))) parts.Add("Calendar");
        }
        else if (provider == "microsoft")
        {
            if (scopes.Any(s => s.Contains("IMAP") || s.Contains("SMTP"))) parts.Add("Outlook");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : string.Join(", ", scopes);
    }
}
