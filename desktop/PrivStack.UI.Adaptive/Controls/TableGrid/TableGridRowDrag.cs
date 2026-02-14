// ============================================================================
// File: TableGridRowDrag.cs
// Description: Row drag-and-drop reorder for the TableGrid. Adds drag handle
//              column (col 0), shows drop indicator, and fires reorder callback.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridRowDrag
{
    private bool _isDragging;
    private int _dragRowIndex = -1;
    private Point _dragStartY;
    private Border? _dropIndicator;

    private readonly Func<Grid> _getGrid;
    private readonly Func<int> _getDataRowCount;
    private readonly Func<int> _getDataRowStartGridRow;
    private readonly Func<ITableGridDataSource?> _getSource;
    private readonly Action _onReorder;
    private readonly Func<Control> _getThemeSource;

    public TableGridRowDrag(
        Func<Grid> getGrid,
        Func<int> getDataRowCount,
        Func<int> getDataRowStartGridRow,
        Func<ITableGridDataSource?> getSource,
        Action onReorder,
        Func<Control> getThemeSource)
    {
        _getGrid = getGrid;
        _getDataRowCount = getDataRowCount;
        _getDataRowStartGridRow = getDataRowStartGridRow;
        _getSource = getSource;
        _onReorder = onReorder;
        _getThemeSource = getThemeSource;
    }

    public Border CreateDragHandle(int rowIndex)
    {
        var themeSource = _getThemeSource();
        var handle = new Border
        {
            Width = 20,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = rowIndex,
            Child = new TextBlock
            {
                Text = "\u2630",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TableGridCellFactory.GetBrush(themeSource, "ThemeTextMutedBrush")
            }
        };

        handle.PointerPressed += OnDragHandlePressed;
        handle.PointerMoved += OnDragHandleMoved;
        handle.PointerReleased += OnDragHandleReleased;

        return handle;
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle || handle.Tag is not int rowIndex) return;
        var grid = _getGrid();
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        _dragRowIndex = rowIndex;
        _dragStartY = e.GetPosition(grid);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var grid = _getGrid();
        var pos = e.GetPosition(grid);
        var dropIndex = GetDropRowIndex(pos.Y, grid);
        ShowDropIndicator(dropIndex, grid);
        e.Handled = true;
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;

        var grid = _getGrid();
        var pos = e.GetPosition(grid);
        var dropIndex = GetDropRowIndex(pos.Y, grid);

        _isDragging = false;
        e.Pointer.Capture(null);
        RemoveDropIndicator(grid);

        if (dropIndex >= 0 && dropIndex != _dragRowIndex && dropIndex != _dragRowIndex + 1)
        {
            var source = _getSource();
            if (source != null)
            {
                var toIndex = dropIndex > _dragRowIndex ? dropIndex - 1 : dropIndex;
                _ = source.OnRowReorderedAsync(_dragRowIndex, toIndex);
                _onReorder();
            }
        }

        _dragRowIndex = -1;
        e.Handled = true;
    }

    private int GetDropRowIndex(double y, Grid grid)
    {
        var rowCount = _getDataRowCount();
        var dataRowStart = _getDataRowStartGridRow();

        // Accumulate Y offset up to the first data row
        double cumulativeHeight = 0;
        for (var r = 0; r < dataRowStart && r < grid.RowDefinitions.Count; r++)
            cumulativeHeight += grid.RowDefinitions[r].ActualHeight;

        for (var r = 0; r < rowCount; r++)
        {
            var gridRowIdx = dataRowStart + r;
            var rowDef = gridRowIdx < grid.RowDefinitions.Count
                ? grid.RowDefinitions[gridRowIdx]
                : null;
            var rowHeight = rowDef?.ActualHeight ?? 40;

            if (y < cumulativeHeight + rowHeight / 2)
                return r;

            cumulativeHeight += rowHeight;
        }

        return rowCount;
    }

    private void ShowDropIndicator(int dropIndex, Grid grid)
    {
        RemoveDropIndicator(grid);

        var dataRowStart = _getDataRowStartGridRow();
        var themeSource = _getThemeSource();
        _dropIndicator = new Border
        {
            Height = 2,
            Background = TableGridCellFactory.GetBrush(themeSource, "ThemePrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };

        var gridRow = dataRowStart + dropIndex;
        var row = Math.Max(0, Math.Min(gridRow, grid.RowDefinitions.Count - 1));
        Grid.SetRow(_dropIndicator, row);
        Grid.SetColumn(_dropIndicator, 0);
        Grid.SetColumnSpan(_dropIndicator, grid.ColumnDefinitions.Count);
        _dropIndicator.VerticalAlignment = gridRow >= grid.RowDefinitions.Count
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;

        grid.Children.Add(_dropIndicator);
    }

    private void RemoveDropIndicator(Grid grid)
    {
        if (_dropIndicator != null)
        {
            grid.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }
    }
}
