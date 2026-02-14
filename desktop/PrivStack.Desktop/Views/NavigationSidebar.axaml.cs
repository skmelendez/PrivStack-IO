using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Controls;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Views;

public partial class NavigationSidebar : UserControl
{
    // Timer popup hover state
    private DispatcherTimer? _timerPopupCloseTimer;
    private bool _isPointerOverTimerIndicator;
    private bool _isPointerOverTimerPopup;

    // Prefetch service for hover-based view state preloading
    private ViewStatePrefetchService? _prefetchService;

    // Drag state
    private Border? _draggedWrapper;
    private NavigationItem? _draggedItem;
    private Point _dragStartPoint;
    private bool _isDragging;
    private bool _pointerDown;
    private int _draggedIndex = -1;
    private int _currentDropIndex = -1;
    private const double DragThreshold = 8;

    // Drag ghost
    private Border? _dragGhost;
    private Canvas? _dragOverlay;

    // Cached item containers for shift animation
    private readonly List<(Control container, int index)> _containerCache = new();
    private readonly Dictionary<int, double> _appliedShifts = new();
    private static readonly ITransform IdentityTransform = TransformOperations.Parse("translateY(0px)");

    public NavigationSidebar()
    {
        InitializeComponent();

        // Tunnel routing: intercept pointer events before the Button swallows them
        AddHandler(PointerPressedEvent, OnNavItemPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnNavItemPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnNavItemPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble);

        // Hover prefetch: preload view state when hovering over nav items
        AddHandler(PointerEnteredEvent, OnNavItemPointerEntered, RoutingStrategies.Tunnel);
        AddHandler(PointerExitedEvent, OnNavItemPointerExited, RoutingStrategies.Tunnel);
    }

    private void OnNavItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Walk visual tree from event source to find the nav-item-wrapper Border
        var wrapper = FindNavItemWrapper(e.Source as Visual);
        if (wrapper == null || wrapper.DataContext is not NavigationItem navItem)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggedWrapper = wrapper;
        _draggedItem = navItem;
        _dragStartPoint = e.GetPosition(this);
        _draggedIndex = GetNavItemIndex(navItem);
        _pointerDown = true;
        _isDragging = false;

