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
    /// Hit-test a connector line segment. Returns the first connector
    /// within ConnectorHitDistance pixels of the screen point.
    /// </summary>
    internal CanvasConnector? HitTestConnector(Point screenPos, CanvasData data)
    {
        foreach (var conn in data.Connectors)
        {
            var source = data.FindElement(conn.SourceId);
            var target = data.FindElement(conn.TargetId);
            if (source == null || target == null) continue;

            var srcCenter = GetAnchorScreenPoint(source, AnchorPoint.Right);
            var tgtCenter = GetAnchorScreenPoint(target, AnchorPoint.Left);

            var dist = DistanceToLineSegment(screenPos, srcCenter, tgtCenter);
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
}
