// ============================================================================
// File: InfiniteCanvasRendering.cs
// Description: Render override for InfiniteCanvasControl. Draws background,
//              grid, elements, connectors, selection overlays, and perf badge.
// ============================================================================

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private static readonly Color DefaultNoteColor = Color.FromRgb(255, 242, 153);
    private static readonly Color DefaultRectColor = Color.FromRgb(200, 210, 230);
    private static readonly Color DefaultEllipseColor = Color.FromRgb(200, 230, 210);
    private static readonly Color DefaultGroupColor = Color.FromRgb(220, 220, 240);
    private static readonly Color DefaultPageRefColor = Color.FromRgb(220, 235, 255);

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var bgBrush = GetBrush("ThemeBackgroundBrush", Brushes.Black);
        ctx.DrawRectangle(bgBrush, null, new Rect(Bounds.Size));

        if (ShowGrid)
            DrawGrid(ctx);

        var data = Data;
        if (data == null || data.Elements.Count == 0)
        {
            RenderEmptyState(ctx);
            return;
        }

        var viewport = GetWorldViewport();

        // Draw connectors first (behind elements)
        foreach (var connector in data.Connectors)
        {
            DrawConnector(ctx, connector, data);
        }

        // Draw pending connector if in creation mode
        DrawPendingConnector(ctx);

        // Draw elements sorted by Z-index
        foreach (var element in data.Elements.OrderBy(e => e.ZIndex))
        {
            if (!IsElementVisible(element, viewport))
                continue;

            switch (element.Type)
            {
                case CanvasElementType.NoteCard:
                    DrawNoteCard(ctx, element);
                    break;
                case CanvasElementType.Text:
                    DrawTextElement(ctx, element);
                    break;
                case CanvasElementType.Rect:
                    DrawRectElement(ctx, element);
                    break;
                case CanvasElementType.Ellipse:
                    DrawEllipseElement(ctx, element);
                    break;
                case CanvasElementType.PageReference:
                    DrawPageReference(ctx, element);
                    break;
                case CanvasElementType.GroupFrame:
                    DrawGroupFrame(ctx, element);
                    break;
                case CanvasElementType.Freehand:
                    DrawFreehandStroke(ctx, element);
                    break;
                case CanvasElementType.Image:
                    DrawImagePlaceholder(ctx, element);
                    break;
            }
        }

        // Draw active freehand stroke
        DrawActiveStroke(ctx);

        // Draw selection overlays
        DrawSelectionOverlays(ctx, data);

        // Draw selection marquee
        DrawMarquee(ctx);

        // Draw creation preview
        if (_isCreating)
            DrawCreationPreview(ctx);

        // Performance badge
        if (data.Elements.Count >= PerformanceWarningThreshold)
            DrawPerformanceBadge(ctx, data.Elements.Count);
    }

    private static bool IsElementVisible(CanvasElement element, Rect viewport)
    {
        var elementRect = new Rect(element.X, element.Y, element.Width, element.Height);
        return viewport.Intersects(elementRect);
    }

    private void DrawGrid(DrawingContext ctx)
    {
        var gridSpacing = 40.0 * Zoom;
        if (gridSpacing < 10) return;

        var dotBrush = GetBrush("ThemeBorderSubtleBrush", Brushes.DarkGray);
        var dotRadius = Math.Max(1.0, 1.5 * Zoom);
        var w = Bounds.Width;
        var h = Bounds.Height;

        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        var offsetX = (cx + _panX * Zoom) % gridSpacing;
        var offsetY = (cy + _panY * Zoom) % gridSpacing;

        for (var x = offsetX; x < w; x += gridSpacing)
            for (var y = offsetY; y < h; y += gridSpacing)
                ctx.DrawEllipse(dotBrush, null, new Point(x, y), dotRadius, dotRadius);
    }

    private void RenderEmptyState(DrawingContext ctx)
    {
        var textBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
        var text = new FormattedText(
            "Select a tool and click to create elements",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            14, textBrush);
        var x = (Bounds.Width - text.Width) / 2;
        var y = (Bounds.Height - text.Height) / 2;
        ctx.DrawText(text, new Point(x, y));
    }

    private void DrawNoteCard(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var color = ParseColor(element.Color, DefaultNoteColor);
        var brush = new SolidColorBrush(color, 0.9);
        var borderPen = new Pen(new SolidColorBrush(Colors.Black, 0.15), 1);
        ctx.DrawRectangle(brush, borderPen, rect, 8, 8);

        if (string.IsNullOrEmpty(element.Text)) return;

        using (ctx.PushClip(rect))
        {
            var pad = 10 * Zoom;
            var fontSize = Math.Max(8, element.FontSize * Zoom);
            var textBrush = new SolidColorBrush(Colors.Black, 0.85);
            var ft = new FormattedText(
                element.Text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetSansTypeface(FontWeight.Normal),
                fontSize, textBrush);
            ft.MaxTextWidth = Math.Max(10, rect.Width - pad * 2);
            ft.MaxTextHeight = Math.Max(10, rect.Height - pad * 2);
            ctx.DrawText(ft, new Point(rect.X + pad, rect.Y + pad));
        }
    }

    private void DrawTextElement(DrawingContext ctx, CanvasElement element)
    {
        if (string.IsNullOrEmpty(element.Text)) return;

        var tl = WorldToScreen(element.X, element.Y);
        var fontSize = Math.Max(8, element.FontSize * Zoom);
        var textBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var ft = new FormattedText(
            element.Text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);
        ft.MaxTextWidth = Math.Max(10, element.Width * Zoom);
        ctx.DrawText(ft, new Point(tl.X, tl.Y));
    }

    private void DrawRectElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var color = ParseColor(element.Color, DefaultRectColor);
        var brush = new SolidColorBrush(color, 0.6);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);
        ctx.DrawRectangle(brush, pen, rect, 4, 4);

        DrawElementLabel(ctx, element, rect);
    }

    private void DrawEllipseElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var color = ParseColor(element.Color, DefaultEllipseColor);
        var brush = new SolidColorBrush(color, 0.6);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        ctx.DrawEllipse(brush, pen, center, rect.Width / 2, rect.Height / 2);

        DrawElementLabel(ctx, element, rect);
    }

    private void DrawPageReference(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var brush = new SolidColorBrush(DefaultPageRefColor, 0.9);
        var borderPen = new Pen(GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue), 2 * Zoom);
        ctx.DrawRectangle(brush, borderPen, rect, 6, 6);

        // Page icon (small doc indicator)
        var iconSize = 16 * Zoom;
        var pad = 10 * Zoom;
        var iconBrush = GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue);
        var iconRect = new Rect(rect.X + pad, rect.Y + pad, iconSize, iconSize);
        ctx.DrawRectangle(null, new Pen(iconBrush, 1.5 * Zoom), iconRect, 2, 2);

        // Page title text
        var title = string.IsNullOrEmpty(element.Text) ? "Page Reference" : element.Text;
        var fontSize = Math.Max(8, 13 * Zoom);
        var ft = new FormattedText(
            title, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.SemiBold),
            fontSize, iconBrush);
        ft.MaxTextWidth = Math.Max(10, rect.Width - pad * 2 - iconSize - 4 * Zoom);
        ctx.DrawText(ft, new Point(rect.X + pad + iconSize + 4 * Zoom, rect.Y + pad));
    }

    private void DrawGroupFrame(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var color = ParseColor(element.Color, DefaultGroupColor);
        var brush = new SolidColorBrush(color, 0.15);
        var pen = new Pen(new SolidColorBrush(color, 0.5), 2 * Zoom)
        {
            DashStyle = DashStyle.Dash,
        };
        ctx.DrawRectangle(brush, pen, rect, 8, 8);

        // Label at top
        if (!string.IsNullOrEmpty(element.Label))
        {
            var fontSize = Math.Max(8, 12 * Zoom);
            var textBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
            var ft = new FormattedText(
                element.Label, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetSansTypeface(FontWeight.SemiBold),
                fontSize, textBrush);
            ctx.DrawText(ft, new Point(rect.X + 8 * Zoom, rect.Y + 4 * Zoom));
        }
    }

    private void DrawImagePlaceholder(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var brush = new SolidColorBrush(Colors.Gray, 0.2);
        var pen = new Pen(new SolidColorBrush(Colors.Gray, 0.4), 1);
        ctx.DrawRectangle(brush, pen, rect, 4, 4);

        // Placeholder text
        var fontSize = Math.Max(8, 12 * Zoom);
        var textBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
        var ft = new FormattedText(
            "Image", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);
        var tx = rect.X + (rect.Width - ft.Width) / 2;
        var ty = rect.Y + (rect.Height - ft.Height) / 2;
        ctx.DrawText(ft, new Point(tx, ty));
    }

    private void DrawElementLabel(DrawingContext ctx, CanvasElement element, Rect rect)
    {
        if (string.IsNullOrEmpty(element.Text)) return;

        var fontSize = Math.Max(8, element.FontSize * Zoom);
        var textBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var ft = new FormattedText(
            element.Text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);
        ft.MaxTextWidth = Math.Max(10, rect.Width - 16 * Zoom);
        var tx = rect.X + (rect.Width - Math.Min(ft.Width, ft.MaxTextWidth)) / 2;
        var ty = rect.Y + (rect.Height - ft.Height) / 2;
        ctx.DrawText(ft, new Point(tx, ty));
    }

    private void DrawCreationPreview(DrawingContext ctx)
    {
        var (startWx, startWy) = ScreenToWorld(_creationStart);
        var (endWx, endWy) = ScreenToWorld(_creationEnd);
        var x = Math.Min(startWx, endWx);
        var y = Math.Min(startWy, endWy);
        var w = Math.Abs(endWx - startWx);
        var h = Math.Abs(endWy - startWy);

        var tl = WorldToScreen(x, y);
        var rect = new Rect(tl.X, tl.Y, w * Zoom, h * Zoom);

        var brush = new SolidColorBrush(Colors.CornflowerBlue, 0.2);
        var pen = new Pen(Brushes.CornflowerBlue, 1) { DashStyle = DashStyle.Dash };
        ctx.DrawRectangle(brush, pen, rect, 4, 4);
    }

    private void DrawPerformanceBadge(DrawingContext ctx, int count)
    {
        var badgeBrush = new SolidColorBrush(Colors.OrangeRed, 0.9);
        var badgeRect = new Rect(Bounds.Width - 160, 8, 150, 28);
        ctx.DrawRectangle(badgeBrush, null, badgeRect, 6, 6);

        var ft = new FormattedText(
            $"{count} elements",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.SemiBold),
            12, Brushes.White);
        ctx.DrawText(ft, new Point(badgeRect.X + 8, badgeRect.Y + 6));
    }

    private static Color ParseColor(string? colorStr, Color fallback)
    {
        if (string.IsNullOrEmpty(colorStr))
            return fallback;

        try
        {
            if (colorStr.StartsWith('#'))
                return Color.Parse(colorStr);

            return colorStr switch
            {
                "yellow" => Color.FromRgb(255, 242, 153),
                "blue" => Color.FromRgb(153, 204, 255),
                "green" => Color.FromRgb(153, 230, 179),
                "pink" => Color.FromRgb(255, 179, 204),
                "purple" => Color.FromRgb(204, 179, 255),
                "orange" => Color.FromRgb(255, 204, 153),
                "red" => Color.FromRgb(255, 153, 153),
                "gray" => Color.FromRgb(200, 200, 200),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    // Creation preview endpoint (set during pointer move)
    private Point _creationEnd;
}
