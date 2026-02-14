// ============================================================================
// File: TableGridCellFactory.cs
// Description: Creates read-only and editable table cells for the TableGrid.
//              Handles theme brushes, URL detection, alignment, striping,
//              color themes, and click-to-open.
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
        IReadOnlyList<TableColumnAlignment> alignments, Control themeSource,
        bool isStriped = false, int dataRowIndex = 0, string? colorTheme = null)
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
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            TextAlignment = textAlignment,
            Padding = new Thickness(12, 10),
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

        var backgroundColor = GetCellBackground(themeSource, isHeader, isStriped, dataRowIndex, colorTheme);

        var border = new Border
        {
            BorderThickness = isHeader ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            BorderBrush = isHeader ? GetBrush(themeSource, "ThemeBorderSubtleBrush") : null,
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
            Width = 2,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Background = Brushes.Transparent,
            Margin = new Thickness(-3, 0),
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

    public static (Border cell, TextBox textBox) CreateEditableCell(
        string text, bool isHeader, string rowId, int colIndex,
        IReadOnlyList<TableColumnAlignment> alignments,
        Action<string, int, string> onCellEdited,
        TableGridCellNavigation navigation, int displayRow,
        Control themeSource,
        bool isStriped = false, int dataRowIndex = 0, string? colorTheme = null)
    {
        var alignment = colIndex < alignments.Count ? alignments[colIndex] : TableColumnAlignment.Left;
        var textAlignment = alignment switch
        {
            TableColumnAlignment.Center => TextAlignment.Center,
            TableColumnAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        var tb = new TextBox
        {
            Text = text,
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            TextAlignment = textAlignment,
            AcceptsReturn = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 10),
            MinWidth = 60,
            MinHeight = 32,
            TextWrapping = TextWrapping.Wrap
        };

        if (themeSource.TryFindResource("ThemeFontSans", out var font) && font is FontFamily ff)
            tb.FontFamily = ff;
        if (themeSource.TryFindResource("ThemeFontSizeMd", out var fs) && fs is double size)
            tb.FontSize = size;
        if (themeSource.TryFindResource("ThemeTextPrimaryBrush", out var fg) && fg is IBrush fgBrush)
            tb.Foreground = fgBrush;

        var capturedRowId = rowId;
        var capturedCol = colIndex;
        tb.LostFocus += (_, _) => onCellEdited(capturedRowId, capturedCol, tb.Text ?? "");

        // Cell navigation — use Tunnel routing so keys are handled before the
        // TextBox's internal OnKeyDown (which may swallow Up/Down for wrapped text).
        var dr = displayRow;
        var ci = colIndex;
        tb.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    onCellEdited(capturedRowId, capturedCol, tb.Text ?? "");
                    navigation.HandleArrowDown(dr, ci);
                    break;
                case Key.Tab:
                    e.Handled = true;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                        navigation.NavigateToPreviousCell(dr, ci);
                    else
                        navigation.NavigateToNextCell(dr, ci);
                    break;
                case Key.Up:
                    navigation.HandleArrowUp(dr, ci);
                    e.Handled = true;
                    break;
                case Key.Down:
                    navigation.HandleArrowDown(dr, ci);
                    e.Handled = true;
                    break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var backgroundColor = GetCellBackground(themeSource, isHeader, isStriped, dataRowIndex, colorTheme);

        var border = new Border
        {
            BorderThickness = isHeader ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            BorderBrush = isHeader ? GetBrush(themeSource, "ThemeBorderSubtleBrush") : null,
            Background = backgroundColor,
            Child = tb
        };

        return (border, tb);
    }

    private static IBrush? GetCellBackground(
        Control themeSource, bool isHeader, bool isStriped, int dataRowIndex, string? colorTheme)
    {
        if (isHeader)
        {
            var themeHeaderKey = ColorThemeBrushKey(colorTheme, "Header");
            if (themeHeaderKey != null)
            {
                var themed = GetBrush(themeSource, themeHeaderKey);
                if (themed != null) return themed;
            }
            return GetBrush(themeSource, "ThemeSurfaceElevatedBrush");
        }

        if (isStriped && dataRowIndex % 2 == 1)
        {
            var themeStripeKey = ColorThemeBrushKey(colorTheme, "Stripe");
            if (themeStripeKey != null)
            {
                var themed = GetBrush(themeSource, themeStripeKey);
                if (themed != null) return themed;
            }
            return GetBrush(themeSource, "ThemeTableStripeBrush")
                   ?? GetBrush(themeSource, "ThemeSurfaceBrush");
        }

        return GetBrush(themeSource, "ThemeSurfaceBrush");
    }

    private static string? ColorThemeBrushKey(string? theme, string part)
    {
        if (string.IsNullOrEmpty(theme)) return null;
        return $"ThemeTable{theme}{part}Brush";
    }

    private static bool IsUrl(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    internal static IBrush? GetBrush(Control source, string resourceKey)
    {
        if (source.TryFindResource(resourceKey, out var resource) && resource is IBrush brush)
            return brush;
        return null;
    }
}
