using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.Desktop.Controls;
using PrivStack.Desktop.ViewModels;
using PrivStack.UI.Adaptive.Controls;
using PrivStack.UI.Adaptive.Models;
using PrivStack.UI.Adaptive.Services;

namespace PrivStack.Desktop.Views;

public partial class InfoPanel : UserControl
{
    private NeuronGraphControl? _graphControl;

    // Resize drag state
    private bool _isResizing;
    private Point _resizeStartPoint;
    private double _resizeStartHeight;

    public InfoPanel()
    {
        InitializeComponent();
        Name = "InfoPanelRoot";
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is InfoPanelViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.PhysicsParametersChanged += OnPhysicsParametersChanged;
            UpdateScrollPanelVisibility(vm.HasActiveItem);
            WireAutoCompleteBox();
            WireNewPropertyBox();
            WireResizeHandle();
        }
    }

    private void WireResizeHandle()
    {
        var handle = this.FindControl<Border>("GraphResizeHandle");
        if (handle == null) return;

        handle.PointerPressed += OnResizePointerPressed;
        handle.PointerMoved += OnResizePointerMoved;
        handle.PointerReleased += OnResizePointerReleased;
        handle.PointerCaptureLost += OnResizePointerCaptureLost;
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        if (DataContext is not InfoPanelViewModel vm) return;

        _isResizing = true;
        _resizeStartPoint = e.GetPosition(this);
        _resizeStartHeight = vm.GraphPanelHeight;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing) return;
        if (DataContext is not InfoPanelViewModel vm) return;

        var current = e.GetPosition(this);
        var delta = _resizeStartPoint.Y - current.Y; // dragging up = larger
        var newHeight = Math.Clamp(_resizeStartHeight + delta, 100, 800);
        vm.GraphPanelHeight = newHeight;
        e.Handled = true;
    }

    private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isResizing = false;
    }

    private void WireAutoCompleteBox()
    {
        var autoComplete = this.FindControl<AutoCompleteBox>("TagAutoCompleteBox");
        if (autoComplete == null) return;

        autoComplete.SelectionChanged += OnAutoCompleteSelectionChanged;
        autoComplete.KeyDown += OnAutoCompleteKeyDown;
    }

    private void OnAutoCompleteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not AutoCompleteBox acb) return;
        if (acb.SelectedItem is string selectedTag && DataContext is InfoPanelViewModel vm)
        {
            _ = vm.AddTagFromAutoComplete(selectedTag);
            acb.Text = "";
        }
    }

    private void OnAutoCompleteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not AutoCompleteBox acb) return;
        if (DataContext is not InfoPanelViewModel vm) return;

        var text = acb.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            _ = vm.AddTagFromAutoComplete(text);
            acb.Text = "";
            e.Handled = true;
        }
    }

    private void WireNewPropertyBox()
    {
        var box = this.FindControl<TextBox>("NewPropertyNameBox");
        if (box == null) return;
        box.KeyDown += OnNewPropertyKeyDown;
    }

    private void OnNewPropertyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox tb) return;
        if (DataContext is not InfoPanelViewModel vm) return;

        var name = tb.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            vm.CreatePropertyDefinitionCommand.Execute(name);
            tb.Text = "";
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not InfoPanelViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(InfoPanelViewModel.HasActiveItem):
                UpdateScrollPanelVisibility(vm.HasActiveItem);
                UpdateNoBacklinksVisibility(vm);
                break;
            case nameof(InfoPanelViewModel.HasBacklinks):
                UpdateNoBacklinksVisibility(vm);
                break;
            case nameof(InfoPanelViewModel.IsLinksExpanded):
                UpdateChevronIcon("LinksChevron", vm.IsLinksExpanded);
                break;
            case nameof(InfoPanelViewModel.IsBacklinksExpanded):
                UpdateChevronIcon("BacklinksChevron", vm.IsBacklinksExpanded);
                UpdateNoBacklinksVisibility(vm);
                break;
            case nameof(InfoPanelViewModel.IsPropertiesExpanded):
                UpdateChevronIcon("PropertiesChevron", vm.IsPropertiesExpanded);
                break;
            case nameof(InfoPanelViewModel.GraphNodes):
            case nameof(InfoPanelViewModel.GraphEdges):
                UpdateGraphControl(vm);
                break;
        }
    }

    private void UpdateScrollPanelVisibility(bool hasActiveItem)
    {
        var infoPanel = this.FindControl<ScrollViewer>("InfoScrollPanel");
        if (infoPanel != null)
            infoPanel.IsVisible = hasActiveItem;
    }

    private void UpdateNoBacklinksVisibility(InfoPanelViewModel vm)
    {
        var noBacklinks = this.FindControl<Border>("NoBacklinksMessage");
        if (noBacklinks == null) return;
        noBacklinks.IsVisible = vm.HasActiveItem && !vm.HasBacklinks
                                && !vm.IsLoading && vm.IsBacklinksExpanded;
    }

    private void UpdateChevronIcon(string controlName, bool isExpanded)
    {
        var chevron = this.FindControl<IconControl>(controlName);
        if (chevron == null) return;
        chevron.Icon = isExpanded ? "ChevronDown" : "ChevronRight";
    }

    private void UpdateGraphControl(InfoPanelViewModel vm)
    {
        var graphPanel = this.FindControl<Border>("GraphPanel");
        if (graphPanel == null) return;

        if (vm.GraphNodes == null || vm.GraphNodes.Count == 0)
        {
            graphPanel.Child = null;
            _graphControl = null;
            return;
        }

        if (_graphControl == null)
        {
            _graphControl = new NeuronGraphControl
            {
                Physics = new PhysicsParameters
                {
                    RepelRadius = vm.NeuronRepelRadius,
                    CenterStrength = vm.NeuronCenterForce,
                    LinkDistance = vm.NeuronLinkDistance,
                    LinkStrength = vm.NeuronLinkForce,
                },
            };
            _graphControl.NodeClicked += OnGraphNodeClicked;
            graphPanel.Child = _graphControl;
        }

        _graphControl.CenterId = vm.GraphCenterId;
        _graphControl.Nodes = vm.GraphNodes;
        _graphControl.Edges = vm.GraphEdges;
        _graphControl.Start();
    }

    private void OnPhysicsParametersChanged(object? sender, EventArgs e)
    {
        if (_graphControl == null || DataContext is not InfoPanelViewModel vm) return;
        _graphControl.Physics = new PhysicsParameters
        {
            RepelRadius = vm.NeuronRepelRadius,
            CenterStrength = vm.NeuronCenterForce,
            LinkDistance = vm.NeuronLinkDistance,
            LinkStrength = vm.NeuronLinkForce,
        };
        _graphControl.ApplyPhysicsChanges();
    }

    private void OnGraphNodeClicked(string nodeId)
    {
        if (DataContext is InfoPanelViewModel vm)
        {
            _ = vm.NavigateToGraphNode(nodeId);
        }
    }
}
