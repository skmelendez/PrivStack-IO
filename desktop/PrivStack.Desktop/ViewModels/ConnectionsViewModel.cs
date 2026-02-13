using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Services.Connections;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Connections section in Settings.
/// Manages GitHub OAuth device flow and connection state.
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
    // Device Flow State
    // ========================================

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string? _deviceUserCode;

    [ObservableProperty]
    private string _deviceVerificationUri = "https://github.com/login/device";

    [ObservableProperty]
    private string? _connectionError;

    /// <summary>
    /// Initiates GitHub device flow OAuth.
    /// </summary>
    [RelayCommand]
    private async Task ConnectGitHubAsync()
    {
        if (IsConnecting) return;

        IsConnecting = true;
        ConnectionError = null;
        _pollCts = new CancellationTokenSource();

        try
        {
            // Step 1: Request device code
            var deviceCode = await _deviceFlowService.RequestDeviceCodeAsync(_pollCts.Token);
            DeviceUserCode = deviceCode.UserCode;
            DeviceVerificationUri = deviceCode.VerificationUri;

            // Step 2: Poll for token (user completes auth in browser)
            var tokenResponse = await _deviceFlowService.PollForTokenAsync(
                deviceCode, _pollCts.Token);

            // Step 3: Fetch user info
            var (username, avatarUrl) = await _deviceFlowService.GetUserInfoAsync(
                tokenResponse.AccessToken, _pollCts.Token);

            // Step 4: Store connection
            var scopes = tokenResponse.Scope.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _connectionService.ConnectGitHubAsync(
                tokenResponse.AccessToken, scopes, username, avatarUrl, _pollCts.Token);
        }
        catch (OperationCanceledException)
        {
            // User cancelled
        }
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

    /// <summary>
    /// Cancels an in-progress device flow.
    /// </summary>
    [RelayCommand]
    private void CancelConnect()
    {
        _pollCts?.Cancel();
    }

    /// <summary>
    /// Disconnects GitHub.
    /// </summary>
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

    /// <summary>
    /// Opens the verification URI in the default browser.
    /// </summary>
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

    private void OnConnectionChanged(string provider)
    {
        if (provider == "github")
            LoadGitHubState();
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
}
