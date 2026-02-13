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
        if (_resizeOriginalWidths.Length != colCount || _resizeTotalWidth <= 0) return;

        var resizedGridCol = _resizeColIndex * 2 + 1;
        if (resizedGridCol >= grid.ColumnDefinitions.Count) return;

        var resizedDef = grid.ColumnDefinitions[resizedGridCol];
        var resizedPixelWidth = resizedDef.Width.IsAbsolute ? resizedDef.Width.Value : _resizeStartWidth;

        var pixelWidths = new double[colCount];
        var otherOriginalTotal = 0.0;
        for (var c = 0; c < colCount; c++)
        {
            if (c != _resizeColIndex)
                otherOriginalTotal += _resizeOriginalWidths[c];
        }

        var remainingWidth = Math.Max(60, _resizeTotalWidth - resizedPixelWidth);

        for (var c = 0; c < colCount; c++)
        {
            if (c == _resizeColIndex)
                pixelWidths[c] = resizedPixelWidth;
            else if (otherOriginalTotal > 0)
                pixelWidths[c] = Math.Max(30, (_resizeOriginalWidths[c] / otherOriginalTotal) * remainingWidth);
            else
                pixelWidths[c] = remainingWidth / Math.Max(1, colCount - 1);
        }

        for (var c = 0; c < colCount; c++)
        {
            var gc = c * 2 + 1;
            if (gc < grid.ColumnDefinitions.Count)
                grid.ColumnDefinitions[gc].Width = new GridLength(pixelWidths[c], GridUnitType.Pixel);
        }

        _onWidthsCommitted(pixelWidths);
    }
}
