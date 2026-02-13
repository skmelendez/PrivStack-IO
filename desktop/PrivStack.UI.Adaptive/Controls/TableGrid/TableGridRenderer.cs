// ============================================================================
// File: TableGridRenderer.cs
// Description: Grid layout engine for the TableGrid control. Builds column/row
//              definitions, places cells into the Avalonia Grid, and renders
//              title/description rows.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal static class TableGridRenderer
{
    public static void RenderGrid(
        Grid grid, TableGridData data, TablePagingInfo paging,
        TableGridSortState sortState, bool supportsSorting,
        Action<int> onHeaderClick,
        Action<int, Avalonia.Input.PointerPressedEventArgs> onResizePressed,
        Action<Avalonia.Input.PointerEventArgs> onResizeMoved,
        Action<Avalonia.Input.PointerReleasedEventArgs> onResizeReleased,
        Control themeSource)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.Children.Clear();

        var colCount = data.Columns.Count;
        if (colCount == 0) return;

        // Column 0: hidden 0-width (no drag handle for read-only)
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0, GridUnitType.Pixel)));

        // Build header and data cell lists for auto-fit
        var headerCells = new List<string>(colCount);
        foreach (var col in data.Columns)
            headerCells.Add(col.Name);

        var dataRowCells = new List<IReadOnlyList<string>>(data.DataRows.Count);
        foreach (var row in data.DataRows)
            dataRowCells.Add(row.Cells);

        // Compute column widths
        var widths = ComputeWidths(data.Columns, headerCells, dataRowCells, colCount, themeSource);

        for (var c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                new GridLength(widths[c], GridUnitType.Pixel)));
            if (c < colCount - 1)
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)));
        }

        // Count display rows: title + header rows + data rows + description
        var hasTitle = !string.IsNullOrEmpty(data.Title);
        var hasDescription = !string.IsNullOrEmpty(data.Description);
        var totalDisplayRows = (hasTitle ? 1 : 0) + data.HeaderRows.Count + data.DataRows.Count
                               + (hasDescription ? 1 : 0);
        for (var r = 0; r < totalDisplayRows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var gridRow = 0;
        var contentSpan = Math.Max(1, grid.ColumnDefinitions.Count - 1);

        // Build alignment list from column definitions
        var alignments = new List<TableColumnAlignment>(colCount);
        foreach (var col in data.Columns)
            alignments.Add(col.Alignment);

        // Title row
        if (hasTitle)
        {
            var titleBlock = new TextBlock
            {
                Text = data.Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 14,
                Margin = new Thickness(4, 4, 4, 8),
            };
            Grid.SetRow(titleBlock, gridRow);
            Grid.SetColumn(titleBlock, 1);
            Grid.SetColumnSpan(titleBlock, contentSpan);
            grid.Children.Add(titleBlock);
            gridRow++;
        }

        // Header rows
        foreach (var headerRow in data.HeaderRows)
        {
            for (var c = 0; c < colCount; c++)
            {
                var text = c < headerRow.Cells.Count ? headerRow.Cells[c] : "";
                if (supportsSorting)
                    text += sortState.GetSortIndicator(c);

                var cell = TableGridCellFactory.CreateReadOnlyCell(text, true, c, alignments, themeSource);

                if (supportsSorting)
                {
                    var colIdx = c;
                    cell.PointerPressed += (_, _) => onHeaderClick(colIdx);
                    cell.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                }

                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, c * 2 + 1);
                grid.Children.Add(cell);

                AddResizeGrip(grid, c, colCount, gridRow,
                    onResizePressed, onResizeMoved, onResizeReleased);
            }
            gridRow++;
        }

        // Data rows
        foreach (var dataRow in data.DataRows)
        {
            for (var c = 0; c < colCount; c++)
            {
                var text = c < dataRow.Cells.Count ? dataRow.Cells[c] : "";
                var cell = TableGridCellFactory.CreateReadOnlyCell(text, false, c, alignments, themeSource);
                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, c * 2 + 1);
                grid.Children.Add(cell);

                AddResizeGrip(grid, c, colCount, gridRow,
                    onResizePressed, onResizeMoved, onResizeReleased);
            }
            gridRow++;
        }

        // Description row
        if (hasDescription)
        {
            var descBlock = new TextBlock
            {
                Text = data.Description,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(4, 8, 4, 4),
            };
            Grid.SetRow(descBlock, gridRow);
            Grid.SetColumn(descBlock, 1);
            Grid.SetColumnSpan(descBlock, contentSpan);
            grid.Children.Add(descBlock);
        }
    }

    private static double[] ComputeWidths(
        IReadOnlyList<TableColumnDefinition> columns,
        List<string> headerCells, List<IReadOnlyList<string>> dataRows,
        int colCount, Control themeSource)
    {
        // Use explicit pixel widths if all columns have them
        var hasExplicitWidths = columns.All(c => c.PixelWidth is >= 10);
        if (hasExplicitWidths)
        {
            var explicit_ = new double[colCount];
            for (var c = 0; c < colCount; c++)
                explicit_[c] = Math.Max(60, columns[c].PixelWidth!.Value);
            return explicit_;
        }

        return TableGridCellFactory.ComputeAutoFitWidths(headerCells, dataRows, colCount, themeSource);
    }

    private static void AddResizeGrip(
        Grid grid, int c, int colCount, int gridRow,
        Action<int, Avalonia.Input.PointerPressedEventArgs> onPressed,
        Action<Avalonia.Input.PointerEventArgs> onMoved,
        Action<Avalonia.Input.PointerReleasedEventArgs> onReleased)
    {
        if (c >= colCount - 1) return;

        var grip = TableGridCellFactory.CreateResizeGrip(c, onPressed, onMoved, onReleased);
        Grid.SetRow(grip, gridRow);
        Grid.SetColumn(grip, c * 2 + 2);
        grid.Children.Add(grip);
    }
}
