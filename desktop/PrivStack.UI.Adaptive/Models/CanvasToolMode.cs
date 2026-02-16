// ============================================================================
// File: CanvasToolMode.cs
// Description: Enum defining the active tool mode for the InfiniteCanvasControl.
// ============================================================================

namespace PrivStack.UI.Adaptive.Models;

/// <summary>
/// The active tool mode determines how pointer events are interpreted
/// on the infinite canvas.
/// </summary>
public enum CanvasToolMode
{
    /// <summary>Select, move, and resize existing elements.</summary>
    Select,

    /// <summary>Pan the viewport by dragging.</summary>
    Pan,

    /// <summary>Create a new note card element.</summary>
    NoteCard,

    /// <summary>Create a new text element.</summary>
    Text,

    /// <summary>Create a new rectangle shape.</summary>
    Rect,

    /// <summary>Create a new ellipse shape.</summary>
    Ellipse,

    /// <summary>Draw freehand strokes.</summary>
    Freehand,

    /// <summary>Create a connector between two elements.</summary>
    Connector,

    /// <summary>Insert an image element.</summary>
    Image,

    /// <summary>Create a group frame to contain other elements.</summary>
    GroupFrame,

    /// <summary>Create a diamond (decision) shape.</summary>
    Diamond,

    /// <summary>Create a parallelogram (I/O) shape.</summary>
    Parallelogram,

    /// <summary>Create a cylinder (database) shape.</summary>
    Cylinder,

    /// <summary>Create a hexagon (preparation) shape.</summary>
    Hexagon,

    /// <summary>Create a rounded rectangle (terminal) shape.</summary>
    RoundedRect,

    /// <summary>Create a triangle shape.</summary>
    Triangle,
}
