// ============================================================================
// File: InfiniteCanvasControl.cs
// Description: Core class for the infinite canvas control. Declares styled
//              properties, events, coordinate transforms, and public helpers.
//              Rendering, interaction, and hit-testing are in partial files.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

/// <summary>
/// A reusable infinite canvas control with pan, zoom, element creation,
/// connectors, freehand drawing, and grouping. Renders via DrawingContext
/// for high performance.
/// </summary>
public sealed partial class InfiniteCanvasControl : Control
{
    // ================================================================
    // Styled Properties
    // ================================================================

    public static readonly StyledProperty<CanvasData?> DataProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, CanvasData?>(nameof(Data));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<CanvasToolMode> ToolModeProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, CanvasToolMode>(nameof(ToolMode));

    public static readonly StyledProperty<int> PerformanceWarningThresholdProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, int>(nameof(PerformanceWarningThreshold), 200);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, bool>(nameof(ShowGrid), true);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<IReadOnlyDictionary<string, Models.EntityRefStyle>?> EntityRefStylesProperty =
        AvaloniaProperty.Register<InfiniteCanvasControl, IReadOnlyDictionary<string, Models.EntityRefStyle>?>(nameof(EntityRefStyles));

    public CanvasData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Clamp(value, 0.1, 5.0));
    }

    public CanvasToolMode ToolMode
    {
        get => GetValue(ToolModeProperty);
        set => SetValue(ToolModeProperty, value);
    }

    public int PerformanceWarningThreshold
    {
        get => GetValue(PerformanceWarningThresholdProperty);
        set => SetValue(PerformanceWarningThresholdProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public IReadOnlyDictionary<string, Models.EntityRefStyle>? EntityRefStyles
    {
        get => GetValue(EntityRefStylesProperty);
        set => SetValue(EntityRefStylesProperty, value);
    }

    // ================================================================
    // Events
    // ================================================================

    /// <summary>Fired when canvas data is mutated (element added/moved/deleted).</summary>
    public event Action? DataChanged;

    /// <summary>Fired when a page reference element is double-clicked.</summary>
    public event Action<string>? PageReferenceClicked;

    /// <summary>Fired when an entity reference element is double-clicked.</summary>
    public event Action<string, string>? EntityReferenceClicked;

    /// <summary>Fired when an element is selected.</summary>
    public event Action<CanvasElement>? ElementSelected;

    /// <summary>Fired when selection is cleared.</summary>
    public event Action? SelectionCleared;

    /// <summary>Fired when element count exceeds PerformanceWarningThreshold.</summary>
    public event Action<int>? PerformanceWarning;

    // ================================================================
    // Internal State
    // ================================================================

    private double _panX, _panY;
    private bool _isPanning;
    private Point _panStartScreen;
    private bool _isSpaceHeld;

    // Element creation state
    private Point _creationStart;
    private bool _isCreating;

    // Element drag state
    private string? _draggedElementId;
    private double _dragOffsetX, _dragOffsetY;
    private Point _dragStartScreen;
    private bool _wasActualDrag;

    // Resize state
    private ResizeHandle? _activeResizeHandle;
    private string? _resizingElementId;
    private Rect _resizeOriginalBounds;

    // Performance warning tracking
    private bool _performanceWarningFired;

    // ================================================================
    // Constructor / Static Init
    // ================================================================

    public InfiniteCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    static InfiniteCanvasControl()
    {
        AffectsRender<InfiniteCanvasControl>(DataProperty, ZoomProperty, ShowGridProperty);
    }

    // ================================================================
    // Coordinate Transforms
    // ================================================================

    internal Point WorldToScreen(double wx, double wy)
    {
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        return new Point(
            cx + (wx + _panX) * Zoom,
            cy + (wy + _panY) * Zoom);
    }

    internal (double wx, double wy) ScreenToWorld(Point screen)
    {
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        return (
            (screen.X - cx) / Zoom - _panX,
            (screen.Y - cy) / Zoom - _panY);
    }

    internal Rect ElementToScreenRect(CanvasElement element)
    {
        var tl = WorldToScreen(element.X, element.Y);
        return new Rect(tl.X, tl.Y, element.Width * Zoom, element.Height * Zoom);
    }

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
