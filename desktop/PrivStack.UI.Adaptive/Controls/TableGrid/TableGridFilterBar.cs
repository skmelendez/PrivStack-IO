// ============================================================================
// File: TableGridFilterBar.cs
// Description: Inline filter bar for the TableGrid. Contains a filter TextBox
//              with 300ms debounce and a page size ComboBox selector.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridFilterBar : Border
{
    private readonly TextBox _filterTextBox;
    private readonly ComboBox _pageSizeCombo;
    private DispatcherTimer? _debounceTimer;

    public event Action<string?>? FilterTextChanged;
    public event Action<int>? PageSizeChanged;

    public TableGridFilterBar()
    {
        _filterTextBox = new TextBox
        {
            Watermark = "Filter...",
            MinWidth = 200,
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(1),
        };
        _filterTextBox.Bind(TextBlock.FontSizeProperty,
            _filterTextBox.GetResourceObservable("ThemeFontSizeXsSm"));
        _filterTextBox.TextChanged += OnFilterTextChanged;

        _pageSizeCombo = new ComboBox
        {
            MinWidth = 70,
            Padding = new Thickness(4, 2),
            ItemsSource = new[] { 10, 25, 50, 100 },
            SelectedIndex = 0,
        };
        _pageSizeCombo.Bind(TextBlock.FontSizeProperty,
            _pageSizeCombo.GetResourceObservable("ThemeFontSizeXsSm"));
        _pageSizeCombo.SelectionChanged += OnPageSizeSelectionChanged;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        panel.Children.Add(_filterTextBox);
        panel.Children.Add(_pageSizeCombo);

        Child = panel;
        Margin = new Thickness(0, 0, 0, 4);
    }

    public void SetFilterText(string? text)
    {
        _filterTextBox.TextChanged -= OnFilterTextChanged;
        _filterTextBox.Text = text;
        _filterTextBox.TextChanged += OnFilterTextChanged;
    }

    public void SetPageSize(int pageSize)
    {
        _pageSizeCombo.SelectionChanged -= OnPageSizeSelectionChanged;
        var items = (int[])_pageSizeCombo.ItemsSource!;
        var idx = Array.IndexOf(items, pageSize);
        _pageSizeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        _pageSizeCombo.SelectionChanged += OnPageSizeSelectionChanged;
    }

    private void OnFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounceTimer?.Stop();
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            FilterTextChanged?.Invoke(_filterTextBox.Text);
        };
        _debounceTimer.Start();
    }

    private void OnPageSizeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pageSizeCombo.SelectedItem is int pageSize)
            PageSizeChanged?.Invoke(pageSize);
    }
}
