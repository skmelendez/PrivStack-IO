using CommunityToolkit.Mvvm.ComponentModel;

namespace PrivStack.Sdk;

public enum PluginState
{
    Discovered,
    Initializing,
    Initialized,
    Active,
    Deactivated,
    Failed,
    Disposed
}

public enum PluginCategory
{
    Productivity,
    Security,
    Communication,
    Information,
    Utility,
    Extension
}

/// <summary>
/// Navigation bar entry for a plugin.
/// </summary>
public sealed class NavigationItem : ObservableObject
{
    private bool _isSelected;
    private bool _isEnabled = true;

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Subtitle { get; init; }
    public string? Icon { get; init; }
    public string? Tooltip { get; init; }
    public int Order { get; set; } = 1000;
    public bool ShowBadge { get; init; }
    public int BadgeCount { get; init; }
    public string? ShortcutHint { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsExperimental { get; init; }
    public bool IsHardLocked { get; init; }
    public string? HardLockedReason { get; init; }

    public ReleaseStage ReleaseStage { get; init; } = ReleaseStage.Release;
    public bool IsAlpha => ReleaseStage == ReleaseStage.Alpha;
    public bool IsBeta => ReleaseStage == ReleaseStage.Beta;
}
