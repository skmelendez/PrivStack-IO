// ============================================================================
// File: NeuronGraphControl.cs
// Description: Reusable neuron graph control rendering a force-directed
//              knowledge graph centered on the selected entity. Supports
//              depth-based styling, empty state, and node dragging.
// ============================================================================

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PrivStack.UI.Adaptive.Models;
using PrivStack.UI.Adaptive.Services;
using Serilog;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// Renders a neuron-style knowledge graph centered on a selected entity.
/// Supports depth-based visual styling, node dragging, and empty state rendering.
/// </summary>
public sealed class NeuronGraphControl : Control
{
    private static readonly ILogger _log = Log.ForContext<NeuronGraphControl>();

    private GraphData? _graphData;
    private GraphData? _fullGraphData; // Unfiltered source of truth — _graphData may be a filtered subset
    private ForceLayoutEngine? _engine;
    private DispatcherTimer? _timer;
    private string? _centerId;
    private string? _hoveredNodeId;
    private string? _draggedNodeId;
    private Point _dragOffset;

    // Zoom & pan state — applied as a render transform over the entire graph
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    // Smoothed viewport: tracks graph center/extent with lerp to prevent jitter
    private double _viewCenterX;
    private double _viewCenterY;
    private double _viewScale = 1.0;
    private bool _viewInitialized;

    // Highlight mode: BFS-based node highlighting for main graph view
    private string? _highlightedNodeId;
    private int _highlightDepth = 2;
    private Dictionary<string, int>? _highlightDistances;

    // Hover highlight: BFS distances from currently hovered node
    private Dictionary<string, int>? _hoverDistances;

    // Animated opacity per node — lerps toward target each tick
    private readonly Dictionary<string, double> _nodeOpacities = new();
    private bool _opacityAnimating;

    // Focus mode: smoothly pan to center on a clicked node
    private string? _focusedNodeId;

    /// <summary>
    /// Physics parameters for the force layout engine. Set before calling Start().
    /// </summary>
    public PhysicsParameters? Physics { get; set; }

    /// <summary>
    /// When true, clicking a node highlights it and BFS-fades nodes beyond HighlightDepth.
    /// Used for the main graph view. The neuron info panel leaves this false.
    /// </summary>
    public bool EnableHighlightMode { get; set; }

    /// <summary>
    /// When true and a node is highlighted, nodes outside HighlightDepth are hidden entirely
    /// rather than dimmed to grey.
    /// </summary>
    public bool HideInactiveNodes
    {
        get => _hideInactiveNodes;
        set
        {
            if (_hideInactiveNodes != value)
            {
                _hideInactiveNodes = value;
                ApplyActiveNodeFilter();
            }
        }
    }
    private bool _hideInactiveNodes;

    /// <summary>
    /// Number of BFS hops from the highlighted node that remain fully colored.
    /// Changing this repaints immediately when a node is highlighted.
    /// </summary>
    public int HighlightDepth
    {
        get => _highlightDepth;
        set
        {
            if (_highlightDepth != value)
            {
                _highlightDepth = value;
                if (_highlightedNodeId != null)
                    ApplyActiveNodeFilter();
            }
        }
    }

    /// <summary>
    /// The currently highlighted node. Setting triggers BFS recomputation and repaint.
    /// </summary>
    public string? HighlightedNodeId
    {
        get => _highlightedNodeId;
        set
        {
            if (_highlightedNodeId != value)
            {
                _highlightedNodeId = value;
                ComputeHighlightDistances();
                ApplyActiveNodeFilter();
            }
        }
    }

    /// <summary>
    /// Smoothly pan the viewport to center on the specified node.
    /// </summary>
    public void FocusOnNode(string nodeId)
    {
        _focusedNodeId = nodeId;
        if (_timer != null && !_timer.IsEnabled)
            _timer.Start();
    }

    /// <summary>
    /// Fired when a node is clicked. Passes the node ID.
    /// </summary>
    public event Action<string>? NodeClicked;

    /// <summary>
    /// Fired when the active node is deselected (ESC key or click on empty space).
    /// </summary>
    public event Action? NodeDeselected;

    /// <summary>
    /// Fired when the pointer enters a node. Passes the node ID.
    /// Use for prefetch on hover.
    /// </summary>
    public event Action<string>? NodeHovered;

    /// <summary>
    /// Fired when the pointer leaves a node. Passes the node ID.
    /// Use to cancel pending prefetch.
    /// </summary>
    public event Action<string>? NodeUnhovered;

    /// <summary>
    /// JSON elements for nodes.
    /// </summary>
    public IReadOnlyList<JsonElement>? Nodes { get; set; }

    /// <summary>
    /// JSON elements for edges.
    /// </summary>
    public IReadOnlyList<JsonElement>? Edges { get; set; }

    /// <summary>
    /// The center node ID (highlighted with primary brush and view centered on it).
    /// </summary>
    public string? CenterId
    {
        get => _centerId;
        set
        {
            if (_centerId != value)
            {
                _log.Debug("NeuronGraph: CenterId changing from {Old} to {New}", _centerId ?? "(null)", value ?? "(null)");
                _centerId = value;
                // When center node changes, reset pan and re-center the view on the new center node
                _panX = 0;
                _panY = 0;
                _needsCenterOnNode = true;
                InvalidateVisual();
            }
        }
    }

