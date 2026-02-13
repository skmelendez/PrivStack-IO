// ============================================================================
// File: TableGridPaging.cs
// Description: Paging controls for the TableGrid: prev/next buttons, page info
//              text, and state management for current page tracking.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridPagingBar : Border
{
    private readonly Button _prevButton;
    private readonly Button _nextButton;
    private readonly TextBlock _pageInfoText;

    public event Action? PrevPageRequested;
    public event Action? NextPageRequested;

    public TableGridPagingBar()
    {
        _prevButton = new Button
        {
            Content = "\u25c0",
            Padding = new Thickness(6, 4),
            IsEnabled = false
        };
        _prevButton.Bind(TextBlock.FontSizeProperty,
            _prevButton.GetResourceObservable("ThemeFontSizeXsSm"));
        _prevButton.Click += (_, _) => PrevPageRequested?.Invoke();

        _nextButton = new Button
        {
            Content = "\u25b6",
            Padding = new Thickness(6, 4),
            IsEnabled = false
        };
        _nextButton.Bind(TextBlock.FontSizeProperty,
            _nextButton.GetResourceObservable("ThemeFontSizeXsSm"));
        _nextButton.Click += (_, _) => NextPageRequested?.Invoke();

        _pageInfoText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageInfoText.Bind(TextBlock.FontSizeProperty,
            _pageInfoText.GetResourceObservable("ThemeFontSizeXsSm"));
        _pageInfoText.Bind(TextBlock.ForegroundProperty,
            _pageInfoText.GetResourceObservable("ThemeTextMutedBrush"));

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        panel.Children.Add(_prevButton);
        panel.Children.Add(_pageInfoText);
        panel.Children.Add(_nextButton);

        Child = panel;
    }

    public void Update(TablePagingInfo paging)
    {
        var hasPaging = paging.TotalFilteredRows > 0;

        _prevButton.IsEnabled = hasPaging && paging.CurrentPage > 0;
        _nextButton.IsEnabled = hasPaging && paging.CurrentPage < paging.TotalPages - 1;

        if (hasPaging)
        {
            var showing = Math.Min(paging.PageSize,
                paging.TotalFilteredRows - paging.CurrentPage * paging.PageSize);
            var totalInfo = paging.TotalFilteredRows.ToString("N0");

            if (paging.TotalSourceRows is > 0 && paging.TotalSourceRows.Value > paging.TotalFilteredRows)
                totalInfo = $"{paging.TotalFilteredRows:N0} of {paging.TotalSourceRows.Value:N0} source";

            _pageInfoText.Text =
                $"Page {paging.CurrentPage + 1} of {paging.TotalPages} ({totalInfo} rows, showing {showing})";
        }
        else
        {
            _pageInfoText.Text = paging.TotalFilteredRows > 0
                ? $"{paging.TotalFilteredRows} row{(paging.TotalFilteredRows != 1 ? "s" : "")}"
                : "";
        }
    }
}
