using System.Security.Cryptography;
using System.Text.Json;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Handles dataset file sync: copies imported CSVs to the shared files directory
/// with .privstack sidecar files, and scans for sidecars to auto-import on new devices.
/// </summary>
internal static class DatasetFileSyncHelper
{
    private static readonly ILogger _log = Log.ForContext(nameof(DatasetFileSyncHelper));
    private const string ImportsSubdir = "Data/Imports";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Copies the source CSV to the shared files directory and writes a .privstack sidecar.
    /// No-op if workspace has no shared files directory.
    /// </summary>
    public static void CopyToSharedDir(
        string sourceCsvPath,
        DatasetInfo datasetInfo,
        IWorkspaceService workspaceService,
        string peerId)
    {
        var workspace = workspaceService.GetActiveWorkspace();
        if (workspace == null) return;

        var sharedFilesDir = workspaceService.ResolveSharedFilesDir(workspace);
        if (sharedFilesDir == null) return;

        try
        {
            var importsDir = Path.Combine(sharedFilesDir, ImportsSubdir);
            Directory.CreateDirectory(importsDir);

            var filename = Path.GetFileName(sourceCsvPath);
            var destPath = Path.Combine(importsDir, filename);

            // Copy CSV to shared dir (skip if already exists with same size)
            var sourceInfo = new FileInfo(sourceCsvPath);
            if (!File.Exists(destPath) || new FileInfo(destPath).Length != sourceInfo.Length)
            {
                File.Copy(sourceCsvPath, destPath, overwrite: true);
                _log.Information("Copied dataset CSV to shared dir: {Path}", destPath);
            }

            // Write sidecar
            var sidecar = new DatasetSidecar
            {
                DatasetId = datasetInfo.Id.ToString(),
                DatasetName = datasetInfo.Name,
                SourceFilename = filename,
                FileSize = sourceInfo.Length,
                FileHash = ComputeFileHash(sourceCsvPath),
                ImportedAt = DateTimeOffset.UtcNow,
                PeerId = peerId,
            };

            var sidecarPath = destPath + ".privstack";
            var json = JsonSerializer.Serialize(sidecar, _jsonOptions);
            File.WriteAllText(sidecarPath, json);

            _log.Information("Wrote dataset sidecar: {Path}", sidecarPath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to copy dataset to shared files dir — sync will use snapshot only");
        }
    }

    /// <summary>
    /// Scans the shared files directory for .privstack sidecars and auto-imports
    /// any datasets that don't already exist locally.
    /// </summary>
    public static async Task ScanAndImportAsync(
        IWorkspaceService workspaceService,
        IDatasetService datasetService,
        CancellationToken ct = default)
    {
        var workspace = workspaceService.GetActiveWorkspace();
        if (workspace == null) return;

        var sharedFilesDir = workspaceService.ResolveSharedFilesDir(workspace);
        if (sharedFilesDir == null) return;

        var importsDir = Path.Combine(sharedFilesDir, ImportsSubdir);
        if (!Directory.Exists(importsDir)) return;

        string[] sidecarFiles;
        try
        {
            sidecarFiles = Directory.GetFiles(importsDir, "*.privstack");
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "Could not list sidecar files in {Dir}", importsDir);
            return;
        }

        if (sidecarFiles.Length == 0) return;

        // Get existing datasets to avoid re-importing
        var existingDatasets = await datasetService.ListDatasetsAsync(ct);
        var existingIds = new HashSet<string>(existingDatasets.Select(d => d.Id.ToString()));

        var imported = 0;
        var syncing = 0;

        foreach (var sidecarPath in sidecarFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(sidecarPath, ct);
                var sidecar = JsonSerializer.Deserialize<DatasetSidecar>(json, _jsonOptions);
                if (sidecar == null) continue;

                // Already imported
                if (existingIds.Contains(sidecar.DatasetId)) continue;

                // Check if the CSV file is present
                var csvPath = sidecarPath[..^".privstack".Length];
                if (!File.Exists(csvPath))
                {
                    _log.Debug("Dataset CSV not yet synced: {File} (sidecar present)",
                        sidecar.SourceFilename);
                    syncing++;
                    continue;
                }

                // Auto-import the dataset
                await datasetService.ImportCsvAsync(csvPath, sidecar.DatasetName, ct);
                imported++;

                _log.Information("Auto-imported dataset from shared dir: {Name} ({Id})",
                    sidecar.DatasetName, sidecar.DatasetId);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to process sidecar: {Path}", sidecarPath);
            }
        }

        if (imported > 0 || syncing > 0)
        {
            _log.Information("Dataset file sync: {Imported} imported, {Syncing} still syncing",
                imported, syncing);
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }
}
