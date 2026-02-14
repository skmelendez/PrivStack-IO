// ============================================================================
// File: TableGridRenderer.cs
// Description: Grid layout engine for the TableGrid control. Builds column/row
//              definitions, places cells into the Avalonia Grid, and renders
//              title/description rows. Supports read-only/editable modes,
//              drag handles, context menus, striping, color themes, and insert
//              indicators for hover-to-insert UX.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed record TableGridRenderResult
{
    public int DataRowCount { get; init; }
    public int TotalDisplayRows { get; init; }
}

internal static class TableGridRenderer
{
    public static TableGridRenderResult RenderGrid(
        Grid grid, TableGridData data, TablePagingInfo paging,
        TableGridSortState sortState, bool supportsSorting,
        bool isEditable, bool isReadOnly,
        bool supportsRowReorder, bool supportsColumnReorder,
        bool supportsStructureEditing,
        ITableGridDataSource? source,
        Action<int> onHeaderClick,
        Action<int, PointerPressedEventArgs> onResizePressed,
        Action<PointerEventArgs> onResizeMoved,
        Action<PointerReleasedEventArgs> onResizeReleased,
        TableGridCellNavigation? cellNavigation,
        Action<string, int, string>? onCellEdited,
        TableGridRowDrag? rowDrag,
        TableGridColumnDrag? columnDrag,
        Action? rebuild,
        Control themeSource,
        bool isStriped = false,
        string? colorTheme = null)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.Children.Clear();
        cellNavigation?.Clear();

        var colCount = data.Columns.Count;
        if (colCount == 0) return new TableGridRenderResult();

        var showDragHandles = supportsRowReorder && !isReadOnly;

