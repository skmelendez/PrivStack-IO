// ============================================================================
// File: CanvasModels.cs
// Description: Data models for the InfiniteCanvasControl. Defines elements,
//              connectors, and the top-level canvas data container.
// ============================================================================

using System.Text.Json.Serialization;

namespace PrivStack.UI.Adaptive.Models;

/// <summary>
/// String constants for canvas element types. Forward-compatible: unknown types
/// are preserved during serialization but not rendered.
/// </summary>
public static class CanvasElementType
{
    public const string NoteCard = "note_card";
    public const string Text = "text";
    public const string Rect = "rect";
    public const string Ellipse = "ellipse";
    public const string Freehand = "freehand";
    public const string PageReference = "page_ref";
    public const string GroupFrame = "group_frame";
    public const string Image = "image";
    public const string EntityReference = "entity_ref";
    public const string Diamond = "diamond";
    public const string Parallelogram = "parallelogram";
    public const string Cylinder = "cylinder";
    public const string Hexagon = "hexagon";
    public const string RoundedRect = "rounded_rect";
    public const string Triangle = "triangle";
}

/// <summary>
/// Visual style for connectors between elements.
/// </summary>
public enum ConnectorStyle
{
    Straight,
    Curved,
    Elbow,
}

/// <summary>
/// Arrow direction mode for connectors.
/// </summary>
public enum ArrowMode
{
    Forward,
    None,
    Backward,
    Both,
}

/// <summary>
/// A single element on the infinite canvas.
/// </summary>
public sealed class CanvasElement
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = CanvasElementType.NoteCard;

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; } = 200;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 120;

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("linked_page_id")]
    public string? LinkedPageId { get; set; }

    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    [JsonPropertyName("z_index")]
    public int ZIndex { get; set; }

    [JsonPropertyName("stroke_points")]
    public List<StrokePoint> StrokePoints { get; set; } = [];

    [JsonPropertyName("stroke_width")]
    public double StrokeWidth { get; set; } = 2.0;

    [JsonPropertyName("stroke_color")]
    public string StrokeColor { get; set; } = "";

    [JsonPropertyName("font_size")]
    public double FontSize { get; set; } = 14;

    [JsonPropertyName("image_path")]
    public string? ImagePath { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("entity_type")]
    public string? EntityType { get; set; }

    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }
}

/// <summary>
/// A single point in a freehand stroke.
/// </summary>
public sealed class StrokePoint
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

/// <summary>
/// A connector (arrow/line) between two canvas elements.
/// </summary>
public sealed class CanvasConnector
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = "";

    [JsonPropertyName("target_id")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("style")]
    public ConnectorStyle Style { get; set; } = ConnectorStyle.Straight;

    [JsonPropertyName("arrow_mode")]
    public ArrowMode ArrowMode { get; set; } = ArrowMode.Forward;

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

/// <summary>
/// Top-level container for all canvas state. Serialized as JSON for persistence.
/// </summary>
public sealed class CanvasData
{
    [JsonPropertyName("elements")]
    public List<CanvasElement> Elements { get; set; } = [];

    [JsonPropertyName("connectors")]
    public List<CanvasConnector> Connectors { get; set; } = [];

    [JsonPropertyName("viewport_x")]
    public double ViewportX { get; set; }

    [JsonPropertyName("viewport_y")]
    public double ViewportY { get; set; }

    [JsonPropertyName("zoom")]
    public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// Find an element by ID, or null if not found.
    /// </summary>
    public CanvasElement? FindElement(string id) =>
        Elements.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Find a connector by ID, or null if not found.
    /// </summary>
    public CanvasConnector? FindConnector(string id) =>
        Connectors.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Returns the next Z-index for stacking a new element on top.
    /// </summary>
    public int NextZIndex() =>
        Elements.Count == 0 ? 0 : Elements.Max(e => e.ZIndex) + 1;
}
