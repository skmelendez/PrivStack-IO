using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.Plugins.Dashboard.Models;

/// <summary>
/// Holds binary size info for a single installed plugin.
/// </summary>
public partial class PluginSizeInfo : ObservableObject
{
    [ObservableProperty]
    private string _pluginId = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _icon = "Package";

    [ObservableProperty]
    private long _sizeBytes;

    public string FormattedSize => SystemMetricsHelper.FormatBytes(SizeBytes);
}

/// <summary>
/// Holds data storage metrics for a single plugin (entity counts, sizes, table breakdown).
/// </summary>
public partial class PluginDataInfo : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private int _entityCount;

    [ObservableProperty]
    private string _formattedSize = "0 B";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private ObservableCollection<DataTableInfo> _tables = [];

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add($"{EntityCount:N0} {(EntityCount == 1 ? "entity" : "entities")}");
            if (!string.IsNullOrEmpty(FormattedSize) && FormattedSize != "0 B")
                parts.Add(FormattedSize);
            if (Tables.Count > 1)
                parts.Add($"{Tables.Count} tables");
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// Shared byte formatting helper.
/// </summary>
public static class SystemMetricsHelper
{
    public static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F1} GB",
            >= MB => $"{bytes / (double)MB:F1} MB",
            >= KB => $"{bytes / (double)KB:F1} KB",
            _ => $"{bytes} B"
        };
    }
}
