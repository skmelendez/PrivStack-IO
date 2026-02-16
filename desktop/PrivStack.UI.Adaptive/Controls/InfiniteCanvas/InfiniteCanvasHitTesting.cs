// ============================================================================
// File: InfiniteCanvasHitTesting.cs
// Description: Hit-testing methods for canvas elements, connectors, and
//              resize handles. Used by the interaction layer.
// ============================================================================

using Avalonia;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

/// <summary>
/// Corner handles for element resizing.
/// </summary>
public enum ResizeHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Anchor points for connector attachment on an element's bounding box.
/// </summary>
public enum AnchorPoint
{
    Top,
    Right,
    Bottom,
    Left,
}

public sealed partial class InfiniteCanvasControl
{
    private const double HandleSize = 8;
    private const double HandleHitRadius = 12;
    private const double ConnectorHitDistance = 8;

    /// <summary>
    /// Hit-test elements in reverse Z-order (topmost first).
    /// Returns the first element whose screen rect contains the point.
    /// </summary>
    internal CanvasElement? HitTestElement(Point screenPos, CanvasData data)
    {
        foreach (var el in data.Elements.OrderByDescending(e => e.ZIndex))
        {
            var rect = ElementToScreenRect(el);
            if (rect.Contains(screenPos))
                return el;
        }

        return null;
    }

    /// <summary>
    /// Hit-test a connector using style-aware distance calculation.
    /// Returns the first connector within ConnectorHitDistance pixels.
    /// </summary>
    internal CanvasConnector? HitTestConnector(Point screenPos, CanvasData data)
    {
        foreach (var conn in data.Connectors)
        {
            var source = data.FindElement(conn.SourceId);
            var target = data.FindElement(conn.TargetId);
            if (source == null || target == null) continue;

            var srcAnchor = GetClosestAnchor(source, GetAnchorScreenPoint(target, AnchorPoint.Left));
            var tgtAnchor = GetClosestAnchor(target, GetAnchorScreenPoint(source, AnchorPoint.Right));
            var start = GetAnchorScreenPoint(source, srcAnchor);
            var end = GetAnchorScreenPoint(target, tgtAnchor);

            var dist = conn.Style switch
            {
                ConnectorStyle.Elbow => DistanceToElbowPath(screenPos, start, end, srcAnchor, tgtAnchor),
                ConnectorStyle.Curved => DistanceToCubicBezier(screenPos, start, end, srcAnchor, tgtAnchor),
                _ => DistanceToLineSegment(screenPos, start, end),
            };

            if (dist <= ConnectorHitDistance)
                return conn;
        }

        return null;
    }

    /// <summary>
    /// Hit-test resize handles on selected elements. Returns the element
    /// and which handle was hit, or null.
    /// </summary>
    internal (CanvasElement element, ResizeHandle handle)? HitTestResizeHandle(Point screenPos)
    {
        var data = Data;
        if (data == null) return null;

        foreach (var id in GetSelectedIds())
        {
            var el = data.FindElement(id);
            if (el == null) continue;

            var rect = ElementToScreenRect(el);
            var handles = GetResizeHandlePositions(rect);

            foreach (var (handle, center) in handles)
            {
                var dx = screenPos.X - center.X;
                var dy = screenPos.Y - center.Y;
                if (dx * dx + dy * dy <= HandleHitRadius * HandleHitRadius)
                    return (el, handle);
            }
        }

        return null;
    }

