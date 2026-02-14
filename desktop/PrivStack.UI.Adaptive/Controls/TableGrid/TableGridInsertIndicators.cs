// ============================================================================
// File: TableGridInsertIndicators.cs
// Description: Manages "+" circle overlays on cell borders for row/column
//              insertion. Shows indicators on hover over row/column gaps.
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

    /// <summary>
    /// Attaches row insert indicators to the left edge of data rows.
    /// A "+" circle appears between rows on hover. Includes an indicator
    /// above the first data row (insert at 0) and below each data row.
    /// </summary>
    public static void AttachRowIndicators(
        Grid grid, int dataRowStartGridRow, int dataRowCount,
        ITableGridDataSource source, Action rebuild, Control themeSource)
    {
        // Insert-above-first-row indicator (insert at index 0)
        if (dataRowCount > 0)
            AttachRowIndicator(grid, dataRowStartGridRow, 0,
                VerticalAlignment.Top, source, rebuild, themeSource);

        // Insert-below-each-row indicators
        for (var dataIdx = 0; dataIdx < dataRowCount; dataIdx++)
        {
            var gridRow = dataRowStartGridRow + dataIdx;
            AttachRowIndicator(grid, gridRow, dataIdx + 1,
                VerticalAlignment.Bottom, source, rebuild, themeSource);
        }
    }

    private static void AttachRowIndicator(
        Grid grid, int gridRow, int insertIndex,
        VerticalAlignment vAlign,
        ITableGridDataSource source, Action rebuild, Control themeSource)
    {
        var indicator = CreateIndicator(themeSource);
        indicator.VerticalAlignment = vAlign;
        indicator.HorizontalAlignment = HorizontalAlignment.Left;
        indicator.Margin = new Thickness(2, 0, 0, 0);
        indicator.Opacity = 0;
        indicator.IsHitTestVisible = false;

        var hoverZone = new Border
        {
            Height = 10,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = vAlign,
        };

        var capturedIndicator = indicator;
        var capturedInsertIdx = insertIndex;

        hoverZone.PointerEntered += (_, _) =>
        {
            capturedIndicator.Opacity = 1;
            capturedIndicator.IsHitTestVisible = true;
        };
        hoverZone.PointerExited += (_, _) =>
        {
            capturedIndicator.Opacity = 0;
            capturedIndicator.IsHitTestVisible = false;
        };

        capturedIndicator.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await source.OnInsertRowAtAsync(capturedInsertIdx);
            rebuild();
        };

        // Span hover zone across column 0 and first data column for wider target
        Grid.SetRow(hoverZone, gridRow);
        Grid.SetColumn(hoverZone, 0);
        Grid.SetColumnSpan(hoverZone, 3);
        grid.Children.Add(hoverZone);

        Grid.SetRow(indicator, gridRow);
        Grid.SetColumn(indicator, 0);
        Grid.SetColumnSpan(indicator, 2);
        grid.Children.Add(indicator);
    }

    /// <summary>
    /// Attaches column insert indicators to the resize grip zones between columns.
    /// The "+" circle appears centered at the top of each grip on hover.
    /// </summary>
    public static void AttachColumnIndicators(
        Grid grid, int colCount, int headerGridRow,
        ITableGridDataSource source, Action rebuild, Control themeSource)
    {
        for (var c = 0; c < colCount - 1; c++)
        {
            var insertIndex = c + 1;
            var gripCol = c * 2 + 2; // Grip columns are at even indices after first data col

            var indicator = CreateIndicator(themeSource);
            indicator.VerticalAlignment = VerticalAlignment.Center;
            indicator.HorizontalAlignment = HorizontalAlignment.Center;
            indicator.Opacity = 0;
            indicator.IsHitTestVisible = false;

            var capturedIndicator = indicator;
            var capturedInsertIdx = insertIndex;

            // Find existing grip borders at this column and attach hover events
            foreach (var child in grid.Children)
            {
                if (child is Border grip &&
                    grip.Classes.Contains("col-resize-grip") &&
                    Grid.GetColumn(grip) == gripCol &&
                    Grid.GetRow(grip) == headerGridRow)
                {
                    grip.PointerEntered += (_, _) =>
                    {
                        capturedIndicator.Opacity = 1;
                        capturedIndicator.IsHitTestVisible = true;
                    };
                    grip.PointerExited += (_, _) =>
                    {
                        capturedIndicator.Opacity = 0;
                        capturedIndicator.IsHitTestVisible = false;
                    };
                    break;
                }
            }

            capturedIndicator.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                await source.OnInsertColumnAtAsync(capturedInsertIdx);
                rebuild();
            };

            Grid.SetRow(indicator, headerGridRow);
            Grid.SetColumn(indicator, gripCol);
            grid.Children.Add(indicator);
        }
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
