// ============================================================================
// File: InfiniteCanvasFreehand.cs
// Description: Freehand stroke drawing and rendering for the canvas. Manages
//              active stroke state and applies Douglas-Peucker point reduction
//              on stroke completion.
// ============================================================================

using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private readonly List<StrokePoint> _currentStrokePoints = [];
    private bool _isDrawingStroke;

    // ================================================================
    // Stroke Lifecycle
    // ================================================================

    internal void BeginStroke(double worldX, double worldY)
    {
        _currentStrokePoints.Clear();
        _currentStrokePoints.Add(new StrokePoint { X = worldX, Y = worldY });
        _isDrawingStroke = true;
    }

    internal void ContinueStroke(double worldX, double worldY)
    {
        if (!_isDrawingStroke) return;
        _currentStrokePoints.Add(new StrokePoint { X = worldX, Y = worldY });
        InvalidateVisual();
    }

    internal void EndStroke(double worldX, double worldY)
    {
        if (!_isDrawingStroke) return;
        _isDrawingStroke = false;

        _currentStrokePoints.Add(new StrokePoint { X = worldX, Y = worldY });

        if (_currentStrokePoints.Count < 2) return;

        // Simplify with Douglas-Peucker
        var simplified = DouglasPeucker(_currentStrokePoints, 1.5);

        var data = Data;
        if (data == null) return;

        // Compute bounding box for the element
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var pt in simplified)
        {
            minX = Math.Min(minX, pt.X);
            minY = Math.Min(minY, pt.Y);
            maxX = Math.Max(maxX, pt.X);
            maxY = Math.Max(maxY, pt.Y);
        }

        var element = new CanvasElement
        {
            Id = Guid.NewGuid().ToString(),
            Type = CanvasElementType.Freehand,
            X = minX,
            Y = minY,
            Width = Math.Max(1, maxX - minX),
            Height = Math.Max(1, maxY - minY),
            StrokePoints = simplified,
            StrokeWidth = 2.0,
            ZIndex = data.NextZIndex(),
        };

        data.Elements.Add(element);
        _currentStrokePoints.Clear();
        NotifyDataChanged();
        InvalidateVisual();
    }

    // ================================================================
    // Stroke Rendering
    // ================================================================

    internal void DrawFreehandStroke(DrawingContext ctx, CanvasElement element)
    {
        if (element.StrokePoints.Count < 2) return;

        var strokeColor = ParseColor(element.StrokeColor, Colors.White);
        var pen = new Pen(
            new SolidColorBrush(strokeColor),
            element.StrokeWidth * Zoom,
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        for (var i = 1; i < element.StrokePoints.Count; i++)
        {
            var p0 = element.StrokePoints[i - 1];
            var p1 = element.StrokePoints[i];
            var s0 = WorldToScreen(p0.X, p0.Y);
            var s1 = WorldToScreen(p1.X, p1.Y);
            ctx.DrawLine(pen, s0, s1);
        }
    }

    internal void DrawActiveStroke(DrawingContext ctx)
    {
        if (!_isDrawingStroke || _currentStrokePoints.Count < 2) return;

        var textBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var pen = new Pen(textBrush, 2 * Zoom,
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        for (var i = 1; i < _currentStrokePoints.Count; i++)
        {
            var p0 = _currentStrokePoints[i - 1];
            var p1 = _currentStrokePoints[i];
            var s0 = WorldToScreen(p0.X, p0.Y);
            var s1 = WorldToScreen(p1.X, p1.Y);
            ctx.DrawLine(pen, s0, s1);
        }
    }

    // ================================================================
    // Douglas-Peucker Simplification
    // ================================================================

    private static List<StrokePoint> DouglasPeucker(List<StrokePoint> points, double epsilon)
    {
        if (points.Count < 3)
            return new List<StrokePoint>(points);

        var maxDist = 0.0;
        var maxIndex = 0;

        var first = points[0];
        var last = points[^1];

        for (var i = 1; i < points.Count - 1; i++)
        {
            var dist = PerpendicularDistance(points[i], first, last);
            if (dist > maxDist)
            {
                maxDist = dist;
                maxIndex = i;
            }
        }

        if (maxDist > epsilon)
        {
            var left = DouglasPeucker(points.GetRange(0, maxIndex + 1), epsilon);
            var right = DouglasPeucker(points.GetRange(maxIndex, points.Count - maxIndex), epsilon);

            var result = new List<StrokePoint>(left);
            result.AddRange(right.Skip(1));
            return result;
        }

        return [first, last];
    }

    private static double PerpendicularDistance(StrokePoint point, StrokePoint lineStart, StrokePoint lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        var lenSq = dx * dx + dy * dy;

        if (lenSq < 0.001)
            return Math.Sqrt(
                (point.X - lineStart.X) * (point.X - lineStart.X) +
                (point.Y - lineStart.Y) * (point.Y - lineStart.Y));

        var t = ((point.X - lineStart.X) * dx + (point.Y - lineStart.Y) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);

        var projX = lineStart.X + t * dx;
        var projY = lineStart.Y + t * dy;

        return Math.Sqrt(
            (point.X - projX) * (point.X - projX) +
            (point.Y - projY) * (point.Y - projY));
    }
}
