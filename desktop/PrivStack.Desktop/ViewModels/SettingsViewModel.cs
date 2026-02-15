using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Connections;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Represents a theme option for display in the UI.
/// Custom themes use CustomThemeId; built-in themes use Theme.
/// </summary>
public record ThemeOption(AppTheme Theme, string DisplayName, string? CustomThemeId = null)
{
    public bool IsCustom => CustomThemeId != null;
}

/// <summary>
/// Represents a Whisper model option for speech-to-text.
/// </summary>
public record WhisperModelOption(string Id, string DisplayName, string SizeText);


/// <summary>
/// Represents a font family option for accessibility.
/// </summary>
public record FontFamilyOption(string Key, string DisplayName, string Description);

/// <summary>
/// Represents a lockout option for sensitive data.
/// </summary>
public record LockoutOption(string Display, int Minutes);

/// <summary>
/// Represents a storage location option for data directory.
/// </summary>
public record StorageLocationOption(DataDirectoryType Type, string Display, string Icon);

/// <summary>
/// Represents a plugin item for the settings panel.
/// </summary>
public partial class PluginSettingsItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string? _icon;

    [ObservableProperty]
    private PluginCategory _category;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _canDisable;

    [ObservableProperty]
    private bool _isExperimental;

    [ObservableProperty]
    private bool _isHardLocked;

    [ObservableProperty]
    private string? _hardLockedReason;

    [ObservableProperty]
    private ObservableCollection<PluginPermissionItem> _permissions = [];

    [ObservableProperty]
    private bool _isExpanded;

    // ========================================
    // Resource Metrics Properties
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
    [NotifyPropertyChangedFor(nameof(MemoryUsagePercent))]
    private PluginResourceMetrics? _metrics;

    /// <summary>
    /// Memory usage display string (e.g., "28.8 MB / 64 MB").
    /// </summary>
    public string MemoryDisplay => Metrics?.MemoryDisplay ?? "N/A";

    /// <summary>
    /// Memory usage as a percentage (0 to 100).
    /// </summary>
    public double MemoryUsagePercent => Metrics?.MemoryUsagePercent ?? 0;

    /// <summary>
    /// Disk usage display string (e.g., "142 entities (2.3 MB)").
    /// </summary>
    public string DiskDisplay => Metrics?.DiskDisplay ?? "N/A";

    /// <summary>
    /// Fuel usage as a percentage (0 to 100) based on last call.
    /// </summary>
    public double FuelUsagePercent => Metrics?.FuelUsagePercent ?? 0;

    /// <summary>
    /// Fuel usage display string for last call.
    /// </summary>
    public string FuelDisplay => Metrics?.FuelDisplay ?? "N/A";

    /// <summary>
    /// Fuel average display string over last 1000 calls.
    /// </summary>
    public string FuelAverageDisplay => Metrics?.FuelAverageDisplay ?? "No history";

    /// <summary>
    /// Fuel peak display string.
    /// </summary>
    public string FuelPeakDisplay => Metrics?.FuelPeakDisplay ?? "N/A";

    /// <summary>
    /// Gets whether this plugin can be toggled by the user.
    /// </summary>
    public bool CanToggle => CanDisable && !IsHardLocked;

    /// <summary>
    /// Gets the status text for display.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (IsHardLocked) return "Coming Soon";
            if (IsExperimental) return "Experimental";
            if (!CanDisable) return "Core";
            return IsEnabled ? "Enabled" : "Disabled";
        }
    }
}

/// <summary>
/// Represents a single permission for a plugin in the settings panel.
/// </summary>
public partial class PluginPermissionItem : ObservableObject
{
    [ObservableProperty]
    private string _permissionKey = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isGranted;

    [ObservableProperty]
    private bool _isTier1;
}

/// <summary>
/// Backup frequency options.
/// </summary>
public enum BackupFrequency
{
    Manual,
    Hourly,
    Daily,
    Weekly
}

/// <summary>
/// Backup type options.
/// </summary>
public enum BackupType
{
    OneTime,
    Rolling
}

