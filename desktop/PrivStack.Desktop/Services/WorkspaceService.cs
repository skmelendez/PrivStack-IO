using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
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
    /// </summary>
    public string GetActiveDataPath()
    {
        var active = GetActiveWorkspace();
        return active != null
            ? GetDataPath(active.Id)
            : Path.Combine(GetLegacyDataDirectory(), "data.duckdb");
    }

    /// <summary>
    /// Gets the database path for a given workspace ID.
    /// </summary>
    public string GetDataPath(string workspaceId)
    {
        return Path.Combine(_basePath, "workspaces", workspaceId, "data.duckdb");
    }

    /// <summary>
    /// Creates a new workspace with the given name.
    /// </summary>
    public Workspace CreateWorkspace(string name)
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
            CreatedAt = DateTime.UtcNow
        };

        // Create workspace directory
        var workspaceDir = Path.Combine(_basePath, "workspaces", slug);
        Directory.CreateDirectory(workspaceDir);

        var workspaces = new List<Workspace>(_registry.Workspaces) { workspace };

        // If this is the first workspace, make it active
        var activeId = _registry.ActiveWorkspaceId ?? slug;

        _registry = new WorkspaceRegistry
        {
            Workspaces = workspaces,
            ActiveWorkspaceId = activeId
        };
        SaveRegistry();

        _log.Information("Created workspace: {Name} ({Id})", name, slug);
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
        }
        finally
        {
            sdkHost.EndWorkspaceSwitch();
        }

        _log.Information("Workspace switched, service re-initialized at: {Path}", GetDataPath(workspaceId));

        WorkspaceChanged?.Invoke(this, workspace);
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

        // Delete workspace directory
        var workspaceDir = Path.Combine(_basePath, "workspaces", workspaceId);
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
    /// Gets the legacy (pre-workspace) data directory for migration/fallback.
    /// </summary>
    private static string GetLegacyDataDirectory()
    {
        var customDir = App.Services.GetRequiredService<Abstractions.IAppSettingsService>().Settings.CustomDataDirectory;
        if (!string.IsNullOrEmpty(customDir))
            return customDir;

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
