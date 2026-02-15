// ============================================================================
// File: InfiniteCanvasGrouping.cs
// Description: Group frame child tracking for the canvas. Moving a group
//              frame moves all elements whose GroupId matches.
// ============================================================================

using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    /// <summary>
    /// Returns all elements whose GroupId equals the given frame's Id.
    /// </summary>
    internal static IEnumerable<CanvasElement> GetGroupChildren(CanvasData data, string groupId) =>
        data.Elements.Where(e => e.GroupId == groupId);

    /// <summary>
    /// Moves all children of a group by the given world-space delta.
    /// </summary>
    internal static void MoveGroupChildren(CanvasData data, string groupId, double dx, double dy)
    {
        foreach (var child in GetGroupChildren(data, groupId))
        {
            child.X += dx;
            child.Y += dy;
        }
    }

    /// <summary>
    /// Sets the GroupId of elements that are fully contained within
    /// the given group frame's bounds.
    /// </summary>
    internal static void ReparentToGroup(CanvasData data, CanvasElement groupFrame)
    {
        if (groupFrame.Type != CanvasElementType.GroupFrame) return;

        var groupRect = new Avalonia.Rect(
            groupFrame.X, groupFrame.Y,
            groupFrame.Width, groupFrame.Height);

        foreach (var el in data.Elements)
        {
            if (el.Id == groupFrame.Id) continue;
            if (el.Type == CanvasElementType.GroupFrame) continue;

            var elRect = new Avalonia.Rect(el.X, el.Y, el.Width, el.Height);

            if (groupRect.Contains(elRect))
                el.GroupId = groupFrame.Id;
            else if (el.GroupId == groupFrame.Id)
                el.GroupId = null;
        }
    }

    /// <summary>
    /// Removes group membership from all elements in the given group.
    /// </summary>
    internal static void DisbandGroup(CanvasData data, string groupId)
    {
        foreach (var el in data.Elements.Where(e => e.GroupId == groupId))
            el.GroupId = null;
    }
}
