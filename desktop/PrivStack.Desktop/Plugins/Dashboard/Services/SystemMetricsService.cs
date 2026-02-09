using System.Collections.ObjectModel;
using System.Diagnostics;
using PrivStack.Desktop.Plugins.Dashboard.Models;
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
    /// Queries all IDataMetricsProvider plugins and scans managed files directory.
    /// Returns (pluginDataItems, totalDatabaseSize, totalFilesSize, totalStorageSize).
    /// </summary>
    public async Task<DataMetricsResult> GetDataMetricsAsync(IPluginRegistry registry)
    {
        var items = new ObservableCollection<PluginDataInfo>();
        long totalEntityBytes = 0;
        long totalFilesBytes = 0;
        long totalVaultBytes = 0;

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

                    items.Add(new PluginDataInfo
                    {
                        Name = provider.ProviderName,
                        Icon = provider.ProviderIcon,
                        EntityCount = metrics.EntityCount,
                        FormattedSize = SystemMetricsHelper.FormatBytes(metrics.EstimatedSizeBytes),
                        Tables = new ObservableCollection<DataTableInfo>(metrics.Tables),
                    });

                    // Categorize by backing mode (matches Files plugin logic)
                    foreach (var table in metrics.Tables)
                    {
                        switch (table.BackingMode)
                        {
                            case "file":
                                totalFilesBytes += metrics.EstimatedSizeBytes;
                                break;
                            case "blob":
                                totalVaultBytes += metrics.EstimatedSizeBytes;
                                break;
                            default:
                                totalEntityBytes += metrics.EstimatedSizeBytes;
                                break;
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

        // Scan managed files directory
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

        var totalSize = totalEntityBytes + diskFilesSize + totalVaultBytes;

        return new DataMetricsResult
        {
            PluginDataItems = items,
            TotalDatabaseBytes = totalEntityBytes,
            TotalFilesBytes = diskFilesSize,
            TotalVaultBytes = totalVaultBytes,
            TotalStorageBytes = totalSize,
        };
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
}