    // Flag to re-center view on the center node after CenterId changes
    private bool _needsCenterOnNode;

    /// <summary>
    /// Initialize the graph data and start the force layout simulation.
    /// </summary>
    public void Start()
    {
        Stop();
        _viewInitialized = false;
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        _highlightedNodeId = null;
        _highlightDistances = null;
        _focusedNodeId = null;
        // If we have a center node, center the view on it
        _needsCenterOnNode = !string.IsNullOrEmpty(_centerId);

        _log.Debug("NeuronGraph.Start: CenterId={CenterId}, Nodes={NodeCount}, Edges={EdgeCount}, centerOnNode={CenterOnNode}",
            _centerId ?? "(null)", Nodes?.Count ?? 0, Edges?.Count ?? 0, _needsCenterOnNode);

        if (Nodes is null || Nodes.Count == 0)
        {
            _log.Debug("NeuronGraph.Start: No nodes provided, aborting");
            return;
        }

        _graphData = GraphData.FromJson(Nodes, Edges ?? []);
        _fullGraphData = _graphData;
        if (_graphData.Nodes.Count == 0)
        {
            _log.Debug("NeuronGraph.Start: GraphData parsed but empty, aborting");
            return;
        }

        // Log node details
        foreach (var (id, node) in _graphData.Nodes)
        {
            _log.Verbose("  Node: Id={Id}, Title={Title}, Type={Type}, Depth={Depth}",
                id, node.Title, node.NodeType, node.Depth);
        }

        // Log edge details
        foreach (var edge in _graphData.Edges)
        {
            _log.Verbose("  Edge: {Source} -> {Target}, Type={EdgeType}",
                edge.SourceId, edge.TargetId, edge.EdgeType);
        }

        if (Physics != null)
        {
            _engine = new ForceLayoutEngine(Physics);
            _engine.SetGraphData(_graphData);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        else
        {
            // Physics disabled — arrange nodes in a circle
            var nodes = _graphData.Nodes.Values.ToList();
            var angleStep = nodes.Count > 1 ? 2 * Math.PI / nodes.Count : 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                nodes[i].X = Math.Cos(i * angleStep) * 150;
                nodes[i].Y = Math.Sin(i * angleStep) * 150;
            }
            InvalidateVisual();
        }
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _engine = null;
        _fullGraphData = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeVariantChanged;

        // Resume the physics timer if we have data from a previous session
        // (e.g. re-attached after a tab switch with view caching).
        if (_engine != null && _graphData != null)
        {
            _engine.Reheat(0.3);
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _timer.Tick += OnTick;
            }
            if (!_timer.IsEnabled)
                _timer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Stop the physics timer to avoid a 60fps tick while the control is
        // off-screen.  Data and engine are kept alive so the graph can resume
        // instantly when re-attached (common with view caching).
        _timer?.Stop();
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>
    /// Initialize the graph with pre-parsed GraphData and start the force layout simulation.
    /// Used for deferred hydration where JSON has already been parsed.
    /// </summary>
    public void StartWithData(GraphData data)
    {
        Focusable = true;
        Stop();
        _viewInitialized = false;
        _zoom = 1.0;
        _panX = 0;
        _panY = 0;
        _highlightedNodeId = null;
        _highlightDistances = null;
        _focusedNodeId = null;
        // If we have a center node, center the view on it
        _needsCenterOnNode = !string.IsNullOrEmpty(_centerId);

        _log.Debug("NeuronGraph.StartWithData: CenterId={CenterId}, Nodes={NodeCount}, centerOnNode={CenterOnNode}",
            _centerId ?? "(null)", data.Nodes.Count, _needsCenterOnNode);

        if (data.Nodes.Count == 0) return;

        _graphData = data;
        _fullGraphData = data;

        if (Physics != null)
        {
            _engine = new ForceLayoutEngine(Physics);
            _engine.SetGraphData(_graphData);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        else
        {
            // Physics disabled — arrange nodes in a circle
            var nodes = _graphData.Nodes.Values.ToList();
            var angleStep = nodes.Count > 1 ? 2 * Math.PI / nodes.Count : 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                nodes[i].X = Math.Cos(i * angleStep) * 150;
                nodes[i].Y = Math.Sin(i * angleStep) * 150;
            }
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Update the graph with new node/edge data, preserving positions of retained nodes.
    /// New nodes are placed near connected existing nodes; removed nodes are dropped.
    /// The physics simulation is reheated so the layout adjusts smoothly.
    /// </summary>
    public void UpdateData(IReadOnlyList<JsonElement> newNodes, IReadOnlyList<JsonElement> newEdges, bool fullRelayout = false)
    {
        _log.Debug("NeuronGraph.UpdateData: CenterId={CenterId}, incoming nodes={NewCount} edges={EdgeCount}, existing nodes={OldCount}, fullRelayout={FullRelayout}",
            _centerId ?? "(null)", newNodes.Count, newEdges.Count, _graphData?.Nodes.Count ?? -1, fullRelayout);

        // Log incoming node IDs for debugging link expectations
        foreach (var nodeEl in newNodes)
        {
            var id = nodeEl.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var title = nodeEl.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var nodeType = nodeEl.TryGetProperty("node_type", out var typeProp) ? typeProp.GetString() : null;
            _log.Verbose("  Incoming node: Id={Id}, Title={Title}, Type={Type}", id, title, nodeType);
        }
        foreach (var edgeEl in newEdges)
        {
            var src = edgeEl.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : null;
            var tgt = edgeEl.TryGetProperty("target", out var tgtProp) ? tgtProp.GetString() : null;
            var edgeType = edgeEl.TryGetProperty("edge_type", out var etProp) ? etProp.GetString() : null;
            _log.Verbose("  Incoming edge: {Source} -> {Target}, Type={EdgeType}", src, tgt, edgeType);
        }

        var incoming = GraphData.FromJson(newNodes, newEdges);
        if (incoming.Nodes.Count == 0)
        {
            _graphData = incoming;
            _engine?.SetGraphData(incoming);
            InvalidateVisual();
            return;
        }

        if (_graphData is null)
        {
            // No existing data — just start fresh
            Nodes = newNodes;
            Edges = newEdges;
            Start();
            return;
        }

        // Preserve positions/velocities of retained nodes
        foreach (var (id, node) in incoming.Nodes)
        {
            if (_graphData.Nodes.TryGetValue(id, out var existing))
            {
                node.X = existing.X;
                node.Y = existing.Y;
                node.Vx = existing.Vx;
                node.Vy = existing.Vy;
                node.IsPinned = existing.IsPinned;
            }
            else
            {
                // New node — place near a connected neighbor or at a random offset from center
                var neighbor = incoming.Edges
                    .Where(e => e.SourceId == id || e.TargetId == id)
                    .Select(e => e.SourceId == id ? e.TargetId : e.SourceId)
                    .FirstOrDefault(nId => _graphData.Nodes.ContainsKey(nId));

                if (neighbor != null && _graphData.Nodes.TryGetValue(neighbor, out var nNode))
                {
                    var angle = Random.Shared.NextDouble() * 2 * Math.PI;
                    node.X = nNode.X + Math.Cos(angle) * 40;
                    node.Y = nNode.Y + Math.Sin(angle) * 40;
                }
                else
                {
                    var angle = Random.Shared.NextDouble() * 2 * Math.PI;
                    var dist = 80 + Random.Shared.NextDouble() * 60;
                    node.X = Math.Cos(angle) * dist;
                    node.Y = Math.Sin(angle) * dist;
                }
            }
        }

        _graphData = incoming;
        _fullGraphData = incoming;
        _engine?.SetGraphData(_graphData, preservePositions: !fullRelayout);
        Reheat();
    }

    /// <summary>
    /// Push current Physics parameters into the running engine and reheat
    /// so slider changes take effect immediately.
    /// </summary>
    public void ApplyPhysicsChanges()
    {
        if (_engine != null && Physics != null)
        {
            _engine.UpdateParameters(Physics);
            Reheat();
        }
    }

    /// <summary>
    /// Reheat the simulation so physics changes take visible effect.
    /// </summary>
    public void Reheat()
    {
        _engine?.Reheat();
        if (_engine is null) return;

        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
        }

        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var engineRunning = _engine?.IsRunning == true;
        if (engineRunning) _engine!.Tick();

        UpdateOpacities();

        if (!engineRunning && _focusedNodeId == null && !_opacityAnimating)
        {
            _timer?.Stop();
            InvalidateVisual();
            return;
        }

        InvalidateVisual();
    }

    // ----------------------------------------------------------------
    // Coordinate helpers — smoothed viewport prevents per-frame jitter
    // ----------------------------------------------------------------

    /// <summary>
    /// Compute the raw (instant) bounding box transform, then smooth it
    /// so the viewport doesn't snap every frame as nodes move.
    /// When _needsCenterOnNode is set, centers on the _centerId node instead of bounding box center.
    /// </summary>
    private (double Scale, double GraphCenterX, double GraphCenterY) ComputeBaseTransform()
    {
        if (_graphData is null) return (1, 0, 0);

        var bounds = Bounds;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var node in _graphData.Nodes.Values)
        {
            minX = Math.Min(minX, node.X);
            maxX = Math.Max(maxX, node.X);
            minY = Math.Min(minY, node.Y);
            maxY = Math.Max(maxY, node.Y);
        }

        var graphWidth = Math.Max(maxX - minX, 1);
        var graphHeight = Math.Max(maxY - minY, 1);
        var padding = 40.0;
        var scaleX = (bounds.Width - padding * 2) / graphWidth;
        var scaleY = (bounds.Height - padding * 2) / graphHeight;
        var targetScale = Math.Min(Math.Min(scaleX, scaleY), 3.0);

        // Default: center on bounding box
        var targetCx = (minX + maxX) / 2;
        var targetCy = (minY + maxY) / 2;

        // If we need to center on a specific node (after CenterId change), use that node's position
        if (_needsCenterOnNode && !string.IsNullOrEmpty(_centerId) &&
            _graphData.Nodes.TryGetValue(_centerId, out var centerNode))
        {
            _log.Debug("NeuronGraph: Centering view on node {CenterId} at ({X}, {Y})", _centerId, centerNode.X, centerNode.Y);
            targetCx = centerNode.X;
            targetCy = centerNode.Y;
            _needsCenterOnNode = false;
            // Snap immediately when centering on a specific node
            _viewCenterX = targetCx;
            _viewCenterY = targetCy;
            _viewScale = targetScale;
            _viewInitialized = true;
            return (_viewScale, _viewCenterX, _viewCenterY);
        }

        // Focus on clicked node: override the lerp target so the viewport
        // smoothly pans to center on that node's world position
        if (_focusedNodeId != null && _graphData.Nodes.TryGetValue(_focusedNodeId, out var focusNode))
        {
            targetCx = focusNode.X;
            targetCy = focusNode.Y;
            // Smoothly zero out user pan offset so it doesn't fight with centering
            _panX *= 0.88;
            _panY *= 0.88;
            if (Math.Abs(_panX) < 0.1) _panX = 0;
            if (Math.Abs(_panY) < 0.1) _panY = 0;
        }

        if (!_viewInitialized)
        {
            _viewCenterX = targetCx;
            _viewCenterY = targetCy;
            _viewScale = targetScale;
            _viewInitialized = true;
        }
        else
        {
            // Lerp center tracking — 0.08 gives smooth tracking without lag.
            // Scale is NOT lerped: it's set once on Start/StartWithData and
            // stays fixed so the user sees actual expansion when LinkDistance
            // changes (auto-fit zoom-out was inverting the visual effect).
            const double lerp = 0.08;
            _viewCenterX += (targetCx - _viewCenterX) * lerp;
            _viewCenterY += (targetCy - _viewCenterY) * lerp;
        }

        return (_viewScale, _viewCenterX, _viewCenterY);
    }

    /// <summary>
    /// Convert graph world coordinates to screen coordinates at base scale (no zoom/pan).
    /// Zoom and pan are applied as a render transform on top of this.
    /// </summary>
    private Point WorldToBase(double wx, double wy, double scale, double gcx, double gcy)
    {
        var bounds = Bounds;
        return new Point(
            bounds.Width / 2 + (wx - gcx) * scale,
            bounds.Height / 2 + (wy - gcy) * scale);
    }

    /// <summary>
    /// Convert screen coordinates (with zoom/pan) back to graph world coordinates.
    /// </summary>
    private (double X, double Y) ScreenToWorld(Point screenPos)
    {
        if (_graphData is null) return (0, 0);

        var bounds = Bounds;
        var cx = bounds.Width / 2;
        var cy = bounds.Height / 2;
        var (scale, gcx, gcy) = ComputeBaseTransform();

        // Undo zoom+pan: screen → base → world
        var baseX = (screenPos.X - cx - _panX * _zoom) / _zoom + cx;
        var baseY = (screenPos.Y - cy - _panY * _zoom) / _zoom + cy;

        // Undo base transform
        var wx = (baseX - cx) / scale + gcx;
        var wy = (baseY - cy) / scale + gcy;
        return (wx, wy);
    }

    // ----------------------------------------------------------------
    // Rendering
    // ----------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var bounds = Bounds;
        var clipRect = new Rect(bounds.Size);

        // Hit-test background + clip to bounds
        ctx.DrawRectangle(Brushes.Transparent, null, clipRect);
        using var clip = ctx.PushClip(clipRect);

        var centerX = bounds.Width / 2;
        var centerY = bounds.Height / 2;

        // Empty state: no data or single node with no edges
        if (_graphData is null || _graphData.Nodes.Count == 0)
            return;

        var isEmptyGraph = _graphData.Nodes.Count == 1 && _graphData.Edges.Count == 0;
        if (isEmptyGraph)
        {
            RenderEmptyState(ctx, bounds, centerX, centerY);
            return;
        }

        var (scale, gcx, gcy) = ComputeBaseTransform();

        // Apply zoom+pan as a render transform: translate to center, scale, translate back + pan
        var zoomMatrix = Matrix.CreateTranslation(-centerX, -centerY)
            * Matrix.CreateScale(_zoom, _zoom)
            * Matrix.CreateTranslation(centerX + _panX * _zoom, centerY + _panY * _zoom);
        using var transform = ctx.PushTransform(zoomMatrix);

        Point ToBase(double x, double y) => WorldToBase(x, y, scale, gcx, gcy);

        // Draw depth ring indicators (subtle concentric circles)
        RenderDepthRings(ctx, centerX, centerY, bounds);

        // Draw edges — opacity driven by animated node opacities
        foreach (var edge in _graphData.Edges)
        {
            if (!_graphData.Nodes.TryGetValue(edge.SourceId, out var src) ||
                !_graphData.Nodes.TryGetValue(edge.TargetId, out var tgt))
                continue;

            var srcOpacity = _nodeOpacities.GetValueOrDefault(edge.SourceId, 0.8);
            var tgtOpacity = _nodeOpacities.GetValueOrDefault(edge.TargetId, 0.8);
            var edgeOpacity = Math.Min(srcOpacity, tgtOpacity);

            if (EnableHighlightMode && _hideInactiveNodes && edgeOpacity < 0.5) continue;

            var p1 = ToBase(src.X, src.Y);
            var p2 = ToBase(tgt.X, tgt.Y);

            IBrush edgeBrush;
            double thickness;

            const double edgeBaseOpacity = 0.75;
            var blendedColor = BlendColors(GetNodeColor(src.NodeType), GetNodeColor(tgt.NodeType));

            if (EnableHighlightMode)
            {
                edgeBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)(blendedColor.A * edgeBaseOpacity), blendedColor.R, blendedColor.G, blendedColor.B));
                thickness = edgeOpacity > 0.7 ? 1.2 : 0.7;
            }
            else
            {
                var isConnectedToCenter = edge.SourceId == _centerId || edge.TargetId == _centerId;
                var maxDepth = Math.Max(src.Depth, tgt.Depth);

                edgeBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)(blendedColor.A * edgeBaseOpacity), blendedColor.R, blendedColor.G, blendedColor.B));
                thickness = isConnectedToCenter ? 1.5 : maxDepth >= 2 ? 0.7 : 1.0;
            }

            var pen = new Pen(edgeBrush, thickness) { LineCap = PenLineCap.Round };
            ctx.DrawLine(pen, p1, p2);
        }

        // Draw nodes — opacity from animated _nodeOpacities
        var typeface = new Typeface(GetFontFamily("ThemeFontSans"), FontStyle.Normal, FontWeight.Normal);

        foreach (var node in _graphData.Nodes.Values)
        {
            var nodeOpacity = _nodeOpacities.GetValueOrDefault(node.Id, 0.8);

            if (EnableHighlightMode && _hideInactiveNodes && nodeOpacity < 0.5) continue;

            var pos = ToBase(node.X, node.Y);
            var radius = node.Radius;

            IBrush fill;
            var drawHighlightRing = false;

            if (EnableHighlightMode)
            {
                var baseColor = GetNodeColor(node.NodeType);
                fill = new SolidColorBrush(Color.FromArgb(
                    (byte)(baseColor.A * nodeOpacity), baseColor.R, baseColor.G, baseColor.B));

                // White ring on click-locked node
                if (node.Id == _highlightedNodeId)
                    drawHighlightRing = true;
            }
            else
            {
                // Non-highlight mode (neuron info panel)
                var depthOpacity = Math.Max(0.4, 1.0 - node.Depth * 0.15);
                fill = GetNodeBrush(node.NodeType);

                if (node.Depth >= 2 && fill is ISolidColorBrush solid)
                    fill = new SolidColorBrush(solid.Color, depthOpacity);

                if (node.Id == _centerId)
                {
                    var ringPen = new Pen(Brushes.White, 2.0);
                    ctx.DrawEllipse(null, ringPen, pos, radius + 3, radius + 3);
                }
            }

            if (drawHighlightRing)
            {
                var ringPen = new Pen(Brushes.White, 2.5);
                ctx.DrawEllipse(null, ringPen, pos, radius + 4, radius + 4);
            }

            ctx.DrawEllipse(fill, null, pos, radius, radius);

            // Label — opacity matches node
            if (!string.IsNullOrEmpty(node.Title))
            {
                var fontSize = (EnableHighlightMode && node.Id == _highlightedNodeId) || node.Id == _centerId
                    ? GetDouble("ThemeFontSizeXsSm", 11) : GetDouble("ThemeFontSize2Xs", 9);
                var labelColor = GetBrush("ThemeTextSecondaryBrush", Brushes.LightGray) is ISolidColorBrush lb
                    ? lb.Color : Colors.LightGray;
                var labelBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)(labelColor.A * nodeOpacity), labelColor.R, labelColor.G, labelColor.B));
