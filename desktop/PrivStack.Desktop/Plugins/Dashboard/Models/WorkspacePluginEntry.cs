using CommunityToolkit.Mvvm.ComponentModel;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Plugins.Dashboard.Models;

/// <summary>
/// Display model for a plugin shown in the workspace plugins section of the Dashboard.
/// Tracks whether the plugin is activated in the current workspace.
/// </summary>
public partial class WorkspacePluginEntry : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public PluginCategory Category { get; init; }
    public ReleaseStage ReleaseStage { get; init; } = ReleaseStage.Release;
    public bool IsAlpha => ReleaseStage == ReleaseStage.Alpha;
    public bool IsBeta => ReleaseStage == ReleaseStage.Beta;

    [ObservableProperty]
    private bool _isActivated;
}
