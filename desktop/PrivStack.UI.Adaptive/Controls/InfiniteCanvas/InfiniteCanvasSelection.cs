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
        if (_selectedElementIds.Count == 0) return;
        _selectedElementIds.Clear();
        InvalidateVisual();
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
        var selectionPen = new Pen(Brushes.CornflowerBlue, 2)
        {
            DashStyle = DashStyle.Dash,
        };

        var handleBrush = Brushes.White;
        var handlePen = new Pen(Brushes.CornflowerBlue, 1.5);

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

        var brush = new SolidColorBrush(Colors.CornflowerBlue, 0.15);
        var pen = new Pen(Brushes.CornflowerBlue, 1) { DashStyle = DashStyle.Dash };
        ctx.DrawRectangle(brush, pen, _selectionMarquee.Value);
    }
}