#pragma warning disable CS0618
                var formattedText = new FormattedText(
                    node.Title.Length > 24 ? node.Title[..21] + "..." : node.Title,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    labelBrush);
#pragma warning restore CS0618
                ctx.DrawText(formattedText, new Point(pos.X - formattedText.Width / 2, pos.Y + radius + 4));
            }
        }
    }

    private void RenderEmptyState(DrawingContext ctx, Rect bounds, double centerX, double centerY)
    {
        var node = _graphData!.Nodes.Values.First();
        var fill = GetBrush("ThemePrimaryBrush", Brushes.DodgerBlue);
        var radius = node.Radius;
        ctx.DrawEllipse(fill, null, new Point(centerX, centerY - 10), radius, radius);

        var typeface = new Typeface(GetFontFamily("ThemeFontSans"), FontStyle.Normal, FontWeight.Normal);
        var labelBrush = GetBrush("ThemeTextSecondaryBrush", Brushes.LightGray);
#pragma warning disable CS0618
        var titleText = new FormattedText(
            node.Title.Length > 25 ? node.Title[..22] + "..." : node.Title,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            GetDouble("ThemeFontSizeXsSm", 12),
            labelBrush);
#pragma warning restore CS0618
        ctx.DrawText(titleText, new Point(centerX - titleText.Width / 2, centerY + radius));

        var mutedBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
#pragma warning disable CS0618
        var noConnText = new FormattedText(
            "No connections",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            GetDouble("ThemeFontSizeXs", 11),
            mutedBrush);
#pragma warning restore CS0618
        ctx.DrawText(noConnText, new Point(centerX - noConnText.Width / 2, centerY + radius + 16));
    }

    private void RenderDepthRings(DrawingContext ctx, double centerX, double centerY, Rect bounds)
    {
        if (_graphData is null) return;

        var maxDepth = 0;
        foreach (var node in _graphData.Nodes.Values)
            maxDepth = Math.Max(maxDepth, node.Depth);

        if (maxDepth <= 0) return;

        var ringBrush = GetBrush("ThemeBorderSubtleBrush", Brushes.DarkGray);
        var ringPen = new Pen(ringBrush, 0.5) { DashStyle = DashStyle.Dot };
        var maxRadius = Math.Min(bounds.Width, bounds.Height) / 2 - 20;

        for (var d = 1; d <= maxDepth && d <= 5; d++)
        {
            var r = maxRadius * d / (maxDepth + 1);
            ctx.DrawEllipse(null, ringPen, new Point(centerX, centerY), r, r);
        }
    }

    // ----------------------------------------------------------------
    // Input handling
    // ----------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;

        var oldZoom = _zoom;
        var factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        _zoom = Math.Clamp(_zoom * factor, 0.2, 8.0);

        // Zoom toward cursor: adjust pan so the point under the cursor stays fixed
        var ratio = _zoom / oldZoom;
        _panX = pos.X / _zoom - (pos.X / oldZoom - _panX);
        _panY = pos.Y / _zoom - (pos.Y / oldZoom - _panY);

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        // Handle middle-button pan
        if (_isPanning)
        {
            _panX = _panStartX + (pos.X - _panStart.X) / _zoom;
            _panY = _panStartY + (pos.Y - _panStart.Y) / _zoom;
            InvalidateVisual();
            return;
        }

        // Handle node dragging
        if (_draggedNodeId != null && _graphData != null &&
            _graphData.Nodes.TryGetValue(_draggedNodeId, out var dragNode))
        {
            var (wx, wy) = ScreenToWorld(pos);
            dragNode.X = wx;
            dragNode.Y = wy;
            dragNode.Vx = 0;
            dragNode.Vy = 0;

            _engine?.Reheat();
            if (_timer != null && !_timer.IsEnabled)
                _timer.Start();

            InvalidateVisual();
            return;
        }

        var hit = HitTestNode(pos);
        if (hit != _hoveredNodeId)
        {
            // Fire unhover for previous node
            if (_hoveredNodeId != null)
            {
                _log.Verbose("NeuronGraph: NodeUnhovered Id={Id}", _hoveredNodeId);
                NodeUnhovered?.Invoke(_hoveredNodeId);
            }

            _hoveredNodeId = hit;
            Cursor = hit != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;

            // Compute BFS distances from hovered node for highlight
            ComputeHoverDistances();
            EnsureTimerRunning();

            // Fire hover for new node
            if (hit != null)
            {
                var node = _graphData?.Nodes.GetValueOrDefault(hit);
                _log.Debug("NeuronGraph: NodeHovered Id={Id}, Title={Title}, Type={Type}",
                    hit, node?.Title ?? "(unknown)", node?.NodeType ?? "(unknown)");
                NodeHovered?.Invoke(hit);
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        // Middle button → start panning
        if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _focusedNodeId = null;
            _panStart = pos;
            _panStartX = _panX;
            _panStartY = _panY;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        var hit = HitTestNode(pos);

        if (hit != null && _graphData != null && _graphData.Nodes.TryGetValue(hit, out var node))
        {
            _draggedNodeId = hit;
            node.IsDragging = true;
            node.IsPinned = true;
            _dragOffset = pos;
            e.Handled = true;
        }
        else if (props.IsLeftButtonPressed)
        {
            // Left-click on empty space → pan (same as middle button)
            _isPanning = true;
            _focusedNodeId = null;
            _panStart = pos;
            _panStartX = _panX;
            _panStartY = _panY;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            // If the pointer barely moved, treat as a click on empty space → deselect
            var pos = e.GetPosition(this);
            var dx = pos.X - _panStart.X;
            var dy = pos.Y - _panStart.Y;
            if (dx * dx + dy * dy < 9 && _highlightedNodeId != null)
            {
                HighlightedNodeId = null;
                NodeDeselected?.Invoke();
            }

            _isPanning = false;
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        if (_draggedNodeId != null)
        {
            if (_graphData != null && _graphData.Nodes.TryGetValue(_draggedNodeId, out var node))
            {
                node.IsDragging = false;

                var pos = e.GetPosition(this);
                var dx = pos.X - _dragOffset.X;
                var dy = pos.Y - _dragOffset.Y;
                var wasActualDrag = dx * dx + dy * dy >= 9;

                if (!wasActualDrag)
                {
                    // Set highlight in highlight mode (triggers BFS + repaint)
                    if (EnableHighlightMode)
                        HighlightedNodeId = _draggedNodeId;

                    // Smoothly center the viewport on the clicked node
                    FocusOnNode(_draggedNodeId);

                    var hasSubscribers = NodeClicked != null;
                    _log.Debug("NeuronGraph: NodeClicked Id={Id}, Title={Title}, Type={Type}, HasSubscribers={HasSubs}, DragDistance={Dist:F1}px",
                        _draggedNodeId, node.Title, node.NodeType, hasSubscribers, Math.Sqrt(dx * dx + dy * dy));

                    if (hasSubscribers)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        NodeClicked!.Invoke(_draggedNodeId);
                        sw.Stop();
                        _log.Debug("NeuronGraph: NodeClicked handler completed in {Elapsed}ms", sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        _log.Warning("NeuronGraph: NodeClicked has NO subscribers - click will be ignored!");
                    }
                }

                // Unpin so physics can rebalance connections around the new position
                node.IsPinned = false;
                node.Vx = 0;
                node.Vy = 0;

                if (wasActualDrag && _engine != null)
                {
                    // Gentle reheat so neighbours settle without a full explosion
                    _engine.Reheat(0.4);
                }
            }

            _draggedNodeId = null;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && _highlightedNodeId != null)
        {
            HighlightedNodeId = null;
            NodeDeselected?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Hit-test a screen point against nodes, accounting for zoom+pan.
    /// </summary>
    private string? HitTestNode(Point pos)
    {
        if (_graphData is null) return null;

        var bounds = Bounds;
        var cx = bounds.Width / 2;
        var cy = bounds.Height / 2;
        var (scale, gcx, gcy) = ComputeBaseTransform();

        foreach (var node in _graphData.Nodes.Values)
        {
            // Compute base position then apply zoom+pan
            var bx = cx + (node.X - gcx) * scale;
            var by = cy + (node.Y - gcy) * scale;
            var sx = (bx - cx) * _zoom + cx + _panX * _zoom;
            var sy = (by - cy) * _zoom + cy + _panY * _zoom;
            var radius = (node.Radius + 4) * _zoom;
            var dx = pos.X - sx;
            var dy = pos.Y - sy;
            if (dx * dx + dy * dy <= radius * radius)
                return node.Id;
        }

        return null;
    }

    // ----------------------------------------------------------------
    // Highlight helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Compute BFS distances from the hovered node for hover highlight.
    /// </summary>
    private void ComputeHoverDistances()
    {
        var sourceData = _fullGraphData ?? _graphData;
        if (_hoveredNodeId is null || sourceData is null)
        {
            _hoverDistances = null;
            return;
        }

        _hoverDistances = ComputeBfsDistances(_hoveredNodeId, sourceData);
    }

    /// <summary>
    /// Lerp each node's animated opacity toward its target each tick.
    /// Target: 1.0 if in BFS range of hover/highlight, 0.3 if dimmed, 0.8 baseline.
    /// </summary>
    private void UpdateOpacities()
    {
        if (_graphData is null) return;

        // Determine active highlight source: hover takes priority over click-lock
        var activeId = _hoveredNodeId ?? _highlightedNodeId;
        var activeDistances = _hoveredNodeId != null ? _hoverDistances
            : _highlightedNodeId != null ? _highlightDistances
            : null;

        _opacityAnimating = false;
        const double lerpRate = 0.18;

        foreach (var node in _graphData.Nodes.Values)
        {
            double target;
            if (activeId == null)
            {
                target = 0.8;
            }
            else if (activeDistances != null &&
                     activeDistances.TryGetValue(node.Id, out var dist) && dist <= _highlightDepth)
            {
                target = 1.0;
            }
            else
            {
                target = 0.25;
            }

            if (!_nodeOpacities.TryGetValue(node.Id, out var current))
                current = 0.8;

            var next = current + (target - current) * lerpRate;
            if (Math.Abs(next - target) < 0.005) next = target;

            _nodeOpacities[node.Id] = next;
            if (Math.Abs(next - target) > 0.001) _opacityAnimating = true;
        }
    }

    /// <summary>
    /// BFS distance map from a root node in the given graph data.
    /// </summary>
    private static Dictionary<string, int> ComputeBfsDistances(string rootId, GraphData data)
    {
        var adj = new Dictionary<string, List<string>>();
        foreach (var edge in data.Edges)
        {
            if (!adj.TryGetValue(edge.SourceId, out var srcList))
            {
                srcList = [];
                adj[edge.SourceId] = srcList;
            }
            srcList.Add(edge.TargetId);

            if (!adj.TryGetValue(edge.TargetId, out var tgtList))
            {
                tgtList = [];
                adj[edge.TargetId] = tgtList;
            }
            tgtList.Add(edge.SourceId);
        }

        var distances = new Dictionary<string, int>();
        if (!data.Nodes.ContainsKey(rootId)) return distances;

        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        distances[rootId] = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var d = distances[current];
            if (!adj.TryGetValue(current, out var neighbors)) continue;
            foreach (var neighbor in neighbors)
            {
                if (distances.ContainsKey(neighbor)) continue;
                distances[neighbor] = d + 1;
                queue.Enqueue(neighbor);
            }
        }

        return distances;
    }

    /// <summary>
    /// Ensure the render timer is running (needed for opacity animation).
    /// </summary>
    private void EnsureTimerRunning()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
        }
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void ComputeHighlightDistances()
    {
        var sourceData = _fullGraphData ?? _graphData;
        if (_highlightedNodeId is null || sourceData is null)
        {
            _highlightDistances = null;
            return;
        }

        _highlightDistances = ComputeBfsDistances(_highlightedNodeId, sourceData);
    }

    /// <summary>
    /// Despawn or restore nodes based on HideInactiveNodes + highlight state.
    /// When hiding, nodes outside HighlightDepth are removed from _graphData
    /// and the physics engine entirely. When not hiding, _fullGraphData is restored.
    /// </summary>
    private void ApplyActiveNodeFilter()
    {
        if (_fullGraphData is null)
        {
            InvalidateVisual();
            return;
        }

        var shouldFilter = _hideInactiveNodes
            && _highlightedNodeId != null
            && _highlightDistances != null;

        var dataChanged = false;

        if (shouldFilter)
        {
            var filtered = new GraphData();
            foreach (var (id, node) in _fullGraphData.Nodes)
            {
                if (_highlightDistances!.TryGetValue(id, out var dist) && dist <= _highlightDepth)
                    filtered.Nodes[id] = node;
            }
            foreach (var edge in _fullGraphData.Edges)
            {
                if (filtered.Nodes.ContainsKey(edge.SourceId) && filtered.Nodes.ContainsKey(edge.TargetId))
                    filtered.Edges.Add(edge);
            }

            _log.Debug("NeuronGraph: Despawning {Removed} nodes (keeping {Kept} within depth {Depth} of {HighlightId})",
                _fullGraphData.Nodes.Count - filtered.Nodes.Count, filtered.Nodes.Count,
                _highlightDepth, _highlightedNodeId);

            dataChanged = _graphData != filtered;
            _graphData = filtered;
        }
        else
        {
            // Restore full data — nodes retain their last physics positions
            if (_graphData != _fullGraphData)
            {
                _log.Debug("NeuronGraph: Restoring all {Count} nodes", _fullGraphData.Nodes.Count);
                dataChanged = true;
                _graphData = _fullGraphData;
            }
        }

        // Only reheat when the node set actually changed (filter toggled);
        // pure highlight changes (clicking a node) just need a repaint.
        if (dataChanged && _engine != null)
        {
            _engine.SetGraphData(_graphData, preservePositions: true);
            _engine.Reheat(0.3);

            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _timer.Tick += OnTick;
            }
            if (!_timer.IsEnabled)
                _timer.Start();
        }

        InvalidateVisual();
    }

    private static Color BlendColors(Color a, Color b) =>
        Color.FromArgb(
            (byte)((a.A + b.A) / 2),
            (byte)((a.R + b.R) / 2),
            (byte)((a.G + b.G) / 2),
            (byte)((a.B + b.B) / 2));

    private static Color GetNodeColor(string nodeType) =>
        GetNodeBrush(nodeType) is ISolidColorBrush scb ? scb.Color : Colors.Gray;

    // ----------------------------------------------------------------
    // Theme helpers
    // ----------------------------------------------------------------

    private static IBrush GetBrush(string key, IBrush fallback)
    {
        var app = Avalonia.Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b)
            return b;
        return app.FindResource(key) as IBrush ?? fallback;
    }

    private static double GetDouble(string key, double fallback)
    {
        var app = Avalonia.Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }

    private static FontFamily GetFontFamily(string key)
    {
        var app = Avalonia.Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is FontFamily ff)
            return ff;
        return FontFamily.Default;
    }

    private static IBrush GetNodeBrush(string nodeType) => nodeType switch
    {
        "note" or "page"       => GetBrush("ThemeSecondaryBrush", Brushes.MediumPurple),
        "task"                 => GetBrush("ThemeSuccessBrush", Brushes.Green),
        "contact"              => GetBrush("ThemeWarningBrush", Brushes.Orange),
        "event" or "calendar"  => GetBrush("ThemePrimaryBrush", Brushes.DodgerBlue),
        "journal"              => GetBrush("ThemeDangerBrush", Brushes.IndianRed),
        "tag"                  => new SolidColorBrush(Color.Parse("#9CA3AF")),
        "company"              => new SolidColorBrush(Color.Parse("#F97316")),
        "contact_group"        => new SolidColorBrush(Color.Parse("#A855F7")),
        "snippet"              => new SolidColorBrush(Color.Parse("#06B6D4")),
        "rss"                  => new SolidColorBrush(Color.Parse("#FB923C")),
        "project"              => new SolidColorBrush(Color.Parse("#84CC16")),
        "deal"                 => new SolidColorBrush(Color.Parse("#EAB308")),
        "transaction"          => new SolidColorBrush(Color.Parse("#EC4899")),
        "credential"           => new SolidColorBrush(Color.Parse("#EF4444")),
        "file"                 => new SolidColorBrush(Color.Parse("#64748B")),
        _                      => GetBrush("ThemeTextMutedBrush", Brushes.Gray),
    };
}
