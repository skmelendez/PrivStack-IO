// ============================================================================
// File: PluginSidebar.cs
// Description: Reusable collapsible, resizable sidebar control for plugins.
//              Encapsulates collapse toggle, resize handle, and content slots.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// A collapsible, resizable sidebar with header, content, and footer slots.
/// Bind <see cref="IsCollapsed"/> TwoWay to the VM to sync collapse state.
/// </summary>
public sealed class PluginSidebar : Border
{
    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<PluginSidebar, bool>(nameof(IsCollapsed), false,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsCollapsibleProperty =
        AvaloniaProperty.Register<PluginSidebar, bool>(nameof(IsCollapsible), true);

    public static readonly StyledProperty<bool> IsResizableProperty =
        AvaloniaProperty.Register<PluginSidebar, bool>(nameof(IsResizable), true);

    public static readonly StyledProperty<double> CollapsedWidthProperty =
        AvaloniaProperty.Register<PluginSidebar, double>(nameof(CollapsedWidth), 48);

    public static readonly StyledProperty<double> ExpandedWidthProperty =
        AvaloniaProperty.Register<PluginSidebar, double>(nameof(ExpandedWidth), 260);

    public static readonly StyledProperty<double> MinExpandedWidthProperty =
        AvaloniaProperty.Register<PluginSidebar, double>(nameof(MinExpandedWidth), 180);

    public static readonly StyledProperty<double> MaxExpandedWidthProperty =
        AvaloniaProperty.Register<PluginSidebar, double>(nameof(MaxExpandedWidth), 500);

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<PluginSidebar, object?>(nameof(HeaderContent));

    public static readonly StyledProperty<object?> SidebarContentProperty =
        AvaloniaProperty.Register<PluginSidebar, object?>(nameof(SidebarContent));

    public static readonly StyledProperty<bool> IsContentScrollableProperty =
        AvaloniaProperty.Register<PluginSidebar, bool>(nameof(IsContentScrollable), true);

    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<PluginSidebar, object?>(nameof(FooterContent));

    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    public bool IsCollapsible
    {
        get => GetValue(IsCollapsibleProperty);
        set => SetValue(IsCollapsibleProperty, value);
    }

    public bool IsResizable
    {
        get => GetValue(IsResizableProperty);
        set => SetValue(IsResizableProperty, value);
    }

    public double CollapsedWidth
    {
        get => GetValue(CollapsedWidthProperty);
        set => SetValue(CollapsedWidthProperty, value);
    }

    public double ExpandedWidth
    {
        get => GetValue(ExpandedWidthProperty);
        set => SetValue(ExpandedWidthProperty, value);
    }

    public double MinExpandedWidth
    {
        get => GetValue(MinExpandedWidthProperty);
        set => SetValue(MinExpandedWidthProperty, value);
    }

    public double MaxExpandedWidth
    {
        get => GetValue(MaxExpandedWidthProperty);
        set => SetValue(MaxExpandedWidthProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? SidebarContent
    {
        get => GetValue(SidebarContentProperty);
        set => SetValue(SidebarContentProperty, value);
    }

    /// <summary>
    /// When true (default), SidebarContent is wrapped in a ScrollViewer.
    /// Set to false for content that manages its own scrolling (ListBox, TreeView).
    /// </summary>
    public bool IsContentScrollable
    {
        get => GetValue(IsContentScrollableProperty);
        set => SetValue(IsContentScrollableProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    /// <summary>Fired on resize handle release with the new width.</summary>
    public event EventHandler<double>? WidthChanged;

    private const string ChevronRight =
        "M8.59 16.59L13.17 12 8.59 7.41 10 6l6 6-6 6-1.41-1.41z";
    private const string ChevronLeft =
        "M15.41 16.59L10.83 12l4.58-4.59L14 6l-6 6 6 6 1.41-1.41z";

    private readonly PathIcon _chevronIcon;
    private readonly Button _collapseButton;
    private readonly Border _collapseRow;
    private readonly Border _resizeHandle;
    private readonly Border _mainBorder;
    private readonly ContentPresenter _headerPresenter;
    private readonly ContentPresenter _contentPresenter;
    private readonly ContentPresenter _footerPresenter;
    private readonly ScrollViewer _scrollViewer;
    private readonly Border _contentContainer;

    private bool _isResizing;
    private Point _resizeStart;
    private double _resizeStartWidth;

    public PluginSidebar()
    {
        ClipToBounds = true;

        _chevronIcon = new PathIcon
        {
            Width = 16,
            Height = 16,
            Data = StreamGeometry.Parse(ChevronLeft),
        };

        _collapseButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Content = _chevronIcon,
        };
        _collapseButton.Click += (_, _) => IsCollapsed = !IsCollapsed;
        ToolTip.SetTip(_collapseButton, "Collapse sidebar");

        _collapseRow = new Border
        {
            Padding = new Thickness(8, 8, 8, 4),
            Child = _collapseButton,
        };

        _headerPresenter = new ContentPresenter();
        _contentPresenter = new ContentPresenter();
        _footerPresenter = new ContentPresenter();

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _contentContainer = new Border();
        ApplyContentScrollMode();

        var innerPanel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_collapseRow, Dock.Top);
        innerPanel.Children.Add(_collapseRow);
        DockPanel.SetDock(_footerPresenter, Dock.Bottom);
        innerPanel.Children.Add(_footerPresenter);
        DockPanel.SetDock(_headerPresenter, Dock.Top);
        innerPanel.Children.Add(_headerPresenter);
        innerPanel.Children.Add(_contentContainer);

        _mainBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = innerPanel,
        };

        var handlePill = new Border
        {
            Width = 3,
            Height = 28,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        handlePill.Bind(BackgroundProperty,
            handlePill.GetResourceObservable("SystemControlForegroundBaseMediumLowBrush"));

        _resizeHandle = new Border
        {
            Width = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = handlePill,
        };
        _resizeHandle.PointerPressed += OnResizePressed;
        _resizeHandle.PointerMoved += OnResizeMoved;
        _resizeHandle.PointerReleased += OnResizeReleased;

        var outer = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_resizeHandle, Dock.Right);
        outer.Children.Add(_resizeHandle);
        outer.Children.Add(_mainBorder);

        Child = outer;

        Width = ExpandedWidth;
        ApplyCollapsedState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (this.TryGetResource("ThemeSidebarWidth", ActualThemeVariant, out var res)
            && res is double themeWidth
            && GetValue(ExpandedWidthProperty) == 260)
        {
            ExpandedWidth = themeWidth;
        }

        ApplyTheme();
        ActualThemeVariantChanged += OnThemeChanged;
        ApplyCollapsedState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        _mainBorder.Background = GetBrush("ThemeSurfaceBrush", Brushes.Transparent);
        _mainBorder.BorderBrush = GetBrush("ThemeBorderSubtleBrush", Brushes.Gray);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCollapsedProperty)
            ApplyCollapsedState();
        else if (change.Property == IsCollapsibleProperty)
        {
            _collapseRow.IsVisible = IsCollapsible;
            _collapseButton.IsVisible = IsCollapsible;
        }
        else if (change.Property == IsResizableProperty)
            UpdateResizeHandleVisibility();
        else if (change.Property == ExpandedWidthProperty && !IsCollapsed)
            Width = ExpandedWidth;
        else if (change.Property == HeaderContentProperty)
            _headerPresenter.Content = change.GetNewValue<object?>();
        else if (change.Property == SidebarContentProperty)
            _contentPresenter.Content = change.GetNewValue<object?>();
        else if (change.Property == FooterContentProperty)
            _footerPresenter.Content = change.GetNewValue<object?>();
        else if (change.Property == IsContentScrollableProperty)
            ApplyContentScrollMode();
    }

    private void ApplyCollapsedState()
    {
        var collapsed = IsCollapsed;
        Width = collapsed ? CollapsedWidth : ExpandedWidth;
        _headerPresenter.IsVisible = !collapsed;
        _contentContainer.IsVisible = !collapsed;
        _footerPresenter.IsVisible = !collapsed;
        UpdateResizeHandleVisibility();
        _chevronIcon.Data = StreamGeometry.Parse(collapsed ? ChevronRight : ChevronLeft);
        ToolTip.SetTip(_collapseButton,
            collapsed ? "Expand sidebar" : "Collapse sidebar");
    }

    private void ApplyContentScrollMode()
    {
        _scrollViewer.Content = null;
        _contentContainer.Child = null;

        if (IsContentScrollable)
        {
            _scrollViewer.Content = _contentPresenter;
            _contentContainer.Child = _scrollViewer;
        }
        else
        {
            _contentContainer.Child = _contentPresenter;
        }
    }

    private void UpdateResizeHandleVisibility()
    {
        _resizeHandle.IsVisible = IsResizable && !IsCollapsed;
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle) return;
        _isResizing = true;
        _resizeStart = e.GetPosition(this);
        _resizeStartWidth = Width;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing) return;
        var current = e.GetPosition(this);
        var deltaX = current.X - _resizeStart.X;
        Width = Math.Clamp(
            _resizeStartWidth + deltaX,
            MinExpandedWidth,
            MaxExpandedWidth);
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        ExpandedWidth = Width;
        WidthChanged?.Invoke(this, Width);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private IBrush? GetBrush(string key, IBrush? fallback)
    {
        if (this.TryGetResource(key, ActualThemeVariant, out var obj) && obj is IBrush brush)
            return brush;
        var app = Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var appObj) == true
            && appObj is IBrush appBrush)
            return appBrush;
        return fallback;
    }
}
