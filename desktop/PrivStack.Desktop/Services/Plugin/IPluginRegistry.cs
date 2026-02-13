// ============================================================================
// File: IPluginRegistry.cs
// Description: Interface for the plugin registry service that manages plugin
//              discovery, initialization, and lifecycle.
// ============================================================================

using System.Collections.ObjectModel;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Services.Plugin;

/// <summary>
/// Defines the contract for the plugin registry service that manages plugin discovery,
/// initialization, and lifecycle. All plugin types come from PrivStack.Sdk.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>
    /// Gets all registered plugins, including inactive and failed plugins.
    /// Sorted by NavigationOrder ascending.
    /// </summary>
    IReadOnlyList<IAppPlugin> Plugins { get; }

    /// <summary>
    /// Gets all active plugins (PluginState.Active).
    /// </summary>
    IReadOnlyList<IAppPlugin> ActivePlugins { get; }

    /// <summary>
    /// Gets all navigation items from active plugins, sorted by order.
    /// </summary>
    IReadOnlyList<NavigationItem> NavigationItems { get; }

    /// <summary>
    /// Gets an observable collection of navigation items for UI binding.
    /// </summary>
    ObservableCollection<NavigationItem> NavigationItemsObservable { get; }

    /// <summary>
    /// Gets a plugin by its unique identifier (case-insensitive).
    /// </summary>
    IAppPlugin? GetPlugin(string pluginId);

    /// <summary>
    /// Gets the plugin that provides the specified navigation item.
    /// </summary>
    IAppPlugin? GetPluginForNavItem(string navItemId);

    /// <summary>
    /// Gets all active plugins that implement a specific capability interface.
    /// Also checks the CapabilityBroker for programmatically registered providers.
    /// </summary>
    IReadOnlyList<TCapability> GetCapabilityProviders<TCapability>() where TCapability : class;

    /// <summary>
    /// Gets a specific capability provider by a string identifier.
    /// </summary>
    TCapability? GetCapabilityProvider<TCapability>(
        string identifier,
        Func<TCapability, string> identifierSelector) where TCapability : class;

    /// <summary>
    /// Raised when a plugin's lifecycle state changes.
    /// </summary>
    event EventHandler<PluginStateChangedEventArgs>? PluginStateChanged;

    /// <summary>
    /// Raised when the navigation items collection changes.
    /// </summary>
    event EventHandler? NavigationItemsChanged;

    /// <summary>
    /// Discovers plugins from assemblies and initializes them.
    /// </summary>
    Task DiscoverAndInitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous version of plugin discovery and initialization.
    /// </summary>
    void DiscoverAndInitialize();

    /// <summary>
    /// Tears down all loaded plugins and rediscovers from scratch.
    /// Used when switching workspaces to get a clean slate.
    /// </summary>
    void Reinitialize();

    /// <summary>
    /// Async version of Reinitialize that runs heavy plugin discovery/init
    /// on a background thread to avoid freezing the UI during workspace switches.
    /// </summary>
    Task ReinitializeAsync();

    /// <summary>
    /// Sets the main window ViewModel for cross-plugin navigation.
    /// </summary>
    void SetMainViewModel(MainWindowViewModel mainViewModel);

    /// <summary>
    /// Gets the main window ViewModel.
    /// </summary>
    MainWindowViewModel? GetMainViewModel();

    /// <summary>
    /// Updates the selected navigation item.
    /// </summary>
    void UpdateSelectedNavItem(string navItemId);

    /// <summary>
    /// Moves a navigation item from one position to another.
    /// </summary>
    void MoveNavigationItem(int fromIndex, int toIndex);

    /// <summary>
    /// Returns whether a plugin is currently enabled.
    /// </summary>
    bool IsPluginEnabled(string pluginId);

    /// <summary>
    /// Enables a plugin in the current workspace. Returns true if successful.
    /// </summary>
    bool EnablePlugin(string pluginId);

    /// <summary>
    /// Disables a plugin in the current workspace. Returns true if successful.
    /// </summary>
    bool DisablePlugin(string pluginId);

    /// <summary>
    /// Toggles a plugin's enabled state. Returns the new enabled state.
    /// </summary>
    bool TogglePlugin(string pluginId);

    /// <summary>
    /// Enables or disables experimental plugins.
    /// </summary>
    void SetExperimentalPluginsEnabled(bool enabled);

    /// <summary>
    /// Hot-loads a plugin from an extracted directory (e.g. after install/update).
    /// Scans for IAppPlugin types, instantiates, initializes, activates, and rebuilds nav.
    /// </summary>
    Task<bool> LoadPluginFromDirectoryAsync(string pluginDirectory, CancellationToken ct = default);

    /// <summary>
    /// Unloads a plugin by ID — deactivates, disposes, and removes from all registries.
    /// </summary>
    bool UnloadPlugin(string pluginId);
}

/// <summary>
/// Event arguments for plugin state change events.
/// </summary>
public sealed class PluginStateChangedEventArgs : EventArgs
{
    public IAppPlugin Plugin { get; }
    public PluginState NewState { get; }
    public string? Message { get; }

    public PluginStateChangedEventArgs(IAppPlugin plugin, PluginState newState, string? message = null)
    {
        Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        NewState = newState;
        Message = message;
    }
}
