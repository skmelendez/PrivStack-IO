namespace PrivStack.Desktop.Services;

/// <summary>
/// Central data directory resolution. Supports PRIVSTACK_DATA_DIR env var override
/// for isolated testing (e.g., build.sh --with-plugins).
/// </summary>
public static class DataPaths
{
    private static string? _cached;
    private static string? _activeWorkspaceId;
    private static string? _activeWorkspaceDir;

    /// <summary>
    /// The base PrivStack data directory. Checks PRIVSTACK_DATA_DIR env var first,
    /// then falls back to the platform default (LocalApplicationData/PrivStack).
    /// </summary>
    public static string BaseDir => _cached ??= ResolveBaseDir();

    /// <summary>
    /// The active workspace data directory. Uses the resolved directory passed to
    /// SetActiveWorkspace, which supports per-workspace storage locations.
    /// Null if no workspace is active (e.g., before setup wizard completes).
    /// </summary>
    public static string? WorkspaceDataDir => _activeWorkspaceDir;

    /// <summary>
    /// Sets the active workspace with its fully resolved directory path.
    /// </summary>
    public static void SetActiveWorkspace(string workspaceId, string resolvedDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedDir);
        _activeWorkspaceId = workspaceId;
        _activeWorkspaceDir = resolvedDir;
    }

    /// <summary>
    /// Clears the active workspace. WorkspaceDataDir will return null.
    /// </summary>
    public static void ClearActiveWorkspace()
    {
        _activeWorkspaceId = null;
        _activeWorkspaceDir = null;
    }

    private static string ResolveBaseDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("PRIVSTACK_DATA_DIR");
        if (!string.IsNullOrEmpty(envOverride))
            return envOverride;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "PrivStack");
    }
}
