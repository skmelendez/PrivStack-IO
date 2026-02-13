// ============================================================================
// File: TableGrid.cs
// Description: Main TableGrid control. A generic, configurable data grid for
//              PrivStack plugins. Delegates data fetching to ITableGridDataSource
//              and renders via TableGridRenderer. Supports sorting, paging,
//              column resize, and theme-aware styling.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

public sealed class TableGrid : Border
{
    // ── StyledProperties ──────────────────────────────────────────────────

    public static readonly StyledProperty<ITableGridDataSource?> DataSourceProperty =
        AvaloniaProperty.Register<TableGrid, ITableGridDataSource?>(nameof(DataSource));

    public static readonly StyledProperty<string?> FilterTextProperty =
        AvaloniaProperty.Register<TableGrid, string?>(nameof(FilterText));

    public static readonly StyledProperty<int> PageSizeProperty =
        AvaloniaProperty.Register<TableGrid, int>(nameof(PageSize), 10);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<TableGrid, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<bool> ShowPagingProperty =
        AvaloniaProperty.Register<TableGrid, bool>(nameof(ShowPaging), true);

    public ITableGridDataSource? DataSource
    {
        get => GetValue(DataSourceProperty);
        set => SetValue(DataSourceProperty, value);
    }

    public string? FilterText
    {
        get => GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    public int PageSize
    {
        get => GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool ShowPaging
    {
        get => GetValue(ShowPagingProperty);
        set => SetValue(ShowPagingProperty, value);
    }

    // ── Events ────────────────────────────────────────────────────────────

    public event Action<int>? BoundaryEscapeRequested;
    public event Action? DataChanged;

    // ── Internal state ────────────────────────────────────────────────────

    private readonly Grid _grid;
    private readonly ScrollViewer _scrollViewer;
    private readonly TableGridPagingBar _pagingBar;
    private readonly Border _errorBorder;
    private readonly TextBlock _errorText;
    private readonly TableGridSortState _sortState = new();
    private readonly TableGridColumnResize _columnResize;

    private int _currentPage;
    private int _rebuildVersion;
    private TablePagingInfo _lastPaging = new()
    {
        CurrentPage = 0, TotalPages = 1, TotalFilteredRows = 0, PageSize = 10
    };

    public TableGrid()
    {
        _grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _grid
        };

        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C42B1C")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        _errorBorder = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#20C42B1C")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Child = _errorText
        };

        _pagingBar = new TableGridPagingBar();
        _pagingBar.PrevPageRequested += OnPrevPage;
        _pagingBar.NextPageRequested += OnNextPage;

        _columnResize = new TableGridColumnResize(
            () => _grid,
            () => _lastPaging.PageSize > 0 ? GetColumnCount() : 0,
            OnColumnWidthsCommitted);

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(_errorBorder);

        var gridBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = _scrollViewer
        };
        root.Children.Add(gridBorder);
        root.Children.Add(_pagingBar);

        Child = root;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Rebuild();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataSourceProperty)
        {
            _sortState.Reset();
            _currentPage = 0;
            Rebuild();
        }
        else if (change.Property == FilterTextProperty)
        {
            _currentPage = 0;
            Rebuild();
        }
        else if (change.Property == PageSizeProperty)
        {
            _currentPage = 0;
            Rebuild();
        }
        else if (change.Property == ShowPagingProperty)
        {
            _pagingBar.IsVisible = change.GetNewValue<bool>();
        }
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void Rebuild() => RebuildAsync();

    public void FocusEdge(int direction) =>
        BoundaryEscapeRequested?.Invoke(direction);

    // ── Async rebuild ─────────────────────────────────────────────────────

    private async void RebuildAsync()
    {
        var source = DataSource;
        if (source == null)
        {
            _grid.ColumnDefinitions.Clear();
            _grid.RowDefinitions.Clear();
            _grid.Children.Clear();
            _errorBorder.IsVisible = false;
            return;
        }

        var version = ++_rebuildVersion;
        var pageSize = Math.Clamp(PageSize, 1, 100);

        try
        {
            var (data, paging) = await source.GetDataAsync(
                FilterText, _sortState.ColumnIndex, _sortState.Direction,
                _currentPage, pageSize);

            if (version != _rebuildVersion) return;

            _lastPaging = paging;
            _currentPage = paging.CurrentPage;

            if (!string.IsNullOrEmpty(data.ErrorMessage))
            {
                ShowError(data.ErrorMessage);
                _pagingBar.Update(paging);
                return;
            }

            ClearError();

            TableGridRenderer.RenderGrid(
                _grid, data, paging, _sortState,
                source.SupportsSorting,
                OnHeaderClick,
                (col, e) => _columnResize.OnGripPressed(col, e, this),
                e => _columnResize.OnGripMoved(e, this),
                e => _columnResize.OnGripReleased(e),
                this);

            _pagingBar.Update(paging);
            _pagingBar.IsVisible = ShowPaging;
        }
        catch (Exception ex)
        {
            if (version != _rebuildVersion) return;
            ShowError(ex.Message);
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private void OnHeaderClick(int colIndex)
    {
        _sortState.OnHeaderClick(colIndex);
        _currentPage = 0;
        Rebuild();
    }

    private void OnPrevPage()
    {
        if (_currentPage > 0)
        {
            _currentPage--;
            Rebuild();
        }
    }

    private void OnNextPage()
    {
        if (_currentPage < _lastPaging.TotalPages - 1)
        {
            _currentPage++;
            Rebuild();
        }
    }

    private void OnColumnWidthsCommitted(IReadOnlyList<double> widths) =>
        DataSource?.OnColumnWidthsChanged(widths);

    // ── Helpers ────────────────────────────────────────────────────────────

    private int GetColumnCount()
    {
        // Count content columns: every other column after col 0
        var count = 0;
        for (var i = 1; i < _grid.ColumnDefinitions.Count; i += 2)
            count++;
        return count;
    }

    private void ShowError(string message)
    {
        _errorBorder.IsVisible = true;
        _errorText.Text = message;
    }

    private void ClearError() => _errorBorder.IsVisible = false;
}
