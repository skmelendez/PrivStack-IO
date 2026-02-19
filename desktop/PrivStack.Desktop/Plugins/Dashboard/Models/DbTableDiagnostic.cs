namespace PrivStack.Desktop.Plugins.Dashboard.Models;

/// <summary>
/// Per-table diagnostic info from DuckDB internals.
/// </summary>
public sealed record DbTableDiagnostic(
    string TableName,
    long RowCount,
    long EstimatedSizeBytes,
    long ColumnCount)
{
    public string FormattedSize => FormatBytes(EstimatedSizeBytes);
    public string FormattedRowCount => RowCount.ToString("N0");

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
    };
}
