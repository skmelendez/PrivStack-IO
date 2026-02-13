// ============================================================================
// File: TableGridPaging.cs
// Description: Paging controls for the TableGrid: prev/next buttons with
//              PathIcon chevrons, center-aligned page info text.
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
        var prevIcon = new PathIcon
        {
            Data = StreamGeometry.Parse("M15 18l-6-6 6-6"),
            Width = 12,
            Height = 12,
        };
        _prevButton = new Button
        {
            Content = prevIcon,
            Padding = new Thickness(6, 4),
            IsEnabled = false
        };
        _prevButton.Bind(TextBlock.FontSizeProperty,
            _prevButton.GetResourceObservable("ThemeFontSizeXsSm"));
        _prevButton.Click += (_, _) => PrevPageRequested?.Invoke();

        var nextIcon = new PathIcon
        {
            Data = StreamGeometry.Parse("M9 6l6 6-6 6"),
            Width = 12,
            Height = 12,
        };
        _nextButton = new Button
        {
            Content = nextIcon,
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
            HorizontalAlignment = HorizontalAlignment.Center,
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
