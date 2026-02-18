using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Sdk;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Wizard steps for the first-run setup.
/// </summary>
public enum SetupStep
{
    Welcome,
    License,           // Auth first — trial email verify or browser OAuth
    Workspace,         // Collects workspace name, display name, theme, and accessibility
    DataDirectory,     // Storage location — includes PrivStack Cloud when authenticated
    CloudWorkspaces,   // Pick existing cloud workspace or create new
    Password,
    EmergencyKit,
    Complete
}

/// <summary>
/// ViewModel for the first-run setup wizard.
/// </summary>
public partial class SetupWizardViewModel : ViewModelBase
{
    private readonly IPrivStackRuntime _runtime;
    private readonly IAuthService _authService;
    private readonly ILicensingService _licensingService;
    private readonly IAppSettingsService _appSettings;
    private readonly IThemeService _themeService;
    private readonly IFontScaleService _fontScaleService;
    private readonly IWorkspaceService _workspaceService;
    private readonly PrivStackApiClient _apiClient;
    private readonly OAuthLoginService _oauthService;
    private readonly ICloudSyncService _cloudSync;
    private CancellationTokenSource? _oauthCts;
    private string? _setupWorkspaceId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeStep))]
    [NotifyPropertyChangedFor(nameof(IsDataDirectoryStep))]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceStep))]
    [NotifyPropertyChangedFor(nameof(IsLicenseStep))]
    [NotifyPropertyChangedFor(nameof(IsCloudWorkspacesStep))]
    [NotifyPropertyChangedFor(nameof(IsPasswordStep))]
    [NotifyPropertyChangedFor(nameof(IsEmergencyKitStep))]
    [NotifyPropertyChangedFor(nameof(IsCompleteStep))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    [NotifyPropertyChangedFor(nameof(StepNumber))]
    [NotifyPropertyChangedFor(nameof(TotalSteps))]
    [NotifyPropertyChangedFor(nameof(ProgressWidth))]
    private SetupStep _currentStep = SetupStep.Welcome;

    // Step visibility
    public bool IsWelcomeStep => CurrentStep == SetupStep.Welcome;
    public bool IsDataDirectoryStep => CurrentStep == SetupStep.DataDirectory;
    public bool IsWorkspaceStep => CurrentStep == SetupStep.Workspace;
    public bool IsLicenseStep => CurrentStep == SetupStep.License;
    public bool IsCloudWorkspacesStep => CurrentStep == SetupStep.CloudWorkspaces;
    public bool IsPasswordStep => CurrentStep == SetupStep.Password;
    public bool IsEmergencyKitStep => CurrentStep == SetupStep.EmergencyKit;
    public bool IsCompleteStep => CurrentStep == SetupStep.Complete;

    // Workspace + Profile step (merged)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private string _workspaceName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private string _displayName = string.Empty;

    // License step
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _licenseError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isLicenseValid;

    [ObservableProperty]
    private string _licenseTier = string.Empty;

    // Trial mode properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isTrialMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private string _trialEmail = string.Empty;

    [ObservableProperty]
    private string _trialError = string.Empty;

    [ObservableProperty]
    private bool _isStartingTrial;

    [ObservableProperty]
    private int _trialDaysRemaining;

    [ObservableProperty]
    private bool _isAwaitingVerification;

    [ObservableProperty]
    private string _verificationCode = string.Empty;

    [ObservableProperty]
    private bool _isVerifyingCode;

    // OAuth login properties
    [ObservableProperty]
    private bool _isWaitingForBrowser;

    [ObservableProperty]
    private string _loginError = string.Empty;

    // Expired subscription properties
    [ObservableProperty]
    private bool _isSubscriptionExpired;

    [ObservableProperty]
    private string _expiredMessage = string.Empty;

    // Data Directory step
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    [NotifyPropertyChangedFor(nameof(IsCustomDirectorySelected))]
    [NotifyPropertyChangedFor(nameof(IsDefaultSelected))]
    private DataDirectoryType _selectedDirectoryType = DataDirectoryType.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    private string _customDataDirectory = string.Empty;

    public bool IsGoogleDriveAvailable => !string.IsNullOrEmpty(GetGoogleDrivePath());
    public bool IsICloudAvailable => !string.IsNullOrEmpty(GetICloudPath());
    public bool IsCustomDirectorySelected => SelectedDirectoryType == DataDirectoryType.Custom;
    public bool IsDefaultSelected => SelectedDirectoryType == DataDirectoryType.Default && !IsPrivStackCloudSelected;

    // Cloud sync workspace selection
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isCloudSyncAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultSelected))]
    private bool _isPrivStackCloudSelected;

    [ObservableProperty]
    private bool _isLoadingCloudWorkspaces;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudWorkspaces))]
    private ObservableCollection<CloudWorkspaceInfo> _cloudWorkspaces = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private CloudWorkspaceInfo? _selectedCloudWorkspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isCreateNewCloudWorkspace = true;

    public bool HasCloudWorkspaces => CloudWorkspaces.Count > 0;

    public string DataDirectoryDisplay => SelectedDirectoryType switch
    {
        DataDirectoryType.Default => GetDefaultDataPath(),
        DataDirectoryType.Custom => string.IsNullOrEmpty(CustomDataDirectory) ? "Select a folder..." : CustomDataDirectory,
        DataDirectoryType.GoogleDrive => GetGoogleDrivePath() ?? "Not available",
        DataDirectoryType.ICloud => GetICloudPath() ?? "Not available",
        _ => GetDefaultDataPath()
    };

    public string EffectiveDataDirectory => SelectedDirectoryType switch
    {
        DataDirectoryType.Default => GetDefaultDataPath(),
        DataDirectoryType.Custom => Path.Combine(CustomDataDirectory, "PrivStack"),
        DataDirectoryType.GoogleDrive => Path.Combine(GetGoogleDrivePath() ?? GetDefaultDataPath(), "PrivStack"),
        DataDirectoryType.ICloud => Path.Combine(GetICloudPath() ?? GetDefaultDataPath(), "PrivStack"),
        _ => GetDefaultDataPath()
    };

    // Password step
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(PasswordsMatch))]
    private string _masterPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(PasswordsMatch))]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private string _passwordStrength = string.Empty;

    [ObservableProperty]
    private string _setupError = string.Empty;

    [ObservableProperty]
    private bool _isAppLoading;

    [ObservableProperty]
    private bool _showResetConfirmation;

    [ObservableProperty]
    private string _loadingMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordStepTitle))]
    [NotifyPropertyChangedFor(nameof(PasswordStepDescription))]
    [NotifyPropertyChangedFor(nameof(ShowConfirmPassword))]
    private bool _isExistingData;

    public string PasswordStepTitle => IsExistingData
        ? "Unlock Existing Data"
        : "Create Master Password";

    public string PasswordStepDescription => IsExistingData
        ? "Enter your master password to unlock the existing PrivStack data."
        : "This password protects your sensitive data. Choose a strong password you'll remember.";

    public bool ShowConfirmPassword => !IsExistingData;
    public bool PasswordsMatch => IsExistingData || MasterPassword == ConfirmPassword;

    // Theme step
    public ObservableCollection<ThemeOption> AvailableThemes { get; } =
    [
        new(AppTheme.Dark, "Dark"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Sage, "Sage"),
        new(AppTheme.Lavender, "Lavender"),
        new(AppTheme.Azure, "Azure"),
        new(AppTheme.Slate, "Slate"),
        new(AppTheme.Ember, "Ember"),
    ];

    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    public string SelectedThemeDisplay => SelectedTheme?.DisplayName ?? "Dark";

    // Font scale / accessibility
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontScaleDisplayText))]
    [NotifyPropertyChangedFor(nameof(FontScalePercentText))]
    private double _fontScaleMultiplier = 1.0;

    public string FontScaleDisplayText => FontScaleMultiplier switch
    {
        <= 0.85 => "Small",
        <= 0.95 => "Medium",
        <= 1.05 => "Default",
        <= 1.15 => "Large",
        <= 1.30 => "Extra Large",
        _ => "Maximum"
    };

    public string FontScalePercentText => $"{FontScaleMultiplier * 100:0}%";

    partial void OnFontScaleMultiplierChanged(double value)
    {
        _fontScaleService.ScaleMultiplier = value;
    }

    [RelayCommand]
    private void SetFontScale(double scale)
    {
        FontScaleMultiplier = scale;
    }

    // Navigation
    public bool CanGoBack => CurrentStep != SetupStep.Welcome
                          && CurrentStep != SetupStep.Complete
                          && CurrentStep != SetupStep.EmergencyKit;

    public bool CanGoNext => CurrentStep switch
    {
        SetupStep.Welcome => false, // Welcome uses ChooseTrial/ChooseSignIn commands
        SetupStep.License => IsLicenseValid,
        SetupStep.Workspace => !string.IsNullOrWhiteSpace(WorkspaceName) && !string.IsNullOrWhiteSpace(DisplayName) && SelectedTheme != null,
        SetupStep.DataDirectory => SelectedDirectoryType != DataDirectoryType.Custom ||
                                   !string.IsNullOrWhiteSpace(CustomDataDirectory),
        SetupStep.CloudWorkspaces => SelectedCloudWorkspace != null || IsCreateNewCloudWorkspace,
        SetupStep.Password => IsExistingData
            ? !string.IsNullOrWhiteSpace(MasterPassword)
            : !string.IsNullOrWhiteSpace(MasterPassword) && MasterPassword.Length >= 8 && PasswordsMatch,
        SetupStep.EmergencyKit => HasDownloadedKit,
        SetupStep.Complete => true,
        _ => false
    };

    public string NextButtonText => CurrentStep switch
    {
        SetupStep.Password => IsExistingData ? "Complete Setup" : "Next",
        SetupStep.EmergencyKit => "Complete Setup",
        SetupStep.Complete => "Get Started",
        _ => "Next"
    };

    public int StepNumber => CurrentStep switch
    {
        SetupStep.Welcome => 1,
        SetupStep.License => 2,
        SetupStep.Workspace => 3,
        SetupStep.DataDirectory => 4,
        SetupStep.CloudWorkspaces => 5,
        SetupStep.Password => _showsCloudWorkspacesStep ? 6 : 5,
        SetupStep.EmergencyKit => _showsCloudWorkspacesStep ? 7 : 6,
        SetupStep.Complete => TotalSteps,
        _ => 0
    };

    private bool _showsCloudWorkspacesStep;
    public int TotalSteps => _showsCloudWorkspacesStep ? 8 : 7;

    public double ProgressWidth => (StepNumber / (double)TotalSteps) * 160;

    public event EventHandler? SetupCompleted;

    [ObservableProperty]
    private DeviceInfo? _deviceInfo;

    public SetupWizardViewModel(
        IPrivStackRuntime runtime,
        IAuthService authService,
        ILicensingService licensingService,
        IAppSettingsService appSettings,
        IThemeService themeService,
        IFontScaleService fontScaleService,
        IWorkspaceService workspaceService,
        PrivStackApiClient apiClient,
        OAuthLoginService oauthService,
        ICloudSyncService cloudSync)
    {
        _runtime = runtime;
        _authService = authService;
        _licensingService = licensingService;
        _appSettings = appSettings;
        _themeService = themeService;
        _fontScaleService = fontScaleService;
        _workspaceService = workspaceService;
        _apiClient = apiClient;
        _oauthService = oauthService;
        _cloudSync = cloudSync;
        LoadDeviceInfo();

        SelectedTheme = AvailableThemes.FirstOrDefault(t => t.Theme == AppTheme.Dark);
        FontScaleMultiplier = _fontScaleService.ScaleMultiplier;
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value != null)
        {
            _themeService.CurrentTheme = value.Theme;
        }
    }

    private void LoadDeviceInfo()
    {
        try
        {
            DeviceInfo = _licensingService.GetDeviceInfo<DeviceInfo>();
        }
        catch
        {
            // Ignore
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (!CanGoBack) return;

        CurrentStep = CurrentStep switch
        {
            SetupStep.License => SetupStep.Welcome,
            SetupStep.Workspace => SetupStep.License,
            SetupStep.DataDirectory => SetupStep.Workspace,
            SetupStep.CloudWorkspaces => SetupStep.DataDirectory,
            SetupStep.Password => _showsCloudWorkspacesStep ? SetupStep.CloudWorkspaces : SetupStep.DataDirectory,
            _ => CurrentStep
        };
    }

    [RelayCommand]
    private async void GoNext()
    {
        if (!CanGoNext) return;

        CurrentStep = CurrentStep switch
        {
            SetupStep.Welcome => SetupStep.License,
            SetupStep.License => SetupStep.Workspace,
            SetupStep.Workspace => SetupStep.DataDirectory,
            SetupStep.DataDirectory => await HandleDataDirectoryNext(),
            SetupStep.CloudWorkspaces => InitializeServiceAndContinue(),
            SetupStep.Password => CompleteSetup(),
            SetupStep.EmergencyKit => CompleteEmergencyKitStep(),
            SetupStep.Complete => FinishSetup(),
            _ => CurrentStep
        };
    }

    [RelayCommand]
    private async Task SignInWithBrowserAsync()
    {
        IsWaitingForBrowser = true;
        LoginError = string.Empty;
        _oauthCts = new CancellationTokenSource();

        Log.Information("[OAuth] Starting browser sign-in flow");

        try
        {
            // Generate PKCE params
            var codeVerifier = OAuthLoginService.GenerateCodeVerifier();
            var codeChallenge = OAuthLoginService.ComputeCodeChallenge(codeVerifier);
            var state = OAuthLoginService.GenerateState();

            // Build authorize URL (redirect_uri is appended by OAuthLoginService)
            var authorizeUrl = $"{PrivStackApiClient.ApiBaseUrl}/connect/authorize" +
                $"?client_id=privstack-desktop" +
                $"&response_type=code" +
                $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                $"&code_challenge_method=S256" +
                $"&state={Uri.EscapeDataString(state)}";

            Log.Information("[OAuth] Opening browser for authorization...");
            var callback = await _oauthService.AuthorizeAsync(authorizeUrl, state, _oauthCts.Token);
            Log.Information("[OAuth] Received authorization code, exchanging for tokens...");

            // Exchange code for tokens
            var tokenResult = await _apiClient.ExchangeCodeForTokenAsync(
                callback.Code, codeVerifier, callback.RedirectUri, _oauthCts.Token);
            Log.Information("[OAuth] Token exchange succeeded");

            // Fetch license key
            Log.Information("[OAuth] Fetching license key...");
            var licenseResult = await _apiClient.GetLicenseKeyAsync(tokenResult.AccessToken);
            Log.Information("[OAuth] License response — has license: {HasLicense}, plan: {Plan}, status: {Status}",
                licenseResult.License != null,
                licenseResult.License?.Plan,
                licenseResult.License?.SubscriptionStatus);

            if (licenseResult.License == null || string.IsNullOrEmpty(licenseResult.License.Key))
            {
                Log.Warning("[OAuth] No license key found for account");
                LoginError = "No license found for this account. Purchase a license at privstack.io";
                return;
            }

            var status = licenseResult.License.SubscriptionStatus?.ToLower();
            if (status is "expired" or "cancelled")
            {
                Log.Warning("[OAuth] Subscription status disqualified: {Status}", status);
                LicenseKey = licenseResult.License.Key;
                IsSubscriptionExpired = true;
                ExpiredMessage = status == "expired"
                    ? "Your subscription has expired."
                    : "Your subscription has been cancelled.";
                return;
            }

            // Persist tokens for authenticated API calls (e.g., update downloads)
            _appSettings.Settings.AccessToken = tokenResult.AccessToken;
            _appSettings.Settings.RefreshToken = tokenResult.RefreshToken;
            _appSettings.SaveDebounced();
            Log.Information("[OAuth] Persisted access and refresh tokens");

            // Capture cloud sync tokens (OAuth always returns cloud_config)
            if (tokenResult.CloudConfig != null)
            {
                _appSettings.Settings.CloudSyncAccessToken = tokenResult.AccessToken;
                _appSettings.Settings.CloudSyncRefreshToken = tokenResult.RefreshToken;
                _appSettings.Settings.CloudSyncConfigJson = JsonSerializer.Serialize(tokenResult.CloudConfig);
                // Extract user ID from JWT
                var userId = CloudSyncSettingsViewModel.ExtractUserIdFromJwt(tokenResult.AccessToken);
                _appSettings.Settings.CloudSyncUserId = userId;
                _appSettings.SaveDebounced();
                IsCloudSyncAvailable = true;
                Log.Information("[OAuth] Cloud sync tokens captured (userId={UserId})", userId);
            }

            // Got a valid license key — run standard validation
            Log.Information("[OAuth] License key received (length: {Length}), running local validation...",
                licenseResult.License.Key.Length);
            LicenseKey = licenseResult.License.Key;
            ValidateLicense();

            Log.Information("[OAuth] Validation result — IsLicenseValid: {Valid}, LicenseError: {Error}",
                IsLicenseValid, LicenseError);

            if (!IsLicenseValid)
            {
                LoginError = "License validation failed. Contact support at privstack.io";
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("[OAuth] Sign-in cancelled by user");
        }
        catch (OAuthException ex)
        {
            Log.Warning("[OAuth] OAuth error: {Message}", ex.Message);
            LoginError = ex.Message;
        }
        catch (PrivStackApiException ex)
        {
            Log.Warning("[OAuth] API error: {Message}", ex.Message);
            LoginError = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("[OAuth] HTTP error: {Message}", ex.Message);
            LoginError = "Could not reach activation server. Check your internet connection.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OAuth] Unexpected failure");
            LoginError = "An unexpected error occurred. Please try again.";
        }
        finally
        {
            IsWaitingForBrowser = false;
            _oauthCts?.Dispose();
            _oauthCts = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        _oauthCts?.Cancel();
    }

    [RelayCommand]
    private void ChooseTrial()
    {
        IsTrialMode = true;
        CurrentStep = SetupStep.License;
    }

    [RelayCommand]
    private void ChooseSignIn()
    {
        IsTrialMode = false;
        CurrentStep = SetupStep.License;
    }

    [RelayCommand]
    private async Task StartTrialAsync()
    {
        if (string.IsNullOrWhiteSpace(TrialEmail) || !TrialEmail.Contains('@'))
        {
            TrialError = "Please enter a valid email address.";
            return;
        }

        IsStartingTrial = true;
        TrialError = string.Empty;

        try
        {
            Log.Information("[Trial] Requesting trial for: {Email}", TrialEmail);
            var result = await _apiClient.StartTrialAsync(TrialEmail.Trim());

            if (result.Success && result.RequiresVerification)
            {
                // Step 1 complete — code sent to email, show verification input
                Log.Information("[Trial] Verification code sent to email");
                IsAwaitingVerification = true;
            }
            else if (result.Success && !string.IsNullOrEmpty(result.LicenseKey))
            {
                // Returning user with active trial — license returned directly
                Log.Information("[Trial] Existing trial — {Days} days remaining", result.TrialDays);
                TrialDaysRemaining = result.TrialDays;
                LicenseKey = result.LicenseKey;
                ValidateLicense();

                if (!IsLicenseValid)
                {
                    TrialError = "License validation failed. Please try again or contact support.";
                }
                else
                {
                    CaptureTrialCloudTokens(result);
                }
            }
            else
            {
                Log.Warning("[Trial] Trial start failed — Error: {Error}", result.Error);
                TrialError = result.Message ?? result.Error ?? "Failed to start trial.";
            }
        }
        catch (HttpRequestException)
        {
            TrialError = "Could not reach the server. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            TrialError = "Request timed out. Please try again.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Trial] Unexpected error starting trial");
            TrialError = "An unexpected error occurred. Please try again.";
        }
        finally
        {
            IsStartingTrial = false;
        }
    }

    [RelayCommand]
    private async Task VerifyTrialCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(VerificationCode) || VerificationCode.Trim().Length != 6)
        {
            TrialError = "Please enter the 6-digit code from your email.";
            return;
        }

        IsVerifyingCode = true;
        TrialError = string.Empty;

        try
        {
            Log.Information("[Trial] Verifying code for: {Email}", TrialEmail);
            var result = await _apiClient.VerifyTrialCodeAsync(TrialEmail.Trim(), VerificationCode.Trim());

            if (result.Success && !string.IsNullOrEmpty(result.LicenseKey))
            {
                Log.Information("[Trial] Trial verified — {Days} days", result.TrialDays);
                TrialDaysRemaining = result.TrialDays;
                LicenseKey = result.LicenseKey;
                ValidateLicense();

                if (!IsLicenseValid)
                {
                    TrialError = "License validation failed. Please try again or contact support.";
                }
                else
                {
                    CaptureTrialCloudTokens(result);
                }
            }
            else
            {
                Log.Warning("[Trial] Verification failed — Error: {Error}", result.Error);
                TrialError = result.Error ?? "Verification failed. Please try again.";
            }
        }
        catch (HttpRequestException)
        {
            TrialError = "Could not reach the server. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            TrialError = "Request timed out. Please try again.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Trial] Unexpected error verifying code");
            TrialError = "An unexpected error occurred. Please try again.";
        }
        finally
        {
            IsVerifyingCode = false;
        }
    }

    [RelayCommand]
    private async Task ResendTrialCodeAsync()
    {
        IsStartingTrial = true;
        TrialError = string.Empty;
        VerificationCode = string.Empty;

        try
        {
            Log.Information("[Trial] Resending verification code for: {Email}", TrialEmail);
            var result = await _apiClient.StartTrialAsync(TrialEmail.Trim());

            if (result.Success && result.RequiresVerification)
            {
                TrialError = string.Empty;
            }
            else
            {
                TrialError = result.Message ?? result.Error ?? "Failed to resend code.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Trial] Error resending code");
            TrialError = "Could not resend code. Please try again.";
        }
        finally
        {
            IsStartingTrial = false;
        }
    }

    [RelayCommand]
    private void BackToTrialEmail()
    {
        IsAwaitingVerification = false;
        VerificationCode = string.Empty;
        TrialError = string.Empty;
    }

    [RelayCommand]
    private void SwitchToSignIn()
    {
        IsTrialMode = false;
        TrialError = string.Empty;
    }

    [RelayCommand]
    private void SwitchToTrial()
    {
        IsTrialMode = true;
        LoginError = string.Empty;
    }

    [RelayCommand]
    private void ContinueReadOnly()
    {
        IsSubscriptionExpired = false;
        IsLicenseValid = true;
        LicenseTier = "Read-Only";
        LicenseError = string.Empty;
        LoginError = string.Empty;
        TrialError = string.Empty;
        CurrentStep = SetupStep.Workspace;
    }

    private void ValidateLicense()
    {
        if (string.IsNullOrWhiteSpace(LicenseKey))
        {
            LicenseError = "Please enter a license key";
            IsLicenseValid = false;
            return;
        }

        Log.Information("[Activate] Validating license key (length: {Length})", LicenseKey.Length);

        try
        {
            Log.Information("[Activate] Parsing license key...");
            var licenseInfo = _licensingService.ParseLicenseKey<LicenseInfo>(LicenseKey);
            Log.Information("[Activate] Parsed — Plan: {Plan}, Status: {Status}", licenseInfo.Plan, licenseInfo.Status);

            if (licenseInfo.Status is "expired" or "readonly")
            {
                Log.Warning("[Activate] License status disqualified: {Status}", licenseInfo.Status);
                IsSubscriptionExpired = true;
                ExpiredMessage = licenseInfo.Status == "readonly"
                    ? "Your license is in read-only mode (grace period expired)."
                    : "Your license key has expired.";
                IsLicenseValid = false;
                return;
            }

            Log.Information("[Activate] Activating license on device...");
            var activation = _licensingService.ActivateLicense<ActivationInfo>(LicenseKey);
            Log.Information("[Activate] Activation result — IsValid: {IsValid}", activation.IsValid);

            if (!activation.IsValid)
            {
                LicenseError = "Failed to activate license on this device";
                IsLicenseValid = false;
                return;
            }

            IsLicenseValid = true;
            LicenseError = string.Empty;
            LicenseTier = FormatLicensePlan(licenseInfo.Plan);
            Log.Information("[Activate] License activated successfully — Tier: {Tier}", LicenseTier);
        }
        catch (PrivStackException ex)
        {
            Log.Error(ex, "[Activate] License validation failed — ErrorCode: {Code}", ex.ErrorCode);
            LicenseError = ex.ErrorCode switch
            {
                PrivStackError.LicenseInvalidFormat => "Invalid license key format",
                PrivStackError.LicenseInvalidSignature => "License key signature invalid",
                PrivStackError.LicenseExpired => HandleExpiredLicenseError(),
                PrivStackError.LicenseActivationFailed => "Activation failed - device limit may be exceeded",
                _ => $"License validation failed: {ex.Message}"
            };
            IsLicenseValid = false;
        }
        catch (Exception ex)
        {
            LicenseError = $"Error: {ex.Message}";
            IsLicenseValid = false;
        }
    }

    partial void OnMasterPasswordChanged(string value)
    {
        PasswordStrength = CalculatePasswordStrength(value);
    }

    private static string CalculatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;

        int score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        if (password.Any(char.IsUpper)) score++;
        if (password.Any(char.IsLower)) score++;
        if (password.Any(char.IsDigit)) score++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score++;

        return score switch
        {
            <= 2 => "Weak",
            <= 4 => "Moderate",
            _ => "Strong"
        };
    }

    private void CaptureTrialCloudTokens(TrialResponse result)
    {
        if (result.AccessToken == null || result.CloudConfig == null)
            return;

        _appSettings.Settings.AccessToken = result.AccessToken;
        _appSettings.Settings.RefreshToken = result.RefreshToken;
        _appSettings.Settings.CloudSyncAccessToken = result.AccessToken;
        _appSettings.Settings.CloudSyncRefreshToken = result.RefreshToken;
        _appSettings.Settings.CloudSyncConfigJson = JsonSerializer.Serialize(result.CloudConfig);
        _appSettings.Settings.CloudSyncUserId = result.UserId;
        _appSettings.SaveDebounced();
        IsCloudSyncAvailable = true;
        Log.Information("[Trial] Cloud sync tokens captured (userId={UserId})", result.UserId);
    }

    private async Task<SetupStep> HandleDataDirectoryNext()
    {
        if (IsPrivStackCloudSelected && IsCloudSyncAvailable)
        {
            // Fetch cloud workspaces via direct HTTP (no FFI needed)
            IsLoadingCloudWorkspaces = true;
            try
            {
                var accessToken = _appSettings.Settings.CloudSyncAccessToken;
                if (!string.IsNullOrEmpty(accessToken))
                {
                    var workspaces = await _apiClient.ListCloudWorkspacesAsync(accessToken);
                    CloudWorkspaces = new ObservableCollection<CloudWorkspaceInfo>(workspaces);
                    OnPropertyChanged(nameof(HasCloudWorkspaces));
                    Log.Information("[Cloud] Fetched {Count} cloud workspaces", workspaces.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Cloud] Failed to fetch cloud workspaces");
            }
            finally
            {
                IsLoadingCloudWorkspaces = false;
            }

            if (CloudWorkspaces.Count > 0)
            {
                _showsCloudWorkspacesStep = true;
                OnPropertyChanged(nameof(TotalSteps));
                return SetupStep.CloudWorkspaces;
            }

            // No existing workspaces — skip cloud workspace picker, proceed to create
            SelectedCloudWorkspace = null;
            IsCreateNewCloudWorkspace = true;
        }

        return InitializeServiceAndContinue();
    }

    [RelayCommand]
    private void SelectCloudWorkspace(CloudWorkspaceInfo? workspace)
    {
        if (workspace != null)
        {
            SelectedCloudWorkspace = workspace;
            IsCreateNewCloudWorkspace = false;
        }
        else
        {
            SelectedCloudWorkspace = null;
            IsCreateNewCloudWorkspace = true;
        }
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private void SelectDirectoryType(DataDirectoryType type)
    {
        SelectedDirectoryType = type;
        IsPrivStackCloudSelected = false;
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private void SelectPrivStackCloud()
    {
        IsPrivStackCloudSelected = true;
        // Reset file-based sync selection — PrivStack Cloud uses Default local path
        SelectedDirectoryType = DataDirectoryType.Default;
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private async Task BrowseDataDirectoryAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Data Directory",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            CustomDataDirectory = folders[0].Path.LocalPath;
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    private static TopLevel? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    private static string GetDefaultDataPath()
    {
        return DataPaths.BaseDir;
    }

    private static string? GetGoogleDrivePath() => CloudPathResolver.GetGoogleDrivePath();

    private static string? GetICloudPath() => CloudPathResolver.GetICloudPath();

    private SetupStep InitializeServiceAndContinue()
    {
        SetupError = string.Empty;

        try
        {
            // 1. Build per-workspace storage location from wizard selections
            var storageLocation = SelectedDirectoryType == DataDirectoryType.Default
                ? null
                : new StorageLocation
                {
                    Type = SelectedDirectoryType.ToString(),
                    CustomPath = SelectedDirectoryType == DataDirectoryType.Custom ? CustomDataDirectory : null
                };

            // 2. Ensure the sync directories exist (event store, snapshots, shared files)
            var dataDir = EffectiveDataDirectory;
            Directory.CreateDirectory(dataDir);
            Log.Information("Data directory set: {Path}", dataDir);

            // 3. Create workspace with storage location (directory only, no DB yet)
            //    makeActive: true ensures the registry points to this workspace,
            //    even on retry when a previous failed workspace was already active.
            var name = WorkspaceName.Trim();
            var workspace = _workspaceService.CreateWorkspace(name, storageLocation, makeActive: true);
            Log.Information("Initial workspace created: {Name} ({Id})", name, workspace.Id);

            // Clean up previously failed setup workspace (now non-active, safe to delete)
            if (_setupWorkspaceId != null && _setupWorkspaceId != workspace.Id)
            {
                try
                {
                    _workspaceService.DeleteWorkspace(_setupWorkspaceId);
                    Log.Information("Cleaned up failed setup workspace: {Id}", _setupWorkspaceId);
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "Could not clean up failed setup workspace: {Id}", _setupWorkspaceId);
                }
            }
            _setupWorkspaceId = workspace.Id;

            // 4. Set DataPaths so all workspace-scoped paths resolve correctly
            var resolvedDir = _workspaceService.ResolveWorkspaceDir(workspace);
            DataPaths.SetActiveWorkspace(workspace.Id, resolvedDir);

            // 5. Initialize runtime directly at workspace DB path — derived from
            //    the workspace we just created, not from GetActiveDataPath()
            var dbPath = Path.Combine(resolvedDir, "data.duckdb");
            var dbDir = Path.GetDirectoryName(dbPath)!;
            Directory.CreateDirectory(dbDir);

            if (_runtime.IsInitialized)
            {
                _runtime.Shutdown();
            }

            Log.Information("Initializing service at workspace path: {Path}", dbPath);
            _runtime.Initialize(dbPath);
            Log.Information("Service initialized successfully at: {Path}", dbPath);

            LoadDeviceInfo();

            // 5. Check for existing auth data
            IsExistingData = _authService.IsAuthInitialized();
            Log.Information("Auth initialized (existing data): {IsExisting}", IsExistingData);

            // 6. Connect PrivStack Cloud if selected (FFI is now available)
            if (IsPrivStackCloudSelected && IsCloudSyncAvailable)
            {
                try
                {
                    ConnectCloudSync(workspace);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to connect cloud sync during setup");
                    SetupError = $"Cloud sync setup failed: {ex.Message}. You can configure it later in Settings.";
                }
            }

            return SetupStep.Password;
        }
        catch (DllNotFoundException dllEx)
        {
            Log.Error(dllEx, "Native library not found");
            SetupError = "Native library not found. The privstack_ffi library may be missing from the application directory. Please reinstall the application.";
            return SetupStep.DataDirectory;
        }
        catch (BadImageFormatException bifEx)
        {
            Log.Error(bifEx, "Native library architecture mismatch");
            SetupError = "Native library architecture mismatch. Please ensure you're running the correct version (32-bit vs 64-bit) for your system.";
            return SetupStep.DataDirectory;
        }
        catch (EntryPointNotFoundException epEx)
        {
            Log.Error(epEx, "Native library entry point not found");
            SetupError = "Native library is incompatible. The library may be corrupted or from an incorrect version. Please reinstall.";
            return SetupStep.DataDirectory;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize service at chosen directory");
            if (ex.InnerException is DllNotFoundException)
            {
                SetupError = "Native library not found. The privstack_ffi library may be missing. Please reinstall the application.";
            }
            else if (ex.Message.Contains("Unable to load") || ex.Message.Contains("DLL"))
            {
                SetupError = $"Failed to load native library: {ex.Message}\n\nPlease ensure all required files are present in the application directory.";
            }
            else
            {
                SetupError = $"Failed to initialize: {ex.Message}";
            }
            return SetupStep.DataDirectory;
        }
    }

    private void ConnectCloudSync(Workspace workspace)
    {
        var settings = _appSettings.Settings;

        // Configure cloud sync engine
        if (!string.IsNullOrEmpty(settings.CloudSyncConfigJson))
        {
            _cloudSync.Configure(settings.CloudSyncConfigJson);
        }

        // Authenticate with persisted tokens
        _cloudSync.AuthenticateWithTokens(
            settings.CloudSyncAccessToken!,
            settings.CloudSyncRefreshToken ?? string.Empty,
            settings.CloudSyncUserId!.Value);

        string cloudWsId;
        if (SelectedCloudWorkspace != null)
        {
            // Connect to existing cloud workspace — no RegisterWorkspace needed
            cloudWsId = SelectedCloudWorkspace.WorkspaceId;
            Log.Information("Connecting to existing cloud workspace: {Id} ({Name})",
                cloudWsId, SelectedCloudWorkspace.WorkspaceName);
        }
        else
        {
            // Create new cloud workspace
            cloudWsId = Guid.NewGuid().ToString();
            _cloudSync.RegisterWorkspace(cloudWsId, workspace.Name);
            Log.Information("Registered new cloud workspace: {Id}", cloudWsId);
        }

        // Persist cloud workspace ID + sync tier on the local workspace record
        var updatedWorkspace = workspace with
        {
            CloudWorkspaceId = cloudWsId,
            SyncTier = SyncTier.PrivStackCloud
        };
        _workspaceService.UpdateWorkspace(updatedWorkspace);
        _appSettings.Save();

        Log.Information("Cloud sync connected for workspace: {Id}", cloudWsId);
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
        SetupError = string.Empty;

        try
        {
            var dbPath = _workspaceService.GetActiveDataPath();
            Log.Warning("[Reset] User requested data wipe for: {Path}", dbPath);

            // Shut down the runtime so the DB file is released
            if (_runtime.IsInitialized)
            {
                _runtime.Shutdown();
            }

            // Delete all files in the workspace directory (DB + WAL + any other artifacts)
            var dir = Path.GetDirectoryName(dbPath)!;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    File.Delete(file);
                }
                Log.Information("[Reset] Deleted all files in: {Dir}", dir);
            }

            // Re-initialize the runtime with a fresh DB
            _runtime.Initialize(dbPath);
            Log.Information("[Reset] Re-initialized with fresh DB at: {Path}", dbPath);

            // Switch to "create new password" mode
            IsExistingData = false;
            MasterPassword = string.Empty;
            ConfirmPassword = string.Empty;
            PasswordStrength = string.Empty;
            Log.Information("[Reset] Data wipe complete — switched to new password mode");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Reset] Failed to wipe data");
            SetupError = $"Failed to reset data: {ex.Message}";
        }
    }

    private SetupStep CompleteSetup()
    {
        SetupError = string.Empty;

        try
        {
            if (IsExistingData)
            {
                Log.Information("Unlocking existing data...");
                _authService.UnlockApp(MasterPassword);
                Log.Information("Existing data unlocked successfully");
            }
            else
            {
                Log.Information("Initializing new master password...");
                _authService.InitializeAuth(MasterPassword);
                Log.Information("Master password initialized successfully");
            }

            // Cache password for seamless workspace switching
            App.Services.GetService<IMasterPasswordCache>()?.Set(MasterPassword);

            SavePreferences();

            // For new installs, set up recovery before completing
            if (!IsExistingData)
            {
                SetupRecoveryMnemonic();
                return SetupStep.EmergencyKit;
            }

            return SetupStep.Complete;
        }
        catch (PrivStackException ex)
        {
            var errorMsg = IsExistingData
                ? "Incorrect password. Please try again."
                : $"Failed to initialize security: {ex.Message}";
            SetupError = errorMsg;
            Log.Error(ex, "Auth setup/unlock failed: {Error}", errorMsg);
            return SetupStep.Password;
        }
        catch (Exception ex)
        {
            SetupError = $"Setup failed: {ex.Message}";
            Log.Error(ex, "Setup failed");
            return SetupStep.Password;
        }
    }

    private SetupStep FinishSetup()
    {
        SetupCompleted?.Invoke(this, EventArgs.Empty);
        return SetupStep.Complete;
    }

    private void SavePreferences()
    {
        var settingsDir = DataPaths.BaseDir;
        Directory.CreateDirectory(settingsDir);

        #pragma warning disable CS0618 // Obsolete fields kept for backward compat
        var settings = new UserSettings
        {
            DisplayName = DisplayName,
            Theme = SelectedTheme?.Theme.ToString() ?? "Dark",
            SetupComplete = true,
            DataDirectoryType = SelectedDirectoryType.ToString(),
            CustomDataDirectory = SelectedDirectoryType == DataDirectoryType.Custom ? CustomDataDirectory : null
        };
        #pragma warning restore CS0618

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), json);

        _appSettings.Settings.UserDisplayName = DisplayName;
        _appSettings.Save();
    }

    private string HandleExpiredLicenseError()
    {
        IsSubscriptionExpired = true;
        ExpiredMessage = "Your license has expired.";
        return "This license has expired";
    }

    private static string FormatLicensePlan(string plan) => plan.ToLower() switch
    {
        "trial" => "Trial",
        "monthly" => "Monthly",
        "annual" => "Annual",
        "perpetual" => "Perpetual",
        _ => plan
    };

    public static bool IsSetupComplete()
    {
        var settingsPath = Path.Combine(DataPaths.BaseDir, "settings.json");

        if (!File.Exists(settingsPath)) return false;

        try
        {
            var json = File.ReadAllText(settingsPath);
            var settings = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json);
            return settings?.SetupComplete ?? false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// User settings stored locally.
/// </summary>
public record UserSettings
{
    public string DisplayName { get; init; } = string.Empty;
    public string Theme { get; init; } = "Dark";
    public bool SetupComplete { get; init; }

    [Obsolete("Storage location is now per-workspace. Kept for deserialization backward compat.")]
    public string DataDirectoryType { get; init; } = "Default";

    [Obsolete("Storage location is now per-workspace. Kept for deserialization backward compat.")]
    public string? CustomDataDirectory { get; init; }
}
