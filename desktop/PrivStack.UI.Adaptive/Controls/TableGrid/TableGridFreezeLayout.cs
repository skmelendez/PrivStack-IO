// ============================================================================
// File: TableGridFreezeLayout.cs
// Description: Manages dual-grid layout for frozen column panes. When columns
//              are frozen, renders a fixed-width left grid and a scrollable
//              right grid separated by a visual divider.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridFreezeLayout
{
    private readonly Grid _frozenGrid;
    private readonly Grid _scrollableGrid;
    private readonly ScrollViewer _scrollViewer;
    private readonly DockPanel _container;
    private readonly Border _divider;

    public Grid FrozenGrid => _frozenGrid;
    public Grid ScrollableGrid => _scrollableGrid;
    public ScrollViewer ScrollViewer => _scrollViewer;
    public DockPanel Container => _container;

    private static IBrush GetBrush(string key, IBrush fallback)
    {
        var app = Avalonia.Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b)
            return b;
        return fallback;
    }

    public TableGridFreezeLayout()
    {
        _frozenGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };

        _scrollableGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _scrollableGrid
        };

        _divider = new Border
        {
            Width = 1,
            Background = GetBrush("ThemeBorderSubtleBrush", new SolidColorBrush(Color.Parse("#40888888"))),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _container = new DockPanel();
        DockPanel.SetDock(_frozenGrid, Dock.Left);
        DockPanel.SetDock(_divider, Dock.Left);
        _container.Children.Add(_frozenGrid);
        _container.Children.Add(_divider);
        _container.Children.Add(_scrollViewer);
    }

    /// <summary>
    /// Computes the total pixel width of the frozen region from column widths.
    /// Accounts for drag handle column (col 0) and grip columns (every other col).
    /// </summary>
    public static double ComputeFrozenWidth(
        Grid sourceGrid, int frozenColumnCount)
    {
        if (frozenColumnCount <= 0 || sourceGrid.ColumnDefinitions.Count == 0)
            return 0;

        // Column layout: [dragHandle] [col0] [grip] [col1] [grip] ...
        // Frozen columns 0..frozenColumnCount-1 map to grid columns 1,3,5,...
        double width = 0;

        // Include drag handle column (index 0)
        if (sourceGrid.ColumnDefinitions.Count > 0)
            width += sourceGrid.ColumnDefinitions[0].Width.Value;

        for (var c = 0; c < frozenColumnCount; c++)
        {
            var dataColIdx = c * 2 + 1;
            var gripColIdx = c * 2 + 2;

            if (dataColIdx < sourceGrid.ColumnDefinitions.Count)
                width += sourceGrid.ColumnDefinitions[dataColIdx].Width.Value;
            if (gripColIdx < sourceGrid.ColumnDefinitions.Count)
                width += sourceGrid.ColumnDefinitions[gripColIdx].Width.Value;
        }

        return width;
    }

    public void Clear()
    {
        _frozenGrid.ColumnDefinitions.Clear();
        _frozenGrid.RowDefinitions.Clear();
        _frozenGrid.Children.Clear();
        _scrollableGrid.ColumnDefinitions.Clear();
        _scrollableGrid.RowDefinitions.Clear();
        _scrollableGrid.Children.Clear();
    }
}
