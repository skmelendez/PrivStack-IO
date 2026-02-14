using CommunityToolkit.Mvvm.Messaging;

namespace PrivStack.Sdk;

/// <summary>
/// Host services provided to plugins during initialization and runtime.
/// Replaces the former IPluginContext with a decoupled, SDK-only contract.
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// The SDK for all data operations (CRUD, queries, commands).
    /// </summary>
    IPrivStackSdk Sdk { get; }

    /// <summary>
    /// Capability broker for cross-plugin discovery and communication.
    /// </summary>
    ICapabilityBroker Capabilities { get; }

    /// <summary>
    /// Plugin-namespaced settings storage.
    /// </summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// Structured logger for plugin diagnostics.
    /// </summary>
    IPluginLogger Logger { get; }

    /// <summary>
    /// Navigation service for cross-plugin tab switching.
    /// </summary>
    INavigationService Navigation { get; }

    /// <summary>
    /// Framework-agnostic dialog service. May be null during early initialization.
    /// </summary>
    ISdkDialogService? DialogService { get; }

    /// <summary>
    /// Info panel service for reporting the currently selected item
    /// so the shell can display backlinks and local graph.
    /// </summary>
    IInfoPanelService InfoPanel { get; }

    /// <summary>
    /// Focus mode service for distraction-free reading.
    /// </summary>
    IFocusModeService FocusMode { get; }

    /// <summary>
    /// Message bus for cross-cutting event notifications (e.g., P2P sync entity arrivals).
    /// Uses CommunityToolkit.Mvvm's WeakReferenceMessenger.
    /// </summary>
    IMessenger Messenger { get; }

    /// <summary>
    /// The running host application version.
    /// </summary>
    Version AppVersion { get; }

    /// <summary>
    /// Service for accessing authenticated external connections (e.g., GitHub).
    /// Nullable for backward compatibility with older hosts.
    /// </summary>
    IConnectionService? Connections { get; }

    /// <summary>
    /// Service for reading and writing entity metadata (tags, custom properties).
    /// Nullable for backward compatibility with older hosts.
    /// </summary>
    IPropertyService? Properties { get; }

    /// <summary>
    /// The active workspace data directory.
    /// Plugins should store all workspace-specific files under this path.
    /// </summary>
    string WorkspaceDataPath { get; }
}
