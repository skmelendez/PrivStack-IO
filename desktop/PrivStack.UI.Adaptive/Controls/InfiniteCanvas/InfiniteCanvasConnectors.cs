// ============================================================================
// File: InfiniteCanvasConnectors.cs
// Description: Connector rendering and creation for the canvas. Supports
//              straight, anchor-aware cubic bezier curves, and orthogonal
//              elbow routing with configurable arrow modes and colors.
// ============================================================================

using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    // Connector creation state
    private bool _isConnecting;
    private string? _connectorSourceId;
    private AnchorPoint _connectorSourceAnchor;
    private Point _pendingConnectorEnd;

    // ================================================================
    // Connector Drawing
    // ================================================================

    internal void DrawConnector(DrawingContext ctx, CanvasConnector connector, CanvasData data)
    {
        var source = data.FindElement(connector.SourceId);
        var target = data.FindElement(connector.TargetId);
        if (source == null || target == null) return;

        var srcAnchor = GetClosestAnchor(source, GetAnchorScreenPoint(target, AnchorPoint.Left));
        var tgtAnchor = GetClosestAnchor(target, GetAnchorScreenPoint(source, AnchorPoint.Right));

        var start = GetAnchorScreenPoint(source, srcAnchor);
        var end = GetAnchorScreenPoint(target, tgtAnchor);

        var connectorBrush = ResolveConnectorBrush(connector);
        var pen = new Pen(connectorBrush, 2 * Zoom);

        switch (connector.Style)
        {
            case ConnectorStyle.Curved:
                DrawCurvedConnector(ctx, start, end, srcAnchor, tgtAnchor, pen);
                break;
            case ConnectorStyle.Elbow:
                DrawElbowConnector(ctx, start, end, srcAnchor, tgtAnchor, pen);
                break;
            default:
                ctx.DrawLine(pen, start, end);
                break;
        }

        // Arrow heads based on ArrowMode
        DrawArrowsForMode(ctx, connector, start, end, srcAnchor, tgtAnchor, connectorBrush);

        // Label
        if (!string.IsNullOrEmpty(connector.Label))
            DrawConnectorLabel(ctx, connector.Label, start, end);
    }

    private IBrush ResolveConnectorBrush(CanvasConnector connector)
    {
        if (!string.IsNullOrEmpty(connector.Color))
        {
            var color = ParseColor(connector.Color, Colors.Gray);
            return new SolidColorBrush(color);
        }

        return GetBrush("ThemeTextMutedBrush", Brushes.Gray);
    }

    // ================================================================
    // Curved Connector — Anchor-aware Cubic Bezier
    // ================================================================

    private void DrawCurvedConnector(DrawingContext ctx, Point start, Point end,
        AnchorPoint srcAnchor, AnchorPoint tgtAnchor, Pen pen)
    {
        var distance = Math.Sqrt(
            (end.X - start.X) * (end.X - start.X) +
            (end.Y - start.Y) * (end.Y - start.Y));
        var offset = Math.Max(50, distance * 0.4) * Zoom;

        var cp1 = GetControlPoint(start, srcAnchor, offset);
        var cp2 = GetControlPoint(end, tgtAnchor, offset);

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(start, false);
            gc.CubicBezierTo(cp1, cp2, end);
            gc.EndFigure(false);
        }

        ctx.DrawGeometry(null, pen, geometry);
    }

    private static Point GetControlPoint(Point anchor, AnchorPoint direction, double offset) =>
        direction switch
        {
            AnchorPoint.Top => new Point(anchor.X, anchor.Y - offset),
            AnchorPoint.Bottom => new Point(anchor.X, anchor.Y + offset),
            AnchorPoint.Left => new Point(anchor.X - offset, anchor.Y),
            AnchorPoint.Right => new Point(anchor.X + offset, anchor.Y),
            _ => new Point(anchor.X + offset, anchor.Y),
        };

    // ================================================================
    // Elbow Connector — Anchor-aware Orthogonal Routing
    // ================================================================

    private void DrawElbowConnector(DrawingContext ctx, Point start, Point end,
        AnchorPoint srcAnchor, AnchorPoint tgtAnchor, Pen pen)
    {
        var margin = 20 * Zoom;
        var segments = ComputeElbowSegments(start, end, srcAnchor, tgtAnchor, margin);

        for (var i = 0; i < segments.Count - 1; i++)
            ctx.DrawLine(pen, segments[i], segments[i + 1]);
    }

    internal static List<Point> ComputeElbowSegments(Point start, Point end,
        AnchorPoint srcAnchor, AnchorPoint tgtAnchor, double margin)
    {
        var exitPt = ExtendFromAnchor(start, srcAnchor, margin);
        var entryPt = ExtendFromAnchor(end, tgtAnchor, margin);

        var segments = new List<Point> { start, exitPt };

        var srcVertical = srcAnchor is AnchorPoint.Top or AnchorPoint.Bottom;
        var tgtVertical = tgtAnchor is AnchorPoint.Top or AnchorPoint.Bottom;

        if (srcVertical == tgtVertical)
        {
            // Same axis: add two midpoints for a Z-shape
            if (srcVertical)
            {
                var midY = (exitPt.Y + entryPt.Y) / 2;
                segments.Add(new Point(exitPt.X, midY));
                segments.Add(new Point(entryPt.X, midY));
            }
            else
            {
                var midX = (exitPt.X + entryPt.X) / 2;
                segments.Add(new Point(midX, exitPt.Y));
                segments.Add(new Point(midX, entryPt.Y));
            }
        }
        else
        {
            // Perpendicular axes: single corner point
            if (srcVertical)
                segments.Add(new Point(exitPt.X, entryPt.Y));
            else
                segments.Add(new Point(entryPt.X, exitPt.Y));
        }

        segments.Add(entryPt);
        segments.Add(end);
        return segments;
    }

    private static Point ExtendFromAnchor(Point pt, AnchorPoint anchor, double margin) =>
        anchor switch
        {
            AnchorPoint.Top => new Point(pt.X, pt.Y - margin),
            AnchorPoint.Bottom => new Point(pt.X, pt.Y + margin),
            AnchorPoint.Left => new Point(pt.X - margin, pt.Y),
            AnchorPoint.Right => new Point(pt.X + margin, pt.Y),
            _ => new Point(pt.X + margin, pt.Y),
        };

    // ================================================================
    // Arrow Head Drawing
    // ================================================================

    private void DrawArrowsForMode(DrawingContext ctx, CanvasConnector connector,
        Point start, Point end, AnchorPoint srcAnchor, AnchorPoint tgtAnchor, IBrush brush)
    {
        var mode = connector.ArrowMode;

        if (mode is ArrowMode.Forward or ArrowMode.Both)
        {
            var dir = GetEndDirection(connector.Style, start, end, srcAnchor, tgtAnchor, atStart: false);
            DrawArrowHeadAtPoint(ctx, end, dir, brush);
        }

        if (mode is ArrowMode.Backward or ArrowMode.Both)
        {
            var dir = GetEndDirection(connector.Style, start, end, srcAnchor, tgtAnchor, atStart: true);
            DrawArrowHeadAtPoint(ctx, start, dir, brush);
        }
    }

    private Point GetEndDirection(ConnectorStyle style, Point start, Point end,
        AnchorPoint srcAnchor, AnchorPoint tgtAnchor, bool atStart)
    {
        if (style == ConnectorStyle.Elbow)
        {
            var margin = 20 * Zoom;
            var segments = ComputeElbowSegments(start, end, srcAnchor, tgtAnchor, margin);
            if (atStart)
            {
                // Direction pointing INTO start (from second point toward first)
                return new Point(segments[0].X - segments[1].X, segments[0].Y - segments[1].Y);
            }

            var last = segments.Count - 1;
            return new Point(segments[last].X - segments[last - 1].X, segments[last].Y - segments[last - 1].Y);
        }

        if (style == ConnectorStyle.Curved)
        {
            // Tangent at endpoints of cubic bezier approximated by control point direction
            var distance = Math.Sqrt(
                (end.X - start.X) * (end.X - start.X) +
                (end.Y - start.Y) * (end.Y - start.Y));
            var offset = Math.Max(50, distance * 0.4) * Zoom;

            if (atStart)
            {
                var cp1 = GetControlPoint(start, srcAnchor, offset);
                return new Point(start.X - cp1.X, start.Y - cp1.Y);
            }

            var cp2 = GetControlPoint(end, tgtAnchor, offset);
            return new Point(end.X - cp2.X, end.Y - cp2.Y);
        }

        // Straight
        return atStart
            ? new Point(start.X - end.X, start.Y - end.Y)
            : new Point(end.X - start.X, end.Y - start.Y);
    }

    private void DrawArrowHeadAtPoint(DrawingContext ctx, Point tip, Point direction, IBrush brush)
    {
        var len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (len < 0.001) return;

        var nx = direction.X / len;
        var ny = direction.Y / len;

        var arrowSize = 10 * Zoom;
        var arrowAngle = Math.PI / 6;

        var cosA = Math.Cos(arrowAngle);
        var sinA = Math.Sin(arrowAngle);

        var p1 = new Point(
            tip.X - arrowSize * (nx * cosA - ny * sinA),
            tip.Y - arrowSize * (ny * cosA + nx * sinA));
        var p2 = new Point(
            tip.X - arrowSize * (nx * cosA + ny * sinA),
            tip.Y - arrowSize * (ny * cosA - nx * sinA));

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(tip, true);
            gc.LineTo(p1);
            gc.LineTo(p2);
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(brush, null, geometry);
    }

    // ================================================================
    // Connector Label
    // ================================================================

    private void DrawConnectorLabel(DrawingContext ctx, string label, Point start, Point end)
    {
        var midPoint = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var fontSize = Math.Max(8, 11 * Zoom);
        var textBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
        var ft = new FormattedText(
            label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);

        var bgRect = new Rect(
            midPoint.X - ft.Width / 2 - 4,
            midPoint.Y - ft.Height / 2 - 2,
            ft.Width + 8, ft.Height + 4);
        var bgBrush = GetBrush("ThemeBackgroundBrush", Brushes.Black);
        ctx.DrawRectangle(bgBrush, null, bgRect, 3, 3);

        ctx.DrawText(ft, new Point(midPoint.X - ft.Width / 2, midPoint.Y - ft.Height / 2));
    }

    // ================================================================
    // Pending Connector (creation mode)
    // ================================================================

    internal void DrawPendingConnector(DrawingContext ctx)
    {
        if (!_isConnecting || _connectorSourceId == null) return;

        var data = Data;
        var source = data?.FindElement(_connectorSourceId);
        if (source == null) return;

        var start = GetAnchorScreenPoint(source, _connectorSourceAnchor);
        var pen = new Pen(Brushes.CornflowerBlue, 2 * Zoom)
        {
            DashStyle = DashStyle.Dash,
        };

        ctx.DrawLine(pen, start, _pendingConnectorEnd);
        DrawArrowHeadAtPoint(ctx, _pendingConnectorEnd,
            new Point(_pendingConnectorEnd.X - start.X, _pendingConnectorEnd.Y - start.Y),
            Brushes.CornflowerBlue);
    }

    // ================================================================
    // Connector Creation Handlers
    // ================================================================

    internal void HandleConnectorPress(Point pos, CanvasData data)
    {
        var anchorHit = HitTestConnectorAnchor(pos, data);
        if (anchorHit == null)
        {
            var el = HitTestElement(pos, data);
            if (el == null) return;
            _connectorSourceId = el.Id;
            _connectorSourceAnchor = GetClosestAnchor(el, pos);
        }
        else
        {
            _connectorSourceId = anchorHit.Value.element.Id;
            _connectorSourceAnchor = anchorHit.Value.anchor;
        }

        _isConnecting = true;
        _pendingConnectorEnd = pos;
    }

    internal void FinishConnectorCreation(Point pos)
    {
        if (!_isConnecting || _connectorSourceId == null)
        {
            CancelConnectorCreation();
            return;
        }

        var data = Data;
        if (data == null)
        {
            CancelConnectorCreation();
            return;
        }

        var targetHit = HitTestElement(pos, data);
        if (targetHit == null || targetHit.Id == _connectorSourceId)
        {
            CancelConnectorCreation();
            return;
        }

        var connector = new CanvasConnector
        {
            Id = Guid.NewGuid().ToString(),
            SourceId = _connectorSourceId,
            TargetId = targetHit.Id,
            Style = DefaultConnectorStyle,
            ArrowMode = DefaultArrowMode,
            Color = DefaultConnectorColor,
        };

        data.Connectors.Add(connector);
        CancelConnectorCreation();
        NotifyDataChanged();
        InvalidateVisual();
    }

    internal void CancelConnectorCreation()
    {
        _isConnecting = false;
        _connectorSourceId = null;
        InvalidateVisual();
    }
}
