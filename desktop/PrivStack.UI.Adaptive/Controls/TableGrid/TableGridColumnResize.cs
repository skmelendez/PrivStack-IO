// ============================================================================
// File: TableGridColumnResize.cs
// Description: Handles column resize grip interaction for the TableGrid control.
//              Tracks pointer capture, delta calculation, and width update.
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

        var gridColIndex = colIndex * 2 + 1;
        _resizeStartWidth = gridColIndex < grid.ColumnDefinitions.Count
            ? grid.ColumnDefinitions[gridColIndex].ActualWidth
            : 100;

        if (e.Source is Control source)
            e.Pointer.Capture(source);
        e.Handled = true;
    }

    public void OnGripMoved(PointerEventArgs e, Control relativeTo)
    {
        if (!_isResizing) return;

        var grid = _getGrid();
        if (grid == null) return;

        var pos = e.GetPosition(relativeTo);
        var delta = pos.X - _resizeStartPoint.X;
        var gridColIndex = _resizeColIndex * 2 + 1;

        if (gridColIndex < grid.ColumnDefinitions.Count)
        {
            var newWidth = Math.Max(60, _resizeStartWidth + delta);
            grid.ColumnDefinitions[gridColIndex].Width = new GridLength(newWidth, GridUnitType.Pixel);
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
