// ============================================================================
// File: TableGridContextMenu.cs
// Description: Right-click context menus for table grid rows and columns.
//              Provides insert/delete/alignment/toggle-header operations via
//              the ITableGridDataSource interface.
// ============================================================================

using Avalonia.Controls;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal static class TableGridContextMenu
{
    public static ContextMenu BuildRowContextMenu(
        int rowIndex, ITableGridDataSource source, Action rebuild)
    {
        var menu = new ContextMenu();

        var insertAbove = new MenuItem { Header = "Insert Row Above" };
        insertAbove.Click += async (_, _) =>
        {
            await source.OnAddRowAsync();
            rebuild();
        };

        var insertBelow = new MenuItem { Header = "Insert Row Below" };
        insertBelow.Click += async (_, _) =>
        {
            await source.OnAddRowAsync();
            rebuild();
        };

        var toggleHeader = new MenuItem { Header = "Toggle Header" };
        toggleHeader.Click += async (_, _) =>
        {
            await source.OnToggleHeaderAsync(true);
            rebuild();
        };

        var deleteRow = new MenuItem { Header = "Delete Row" };
        deleteRow.Click += async (_, _) =>
        {
            await source.OnRemoveRowAsync();
            rebuild();
        };

        menu.Items.Add(insertAbove);
        menu.Items.Add(insertBelow);
        menu.Items.Add(new Separator());
        menu.Items.Add(toggleHeader);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteRow);

        return menu;
    }

    public static ContextMenu BuildColumnContextMenu(
        int colIndex, ITableGridDataSource source, Action rebuild)
    {
        var menu = new ContextMenu();

        var insertLeft = new MenuItem { Header = "Insert Column Left" };
        insertLeft.Click += async (_, _) =>
        {
            await source.OnAddColumnAsync();
            rebuild();
        };

        var insertRight = new MenuItem { Header = "Insert Column Right" };
        insertRight.Click += async (_, _) =>
        {
            await source.OnAddColumnAsync();
            rebuild();
        };

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

        var deleteCol = new MenuItem { Header = "Delete Column" };
        deleteCol.Click += async (_, _) =>
        {
            await source.OnRemoveColumnAsync();
            rebuild();
        };

        menu.Items.Add(insertLeft);
        menu.Items.Add(insertRight);
        menu.Items.Add(new Separator());
        menu.Items.Add(alignMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteCol);

        return menu;
    }
}
