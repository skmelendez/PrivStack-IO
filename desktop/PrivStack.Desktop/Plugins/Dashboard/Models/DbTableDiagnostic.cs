using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PrivStack.Desktop.Plugins.Dashboard.Models;

/// <summary>
/// Per-table diagnostic info from DuckDB internals.
/// </summary>
public sealed record DbTableDiagnostic(
    string DatabaseLabel,
    string TableName,
    long RowCount,
    long EstimatedSizeBytes,
    long ColumnCount)
{
    public string FormattedSize => FormatBytes(EstimatedSizeBytes);
    public string FormattedRowCount => RowCount == 1 ? "1 row" : $"{RowCount:N0} rows";
    public string DisplayName => $"{DatabaseLabel}.{TableName}";

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
    };
}

/// <summary>
/// Expandable summary for a single DuckDB file with its tables.
/// </summary>
public partial class DbFileDiagnostic : ObservableObject
{
    public string Label { get; }
    public string FileName { get; }
    public long FileSize { get; }
    public long TotalRows { get; }
    public string FormattedFileSize => DbTableDiagnostic.FormatBytes(FileSize);
    public string Summary => $"{Tables.Count} tables · {TotalRows:N0} rows · {FormattedFileSize}";
    public ObservableCollection<DbTableDiagnostic> Tables { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    // Block-level info from pragma_database_size
    public long TotalBlocks { get; set; }
    public long UsedBlocks { get; set; }
    public long FreeBlocks { get; set; }
    public string BlockInfo => TotalBlocks > 0
        ? $"{UsedBlocks:N0} used / {FreeBlocks:N0} free of {TotalBlocks:N0} blocks"
        : "";

    public DbFileDiagnostic(string label, string fileName, long fileSize, long totalRows)
    {
        Label = label;
        FileName = fileName;
        FileSize = fileSize;
        TotalRows = totalRows;
    }
}
