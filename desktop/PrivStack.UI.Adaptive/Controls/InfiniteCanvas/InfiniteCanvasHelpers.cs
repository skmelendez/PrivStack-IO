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
}
