// ============================================================================
// File: TableGridInsertIndicators.cs
// Description: Manages floating "+" circle indicators for row/column insertion.
//              Uses grid-level PointerMoved to track mouse position and show
//              indicators at cell borders — no PointerEntered/Exited flickering.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal static class TableGridInsertIndicators
{
    private const double IndicatorSize = 16;
    private const double EdgeThreshold = 12;

    /// <summary>
    /// Attaches floating row/column insert indicators to the grid.
    /// Row "+" appears at the bottom-center of the hovered data cell.
    /// Column "+" appears at the left/right border of the hovered cell,
    /// centered vertically, as the mouse approaches that edge.
    /// </summary>
    public static void AttachCellIndicators(
        Grid grid, int headerGridRow, int dataRowStartGridRow,
        int dataRowCount, int colCount,
        ITableGridDataSource source, Action rebuild, Control themeSource)
    {
        var rowIndicator = CreateIndicator(themeSource);
        rowIndicator.VerticalAlignment = VerticalAlignment.Bottom;
        rowIndicator.HorizontalAlignment = HorizontalAlignment.Center;
        rowIndicator.Margin = new Thickness(0, 0, 0, -IndicatorSize / 2);
        rowIndicator.Opacity = 0;
        rowIndicator.IsHitTestVisible = false;
        grid.Children.Add(rowIndicator);

        var colIndicator = CreateIndicator(themeSource);
        colIndicator.VerticalAlignment = VerticalAlignment.Center;
        colIndicator.Opacity = 0;
        colIndicator.IsHitTestVisible = false;
        grid.Children.Add(colIndicator);

        var currentRowInsertIdx = -1;
        var currentColInsertIdx = -1;

        grid.PointerMoved += (_, e) =>
        {
            var pos = e.GetPosition(grid);

            // ── Hit test: find grid row ──────────────────────────────
            int hitGridRow = -1;
            double rowRelY = 0, rowHeight = 0;
            {
                double cumY = 0;
                for (var r = 0; r < grid.RowDefinitions.Count; r++)
                {
                    var rh = grid.RowDefinitions[r].ActualHeight;
                    if (pos.Y >= cumY && pos.Y < cumY + rh)
                    {
                        hitGridRow = r;
                        rowRelY = pos.Y - cumY;
                        rowHeight = rh;
                        break;
                    }
                    cumY += rh;
                }
            }

            // ── Hit test: find data column ───────────────────────────
            int hitDataCol = -1, hitGridCol = -1;
            double colRelX = 0, colWidth = 0;
            {
                double cumX = 0;
                for (var c = 0; c < grid.ColumnDefinitions.Count; c++)
                {
                    var cw = grid.ColumnDefinitions[c].ActualWidth;
                    if (pos.X >= cumX && pos.X < cumX + cw)
                    {
                        // Data columns are at odd indices (1, 3, 5, ...)
                        if (c >= 1 && c % 2 == 1)
                        {
                            hitDataCol = (c - 1) / 2;
                            hitGridCol = c;
                            colRelX = pos.X - cumX;
                            colWidth = cw;
                        }
                        break;
                    }
                    cumX += cw;
                }
            }

            var hitDataRow = hitGridRow >= 0 ? hitGridRow - dataRowStartGridRow : -1;
            var isDataRow = hitDataRow >= 0 && hitDataRow < dataRowCount;
            var isTableRow = hitGridRow >= headerGridRow
                             && hitGridRow < dataRowStartGridRow + dataRowCount;

            // ── Row indicator (bottom border of hovered data cell) ───
            if (isDataRow && hitGridCol >= 0 && rowHeight > 0
                && rowRelY > rowHeight - EdgeThreshold)
            {
                currentRowInsertIdx = hitDataRow + 1;
                Grid.SetRow(rowIndicator, hitGridRow);
                Grid.SetColumn(rowIndicator, hitGridCol);
                rowIndicator.Opacity = 1;
                rowIndicator.IsHitTestVisible = true;
            }
            else
            {
                rowIndicator.Opacity = 0;
                rowIndicator.IsHitTestVisible = false;
                currentRowInsertIdx = -1;
            }

            // ── Column indicator (left/right border) ─────────────────
            var showCol = false;
            if (hitDataCol >= 0 && hitGridRow >= 0 && isTableRow && colWidth > 0)
            {
                var nearLeft = colRelX < EdgeThreshold;
                var nearRight = colRelX > colWidth - EdgeThreshold;

                if (nearRight)
                {
                    currentColInsertIdx = hitDataCol + 1;
                    Grid.SetRow(colIndicator, hitGridRow);
                    Grid.SetColumn(colIndicator, hitDataCol * 2 + 2);
                    colIndicator.HorizontalAlignment = HorizontalAlignment.Center;
                    showCol = true;
                }
                else if (nearLeft && hitDataCol > 0)
                {
                    currentColInsertIdx = hitDataCol;
                    Grid.SetRow(colIndicator, hitGridRow);
                    Grid.SetColumn(colIndicator, (hitDataCol - 1) * 2 + 2);
                    colIndicator.HorizontalAlignment = HorizontalAlignment.Center;
                    showCol = true;
                }
                else if (nearLeft && hitDataCol == 0)
                {
                    currentColInsertIdx = 0;
                    Grid.SetRow(colIndicator, hitGridRow);
                    Grid.SetColumn(colIndicator, hitGridCol);
                    colIndicator.HorizontalAlignment = HorizontalAlignment.Left;
                    showCol = true;
                }
            }

            colIndicator.Opacity = showCol ? 1 : 0;
            colIndicator.IsHitTestVisible = showCol;
            if (!showCol) currentColInsertIdx = -1;
        };

        grid.PointerExited += (_, _) =>
        {
            rowIndicator.Opacity = 0;
            colIndicator.Opacity = 0;
            rowIndicator.IsHitTestVisible = false;
            colIndicator.IsHitTestVisible = false;
            currentRowInsertIdx = -1;
            currentColInsertIdx = -1;
        };

        rowIndicator.PointerPressed += async (_, e) =>
        {
            if (currentRowInsertIdx < 0) return;
            e.Handled = true;
            var idx = currentRowInsertIdx;
            await source.OnInsertRowAtAsync(idx);
            rebuild();
        };

        colIndicator.PointerPressed += async (_, e) =>
        {
            if (currentColInsertIdx < 0) return;
            e.Handled = true;
            var idx = currentColInsertIdx;
            await source.OnInsertColumnAtAsync(idx);
            rebuild();
        };
    }

    private static Border CreateIndicator(Control themeSource)
    {
        var primaryBrush = Brushes.DodgerBlue as IBrush;
        if (themeSource.TryFindResource("ThemePrimaryBrush", out var res) && res is IBrush brush)
            primaryBrush = brush;

        var text = new TextBlock
        {
            Text = "+",
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };

        return new Border
        {
            Width = IndicatorSize,
            Height = IndicatorSize,
            CornerRadius = new CornerRadius(IndicatorSize / 2),
            Background = primaryBrush,
            Child = text,
            Cursor = new Cursor(StandardCursorType.Hand),
            ZIndex = 10,
        };
    }
}
