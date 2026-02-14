// ============================================================================
// File: TableGridRenderer.cs
// Description: Grid layout engine for the TableGrid control. Builds column/row
//              definitions, places cells into the Avalonia Grid, and renders
//              title/description rows. Supports read-only/editable modes,
//              drag handles, context menus, striping, color themes, insert
//              indicators, and dual-grid freeze-pane layout.
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
    public int DataRowStartGridRow { get; init; }
    public double[]? ComputedWidths { get; init; }
    public bool WasAutoFit { get; init; }
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
        string? colorTheme = null,
        int frozenColumnCount = 0,
        int frozenRowCount = 0,
        Action<int>? onFreezeColumns = null,
        Action<int>? onFreezeRows = null,
        Grid? frozenGrid = null)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.Children.Clear();
        cellNavigation?.Clear();

        var isDualGrid = frozenGrid != null && frozenColumnCount > 0;
        if (isDualGrid)
        {
            frozenGrid!.ColumnDefinitions.Clear();
            frozenGrid.RowDefinitions.Clear();
            frozenGrid.Children.Clear();
        }

        var colCount = data.Columns.Count;
        if (colCount == 0) return new TableGridRenderResult();

        var effectiveFrozenCols = isDualGrid
            ? Math.Min(frozenColumnCount, colCount) : 0;
        var showDragHandles = supportsRowReorder && !isReadOnly;

        // Dual-grid targeting: resolves which grid and column index for a cell
        Grid CellGrid(int c) => isDualGrid && c < effectiveFrozenCols
            ? frozenGrid! : grid;
        int CellCol(int c) => isDualGrid && c >= effectiveFrozenCols
            ? (c - effectiveFrozenCols) * 2 + 1 : c * 2 + 1;
        int GripCol(int c) => isDualGrid && c >= effectiveFrozenCols
            ? (c - effectiveFrozenCols) * 2 + 2 : c * 2 + 2;

        // Build cell text lists for auto-fit width computation
        var headerCells = new List<string>(colCount);
        foreach (var col in data.Columns)
            headerCells.Add(col.Name);

        var dataRowCells = new List<IReadOnlyList<string>>(data.DataRows.Count);
        foreach (var row in data.DataRows)
            dataRowCells.Add(row.Cells);

        var hasExplicitWidths = data.Columns.All(c => c.PixelWidth is >= 10);
        var widths = ComputeWidths(data.Columns, headerCells, dataRowCells,
            colCount, themeSource);

        SetupColumnDefinitions(grid, frozenGrid, isDualGrid, effectiveFrozenCols,
            colCount, widths, showDragHandles);

        // Row definitions — both grids share the same count
        var hasDescription = !string.IsNullOrEmpty(data.Description);
        var totalDisplayRows = data.HeaderRows.Count + data.DataRows.Count
                               + (hasDescription ? 1 : 0);
        for (var r = 0; r < totalDisplayRows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (isDualGrid)
                frozenGrid!.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        var gridRow = 0;

        // Build alignment list
        var alignments = new List<TableColumnAlignment>(colCount);
        foreach (var col in data.Columns)
            alignments.Add(col.Alignment);

        if (cellNavigation != null)
            cellNavigation.ColumnCount = colCount;

        // Header rows
        gridRow = RenderHeaderRows(grid, frozenGrid, isDualGrid, effectiveFrozenCols,
            data, gridRow, colCount, showDragHandles,
            isEditable, isReadOnly, supportsSorting, supportsStructureEditing,
            source, sortState, alignments,
            onHeaderClick, onResizePressed, onResizeMoved, onResizeReleased,
            cellNavigation, onCellEdited, rowDrag, columnDrag,
            rebuild, themeSource, isStriped, colorTheme,
            frozenColumnCount, onFreezeColumns,
            CellGrid, CellCol, GripCol);

        // Data rows
        var dataRowStartGridRow = gridRow;
        gridRow = RenderDataRows(grid, frozenGrid, isDualGrid, effectiveFrozenCols,
            data, gridRow, colCount, showDragHandles,
            isEditable, isReadOnly, supportsStructureEditing,
            source, alignments,
            onResizePressed, onResizeMoved, onResizeReleased,
            cellNavigation, onCellEdited, rowDrag,
            rebuild, themeSource, isStriped, colorTheme,
            frozenColumnCount, frozenRowCount,
            onFreezeColumns, onFreezeRows,
            CellGrid, CellCol, GripCol, dataRowStartGridRow);

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
            Grid.SetColumn(descBlock, isDualGrid ? 0 : 1);
            Grid.SetColumnSpan(descBlock,
                Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(descBlock);
        }

        return new TableGridRenderResult
        {
            DataRowCount = data.DataRows.Count,
            TotalDisplayRows = totalDisplayRows,
            DataRowStartGridRow = dataRowStartGridRow,
            ComputedWidths = widths,
            WasAutoFit = !hasExplicitWidths
        };
    }

    private static void SetupColumnDefinitions(
        Grid grid, Grid? frozenGrid, bool isDualGrid,
        int effectiveFrozenCols, int colCount,
        double[] widths, bool showDragHandles)
    {
        if (isDualGrid)
        {
            // Frozen grid: drag handle + frozen columns + grips
            frozenGrid!.ColumnDefinitions.Add(new ColumnDefinition(
                new GridLength(showDragHandles ? 20 : 0, GridUnitType.Pixel)));
            for (var c = 0; c < effectiveFrozenCols; c++)
            {
                frozenGrid.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(widths[c], GridUnitType.Pixel)));
                frozenGrid.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(2, GridUnitType.Pixel)));
            }

            // Scrollable grid: 0px placeholder + remaining columns + grips
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                new GridLength(0, GridUnitType.Pixel)));
            for (var c = effectiveFrozenCols; c < colCount; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(widths[c], GridUnitType.Pixel)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(2, GridUnitType.Pixel)));
            }
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                new GridLength(showDragHandles ? 20 : 0, GridUnitType.Pixel)));
            for (var c = 0; c < colCount; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(widths[c], GridUnitType.Pixel)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(2, GridUnitType.Pixel)));
            }
        }
    }

    private static int RenderHeaderRows(
        Grid grid, Grid? frozenGrid, bool isDualGrid, int effectiveFrozenCols,
        TableGridData data, int gridRow, int colCount,
        bool showDragHandles,
        bool isEditable, bool isReadOnly,
        bool supportsSorting, bool supportsStructureEditing,
        ITableGridDataSource? source,
        TableGridSortState sortState,
        List<TableColumnAlignment> alignments,
        Action<int> onHeaderClick,
        Action<int, PointerPressedEventArgs> onResizePressed,
        Action<PointerEventArgs> onResizeMoved,
        Action<PointerReleasedEventArgs> onResizeReleased,
        TableGridCellNavigation? cellNavigation,
        Action<string, int, string>? onCellEdited,
        TableGridRowDrag? rowDrag,
        TableGridColumnDrag? columnDrag,
        Action? rebuild, Control themeSource,
        bool isStriped, string? colorTheme,
        int frozenColumnCount, Action<int>? onFreezeColumns,
        Func<int, Grid> cellGrid, Func<int, int> cellCol, Func<int, int> gripCol)
    {
        foreach (var headerRow in data.HeaderRows)
        {
            if (showDragHandles && rowDrag != null)
            {
                var placeholder = new Border { Width = 20 };
                Grid.SetRow(placeholder, gridRow);
                Grid.SetColumn(placeholder, 0);
                var handleGrid = isDualGrid ? frozenGrid! : grid;
                handleGrid.Children.Add(placeholder);
            }

            for (var c = 0; c < colCount; c++)
            {
                var text = c < headerRow.Cells.Count ? headerRow.Cells[c] : "";

                Control cell;
                if (isEditable && !isReadOnly && onCellEdited != null
                    && cellNavigation != null)
                {
                    var (editCell, _) = TableGridCellFactory.CreateEditableCell(
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

                if (supportsSorting && cell is Border sortBorder)
                    AddSortArrowToHeaderCell(sortBorder, c, sortState,
                        onHeaderClick, themeSource);

                // Column drag reorder (skip frozen columns in dual-grid)
                if (columnDrag != null && cell is Border dragBorder
                    && !(isDualGrid && c < effectiveFrozenCols))
                {
                    columnDrag.AttachToHeader(dragBorder, c, themeSource);
                    cell.Cursor = new Cursor(StandardCursorType.Hand);
                }

                if (source != null && rebuild != null)
                {
                    var colIdx = c;
                    var hasHeader = data.HeaderRows.Count > 0;
                    var ctxMenu = TableGridContextMenu.BuildHeaderContextMenu(
                        colIdx, hasHeader, supportsStructureEditing,
                        frozenColumnCount, source, rebuild, onFreezeColumns);
                    cell.ContextMenu = ctxMenu;
                    if (cell is Border { Child: TextBox headerTb })
                        headerTb.ContextMenu = ctxMenu;
                }

                // Frozen column boundary indicator (single-grid only)
                if (!isDualGrid && frozenColumnCount > 0
                    && c == frozenColumnCount - 1
                    && cell is Border freezeBorder)
                {
                    freezeBorder.BorderBrush =
                        new SolidColorBrush(Color.Parse("#4090CAF9"));
                    freezeBorder.BorderThickness = new Thickness(
                        freezeBorder.BorderThickness.Left,
                        freezeBorder.BorderThickness.Top,
                        2,
                        freezeBorder.BorderThickness.Bottom);
                }

                var tg = cellGrid(c);
                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, cellCol(c));
                tg.Children.Add(cell);

                AddResizeGrip(cellGrid(c), c, gripCol(c), gridRow,
                    onResizePressed, onResizeMoved, onResizeReleased);
            }
            gridRow++;
        }
        return gridRow;
    }

    private static int RenderDataRows(
        Grid grid, Grid? frozenGrid, bool isDualGrid, int effectiveFrozenCols,
        TableGridData data, int gridRow, int colCount,
        bool showDragHandles,
        bool isEditable, bool isReadOnly,
        bool supportsStructureEditing,
        ITableGridDataSource? source,
        List<TableColumnAlignment> alignments,
        Action<int, PointerPressedEventArgs> onResizePressed,
        Action<PointerEventArgs> onResizeMoved,
        Action<PointerReleasedEventArgs> onResizeReleased,
        TableGridCellNavigation? cellNavigation,
        Action<string, int, string>? onCellEdited,
        TableGridRowDrag? rowDrag,
        Action? rebuild, Control themeSource,
        bool isStriped, string? colorTheme,
        int frozenColumnCount, int frozenRowCount,
        Action<int>? onFreezeColumns, Action<int>? onFreezeRows,
        Func<int, Grid> cellGrid, Func<int, int> cellCol, Func<int, int> gripCol,
        int dataRowStartGridRow)
    {
        for (var dataIdx = 0; dataIdx < data.DataRows.Count; dataIdx++)
        {
            var dataRow = data.DataRows[dataIdx];

            if (showDragHandles && rowDrag != null)
            {
                var handle = rowDrag.CreateDragHandle(dataIdx);
                Grid.SetRow(handle, gridRow);
                Grid.SetColumn(handle, 0);
                var handleGrid = isDualGrid ? frozenGrid! : grid;
                handleGrid.Children.Add(handle);
            }

            for (var c = 0; c < colCount; c++)
            {
                var text = c < dataRow.Cells.Count ? dataRow.Cells[c] : "";

                Control cell;
                if (isEditable && !isReadOnly && onCellEdited != null
                    && cellNavigation != null)
                {
                    var (editCell, textBox) = TableGridCellFactory.CreateEditableCell(
                        text, false, dataRow.Id, c, alignments,
                        onCellEdited, cellNavigation,
                        gridRow - dataRowStartGridRow,
                        themeSource, isStriped, dataIdx, colorTheme);
                    cell = editCell;
                    cellNavigation.Register(
                        gridRow - dataRowStartGridRow, c, textBox);
                }
                else
                {
                    cell = TableGridCellFactory.CreateReadOnlyCell(
                        text, false, c, alignments, themeSource,
                        isStriped, dataIdx, colorTheme);
                }

                // Frozen column boundary indicator (single-grid only)
                if (!isDualGrid && frozenColumnCount > 0
                    && c == frozenColumnCount - 1
                    && cell is Border freezeDataBorder)
                {
                    freezeDataBorder.BorderBrush =
                        new SolidColorBrush(Color.Parse("#4090CAF9"));
                    freezeDataBorder.BorderThickness = new Thickness(
                        freezeDataBorder.BorderThickness.Left,
                        freezeDataBorder.BorderThickness.Top,
                        2,
                        freezeDataBorder.BorderThickness.Bottom);
                }

                if (source != null && rebuild != null)
                {
                    var capturedRow = dataIdx;
                    var capturedCol = c;
                    var hasHeader = data.HeaderRows.Count > 0;
                    var ctxMenu = TableGridContextMenu.BuildCellContextMenu(
                        capturedRow, capturedCol, hasHeader,
                        supportsStructureEditing,
                        frozenColumnCount, frozenRowCount,
                        source, rebuild, onFreezeColumns, onFreezeRows);
                    cell.ContextMenu = ctxMenu;
                    if (cell is Border { Child: TextBox cellTb })
                        cellTb.ContextMenu = ctxMenu;
                }

                var tg = cellGrid(c);
                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, cellCol(c));
                tg.Children.Add(cell);

                AddResizeGrip(cellGrid(c), c, gripCol(c), gridRow,
                    onResizePressed, onResizeMoved, onResizeReleased);
            }

            // Frozen row boundary indicator
            if (frozenRowCount > 0 && dataIdx == frozenRowCount - 1)
            {
                AddFreezeRowLine(grid, gridRow);
                if (isDualGrid)
                    AddFreezeRowLine(frozenGrid!, gridRow);
            }

            gridRow++;
        }
        return gridRow;
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

        return TableGridCellFactory.ComputeAutoFitWidths(
            headerCells, dataRows, colCount, themeSource);
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
        var isAsc = isActive
            && sortState.Direction == TableSortDirection.Ascending;

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
        Grid targetGrid, int originalColIndex, int targetColumn, int gridRow,
        Action<int, PointerPressedEventArgs> onPressed,
        Action<PointerEventArgs> onMoved,
        Action<PointerReleasedEventArgs> onReleased)
    {
        var grip = TableGridCellFactory.CreateResizeGrip(
            originalColIndex, onPressed, onMoved, onReleased);
        Grid.SetRow(grip, gridRow);
        Grid.SetColumn(grip, targetColumn);
        targetGrid.Children.Add(grip);
    }

    private static void AddFreezeRowLine(Grid targetGrid, int gridRow)
    {
        var freezeLine = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.Parse("#4090CAF9")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        Grid.SetRow(freezeLine, gridRow);
        Grid.SetColumn(freezeLine, 1);
        Grid.SetColumnSpan(freezeLine,
            Math.Max(1, targetGrid.ColumnDefinitions.Count - 1));
        targetGrid.Children.Add(freezeLine);
    }
}
