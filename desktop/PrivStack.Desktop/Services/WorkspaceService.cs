using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Sdk;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Manages workspace lifecycle: creation, switching, deletion, and registry persistence.
/// </summary>
public sealed partial class WorkspaceService : IWorkspaceService
{
    private static readonly ILogger _log = Log.ForContext<WorkspaceService>();

    private readonly string _basePath;
    private readonly string _registryPath;
    private WorkspaceRegistry _registry = new();

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };


    /// <summary>
    /// Fired after a workspace switch completes (service re-initialized).
    /// </summary>
    public event EventHandler<Workspace>? WorkspaceChanged;

    public WorkspaceService()
    {
        _basePath = GetBasePath();
        _registryPath = Path.Combine(_basePath, "workspaces.json");
        Directory.CreateDirectory(_basePath);
        LoadRegistry();
        MigrateGlobalStorageSettings();
        MigrateCloudDbToLocal();

        // If there's already an active workspace, set DataPaths so workspace-scoped
        // paths resolve immediately (before InitializeService runs).
        var active = GetActiveWorkspace();
        if (active != null)
        {
            var resolvedDir = ResolveWorkspaceDir(active);
            DataPaths.SetActiveWorkspace(active.Id, resolvedDir);
        }
    }

    /// <summary>
    /// Whether any workspaces exist in the registry.
    /// </summary>
    public bool HasWorkspaces => _registry.Workspaces.Count > 0;

    /// <summary>
    /// Gets the currently active workspace, or null if none.
    /// </summary>
    public Workspace? GetActiveWorkspace()
    {
        if (string.IsNullOrEmpty(_registry.ActiveWorkspaceId))
            return _registry.Workspaces.FirstOrDefault();

        return _registry.Workspaces.FirstOrDefault(w => w.Id == _registry.ActiveWorkspaceId)
            ?? _registry.Workspaces.FirstOrDefault();
    }

    /// <summary>
    /// Lists all workspaces.
    /// </summary>
    public IReadOnlyList<Workspace> ListWorkspaces() => _registry.Workspaces.AsReadOnly();

    /// <summary>
    /// Gets the database path for the active workspace.
    /// Throws if no workspaces exist — caller must ensure setup is complete first.
    /// </summary>
    public string GetActiveDataPath()
    {
        var active = GetActiveWorkspace();
        if (active == null)
            throw new InvalidOperationException("No workspaces exist. Run setup wizard first.");

        return GetDataPath(active.Id);
    }

    /// <summary>
    /// Gets the database path for a given workspace ID.
    /// </summary>
    public string GetDataPath(string workspaceId)
    {
        var workspace = _registry.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        var wsDir = ResolveWorkspaceDir(workspaceId, workspace?.StorageLocation);
        return Path.Combine(wsDir, "data.duckdb");
    }

    /// <summary>
    /// Gets the directory for the active workspace.
    /// </summary>
    public string GetActiveWorkspaceDir()
    {
        var active = GetActiveWorkspace()
            ?? throw new InvalidOperationException("No active workspace.");
        return ResolveWorkspaceDir(active);
    }

    /// <summary>
    /// Resolves the workspace directory from a Workspace record.
    /// </summary>
    public string ResolveWorkspaceDir(Workspace workspace)
    {
        return ResolveWorkspaceDir(workspace.Id, workspace.StorageLocation);
    }

    /// <summary>
    /// Central path resolver for workspace directories.
    /// Always returns a local path — DuckDB requires POSIX file locks which
    /// don't work on network filesystems. Cloud/NAS locations are used for
    /// the event store directory (see <see cref="ResolveEventStoreDir"/>).
    /// </summary>
    public string ResolveWorkspaceDir(string workspaceId, StorageLocation? location)
    {
        return Path.Combine(_basePath, "workspaces", workspaceId);
    }

    /// <summary>
    /// Resolves the event store directory for file-based sync.
    /// Returns null for Default/null storage (no file sync — P2P only).
    /// </summary>
    public string? ResolveEventStoreDir(Workspace workspace)
    {
        return ResolveEventStoreDir(workspace.Id, workspace.StorageLocation);
    }

    /// <summary>
    /// Resolves the event store directory for file-based sync.
    /// GoogleDrive → {GoogleDrivePath}/PrivStack/events/{workspaceId}
    /// ICloud → {ICloudPath}/PrivStack/events/{workspaceId}
    /// Custom → {CustomPath}/PrivStack/events/{workspaceId}
    /// Default/null → null (no file sync)
    /// </summary>
    public string? ResolveEventStoreDir(string workspaceId, StorageLocation? location)
    {
        return ResolveCloudSubdir(workspaceId, location, "events");
    }

    /// <summary>
    /// Resolves the snapshot directory for full-state sync.
    /// Snapshots are full entity dumps written on app close, enabling new device bootstrapping.
    /// </summary>
    public string? ResolveSnapshotDir(Workspace workspace)
    {
        return ResolveCloudSubdir(workspace.Id, workspace.StorageLocation, "snapshots");
    }

    /// <summary>
    /// Resolves the shared files directory for cloud/NAS file sync.
    /// Regular files (attachments, media, dataset imports) are stored here.
    /// </summary>
    public string? ResolveSharedFilesDir(Workspace workspace)
    {
        return ResolveCloudSubdir(workspace.Id, workspace.StorageLocation, "files");
    }

    /// <summary>
    /// Common resolver for cloud/NAS subdirectories under PrivStack/{subdir}/{workspaceId}.
    /// </summary>
    private static string? ResolveCloudSubdir(string workspaceId, StorageLocation? location, string subdir)
    {
        if (location == null || location.Type == "Default")
            return null;

        return location.Type switch
        {
            "Custom" when !string.IsNullOrEmpty(location.CustomPath) =>
                Path.Combine(location.CustomPath, "PrivStack", subdir, workspaceId),
            "GoogleDrive" => CloudPathResolver.GetGoogleDrivePath() is { } gd
                ? Path.Combine(gd, "PrivStack", subdir, workspaceId)
                : null,
            "ICloud" => CloudPathResolver.GetICloudPath() is { } ic
                ? Path.Combine(ic, "PrivStack", subdir, workspaceId)
                : null,
            _ => null,
        };
    }

    /// <summary>
    /// Creates a new workspace with the given name and optional storage location.
    /// </summary>
    public Workspace CreateWorkspace(string name, StorageLocation? storageLocation = null, bool makeActive = false)
    {
        var slug = Slugify(name);

        // Ensure unique slug
        var baseSlug = slug;
        int counter = 1;
        while (_registry.Workspaces.Any(w => w.Id == slug))
        {
            slug = $"{baseSlug}-{counter++}";
        }

        var workspace = new Workspace
        {
            Id = slug,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            StorageLocation = storageLocation
        };

        // Create workspace directory at resolved path
        var workspaceDir = ResolveWorkspaceDir(workspace);
        Directory.CreateDirectory(workspaceDir);

        var workspaces = new List<Workspace>(_registry.Workspaces) { workspace };

        // If this is the first workspace or caller requested, make it active
        var activeId = makeActive ? slug : (_registry.ActiveWorkspaceId ?? slug);

        _registry = new WorkspaceRegistry
        {
            Workspaces = workspaces,
            ActiveWorkspaceId = activeId
        };
        SaveRegistry();

        _log.Information("Created workspace: {Name} ({Id}) at {Dir}", name, slug, workspaceDir);
        return workspace;
    }

    /// <summary>
    /// Switches to a different workspace. Re-initializes the PrivStack service.
    /// Acquires the SdkHost write lock to block all FFI calls during the transition.
    /// </summary>
    public void SwitchWorkspace(string workspaceId)
    {
        var workspace = _registry.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace not found: {workspaceId}");

        if (_registry.ActiveWorkspaceId == workspaceId)
            return;

        _log.Information("Switching workspace to: {Name} ({Id})", workspace.Name, workspaceId);

        // Update registry
        _registry = _registry with { ActiveWorkspaceId = workspaceId };
        SaveRegistry();

        // Update DataPaths so all workspace-scoped paths resolve to the new workspace
        var resolvedDir = ResolveWorkspaceDir(workspace);
        DataPaths.SetActiveWorkspace(workspaceId, resolvedDir);

        // Reconfigure logger for the new workspace
        Log.ReconfigureForWorkspace(workspaceId);

        // Run one-time data migration for this workspace (moves legacy root-level data)
        WorkspaceDataMigration.MigrateIfNeeded(workspaceId, resolvedDir);

        // Clear prefetch cache before workspace switch to avoid stale data
        App.Services.GetService<ViewStatePrefetchService>()?.Clear();

        // Guard SDK calls during the Shutdown → Initialize window
        var sdkHost = App.Services.GetRequiredService<SdkHost>();
        var service = App.Services.GetRequiredService<Native.IPrivStackRuntime>();

        sdkHost.BeginWorkspaceSwitch();
        try
        {
            service.Shutdown();

            var newDbPath = GetDataPath(workspaceId);
            var newDir = Path.GetDirectoryName(newDbPath)!;
            Directory.CreateDirectory(newDir);
            service.Initialize(newDbPath);

            // Auto-initialize or unlock the new workspace vault
            var authService = App.Services.GetRequiredService<IAuthService>();
            var passwordCache = App.Services.GetService<IMasterPasswordCache>();
            var cachedPassword = passwordCache?.Get();

            if (cachedPassword != null)
            {
                if (!authService.IsAuthInitialized())
                {
                    _log.Information("New workspace has no vault — initializing auth with cached password");
                    authService.InitializeAuth(cachedPassword);
                }
                else if (!authService.IsAuthUnlocked())
                {
                    _log.Information("Workspace vault locked — unlocking with cached password");
                    authService.UnlockApp(cachedPassword);
                }
            }
            else
            {
                _log.Warning("No cached password available for workspace vault initialization");
            }
        }
        finally
        {
            sdkHost.EndWorkspaceSwitch();
        }

        _log.Information("Workspace switched, service re-initialized at: {Path}", GetDataPath(workspaceId));

        WorkspaceChanged?.Invoke(this, workspace);
    }

    /// <summary>
    /// Updates a workspace's sync location (event store directory).
    /// DB stays local — only the StorageLocation record changes, which controls
    /// where encrypted event files are written for file-based sync.
    /// </summary>
    public Task MigrateWorkspaceStorageAsync(
        string workspaceId,
        StorageLocation newLocation,
        IProgress<WorkspaceMigrationProgress>? progress = null)
    {
        var workspace = _registry.Workspaces.FirstOrDefault(w => w.Id == workspaceId)
            ?? throw new InvalidOperationException($"Workspace not found: {workspaceId}");

        var oldEventStore = ResolveEventStoreDir(workspace);
        var newEventStore = ResolveEventStoreDir(workspaceId, newLocation);

        if (workspace.StorageLocation?.Type == newLocation.Type
            && workspace.StorageLocation?.CustomPath == newLocation.CustomPath)
        {
            _log.Information("Storage location unchanged for workspace {Id}", workspaceId);
            return Task.CompletedTask;
        }

        _log.Information("Updating sync location for workspace {Id}: {OldType} → {NewType}",
            workspaceId, workspace.StorageLocation?.Type ?? "Default", newLocation.Type);

        // Update workspace record
        var updatedWorkspace = workspace with { StorageLocation = newLocation };
        var workspaces = _registry.Workspaces.Select(w =>
            w.Id == workspaceId ? updatedWorkspace : w).ToList();
        _registry = _registry with { Workspaces = workspaces };
        SaveRegistry();

        // Create sync directories at the new location
        if (newEventStore != null)
        {
            Directory.CreateDirectory(newEventStore);
            _log.Information("Created event store directory: {Dir}", newEventStore);
        }

        var newSnapshotDir = ResolveSnapshotDir(updatedWorkspace);
        if (newSnapshotDir != null)
            Directory.CreateDirectory(newSnapshotDir);

        var newSharedFilesDir = ResolveSharedFilesDir(updatedWorkspace);
        if (newSharedFilesDir != null)
            Directory.CreateDirectory(newSharedFilesDir);

        // Fire workspace changed so FileEventSyncService restarts with new location
        if (_registry.ActiveWorkspaceId == workspaceId)
        {
            WorkspaceChanged?.Invoke(this, updatedWorkspace);
        }

        progress?.Report(new WorkspaceMigrationProgress(0, 0, 0, 0, "", MigrationPhase.Complete));

        _log.Information("Workspace {Id} sync location updated to {Type}",
            workspaceId, newLocation.Type);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a workspace. Cannot delete the active workspace.
    /// </summary>
    public void DeleteWorkspace(string workspaceId)
    {
        if (_registry.ActiveWorkspaceId == workspaceId)
            throw new InvalidOperationException("Cannot delete the active workspace. Switch to another workspace first.");

        var workspace = _registry.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null) return;

        // Remove from registry
        var workspaces = new List<Workspace>(_registry.Workspaces);
        workspaces.RemoveAll(w => w.Id == workspaceId);
        _registry = _registry with { Workspaces = workspaces };
        SaveRegistry();

        // Delete workspace directory at resolved path
        var workspaceDir = ResolveWorkspaceDir(workspace);
        if (Directory.Exists(workspaceDir))
        {
            try
            {
                Directory.Delete(workspaceDir, recursive: true);
                _log.Information("Deleted workspace directory: {Dir}", workspaceDir);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to delete workspace directory: {Dir}", workspaceDir);
            }
        }

        _log.Information("Deleted workspace: {Name} ({Id})", workspace.Name, workspaceId);
    }

    /// <summary>
    /// One-time migration: converts global AppSettings.DataDirectoryType into per-workspace StorageLocation.
    /// </summary>
    private void MigrateGlobalStorageSettings()
    {
        try
        {
            var settingsPath = Path.Combine(_basePath, "window-settings.json");
            if (!File.Exists(settingsPath)) return;

            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data_directory_type", out var typeProp))
                return;

            var dirType = typeProp.GetString();
            if (string.IsNullOrEmpty(dirType) || dirType == "Default")
                return;

            // Check if any workspace still needs migration (has null StorageLocation)
            var needsMigration = _registry.Workspaces.Any(w => w.StorageLocation == null);
            if (!needsMigration) return;

            string? customPath = null;
            if (root.TryGetProperty("custom_data_directory", out var customProp))
                customPath = customProp.GetString();

            var location = new StorageLocation
            {
                Type = dirType,
                CustomPath = customPath
            };

            var updated = _registry.Workspaces.Select(w =>
                w.StorageLocation == null ? w with { StorageLocation = location } : w).ToList();

            _registry = _registry with { Workspaces = updated };
            SaveRegistry();

            _log.Information("Migrated global storage setting ({Type}) into {Count} workspace records",
                dirType, updated.Count(w => w.StorageLocation?.Type == dirType));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to migrate global storage settings — workspaces will use default paths");
        }
    }

    /// <summary>
    /// One-time migration: copies DB files from old cloud/NAS paths to local workspace dirs.
    /// Previously, ResolveWorkspaceDir pointed directly to cloud paths; now DB always stays local.
    /// </summary>
    private void MigrateCloudDbToLocal()
    {
        try
        {
            foreach (var workspace in _registry.Workspaces)
            {
                if (workspace.StorageLocation == null || workspace.StorageLocation.Type == "Default")
                    continue;

                var localDir = Path.Combine(_basePath, "workspaces", workspace.Id);
                var localDb = Path.Combine(localDir, "data.duckdb");

                // Already migrated — local DB exists
                if (File.Exists(localDb)) continue;

                // Resolve the old cloud path where DB files used to live
                var oldDir = ResolveOldCloudWorkspaceDir(workspace.Id, workspace.StorageLocation);
                if (oldDir == null) continue;

                var oldDb = Path.Combine(oldDir, "data.duckdb");
                if (!File.Exists(oldDb)) continue;

                _log.Information("Migrating workspace {Id} DB from cloud path {Old} to local {New}",
                    workspace.Id, oldDir, localDir);

                Directory.CreateDirectory(localDir);

                // Copy all files from the old directory to the local one
                foreach (var file in Directory.GetFiles(oldDir))
                {
                    var destFile = Path.Combine(localDir, Path.GetFileName(file));
                    if (!File.Exists(destFile))
                    {
                        File.Copy(file, destFile);
                    }
                }

                _log.Information("Cloud-to-local DB migration complete for workspace {Id}", workspace.Id);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Cloud-to-local DB migration failed — workspaces may need manual migration");
        }
    }

    /// <summary>
    /// Resolves the old cloud workspace directory (pre-migration layout where DB lived at cloud path).
    /// Only used for backward-compat migration.
    /// </summary>
    private string? ResolveOldCloudWorkspaceDir(string workspaceId, StorageLocation location)
    {
        return location.Type switch
        {
            "Custom" when !string.IsNullOrEmpty(location.CustomPath) =>
                Path.Combine(location.CustomPath, "PrivStack", "workspaces", workspaceId),
            "GoogleDrive" => CloudPathResolver.GetGoogleDrivePath() is { } gd
                ? Path.Combine(gd, "PrivStack", "workspaces", workspaceId)
                : null,
            "ICloud" => CloudPathResolver.GetICloudPath() is { } ic
                ? Path.Combine(ic, "PrivStack", "workspaces", workspaceId)
                : null,
            _ => null,
        };
    }

    private void LoadRegistry()
    {
        try
        {
            if (File.Exists(_registryPath))
            {
                var json = File.ReadAllText(_registryPath);
                _registry = JsonSerializer.Deserialize<WorkspaceRegistry>(json) ?? new WorkspaceRegistry();
                _log.Debug("Workspace registry loaded: {Count} workspaces", _registry.Workspaces.Count);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load workspace registry");
            _registry = new WorkspaceRegistry();
        }
    }

    private void SaveRegistry()
    {
        try
        {
            var json = JsonSerializer.Serialize(_registry, _jsonOptions);
            File.WriteAllText(_registryPath, json);
            _log.Debug("Workspace registry saved");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save workspace registry");
        }
    }

    private static string GetBasePath()
    {
        return DataPaths.BaseDir;
    }


    /// <summary>
    /// Converts a workspace name to a URL-safe slug.
    /// </summary>
    private static string Slugify(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = SlugInvalidChars().Replace(slug, "");
        slug = SlugWhitespace().Replace(slug, "-");
        slug = SlugMultipleDashes().Replace(slug, "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "workspace" : slug;
    }

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex SlugInvalidChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SlugWhitespace();

    [GeneratedRegex(@"-+")]
    private static partial Regex SlugMultipleDashes();
}
