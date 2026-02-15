// ============================================================================
// File: EntityDragHelper.cs
// Description: Reusable helper for plugins to emit privstack/entity-ref
//              drag data without duplicating boilerplate.
// ============================================================================

#pragma warning disable CS0618 // DataObject is obsolete in Avalonia 11.3+

using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Helpers;

public static class EntityDragHelper
{
    public const string Format = "privstack/entity-ref";
    private const double DragThreshold = 5;

    public static bool ShouldStartDrag(Point start, Point current)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        return Math.Sqrt(dx * dx + dy * dy) > DragThreshold;
    }

    public static DataObject CreateDataObject(
        string entityType, string entityId,
        string title, string? subtitle = null)
    {
        var payload = new EntityRefDropPayload
        {
            EntityType = entityType,
            EntityId = entityId,
            Title = title,
            Subtitle = subtitle,
        };
        var json = JsonSerializer.Serialize(payload);
        var data = new DataObject();
        data.Set(Format, json);
        return data;
    }
}
