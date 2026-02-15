// ============================================================================
// File: InfiniteCanvasConnectors.cs
// Description: Connector rendering and creation for the canvas. Supports
//              straight, curved (quadratic bezier), and elbow (orthogonal)
//              connector styles with arrow heads.
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

        var pen = new Pen(GetBrush("ThemeTextMutedBrush", Brushes.Gray), 2 * Zoom);

        switch (connector.Style)
        {
            case ConnectorStyle.Curved:
                DrawCurvedConnector(ctx, start, end, pen);
                break;
            case ConnectorStyle.Elbow:
                DrawElbowConnector(ctx, start, end, pen);
                break;
            default:
                ctx.DrawLine(pen, start, end);
                break;
        }

        // Arrow head at target
        DrawArrowHead(ctx, start, end, pen.Brush);

        // Label
        if (!string.IsNullOrEmpty(connector.Label))
            DrawConnectorLabel(ctx, connector.Label, start, end);
    }

    private void DrawCurvedConnector(DrawingContext ctx, Point start, Point end, Pen pen)
    {
        var midX = (start.X + end.X) / 2;
        var controlOffset = Math.Abs(end.X - start.X) * 0.3;

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(start, false);
            gc.QuadraticBezierTo(
                new Point(midX, start.Y - controlOffset),
                end);
            gc.EndFigure(false);
        }

        ctx.DrawGeometry(null, pen, geometry);
    }

    private static void DrawElbowConnector(DrawingContext ctx, Point start, Point end, Pen pen)
    {
        var midX = (start.X + end.X) / 2;

        // Three-segment orthogonal path
        ctx.DrawLine(pen, start, new Point(midX, start.Y));
        ctx.DrawLine(pen, new Point(midX, start.Y), new Point(midX, end.Y));
        ctx.DrawLine(pen, new Point(midX, end.Y), end);
    }

    private void DrawArrowHead(DrawingContext ctx, Point from, Point to, IBrush? brush)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        var nx = dx / len;
        var ny = dy / len;

        var arrowSize = 10 * Zoom;
        var arrowAngle = Math.PI / 6;

        var p1 = new Point(
            to.X - arrowSize * (nx * Math.Cos(arrowAngle) - ny * Math.Sin(arrowAngle)),
            to.Y - arrowSize * (ny * Math.Cos(arrowAngle) + nx * Math.Sin(arrowAngle)));
        var p2 = new Point(
            to.X - arrowSize * (nx * Math.Cos(arrowAngle) + ny * Math.Sin(arrowAngle)),
            to.Y - arrowSize * (ny * Math.Cos(arrowAngle) - nx * Math.Sin(arrowAngle)));

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(to, true);
            gc.LineTo(p1);
            gc.LineTo(p2);
            gc.EndFigure(true);
        }

        ctx.DrawGeometry(brush, null, geometry);
    }

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

        // Background for readability
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
        DrawArrowHead(ctx, start, _pendingConnectorEnd, Brushes.CornflowerBlue);
    }

    // ================================================================
    // Connector Creation Handlers
    // ================================================================

    internal void HandleConnectorPress(Point pos, CanvasData data)
    {
        var anchorHit = HitTestConnectorAnchor(pos, data);
        if (anchorHit == null)
        {
            // Try hitting element center
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
            Style = ConnectorStyle.Straight,
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
