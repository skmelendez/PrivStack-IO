using Avalonia.Controls;
using PrivStack.Desktop.Plugins.Graph.ViewModels;
using PrivStack.UI.Adaptive.Controls;
using PrivStack.UI.Adaptive.Models;
using PrivStack.UI.Adaptive.Services;
using AdaptiveGraphData = PrivStack.UI.Adaptive.Models.GraphData;
using AdaptiveGraphNode = PrivStack.UI.Adaptive.Models.GraphNode;
using AdaptiveGraphEdge = PrivStack.UI.Adaptive.Models.GraphEdge;
using PluginGraphData = PrivStack.Desktop.Plugins.Graph.Models.GraphData;
using PluginNodeType = PrivStack.Desktop.Plugins.Graph.Models.NodeType;
using PluginEdgeType = PrivStack.Desktop.Plugins.Graph.Models.EdgeType;

namespace PrivStack.Desktop.Plugins.Graph.Views;

public partial class GraphView : UserControl
{
    private NeuronGraphControl? _graphControl;
    private GraphViewModel? _vm;

    public GraphView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old VM
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.RequestReheat -= OnRequestReheat;
            _vm.RequestResetView -= OnRequestResetView;
            _vm.PhysicsParametersChanged -= OnPhysicsChanged;
        }

        if (DataContext is not GraphViewModel vm) return;
        _vm = vm;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.RequestReheat += OnRequestReheat;
        _vm.RequestResetView += OnRequestResetView;
        _vm.PhysicsParametersChanged += OnPhysicsChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm == null) return;

        if (e.PropertyName == nameof(GraphViewModel.GraphData))
            UpdateGraphCanvas();

        if (e.PropertyName == nameof(GraphViewModel.HighlightDepth) && _graphControl != null)
            _graphControl.HighlightDepth = _vm.HighlightDepth;

        if (e.PropertyName == nameof(GraphViewModel.HideInactiveNodes) && _graphControl != null)
            _graphControl.HideInactiveNodes = _vm.HideInactiveNodes;
    }

    private void UpdateGraphCanvas()
    {
        if (_vm?.GraphData == null || _vm.GraphData.NodeCount == 0)
        {
            var host = this.FindControl<Border>("GraphCanvasHost");
            if (host != null) host.Child = null;
            _graphControl = null;

            var emptyText = this.FindControl<TextBlock>("EmptyStateText");
            if (emptyText != null) emptyText.IsVisible = !(_vm?.IsLoading ?? false);
            return;
        }

        var emptyState = this.FindControl<TextBlock>("EmptyStateText");
        if (emptyState != null) emptyState.IsVisible = false;

        var adaptiveData = ConvertToAdaptiveGraphData(_vm.GraphData, _vm.CenterNodeId);

        EnsureGraphControl();

        if (_graphControl == null) return;

        _graphControl.CenterId = _vm.CenterNodeId;
        _graphControl.HighlightDepth = _vm.HighlightDepth;
        _graphControl.HideInactiveNodes = _vm.HideInactiveNodes;
        _graphControl.Physics = new PhysicsParameters
        {
            CenterStrength = _vm.CenterStrength,
            RepulsionStrength = _vm.RepulsionStrength,
            LinkStrength = _vm.LinkStrength,
            LinkDistance = _vm.LinkDistance,
        };
        _graphControl.StartWithData(adaptiveData);
    }

    private void EnsureGraphControl()
    {
        if (_graphControl != null) return;

        var host = this.FindControl<Border>("GraphCanvasHost");
        if (host == null) return;

        _graphControl = new NeuronGraphControl();
        _graphControl.EnableHighlightMode = true;
        _graphControl.NodeClicked += OnNodeClicked;
        _graphControl.NodeDeselected += OnNodeDeselected;
        host.Child = _graphControl;
    }

    private void OnNodeClicked(string nodeId)
    {
        _vm?.OnNodeClicked(nodeId);
    }

    private void OnNodeDeselected()
    {
        _vm?.OnNodeDeselected();
    }

    private void OnRequestReheat(object? sender, EventArgs e)
    {
        // NeuronGraphControl doesn't expose a reheat, but we can restart
        UpdateGraphCanvas();
    }

    private void OnRequestResetView(object? sender, EventArgs e)
    {
        // Destroy and rebuild the control to reset all state
        var host = this.FindControl<Border>("GraphCanvasHost");
        if (host != null) host.Child = null;
        _graphControl = null;
    }

    private void OnPhysicsChanged(object? sender, EventArgs e)
    {
        if (_graphControl == null || _vm == null) return;
        _graphControl.Physics = new PhysicsParameters
        {
            CenterStrength = _vm.CenterStrength,
            RepulsionStrength = _vm.RepulsionStrength,
            LinkStrength = _vm.LinkStrength,
            LinkDistance = _vm.LinkDistance,
        };
        _graphControl.ApplyPhysicsChanges();
    }

    private static AdaptiveGraphData ConvertToAdaptiveGraphData(PluginGraphData pluginData, string? centerId)
    {
        var data = new AdaptiveGraphData();

        // For global view (no center), pick the most-connected node as BFS root
        // so nodes get spread into depth rings instead of all piling at origin
        var bfsRoot = centerId;
        if (string.IsNullOrEmpty(bfsRoot) || !pluginData.Nodes.ContainsKey(bfsRoot))
        {
            bfsRoot = pluginData.Nodes.Count > 0
                ? pluginData.Nodes.OrderByDescending(kv => kv.Value.LinkCount).First().Key
                : null;
        }

        // Compute depths from root via BFS
        var depths = new Dictionary<string, int>();
        if (bfsRoot != null && pluginData.Nodes.ContainsKey(bfsRoot))
        {
            var queue = new Queue<string>();
            queue.Enqueue(bfsRoot);
            depths[bfsRoot] = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var currentDepth = depths[current];
                foreach (var neighbor in pluginData.GetNeighbors(current))
                {
                    if (depths.ContainsKey(neighbor)) continue;
                    depths[neighbor] = currentDepth + 1;
                    queue.Enqueue(neighbor);
                }
            }
        }

        // Disconnected nodes (no path from root) get max depth + 1
        var maxDepth = depths.Count > 0 ? depths.Values.Max() + 1 : 1;

        foreach (var (id, node) in pluginData.Nodes)
        {
            data.Nodes[id] = new AdaptiveGraphNode
            {
                Id = id,
                Title = node.Title,
                NodeType = NodeTypeToString(node.NodeType),
                LinkType = node.LinkType,
                LinkCount = node.LinkCount,
                Depth = depths.GetValueOrDefault(id, maxDepth),
            };
        }

        foreach (var edge in pluginData.Edges)
        {
            if (!data.Nodes.ContainsKey(edge.SourceId) || !data.Nodes.ContainsKey(edge.TargetId))
                continue;

            data.Edges.Add(new AdaptiveGraphEdge
            {
                SourceId = edge.SourceId,
                TargetId = edge.TargetId,
                EdgeType = EdgeTypeToString(edge.Type),
            });
        }

        return data;
    }

    private static string NodeTypeToString(PluginNodeType nodeType) => nodeType switch
    {
        PluginNodeType.Note => "note",
        PluginNodeType.Task => "task",
        PluginNodeType.Contact => "contact",
        PluginNodeType.Event => "event",
        PluginNodeType.Journal => "journal",
        PluginNodeType.Company => "company",
        PluginNodeType.RssArticle => "rss",
        PluginNodeType.Snippet => "snippet",
        PluginNodeType.Tag => "tag",
        PluginNodeType.Project => "project",
        PluginNodeType.Deal => "deal",
        PluginNodeType.Transaction => "transaction",
        PluginNodeType.Credential => "credential",
        PluginNodeType.File => "file",
        PluginNodeType.WikiSource => "wiki_source",
        _ => "note",
    };

    private static string EdgeTypeToString(PluginEdgeType edgeType) => edgeType switch
    {
        PluginEdgeType.WikiLink => "link",
        PluginEdgeType.Backlink => "backlink",
        PluginEdgeType.TagRelation => "tag",
        PluginEdgeType.ProjectMembership => "parent",
        PluginEdgeType.ParentChild => "parent",
        PluginEdgeType.WikiSourceMembership => "parent",
        _ => "link",
    };
}
