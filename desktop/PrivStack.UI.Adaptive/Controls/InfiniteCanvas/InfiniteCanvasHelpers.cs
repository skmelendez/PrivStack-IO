// ============================================================================
// File: InfiniteCanvasHelpers.cs
// Description: Public helpers, theme helpers, and connector property methods
//              for InfiniteCanvasControl. Extracted for modularity.
// ============================================================================

using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    // ================================================================
    // Public Helpers
    // ================================================================

    /// <summary>
    /// Returns the world coordinates at the center of the current viewport.
    /// </summary>
    public (double X, double Y) GetViewportCenter()
    {
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        return ScreenToWorld(new Point(cx, cy));
    }

    /// <summary>
    /// Fits all elements into view by adjusting pan and zoom.
    /// </summary>
    public void FitToView()
    {
        var data = Data;
        if (data == null || data.Elements.Count == 0)
        {
            _panX = 0;
            _panY = 0;
            Zoom = 1.0;
            InvalidateVisual();
            return;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var el in data.Elements)
        {
            minX = Math.Min(minX, el.X);
            minY = Math.Min(minY, el.Y);
            maxX = Math.Max(maxX, el.X + el.Width);
            maxY = Math.Max(maxY, el.Y + el.Height);
        }

        var worldW = maxX - minX;
        var worldH = maxY - minY;

        if (worldW < 1 || worldH < 1)
        {
            _panX = -minX;
            _panY = -minY;
            Zoom = 1.0;
            InvalidateVisual();
            return;
        }

        var viewW = Bounds.Width - 80;
        var viewH = Bounds.Height - 80;
        var scale = Math.Min(viewW / worldW, viewH / worldH);
        scale = Math.Clamp(scale, 0.1, 5.0);

        _panX = -(minX + worldW / 2);
        _panY = -(minY + worldH / 2);
        Zoom = scale;
        InvalidateVisual();
    }

    /// <summary>
    /// Returns the visible viewport rectangle in world coordinates.
    /// </summary>
    internal Rect GetWorldViewport()
    {
        var (tlX, tlY) = ScreenToWorld(new Point(0, 0));
        var (brX, brY) = ScreenToWorld(new Point(Bounds.Width, Bounds.Height));
        return new Rect(tlX, tlY, brX - tlX, brY - tlY);
    }

    /// <summary>
    /// Raises DataChanged and checks performance threshold.
    /// </summary>
    internal void NotifyDataChanged()
    {
        DataChanged?.Invoke();

        var data = Data;
        if (data == null) return;

        var count = data.Elements.Count;
        if (count >= PerformanceWarningThreshold && !_performanceWarningFired)
        {
            _performanceWarningFired = true;
            PerformanceWarning?.Invoke(count);
        }
        else if (count < PerformanceWarningThreshold)
        {
            _performanceWarningFired = false;
        }
    }

    // ================================================================
    // Element Operations
    // ================================================================

    /// <summary>
    /// Returns the currently selected connector, or null.
    /// </summary>
    public CanvasConnector? GetSelectedConnector()
    {
        var data = Data;
        if (data == null || _selectedConnectorId == null) return null;
        return data.FindConnector(_selectedConnectorId);
    }

    /// <summary>
    /// Returns the currently selected elements.
    /// </summary>
    public IReadOnlyList<CanvasElement> GetSelectedElements()
    {
        var data = Data;
        if (data == null) return [];
        return _selectedElementIds
            .Select(id => data.FindElement(id))
            .Where(e => e != null)
            .ToList()!;
    }

    /// <summary>
    /// Moves all selected elements to the top of the Z-order.
    /// </summary>
    public void BringSelectedToFront()
    {
        var data = Data;
        if (data == null || _selectedElementIds.Count == 0) return;

        var maxZ = data.Elements.Count > 0 ? data.Elements.Max(e => e.ZIndex) : 0;
        foreach (var id in _selectedElementIds)
        {
            var el = data.FindElement(id);
            if (el != null) el.ZIndex = ++maxZ;
        }

        NotifyDataChanged();
        InvalidateVisual();
    }

    /// <summary>
    /// Moves all selected elements to the bottom of the Z-order.
    /// </summary>
    public void SendSelectedToBack()
    {
        var data = Data;
        if (data == null || _selectedElementIds.Count == 0) return;

        var minZ = data.Elements.Count > 0 ? data.Elements.Min(e => e.ZIndex) : 0;
        foreach (var id in _selectedElementIds)
        {
            var el = data.FindElement(id);
            if (el != null) el.ZIndex = --minZ;
        }

        // Renormalize so no negative indices persist
        var offset = data.Elements.Count > 0 ? data.Elements.Min(e => e.ZIndex) : 0;
        if (offset < 0)
        {
            foreach (var el in data.Elements)
                el.ZIndex -= offset;
        }

        NotifyDataChanged();
        InvalidateVisual();
    }

    /// <summary>
    /// Deep-clones selected elements with +20px offset and new IDs.
    /// Also duplicates connectors between selected elements with remapped IDs.
    /// </summary>
    public void DuplicateSelected()
    {
        var data = Data;
        if (data == null || _selectedElementIds.Count == 0) return;

        var idMap = new Dictionary<string, string>();
        var newElements = new List<CanvasElement>();

        foreach (var id in _selectedElementIds)
        {
            var el = data.FindElement(id);
            if (el == null) continue;

            var json = System.Text.Json.JsonSerializer.Serialize(el);
            var clone = System.Text.Json.JsonSerializer.Deserialize<CanvasElement>(json);
            if (clone == null) continue;

            var newId = Guid.NewGuid().ToString();
            idMap[id] = newId;
            clone.Id = newId;
            clone.X += 20;
            clone.Y += 20;
            clone.ZIndex = data.NextZIndex();
            newElements.Add(clone);
        }

        data.Elements.AddRange(newElements);

        // Duplicate connectors between selected elements
        foreach (var conn in data.Connectors.ToList())
        {
            if (idMap.ContainsKey(conn.SourceId) && idMap.ContainsKey(conn.TargetId))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(conn);
                var clone = System.Text.Json.JsonSerializer.Deserialize<CanvasConnector>(json);
                if (clone == null) continue;

                clone.Id = Guid.NewGuid().ToString();
                clone.SourceId = idMap[conn.SourceId];
                clone.TargetId = idMap[conn.TargetId];
                data.Connectors.Add(clone);
            }
        }

        // Select the new elements
        _selectedElementIds.Clear();
        foreach (var newEl in newElements)
            _selectedElementIds.Add(newEl.Id);

        NotifyDataChanged();
        InvalidateVisual();
    }

    /// <summary>
    /// Sets the color on all selected elements.
    /// </summary>
    public void SetSelectedElementColor(string? color)
    {
        var data = Data;
        if (data == null || _selectedElementIds.Count == 0) return;

        foreach (var id in _selectedElementIds)
        {
            var el = data.FindElement(id);
            if (el != null) el.Color = color ?? "";
        }

        NotifyDataChanged();
        InvalidateVisual();
    }

    // ================================================================
    // Connector Property Helpers
    // ================================================================

    /// <summary>
    /// Sets the connector style on the currently selected connector.
    /// </summary>
    public void SetSelectedConnectorStyle(ConnectorStyle style)
    {
        var data = Data;
        if (data == null || _selectedConnectorId == null) return;

        var connector = data.FindConnector(_selectedConnectorId);
        if (connector == null) return;

        connector.Style = style;
        NotifyDataChanged();
        ConnectorPropertyChanged?.Invoke(connector);
        InvalidateVisual();
    }

    /// <summary>
    /// Sets the arrow mode on the currently selected connector.
    /// </summary>
    public void SetSelectedArrowMode(ArrowMode mode)
    {
        var data = Data;
        if (data == null || _selectedConnectorId == null) return;

        var connector = data.FindConnector(_selectedConnectorId);
        if (connector == null) return;

        connector.ArrowMode = mode;
        NotifyDataChanged();
        ConnectorPropertyChanged?.Invoke(connector);
        InvalidateVisual();
    }

    /// <summary>
    /// Sets the color on the currently selected connector.
    /// </summary>
    public void SetSelectedConnectorColor(string? color)
    {
        var data = Data;
        if (data == null || _selectedConnectorId == null) return;

        var connector = data.FindConnector(_selectedConnectorId);
        if (connector == null) return;

        connector.Color = color;
        NotifyDataChanged();
        ConnectorPropertyChanged?.Invoke(connector);
        InvalidateVisual();
    }

    // ================================================================
    // Theme Helpers
    // ================================================================

    internal static Typeface GetSansTypeface(FontWeight weight)
    {
        var app = Avalonia.Application.Current;
        if (app != null && app.Resources.TryGetResource("ThemeFontSans", app.ActualThemeVariant, out var val)
            && val is FontFamily ff)
            return new Typeface(ff, FontStyle.Normal, weight);
        return new Typeface(FontFamily.Default, FontStyle.Normal, weight);
    }

    internal static IBrush GetBrush(string key, IBrush fallback)
    {
        var app = Avalonia.Application.Current;
        if (app != null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var val)
            && val is IBrush b)
            return b;
        return fallback;
    }

    internal static Color GetColor(string key, Color fallback)
    {
        var brush = GetBrush(key, new SolidColorBrush(fallback));
        return (brush as SolidColorBrush)?.Color ?? fallback;
    }
}
