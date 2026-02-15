// ============================================================================
// File: InfiniteCanvasDragDrop.cs
// Description: Avalonia DragDrop handling for the canvas. Accepts page
//              references and image files dropped onto the canvas surface.
// ============================================================================

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
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

        var hasPageRef = e.Data.Contains("privstack/page-ref");
        var hasFiles = e.Data.Contains(DataFormats.Files);

        if (hasPageRef || hasFiles)
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
        if (e.Data.Contains("privstack/page-ref"))
        {
            var pageRefData = e.Data.Get("privstack/page-ref") as string;
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

        // Handle file drops (images)
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
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
