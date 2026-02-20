using CommunityToolkit.Mvvm.ComponentModel;

namespace PrivStack.Desktop.Plugins.Dashboard.Models;

/// <summary>
/// Display model for a plugin shown in the Dashboard grid.
/// Merges data from the server registry with local installed state.
/// </summary>
public partial class DashboardPluginItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string? _tagline;

    [ObservableProperty]
    private string _author = "PrivStack";

    [ObservableProperty]
    private string _latestVersion = string.Empty;

    [ObservableProperty]
    private string? _installedVersion;

    [ObservableProperty]
    private string _category = "productivity";

    [ObservableProperty]
    private string _icon = "Package";

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _trustTier = "Official";

    [ObservableProperty]
    private long? _packageSizeBytes;

    [ObservableProperty]
    private long? _diskSizeBytes;

    [ObservableProperty]
    private string _releaseStage = "release";

    [ObservableProperty]
    private bool _isActivated;

    [ObservableProperty]
    private bool _canToggle;

    public bool IsAlpha => string.Equals(ReleaseStage, "alpha", StringComparison.OrdinalIgnoreCase);
    public bool IsBeta => string.Equals(ReleaseStage, "beta", StringComparison.OrdinalIgnoreCase);

    public bool IsOfficial => TrustTier == "Official";
    public bool IsNotInstalled => !IsInstalled;
    public bool IsInstalledNoUpdate => IsInstalled && !HasUpdate;

    public string DisplayVersion => IsInstalled
        ? $"v{InstalledVersion}"
        : $"v{LatestVersion}";

    public string UpdateVersionDisplay => HasUpdate
        ? $"v{InstalledVersion} → v{LatestVersion}"
        : string.Empty;

    public string SizeDisplay => PackageSizeBytes switch
    {
        null or 0 => "",
        < 1024 => $"{PackageSizeBytes} B",
        < 1024 * 1024 => $"{PackageSizeBytes / 1024.0:F1} KB",
        _ => $"{PackageSizeBytes / (1024.0 * 1024.0):F1} MB"
    };

    public string DiskSizeDisplay => DiskSizeBytes switch
    {
        null or 0 => "",
        _ => $"{SystemMetricsHelper.FormatBytes(DiskSizeBytes.Value)} on disk"
    };
}