        // Do NOT mark handled — let the Button still process clicks
    }

    private void OnNavItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown || _draggedWrapper == null || _draggedItem == null)
            return;

        var currentPoint = e.GetPosition(this);
        var dy = currentPoint.Y - _dragStartPoint.Y;
        var dx = currentPoint.X - _dragStartPoint.X;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (!_isDragging && distance > DragThreshold)
        {
            _isDragging = true;
            _draggedWrapper.Classes.Add("dragging");
            e.Pointer.Capture(this);

            CacheItemContainers();
            CreateDragGhost();
        }

        if (_isDragging)
        {
            UpdateDragGhostPosition(currentPoint);
            UpdateItemShifts(currentPoint);

            // Suppress Button click while dragging
            e.Handled = true;
        }
    }

    private void OnNavItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _draggedItem != null && _currentDropIndex >= 0 && _currentDropIndex != _draggedIndex)
        {
            App.Services.GetRequiredService<IPluginRegistry>().MoveNavigationItem(_draggedIndex, _currentDropIndex);
            e.Handled = true;

            // Brief "pop" settle animation on the landed item
            ScheduleSettleAnimation(_currentDropIndex);
        }

        CleanupDragState(e.Pointer);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Safety net: if capture is lost unexpectedly, clean up
        if (_isDragging)
        {
            CleanupDragState(null);
        }
    }

    // --- Visual tree helpers ---

    private static Border? FindNavItemWrapper(Visual? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Border border && border.Classes.Contains("nav-item-wrapper"))
                return border;
            current = current.GetVisualParent();
        }
        return null;
    }

    private static int GetNavItemIndex(NavigationItem item)
    {
        var navItems = App.Services.GetRequiredService<IPluginRegistry>().NavigationItems;
        for (int i = 0; i < navItems.Count; i++)
        {
            if (navItems[i] == item)
                return i;
        }
        return -1;
    }

    // --- Item container cache ---

    private void CacheItemContainers()
    {
        _containerCache.Clear();
        var itemsControl = GetActiveItemsControl();
        if (itemsControl == null) return;

        var navItems = App.Services.GetRequiredService<IPluginRegistry>().NavigationItems;
        for (int i = 0; i < navItems.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container is Control control)
            {
                _containerCache.Add((control, i));
            }
        }
    }

    private ItemsControl? GetActiveItemsControl()
    {
        var expanded = this.FindControl<ItemsControl>("ExpandedNavItems");
        var collapsed = this.FindControl<ItemsControl>("CollapsedNavItems");
        return expanded?.IsVisible == true ? expanded : collapsed;
    }

    // --- Drag ghost ---

    private void CreateDragGhost()
    {
        _dragOverlay = this.FindControl<Canvas>("DragOverlay");
        if (_dragOverlay == null || _draggedItem == null) return;

        // Build ghost content matching the nav item
        var icon = new IconControl
        {
            Icon = _draggedItem.Icon,
            Width = 18,
            Height = 18,
            StrokeThickness = 1.5
        };
        // Apply theme stroke so icon is visible in ghost
        icon.SetValue(IconControl.StrokeProperty,
            this.FindResource("ThemeTextPrimaryBrush") as IBrush
            ?? Brushes.White);

        var label = new TextBlock
        {
            Text = _draggedItem.DisplayName,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = this.FindResource("ThemeFontSizeSmMd") is double fs ? fs : 13,
            Foreground = this.FindResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White
        };

        var stack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { icon, label }
        };

        _dragGhost = new Border
        {
            Classes = { "drag-ghost" },
            Child = stack,
            Width = _draggedWrapper?.Bounds.Width ?? 180,
            IsHitTestVisible = false
        };

        _dragOverlay.Children.Add(_dragGhost);

        // Initial position
        var pos = _dragStartPoint;
        Canvas.SetLeft(_dragGhost, pos.X + 8);
        Canvas.SetTop(_dragGhost, pos.Y - 20);
    }

    private void UpdateDragGhostPosition(Point pointerPos)
    {
        if (_dragGhost == null) return;
        Canvas.SetLeft(_dragGhost, pointerPos.X + 8);
        Canvas.SetTop(_dragGhost, pointerPos.Y - 20);
    }

    // --- Animated item shifting ---

    private void UpdateItemShifts(Point pointerPos)
    {
        if (_containerCache.Count == 0 || _draggedIndex < 0) return;

        // Determine the height of a single nav item
        double itemHeight = 0;
        foreach (var (container, _) in _containerCache)
        {
            if (container.Bounds.Height > 0)
            {
                itemHeight = container.Bounds.Height;
                break;
            }
        }
        if (itemHeight <= 0) return;

        // Find which slot the pointer is over
        int targetIndex = _draggedIndex;
        for (int i = 0; i < _containerCache.Count; i++)
        {
            var (container, _) = _containerCache[i];
            var containerTop = container.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
            // Account for any current shift when testing midpoint
            if (_appliedShifts.TryGetValue(i, out var currentShift))
                containerTop -= currentShift;
            var containerMid = containerTop + container.Bounds.Height / 2;

            if (pointerPos.Y < containerMid)
            {
                targetIndex = i;
                break;
            }
            else if (i == _containerCache.Count - 1)
            {
                targetIndex = _containerCache.Count - 1;
            }
        }

        // Compute effective drop index (accounting for the dragged item being removed)
        _currentDropIndex = targetIndex;
        if (targetIndex > _draggedIndex)
        {
            // When moving down, the items between drag source and target shift up
        }
        else if (targetIndex < _draggedIndex)
        {
            // When moving up, the items between target and drag source shift down
        }

        // Apply shift transforms to non-dragged items
        for (int i = 0; i < _containerCache.Count; i++)
        {
            var (container, _) = _containerCache[i];
            if (i == _draggedIndex) continue;

            double shiftY = 0;

            if (_draggedIndex < targetIndex)
            {
                // Dragging downward: items between (draggedIndex, targetIndex] shift up
                if (i > _draggedIndex && i <= targetIndex)
                    shiftY = -itemHeight;
            }
            else if (_draggedIndex > targetIndex)
            {
                // Dragging upward: items between [targetIndex, draggedIndex) shift down
                if (i >= targetIndex && i < _draggedIndex)
                    shiftY = itemHeight;
            }

            _appliedShifts[i] = shiftY;
            container.RenderTransform = shiftY == 0
                ? IdentityTransform
                : TransformOperations.Parse(
                    string.Format(CultureInfo.InvariantCulture, "translateY({0}px)", shiftY));
        }
    }

    // --- Drop settle animation ---

    private async void ScheduleSettleAnimation(int landedIndex)
    {
        // Wait for the collection to re-render after MoveNavigationItem
        await System.Threading.Tasks.Task.Delay(50);

        var itemsControl = GetActiveItemsControl();
        if (itemsControl == null) return;

        var container = itemsControl.ContainerFromIndex(landedIndex);
        if (container is not Control control) return;

        // Pop: scale up briefly, then back to normal
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        control.RenderTransform = TransformOperations.Parse("scale(1.06)");

        await System.Threading.Tasks.Task.Delay(150);
        control.RenderTransform = IdentityTransform;
    }

    // --- Cleanup ---

    private void CleanupDragState(IPointer? pointer)
    {
        if (_draggedWrapper != null)
        {
            _draggedWrapper.Classes.Remove("dragging");
        }

        // Remove ghost
        if (_dragOverlay != null && _dragGhost != null)
        {
            _dragOverlay.Children.Remove(_dragGhost);
        }

        // Reset all item shifts
        foreach (var (container, _) in _containerCache)
        {
            container.RenderTransform = IdentityTransform;
        }

        // Only release capture if we captured it (during drag).
        // Releasing unconditionally kills the Button's internal capture and blocks clicks.
        if (_isDragging && pointer != null)
        {
            pointer.Capture(null);
        }

        _dragGhost = null;
        _dragOverlay = null;
        _draggedWrapper = null;
        _draggedItem = null;
        _isDragging = false;
        _pointerDown = false;
        _draggedIndex = -1;
        _currentDropIndex = -1;
        _containerCache.Clear();
        _appliedShifts.Clear();
    }

    // --- Collapsed timer hover popup ---

    private void OnCollapsedTimerPointerEnter(object? sender, PointerEventArgs e)
    {
        _isPointerOverTimerIndicator = true;
        _timerPopupCloseTimer?.Stop();
        var popup = this.FindControl<Popup>("CollapsedTimerPopup");
        if (popup != null)
            popup.IsOpen = true;
    }

    private void OnCollapsedTimerPointerLeave(object? sender, PointerEventArgs e)
    {
        _isPointerOverTimerIndicator = false;
        ScheduleTimerPopupClose();
    }

    private void OnTimerPopupPointerEnter(object? sender, PointerEventArgs e)
    {
        _isPointerOverTimerPopup = true;
        _timerPopupCloseTimer?.Stop();
    }

    private void OnTimerPopupPointerLeave(object? sender, PointerEventArgs e)
    {
        _isPointerOverTimerPopup = false;
        ScheduleTimerPopupClose();
    }

    private void ScheduleTimerPopupClose()
    {
        _timerPopupCloseTimer?.Stop();
        _timerPopupCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timerPopupCloseTimer.Tick += (_, _) =>
        {
            _timerPopupCloseTimer.Stop();
            if (!_isPointerOverTimerIndicator && !_isPointerOverTimerPopup)
            {
                var popup = this.FindControl<Popup>("CollapsedTimerPopup");
                if (popup != null)
                    popup.IsOpen = false;
            }
        };
        _timerPopupCloseTimer.Start();
    }

    // --- Hover prefetch for nav items ---

    private void OnNavItemPointerEntered(object? sender, PointerEventArgs e)
    {
        var wrapper = FindNavItemWrapper(e.Source as Visual);
        if (wrapper?.DataContext is not NavigationItem navItem)
            return;

        // Get prefetch service lazily (avoid DI lookup in constructor)
        _prefetchService ??= App.Services.GetService<ViewStatePrefetchService>();
        _prefetchService?.RequestPrefetch(navItem.Id);
    }

    private void OnNavItemPointerExited(object? sender, PointerEventArgs e)
    {
        var wrapper = FindNavItemWrapper(e.Source as Visual);
        if (wrapper?.DataContext is not NavigationItem navItem)
            return;

        // Cancel pending prefetch (but don't evict cached data)
        _prefetchService?.CancelPrefetch(navItem.Id);
    }
}