        // Column 0: drag handle column (20px when active, 0px when hidden)
        grid.ColumnDefinitions.Add(new ColumnDefinition(
            new Avalonia.Controls.GridLength(showDragHandles ? 20 : 0, GridUnitType.Pixel)));

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
                new Avalonia.Controls.GridLength(widths[c], GridUnitType.Pixel)));
            // Grip column after every data column (including last) for resize
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                new Avalonia.Controls.GridLength(4, GridUnitType.Pixel)));
        }

        // Count display rows
        var hasTitle = !string.IsNullOrEmpty(data.Title);
        var hasDescription = !string.IsNullOrEmpty(data.Description);
        var totalDisplayRows = (hasTitle ? 1 : 0) + data.HeaderRows.Count + data.DataRows.Count
                               + (hasDescription ? 1 : 0);
        for (var r = 0; r < totalDisplayRows; r++)
            grid.RowDefinitions.Add(new RowDefinition(Avalonia.Controls.GridLength.Auto));

        var gridRow = 0;
        var contentSpan = Math.Max(1, grid.ColumnDefinitions.Count - 1);

        // Build alignment list
        var alignments = new List<TableColumnAlignment>(colCount);
        foreach (var col in data.Columns)
            alignments.Add(col.Alignment);

        if (cellNavigation != null)
            cellNavigation.ColumnCount = colCount;

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

        var headerGridRow = gridRow;

        // Header rows
        foreach (var headerRow in data.HeaderRows)
        {
            if (showDragHandles && rowDrag != null)
            {
                var placeholder = new Border { Width = 20 };
                Grid.SetRow(placeholder, gridRow);
                Grid.SetColumn(placeholder, 0);
                grid.Children.Add(placeholder);
            }

            for (var c = 0; c < colCount; c++)
            {
                var text = c < headerRow.Cells.Count ? headerRow.Cells[c] : "";

                Control cell;

                // Editable headers for inline tables — click text to edit
                if (isEditable && !isReadOnly && onCellEdited != null && cellNavigation != null)
                {
                    var (editCell, textBox) = TableGridCellFactory.CreateEditableCell(
                        text, true, headerRow.Id, c, alignments,
                        onCellEdited, cellNavigation, -1,
                        themeSource, isStriped, 0, colorTheme);
                    cell = editCell;
                }
                else
                {
                    cell = TableGridCellFactory.CreateReadOnlyCell(
                        text, true, c, alignments, themeSource,
                        isStriped, 0, colorTheme);
                }

                // Sort arrow — separate clickable indicator in the header cell
                if (supportsSorting && cell is Border sortBorderCell)
                    AddSortArrowToHeaderCell(sortBorderCell, c, sortState, onHeaderClick, themeSource);

                // Column drag reorder
                if (supportsColumnReorder && !isReadOnly && columnDrag != null)
                {
                    if (cell is Border borderCell)
                    {
                        columnDrag.AttachToHeader(borderCell, c, themeSource);
                        cell.Cursor = new Cursor(StandardCursorType.Hand);
                    }
                }

                // Context menu on header for structure editing
                if (supportsStructureEditing && !isReadOnly && source != null && rebuild != null)
                {
                    var colIdx = c;
                    var capturedCell = cell;
                    cell.PointerPressed += (s, e) =>
                    {
                        if (e.GetCurrentPoint(capturedCell).Properties.IsRightButtonPressed)
                        {
                            var menu = TableGridContextMenu.BuildColumnContextMenu(colIdx, source, rebuild);
                            menu.Open(capturedCell);
                            e.Handled = true;
                        }
                    };
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
        var dataRowStartGridRow = gridRow;
        for (var dataIdx = 0; dataIdx < data.DataRows.Count; dataIdx++)
        {
            var dataRow = data.DataRows[dataIdx];

            // Drag handle
            if (showDragHandles && rowDrag != null)
            {
                var handle = rowDrag.CreateDragHandle(dataIdx);
                Grid.SetRow(handle, gridRow);
                Grid.SetColumn(handle, 0);
                grid.Children.Add(handle);
            }

            for (var c = 0; c < colCount; c++)
            {
                var text = c < dataRow.Cells.Count ? dataRow.Cells[c] : "";

                Control cell;
                if (isEditable && !isReadOnly && onCellEdited != null && cellNavigation != null)
                {
                    var (editCell, textBox) = TableGridCellFactory.CreateEditableCell(
                        text, false, dataRow.Id, c, alignments,
                        onCellEdited, cellNavigation, gridRow - dataRowStartGridRow,
                        themeSource, isStriped, dataIdx, colorTheme);
                    cell = editCell;
                    cellNavigation.Register(gridRow - dataRowStartGridRow, c, textBox);

                    // Context menu on editable data cells
                    if (supportsStructureEditing && source != null && rebuild != null)
                    {
                        var capturedIdx = dataIdx;
                        editCell.PointerPressed += (s, e) =>
                        {
                            if (e.GetCurrentPoint(editCell).Properties.IsRightButtonPressed)
                            {
                                var menu = TableGridContextMenu.BuildRowContextMenu(capturedIdx, source, rebuild);
                                menu.Open(editCell);
                                e.Handled = true;
                            }
                        };
                    }
                }
                else
                {
                    cell = TableGridCellFactory.CreateReadOnlyCell(
                        text, false, c, alignments, themeSource,
                        isStriped, dataIdx, colorTheme);
                }

                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, c * 2 + 1);
                grid.Children.Add(cell);

                AddResizeGrip(grid, c, colCount, gridRow,
                    onResizePressed, onResizeMoved, onResizeReleased);
            }
            gridRow++;
        }

        // Insert indicators for hover-to-insert UX (floating "+" circles on cell borders)
        if (supportsStructureEditing && !isReadOnly && source != null && rebuild != null)
        {
            TableGridInsertIndicators.AttachCellIndicators(
                grid, headerGridRow, dataRowStartGridRow, data.DataRows.Count,
                colCount, source, rebuild, themeSource);
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

        return new TableGridRenderResult
        {
            DataRowCount = data.DataRows.Count,
            TotalDisplayRows = totalDisplayRows
        };
    }

    private static double[] ComputeWidths(
        IReadOnlyList<TableColumnDefinition> columns,
        List<string> headerCells, List<IReadOnlyList<string>> dataRows,
        int colCount, Control themeSource)
    {
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

    private static void AddSortArrowToHeaderCell(
        Border cell, int colIndex, TableGridSortState sortState,
        Action<int> onHeaderClick, Control themeSource)
    {
        var existingChild = cell.Child;
        cell.Child = null;

        var wrapperGrid = new Grid();
        wrapperGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        wrapperGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        if (existingChild != null)
        {
            Grid.SetColumn(existingChild, 0);
            wrapperGrid.Children.Add(existingChild);
        }

        var isActive = sortState.ColumnIndex == colIndex
                       && sortState.Direction != TableSortDirection.None;
        var isAsc = isActive && sortState.Direction == TableSortDirection.Ascending;

        var pathData = isAsc ? "M7 14l5-5 5 5" : "M7 10l5 5 5-5";
        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(pathData),
            Width = 10,
            Height = 10,
        };

        var fg = isActive
            ? TableGridCellFactory.GetBrush(themeSource, "ThemePrimaryBrush")
            : TableGridCellFactory.GetBrush(themeSource, "ThemeTextMutedBrush");
        if (fg != null) icon.Foreground = fg;

        var arrowBorder = new Border
        {
            Child = icon,
            Padding = new Thickness(4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Opacity = isActive ? 1.0 : 0.4,
        };

        var idx = colIndex;
        arrowBorder.PointerPressed += (_, e) =>
        {
            onHeaderClick(idx);
            e.Handled = true;
        };

        Grid.SetColumn(arrowBorder, 1);
        wrapperGrid.Children.Add(arrowBorder);

        cell.Child = wrapperGrid;
    }

    private static void AddResizeGrip(
        Grid grid, int c, int colCount, int gridRow,
        Action<int, PointerPressedEventArgs> onPressed,
        Action<PointerEventArgs> onMoved,
        Action<PointerReleasedEventArgs> onReleased)
    {
        var grip = TableGridCellFactory.CreateResizeGrip(c, onPressed, onMoved, onReleased);
        Grid.SetRow(grip, gridRow);
        Grid.SetColumn(grip, c * 2 + 2);
        grid.Children.Add(grip);
    }
}
