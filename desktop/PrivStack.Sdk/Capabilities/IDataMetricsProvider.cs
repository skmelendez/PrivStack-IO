namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Capability for plugins to report storage and data metrics
/// for the Files plugin's Data dashboard.
/// </summary>
public interface IDataMetricsProvider
{
    /// <summary>Display name for this provider (e.g., "Notes", "Passwords").</summary>
    string ProviderName { get; }

    /// <summary>Icon identifier for the provider.</summary>
    string ProviderIcon { get; }

    /// <summary>Gathers data metrics for this plugin.</summary>
    Task<PluginDataMetrics> GetMetricsAsync(CancellationToken ct = default);
}

/// <summary>
/// Aggregated metrics for a single plugin's data.
/// </summary>
public record PluginDataMetrics
{
    /// <summary>Total entity count across all tables.</summary>
    public int EntityCount { get; init; }

    /// <summary>Estimated total size in bytes.</summary>
    public long EstimatedSizeBytes { get; init; }

    /// <summary>Per-table breakdown.</summary>
    public IReadOnlyList<DataTableInfo> Tables { get; init; } = [];
}

/// <summary>
/// Describes a single data table / entity type within a plugin.
/// </summary>
public record DataTableInfo
{
    /// <summary>Human-friendly table name (e.g., "Pages", "Table Rows").</summary>
    public required string Name { get; init; }

    /// <summary>Entity type key used in the SDK (e.g., "page", "table_row").</summary>
    public required string EntityType { get; init; }

    /// <summary>Number of rows / entities.</summary>
    public int RowCount { get; init; }

    /// <summary>Backing mode: "entity", "file", "blob", etc.</summary>
    public string BackingMode { get; init; } = "entity";

    /// <summary>Estimated size in bytes for this table (0 if unknown).</summary>
    public long EstimatedSizeBytes { get; init; }

    /// <summary>Actual on-disk size in bytes, proportionally allocated from DuckDB file (0 if unknown).</summary>
    public long ActualSizeBytes { get; init; }

    /// <summary>Parent item name (e.g., the note title this table belongs to).</summary>
    public string? ParentName { get; init; }

    /// <summary>Parent item ID for navigation (e.g., page ID).</summary>
    public string? ParentId { get; init; }

    /// <summary>Block ID within the parent (for deep-linking to a specific block).</summary>
    public string? BlockId { get; init; }

    /// <summary>Plugin ID that owns this data (for cross-plugin navigation).</summary>
    public string? PluginId { get; init; }

    /// <summary>Whether this table has a parent item for navigation.</summary>
    public bool HasParent => !string.IsNullOrEmpty(ParentId);

    /// <summary>Human-readable size with dual display when both actual and estimated are available.</summary>
    public string FormattedSize
    {
        get
        {
            if (ActualSizeBytes > 0 && EstimatedSizeBytes > 0)
                return $"{FormatSize(ActualSizeBytes)} | ~{FormatSize(EstimatedSizeBytes)} est.";
            if (ActualSizeBytes > 0)
                return FormatSize(ActualSizeBytes);
            if (EstimatedSizeBytes > 0)
                return $"~{FormatSize(EstimatedSizeBytes)}";
            return "";
        }
    }

    /// <summary>Formats a byte count into a human-readable string (e.g., "34.2 MB").</summary>
    public static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        int idx = 0;
        double size = bytes;
        while (size >= 1024 && idx < suffixes.Length - 1) { size /= 1024; idx++; }
        return $"{size:0.#} {suffixes[idx]}";
    }

    /// <summary>Formatted row count with thousands separator.</summary>
    public string FormattedRowCount => RowCount.ToString("N0");

    /// <summary>SVG path icon for the backing mode.</summary>
    public string BackingModeIcon => BackingMode switch
    {
        "file" => "M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm-1 7V3.5L18.5 9H13z",
        "blob" => "M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z",
        _ => "M2 20h20v-4H2v4zm2-3h2v2H4v-2zM2 4v4h20V4H2zm4 3H4V5h2v2zm-4 7h20v-4H2v4zm2-3h2v2H4v-2z",
    };
}