    /// <summary>
    /// Hit-test connector anchor points on an element's bounding box.
    /// Used when creating connectors.
    /// </summary>
    internal (CanvasElement element, AnchorPoint anchor)? HitTestConnectorAnchor(
        Point screenPos, CanvasData data)
    {
        foreach (var el in data.Elements.OrderByDescending(e => e.ZIndex))
        {
            var rect = ElementToScreenRect(el);
            if (!rect.Inflate(HandleHitRadius).Contains(screenPos))
                continue;

            var anchors = new[]
            {
                (AnchorPoint.Top, new Point(rect.X + rect.Width / 2, rect.Y)),
                (AnchorPoint.Right, new Point(rect.Right, rect.Y + rect.Height / 2)),
                (AnchorPoint.Bottom, new Point(rect.X + rect.Width / 2, rect.Bottom)),
                (AnchorPoint.Left, new Point(rect.X, rect.Y + rect.Height / 2)),
            };

            foreach (var (anchor, center) in anchors)
            {
                var dx = screenPos.X - center.X;
                var dy = screenPos.Y - center.Y;
                if (dx * dx + dy * dy <= HandleHitRadius * HandleHitRadius)
                    return (el, anchor);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the screen position of an anchor point on an element.
    /// </summary>
    internal Point GetAnchorScreenPoint(CanvasElement element, AnchorPoint anchor)
    {
        var rect = ElementToScreenRect(element);
        return anchor switch
        {
            AnchorPoint.Top => new Point(rect.X + rect.Width / 2, rect.Y),
            AnchorPoint.Right => new Point(rect.Right, rect.Y + rect.Height / 2),
            AnchorPoint.Bottom => new Point(rect.X + rect.Width / 2, rect.Bottom),
            AnchorPoint.Left => new Point(rect.X, rect.Y + rect.Height / 2),
            _ => new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
        };
    }

    /// <summary>
    /// Returns the closest anchor point on an element to a given screen point.
    /// </summary>
    internal AnchorPoint GetClosestAnchor(CanvasElement element, Point screenPos)
    {
        var rect = ElementToScreenRect(element);
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;

        var dx = screenPos.X - cx;
        var dy = screenPos.Y - cy;

        // Use aspect-ratio-normalized comparison
        var nx = dx / Math.Max(1, rect.Width / 2);
        var ny = dy / Math.Max(1, rect.Height / 2);

        if (Math.Abs(nx) > Math.Abs(ny))
            return nx > 0 ? AnchorPoint.Right : AnchorPoint.Left;
        return ny > 0 ? AnchorPoint.Bottom : AnchorPoint.Top;
    }

    // ================================================================
    // Geometry Helpers
    // ================================================================

    internal static IEnumerable<(ResizeHandle handle, Point center)> GetResizeHandlePositions(Rect rect)
    {
        yield return (ResizeHandle.TopLeft, new Point(rect.X, rect.Y));
        yield return (ResizeHandle.TopRight, new Point(rect.Right, rect.Y));
        yield return (ResizeHandle.BottomLeft, new Point(rect.X, rect.Bottom));
        yield return (ResizeHandle.BottomRight, new Point(rect.Right, rect.Bottom));
    }

    private static double DistanceToLineSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;

        if (lenSq < 0.001)
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;

        return Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
    }

    private double DistanceToElbowPath(Point p, Point start, Point end,
        AnchorPoint srcAnchor, AnchorPoint tgtAnchor)
    {
        var margin = 20 * Zoom;
        var segments = ComputeElbowSegments(start, end, srcAnchor, tgtAnchor, margin);

        var minDist = double.MaxValue;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var dist = DistanceToLineSegment(p, segments[i], segments[i + 1]);
            minDist = Math.Min(minDist, dist);
        }

        return minDist;
    }

    private double DistanceToCubicBezier(Point p, Point start, Point end,
        AnchorPoint srcAnchor, AnchorPoint tgtAnchor)
    {
        var distance = Math.Sqrt(
            (end.X - start.X) * (end.X - start.X) +
            (end.Y - start.Y) * (end.Y - start.Y));
        var offset = Math.Max(50, distance * 0.4) * Zoom;

        var cp1 = GetControlPoint(start, srcAnchor, offset);
        var cp2 = GetControlPoint(end, tgtAnchor, offset);

        // Sample 20 points along the cubic bezier and find minimum distance
        var minDist = double.MaxValue;
        const int samples = 20;
        var prevPt = start;

        for (var i = 1; i <= samples; i++)
        {
            var t = (double)i / samples;
            var u = 1 - t;
            var curPt = new Point(
                u * u * u * start.X + 3 * u * u * t * cp1.X + 3 * u * t * t * cp2.X + t * t * t * end.X,
                u * u * u * start.Y + 3 * u * u * t * cp1.Y + 3 * u * t * t * cp2.Y + t * t * t * end.Y);

            var dist = DistanceToLineSegment(p, prevPt, curPt);
            minDist = Math.Min(minDist, dist);
            prevPt = curPt;
        }

        return minDist;
    }

}
