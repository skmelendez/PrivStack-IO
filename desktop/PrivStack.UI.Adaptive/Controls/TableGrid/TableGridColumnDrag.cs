// ============================================================================
// File: TableGridColumnDrag.cs
// Description: Column drag reorder for the TableGrid. Attaches drag behavior
//              to header cells and fires reorder callback on drop.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridColumnDrag
{
    private bool _isDragging;
    private int _dragColIndex = -1;
    private Point _dragStartPoint;
    private const double DragThreshold = 10;

    private readonly Func<Grid> _getGrid;
    private readonly Func<int> _getColumnCount;
    private readonly Func<ITableGridDataSource?> _getSource;
    private readonly Action _onReorder;

    public TableGridColumnDrag(
        Func<Grid> getGrid,
        Func<int> getColumnCount,
        Func<ITableGridDataSource?> getSource,
        Action onReorder)
    {
        _getGrid = getGrid;
        _getColumnCount = getColumnCount;
        _getSource = getSource;
        _onReorder = onReorder;
    }

    /// <summary>
    /// Attaches drag behavior to a header cell border. Sorting is handled
    /// separately via sort arrow indicators in the header cells.
    /// </summary>
    public void AttachToHeader(Border headerBorder, int colIndex, Control relativeTo)
    {
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
            {
                _isDragging = false;
                _dragColIndex = colIndex;
                _dragStartPoint = e.GetPosition(relativeTo);
            }
        };

        headerBorder.PointerMoved += (_, e) =>
        {
            if (_dragColIndex >= 0 && !_isDragging)
            {
                var pos = e.GetPosition(relativeTo);
                if (Math.Abs(pos.X - _dragStartPoint.X) > DragThreshold)
                    _isDragging = true;
            }
        };

        headerBorder.PointerReleased += (_, e) =>
        {
            if (_isDragging && _dragColIndex >= 0)
            {
                var pos = e.GetPosition(relativeTo);
                var dropCol = GetDropColumnIndex(pos.X, relativeTo);
                _isDragging = false;

                if (dropCol >= 0 && dropCol != _dragColIndex)
                {
                    var source = _getSource();
                    if (source != null)
                    {
                        _ = source.OnColumnReorderedAsync(_dragColIndex, dropCol);
                        _onReorder();
                    }
                    _dragColIndex = -1;
                    e.Handled = true;
                    return;
                }
            }
            _dragColIndex = -1;
        };
    }

    private int GetDropColumnIndex(double x, Control relativeTo)
    {
        var grid = _getGrid();
        var colCount = _getColumnCount();
        var cumulativeWidth = 0.0;

        // Skip drag handle column (column 0)
        if (grid.ColumnDefinitions.Count > 0)
            cumulativeWidth = grid.ColumnDefinitions[0].ActualWidth;

        for (var c = 0; c < colCount; c++)
        {
            var gridCol = c * 2 + 1;
            var colWidth = gridCol < grid.ColumnDefinitions.Count
                ? grid.ColumnDefinitions[gridCol].ActualWidth
                : 100;

            var gripWidth = 0.0;
            if (c > 0)
            {
                var gripCol = (c - 1) * 2 + 2;
                if (gripCol < grid.ColumnDefinitions.Count)
                    gripWidth = grid.ColumnDefinitions[gripCol].ActualWidth;
            }

            cumulativeWidth += gripWidth;

            if (x < cumulativeWidth + colWidth / 2)
                return c;

            cumulativeWidth += colWidth;
        }

        return colCount - 1;
    }
}
