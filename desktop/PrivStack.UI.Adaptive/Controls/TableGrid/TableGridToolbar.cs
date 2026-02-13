// ============================================================================
// File: TableGridToolbar.cs
// Description: Configurable toolbar for the TableGrid. Shows a single options
//              gear button with a MenuFlyout containing header toggle, striped
//              rows toggle, plugin-injected items, export submenu, and edit table.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

internal sealed class TableGridToolbar : Border
{
    private readonly Button _optionsBtn;
    private readonly StackPanel _mainPanel;

    private ITableGridDataSource? _source;
    private Action? _rebuild;
    private bool _currentHasHeader;
    private bool _currentIsStriped;

    public event Action<bool>? IsStripedChanged;
    public event Action<bool>? HeaderToggleChanged;
    public event Action? EditTableRequested;

    /// <summary>
    /// Slot for plugin-injected menu items. Add MenuItem controls to this list;
    /// they will appear in the gear flyout between the toggles and the export submenu.
    /// </summary>
    public List<MenuItem> AdditionalMenuItems { get; } = [];

    public TableGridToolbar()
    {
        var gearIcon = new PathIcon
        {
            Data = StreamGeometry.Parse("M19.14 12.94c.04-.31.06-.63.06-.94 0-.31-.02-.63-.06-.94l2.03-1.58a.49.49 0 00.12-.61l-1.92-3.32a.488.488 0 00-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.44.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.04.31-.06.63-.06.94s.02.63.06.94l-2.03 1.58a.49.49 0 00-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6A3.6 3.6 0 1112 8.4a3.6 3.6 0 010 7.2z"),
            Width = 14,
            Height = 14,
        };

        _optionsBtn = new Button
        {
            Content = gearIcon,
            Padding = new Thickness(6, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(_optionsBtn, "Table options");
        _optionsBtn.Click += OnOptionsButtonClick;

        _mainPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _mainPanel.Children.Add(_optionsBtn);

        Child = _mainPanel;
    }

    public void Update(ITableGridDataSource? source, Action rebuild, bool hasHeaderRow, bool isStriped = false)
    {
        _source = source;
        _rebuild = rebuild;
        _currentHasHeader = hasHeaderRow;
        _currentIsStriped = isStriped;
    }

    private void OnOptionsButtonClick(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        // Header Row toggle
        var headerItem = new MenuItem
        {
            Header = _currentHasHeader ? "\u2611 Header Row" : "\u2610 Header Row"
        };
        headerItem.Click += (_, _) =>
        {
            _currentHasHeader = !_currentHasHeader;
            if (_source != null)
                _ = _source.OnToggleHeaderAsync(_currentHasHeader);
            HeaderToggleChanged?.Invoke(_currentHasHeader);
            _rebuild?.Invoke();
        };
        flyout.Items.Add(headerItem);

        // Striped Rows toggle
        var stripedItem = new MenuItem
        {
            Header = _currentIsStriped ? "\u2611 Striped Rows" : "\u2610 Striped Rows"
        };
        stripedItem.Click += (_, _) =>
        {
            _currentIsStriped = !_currentIsStriped;
            IsStripedChanged?.Invoke(_currentIsStriped);
        };
        flyout.Items.Add(stripedItem);

        // Additional items from plugin
        if (AdditionalMenuItems.Count > 0)
        {
            flyout.Items.Add(new Separator());
            foreach (var item in AdditionalMenuItems)
            {
                if (item.IsVisible)
                    flyout.Items.Add(item);
            }
        }

        // Export submenu
        flyout.Items.Add(new Separator());

        var exportSub = new MenuItem { Header = "Export" };
        var csvItem = new MenuItem { Header = "CSV" };
        csvItem.Click += (_, _) => OnExport("csv");
        var tsvItem = new MenuItem { Header = "TSV" };
        tsvItem.Click += (_, _) => OnExport("tsv");
        var mdItem = new MenuItem { Header = "Markdown" };
        mdItem.Click += (_, _) => OnExport("md");
        var jsonItem = new MenuItem { Header = "JSON" };
        jsonItem.Click += (_, _) => OnExport("json");
        var clipItem = new MenuItem { Header = "Copy to Clipboard" };
        clipItem.Click += (_, _) => OnCopyToClipboard();
        exportSub.Items.Add(csvItem);
        exportSub.Items.Add(tsvItem);
        exportSub.Items.Add(mdItem);
        exportSub.Items.Add(jsonItem);
        exportSub.Items.Add(new Separator());
        exportSub.Items.Add(clipItem);
        flyout.Items.Add(exportSub);

        // Edit Table
        flyout.Items.Add(new Separator());
        var editItem = new MenuItem { Header = "Edit Table..." };
        editItem.Click += (_, _) => EditTableRequested?.Invoke();
        flyout.Items.Add(editItem);

        flyout.ShowAt(_optionsBtn);
    }

    private async void OnExport(string format)
    {
        if (_source == null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var data = await _source.GetFullDataForExportAsync();
        if (data == null) return;

        await TableGridExport.ExportToFileAsync(data, format, topLevel);
    }

    private async void OnCopyToClipboard()
    {
        if (_source == null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var data = await _source.GetFullDataForExportAsync();
        if (data == null) return;

        await TableGridExport.CopyToClipboardAsync(data, topLevel);
    }
}