/// <summary>
/// ViewModel for the application settings panel.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly IBackupService _backupService;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IThemeService _themeService;
    private readonly IFontScaleService _fontScaleService;
    private readonly ISensitiveLockService _sensitiveLockService;
    private readonly IDialogService _dialogService;
    private readonly IAuthService _authService;
    private readonly SeedDataService _seedDataService;
    private Avalonia.Threading.DispatcherTimer? _metricsRefreshTimer;

    private readonly ISystemNotificationService _notificationService;
    private readonly CustomThemeStore _customThemeStore;

    public ThemeEditorViewModel ThemeEditor { get; }
    public ConnectionsViewModel Connections { get; }
    public CloudSyncSettingsViewModel CloudSync { get; }

    public SettingsViewModel(
        IAppSettingsService settingsService,
        IBackupService backupService,
        IPluginRegistry pluginRegistry,
        IThemeService themeService,
        IFontScaleService fontScaleService,
        ISensitiveLockService sensitiveLockService,
        IDialogService dialogService,
        IAuthService authService,
        SeedDataService seedDataService,
        ISystemNotificationService notificationService,
        CustomThemeStore customThemeStore,
        ConnectionService connectionService,
        GitHubDeviceFlowService gitHubDeviceFlowService,
        ICloudSyncService cloudSyncService,
        IWorkspaceService workspaceService,
        OAuthLoginService oauthLoginService,
        PrivStackApiClient apiClient,
        IMasterPasswordCache passwordCache)
    {
        _settingsService = settingsService;
        _backupService = backupService;
        _pluginRegistry = pluginRegistry;
        _themeService = themeService;
        _fontScaleService = fontScaleService;
        _sensitiveLockService = sensitiveLockService;
        _dialogService = dialogService;
        _authService = authService;
        _seedDataService = seedDataService;
        _notificationService = notificationService;
        _customThemeStore = customThemeStore;

        ThemeEditor = new ThemeEditorViewModel(themeService, customThemeStore, settingsService);
        ThemeEditor.EditorClosed += OnThemeEditorClosed;
        Connections = new ConnectionsViewModel(connectionService, gitHubDeviceFlowService);
        CloudSync = new CloudSyncSettingsViewModel(cloudSyncService, workspaceService,
            oauthLoginService, apiClient, settingsService, passwordCache);

        LoadPluginItems();
        LoadSettings();
    }

    [ObservableProperty]
    private string _userDisplayName = string.Empty;

    // ========================================
    // Profile Image
    // ========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileImage))]
    [NotifyPropertyChangedFor(nameof(ProfileImageBitmap))]
    private string? _profileImagePath;

    private Bitmap? _cachedProfileBitmap;

    public bool HasProfileImage => !string.IsNullOrEmpty(ProfileImagePath);

    public Bitmap? ProfileImageBitmap
    {
        get
        {
            if (string.IsNullOrEmpty(ProfileImagePath))
            {
                _cachedProfileBitmap?.Dispose();
                _cachedProfileBitmap = null;
                return null;
            }

            var fullPath = Path.Combine(Services.DataPaths.BaseDir, ProfileImagePath);
            if (!File.Exists(fullPath))
            {
                _cachedProfileBitmap?.Dispose();
                _cachedProfileBitmap = null;
                return null;
            }

            // Only reload if we don't have a cached bitmap
            if (_cachedProfileBitmap == null)
            {
                try
                {
                    _cachedProfileBitmap = new Bitmap(fullPath);
                }
                catch
                {
                    _cachedProfileBitmap = null;
                }
            }

            return _cachedProfileBitmap;
        }
    }

    public string UserInitial => string.IsNullOrEmpty(UserDisplayName)
        ? "U"
        : UserDisplayName[0].ToString().ToUpperInvariant();

    public void SetProfileImage(string sourcePath)
    {
        var destPath = Path.Combine(Services.DataPaths.BaseDir, "profile-image.png");

        try
        {
            File.Copy(sourcePath, destPath, overwrite: true);
        }
        catch
        {
            return;
        }

        // Dispose old cached bitmap before updating path
        _cachedProfileBitmap?.Dispose();
        _cachedProfileBitmap = null;

        ProfileImagePath = "profile-image.png";
        _settingsService.Settings.ProfileImagePath = "profile-image.png";
        _settingsService.SaveDebounced();
        _settingsService.NotifyProfileChanged();
    }

    [RelayCommand]
    private void RemoveProfileImage()
    {
        if (string.IsNullOrEmpty(ProfileImagePath)) return;

        var fullPath = Path.Combine(Services.DataPaths.BaseDir, ProfileImagePath);
        try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* best effort */ }

        _cachedProfileBitmap?.Dispose();
        _cachedProfileBitmap = null;

        ProfileImagePath = null;
        _settingsService.Settings.ProfileImagePath = null;
        _settingsService.SaveDebounced();
        _settingsService.NotifyProfileChanged();
    }

    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    [NotifyPropertyChangedFor(nameof(IsCustomDirectorySelected))]
    private DataDirectoryType _selectedDirectoryType = DataDirectoryType.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataDirectoryDisplay))]
    private string _dataDirectory = string.Empty;

    [ObservableProperty]
    private string _backupDirectory = string.Empty;

    /// <summary>
    /// Whether Google Drive is available on this system.
    /// </summary>
    public bool IsGoogleDriveAvailable => !string.IsNullOrEmpty(GetGoogleDrivePath());

    /// <summary>
    /// Whether iCloud is available on this system.
    /// </summary>
    public bool IsICloudAvailable => !string.IsNullOrEmpty(GetICloudPath());

    /// <summary>
    /// Whether custom directory option is selected.
    /// </summary>
    public bool IsCustomDirectorySelected => SelectedDirectoryType == DataDirectoryType.Custom;

    /// <summary>
    /// Display path for the current data directory selection.
    /// </summary>
    public string DataDirectoryDisplay => SelectedDirectoryType switch
    {
        DataDirectoryType.Default => GetDefaultDataPath(),
        DataDirectoryType.Custom => string.IsNullOrEmpty(DataDirectory) ? "Select a folder..." : DataDirectory,
        DataDirectoryType.GoogleDrive => GetGoogleDrivePath() ?? "Not available",
        DataDirectoryType.ICloud => GetICloudPath() ?? "Not available",
        _ => GetDefaultDataPath()
    };

    /// <summary>
    /// Available storage location options.
    /// </summary>
    public List<StorageLocationOption> StorageLocationOptions { get; } =
    [
        new(DataDirectoryType.Default, "Default (Local)", "📁"),
        new(DataDirectoryType.GoogleDrive, "Google Drive", "G"),
        new(DataDirectoryType.ICloud, "iCloud Drive", "☁️"),
        new(DataDirectoryType.Custom, "Custom Location", "📂")
    ];

    [ObservableProperty]
    private bool _isMigratingStorage;

    [ObservableProperty]
    private double _migrationProgress;

    [ObservableProperty]
    private string _migrationStatus = string.Empty;

    [ObservableProperty]
    private BackupFrequency _selectedBackupFrequency = BackupFrequency.Daily;

    [ObservableProperty]
    private BackupType _selectedBackupType = BackupType.Rolling;

    public bool IsRollingBackup
    {
        get => SelectedBackupType == BackupType.Rolling;
        set { if (value) SelectedBackupType = BackupType.Rolling; }
    }

    public bool IsOneTimeBackup
    {
        get => SelectedBackupType == BackupType.OneTime;
        set { if (value) SelectedBackupType = BackupType.OneTime; }
    }

    [ObservableProperty]
    private int _maxBackups = 7;

    [ObservableProperty]
    private string _version = string.Empty;

    public string CoreVersion => $"v{Native.PrivStackService.Version}";

    public string ShellVersion
    {
        get
        {
            var asm = typeof(SettingsViewModel).Assembly;
            var ver = asm.GetName().Version;
            return ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "unknown";
        }
    }

    public string PlatformInfo
    {
        get
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
                   : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
                   : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
                   : "Unknown";
            var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            var dotnet = $".NET {Environment.Version.Major}.{Environment.Version.Minor}";
            var avalonia = "Avalonia 11.3";
            var rust = "Rust 1.85";
            return $"{os} ({arch}) · {dotnet} · {avalonia} · {rust}";
        }
    }

    [ObservableProperty]
    private bool _isChangingDataDirectory;

    [ObservableProperty]
    private bool _isBackingUp;

    [ObservableProperty]
    private string? _backupStatus;

    public BackupFrequency[] BackupFrequencies { get; } = Enum.GetValues<BackupFrequency>();
    public BackupType[] BackupTypes { get; } = Enum.GetValues<BackupType>();

    /// <summary>
    /// Available lockout options for sensitive data.
    /// </summary>
    public List<LockoutOption> LockoutOptions { get; } =
    [
        new("1 minute", 1),
        new("2 minutes", 2),
        new("5 minutes (Default)", 5),
        new("10 minutes", 10),
        new("15 minutes", 15),
        new("30 minutes", 30),
        new("Never", 0)
    ];

    [ObservableProperty]
    private LockoutOption? _selectedLockoutOption;

    // ========================================
    // Logout & Change Password
    // ========================================

    /// <summary>
    /// Fired when the user requests to log out (lock the app).
    /// </summary>
    public event EventHandler? LogoutRequested;

    [ObservableProperty]
    private bool _isChangePasswordVisible;

    [ObservableProperty]
    private string _currentPassword = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _confirmNewPassword = string.Empty;

    [ObservableProperty]
    private bool _isChangingPassword;

    [ObservableProperty]
    private string? _changePasswordStatus;

    [ObservableProperty]
    private bool _changePasswordSuccess;

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Lock App",
            "This will lock the app and clear encryption keys from memory. You'll need to re-enter your master password to continue.",
            "Lock");

        if (!confirmed) return;

        _authService.LockApp();
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleChangePassword()
    {
        IsChangePasswordVisible = !IsChangePasswordVisible;
        if (!IsChangePasswordVisible)
        {
            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmNewPassword = string.Empty;
            ChangePasswordStatus = null;
            ChangePasswordSuccess = false;
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (IsChangingPassword) return;

        ChangePasswordStatus = null;
        ChangePasswordSuccess = false;

        if (string.IsNullOrEmpty(CurrentPassword))
        {
            ChangePasswordStatus = "Current password is required.";
            return;
        }

        if (NewPassword.Length < 8)
        {
            ChangePasswordStatus = "New password must be at least 8 characters.";
            return;
        }

        if (NewPassword != ConfirmNewPassword)
        {
            ChangePasswordStatus = "New passwords do not match.";
            return;
        }

        if (!_authService.ValidateMasterPassword(CurrentPassword))
        {
            ChangePasswordStatus = "Current password is incorrect.";
            return;
        }

        IsChangingPassword = true;
        ChangePasswordStatus = "Re-encrypting data — do not close the app...";

        try
        {
            await Task.Run(() => _authService.ChangeAppPassword(CurrentPassword, NewPassword));

            ChangePasswordSuccess = true;
            ChangePasswordStatus = "Password changed successfully.";
            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmNewPassword = string.Empty;
        }
        catch (Exception ex)
        {
            ChangePasswordStatus = $"Failed to change password: {ex.Message}";
        }
        finally
        {
            IsChangingPassword = false;
        }
    }

    /// <summary>
    /// List of plugins for the settings panel.
    /// </summary>
    public ObservableCollection<PluginSettingsItem> PluginItems { get; } = [];

    /// <summary>
    /// Whether experimental plugins are enabled globally.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExperimentalPluginsStatusText))]
    private bool _experimentalPluginsEnabled;

    // ========================================
    // Plugin Sandbox Status (P5.7)
    // ========================================

    /// <summary>
    /// Number of Wasm plugins currently loaded in the sandbox.
    /// </summary>
    public int WasmPluginCount => NativeLib.PluginCount();

    /// <summary>
    /// Summary text for the plugin sandbox status.
    /// </summary>
    public string SandboxStatusText => WasmPluginCount switch
    {
        0 => "No sandboxed plugins loaded",
        1 => "1 sandboxed plugin running",
        _ => $"{WasmPluginCount} sandboxed plugins running"
    };

    /// <summary>
    /// Whether any Wasm plugins are loaded.
    /// </summary>
    public bool HasWasmPlugins => WasmPluginCount > 0;

    // ========================================
    // Manage Plugins Dialog
    // ========================================

    /// <summary>
    /// Whether the manage plugins dialog is open.
    /// </summary>
    [ObservableProperty]
    private bool _isManagePluginsDialogOpen;

    /// <summary>
    /// Opens the manage plugins dialog and starts metrics refresh.
    /// </summary>
    [RelayCommand]
    private void OpenManagePluginsDialog()
    {
        IsManagePluginsDialogOpen = true;
        RefreshPluginMetrics();
        StartMetricsRefresh();
    }

    /// <summary>
    /// Closes the manage plugins dialog and stops metrics refresh.
    /// </summary>
    [RelayCommand]
    private void CloseManagePluginsDialog()
    {
        IsManagePluginsDialogOpen = false;
        StopMetricsRefresh();
    }

    /// <summary>
    /// Manually refresh plugin metrics.
    /// </summary>
    [RelayCommand]
    private void RefreshPluginMetrics()
    {
        try
        {
            var metricsPtr = NativeLib.PluginGetAllMetrics();
            if (metricsPtr == nint.Zero) return;

            var json = Marshal.PtrToStringUTF8(metricsPtr);
            NativeLib.FreeString(metricsPtr);

            if (string.IsNullOrEmpty(json)) return;

            var allMetrics = JsonSerializer.Deserialize<List<PluginResourceMetrics>>(json);
            if (allMetrics == null) return;

            // Update metrics for each plugin item
            foreach (var metrics in allMetrics)
            {
                if (metrics.PluginId == null) continue;

                var pluginItem = PluginItems.FirstOrDefault(p => p.Id == metrics.PluginId);
                if (pluginItem != null)
                {
                    pluginItem.Metrics = metrics;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to refresh plugin metrics: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the metrics refresh timer (2 second interval for memory/fuel).
    /// </summary>
    private void StartMetricsRefresh()
    {
        if (_metricsRefreshTimer != null) return;

        _metricsRefreshTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _metricsRefreshTimer.Tick += (_, _) => RefreshPluginMetrics();
        _metricsRefreshTimer.Start();
    }

    /// <summary>
    /// Stops the metrics refresh timer.
    /// </summary>
    private void StopMetricsRefresh()
    {
        _metricsRefreshTimer?.Stop();
        _metricsRefreshTimer = null;
    }

    // ========================================
    // Enterprise Admin (P5.8)
    // ========================================

    /// <summary>
    /// Whether a policy.toml file is loaded and active.
    /// </summary>
    public bool IsPolicyActive => File.Exists(PolicyFilePath);

    /// <summary>
    /// Path to the enterprise policy file.
    /// </summary>
    public static string PolicyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".privstack", "policy.toml");

    /// <summary>
    /// Display text for the policy status.
    /// </summary>
    public string PolicyStatusText => IsPolicyActive
        ? "Enterprise policy active"
        : "No enterprise policy configured";

    /// <summary>
    /// Font scale multiplier for accessibility (0.8 to 1.5).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontScaleDisplayText))]
    [NotifyPropertyChangedFor(nameof(FontScalePercentText))]
    private double _fontScaleMultiplier = 1.0;

    /// <summary>
    /// Gets the display text for the current font scale.
    /// </summary>
    public string FontScaleDisplayText => FontScaleMultiplier switch
    {
        <= 0.85 => "Small",
        <= 0.95 => "Medium",
        <= 1.05 => "Default",
        <= 1.15 => "Large",
        <= 1.30 => "Extra Large",
        _ => "Maximum"
    };

    /// <summary>
    /// Gets the percentage text for the current font scale.
    /// </summary>
    public string FontScalePercentText => $"{FontScaleMultiplier * 100:F0}%";

    /// <summary>
    /// Available font family options for accessibility.
    /// </summary>
    public List<FontFamilyOption> AvailableFontFamilies { get; } =
    [
        new("system", "Inter (Default)", "Clean, modern UI font designed for screens"),
        new("ibm-plex-sans", "IBM Plex Sans", "Professional, open-source corporate typeface"),
        new("lexend", "Lexend", "Designed to improve reading proficiency"),
        new("nunito", "Nunito", "Rounded, friendly sans-serif"),
        new("atkinson", "Atkinson Hyperlegible", "Designed for low vision (Braille Institute)"),
        new("opendyslexic", "OpenDyslexic", "Designed for readers with dyslexia"),
    ];

    /// <summary>
    /// The currently selected font family option.
    /// </summary>
    [ObservableProperty]
    private FontFamilyOption? _selectedFontFamily;

    // ========================================
    // Notification Properties
    // ========================================

    [ObservableProperty]
    private bool _autoCheckForUpdates = true;

    [ObservableProperty]
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private bool _notificationSoundEnabled = true;

    [ObservableProperty]
    private string? _testNotificationStatus;

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        _settingsService.Settings.AutoCheckForUpdates = value;
        _settingsService.SaveDebounced();
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationsEnabled = value;
        _settingsService.SaveDebounced();
    }

    partial void OnNotificationSoundEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationSoundEnabled = value;
        _settingsService.SaveDebounced();
    }

    [RelayCommand]
    private async Task TestNotificationAsync()
    {
        TestNotificationStatus = "Sending...";
        var success = await _notificationService.SendNotificationAsync(
            "PrivStack",
            "Notifications are working!",
            "Test Notification",
            NotificationSoundEnabled);
        TestNotificationStatus = success ? "Notification sent!" : "Failed to send notification";
        await Task.Delay(3000);
        TestNotificationStatus = null;
    }

    // ========================================
    // Speech-to-Text Properties
    // ========================================

    /// <summary>
    /// Whether speech-to-text is enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeechWarningMessage))]
    [NotifyPropertyChangedFor(nameof(HasSpeechWarning))]
    private bool _speechToTextEnabled = true;

    /// <summary>
    /// Available audio input devices.
    /// </summary>
    public ObservableCollection<AudioInputDevice> AudioInputDevices { get; } = [];

    /// <summary>
    /// The currently selected audio input device. Null means system default.
    /// </summary>
    [ObservableProperty]
    private AudioInputDevice? _selectedAudioInputDevice;

    /// <summary>
    /// When enabled, Whisper uses beam search decoding for higher accuracy at the cost of speed.
    /// </summary>
    [ObservableProperty]
    private bool _whisperBeamSearch;

    /// <summary>
    /// Warning message shown when speech-to-text is enabled but model not downloaded.
    /// </summary>
    public string? SpeechWarningMessage
    {
        get
        {
            if (!SpeechToTextEnabled) return null;
            if (SelectedWhisperModel == null) return "Please select a model";
            if (!WhisperModelManager.Instance.IsModelDownloaded(SelectedWhisperModel.Id))
                return "Model required - click Download to enable speech-to-text";
            return null;
        }
    }

    /// <summary>
    /// Whether there's a warning to show for speech-to-text.
    /// </summary>
    public bool HasSpeechWarning => !string.IsNullOrEmpty(SpeechWarningMessage);

    /// <summary>
    /// Available Whisper models for selection.
    /// </summary>
    public ObservableCollection<WhisperModelOption> WhisperModels { get; } =
    [
        new("tiny.en", "Tiny (English)", "~75 MB"),
        new("base.en", "Base (English)", "~142 MB"),
        new("small.en", "Small (English)", "~466 MB"),
        new("tiny", "Tiny (Multilingual)", "~75 MB"),
        new("base", "Base (Multilingual)", "~142 MB"),
        new("small", "Small (Multilingual)", "~466 MB"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText))]
    [NotifyPropertyChangedFor(nameof(CanDownloadModel))]
    [NotifyPropertyChangedFor(nameof(DownloadButtonText))]
    private WhisperModelOption? _selectedWhisperModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadProgressText))]
    private double _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText))]
    [NotifyPropertyChangedFor(nameof(CanDownloadModel))]
    [NotifyPropertyChangedFor(nameof(DownloadButtonText))]
    private bool _isDownloadingModel;

    /// <summary>
    /// Gets the keyboard shortcut text based on platform.
    /// </summary>
    public string SpeechShortcutText => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Cmd + M" : "Ctrl + M";

    /// <summary>
    /// Gets the model status text.
    /// </summary>
    public string ModelStatusText
    {
        get
        {
            if (SelectedWhisperModel == null) return "No model selected";
            var isDownloaded = WhisperModelManager.Instance.IsModelDownloaded(SelectedWhisperModel.Id);
            return isDownloaded ? "Downloaded and ready" : "Not downloaded";
        }
    }

    /// <summary>
    /// Gets whether the download button should be enabled.
    /// </summary>
    public bool CanDownloadModel
    {
        get
        {
            if (SelectedWhisperModel == null || IsDownloadingModel) return false;
            return !WhisperModelManager.Instance.IsModelDownloaded(SelectedWhisperModel.Id);
        }
    }

    /// <summary>
    /// Gets the download button text.
    /// </summary>
    public string DownloadButtonText
    {
        get
        {
            if (SelectedWhisperModel == null) return "Download";
            var isDownloaded = WhisperModelManager.Instance.IsModelDownloaded(SelectedWhisperModel.Id);
            return isDownloaded ? "Downloaded" : "Download";
        }
    }

    /// <summary>
    /// Gets the download progress text.
    /// </summary>
    public string DownloadProgressText => $"{DownloadProgress:F0}%";

    /// <summary>
    /// All available themes for selection (built-in + custom).
    /// </summary>
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

    /// <summary>
    /// Opens the theme editor dialog for the current theme.
    /// </summary>
    [RelayCommand]
    private void OpenThemeEditor()
    {
        ThemeEditor.OpenForCurrentTheme();
    }

    private void OnThemeEditorClosed(bool saved)
    {
        if (saved)
        {
            RefreshAvailableThemes();
        }
    }

    /// <summary>
    /// Refreshes the available themes list to include custom themes.
    /// </summary>
    public void RefreshAvailableThemes()
    {
        // Preserve selection
        var currentId = _themeService.CurrentCustomThemeId;

        // Remove existing custom themes
        for (int i = AvailableThemes.Count - 1; i >= 0; i--)
        {
            if (AvailableThemes[i].IsCustom)
                AvailableThemes.RemoveAt(i);
        }

        // Add custom themes from store
        var customThemes = _customThemeStore.LoadAll();
        foreach (var ct in customThemes.OrderBy(t => t.Name))
        {
            var basedOn = Enum.TryParse<AppTheme>(ct.BasedOn, out var parsed) ? parsed : AppTheme.Dark;
            AvailableThemes.Add(new ThemeOption(basedOn, ct.Name, ct.Id));
        }

        // Restore selection
        if (currentId != null)
        {
            SelectedTheme = AvailableThemes.FirstOrDefault(t => t.CustomThemeId == currentId)
                          ?? AvailableThemes[0];
        }
        else
        {
            var currentTheme = _themeService.CurrentTheme;
            SelectedTheme = AvailableThemes.FirstOrDefault(t => !t.IsCustom && t.Theme == currentTheme)
                          ?? AvailableThemes[0];
        }
    }

    /// <summary>
    /// Gets the status text for experimental plugins toggle.
    /// </summary>
    public string ExperimentalPluginsStatusText =>
        ExperimentalPluginsEnabled ? "Experimental features are enabled" : "Enable to access experimental features";

    private void LoadPluginItems()
    {
        PluginItems.Clear();

        ExperimentalPluginsEnabled = _settingsService.Settings.ExperimentalPluginsEnabled;

        // Sort plugins alphabetically by name
        var sortedPlugins = _pluginRegistry.Plugins
            .OrderBy(p => p.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var plugin in sortedPlugins)
        {
            var meta = plugin.Metadata;
            var isEnabled = _pluginRegistry.IsPluginEnabled(meta.Id);

            var item = new PluginSettingsItem
            {
                Id = meta.Id,
                Name = meta.Name,
                Description = meta.Description,
                Icon = meta.Icon,
                Category = meta.Category,
                IsEnabled = isEnabled,
                CanDisable = meta.CanDisable,
                IsExperimental = meta.IsExperimental,
                IsHardLocked = meta.IsHardLocked,
                HardLockedReason = meta.HardLockedReason
            };

            // Populate permissions for this plugin
            PopulatePluginPermissions(item);

            PluginItems.Add(item);
        }
    }

    [RelayCommand]
    private void TogglePlugin(PluginSettingsItem? item)
    {
        if (item == null || !item.CanToggle) return;

        var newState = _pluginRegistry.TogglePlugin(item.Id);
        item.IsEnabled = newState;
        OnPropertyChanged(nameof(PluginItems));
    }

    [RelayCommand]
    private async Task ResetPluginAsync(PluginSettingsItem? item)
    {
        if (item == null) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Reset Plugin",
            $"This will erase all data for \"{item.Name}\" and restore it to its initial state. All permissions will be revoked. This cannot be undone.",
            "Reset");
        if (!confirmed) return;

        // Send __reset command to wipe plugin data and regenerate defaults
        var resultPtr = NativeLib.PluginSendCommand(item.Id, "__reset", "{}");
        if (resultPtr != nint.Zero)
            NativeLib.FreeString(resultPtr);

        // Reset permissions back to Tier 1 only (clears all user grants/denials)
        var wsConfig = _settingsService.GetWorkspacePluginConfig();
        wsConfig.PluginPermissions.Remove(item.Id);
        _settingsService.Save();

        // Push Tier1-only permissions to the runtime sandbox
        var permObj = new
        {
            granted = PluginRegistry.Tier1Permissions.ToList(),
            denied = Array.Empty<string>(),
            pending_jit = Array.Empty<string>()
        };
        NativeLib.PluginUpdatePermissions(item.Id, JsonSerializer.Serialize(permObj));

        // Refresh the permission checkboxes in the UI
        PopulatePluginPermissions(item);
        item.IsExpanded = false;

        // Evict cached ViewModel and re-navigate to force a full reload
        var mainVm = _pluginRegistry.GetMainViewModel();
        if (mainVm != null)
        {
            var plugin = _pluginRegistry.GetPlugin(item.Id);
            var navId = plugin?.NavigationItem?.Id;
            if (navId != null)
                await mainVm.ReloadPluginAsync(navId);
        }
    }

    [RelayCommand]
    private async Task UninstallPluginAsync(PluginSettingsItem? item)
    {
        if (item == null || !item.CanDisable) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Uninstall Plugin",
            $"This will permanently erase all data for \"{item.Name}\" and disable it. All permissions will be revoked. This cannot be undone.",
            "Uninstall");
        if (!confirmed) return;

        // Send __reset to wipe all plugin data
        var resultPtr = NativeLib.PluginSendCommand(item.Id, "__reset", "{}");
        if (resultPtr != nint.Zero)
            NativeLib.FreeString(resultPtr);

        // Clear saved permissions
        var wsConfig = _settingsService.GetWorkspacePluginConfig();
        wsConfig.PluginPermissions.Remove(item.Id);
        _settingsService.Save();

        // Disable the plugin
        if (item.IsEnabled)
        {
            _pluginRegistry.TogglePlugin(item.Id);
            item.IsEnabled = false;
        }
        OnPropertyChanged(nameof(PluginItems));
    }

    [RelayCommand]
    private void TogglePermission(PluginPermissionItem? permItem)
    {
        if (permItem == null || permItem.IsTier1) return;

        // Find the parent plugin
        var plugin = PluginItems.FirstOrDefault(p =>
            p.Permissions.Contains(permItem));
        if (plugin == null) return;

        var wsConfig = _settingsService.GetWorkspacePluginConfig();
        if (!wsConfig.PluginPermissions.TryGetValue(plugin.Id, out var state))
        {
            state = new PluginPermissionState();
            wsConfig.PluginPermissions[plugin.Id] = state;
        }

        // IsGranted was already toggled by the CheckBox two-way binding
        if (permItem.IsGranted)
        {
            state.Granted.Add(permItem.PermissionKey);
            state.Denied.Remove(permItem.PermissionKey);
        }
        else
        {
            state.Granted.Remove(permItem.PermissionKey);
            state.Denied.Add(permItem.PermissionKey);
        }

        _settingsService.Save();

        // Update the runtime sandbox via FFI
        var granted = new List<string>(PluginRegistry.Tier1Permissions);
        granted.AddRange(state.Granted);
        var permObj = new { granted = granted.Distinct().ToList(), denied = state.Denied.ToList(), pending_jit = Array.Empty<string>() };
        var json = JsonSerializer.Serialize(permObj);
        NativeLib.PluginUpdatePermissions(plugin.Id, json);
    }

    private void PopulatePluginPermissions(PluginSettingsItem item)
    {
        var wsConfig = _settingsService.GetWorkspacePluginConfig();
        wsConfig.PluginPermissions.TryGetValue(item.Id, out var saved);

        foreach (var (key, (displayName, description)) in PluginRegistry.PermissionDisplayInfo)
        {
            var isTier1 = PluginRegistry.Tier1Permissions.Contains(key);
            var isGranted = isTier1 || (saved?.Granted.Contains(key) ?? false);

            item.Permissions.Add(new PluginPermissionItem
            {
                PermissionKey = key,
                DisplayName = displayName,
                Description = description,
                IsGranted = isGranted,
                IsTier1 = isTier1,
            });
        }
    }

    partial void OnExperimentalPluginsEnabledChanged(bool value)
    {
        _pluginRegistry.SetExperimentalPluginsEnabled(value);
        // Refresh plugin items to update enabled states
        LoadPluginItems();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        UserDisplayName = settings.UserDisplayName ?? Environment.UserName ?? "User";
        ProfileImagePath = settings.ProfileImagePath;
        Version = $"v{Native.PrivStackService.Version}";

        // Get storage location from active workspace (per-workspace, not global)
        var workspaceService = App.Services.GetRequiredService<IWorkspaceService>();
        var activeWorkspace = workspaceService.GetActiveWorkspace();
        var storageLocation = activeWorkspace?.StorageLocation;

        if (storageLocation != null && Enum.TryParse<DataDirectoryType>(storageLocation.Type, out var dirType))
            SelectedDirectoryType = dirType;
        else
            SelectedDirectoryType = DataDirectoryType.Default;

        var defaultDataDir = Services.DataPaths.BaseDir;
        DataDirectory = storageLocation?.CustomPath ?? defaultDataDir;

        // Backup settings
        var defaultBackupDir = Path.Combine(defaultDataDir, "backups");
        BackupDirectory = settings.BackupDirectory ?? defaultBackupDir;

        if (Enum.TryParse<BackupFrequency>(settings.BackupFrequency, out var freq))
            SelectedBackupFrequency = freq;

        if (Enum.TryParse<BackupType>(settings.BackupType, out var type))
            SelectedBackupType = type;

        MaxBackups = settings.MaxBackups;

        // Load custom themes into the list, then select current
        RefreshAvailableThemes();

        // Set selected lockout option
        var lockoutMinutes = settings.SensitiveLockoutMinutes;
        SelectedLockoutOption = LockoutOptions.FirstOrDefault(o => o.Minutes == lockoutMinutes)
            ?? LockoutOptions.First(o => o.Minutes == 5); // Default to 5 minutes

        // Set font scale from service (which loaded from settings)
        FontScaleMultiplier = _fontScaleService.ScaleMultiplier;

        // Set font family from service
        var fontFamilyKey = _fontScaleService.CurrentFontFamily;
        SelectedFontFamily = AvailableFontFamilies.FirstOrDefault(f => f.Key == fontFamilyKey)
            ?? AvailableFontFamilies[0];

        // Update settings
        AutoCheckForUpdates = settings.AutoCheckForUpdates;

        // Notification settings
        NotificationsEnabled = settings.NotificationsEnabled;
        NotificationSoundEnabled = settings.NotificationSoundEnabled;

        // Speech-to-text settings
        SpeechToTextEnabled = settings.SpeechToTextEnabled;
        var modelId = settings.WhisperModelSize ?? "base.en";
        SelectedWhisperModel = WhisperModels.FirstOrDefault(m => m.Id == modelId) ?? WhisperModels[1]; // Default to base.en

        // Beam search
        WhisperBeamSearch = settings.WhisperBeamSearch;
        WhisperService.Instance.BeamSearchEnabled = settings.WhisperBeamSearch;

        // Audio input device
        AudioRecorderService.Instance.SelectedDeviceId = settings.AudioInputDevice;
        RefreshAudioDevices();

        // Emergency Kit status
        LoadEmergencyKitStatus();
    }

    partial void OnUserDisplayNameChanged(string value)
    {
        _settingsService.Settings.UserDisplayName = value;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(UserInitial));
        _settingsService.NotifyProfileChanged();
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value == null) return;

        if (value.IsCustom)
        {
            var customTheme = _customThemeStore.Load(value.CustomThemeId!);
            if (customTheme != null)
            {
                _themeService.ApplyCustomTheme(customTheme);
                _themeService.SaveThemePreference($"custom:{customTheme.Id}");
            }
        }
        else
        {
            _themeService.CurrentTheme = value.Theme;
        }
    }

    partial void OnSelectedBackupFrequencyChanged(BackupFrequency value)
    {
        _settingsService.Settings.BackupFrequency = value.ToString();
        _settingsService.SaveDebounced();
        _backupService.UpdateBackupFrequency(value);
    }

    partial void OnSelectedBackupTypeChanged(BackupType value)
    {
        _settingsService.Settings.BackupType = value.ToString();
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(IsRollingBackup));
        OnPropertyChanged(nameof(IsOneTimeBackup));
    }

    partial void OnMaxBackupsChanged(int value)
    {
        _settingsService.Settings.MaxBackups = value;
        _settingsService.SaveDebounced();
    }

    partial void OnSelectedLockoutOptionChanged(LockoutOption? value)
    {
        if (value != null)
        {
            _settingsService.Settings.SensitiveLockoutMinutes = value.Minutes;
            _sensitiveLockService.LockoutMinutes = value.Minutes;
            _settingsService.SaveDebounced();
        }
    }

    partial void OnFontScaleMultiplierChanged(double value)
    {
        _fontScaleService.ScaleMultiplier = value;
    }

    partial void OnSelectedFontFamilyChanged(FontFamilyOption? value)
    {
        if (value != null)
        {
            _fontScaleService.CurrentFontFamily = value.Key;
        }
    }

    [RelayCommand]
    private void SetFontScale(double scale)
    {
        FontScaleMultiplier = scale;
    }

    partial void OnSpeechToTextEnabledChanged(bool value)
    {
        _settingsService.Settings.SpeechToTextEnabled = value;
        _settingsService.SaveDebounced();

        // Refresh warning message
        OnPropertyChanged(nameof(SpeechWarningMessage));
        OnPropertyChanged(nameof(HasSpeechWarning));

        if (value)
            RefreshAudioDevices();
    }

    partial void OnSelectedAudioInputDeviceChanged(AudioInputDevice? value)
    {
        var deviceId = value?.Id;
        _settingsService.Settings.AudioInputDevice = deviceId;
        AudioRecorderService.Instance.SelectedDeviceId = deviceId;
        _settingsService.SaveDebounced();
    }

    partial void OnWhisperBeamSearchChanged(bool value)
    {
        _settingsService.Settings.WhisperBeamSearch = value;
        WhisperService.Instance.BeamSearchEnabled = value;
        _settingsService.SaveDebounced();

        // Force model reload with new sampling strategy on next use
        if (WhisperService.Instance.IsModelLoaded)
            _ = WhisperService.Instance.InitializeAsync();
    }

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        // Capture saved device ID BEFORE clearing the collection.
        // Clear() nulls SelectedAudioInputDevice, which triggers
        // OnSelectedAudioInputDeviceChanged(null) and overwrites the setting.
        var savedId = _settingsService.Settings.AudioInputDevice;

        AudioInputDevices.Clear();

        var devices = AudioRecorderService.Instance.GetAvailableDevices();
        foreach (var device in devices)
            AudioInputDevices.Add(device);

        // Restore saved selection (or fall back to first device)
        if (savedId != null)
            SelectedAudioInputDevice = AudioInputDevices.FirstOrDefault(d => d.Id == savedId);

        SelectedAudioInputDevice ??= AudioInputDevices.FirstOrDefault();
    }

    partial void OnSelectedWhisperModelChanged(WhisperModelOption? value)
    {
        if (value != null)
        {
            _settingsService.Settings.WhisperModelSize = value.Id;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(ModelStatusText));
            OnPropertyChanged(nameof(CanDownloadModel));
            OnPropertyChanged(nameof(DownloadButtonText));
            OnPropertyChanged(nameof(SpeechWarningMessage));
            OnPropertyChanged(nameof(HasSpeechWarning));
        }
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (SelectedWhisperModel == null || IsDownloadingModel) return;

        var modelManager = WhisperModelManager.Instance;

        // Subscribe to progress updates
        modelManager.PropertyChanged += OnModelManagerPropertyChanged;

        try
        {
            IsDownloadingModel = true;
            DownloadProgress = 0;

            await modelManager.DownloadModelAsync(SelectedWhisperModel.Id);

            // Refresh status and warning
            OnPropertyChanged(nameof(ModelStatusText));
            OnPropertyChanged(nameof(CanDownloadModel));
            OnPropertyChanged(nameof(DownloadButtonText));
            OnPropertyChanged(nameof(SpeechWarningMessage));
            OnPropertyChanged(nameof(HasSpeechWarning));
        }
        catch (OperationCanceledException)
        {
            // Download was cancelled
        }
        catch (Exception)
        {
            // Download failed - error is logged by the service
        }
        finally
        {
            IsDownloadingModel = false;
            modelManager.PropertyChanged -= OnModelManagerPropertyChanged;
        }
    }

    private void OnModelManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WhisperModelManager.DownloadProgress))
        {
            DownloadProgress = WhisperModelManager.Instance.DownloadProgress;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        WhisperModelManager.Instance.CancelDownload();
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataDirectory,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Ignore errors opening directory
        }
    }

    [RelayCommand]
    private void OpenUrl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Ignore errors opening URL
        }
    }

    [RelayCommand]
    private void OpenBackupDirectory()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory))
                Directory.CreateDirectory(BackupDirectory);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = BackupDirectory,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Ignore errors opening directory
        }
    }

    [RelayCommand]
    private async Task ChangeDataDirectoryAsync()
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
            var newPath = folders[0].Path.LocalPath;
            DataDirectory = newPath;
            SelectedDirectoryType = DataDirectoryType.Custom;
            await ChangeWorkspaceStorageAsync(new StorageLocation
            {
                Type = "Custom",
                CustomPath = newPath
            });
        }
    }

    [RelayCommand]
    private async Task ChangeBackupDirectoryAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Backup Directory",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var newPath = folders[0].Path.LocalPath;
            _backupService.UpdateBackupDirectory(newPath);
            BackupDirectory = newPath;
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (IsBackingUp) return;

        IsBackingUp = true;
        BackupStatus = $"Backing up {DataDirectory}...";

        try
        {
            var backupPath = await _backupService.BackupNowAsync();
            if (backupPath != null)
            {
                var fileInfo = new FileInfo(backupPath);
                var sizeKb = fileInfo.Length / 1024.0;
                BackupStatus = $"Backup created ({sizeKb:F1} KB)";
            }
            else
            {
                BackupStatus = "Backup failed - no files found";
            }
        }
        catch (Exception ex)
        {
            BackupStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsBackingUp = false;

            // Clear status after 5 seconds
            await Task.Delay(5000);
            BackupStatus = null;
        }
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    /// <summary>
    /// Fired when the user wants to open the workspace switcher from settings.
    /// </summary>
    public event EventHandler? SwitchWorkspaceRequested;

    [RelayCommand]
    private void SwitchWorkspace()
    {
        SwitchWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ResetSettings()
    {
        // Remove profile image file if it exists
        if (!string.IsNullOrEmpty(_settingsService.Settings.ProfileImagePath))
        {
            var fullPath = Path.Combine(Services.DataPaths.BaseDir, _settingsService.Settings.ProfileImagePath);
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* best effort */ }
        }

        _cachedProfileBitmap?.Dispose();
        _cachedProfileBitmap = null;

        _settingsService.Settings.UserDisplayName = null;
        _settingsService.Settings.ProfileImagePath = null;
        _settingsService.Settings.Theme = "Dark";
        _settingsService.Save();
        _settingsService.NotifyProfileChanged();
        LoadSettings();
    }

    // ========================================
    // Reseed / Wipe Sample Data
    // ========================================

    [ObservableProperty]
    private bool _isReseeding;

    [ObservableProperty]
    private string? _reseedStatus;

    [RelayCommand]
    private async Task ReseedSampleDataAsync()
    {
        var password = await _dialogService.ShowPasswordConfirmationAsync(
            "Reseed Sample Data",
            "This will wipe ALL plugin data and repopulate with fresh sample data. This cannot be undone.",
            "Reseed");

        if (password == null) return;

        if (!_authService.ValidateMasterPassword(password))
        {
            await _dialogService.ShowConfirmationAsync(
                "Incorrect Password",
                "The master password you entered is incorrect. Reseed was cancelled.",
                "OK");
            return;
        }

        IsReseeding = true;
        ReseedStatus = "Wiping data and reseeding...";

        try
        {
            await Task.Run(() => _seedDataService.ReseedAsync());
            ReseedStatus = "Sample data reseeded successfully.";
            await RefreshAllPluginsAsync();
        }
        catch (Exception ex)
        {
            ReseedStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsReseeding = false;
            await Task.Delay(5000);
            ReseedStatus = null;
        }
    }

    [RelayCommand]
    private async Task WipeSeedDataAsync()
    {
        var password = await _dialogService.ShowPasswordConfirmationAsync(
            "Wipe All Data",
            "This will permanently delete ALL plugin data. The databases will be completely empty. This cannot be undone.",
            "Wipe");

        if (password == null) return;

        if (!_authService.ValidateMasterPassword(password))
        {
            await _dialogService.ShowConfirmationAsync(
                "Incorrect Password",
                "The master password you entered is incorrect. Wipe was cancelled.",
                "OK");
            return;
        }

        IsReseeding = true;
        ReseedStatus = "Wiping all data...";

        try
        {
            await Task.Run(() => _seedDataService.WipeAsync());
            ReseedStatus = "All data wiped successfully.";
            await RefreshAllPluginsAsync();
        }
        catch (Exception ex)
        {
            ReseedStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsReseeding = false;
            await Task.Delay(5000);
            ReseedStatus = null;
        }
    }

    /// <summary>
    /// Evicts cached ViewModels for all plugins so they reload fresh data.
    /// If a plugin is the active tab, it gets re-navigated immediately.
    /// </summary>
    private async Task RefreshAllPluginsAsync()
    {
        var mainVm = _pluginRegistry.GetMainViewModel();
        if (mainVm == null) return;

        foreach (var plugin in _pluginRegistry.Plugins)
        {
            var navId = plugin.NavigationItem?.Id;
            if (navId != null)
                await mainVm.ReloadPluginAsync(navId);
        }
    }

    [RelayCommand]
    private async Task SelectStorageLocation(DataDirectoryType type)
    {
        SelectedDirectoryType = type;

        if (type == DataDirectoryType.Custom)
            return; // Custom requires folder picker via ChangeDataDirectoryAsync

        var newLocation = type == DataDirectoryType.Default
            ? null
            : new StorageLocation { Type = type.ToString() };

        DataDirectory = type switch
        {
            DataDirectoryType.GoogleDrive => GetGoogleDrivePath() ?? GetDefaultDataPath(),
            DataDirectoryType.ICloud => GetICloudPath() ?? GetDefaultDataPath(),
            _ => GetDefaultDataPath()
        };

        await ChangeWorkspaceStorageAsync(newLocation);
    }

    private async Task ChangeWorkspaceStorageAsync(StorageLocation? newLocation)
    {
        var workspaceService = App.Services.GetRequiredService<IWorkspaceService>();
        var activeWorkspace = workspaceService.GetActiveWorkspace();
        if (activeWorkspace == null) return;

        IsMigratingStorage = true;
        MigrationStatus = "Preparing migration...";
        MigrationProgress = 0;

        try
        {
            var progress = new Progress<WorkspaceMigrationProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    MigrationProgress = (double)p.BytesCopied / p.TotalBytes * 100;

                MigrationStatus = p.Phase switch
                {
                    MigrationPhase.Calculating => "Calculating workspace size...",
                    MigrationPhase.Copying => $"Copying {p.FilesCopied}/{p.TotalFiles}: {p.CurrentFile}",
                    MigrationPhase.Verifying => "Verifying copied files...",
                    MigrationPhase.Reloading => "Reloading workspace...",
                    MigrationPhase.CleaningUp => "Cleaning up old location...",
                    MigrationPhase.Complete => "Migration complete",
                    MigrationPhase.Failed => $"Migration failed: {p.CurrentFile}",
                    _ => MigrationStatus
                };
            });

            await workspaceService.MigrateWorkspaceStorageAsync(
                activeWorkspace.Id,
                newLocation ?? new StorageLocation { Type = "Default" },
                progress);
        }
        catch (Exception ex)
        {
            MigrationStatus = $"Migration failed: {ex.Message}";
            // Revert UI to match actual state
            var current = workspaceService.GetActiveWorkspace();
            if (current?.StorageLocation != null &&
                Enum.TryParse<DataDirectoryType>(current.StorageLocation.Type, out var revertType))
                SelectedDirectoryType = revertType;
            else
                SelectedDirectoryType = DataDirectoryType.Default;
        }
        finally
        {
            IsMigratingStorage = false;
        }
    }

    partial void OnSelectedDirectoryTypeChanged(DataDirectoryType value)
    {
        OnPropertyChanged(nameof(IsCustomDirectorySelected));
        OnPropertyChanged(nameof(DataDirectoryDisplay));
    }

    /// <summary>
    /// Gets the default PrivStack data path.
    /// </summary>
    private static string GetDefaultDataPath()
    {
        return Services.DataPaths.BaseDir;
    }

    private static string? GetGoogleDrivePath() => Services.CloudPathResolver.GetGoogleDrivePath();

    private static string? GetICloudPath() => Services.CloudPathResolver.GetICloudPath();
}
