using Avalonia.Controls;
using PrivStack.Desktop.Plugins.Graph.ViewModels;
using PrivStack.UI.Adaptive.Controls;
using PrivStack.UI.Adaptive.Services;

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

        // Assign BFS depths for the layout engine
        _vm.GraphData.AssignBfsDepths(_vm.CenterNodeId);

        EnsureGraphControl();

        if (_graphControl == null) return;

        _graphControl.CenterId = _vm.CenterNodeId;
        _graphControl.HighlightDepth = _vm.HighlightDepth;
        _graphControl.HideInactiveNodes = _vm.HideInactiveNodes;
        _graphControl.Physics = new PhysicsParameters
        {
            RepelRadius = _vm.RepelRadius,
            CenterStrength = _vm.CenterForce,
            LinkDistance = _vm.LinkDistance,
            LinkStrength = _vm.LinkForce,
        };
        _graphControl.StartWithData(_vm.GraphData);
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
        UpdateGraphCanvas();
    }

    private void OnRequestResetView(object? sender, EventArgs e)
    {
        var host = this.FindControl<Border>("GraphCanvasHost");
        if (host != null) host.Child = null;
        _graphControl = null;
    }

    private void OnPhysicsChanged(object? sender, EventArgs e)
    {
        if (_graphControl == null || _vm == null) return;
        _graphControl.Physics = new PhysicsParameters
        {
            RepelRadius = _vm.RepelRadius,
            CenterStrength = _vm.CenterForce,
            LinkDistance = _vm.LinkDistance,
            LinkStrength = _vm.LinkForce,
        };
        _graphControl.ApplyPhysicsChanges();
    }
}
