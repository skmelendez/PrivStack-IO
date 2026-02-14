// ============================================================================
// File: TableGridCellNavigation.cs
// Description: Manages Tab/Arrow keyboard navigation between editable cells
//              in the TableGrid. Tracks a lookup dictionary of (row, col) → TextBox
//              and raises BoundaryEscapeRequested when navigation hits the edge.
// ============================================================================

using Avalonia.Controls;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridCellNavigation
{
    private readonly Dictionary<(int row, int col), TextBox> _cells = new();

    public int ColumnCount { get; set; }

    public event Action<int>? BoundaryEscapeRequested;

    public void Register(int row, int col, TextBox textBox) =>
        _cells[(row, col)] = textBox;

    public void Clear()
    {
        _cells.Clear();
        ColumnCount = 0;
    }

    public void NavigateToCell(int row, int col)
    {
        if (_cells.TryGetValue((row, col), out var textBox))
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    public void NavigateToNextCell(int row, int col)
    {
        col++;
        if (col >= ColumnCount)
        {
            col = 0;
            row++;
        }

        if (_cells.ContainsKey((row, col)))
            NavigateToCell(row, col);
    }

    public void NavigateToPreviousCell(int row, int col)
    {
        col--;
        if (col < 0)
        {
            col = ColumnCount - 1;
            row--;
        }

        if (_cells.ContainsKey((row, col)))
            NavigateToCell(row, col);
    }

    public void HandleArrowUp(int row, int col)
    {
        if (_cells.ContainsKey((row - 1, col)))
            NavigateToCell(row - 1, col);
        else
            BoundaryEscapeRequested?.Invoke(-1);
    }

    public void HandleArrowDown(int row, int col)
    {
        if (_cells.ContainsKey((row + 1, col)))
            NavigateToCell(row + 1, col);
        else
            BoundaryEscapeRequested?.Invoke(1);
    }

    public void FocusEdge(int direction)
    {
        if (_cells.Count == 0)
        {
            BoundaryEscapeRequested?.Invoke(direction);
            return;
        }

        if (direction > 0)
        {
            var minRow = _cells.Keys.Min(k => k.row);
            var minCol = _cells.Keys.Where(k => k.row == minRow).Min(k => k.col);
            NavigateToCell(minRow, minCol);
        }
        else
        {
            var maxRow = _cells.Keys.Max(k => k.row);
            var maxCol = _cells.Keys.Where(k => k.row == maxRow).Max(k => k.col);
            NavigateToCell(maxRow, maxCol);
        }
    }
}
