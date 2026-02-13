using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// One-time migration that moves legacy root-level data into workspace-scoped directories.
/// Runs once per workspace (marker file: .data-migrated in the workspace dir).
/// </summary>
public static class WorkspaceDataMigration
{
    private static readonly ILogger _log = Log.ForContext(nameof(WorkspaceDataMigration));

    /// <summary>
    /// Migrates legacy root-level directories into the active workspace if not already done.
    /// Safe to call multiple times — uses a marker file to skip subsequent runs.
    /// </summary>
    public static void MigrateIfNeeded(string workspaceId, string resolvedWorkspaceDir)
    {
        try
        {
            var baseDir = DataPaths.BaseDir;
            var wsDir = resolvedWorkspaceDir;
            var marker = Path.Combine(wsDir, ".data-migrated");

            if (File.Exists(marker))
                return;

            Directory.CreateDirectory(wsDir);

            MoveDirectoryContents(
                Path.Combine(baseDir, "backups"),
                Path.Combine(wsDir, "backups"));

            MoveDirectoryContents(
                Path.Combine(baseDir, "quill-images"),
                Path.Combine(wsDir, "files", "notes"));

            MoveDirectoryContents(
                Path.Combine(baseDir, "models", "whisper"),
                Path.Combine(wsDir, "models", "whisper"));

            // Write marker so we don't re-run
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            _log.Information("Workspace data migration completed for {WorkspaceId}", workspaceId);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Workspace data migration failed for {WorkspaceId}", workspaceId);
        }
    }

    private static void MoveDirectoryContents(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        var files = Directory.GetFiles(source);
        if (files.Length == 0)
            return;

        Directory.CreateDirectory(destination);

        foreach (var file in files)
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(destFile))
            {
                _log.Debug("Skipping migration (already exists): {File}", Path.GetFileName(file));
                continue;
            }

            try
            {
                File.Move(file, destFile);
                _log.Debug("Migrated: {File} → {Dest}", Path.GetFileName(file), destination);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to migrate file: {File}", file);
            }
        }
    }
}
