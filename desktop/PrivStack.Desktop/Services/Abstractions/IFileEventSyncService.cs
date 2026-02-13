namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Manages file-based event sync for cloud/NAS storage locations.
/// Writes encrypted event files to a shared directory and scans for
/// events from other peers. Coexists with P2P relay sync.
/// </summary>
public interface IFileEventSyncService : IDisposable
{
    /// <summary>
    /// Whether file-based sync is currently active for the current workspace.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Starts file sync for the current workspace.
    /// No-op if the workspace has no event store (Default storage location).
    /// </summary>
    void Start();

    /// <summary>
    /// Stops file sync, clears the encryption key from memory.
    /// </summary>
    void Stop();

    /// <summary>
    /// Writes an encrypted event file to the outbound peer directory.
    /// No-op if file sync is not active.
    /// </summary>
    void WriteEventFile(string entityId, string entityType, string payload);
}
