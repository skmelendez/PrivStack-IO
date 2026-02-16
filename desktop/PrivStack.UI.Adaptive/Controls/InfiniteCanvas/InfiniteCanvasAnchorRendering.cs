// ============================================================================
// File: InfiniteCanvasAnchorRendering.cs
// Description: Renders visible anchor point indicators on canvas elements
//              when the Connector tool is active or a connection is in progress.
// ============================================================================

using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private (string elementId, AnchorPoint anchor)? _hoveredAnchor;

    internal void DrawAnchorPoints(DrawingContext ctx, CanvasData data, Rect viewport)
    {
        var normalRadius = 5.0 * Zoom;
        var hoverRadius = 7.0 * Zoom;
        var normalBrush = new SolidColorBrush(Colors.CornflowerBlue, 0.6);
        var hoverBrush = new SolidColorBrush(Colors.CornflowerBlue, 0.9);
        var outlinePen = new Pen(Brushes.White, 1.5 * Zoom);

        foreach (var element in data.Elements)
        {
            if (!IsElementVisible(element, viewport))
                continue;

            var anchors = new[]
            {
                AnchorPoint.Top,
                AnchorPoint.Right,
                AnchorPoint.Bottom,
                AnchorPoint.Left,
            };

            foreach (var anchor in anchors)
            {
                var pt = GetAnchorScreenPoint(element, anchor);
                var isHovered = _hoveredAnchor.HasValue
                    && _hoveredAnchor.Value.elementId == element.Id
                    && _hoveredAnchor.Value.anchor == anchor;

                var radius = isHovered ? hoverRadius : normalRadius;
                var brush = isHovered ? hoverBrush : normalBrush;

                ctx.DrawEllipse(brush, outlinePen, pt, radius, radius);
            }
        }
    }

    internal void UpdateHoveredAnchor(Point screenPos)
    {
        var data = Data;
        if (data == null)
        {
            _hoveredAnchor = null;
            return;
        }

        var hit = HitTestConnectorAnchor(screenPos, data);
        var newHover = hit.HasValue
            ? (hit.Value.element.Id, hit.Value.anchor)
            : ((string, AnchorPoint)?)null;

        if (newHover != _hoveredAnchor)
        {
            _hoveredAnchor = newHover;
            InvalidateVisual();
        }
    }
}
