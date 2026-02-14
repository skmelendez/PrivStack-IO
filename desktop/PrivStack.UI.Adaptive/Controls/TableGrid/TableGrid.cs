// ============================================================================
// File: TableGrid.cs
// Description: Main TableGrid control. A generic, configurable data grid for
//              PrivStack plugins. Delegates data fetching to ITableGridDataSource
//              and renders via TableGridRenderer. Supports sorting, paging,
//              column resize, editing, drag-drop reorder, context menus,
//              filter bar, toolbar with options gear, striping, color themes,
//              and export.
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

    public static readonly StyledProperty<bool> ShowToolbarProperty =
        AvaloniaProperty.Register<TableGrid, bool>(nameof(ShowToolbar), true);

    public static readonly StyledProperty<bool> IsStripedProperty =
        AvaloniaProperty.Register<TableGrid, bool>(nameof(IsStriped));

    public static readonly StyledProperty<bool> ShowFilterProperty =
        AvaloniaProperty.Register<TableGrid, bool>(nameof(ShowFilter), true);

    public static readonly StyledProperty<string?> ColorThemeProperty =
        AvaloniaProperty.Register<TableGrid, string?>(nameof(ColorTheme));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TableGrid, string?>(nameof(Title));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<TableGrid, string?>(nameof(Caption));

    public static readonly StyledProperty<int> FrozenColumnCountProperty =
        AvaloniaProperty.Register<TableGrid, int>(nameof(FrozenColumnCount));

    public static readonly StyledProperty<int> FrozenRowCountProperty =
        AvaloniaProperty.Register<TableGrid, int>(nameof(FrozenRowCount));

    public static readonly StyledProperty<TableScrollMode> ScrollModeProperty =
        AvaloniaProperty.Register<TableGrid, TableScrollMode>(nameof(ScrollMode));

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

    public bool ShowToolbar
    {
        get => GetValue(ShowToolbarProperty);
        set => SetValue(ShowToolbarProperty, value);
    }

    public bool IsStriped
    {
        get => GetValue(IsStripedProperty);
        set => SetValue(IsStripedProperty, value);
    }

    public bool ShowFilter
    {
        get => GetValue(ShowFilterProperty);
        set => SetValue(ShowFilterProperty, value);
    }

    public string? ColorTheme
    {
        get => GetValue(ColorThemeProperty);
        set => SetValue(ColorThemeProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public int FrozenColumnCount
    {
        get => GetValue(FrozenColumnCountProperty);
        set => SetValue(FrozenColumnCountProperty, value);
    }

    public int FrozenRowCount
    {
        get => GetValue(FrozenRowCountProperty);
        set => SetValue(FrozenRowCountProperty, value);
    }

    public TableScrollMode ScrollMode
    {
        get => GetValue(ScrollModeProperty);
        set => SetValue(ScrollModeProperty, value);
    }

    // ── Events ────────────────────────────────────────────────────────────

    public event Action<int>? BoundaryEscapeRequested;
    public event Action? DataChanged;
    public event Action<string?>? FilterTextCommitted;
    public event Action<int>? PageSizeCommitted;
    public event Action<bool>? IsStripedCommitted;
    public event Action? EditTableRequested;
    public event Action<int>? FrozenColumnCountCommitted;
    public event Action<int>? FrozenRowCountCommitted;
    public event Action<TableScrollMode>? ScrollModeCommitted;

    // ── Internal state ────────────────────────────────────────────────────

    private readonly Grid _grid;
    private readonly ScrollViewer _scrollViewer;
    private readonly TableGridPagingBar _pagingBar;
    private readonly Border _errorBorder;
    private readonly TextBlock _errorText;
    private readonly TableGridSortState _sortState = new();
    private readonly TableGridColumnResize _columnResize;
    private readonly TableGridCellNavigation _cellNavigation;
    private readonly TableGridToolbar _toolbar;
    private readonly TableGridFilterBar _filterBar;
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _captionBlock;

    private TableGridRowDrag? _rowDrag;
    private TableGridColumnDrag? _columnDrag;
    private readonly TableGridInfiniteScroll _infiniteScroll;
    private int _lastDataRowCount;
    private int _lastDataRowStartGridRow;

    private int _currentPage;
    private int _rebuildVersion;
    private bool _isScrollTriggeredRebuild;
    private TablePagingInfo _lastPaging = new()
    {
        CurrentPage = 0, TotalPages = 1, TotalFilteredRows = 0, PageSize = 10
    };

    /// <summary>
    /// Access the toolbar's additional menu items for plugin-specific entries.
    /// </summary>
    public List<MenuItem> AdditionalMenuItems => _toolbar.AdditionalMenuItems;

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

        _filterBar = new TableGridFilterBar();
        _filterBar.FilterTextChanged += OnFilterBarTextChanged;
        _filterBar.PageSizeChanged += OnFilterBarPageSizeChanged;

        _pagingBar = new TableGridPagingBar();
        _pagingBar.PrevPageRequested += OnPrevPage;
        _pagingBar.NextPageRequested += OnNextPage;

        _columnResize = new TableGridColumnResize(
            () => _grid,
            () => _lastPaging.PageSize > 0 ? GetColumnCount() : 0,
            OnColumnWidthsCommitted);

        _cellNavigation = new TableGridCellNavigation();
        _cellNavigation.BoundaryEscapeRequested += dir => BoundaryEscapeRequested?.Invoke(dir);

        _infiniteScroll = new TableGridInfiniteScroll();
        _infiniteScroll.ScrollOffsetChanged += OnScrollOffsetChanged;

        _toolbar = new TableGridToolbar();
        _toolbar.IsStripedChanged += OnToolbarStripedChanged;
        _toolbar.HeaderToggleChanged += OnToolbarHeaderToggled;
        _toolbar.EditTableRequested += () => EditTableRequested?.Invoke();
        _toolbar.ScrollModeChanged += OnToolbarScrollModeChanged;

        _titleBlock = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            IsVisible = false
        };
        _titleBlock.Bind(TextBlock.FontSizeProperty,
            _titleBlock.GetResourceObservable("ThemeFontSizeLg"));

        _captionBlock = new TextBlock
        {
            FontStyle = FontStyle.Italic,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };
        _captionBlock.Bind(TextBlock.FontSizeProperty,
            _captionBlock.GetResourceObservable("ThemeFontSizeSmMd"));

        // Filter bar (left) + toolbar/cog (right) on same row
        var filterToolbarRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_toolbar, Dock.Right);
        filterToolbarRow.Children.Add(_toolbar);
        filterToolbarRow.Children.Add(_filterBar);

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(_titleBlock);
        root.Children.Add(filterToolbarRow);
        root.Children.Add(_errorBorder);

        var gridBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = _scrollViewer
        };
        root.Children.Add(gridBorder);
        root.Children.Add(_captionBlock);
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
            UpdateDragHandlers();
            Rebuild();
        }
        else if (change.Property == FilterTextProperty)
        {
            _currentPage = 0;
            _filterBar.SetFilterText(FilterText);
            Rebuild();
        }
        else if (change.Property == PageSizeProperty)
        {
            _currentPage = 0;
            _filterBar.SetPageSize(PageSize);
            Rebuild();
        }
        else if (change.Property == ShowPagingProperty)
        {
            _pagingBar.IsVisible = change.GetNewValue<bool>();
        }
        else if (change.Property == ShowToolbarProperty)
        {
            _toolbar.IsVisible = change.GetNewValue<bool>();
        }
        else if (change.Property == ShowFilterProperty)
        {
            _filterBar.IsVisible = change.GetNewValue<bool>();
        }
        else if (change.Property == IsStripedProperty || change.Property == ColorThemeProperty)
        {
            Rebuild();
        }
        else if (change.Property == TitleProperty)
        {
            var title = change.GetNewValue<string?>();
            _titleBlock.Text = title ?? "";
            _titleBlock.IsVisible = !string.IsNullOrEmpty(title);
        }
        else if (change.Property == CaptionProperty)
        {
            var caption = change.GetNewValue<string?>();
            _captionBlock.Text = caption ?? "";
            _captionBlock.IsVisible = !string.IsNullOrEmpty(caption);
        }
        else if (change.Property == FrozenColumnCountProperty
                 || change.Property == FrozenRowCountProperty)
        {
            Rebuild();
        }
        else if (change.Property == ScrollModeProperty)
        {
            OnScrollModeChanged();
        }
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void Rebuild() => RebuildAsync();

    public void FocusEdge(int direction) =>
        _cellNavigation.FocusEdge(direction);

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
            _toolbar.Update(null, Rebuild, false);
            return;
        }

        var isScrollRebuild = _isScrollTriggeredRebuild;
        _isScrollTriggeredRebuild = false;

        var version = ++_rebuildVersion;
        var pageSize = Math.Clamp(PageSize, 1, 100);

        try
        {
            TableGridData data;
            TablePagingInfo paging;

            if (ScrollMode == TableScrollMode.InfiniteScroll
                && isScrollRebuild && _infiniteScroll.Template != null)
            {
                // Scroll-triggered: serve from page cache, fetch missing pages
                var (neededPages, visibleRows) = _infiniteScroll.GetVisibleWindow();

                if (neededPages.Count > 0)
                {
                    foreach (var page in neededPages)
                    {
                        var (pageData, _) = await source.GetDataAsync(
                            FilterText, _sortState.ColumnIndex, _sortState.Direction,
                            page, pageSize);
                        if (version != _rebuildVersion) return;
                        _infiniteScroll.CachePage(page, pageData.DataRows);
                    }
                    (_, visibleRows) = _infiniteScroll.GetVisibleWindow();
                }

                data = _infiniteScroll.Template with { DataRows = visibleRows ?? [] };
                paging = _lastPaging;
            }
            else
            {
                // Normal fetch (initial load, paginated, filter/sort change)
                (data, paging) = await source.GetDataAsync(
                    FilterText, _sortState.ColumnIndex, _sortState.Direction,
                    _currentPage, pageSize);

                if (version != _rebuildVersion) return;

                // Initialize infinite scroll cache on first fetch
                if (ScrollMode == TableScrollMode.InfiniteScroll)
                {
                    _infiniteScroll.Reset();
                    _infiniteScroll.Configure(pageSize, paging.TotalFilteredRows, pageSize);
                    _infiniteScroll.SetTemplate(data);
                    _infiniteScroll.CachePage(0, data.DataRows);

                    var (_, visibleRows) = _infiniteScroll.GetVisibleWindow();
                    if (visibleRows != null)
                        data = data with { DataRows = visibleRows };
                }
            }

            _lastPaging = paging;
            _currentPage = paging.CurrentPage;

            if (!string.IsNullOrEmpty(data.ErrorMessage))
            {
                ShowError(data.ErrorMessage);
                _pagingBar.Update(paging);
                return;
            }

            ClearError();

            var hasHeaderRow = data.HeaderRows.Count > 0 &&
                               data.HeaderRows.Any(r => r.IsHeader);

            var result = TableGridRenderer.RenderGrid(
                _grid, data, paging, _sortState,
                source.SupportsSorting,
                source.IsEditable,
                IsReadOnly,
                source.SupportsRowReorder,
                source.SupportsColumnReorder,
                source.SupportsStructureEditing,
                source,
                OnHeaderClick,
                (col, e) => _columnResize.OnGripPressed(col, e, this),
                e => _columnResize.OnGripMoved(e, this),
                e => _columnResize.OnGripReleased(e),
                source.IsEditable ? _cellNavigation : null,
                source.IsEditable ? OnCellEdited : null,
                source.SupportsRowReorder && !IsReadOnly ? _rowDrag : null,
                source.SupportsColumnReorder && !IsReadOnly ? _columnDrag : null,
                Rebuild,
                this,
                IsStriped,
                ColorTheme,
                FrozenColumnCount,
                FrozenRowCount,
                OnFreezeColumnsFromContextMenu,
                OnFreezeRowsFromContextMenu);

            _lastDataRowCount = result.DataRowCount;
            _lastDataRowStartGridRow = result.DataRowStartGridRow;

            var isInfiniteScroll = ScrollMode == TableScrollMode.InfiniteScroll;
            _pagingBar.Update(paging);
            _pagingBar.IsVisible = ShowPaging && !isInfiniteScroll;

            _toolbar.Update(source, Rebuild, hasHeaderRow, IsStriped,
                ScrollMode == TableScrollMode.InfiniteScroll);
            _toolbar.IsVisible = ShowToolbar;

            _filterBar.IsVisible = ShowFilter;
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

    private void OnCellEdited(string rowId, int columnIndex, string newValue)
    {
        var source = DataSource;
        if (source == null || IsReadOnly) return;

        _ = source.OnCellEditedAsync(rowId, columnIndex, newValue);
        DataChanged?.Invoke();
    }

    private void OnFilterBarTextChanged(string? text)
    {
        _currentPage = 0;
        FilterText = text;
        FilterTextCommitted?.Invoke(text);
    }

    private void OnFilterBarPageSizeChanged(int pageSize)
    {
        PageSize = pageSize;
        PageSizeCommitted?.Invoke(pageSize);
    }

    private void OnToolbarStripedChanged(bool isStriped)
    {
        IsStriped = isStriped;
        IsStripedCommitted?.Invoke(isStriped);
    }

    private void OnToolbarHeaderToggled(bool hasHeader)
    {
        DataChanged?.Invoke();
    }

    private void OnToolbarScrollModeChanged(bool isInfiniteScroll)
    {
        var mode = isInfiniteScroll
            ? TableScrollMode.InfiniteScroll
            : TableScrollMode.Paginated;
        ScrollMode = mode;
        ScrollModeCommitted?.Invoke(mode);
    }

    private void OnScrollModeChanged()
    {
        _infiniteScroll.Reset();
        if (ScrollMode == TableScrollMode.InfiniteScroll)
        {
            _infiniteScroll.Attach(_scrollViewer);
            _currentPage = 0;
        }
        else
        {
            _infiniteScroll.Detach();
            _currentPage = 0;
        }
        Rebuild();
    }

    private void OnScrollOffsetChanged()
    {
        _isScrollTriggeredRebuild = true;
        Rebuild();
    }

    private void OnFreezeColumnsFromContextMenu(int count)
    {
        FrozenColumnCount = count;
        FrozenColumnCountCommitted?.Invoke(count);
    }

    private void OnFreezeRowsFromContextMenu(int count)
    {
        FrozenRowCount = count;
        FrozenRowCountCommitted?.Invoke(count);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void UpdateDragHandlers()
    {
        var source = DataSource;
        if (source == null)
        {
            _rowDrag = null;
            _columnDrag = null;
            return;
        }

        if (source.SupportsRowReorder)
        {
            _rowDrag = new TableGridRowDrag(
                () => _grid,
                () => _lastDataRowCount,
                () => _lastDataRowStartGridRow,
                () => DataSource,
                Rebuild,
                () => this);
        }
        else
        {
            _rowDrag = null;
        }

        _columnDrag = new TableGridColumnDrag(
            () => _grid,
            () => GetColumnCount(),
            () => DataSource,
            Rebuild,
            () => this);
    }

    private int GetColumnCount()
    {
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
