// ============================================================================
// File: TableGridInfiniteScroll.cs
// Description: Handles mouse-wheel-driven page scrolling for TableGrid.
//              When active, the table always shows exactly `pageSize` rows
//              but the mouse wheel advances/retreats pages instead of using
//              Prev/Next pagination buttons.
// ============================================================================

using Avalonia.Controls;
using Avalonia.Input;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridInfiniteScroll
{
    private Control? _target;
    private int _totalPages = 1;

    /// <summary>Fires when the mouse wheel requests a page change. Arg: delta (-1 = prev, +1 = next).</summary>
    public event Action<int>? PageChangeRequested;

    public void UpdateTotalPages(int totalPages)
    {
        _totalPages = totalPages;
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
        if (_totalPages <= 1) return;

        // Delta.Y > 0 = scroll up (prev page), < 0 = scroll down (next page)
        var delta = e.Delta.Y > 0 ? -1 : e.Delta.Y < 0 ? 1 : 0;
        if (delta == 0) return;

        e.Handled = true;
        PageChangeRequested?.Invoke(delta);
    }
}
