// ============================================================================
// File: TableGridInfiniteScroll.cs
// Description: Handles row-by-row mouse-wheel scrolling for TableGrid.
//              Maintains a page cache so the visible window (pageSize rows)
//              can slide 1 row at a time across page boundaries. Each wheel
//              tick shifts the view by one row.
// ============================================================================

using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridInfiniteScroll
{
    private Control? _target;
    private int _scrollOffset;   // index of the top visible row
    private int _totalRows;
    private int _displaySize;    // pageSize — rows visible at once
    private int _fetchPageSize;  // page size used for data source fetches

    private readonly Dictionary<int, IReadOnlyList<TableGridRow>> _pageCache = new();

    /// <summary>Column definitions + header rows from the initial fetch (reused for scroll renders).</summary>
    private TableGridData? _template;

    /// <summary>Fires when the scroll offset changes and a re-render is needed.</summary>
    public event Action? ScrollOffsetChanged;

    public int ScrollOffset => _scrollOffset;
    public TableGridData? Template => _template;

    /// <summary>Reset all state (call on filter/sort/data source change).</summary>
    public void Reset()
    {
        _pageCache.Clear();
        _scrollOffset = 0;
        _totalRows = 0;
        _template = null;
    }

    /// <summary>Configure after the initial data fetch.</summary>
    public void Configure(int displaySize, int totalRows, int fetchPageSize)
    {
        _displaySize = displaySize;
        _totalRows = totalRows;
        _fetchPageSize = fetchPageSize;
    }

    /// <summary>Store the template (columns + headers) from the initial fetch.</summary>
    public void SetTemplate(TableGridData data)
    {
        _template = data with { DataRows = [] };
    }

    /// <summary>Cache a fetched page of rows.</summary>
    public void CachePage(int pageIndex, IReadOnlyList<TableGridRow> rows)
    {
        _pageCache[pageIndex] = rows;
    }

    /// <summary>
    /// Compute the visible row window for the current scroll offset.
    /// Returns page indices that need fetching (empty if all cached)
    /// and the visible row slice (null if pages still needed).
    /// </summary>
    public (List<int> NeededPages, IReadOnlyList<TableGridRow>? VisibleRows) GetVisibleWindow()
    {
        if (_totalRows == 0 || _displaySize == 0 || _fetchPageSize == 0)
            return ([], null);

        var startPage = _scrollOffset / _fetchPageSize;
        var lastRow = Math.Min(_scrollOffset + _displaySize - 1, _totalRows - 1);
        var endPage = lastRow / _fetchPageSize;

        var needed = new List<int>();
        for (var p = startPage; p <= endPage; p++)
            if (!_pageCache.ContainsKey(p))
                needed.Add(p);

        if (needed.Count > 0)
            return (needed, null);

        // All pages cached — build the visible slice
        var buffer = new List<TableGridRow>();
        for (var p = startPage; p <= endPage; p++)
            if (_pageCache.TryGetValue(p, out var pageRows))
                buffer.AddRange(pageRows);

        var offsetInBuffer = _scrollOffset - startPage * _fetchPageSize;
        var visible = buffer.Skip(offsetInBuffer).Take(_displaySize).ToList();
        return ([], visible);
    }

    /// <summary>Attach mouse wheel interception to the given control.</summary>
    public void Attach(Control target)
    {
        if (_target == target) return;
        Detach();
        _target = target;
        _target.PointerWheelChanged += OnPointerWheelChanged;
    }

    /// <summary>Detach from the currently tracked control.</summary>
    public void Detach()
    {
        if (_target != null)
        {
            _target.PointerWheelChanged -= OnPointerWheelChanged;
            _target = null;
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_totalRows <= _displaySize) return;

        // Delta.Y > 0 = scroll up (show earlier rows), < 0 = scroll down (show later rows)
        var delta = e.Delta.Y > 0 ? -1 : e.Delta.Y < 0 ? 1 : 0;
        if (delta == 0) return;

        var maxOffset = Math.Max(0, _totalRows - _displaySize);
        var newOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        if (newOffset == _scrollOffset) return;

        _scrollOffset = newOffset;
        e.Handled = true;
        ScrollOffsetChanged?.Invoke();
    }
}
