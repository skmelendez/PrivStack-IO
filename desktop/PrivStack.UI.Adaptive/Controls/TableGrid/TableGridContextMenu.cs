// ============================================================================
// File: TableGridContextMenu.cs
// Description: Right-click context menus for table grid cells. Builds a unified
//              menu with Row and Column submenus using positional operations.
// ============================================================================

using Avalonia.Controls;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal static class TableGridContextMenu
{
    /// <summary>
    /// Builds a context menu for a cell at the given row/column position.
    /// Row submenu: Add Above, Add Below, Remove.
    /// Column submenu: Add Left, Add Right, Remove.
    /// </summary>
    public static ContextMenu BuildCellContextMenu(
        int rowIndex, int colIndex,
        ITableGridDataSource source, Action rebuild)
    {
        var menu = new ContextMenu();

        // ── Row submenu ──────────────────────────────────────────────
        var rowMenu = new MenuItem { Header = "Row" };

        var addAbove = new MenuItem { Header = "Add Above" };
        addAbove.Click += async (_, _) =>
        {
            await source.OnInsertRowAtAsync(rowIndex);
            rebuild();
        };
        rowMenu.Items.Add(addAbove);

        var addBelow = new MenuItem { Header = "Add Below" };
        addBelow.Click += async (_, _) =>
        {
            await source.OnInsertRowAtAsync(rowIndex + 1);
            rebuild();
        };
        rowMenu.Items.Add(addBelow);

        rowMenu.Items.Add(new Separator());

        var removeRow = new MenuItem { Header = "Remove" };
        removeRow.Click += async (_, _) =>
        {
            await source.OnDeleteRowAtAsync(rowIndex);
            rebuild();
        };
        rowMenu.Items.Add(removeRow);

        menu.Items.Add(rowMenu);

        // ── Column submenu ───────────────────────────────────────────
        var colMenu = new MenuItem { Header = "Column" };

        var addLeft = new MenuItem { Header = "Add Left" };
        addLeft.Click += async (_, _) =>
        {
            await source.OnInsertColumnAtAsync(colIndex);
            rebuild();
        };
        colMenu.Items.Add(addLeft);

        var addRight = new MenuItem { Header = "Add Right" };
        addRight.Click += async (_, _) =>
        {
            await source.OnInsertColumnAtAsync(colIndex + 1);
            rebuild();
        };
        colMenu.Items.Add(addRight);

        colMenu.Items.Add(new Separator());

        var removeCol = new MenuItem { Header = "Remove" };
        removeCol.Click += async (_, _) =>
        {
            await source.OnDeleteColumnAtAsync(colIndex);
            rebuild();
        };
        colMenu.Items.Add(removeCol);

        menu.Items.Add(colMenu);

        return menu;
    }

    /// <summary>
    /// Builds a header-only context menu (column operations + alignment).
    /// </summary>
    public static ContextMenu BuildHeaderContextMenu(
        int colIndex, ITableGridDataSource source, Action rebuild)
    {
        var menu = new ContextMenu();

        // ── Column submenu ───────────────────────────────────────────
        var colMenu = new MenuItem { Header = "Column" };

        var addLeft = new MenuItem { Header = "Add Left" };
        addLeft.Click += async (_, _) =>
        {
            await source.OnInsertColumnAtAsync(colIndex);
            rebuild();
        };
        colMenu.Items.Add(addLeft);

        var addRight = new MenuItem { Header = "Add Right" };
        addRight.Click += async (_, _) =>
        {
            await source.OnInsertColumnAtAsync(colIndex + 1);
            rebuild();
        };
        colMenu.Items.Add(addRight);

        colMenu.Items.Add(new Separator());

        var removeCol = new MenuItem { Header = "Remove" };
        removeCol.Click += async (_, _) =>
        {
            await source.OnDeleteColumnAtAsync(colIndex);
            rebuild();
        };
        colMenu.Items.Add(removeCol);

        menu.Items.Add(colMenu);

        // ── Alignment submenu ────────────────────────────────────────
        menu.Items.Add(new Separator());
        var alignMenu = new MenuItem { Header = "Alignment" };

        var alignLeft = new MenuItem { Header = "Left" };
        alignLeft.Click += async (_, _) =>
        {
            await source.OnColumnAlignmentChangedAsync(colIndex, TableColumnAlignment.Left);
            rebuild();
        };
        var alignCenter = new MenuItem { Header = "Center" };
        alignCenter.Click += async (_, _) =>
        {
            await source.OnColumnAlignmentChangedAsync(colIndex, TableColumnAlignment.Center);
            rebuild();
        };
        var alignRight = new MenuItem { Header = "Right" };
        alignRight.Click += async (_, _) =>
        {
            await source.OnColumnAlignmentChangedAsync(colIndex, TableColumnAlignment.Right);
            rebuild();
        };
        alignMenu.Items.Add(alignLeft);
        alignMenu.Items.Add(alignCenter);
        alignMenu.Items.Add(alignRight);
        menu.Items.Add(alignMenu);

        return menu;
    }
}
