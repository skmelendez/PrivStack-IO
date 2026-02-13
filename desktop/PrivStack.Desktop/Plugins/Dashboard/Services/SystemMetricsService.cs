using System.Collections.ObjectModel;
using System.Diagnostics;
using PrivStack.Desktop.Plugins.Dashboard.Models;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Plugins.Dashboard.Services;

/// <summary>
/// Gathers system-level metrics: app shell size, plugin binary sizes,
/// memory usage, and per-plugin data storage.
/// </summary>
internal sealed class SystemMetricsService
{
    private static readonly ILogger _log = Log.ForContext<SystemMetricsService>();

    /// <summary>
    /// Enumerates files in the app base directory (excluding plugins/) and returns total bytes.
    /// </summary>
    public Task<long> GetAppShellSizeBytesAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
                if (!baseDir.Exists) return 0L;

                var pluginsDir = Path.Combine(baseDir.FullName, "plugins");

                return baseDir.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => !f.FullName.StartsWith(pluginsDir, StringComparison.OrdinalIgnoreCase))
                    .Sum(f => f.Length);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to calculate app shell size");
                return 0L;
            }
        });
    }

    /// <summary>
    /// Scans plugin directories for installed plugin binaries and returns per-plugin sizes.
    /// </summary>
    public Task<List<PluginSizeInfo>> GetPluginBinarySizesAsync(IPluginRegistry registry)
    {
        return Task.Run(() =>
        {
            var results = new List<PluginSizeInfo>();

            try
            {
                var dirs = GetPluginDirectories();
                var pluginLookup = registry.Plugins.ToDictionary(
                    p => p.Metadata.Id.ToLowerInvariant(),
                    p => p);

                // Track which plugin IDs we've already seen (bundled takes priority)
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var parentDir in dirs)
                {
                    if (!Directory.Exists(parentDir)) continue;

                    foreach (var pluginDir in Directory.GetDirectories(parentDir))
                    {
                        var dirName = Path.GetFileName(pluginDir).ToLowerInvariant();

                        // Try to match directory name to a plugin ID
                        var pluginId = dirName.StartsWith("privstack.plugin.")
                            ? dirName
                            : $"privstack.plugin.{dirName}";

                        if (!seen.Add(pluginId)) continue;

                        var dirInfo = new DirectoryInfo(pluginDir);
                        var totalSize = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                            .Sum(f => f.Length);

                        var name = dirName;
                        var icon = "Package";

                        if (pluginLookup.TryGetValue(pluginId, out var plugin))
                        {
                            name = plugin.Metadata.Name;
                            icon = plugin.Metadata.Icon ?? "Package";
                        }

                        results.Add(new PluginSizeInfo
                        {
                            PluginId = pluginId,
                            Name = name,
                            Icon = icon,
                            SizeBytes = totalSize
                        });
                    }
                }

                results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to calculate plugin binary sizes");
            }

            return results;
        });
    }

    /// <summary>
    /// Returns (WorkingSet64, GcHeapSize) — cheap, synchronous.
    /// </summary>
    public (long WorkingSet, long GcHeap) GetMemoryMetrics()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            return (proc.WorkingSet64, GC.GetTotalMemory(false));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Queries all IDataMetricsProvider plugins, measures actual DuckDB file sizes,
    /// and proportionally allocates on-disk bytes to each table.
    /// </summary>
    public async Task<DataMetricsResult> GetDataMetricsAsync(
        IPluginRegistry registry, IWorkspaceService workspaceService)
    {
        var allTables = new List<DataTableInfo>();
        var pluginMetrics = new List<(string Name, string Icon, int EntityCount, long EstimatedSize, List<DataTableInfo> Tables)>();

        long estEntityBytes = 0;
        long estFilesBytes = 0;
        long estVaultBytes = 0;

        try
        {
            var providers = registry.GetCapabilityProviders<IDataMetricsProvider>();

            foreach (var provider in providers)
            {
                try
                {
                    var metrics = await provider.GetMetricsAsync();
                    if (metrics.EntityCount == 0 && metrics.Tables.Count == 0)
                        continue;

                    pluginMetrics.Add((provider.ProviderName, provider.ProviderIcon,
                        metrics.EntityCount, metrics.EstimatedSizeBytes, metrics.Tables.ToList()));

                    foreach (var table in metrics.Tables)
                    {
                        allTables.Add(table);
                        switch (table.BackingMode)
                        {
                            case "file": estFilesBytes += table.EstimatedSizeBytes; break;
                            case "blob": estVaultBytes += table.EstimatedSizeBytes; break;
                            default: estEntityBytes += table.EstimatedSizeBytes; break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to get metrics from {Provider}", provider.ProviderName);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to query data metrics providers");
        }

        // Measure actual DuckDB file sizes on disk
        var (entityFileSize, datasetFileSize, blobFileSize) =
            MeasureDuckDbFiles(workspaceService);

        // Proportionally allocate actual bytes to tables by backing mode group
        var actualEntityTotal = entityFileSize + datasetFileSize;
        var updatedTables = AllocateActualBytes(allTables, actualEntityTotal, blobFileSize);

        // Build updated plugin data items with actual sizes
        var items = new ObservableCollection<PluginDataInfo>();
        foreach (var (name, icon, entityCount, estimatedSize, tables) in pluginMetrics)
        {
            long pluginActual = 0;
            long pluginEstimated = 0;
            var updatedPluginTables = new ObservableCollection<DataTableInfo>();

            foreach (var origTable in tables)
            {
                var updated = updatedTables.TryGetValue(origTable, out var u) ? u : origTable;
                updatedPluginTables.Add(updated);
                pluginActual += updated.ActualSizeBytes;
                pluginEstimated += updated.EstimatedSizeBytes;
            }

            items.Add(new PluginDataInfo
            {
                Name = name,
                Icon = icon,
                EntityCount = entityCount,
                FormattedSize = SystemMetricsHelper.FormatBytesWithEstimate(pluginActual, pluginEstimated),
                Tables = updatedPluginTables,
            });
        }

        // Scan managed files directory (already accurate — real file sizes)
        long diskFilesSize = 0;
        try
        {
            var managedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrivStack", "files");

            if (Directory.Exists(managedDir))
            {
                diskFilesSize = await Task.Run(() =>
                    new DirectoryInfo(managedDir)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length));
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to scan managed files directory");
        }

        var actualTotal = actualEntityTotal + diskFilesSize + blobFileSize;
        var estTotal = estEntityBytes + estFilesBytes + estVaultBytes;

        return new DataMetricsResult
        {
            PluginDataItems = items,
            TotalDatabaseBytes = actualEntityTotal,
            TotalFilesBytes = diskFilesSize,
            TotalVaultBytes = blobFileSize,
            TotalStorageBytes = actualTotal,
            EstimatedDatabaseBytes = estEntityBytes,
            EstimatedVaultBytes = estVaultBytes,
            EstimatedStorageBytes = estTotal,
        };
    }

    /// <summary>
    /// Measures actual DuckDB file sizes (including WAL files) for the active workspace.
    /// </summary>
    private static (long EntityFile, long DatasetFile, long BlobFile) MeasureDuckDbFiles(
        IWorkspaceService workspaceService)
    {
        try
        {
            var dbPath = workspaceService.GetActiveDataPath();
            if (string.IsNullOrEmpty(dbPath)) return (0, 0, 0);

            var dir = Path.GetDirectoryName(dbPath);
            if (dir == null || !Directory.Exists(dir)) return (0, 0, 0);

            var entityPath = Path.Combine(dir, "data.entities.duckdb");
            var datasetPath = Path.Combine(dir, "data.datasets.duckdb");
            var blobPath = Path.Combine(dir, "data.blobs.duckdb");

            return (
                GetFileAndWalSize(entityPath),
                GetFileAndWalSize(datasetPath),
                GetFileAndWalSize(blobPath));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to measure DuckDB file sizes");
            return (0, 0, 0);
        }
    }

    private static long GetFileAndWalSize(string path)
    {
        long size = 0;
        if (File.Exists(path))
            size += new FileInfo(path).Length;
        var walPath = path + ".wal";
        if (File.Exists(walPath))
            size += new FileInfo(walPath).Length;
        return size;
    }

    /// <summary>
    /// Proportionally allocates actual on-disk bytes to tables based on their estimated size
    /// within each backing mode group.
    /// </summary>
    private static Dictionary<DataTableInfo, DataTableInfo> AllocateActualBytes(
        List<DataTableInfo> tables, long actualEntityBytes, long actualBlobBytes)
    {
        var result = new Dictionary<DataTableInfo, DataTableInfo>();

        var entityTables = tables.Where(t => t.BackingMode != "file" && t.BackingMode != "blob").ToList();
        var blobTables = tables.Where(t => t.BackingMode == "blob").ToList();
        // "file" tables are already measured from disk scan, no allocation needed

        AllocateGroup(entityTables, actualEntityBytes, result);
        AllocateGroup(blobTables, actualBlobBytes, result);

        return result;
    }

    private static void AllocateGroup(
        List<DataTableInfo> tables, long actualTotal,
        Dictionary<DataTableInfo, DataTableInfo> result)
    {
        if (actualTotal <= 0 || tables.Count == 0) return;

        var groupEstimated = tables.Sum(t => t.EstimatedSizeBytes);
        if (groupEstimated <= 0)
        {
            // Distribute evenly if no estimates
            var perTable = actualTotal / tables.Count;
            foreach (var t in tables)
                result[t] = t with { ActualSizeBytes = perTable };
            return;
        }

        foreach (var t in tables)
        {
            var proportion = (double)t.EstimatedSizeBytes / groupEstimated;
            var actual = (long)(proportion * actualTotal);
            result[t] = t with { ActualSizeBytes = actual };
        }
    }

    private static List<string> GetPluginDirectories()
    {
        var dirs = new List<string>();

        var bundledDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(bundledDir))
        {
            dirs.Add(bundledDir);
        }
        else
        {
            var devDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins");
            if (Directory.Exists(devDir))
                dirs.Add(Path.GetFullPath(devDir));
        }

        var userPluginDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".privstack", "plugins");
        if (Directory.Exists(userPluginDir))
            dirs.Add(userPluginDir);

        return dirs;
    }
}

/// <summary>
/// Result container for data metrics aggregation.
/// </summary>
internal sealed class DataMetricsResult
{
    public ObservableCollection<PluginDataInfo> PluginDataItems { get; init; } = [];
    public long TotalDatabaseBytes { get; init; }
    public long TotalFilesBytes { get; init; }
    public long TotalVaultBytes { get; init; }
    public long TotalStorageBytes { get; init; }
    public long EstimatedDatabaseBytes { get; init; }
    public long EstimatedVaultBytes { get; init; }
    public long EstimatedStorageBytes { get; init; }
}
