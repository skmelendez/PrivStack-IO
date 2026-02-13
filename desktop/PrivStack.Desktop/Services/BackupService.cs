using System.IO.Compression;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Service for creating and managing data backups.
/// Creates compressed zip archives of the data directory.
/// </summary>
public class BackupService : IBackupService
{
    private static readonly ILogger _log = Log.ForContext<BackupService>();

    private readonly IAppSettingsService _settings;
    private System.Timers.Timer? _scheduledBackupTimer;
    private DateTime? _lastBackupTime;

    /// <summary>
    /// Event raised when a backup operation completes.
    /// </summary>
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;

    public BackupService(IAppSettingsService settings)
    {
        _settings = settings;
        LoadLastBackupTime();
        StartScheduledBackups();
    }

    /// <summary>
    /// Gets the data directory to backup (workspace-scoped).
    /// </summary>
    public string DataDirectory
    {
        get
        {
            // Prefer workspace-scoped directory; fall back to root for pre-migration compat
            return DataPaths.WorkspaceDataDir ?? DataPaths.BaseDir;
        }
    }

    /// <summary>
    /// Gets the backup destination directory (workspace-scoped).
    /// </summary>
    public string BackupDirectory
    {
        get
        {
            if (_settings.Settings.BackupDirectory != null)
                return _settings.Settings.BackupDirectory;

            return Path.Combine(DataDirectory, "backups");
        }
    }

