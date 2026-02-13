using Avalonia.Controls;
using PrivStack.Desktop.Services;

namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Abstraction over application settings persistence.
/// </summary>
public interface IAppSettingsService
{
    AppSettings Settings { get; }
    void Load();
    void Save();
    void SaveDebounced();
    void Flush();
    void UpdateLastActiveTab(string tabName);
    void UpdateCalendarViewMode(string viewMode);
    void UpdateTasksViewMode(string viewMode);
    void UpdateTasksSimpleMode(bool simpleMode);
    void UpdatePluginOrder(IEnumerable<string> pluginIds);
    void UpdateWindowBounds(Window window);
    void ApplyToWindow(Window window);

    /// <summary>
    /// Gets the plugin configuration for the current workspace.
    /// Creates a new entry if one doesn't exist yet, migrating from global settings on first access.
    /// </summary>
    WorkspacePluginConfig GetWorkspacePluginConfig();

    /// <summary>
    /// Creates a whitelist-mode plugin config for a workspace.
    /// Defaults to core-only plugins if no enabledPluginIds provided.
    /// </summary>
    WorkspacePluginConfig InitializeWorkspacePluginConfig(
        string workspaceId,
        IEnumerable<string>? enabledPluginIds = null);

    /// <summary>
    /// Raised when the user's profile (display name or image) changes.
    /// </summary>
    event Action? ProfileChanged;

    /// <summary>
    /// Fires the ProfileChanged event.
    /// </summary>
    void NotifyProfileChanged();
}
