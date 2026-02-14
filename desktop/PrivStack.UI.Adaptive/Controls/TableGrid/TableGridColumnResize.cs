// ============================================================================
// File: TableGridColumnResize.cs
// Description: Handles column resize grip interaction for the TableGrid control.
//              Tracks pointer capture, delta calculation, and width redistribution.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridColumnResize
{
    private bool _isResizing;
    private int _resizeColIndex;
    private Point _resizeStartPoint;
    private double _resizeStartWidth;
    private double _resizeTotalWidth;
    private double[] _resizeOriginalWidths = [];

    private readonly Func<Grid?> _getGrid;
    private readonly Func<int> _getColCount;
    private readonly Action<IReadOnlyList<double>> _onWidthsCommitted;

    public TableGridColumnResize(Func<Grid?> getGrid, Func<int> getColCount,
        Action<IReadOnlyList<double>> onWidthsCommitted)
    {
        _getGrid = getGrid;
        _getColCount = getColCount;
        _onWidthsCommitted = onWidthsCommitted;
    }

    public void OnGripPressed(int colIndex, PointerPressedEventArgs e, Control relativeTo)
    {
        if (!e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed) return;

        _isResizing = true;
        _resizeColIndex = colIndex;
        _resizeStartPoint = e.GetPosition(relativeTo);

        var grid = _getGrid();
        if (grid == null) return;

        var colCount = _getColCount();
        _resizeTotalWidth = 0;
        _resizeOriginalWidths = new double[colCount];

        for (var c = 0; c < colCount; c++)
        {
            var gc = c * 2 + 1;
            if (gc < grid.ColumnDefinitions.Count)
            {
                _resizeOriginalWidths[c] = grid.ColumnDefinitions[gc].ActualWidth;
                _resizeTotalWidth += _resizeOriginalWidths[c];
            }
            else
            {
                _resizeOriginalWidths[c] = 100;
                _resizeTotalWidth += 100;
            }
        }

        _resizeStartWidth = colIndex < colCount ? _resizeOriginalWidths[colIndex] : 100;

        if (e.Source is Control source)
            e.Pointer.Capture(source);
        e.Handled = true;
    }

    public void OnGripMoved(PointerEventArgs e, Control relativeTo)
    {
        if (!_isResizing) return;

        var grid = _getGrid();
        if (grid == null) return;

        var colCount = _getColCount();
        var pos = e.GetPosition(relativeTo);
        var delta = pos.X - _resizeStartPoint.X;
        var gridColIndex = _resizeColIndex * 2 + 1;

        if (gridColIndex < grid.ColumnDefinitions.Count)
        {
            var newWidth = Math.Max(60, _resizeStartWidth + delta);
            grid.ColumnDefinitions[gridColIndex].Width = new GridLength(newWidth, GridUnitType.Pixel);

            // Resize adjacent column to maintain total width
            if (_resizeColIndex < colCount - 1)
            {
                var nextGridCol = (_resizeColIndex + 1) * 2 + 1;
                if (nextGridCol < grid.ColumnDefinitions.Count
                    && _resizeColIndex + 1 < _resizeOriginalWidths.Length)
                {
                    var nextNew = Math.Max(60, _resizeOriginalWidths[_resizeColIndex + 1] - delta);
                    grid.ColumnDefinitions[nextGridCol].Width = new GridLength(nextNew, GridUnitType.Pixel);
                }
            }
        }

        e.Handled = true;
    }

    public void OnGripReleased(PointerReleasedEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        e.Pointer.Capture(null);

        CommitWidths();
        e.Handled = true;
    }

    private void CommitWidths()
    {
        var grid = _getGrid();
        if (grid == null) return;

        var colCount = _getColCount();
        var pixelWidths = new double[colCount];

        for (var c = 0; c < colCount; c++)
        {
            var gc = c * 2 + 1;
            if (gc < grid.ColumnDefinitions.Count)
            {
                var def = grid.ColumnDefinitions[gc];
                pixelWidths[c] = def.Width.IsAbsolute ? def.Width.Value : def.ActualWidth;
            }
            else
            {
                pixelWidths[c] = 100;
            }
        }

        _onWidthsCommitted(pixelWidths);
    }
}
