// ============================================================================
// File: InfiniteCanvasShapeRendering.cs
// Description: Diagram-specific shape rendering for the InfiniteCanvasControl.
//              Draws Diamond, Parallelogram, Cylinder, Hexagon, RoundedRect,
//              and Triangle shapes using StreamGeometry for crisp vectors.
// ============================================================================

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private static readonly Color DefaultDiamondColor = Color.Parse("#FFDCB4");
    private static readonly Color DefaultParallelogramColor = Color.Parse("#B4D2FF");
    private static readonly Color DefaultCylinderColor = Color.Parse("#C8DCF0");
    private static readonly Color DefaultHexagonColor = Color.Parse("#DCC8F0");
    private static readonly Color DefaultRoundedRectColor = Color.Parse("#B4E6C8");
    private static readonly Color DefaultTriangleColor = Color.Parse("#FFC8C8");

    // ================================================================
    // Diamond (decision shape)
    // ================================================================

    private void DrawDiamondElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultDiamondColor);
        var brush = new SolidColorBrush(color, 0.7);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);

        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(new Point(cx, rect.Y), true);          // top
            gc.LineTo(new Point(rect.Right, cy));                   // right
            gc.LineTo(new Point(cx, rect.Bottom));                  // bottom
            gc.LineTo(new Point(rect.X, cy));                      // left
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(brush, pen, geometry);
        DrawElementLabel(ctx, element, rect);
    }

    // ================================================================
    // Parallelogram (I/O shape)
    // ================================================================

    private void DrawParallelogramElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultParallelogramColor);
        var brush = new SolidColorBrush(color, 0.7);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);

        var inset = rect.Width * 0.2;

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(new Point(rect.X + inset, rect.Y), true);       // top-left (inset)
            gc.LineTo(new Point(rect.Right, rect.Y));                       // top-right
            gc.LineTo(new Point(rect.Right - inset, rect.Bottom));          // bottom-right (inset)
            gc.LineTo(new Point(rect.X, rect.Bottom));                      // bottom-left
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(brush, pen, geometry);
        DrawElementLabel(ctx, element, rect);
    }

    // ================================================================
    // Cylinder (database shape)
    // ================================================================

    private void DrawCylinderElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultCylinderColor);
        var brush = new SolidColorBrush(color, 0.7);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);

        var capHeight = rect.Height * 0.15;
        var bodyTop = rect.Y + capHeight;
        var bodyBottom = rect.Bottom - capHeight;

        // Body rectangle
        ctx.DrawRectangle(brush, null,
            new Rect(rect.X, bodyTop, rect.Width, bodyBottom - bodyTop));

        // Bottom cap (full ellipse)
        ctx.DrawEllipse(brush, pen,
            new Point(rect.X + rect.Width / 2, bodyBottom),
            rect.Width / 2, capHeight);

        // Side lines connecting top and bottom caps
        ctx.DrawLine(pen, new Point(rect.X, bodyTop), new Point(rect.X, bodyBottom));
        ctx.DrawLine(pen, new Point(rect.Right, bodyTop), new Point(rect.Right, bodyBottom));

        // Top cap (full ellipse, drawn on top)
        ctx.DrawEllipse(brush, pen,
            new Point(rect.X + rect.Width / 2, bodyTop),
            rect.Width / 2, capHeight);

        DrawElementLabel(ctx, element, rect);
    }

    // ================================================================
    // Hexagon (preparation shape)
    // ================================================================

    private void DrawHexagonElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultHexagonColor);
        var brush = new SolidColorBrush(color, 0.7);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);

        var inset = rect.Width * 0.25;
        var cy = rect.Y + rect.Height / 2;

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(new Point(rect.X + inset, rect.Y), true);       // top-left
            gc.LineTo(new Point(rect.Right - inset, rect.Y));               // top-right
            gc.LineTo(new Point(rect.Right, cy));                            // right
            gc.LineTo(new Point(rect.Right - inset, rect.Bottom));           // bottom-right
            gc.LineTo(new Point(rect.X + inset, rect.Bottom));               // bottom-left
            gc.LineTo(new Point(rect.X, cy));                                // left
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(brush, pen, geometry);
        DrawElementLabel(ctx, element, rect);
    }

    // ================================================================
    // RoundedRect (terminal/pill shape)
    // ================================================================

    private void DrawRoundedRectElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultRoundedRectColor);
        var brush = new SolidColorBrush(color, 0.7);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);

        var radius = Math.Min(rect.Height / 2, rect.Width / 4);
        ctx.DrawRectangle(brush, pen, rect, radius, radius);

        DrawElementLabel(ctx, element, rect);
    }

    // ================================================================
    // Triangle (pointing up)
    // ================================================================

    private void DrawTriangleElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultTriangleColor);
        var brush = new SolidColorBrush(color, 0.7);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);

        var cx = rect.X + rect.Width / 2;

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(new Point(cx, rect.Y), true);            // top center
            gc.LineTo(new Point(rect.Right, rect.Bottom));            // bottom right
            gc.LineTo(new Point(rect.X, rect.Bottom));                // bottom left
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(brush, pen, geometry);
        DrawElementLabel(ctx, element, rect);
    }
}
