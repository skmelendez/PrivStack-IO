// ============================================================================
// File: TableGridCellFactory.cs
// Description: Creates read-only and editable table cells for the TableGrid.
//              Handles theme brushes, URL detection, alignment, and click-to-open.
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal static class TableGridCellFactory
{
    public static Border CreateReadOnlyCell(
        string text, bool isHeader, int colIndex,
        IReadOnlyList<TableColumnAlignment> alignments, Control themeSource)
    {
        var alignment = colIndex < alignments.Count ? alignments[colIndex] : TableColumnAlignment.Left;
        var textAlignment = alignment switch
        {
            TableColumnAlignment.Center => TextAlignment.Center,
            TableColumnAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        var isUrl = !isHeader && IsUrl(text);

        var tb = new TextBlock
        {
            Text = text,
            FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
            TextAlignment = textAlignment,
            Padding = new Thickness(12, 6),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };

        if (isUrl)
        {
            tb.TextDecorations = TextDecorations.Underline;
            tb.Cursor = new Cursor(StandardCursorType.Hand);
        }

        if (themeSource.TryFindResource("ThemeFontSans", out var font) && font is FontFamily ff)
            tb.FontFamily = ff;
        if (themeSource.TryFindResource("ThemeFontSizeMd", out var fs) && fs is double size)
            tb.FontSize = size;
        if (isUrl && themeSource.TryFindResource("ThemePrimaryBrush", out var linkBrush) && linkBrush is IBrush lb)
            tb.Foreground = lb;
        else if (themeSource.TryFindResource("ThemeTextPrimaryBrush", out var fg) && fg is IBrush fgBrush)
            tb.Foreground = fgBrush;

        var backgroundColor = isHeader
            ? GetBrush(themeSource, "ThemeTableHeaderBrush")
            : GetBrush(themeSource, "ThemeSurfaceBrush");

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = GetBrush(themeSource, "ThemeBorderBrush"),
            Background = backgroundColor,
            Child = tb
        };

        if (isUrl)
        {
            var url = text;
            tb.PointerPressed += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* best-effort */ }
                e.Handled = true;
            };
        }

        return border;
    }

    public static Border CreateResizeGrip(int colIndex, Action<int, PointerPressedEventArgs> onPressed,
        Action<PointerEventArgs> onMoved, Action<PointerReleasedEventArgs> onReleased)
    {
        var grip = new Border
        {
            Classes = { "col-resize-grip" },
            Width = 4,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Background = Brushes.Transparent,
            Tag = colIndex
        };

        grip.PointerPressed += (s, e) => onPressed(colIndex, e);
        grip.PointerMoved += (_, e) => onMoved(e);
        grip.PointerReleased += (_, e) => onReleased(e);

        return grip;
    }

    public static double MeasureCellTextWidth(string text, bool isBold, Control themeSource)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var fontFamily = FontFamily.Default;
        if (themeSource.TryFindResource("ThemeFontSans", out var font) && font is FontFamily ff)
            fontFamily = ff;
        double fontSize = 14;
        if (themeSource.TryFindResource("ThemeFontSizeMd", out var fs) && fs is double size)
            fontSize = size;

        var typeface = new Typeface(fontFamily, FontStyle.Normal, isBold ? FontWeight.Bold : FontWeight.Normal);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, null);
        return ft.Width;
    }

    public static double[] ComputeAutoFitWidths(
        IReadOnlyList<string> headerCells, IReadOnlyList<IReadOnlyList<string>> dataRows,
        int colCount, Control themeSource)
    {
        const double cellPadding = 49;
        const double minWidth = 60;

        var widths = new double[colCount];

        for (var c = 0; c < Math.Min(headerCells.Count, colCount); c++)
        {
            var textWidth = MeasureCellTextWidth(headerCells[c], true, themeSource);
            if (textWidth > widths[c])
                widths[c] = textWidth;
        }

        foreach (var row in dataRows)
        {
            for (var c = 0; c < Math.Min(row.Count, colCount); c++)
            {
                var textWidth = MeasureCellTextWidth(row[c], false, themeSource);
                if (textWidth > widths[c])
                    widths[c] = textWidth;
            }
        }

        for (var c = 0; c < colCount; c++)
            widths[c] = Math.Max(minWidth, widths[c] + cellPadding);

        return widths;
    }

    private static bool IsUrl(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static IBrush? GetBrush(Control source, string resourceKey)
    {
        if (source.TryFindResource(resourceKey, out var resource) && resource is IBrush brush)
            return brush;
        return null;
    }
}
