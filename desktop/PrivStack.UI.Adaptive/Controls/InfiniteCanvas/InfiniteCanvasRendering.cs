// ============================================================================
// File: InfiniteCanvasRendering.cs
// Description: Render override for InfiniteCanvasControl. Draws background,
//              grid, elements, connectors, selection overlays, and perf badge.
//              Element-specific drawing is in InfiniteCanvasElementRendering.cs.
// ============================================================================

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
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
                case CanvasElementType.EntityReference:
                    DrawEntityReference(ctx, element);
                    break;
                case CanvasElementType.Diamond:
                    DrawDiamondElement(ctx, element);
                    break;
                case CanvasElementType.Parallelogram:
                    DrawParallelogramElement(ctx, element);
                    break;
                case CanvasElementType.Cylinder:
                    DrawCylinderElement(ctx, element);
                    break;
                case CanvasElementType.Hexagon:
                    DrawHexagonElement(ctx, element);
                    break;
                case CanvasElementType.RoundedRect:
                    DrawRoundedRectElement(ctx, element);
                    break;
                case CanvasElementType.Triangle:
                    DrawTriangleElement(ctx, element);
                    break;
            }
        }

        // Draw active freehand stroke
        DrawActiveStroke(ctx);

        // Draw anchor points when connector tool is active
        if (ToolMode == CanvasToolMode.Connector || _isConnecting)
            DrawAnchorPoints(ctx, data, viewport);

        // Draw connector selection highlight
        DrawConnectorSelectionHighlight(ctx, data);

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

    // Creation preview endpoint (set during pointer move)
    private Point _creationEnd;
}
