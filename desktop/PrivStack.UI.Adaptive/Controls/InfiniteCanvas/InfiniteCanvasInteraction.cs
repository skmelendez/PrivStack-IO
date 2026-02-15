// ============================================================================
// File: InfiniteCanvasInteraction.cs
// Description: Pointer and keyboard event handling for InfiniteCanvasControl.
//              Implements the tool-mode state machine for selection, pan,
//              shape creation, connector creation, and freehand drawing.
// ============================================================================

using Avalonia;
using Avalonia.Input;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (IsReadOnly) return;

        var point = e.GetCurrentPoint(this);
        var pos = point.Position;

        // Middle-click always pans
        if (point.Properties.IsMiddleButtonPressed)
        {
            StartPan(pos);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        // Space+click pans
        if (_isSpaceHeld || ToolMode == CanvasToolMode.Pan)
        {
            StartPan(pos);
            e.Handled = true;
            return;
        }

        var data = Data;
        if (data == null) return;

        switch (ToolMode)
        {
            case CanvasToolMode.Select:
                HandleSelectPress(pos, data, e);
                break;

            case CanvasToolMode.NoteCard:
            case CanvasToolMode.Rect:
            case CanvasToolMode.Ellipse:
            case CanvasToolMode.Text:
            case CanvasToolMode.GroupFrame:
            case CanvasToolMode.Image:
                HandleCreationPress(pos);
                break;

            case CanvasToolMode.Freehand:
                var (wx, wy) = ScreenToWorld(pos);
                BeginStroke(wx, wy);
                break;

            case CanvasToolMode.Connector:
                HandleConnectorPress(pos, data);
                break;
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            var dx = pos.X - _panStartScreen.X;
            var dy = pos.Y - _panStartScreen.Y;
            _panX += dx / Zoom;
            _panY += dy / Zoom;
            _panStartScreen = pos;
            InvalidateVisual();
            return;
        }

        if (_resizingElementId != null)
        {
            HandleResizeMove(pos);
            return;
        }

        if (_draggedElementId != null)
        {
            HandleDragMove(pos);
            return;
        }

        if (_isDrawingStroke)
        {
            var (wx, wy) = ScreenToWorld(pos);
            ContinueStroke(wx, wy);
            return;
        }

        if (_isCreating)
        {
            _creationEnd = pos;
            InvalidateVisual();
            return;
        }

        if (_isConnecting)
        {
            _pendingConnectorEnd = pos;
            InvalidateVisual();
            return;
        }

        // Update cursor based on hover
        UpdateCursor(pos);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
            return;
        }

        if (_resizingElementId != null)
        {
            _resizingElementId = null;
            _activeResizeHandle = null;
            NotifyDataChanged();
            return;
        }

        if (_draggedElementId != null)
        {
            FinishDrag();
            return;
        }

        if (_isDrawingStroke)
        {
            var (wx, wy) = ScreenToWorld(pos);
            EndStroke(wx, wy);
            return;
        }

        if (_isCreating)
        {
            FinishCreation(pos);
            return;
        }

        if (_isConnecting)
        {
            FinishConnectorCreation(pos);
            return;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var pos = e.GetPosition(this);
        var oldZoom = Zoom;
        var delta = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var newZoom = Math.Clamp(oldZoom * delta, 0.1, 5.0);

        if (Math.Abs(newZoom - oldZoom) < 0.001) return;

        // Zoom toward cursor
        _panX = pos.X / newZoom - (pos.X / oldZoom - _panX);
        _panY = pos.Y / newZoom - (pos.Y / oldZoom - _panY);

        Zoom = newZoom;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Space)
        {
            _isSpaceHeld = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            DeleteSelectedElements();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ClearSelection();
            CancelCreation();
            CancelConnectorCreation();
            e.Handled = true;
            return;
        }

        // Ctrl+A: Select all
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            SelectAll();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key == Key.Space)
        {
            _isSpaceHeld = false;
            Cursor = Cursor.Default;
        }
    }

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
            // Click on empty space
            ClearSelection();
            SelectionCleared?.Invoke();

            // Start marquee selection
            BeginMarquee(pos);
        }
    }

    private void HandleDragMove(Point pos)
    {
        var dx = pos.X - _dragStartScreen.X;
        var dy = pos.Y - _dragStartScreen.Y;

        if (!_wasActualDrag && dx * dx + dy * dy >= 9)
            _wasActualDrag = true;

        if (!_wasActualDrag) return;

        var data = Data;
        if (data == null) return;

        // Move all selected elements
        var worldDx = (pos.X - _dragStartScreen.X) / Zoom;
        var worldDy = (pos.Y - _dragStartScreen.Y) / Zoom;
        _dragStartScreen = pos;

        foreach (var id in GetSelectedIds())
        {
            var el = data.FindElement(id);
            if (el == null) continue;
            el.X += worldDx;
            el.Y += worldDy;

            // Move group children if this is a group frame
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

        // Minimum size threshold (if user just clicked, use defaults)
        if (w < 10) w = 200;
        if (h < 10) h = ToolMode == CanvasToolMode.Text ? 40 : 120;

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

    private static string ToolModeToElementType(CanvasToolMode mode) => mode switch
    {
        CanvasToolMode.NoteCard => CanvasElementType.NoteCard,
        CanvasToolMode.Text => CanvasElementType.Text,
        CanvasToolMode.Rect => CanvasElementType.Rect,
        CanvasToolMode.Ellipse => CanvasElementType.Ellipse,
        CanvasToolMode.GroupFrame => CanvasElementType.GroupFrame,
        CanvasToolMode.Image => CanvasElementType.Image,
        _ => CanvasElementType.NoteCard,
    };

    // ================================================================
    // Deletion
    // ================================================================

    private void DeleteSelectedElements()
    {
        var data = Data;
        if (data == null) return;

        var ids = GetSelectedIds().ToHashSet();
        if (ids.Count == 0) return;

        data.Elements.RemoveAll(e => ids.Contains(e.Id));
        data.Connectors.RemoveAll(c => ids.Contains(c.SourceId) || ids.Contains(c.TargetId));
        ClearSelection();
        NotifyDataChanged();
        InvalidateVisual();
    }

    // ================================================================
    // Pan
    // ================================================================

    private void StartPan(Point pos)
    {
        _isPanning = true;
        _panStartScreen = pos;
        Cursor = new Cursor(StandardCursorType.Hand);
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
        }

        Cursor = ToolMode switch
        {
            CanvasToolMode.Freehand => new Cursor(StandardCursorType.Cross),
            CanvasToolMode.Connector => new Cursor(StandardCursorType.Cross),
            CanvasToolMode.NoteCard or CanvasToolMode.Rect or CanvasToolMode.Ellipse
                or CanvasToolMode.Text or CanvasToolMode.GroupFrame =>
                new Cursor(StandardCursorType.Cross),
            _ => Cursor.Default,
        };
    }
}
