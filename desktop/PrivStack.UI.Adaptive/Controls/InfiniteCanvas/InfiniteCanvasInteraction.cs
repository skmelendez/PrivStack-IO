// ============================================================================
// File: InfiniteCanvasInteraction.cs
// Description: Pointer and keyboard event dispatchers for InfiniteCanvasControl.
//              Tool-specific handlers are in InfiniteCanvasElementInteraction.cs.
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
            case CanvasToolMode.Diamond:
            case CanvasToolMode.Parallelogram:
            case CanvasToolMode.Cylinder:
            case CanvasToolMode.Hexagon:
            case CanvasToolMode.RoundedRect:
            case CanvasToolMode.Triangle:
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
            UpdateHoveredAnchor(pos);
            InvalidateVisual();
            return;
        }

        // Track hovered anchor in connector mode
        if (ToolMode == CanvasToolMode.Connector)
            UpdateHoveredAnchor(pos);

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
    // Pan
    // ================================================================

    private void StartPan(Point pos)
    {
        _isPanning = true;
        _panStartScreen = pos;
        Cursor = new Cursor(StandardCursorType.Hand);
    }
}
