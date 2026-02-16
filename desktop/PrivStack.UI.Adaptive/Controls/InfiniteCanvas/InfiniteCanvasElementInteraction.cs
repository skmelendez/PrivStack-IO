// ============================================================================
// File: InfiniteCanvasElementInteraction.cs
// Description: Tool-specific interaction handlers for selection, drag, resize,
//              shape creation, deletion, and cursor updates.
// ============================================================================

using Avalonia;
using Avalonia.Input;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    // ================================================================
    // Select Tool Handlers
    // ================================================================

    private void HandleSelectPress(Point pos, CanvasData data, PointerPressedEventArgs e)
    {
        // Check resize handles first
        var resizeHit = HitTestResizeHandle(pos);
        if (resizeHit != null)
        {
            var (element, handle) = resizeHit.Value;
            _resizingElementId = element.Id;
            _activeResizeHandle = handle;
            _resizeOriginalBounds = new Rect(element.X, element.Y, element.Width, element.Height);
            _dragStartScreen = pos;
            return;
        }

        var hitElement = HitTestElement(pos, data);
        if (hitElement != null)
        {
            // Clear connector selection when selecting an element
            ClearConnectorSelection();

            // Double-click entity ref navigates
            if (e.ClickCount == 2 && hitElement.Type == CanvasElementType.EntityReference
                && !string.IsNullOrEmpty(hitElement.EntityType)
                && !string.IsNullOrEmpty(hitElement.EntityId))
            {
                EntityReferenceClicked?.Invoke(hitElement.EntityType, hitElement.EntityId);
                return;
            }

            // Double-click page ref navigates
            if (e.ClickCount == 2 && hitElement.Type == CanvasElementType.PageReference
                && !string.IsNullOrEmpty(hitElement.LinkedPageId))
            {
                PageReferenceClicked?.Invoke(hitElement.LinkedPageId);
                return;
            }

            // Shift-click toggles selection
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                ToggleSelection(hitElement.Id);
            }
            else if (!IsSelected(hitElement.Id))
            {
                ClearSelection();
                Select(hitElement.Id);
            }

            ElementSelected?.Invoke(hitElement);

            // Start drag
            _draggedElementId = hitElement.Id;
            _wasActualDrag = false;
            _dragStartScreen = pos;
            var screenPos = WorldToScreen(hitElement.X, hitElement.Y);
            _dragOffsetX = pos.X - screenPos.X;
            _dragOffsetY = pos.Y - screenPos.Y;
        }
        else
        {
            // Try selecting a connector
            var hitConnector = HitTestConnector(pos, data);
            if (hitConnector != null)
            {
                ClearSelection();
                SelectConnector(hitConnector.Id);
                return;
            }

            // Click on empty space
            ClearSelection();
            ClearConnectorSelection();
            SelectionCleared?.Invoke();

            // Start marquee selection
            BeginMarquee(pos);
        }
    }

    // ================================================================
    // Drag Handlers
    // ================================================================

    private void HandleDragMove(Point pos)
    {
        var dx = pos.X - _dragStartScreen.X;
        var dy = pos.Y - _dragStartScreen.Y;

        if (!_wasActualDrag && dx * dx + dy * dy >= 9)
            _wasActualDrag = true;

        if (!_wasActualDrag) return;

        var data = Data;
        if (data == null) return;

        var worldDx = (pos.X - _dragStartScreen.X) / Zoom;
        var worldDy = (pos.Y - _dragStartScreen.Y) / Zoom;
        _dragStartScreen = pos;

        foreach (var id in GetSelectedIds())
        {
            var el = data.FindElement(id);
            if (el == null) continue;
            el.X += worldDx;
            el.Y += worldDy;

            if (el.Type == CanvasElementType.GroupFrame)
                MoveGroupChildren(data, el.Id, worldDx, worldDy);
        }

        InvalidateVisual();
    }

    private void FinishDrag()
    {
        _draggedElementId = null;
        if (_wasActualDrag)
            NotifyDataChanged();
    }

    // ================================================================
    // Resize Handlers
    // ================================================================

    private void HandleResizeMove(Point pos)
    {
        var data = Data;
        var element = data?.FindElement(_resizingElementId!);
        if (element == null || _activeResizeHandle == null) return;

        var (wx, wy) = ScreenToWorld(pos);
        ApplyResize(element, _activeResizeHandle.Value, wx, wy, _resizeOriginalBounds);
        InvalidateVisual();
    }

    private static void ApplyResize(CanvasElement el, ResizeHandle handle, double wx, double wy, Rect orig)
    {
        const double minSize = 20;

        switch (handle)
        {
            case ResizeHandle.BottomRight:
                el.Width = Math.Max(minSize, wx - orig.X);
                el.Height = Math.Max(minSize, wy - orig.Y);
                break;
            case ResizeHandle.BottomLeft:
                el.X = Math.Min(wx, orig.Right - minSize);
                el.Width = Math.Max(minSize, orig.Right - el.X);
                el.Height = Math.Max(minSize, wy - orig.Y);
                break;
            case ResizeHandle.TopRight:
                el.Y = Math.Min(wy, orig.Bottom - minSize);
                el.Width = Math.Max(minSize, wx - orig.X);
                el.Height = Math.Max(minSize, orig.Bottom - el.Y);
                break;
            case ResizeHandle.TopLeft:
                el.X = Math.Min(wx, orig.Right - minSize);
                el.Y = Math.Min(wy, orig.Bottom - minSize);
                el.Width = Math.Max(minSize, orig.Right - el.X);
                el.Height = Math.Max(minSize, orig.Bottom - el.Y);
                break;
        }
    }

    // ================================================================
    // Shape Creation Handlers
    // ================================================================

    private void HandleCreationPress(Point pos)
    {
        _creationStart = pos;
        _creationEnd = pos;
        _isCreating = true;
    }

    private void FinishCreation(Point endPos)
    {
        _isCreating = false;

        var (startWx, startWy) = ScreenToWorld(_creationStart);
        var (endWx, endWy) = ScreenToWorld(endPos);

        var x = Math.Min(startWx, endWx);
        var y = Math.Min(startWy, endWy);
        var w = Math.Abs(endWx - startWx);
        var h = Math.Abs(endWy - startWy);

        // Shape-specific click-create defaults
        if (w < 10 || h < 10)
        {
            var (defaultW, defaultH) = GetDefaultSizeForTool(ToolMode);
            if (w < 10) w = defaultW;
            if (h < 10) h = defaultH;
        }

        var data = Data;
        if (data == null) return;

        var element = new CanvasElement
        {
            Id = Guid.NewGuid().ToString(),
            Type = ToolModeToElementType(ToolMode),
            X = x, Y = y,
            Width = w, Height = h,
            ZIndex = data.NextZIndex(),
        };

        data.Elements.Add(element);
        ClearSelection();
        Select(element.Id);
        NotifyDataChanged();
        InvalidateVisual();
    }

    private void CancelCreation()
    {
        _isCreating = false;
        InvalidateVisual();
    }

    private static (double w, double h) GetDefaultSizeForTool(CanvasToolMode mode) => mode switch
    {
        CanvasToolMode.Text => (200, 40),
        CanvasToolMode.Diamond => (140, 120),
        CanvasToolMode.Cylinder => (120, 160),
        CanvasToolMode.Triangle => (140, 120),
        CanvasToolMode.Parallelogram => (180, 100),
        CanvasToolMode.Hexagon => (160, 120),
        CanvasToolMode.RoundedRect => (180, 80),
        _ => (200, 120),
    };

    private static string ToolModeToElementType(CanvasToolMode mode) => mode switch
    {
        CanvasToolMode.NoteCard => CanvasElementType.NoteCard,
        CanvasToolMode.Text => CanvasElementType.Text,
        CanvasToolMode.Rect => CanvasElementType.Rect,
        CanvasToolMode.Ellipse => CanvasElementType.Ellipse,
        CanvasToolMode.GroupFrame => CanvasElementType.GroupFrame,
        CanvasToolMode.Image => CanvasElementType.Image,
        CanvasToolMode.Diamond => CanvasElementType.Diamond,
        CanvasToolMode.Parallelogram => CanvasElementType.Parallelogram,
        CanvasToolMode.Cylinder => CanvasElementType.Cylinder,
        CanvasToolMode.Hexagon => CanvasElementType.Hexagon,
        CanvasToolMode.RoundedRect => CanvasElementType.RoundedRect,
        CanvasToolMode.Triangle => CanvasElementType.Triangle,
        _ => CanvasElementType.NoteCard,
    };

    // ================================================================
    // Deletion
    // ================================================================

    private void DeleteSelectedElements()
    {
        var data = Data;
        if (data == null) return;

        // Delete selected connector
        if (_selectedConnectorId != null)
        {
            data.Connectors.RemoveAll(c => c.Id == _selectedConnectorId);
            ClearConnectorSelection();
            NotifyDataChanged();
            InvalidateVisual();
            return;
        }

        var ids = GetSelectedIds().ToHashSet();
        if (ids.Count == 0) return;

        data.Elements.RemoveAll(e => ids.Contains(e.Id));
        data.Connectors.RemoveAll(c => ids.Contains(c.SourceId) || ids.Contains(c.TargetId));
        ClearSelection();
        NotifyDataChanged();
        InvalidateVisual();
    }

    // ================================================================
    // Cursor
    // ================================================================

    private void UpdateCursor(Point pos)
    {
        if (_isSpaceHeld || ToolMode == CanvasToolMode.Pan)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        if (ToolMode == CanvasToolMode.Select)
        {
            var resizeHit = HitTestResizeHandle(pos);
            if (resizeHit != null)
            {
                Cursor = new Cursor(StandardCursorType.SizeAll);
                return;
            }

            var data = Data;
            if (data != null && HitTestElement(pos, data) != null)
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            }

            if (data != null && HitTestConnector(pos, data) != null)
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            }
        }

        Cursor = ToolMode switch
        {
            CanvasToolMode.Freehand => new Cursor(StandardCursorType.Cross),
            CanvasToolMode.Connector => new Cursor(StandardCursorType.Cross),
            CanvasToolMode.NoteCard or CanvasToolMode.Rect or CanvasToolMode.Ellipse
                or CanvasToolMode.Text or CanvasToolMode.GroupFrame
                or CanvasToolMode.Diamond or CanvasToolMode.Parallelogram
                or CanvasToolMode.Cylinder or CanvasToolMode.Hexagon
                or CanvasToolMode.RoundedRect or CanvasToolMode.Triangle =>
                new Cursor(StandardCursorType.Cross),
            _ => Cursor.Default,
        };
    }
}
