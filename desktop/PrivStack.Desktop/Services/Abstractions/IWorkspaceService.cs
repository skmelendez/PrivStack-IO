using PrivStack.Desktop.Models;

namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Abstraction over workspace management.
/// </summary>
public interface IWorkspaceService
{
    bool HasWorkspaces { get; }
    event EventHandler<Workspace>? WorkspaceChanged;
    Workspace? GetActiveWorkspace();
    IReadOnlyList<Workspace> ListWorkspaces();
    string GetActiveDataPath();
    string GetDataPath(string workspaceId);
    string ResolveWorkspaceDir(Workspace workspace);
    string ResolveWorkspaceDir(string workspaceId, StorageLocation? location);
    Workspace CreateWorkspace(string name, StorageLocation? storageLocation = null, bool makeActive = false);
    void SwitchWorkspace(string workspaceId);
    void DeleteWorkspace(string workspaceId);
    Task MigrateWorkspaceStorageAsync(string workspaceId, StorageLocation newLocation, IProgress<WorkspaceMigrationProgress>? progress = null);

    /// <summary>
    /// Resolves the event store directory for file-based sync.
    /// Returns null for Default storage (no file sync needed).
    /// </summary>
    string? ResolveEventStoreDir(Workspace workspace);

    /// <summary>
    /// Resolves the event store directory for file-based sync by workspace ID and location.
    /// Returns null for Default storage (no file sync needed).
    /// </summary>
    string? ResolveEventStoreDir(string workspaceId, StorageLocation? location);

    /// <summary>
    /// Resolves the snapshot directory for full-state sync.
    /// Returns null for Default storage (no file sync needed).
    /// </summary>
    string? ResolveSnapshotDir(Workspace workspace);

    /// <summary>
    /// Resolves the shared files directory for cloud/NAS file sync.
    /// Regular files (attachments, media, dataset imports) are stored here
    /// and synced natively by the cloud provider. Returns null for Default storage.
    /// </summary>
    string? ResolveSharedFilesDir(Workspace workspace);
}
