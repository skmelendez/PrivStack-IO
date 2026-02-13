namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Manages full-state snapshot sync for cloud/NAS storage.
/// Exports a complete entity snapshot on app close; imports the latest
/// peer snapshot on app start to bootstrap new/offline devices.
/// </summary>
public interface ISnapshotSyncService : IDisposable
{
    /// <summary>
    /// Whether snapshot sync is active for the current workspace.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Starts snapshot sync: imports the latest peer snapshot if available.
    /// No-op if the workspace has no cloud/NAS storage location.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Exports a full snapshot of all entities to the shared snapshot directory.
    /// Called on app close or workspace switch.
    /// </summary>
    Task ExportSnapshotAsync();

    /// <summary>
    /// Stops snapshot sync, clears key from memory.
    /// </summary>
    void Stop();
}
