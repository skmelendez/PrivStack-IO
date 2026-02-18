// ============================================================================
// File: InfiniteCanvasSelection.cs
// Description: Selection state management and rendering for the canvas.
//              Handles single/multi-select, marquee selection, and
//              resize handle rendering on selected elements.
// ============================================================================

using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private readonly HashSet<string> _selectedElementIds = [];
    private string? _selectedConnectorId;
    private Rect? _selectionMarquee;
    private Point _marqueeStart;
    private bool _isMarqueeActive;

    // ================================================================
    // Selection API
    // ================================================================

    internal void Select(string elementId)
    {
        _selectedElementIds.Add(elementId);
        InvalidateVisual();
    }

    internal void Deselect(string elementId)
    {
        _selectedElementIds.Remove(elementId);
        InvalidateVisual();
    }

    internal void ToggleSelection(string elementId)
    {
        if (!_selectedElementIds.Remove(elementId))
            _selectedElementIds.Add(elementId);
        InvalidateVisual();
    }

    internal void ClearSelection()
    {
        var hadSelection = _selectedElementIds.Count > 0 || _selectedConnectorId != null;
        _selectedElementIds.Clear();
        _selectedConnectorId = null;
        if (hadSelection)
            InvalidateVisual();
    }

    internal void SelectConnector(string connectorId)
    {
        _selectedElementIds.Clear();
        _selectedConnectorId = connectorId;
        InvalidateVisual();

        var connector = Data?.FindConnector(connectorId);
        if (connector != null)
            ConnectorSelected?.Invoke(connector);
    }

    internal void ClearConnectorSelection()
    {
        if (_selectedConnectorId == null) return;
        _selectedConnectorId = null;
        InvalidateVisual();
        SelectionCleared?.Invoke();
    }

    internal void SelectAll()
    {
        var data = Data;
        if (data == null) return;

        foreach (var el in data.Elements)
            _selectedElementIds.Add(el.Id);
        InvalidateVisual();
    }

    internal bool IsSelected(string elementId) =>
        _selectedElementIds.Contains(elementId);

    internal IEnumerable<string> GetSelectedIds() =>
        _selectedElementIds;

    internal int SelectedCount => _selectedElementIds.Count;

    // ================================================================
    // Marquee
    // ================================================================

    internal void BeginMarquee(Point screenPos)
    {
        _marqueeStart = screenPos;
        _isMarqueeActive = true;
        _selectionMarquee = null;
    }

    internal void UpdateMarquee(Point screenPos)
    {
        if (!_isMarqueeActive) return;

        var x = Math.Min(_marqueeStart.X, screenPos.X);
        var y = Math.Min(_marqueeStart.Y, screenPos.Y);
        var w = Math.Abs(screenPos.X - _marqueeStart.X);
        var h = Math.Abs(screenPos.Y - _marqueeStart.Y);

        _selectionMarquee = new Rect(x, y, w, h);

        // Select elements within marquee
        var data = Data;
        if (data == null) return;

        _selectedElementIds.Clear();
        foreach (var el in data.Elements)
        {
            var rect = ElementToScreenRect(el);
            if (_selectionMarquee.Value.Intersects(rect))
                _selectedElementIds.Add(el.Id);
        }

        InvalidateVisual();
    }

    internal void EndMarquee()
    {
        _isMarqueeActive = false;
        _selectionMarquee = null;
        InvalidateVisual();
    }

    // ================================================================
    // Selection Rendering
    // ================================================================

    internal void DrawSelectionOverlays(DrawingContext ctx, CanvasData data)
    {
        var selectionPen = new Pen(GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue), 2)
        {
            DashStyle = DashStyle.Dash,
        };

        var handleBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var handlePen = new Pen(GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue), 1.5);

        foreach (var id in _selectedElementIds)
        {
            var el = data.FindElement(id);
            if (el == null) continue;

            var rect = ElementToScreenRect(el);

            // Selection border
            ctx.DrawRectangle(null, selectionPen, rect.Inflate(2));

            if (IsReadOnly) continue;

            // Resize handles at corners
            foreach (var (_, center) in GetResizeHandlePositions(rect))
            {
                var handleRect = new Rect(
                    center.X - HandleSize / 2,
                    center.Y - HandleSize / 2,
                    HandleSize, HandleSize);
                ctx.DrawRectangle(handleBrush, handlePen, handleRect, 2, 2);
            }
        }
    }

    internal void DrawMarquee(DrawingContext ctx)
    {
        if (_selectionMarquee == null) return;

        var primaryBrush = GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue);
        var primaryColor = primaryBrush is ISolidColorBrush scb ? scb.Color : Colors.CornflowerBlue;
        var brush = new SolidColorBrush(primaryColor, 0.15);
        var pen = new Pen(primaryBrush, 1) { DashStyle = DashStyle.Dash };
        ctx.DrawRectangle(brush, pen, _selectionMarquee.Value);
    }

    internal void DrawConnectorSelectionHighlight(DrawingContext ctx, CanvasData data)
    {
        if (_selectedConnectorId == null) return;

        var connector = data.FindConnector(_selectedConnectorId);
        if (connector == null) return;

        var source = data.FindElement(connector.SourceId);
        var target = data.FindElement(connector.TargetId);
        if (source == null || target == null) return;

        var srcAnchor = GetClosestAnchor(source, GetAnchorScreenPoint(target, AnchorPoint.Left));
        var tgtAnchor = GetClosestAnchor(target, GetAnchorScreenPoint(source, AnchorPoint.Right));
        var start = GetAnchorScreenPoint(source, srcAnchor);
        var end = GetAnchorScreenPoint(target, tgtAnchor);

        var highlightPen = new Pen(GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue), 4 * Zoom) { DashStyle = DashStyle.Dash };

        switch (connector.Style)
        {
            case ConnectorStyle.Curved:
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

                ctx.DrawGeometry(null, highlightPen, geometry);
                break;
            }
            case ConnectorStyle.Elbow:
            {
                var margin = 20 * Zoom;
                var segments = ComputeElbowSegments(start, end, srcAnchor, tgtAnchor, margin);
                for (var i = 0; i < segments.Count - 1; i++)
                    ctx.DrawLine(highlightPen, segments[i], segments[i + 1]);
                break;
            }
            default:
                ctx.DrawLine(highlightPen, start, end);
                break;
        }
    }

}
