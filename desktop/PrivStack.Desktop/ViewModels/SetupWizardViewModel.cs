using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Wizard steps for the first-run setup.
/// </summary>
public enum SetupStep
{
    Welcome,
    Workspace,   // Collects workspace name, display name, theme, and accessibility
    DataDirectory,
    License,
    Password,
    Complete
}

/// <summary>
/// Type of data storage location.
/// </summary>
public enum DataDirectoryType
{
    Default,
    Custom,
    GoogleDrive,
    ICloud
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
    private CancellationTokenSource? _oauthCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeStep))]
    [NotifyPropertyChangedFor(nameof(IsDataDirectoryStep))]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceStep))]
    [NotifyPropertyChangedFor(nameof(IsLicenseStep))]
    [NotifyPropertyChangedFor(nameof(IsPasswordStep))]
    [NotifyPropertyChangedFor(nameof(IsCompleteStep))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    [NotifyPropertyChangedFor(nameof(StepNumber))]
    [NotifyPropertyChangedFor(nameof(ProgressWidth))]
    private SetupStep _currentStep = SetupStep.Welcome;

    // Step visibility
    public bool IsWelcomeStep => CurrentStep == SetupStep.Welcome;
    public bool IsDataDirectoryStep => CurrentStep == SetupStep.DataDirectory;
    public bool IsWorkspaceStep => CurrentStep == SetupStep.Workspace;
    public bool IsLicenseStep => CurrentStep == SetupStep.License;
    public bool IsPasswordStep => CurrentStep == SetupStep.Password;
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

    // OAuth login properties
    [ObservableProperty]
    private bool _isWaitingForBrowser;

    [ObservableProperty]
    private string _loginError = string.Empty;

    // Data Directory step
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    [NotifyPropertyChangedFor(nameof(IsCustomDirectorySelected))]
    private DataDirectoryType _selectedDirectoryType = DataDirectoryType.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    private string _customDataDirectory = string.Empty;

    public bool IsGoogleDriveAvailable => !string.IsNullOrEmpty(GetGoogleDrivePath());
    public bool IsICloudAvailable => !string.IsNullOrEmpty(GetICloudPath());
    public bool IsCustomDirectorySelected => SelectedDirectoryType == DataDirectoryType.Custom;

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
        DataDirectoryType.Custom => CustomDataDirectory,
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
    public bool CanGoBack => CurrentStep != SetupStep.Welcome && CurrentStep != SetupStep.Complete;

    public bool CanGoNext => CurrentStep switch
    {
        SetupStep.Welcome => true,
        SetupStep.DataDirectory => SelectedDirectoryType != DataDirectoryType.Custom ||
                                   !string.IsNullOrWhiteSpace(CustomDataDirectory),
        SetupStep.Workspace => !string.IsNullOrWhiteSpace(WorkspaceName) && !string.IsNullOrWhiteSpace(DisplayName) && SelectedTheme != null,
        SetupStep.License => IsLicenseValid,
        SetupStep.Password => IsExistingData
            ? !string.IsNullOrWhiteSpace(MasterPassword)
            : !string.IsNullOrWhiteSpace(MasterPassword) && MasterPassword.Length >= 8 && PasswordsMatch,
        SetupStep.Complete => true,
        _ => false
    };

    public string NextButtonText => CurrentStep switch
    {
        SetupStep.Password => "Complete Setup",
        SetupStep.Complete => "Get Started",
        _ => "Next"
    };

    public int StepNumber => CurrentStep switch
    {
        SetupStep.Welcome => 1,
        SetupStep.Workspace => 2,
        SetupStep.DataDirectory => 3,
        SetupStep.License => 4,
        SetupStep.Password => 5,
        SetupStep.Complete => 6,
        _ => 0
    };

    public int TotalSteps => 6;

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
        OAuthLoginService oauthService)
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
            SetupStep.Workspace => SetupStep.Welcome,
            SetupStep.DataDirectory => SetupStep.Workspace,
            SetupStep.License => SetupStep.DataDirectory,
            SetupStep.Password => SetupStep.License,
            _ => CurrentStep
        };
    }

    [RelayCommand]
    private void GoNext()
    {
        if (!CanGoNext) return;

        CurrentStep = CurrentStep switch
        {
            SetupStep.Welcome => SetupStep.Workspace,
            SetupStep.Workspace => SetupStep.DataDirectory,
            SetupStep.DataDirectory => InitializeServiceAndContinue(),
            SetupStep.License => SetupStep.Password,
            SetupStep.Password => CompleteSetup(),
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
                LoginError = status == "expired"
                    ? "Your subscription has expired. Renew at privstack.io"
                    : "Your subscription has been cancelled. Resubscribe at privstack.io";
                return;
            }

            // Persist tokens for authenticated API calls (e.g., update downloads)
            _appSettings.Settings.AccessToken = tokenResult.AccessToken;
            _appSettings.Settings.RefreshToken = tokenResult.RefreshToken;
            _appSettings.SaveDebounced();
            Log.Information("[OAuth] Persisted access and refresh tokens");

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
                LicenseError = licenseInfo.Status == "readonly"
                    ? "This license is in read-only mode (grace period expired)"
                    : "This license key has expired";
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
                PrivStackError.LicenseExpired => "This license has expired",
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

    [RelayCommand]
    private void SelectDirectoryType(DataDirectoryType type)
    {
        SelectedDirectoryType = type;
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

    private static string? GetGoogleDrivePath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var cloudStorage = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "CloudStorage");

            if (Directory.Exists(cloudStorage))
            {
                var googleDirs = Directory.GetDirectories(cloudStorage, "GoogleDrive-*");
                if (googleDirs.Length > 0)
                {
                    var myDrive = Path.Combine(googleDirs[0], "My Drive");
                    if (Directory.Exists(myDrive))
                        return myDrive;
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new[]
            {
                Path.Combine(userProfile, "Google Drive"),
                Path.Combine(userProfile, "My Drive"),
                Path.Combine(userProfile, "GoogleDrive")
            };

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static string? GetICloudPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var iCloudPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Mobile Documents", "com~apple~CloudDocs");

            if (Directory.Exists(iCloudPath))
                return iCloudPath;
        }
        else if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var iCloudPath = Path.Combine(userProfile, "iCloudDrive");

            if (Directory.Exists(iCloudPath))
                return iCloudPath;
        }

        return null;
    }

    private SetupStep InitializeServiceAndContinue()
    {
        SetupError = string.Empty;

        try
        {
            _appSettings.Settings.DataDirectoryType = SelectedDirectoryType.ToString();
            if (SelectedDirectoryType != DataDirectoryType.Default)
            {
                _appSettings.Settings.CustomDataDirectory = EffectiveDataDirectory;
            }
            else
            {
                _appSettings.Settings.CustomDataDirectory = null;
            }
            _appSettings.Save();

            var dataDir = EffectiveDataDirectory;
            var dbPath = Path.Combine(dataDir, "data.duckdb");

            var dbExists = File.Exists(dbPath);
            Log.Information("Data directory: {Path}, DB exists: {Exists}", dataDir, dbExists);

            if (!_runtime.IsInitialized)
            {
                Log.Information("Initializing service at user-chosen directory: {Path}", dataDir);
                Directory.CreateDirectory(dataDir);
                _runtime.Initialize(dbPath);
                Log.Information("Service initialized successfully at: {Path}", dbPath);

                LoadDeviceInfo();
            }

            IsExistingData = _authService.IsAuthInitialized();
            Log.Information("Auth initialized (existing data): {IsExisting}", IsExistingData);

            // Now create the workspace (needs service initialized first)
            return CreateWorkspaceAndContinue();
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
                SetupError = $"Failed to initialize data directory: {ex.Message}";
            }
            return SetupStep.DataDirectory;
        }
    }

    private SetupStep CreateWorkspaceAndContinue()
    {
        try
        {
            var name = WorkspaceName.Trim();
            _workspaceService.CreateWorkspace(name);
            Log.Information("Initial workspace created: {Name}", name);

            var dbPath = _workspaceService.GetActiveDataPath();
            var dir = Path.GetDirectoryName(dbPath)!;
            Directory.CreateDirectory(dir);

            if (_runtime.IsInitialized)
            {
                _runtime.Shutdown();
            }
            _runtime.Initialize(dbPath);

            IsExistingData = _authService.IsAuthInitialized();

            return SetupStep.License;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create workspace");
            SetupError = $"Failed to create workspace: {ex.Message}";
            return SetupStep.Workspace;
        }
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

            SavePreferences();

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

        var settings = new UserSettings
        {
            DisplayName = DisplayName,
            Theme = SelectedTheme?.Theme.ToString() ?? "Dark",
            SetupComplete = true,
            DataDirectoryType = SelectedDirectoryType.ToString(),
            CustomDataDirectory = SelectedDirectoryType == DataDirectoryType.Custom ? CustomDataDirectory : null
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), json);

        _appSettings.Settings.UserDisplayName = DisplayName;
        _appSettings.Settings.DataDirectoryType = SelectedDirectoryType.ToString();

        if (SelectedDirectoryType != DataDirectoryType.Default)
        {
            _appSettings.Settings.CustomDataDirectory = EffectiveDataDirectory;
        }
        else
        {
            _appSettings.Settings.CustomDataDirectory = null;
        }

        _appSettings.Save();
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
    public string DataDirectoryType { get; init; } = "Default";
    public string? CustomDataDirectory { get; init; }
}