    /// <summary>
    /// Creates an immediate backup of the data directory.
    /// </summary>
    /// <returns>The path to the created backup file, or null if failed.</returns>
    public async Task<string?> BackupNowAsync()
    {
        try
        {
            _log.Information("Starting manual backup");

            var backupPath = await CreateBackupAsync();

            if (backupPath != null)
            {
                _lastBackupTime = DateTime.Now;
                SaveLastBackupTime();

                // Handle rolling backups
                if (_settings.Settings.BackupType == BackupType.Rolling.ToString())
                {
                    await EnforceMaxBackupsAsync();
                }

                BackupCompleted?.Invoke(this, new BackupCompletedEventArgs(true, backupPath, null));
                _log.Information("Manual backup completed: {Path}", backupPath);
            }

            return backupPath;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to create manual backup");
            BackupCompleted?.Invoke(this, new BackupCompletedEventArgs(false, null, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Creates a compressed zip backup of the data directory.
    /// Handles locked files by copying them first.
    /// </summary>
    private async Task<string?> CreateBackupAsync()
    {
        return await Task.Run(() =>
        {
            var dataDir = DataDirectory;
            var backupDir = BackupDirectory;

            _log.Information("Backup source directory: {DataDir}", dataDir);
            _log.Information("Backup destination directory: {BackupDir}", backupDir);

            if (!Directory.Exists(dataDir))
            {
                _log.Warning("Data directory does not exist: {Path}", dataDir);
                return null;
            }

            // Ensure backup directory exists
            Directory.CreateDirectory(backupDir);

            // Generate backup filename with timestamp
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupFileName = $"privstack_backup_{timestamp}.zip";
            var backupPath = Path.Combine(backupDir, backupFileName);

            _log.Debug("Creating backup at {Path}", backupPath);

            // Normalize paths for comparison - backup dir must be inside data dir to be excluded
            var normalizedDataDir = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedBackupDir = Path.GetFullPath(backupDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Only exclude backup dir if it's actually inside data dir
            bool backupDirInsideDataDir = normalizedBackupDir.StartsWith(normalizedDataDir, StringComparison.OrdinalIgnoreCase);
            _log.Debug("Backup dir inside data dir: {Inside}", backupDirInsideDataDir);

            // Create zip archive
            int fileCount = 0;
            int skippedCount = 0;
            using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
            {
                var allFiles = Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories);
                _log.Information("Found {Count} total files in data directory", allFiles.Length);

                foreach (var file in allFiles)
                {
                    var normalizedFile = Path.GetFullPath(file);

                    // Skip files in backup directory (only if backup dir is inside data dir)
                    if (backupDirInsideDataDir && normalizedFile.StartsWith(normalizedBackupDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Debug("Skipping backup file: {File}", file);
                        continue;
                    }

                    // Skip log files
                    if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Debug("Skipping log file: {File}", file);
                        continue;
                    }

                    // Skip logs directory entirely
                    if (normalizedFile.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Debug("Skipping log directory file: {File}", file);
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(dataDir, file);

                    // Try to add file directly first, then copy if locked
                    if (TryAddFileToArchive(archive, file, relativePath))
                    {
                        fileCount++;
                        _log.Debug("Added to backup: {RelativePath}", relativePath);
                    }
                    else
                    {
                        skippedCount++;
                        _log.Warning("Could not backup file (may be locked): {File}", file);
                    }
                }
            }

            var fileInfo = new FileInfo(backupPath);
            _log.Information("Backup created: {Path} ({Size:N0} bytes, {FileCount} files, {SkippedCount} skipped)",
                backupPath, fileInfo.Length, fileCount, skippedCount);

            return backupPath;
        });
    }

    /// <summary>
    /// Tries to add a file to the archive, handling locked files by copying first.
    /// </summary>
    private bool TryAddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        // First try direct access
        try
        {
            archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
            return true;
        }
        catch (IOException)
        {
            // File is likely locked, try copying first
        }

        // Try copying to temp file first (works with locked database files)
        string? tempFile = null;
        try
        {
            tempFile = Path.GetTempFileName();

            // Use FileShare.ReadWrite to allow copying even if file is in use
            using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            {
                sourceStream.CopyTo(destStream);
            }

            archive.CreateEntryFromFile(tempFile, entryName, CompressionLevel.Optimal);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to copy and backup file: {File}", filePath);
            return false;
        }
        finally
        {
            // Clean up temp file
            if (tempFile != null && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    /// <summary>
    /// Removes old backups to stay within the max backup count.
    /// </summary>
    private async Task EnforceMaxBackupsAsync()
    {
        await Task.Run(() =>
        {
            var backupDir = BackupDirectory;
            var maxBackups = _settings.Settings.MaxBackups;

            if (!Directory.Exists(backupDir))
                return;

            var backupFiles = Directory.GetFiles(backupDir, "privstack_backup_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (backupFiles.Count <= maxBackups)
                return;

            // Delete oldest backups
            var filesToDelete = backupFiles.Skip(maxBackups).ToList();
            foreach (var file in filesToDelete)
            {
                try
                {
                    file.Delete();
                    _log.Information("Deleted old backup: {Path}", file.FullName);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to delete old backup: {Path}", file.FullName);
                }
            }
        });
    }

    /// <summary>
    /// Gets a list of existing backup files.
    /// </summary>
    public IEnumerable<BackupInfo> GetExistingBackups()
    {
        var backupDir = BackupDirectory;

        if (!Directory.Exists(backupDir))
            return Enumerable.Empty<BackupInfo>();

        return Directory.GetFiles(backupDir, "privstack_backup_*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .Select(f => new BackupInfo(f.FullName, f.CreationTime, f.Length));
    }

    /// <summary>
    /// Restores data from a backup file.
    /// </summary>
    public async Task<bool> RestoreBackupAsync(string backupPath)
    {
        try
        {
            _log.Information("Starting restore from {Path}", backupPath);

            return await Task.Run(() =>
            {
                if (!File.Exists(backupPath))
                {
                    _log.Error("Backup file not found: {Path}", backupPath);
                    return false;
                }

                var dataDir = DataDirectory;

                // Create a temporary restore directory
                var tempRestoreDir = Path.Combine(Path.GetTempPath(), $"privstack_restore_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempRestoreDir);

                try
                {
                    // Extract to temp location first
                    ZipFile.ExtractToDirectory(backupPath, tempRestoreDir);

                    // Copy restored files to data directory
                    foreach (var file in Directory.GetFiles(tempRestoreDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(tempRestoreDir, file);
                        var destPath = Path.Combine(dataDir, relativePath);

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(file, destPath, overwrite: true);
                    }

                    _log.Information("Restore completed successfully");
                    return true;
                }
                finally
                {
                    // Clean up temp directory
                    try
                    {
                        Directory.Delete(tempRestoreDir, recursive: true);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to restore backup from {Path}", backupPath);
            return false;
        }
    }

    /// <summary>
    /// Starts the scheduled backup timer based on settings.
    /// </summary>
    public void StartScheduledBackups()
    {
        StopScheduledBackups();

        if (!Enum.TryParse<BackupFrequency>(_settings.Settings.BackupFrequency, out var frequency))
            frequency = BackupFrequency.Daily;

        if (frequency == BackupFrequency.Manual)
        {
            _log.Debug("Backup frequency is Manual, no scheduled backups");
            return;
        }

        var intervalMs = frequency switch
        {
            BackupFrequency.Hourly => TimeSpan.FromHours(1).TotalMilliseconds,
            BackupFrequency.Daily => TimeSpan.FromDays(1).TotalMilliseconds,
            BackupFrequency.Weekly => TimeSpan.FromDays(7).TotalMilliseconds,
            _ => TimeSpan.FromDays(1).TotalMilliseconds
        };

        _scheduledBackupTimer = new System.Timers.Timer(intervalMs);
        _scheduledBackupTimer.Elapsed += async (_, _) => await PerformScheduledBackupAsync();
        _scheduledBackupTimer.AutoReset = true;
        _scheduledBackupTimer.Start();

        _log.Information("Scheduled backups started with frequency: {Frequency}", frequency);

        // Check if we need an immediate backup based on last backup time
        _ = CheckInitialBackupAsync(frequency);
    }

    private async Task CheckInitialBackupAsync(BackupFrequency frequency)
    {
        if (_lastBackupTime == null)
        {
            _log.Debug("No previous backup found, creating initial backup");
            await BackupNowAsync();
            return;
        }

        var timeSinceLastBackup = DateTime.Now - _lastBackupTime.Value;
        var shouldBackup = frequency switch
        {
            BackupFrequency.Hourly => timeSinceLastBackup > TimeSpan.FromHours(1),
            BackupFrequency.Daily => timeSinceLastBackup > TimeSpan.FromDays(1),
            BackupFrequency.Weekly => timeSinceLastBackup > TimeSpan.FromDays(7),
            _ => false
        };

        if (shouldBackup)
        {
            _log.Debug("Backup overdue (last backup: {LastBackup}), creating backup", _lastBackupTime);
            await BackupNowAsync();
        }
    }

    private async Task PerformScheduledBackupAsync()
    {
        _log.Information("Performing scheduled backup");
        await BackupNowAsync();
    }

    /// <summary>
    /// Stops the scheduled backup timer.
    /// </summary>
    public void StopScheduledBackups()
    {
        _scheduledBackupTimer?.Stop();
        _scheduledBackupTimer?.Dispose();
        _scheduledBackupTimer = null;
    }

    private void LoadLastBackupTime()
    {
        var backupDir = BackupDirectory;
        if (!Directory.Exists(backupDir))
            return;

        var latestBackup = Directory.GetFiles(backupDir, "privstack_backup_*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefault();

        if (latestBackup != null)
        {
            _lastBackupTime = latestBackup.CreationTime;
            _log.Debug("Last backup time: {Time}", _lastBackupTime);
        }
    }

    private void SaveLastBackupTime()
    {
        // Last backup time is inferred from the latest backup file
        // No need to persist separately
    }

    /// <summary>
    /// Updates the backup directory and restarts scheduled backups if needed.
    /// </summary>
    public void UpdateBackupDirectory(string newPath)
    {
        _settings.Settings.BackupDirectory = newPath;
        _settings.SaveDebounced();

        LoadLastBackupTime();
        StartScheduledBackups();
    }

    /// <summary>
    /// Updates the backup frequency and restarts scheduled backups.
    /// </summary>
    public void UpdateBackupFrequency(BackupFrequency frequency)
    {
        _settings.Settings.BackupFrequency = frequency.ToString();
        _settings.SaveDebounced();
        StartScheduledBackups();
    }
}

/// <summary>
/// Information about an existing backup file.
/// </summary>
public record BackupInfo(string Path, DateTime CreatedAt, long SizeBytes)
{
    public string FormattedSize => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
    };
}

/// <summary>
/// Event args for backup completion.
/// </summary>
public class BackupCompletedEventArgs : EventArgs
{
    public bool Success { get; }
    public string? BackupPath { get; }
    public string? ErrorMessage { get; }

    public BackupCompletedEventArgs(bool success, string? backupPath, string? errorMessage)
    {
        Success = success;
        BackupPath = backupPath;
        ErrorMessage = errorMessage;
    }
}
