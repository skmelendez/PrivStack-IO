// ============================================================================
// File: TableGridInfiniteScroll.cs
// Description: Manages infinite scroll behavior for the TableGrid. Monitors
//              scroll position, triggers page loads near the bottom, accumulates
//              rows across pages, and manages the loading indicator.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridInfiniteScroll
{
    private const double ScrollThresholdPx = 100;

    private readonly List<TableGridRow> _accumulatedRows = [];
    private readonly TextBlock _loadingIndicator;

    private int _nextPage;
    private bool _isLoading;
    private bool _hasMorePages = true;
    private int _totalFilteredRows;

    public IReadOnlyList<TableGridRow> AccumulatedRows => _accumulatedRows;
    public TextBlock LoadingIndicator => _loadingIndicator;
    public bool IsLoading => _isLoading;
    public bool HasMorePages => _hasMorePages;
    public int NextPage => _nextPage;

    public event Func<int, Task>? LoadPageRequested;

    public TableGridInfiniteScroll()
    {
        _loadingIndicator = new TextBlock
        {
            Text = "Loading more...",
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(0, 8),
            IsVisible = false
        };
    }

    public void Reset()
    {
        _accumulatedRows.Clear();
        _nextPage = 0;
        _isLoading = false;
        _hasMorePages = true;
        _totalFilteredRows = 0;
        _loadingIndicator.IsVisible = false;
    }

    /// <summary>
    /// Appends a page of rows from a data fetch result. Returns true if this
    /// was the initial page load (page 0).
    /// </summary>
    public bool AppendPage(IReadOnlyList<TableGridRow> rows, TablePagingInfo paging)
    {
        var isInitial = _nextPage == 0 || _accumulatedRows.Count == 0;

        if (isInitial)
            _accumulatedRows.Clear();

        _accumulatedRows.AddRange(rows);
        _totalFilteredRows = paging.TotalFilteredRows;
        _hasMorePages = _accumulatedRows.Count < _totalFilteredRows;
        _nextPage = paging.CurrentPage + 1;
        _isLoading = false;
        _loadingIndicator.IsVisible = false;

        return isInitial;
    }

    /// <summary>
    /// Attaches scroll monitoring to a ScrollViewer. Call after switching to
    /// infinite scroll mode.
    /// </summary>
    public void AttachToScrollViewer(ScrollViewer scrollViewer)
    {
        scrollViewer.ScrollChanged += OnScrollChanged;
    }

    public void DetachFromScrollViewer(ScrollViewer scrollViewer)
    {
        scrollViewer.ScrollChanged -= OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (_isLoading || !_hasMorePages) return;

        var distanceFromBottom = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (distanceFromBottom <= ScrollThresholdPx)
        {
            RequestNextPage();
        }
    }

    private void RequestNextPage()
    {
        if (_isLoading || !_hasMorePages) return;

        _isLoading = true;
        _loadingIndicator.IsVisible = true;
        LoadPageRequested?.Invoke(_nextPage);
    }
}
