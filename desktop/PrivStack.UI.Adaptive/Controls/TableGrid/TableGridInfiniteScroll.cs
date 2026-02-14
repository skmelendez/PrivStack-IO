// ============================================================================
// File: TableGridInfiniteScroll.cs
// Description: Handles row-by-row mouse-wheel scrolling for TableGrid.
//              Maintains a page cache so the visible window (pageSize rows)
//              can slide 1 row at a time across page boundaries. Each wheel
//              tick shifts the view by one row. Supports frozen rows that
//              always display at the top of the visible window.
// ============================================================================

using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridInfiniteScroll
{
    private Control? _target;
    private int _scrollOffset;   // index of the top visible scrollable row
    private int _totalRows;
    private int _displaySize;    // pageSize — total rows visible at once
    private int _fetchPageSize;  // page size used for data source fetches
    private int _frozenRowCount; // rows pinned at top (always visible)

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
        _frozenRowCount = 0;
    }

    /// <summary>Configure after the initial data fetch.</summary>
    public void Configure(int displaySize, int totalRows, int fetchPageSize,
        int frozenRowCount = 0)
    {
        _displaySize = displaySize;
        _totalRows = totalRows;
        _fetchPageSize = fetchPageSize;
        _frozenRowCount = Math.Min(frozenRowCount, Math.Max(0, totalRows - 1));
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
    /// When frozen rows are configured, they always appear first in the
    /// returned slice, followed by the scrollable portion.
    /// </summary>
    public (List<int> NeededPages, IReadOnlyList<TableGridRow>? VisibleRows) GetVisibleWindow()
    {
        if (_totalRows == 0 || _displaySize == 0 || _fetchPageSize == 0)
            return ([], null);

        var frozenCount = Math.Min(_frozenRowCount, _totalRows);
        var scrollableDisplay = Math.Max(0, _displaySize - frozenCount);
        var scrollStart = frozenCount + _scrollOffset;
        var scrollEnd = Math.Min(scrollStart + scrollableDisplay - 1, _totalRows - 1);

        var needed = new List<int>();

        // Pages needed for frozen rows
        if (frozenCount > 0)
        {
            var frozenEndPage = (frozenCount - 1) / _fetchPageSize;
            for (var p = 0; p <= frozenEndPage; p++)
                if (!_pageCache.ContainsKey(p))
                    needed.Add(p);
        }

        // Pages needed for scrollable rows
        if (scrollableDisplay > 0 && scrollStart <= scrollEnd)
        {
            var startPage = scrollStart / _fetchPageSize;
            var endPage = scrollEnd / _fetchPageSize;
            for (var p = startPage; p <= endPage; p++)
                if (!_pageCache.ContainsKey(p) && !needed.Contains(p))
                    needed.Add(p);
        }

        if (needed.Count > 0)
            return (needed, null);

        // All pages cached — build the visible slice
        var result = new List<TableGridRow>();

        // Frozen rows (always rows 0..frozenCount-1)
        if (frozenCount > 0)
            result.AddRange(GetRowRange(0, frozenCount - 1));

        // Scrollable rows
        if (scrollableDisplay > 0 && scrollStart <= scrollEnd)
            result.AddRange(GetRowRange(scrollStart, scrollEnd));

        return ([], result);
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

    private IEnumerable<TableGridRow> GetRowRange(int startRow, int endRow)
    {
        var startPage = startRow / _fetchPageSize;
        var endPage = endRow / _fetchPageSize;

        var buffer = new List<TableGridRow>();
        for (var p = startPage; p <= endPage; p++)
            if (_pageCache.TryGetValue(p, out var pageRows))
                buffer.AddRange(pageRows);

        var offsetInBuffer = startRow - startPage * _fetchPageSize;
        var count = endRow - startRow + 1;
        return buffer.Skip(offsetInBuffer).Take(count);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_totalRows <= _displaySize) return;

        // Delta.Y > 0 = scroll up (show earlier rows), < 0 = scroll down
        var delta = e.Delta.Y > 0 ? -1 : e.Delta.Y < 0 ? 1 : 0;
        if (delta == 0) return;

        // Max offset for the scrollable portion (rows after frozen rows)
        var scrollableTotal = _totalRows - _frozenRowCount;
        var scrollableDisplay = Math.Max(1, _displaySize - _frozenRowCount);
        var maxOffset = Math.Max(0, scrollableTotal - scrollableDisplay);

        var newOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        if (newOffset == _scrollOffset) return;

        _scrollOffset = newOffset;
        e.Handled = true;
        ScrollOffsetChanged?.Invoke();
    }
}
