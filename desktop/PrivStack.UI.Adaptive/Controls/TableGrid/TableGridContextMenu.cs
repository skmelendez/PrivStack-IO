// ============================================================================
// File: TableGridContextMenu.cs
// Description: Right-click context menus for table grid cells. Builds menus
//              with Row/Column submenus (when structure editing is supported)
//              and header row toggle (always available).
// ============================================================================

using Avalonia.Controls;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal static class TableGridContextMenu
{
    /// <summary>
    /// Builds a context menu for a data cell. Includes Row/Column submenus
    /// when structure editing is supported, and header toggle for all tables.
    /// </summary>
    public static ContextMenu BuildCellContextMenu(
        int rowIndex, int colIndex, bool hasHeaderRow,
        bool supportsStructureEditing,
        int frozenRowCount,
        ITableGridDataSource source, Action rebuild)
    {
        var menu = new ContextMenu();

        if (supportsStructureEditing)
        {
            // ── Row submenu ──────────────────────────────────────────
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

            // ── Column submenu ───────────────────────────────────────
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
            menu.Items.Add(new Separator());
        }

        // ── Freeze row ─────────────────────────────────────────────
        if (frozenRowCount > 0)
        {
            var unfreezeRows = new MenuItem { Header = "Unfreeze Rows" };
            unfreezeRows.Click += async (_, _) =>
            {
                await source.OnFreezeRowsAsync(0);
                rebuild();
            };
            menu.Items.Add(unfreezeRows);
        }
        else
        {
            var freezeRow = new MenuItem { Header = "Freeze This Row" };
            freezeRow.Click += async (_, _) =>
            {
                await source.OnFreezeRowsAsync(rowIndex + 1);
                rebuild();
            };
            menu.Items.Add(freezeRow);
        }

        menu.Items.Add(new Separator());

        // ── Header row toggle (always available) ─────────────────────
        var headerToggle = new MenuItem
        {
            Header = hasHeaderRow ? "Disable Header Row" : "Enable Header Row"
        };
        headerToggle.Click += async (_, _) =>
        {
            await source.OnToggleHeaderAsync(!hasHeaderRow);
            rebuild();
        };
        menu.Items.Add(headerToggle);

        return menu;
    }

    /// <summary>
    /// Builds a context menu for a header cell. Includes Column submenu
    /// when structure editing is supported, alignment options, and header toggle.
    /// </summary>
    public static ContextMenu BuildHeaderContextMenu(
        int colIndex, bool hasHeaderRow,
        bool supportsStructureEditing,
        int frozenColumnCount,
        ITableGridDataSource source, Action rebuild)
    {
        var menu = new ContextMenu();

        if (supportsStructureEditing)
        {
            // ── Column submenu ───────────────────────────────────────
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

            // ── Alignment submenu ────────────────────────────────────
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

            menu.Items.Add(new Separator());
        }

        // ── Freeze columns ─────────────────────────────────────────
        if (frozenColumnCount > 0)
        {
            var unfreezeCol = new MenuItem { Header = "Unfreeze Columns" };
            unfreezeCol.Click += async (_, _) =>
            {
                await source.OnFreezeColumnsAsync(0);
                rebuild();
            };
            menu.Items.Add(unfreezeCol);
        }
        else
        {
            var freezeCol = new MenuItem { Header = "Freeze From Here" };
            freezeCol.Click += async (_, _) =>
            {
                await source.OnFreezeColumnsAsync(colIndex + 1);
                rebuild();
            };
            menu.Items.Add(freezeCol);
        }

        menu.Items.Add(new Separator());

        // ── Header row toggle (always available) ─────────────────────
        var headerToggle = new MenuItem
        {
            Header = hasHeaderRow ? "Disable Header Row" : "Enable Header Row"
        };
        headerToggle.Click += async (_, _) =>
        {
            await source.OnToggleHeaderAsync(!hasHeaderRow);
            rebuild();
        };
        menu.Items.Add(headerToggle);

        return menu;
    }
}
