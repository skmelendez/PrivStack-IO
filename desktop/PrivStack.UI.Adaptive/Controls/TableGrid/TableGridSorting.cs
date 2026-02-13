// ============================================================================
// File: TableGridSorting.cs
// Description: Sort state machine and SmartComparer for the TableGrid control.
//              Handles header click cycling (None → Asc → Desc → None) and
//              numeric-aware string comparison.
// ============================================================================

using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridSortState
{
    public int ColumnIndex { get; private set; } = -1;
    public TableSortDirection Direction { get; private set; } = TableSortDirection.None;

    public void OnHeaderClick(int colIndex)
    {
        if (ColumnIndex == colIndex)
        {
            Direction = Direction switch
            {
                TableSortDirection.None => TableSortDirection.Ascending,
                TableSortDirection.Ascending => TableSortDirection.Descending,
                TableSortDirection.Descending => TableSortDirection.None,
                _ => TableSortDirection.None
            };
            if (Direction == TableSortDirection.None)
                ColumnIndex = -1;
        }
        else
        {
            ColumnIndex = colIndex;
            Direction = TableSortDirection.Ascending;
        }
    }

    public void Reset()
    {
        ColumnIndex = -1;
        Direction = TableSortDirection.None;
    }

    public string GetSortIndicator(int colIndex)
    {
        if (colIndex != ColumnIndex || Direction == TableSortDirection.None) return "";
        return Direction == TableSortDirection.Ascending ? " \u25b2" : " \u25bc";
    }
}

internal sealed class SmartComparer : IComparer<object>
{
    public static readonly SmartComparer Instance = new();

    public int Compare(object? x, object? y) => (x, y) switch
    {
        (double a, double b) => a.CompareTo(b),
        (DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b),
        (string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
        (double, _) => -1,
        (_, double) => 1,
        (DateTimeOffset, _) => -1,
        (_, DateTimeOffset) => 1,
        _ => string.Compare(x?.ToString() ?? "", y?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
    };
}
