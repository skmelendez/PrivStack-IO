// ============================================================================
// File: InfiniteCanvasDragDrop.cs
// Description: Avalonia DragDrop handling for the canvas. Accepts page
//              references and image files dropped onto the canvas surface.
// ============================================================================

using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private static readonly DataFormat<string> PageRefFormat =
        DataFormat.CreateStringPlatformFormat("privstack/page-ref");

    private static readonly DataFormat<string> EntityRefFormat =
        DataFormat.CreateStringPlatformFormat("privstack/entity-ref");

    private Point? _dropGhostPosition;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        RemoveHandler(DragDrop.DropEvent, OnDrop);
        RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (IsReadOnly)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var hasPageRef = e.DataTransfer.Contains(PageRefFormat);
        var hasEntityRef = e.DataTransfer.Contains(EntityRefFormat);
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);

        if (hasPageRef || hasEntityRef || hasFiles)
        {
            e.DragEffects = DragDropEffects.Copy;
            _dropGhostPosition = e.GetPosition(this);
            InvalidateVisual();
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dropGhostPosition = null;
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropGhostPosition = null;

        if (IsReadOnly) return;

        var data = Data;
        if (data == null) return;

        var pos = e.GetPosition(this);
        var (wx, wy) = ScreenToWorld(pos);

        // Handle page reference drop
        if (e.DataTransfer.Contains(PageRefFormat))
        {
            var pageRefData = e.DataTransfer.TryGetValue(PageRefFormat);
            if (string.IsNullOrEmpty(pageRefData)) return;

            // Expected format: "pageId|pageTitle"
            var parts = pageRefData.Split('|', 2);
            var pageId = parts[0];
            var pageTitle = parts.Length > 1 ? parts[1] : "Page Reference";

            var element = new CanvasElement
            {
                Id = Guid.NewGuid().ToString(),
                Type = CanvasElementType.PageReference,
                X = wx,
                Y = wy,
                Width = 220,
                Height = 60,
                Text = pageTitle,
                LinkedPageId = pageId,
                ZIndex = data.NextZIndex(),
            };

            data.Elements.Add(element);
            ClearSelection();
            Select(element.Id);
            NotifyDataChanged();
            InvalidateVisual();
            return;
        }

        // Handle entity reference drop
        if (e.DataTransfer.Contains(EntityRefFormat))
        {
            var json = e.DataTransfer.TryGetValue(EntityRefFormat);
            if (string.IsNullOrEmpty(json)) return;

            EntityRefDropPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<EntityRefDropPayload>(json);
            }
            catch
            {
                return;
            }
            if (payload == null || string.IsNullOrEmpty(payload.EntityId)) return;

            var badgeColor = "#888888";
            var styles = EntityRefStyles;
            if (styles != null && styles.TryGetValue(payload.EntityType, out var style))
                badgeColor = style.BadgeColor;

            var element = new CanvasElement
            {
                Id = Guid.NewGuid().ToString(),
                Type = CanvasElementType.EntityReference,
                X = wx,
                Y = wy,
                Width = 240,
                Height = 70,
                Text = payload.Title,
                Label = payload.Subtitle,
                Color = badgeColor,
                EntityType = payload.EntityType,
                EntityId = payload.EntityId,
                ZIndex = data.NextZIndex(),
            };

            data.Elements.Add(element);
            ClearSelection();
            Select(element.Id);
            NotifyDataChanged();
            InvalidateVisual();
            return;
        }

        // Handle file drops (images)
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            var offsetX = 0.0;
            foreach (var file in files)
            {
                var path = file.Path?.LocalPath;
                if (string.IsNullOrEmpty(path)) continue;

                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"))
                    continue;

                var element = new CanvasElement
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = CanvasElementType.Image,
                    X = wx + offsetX,
                    Y = wy,
                    Width = 300,
                    Height = 200,
                    ImagePath = path,
                    Text = System.IO.Path.GetFileName(path),
                    ZIndex = data.NextZIndex(),
                };

                data.Elements.Add(element);
                offsetX += 320;
            }

            NotifyDataChanged();
            InvalidateVisual();
        }
    }
}
