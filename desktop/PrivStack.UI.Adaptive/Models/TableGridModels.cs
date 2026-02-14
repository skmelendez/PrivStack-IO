// ============================================================================
// File: TableGridModels.cs
// Description: Data models for the TableGrid control. Defines column definitions,
//              row data, grid data, and paging info as immutable records.
// ============================================================================

namespace PrivStack.UI.Adaptive.Models;

public enum TableColumnAlignment { Left, Center, Right }
public enum TableSortDirection { None, Ascending, Descending }
public enum TableScrollMode { Paginated, InfiniteScroll }

public sealed record TableColumnDefinition
{
    public required string Name { get; init; }
    public TableColumnAlignment Alignment { get; init; } = TableColumnAlignment.Left;
    public double? PixelWidth { get; init; }
    public bool IsVisible { get; init; } = true;
}

public sealed record TableGridRow
{
    public required string Id { get; init; }
    public bool IsHeader { get; init; }
    public required IReadOnlyList<string> Cells { get; init; }
}

public sealed record TableGridData
{
    public required IReadOnlyList<TableColumnDefinition> Columns { get; init; }
    public required IReadOnlyList<TableGridRow> HeaderRows { get; init; }
    public required IReadOnlyList<TableGridRow> DataRows { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsStriped { get; init; }
    public string? ColorTheme { get; init; }
    public int FrozenColumnCount { get; init; }
    public int FrozenRowCount { get; init; }
}

public sealed record TablePagingInfo
{
    public int CurrentPage { get; init; }
    public int TotalPages { get; init; }
    public int TotalFilteredRows { get; init; }
    public int? TotalSourceRows { get; init; }
    public int PageSize { get; init; }
}
