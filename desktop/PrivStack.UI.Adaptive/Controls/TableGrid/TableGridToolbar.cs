// ============================================================================
// File: TableGridToolbar.cs
// Description: Configurable toolbar for the TableGrid. Shows add/remove row/col
//              buttons, header toggle, and export menu based on data source
//              capability flags. Supports an additional content slot for plugin-
//              specific buttons (import, refresh, columns, edit source).
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
    private readonly Button _addColBtn;
    private readonly Button _removeColBtn;
    private readonly Button _addRowBtn;
    private readonly Button _removeRowBtn;
    private readonly Border _structureSeparator;
    private readonly ToggleButton _headerToggle;
    private readonly Border _headerSeparator;
    private readonly Button _exportBtn;
    private readonly StackPanel _mainPanel;
    private readonly StackPanel _additionalSlot;

    private ITableGridDataSource? _source;
    private Action? _rebuild;

    public TableGridToolbar()
    {
        _addColBtn = MakeButton("+ Column", "Add column");
        _removeColBtn = MakeButton("- Column", "Remove last column");
        _addRowBtn = MakeButton("+ Row", "Add row");
        _removeRowBtn = MakeButton("- Row", "Remove last row");

        _structureSeparator = MakeSeparator();

        _headerToggle = new ToggleButton
        {
            Content = "Header Row",
            Padding = new Thickness(8, 4),
        };
        ToolTip.SetTip(_headerToggle, "Toggle whether the first row is a header");
        _headerToggle.Bind(TextBlock.FontSizeProperty,
            _headerToggle.GetResourceObservable("ThemeFontSizeXsSm"));
        _headerToggle.IsCheckedChanged += OnHeaderToggleChanged;

        _headerSeparator = MakeSeparator();

        _exportBtn = MakeButton("Export", "Export table data");
        _exportBtn.Flyout = BuildExportFlyout();

        _additionalSlot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        _mainPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        _mainPanel.Children.Add(_addColBtn);
        _mainPanel.Children.Add(_removeColBtn);
        _mainPanel.Children.Add(_addRowBtn);
        _mainPanel.Children.Add(_removeRowBtn);
        _mainPanel.Children.Add(_structureSeparator);
        _mainPanel.Children.Add(_headerToggle);
        _mainPanel.Children.Add(_headerSeparator);
        _mainPanel.Children.Add(_additionalSlot);
        _mainPanel.Children.Add(_exportBtn);

        Child = _mainPanel;
    }

    public StackPanel AdditionalContentSlot => _additionalSlot;

    public void Update(ITableGridDataSource? source, Action rebuild, bool hasHeaderRow)
    {
        _source = source;
        _rebuild = rebuild;

        var editable = source?.SupportsStructureEditing ?? false;
        _addColBtn.IsVisible = editable;
        _removeColBtn.IsVisible = editable;
        _addRowBtn.IsVisible = editable;
        _removeRowBtn.IsVisible = editable;
        _structureSeparator.IsVisible = editable;
        _headerToggle.IsVisible = editable;
        _headerSeparator.IsVisible = editable;

        _headerToggle.IsCheckedChanged -= OnHeaderToggleChanged;
        _headerToggle.IsChecked = hasHeaderRow;
        _headerToggle.IsCheckedChanged += OnHeaderToggleChanged;
    }

    private void OnHeaderToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_source == null || _rebuild == null) return;
        var isHeader = _headerToggle.IsChecked ?? false;
        _ = _source.OnToggleHeaderAsync(isHeader);
        _rebuild();
    }

    private MenuFlyout BuildExportFlyout()
    {
        var flyout = new MenuFlyout();

        var csvItem = new MenuItem { Header = "Export as CSV" };
        csvItem.Click += (_, _) => OnExport("csv");
        flyout.Items.Add(csvItem);

        var tsvItem = new MenuItem { Header = "Export as TSV" };
        tsvItem.Click += (_, _) => OnExport("tsv");
        flyout.Items.Add(tsvItem);

        var mdItem = new MenuItem { Header = "Export as Markdown" };
        mdItem.Click += (_, _) => OnExport("md");
        flyout.Items.Add(mdItem);

        var jsonItem = new MenuItem { Header = "Export as JSON" };
        jsonItem.Click += (_, _) => OnExport("json");
        flyout.Items.Add(jsonItem);

        flyout.Items.Add(new Separator());

        var clipItem = new MenuItem { Header = "Copy to Clipboard (Tab-separated)" };
        clipItem.Click += (_, _) => OnCopyToClipboard();
        flyout.Items.Add(clipItem);

        return flyout;
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

    internal void WireStructureButtons(Action rebuild)
    {
        _addColBtn.Click += async (_, _) =>
        {
            if (_source != null) await _source.OnAddColumnAsync();
            rebuild();
        };
        _removeColBtn.Click += async (_, _) =>
        {
            if (_source != null) await _source.OnRemoveColumnAsync();
            rebuild();
        };
        _addRowBtn.Click += async (_, _) =>
        {
            if (_source != null) await _source.OnAddRowAsync();
            rebuild();
        };
        _removeRowBtn.Click += async (_, _) =>
        {
            if (_source != null) await _source.OnRemoveRowAsync();
            rebuild();
        };
    }

    private static Button MakeButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(8, 4),
        };
        ToolTip.SetTip(btn, tooltip);
        btn.Bind(TextBlock.FontSizeProperty,
            btn.GetResourceObservable("ThemeFontSizeXsSm"));
        return btn;
    }

    private static Border MakeSeparator() => new()
    {
        Width = 1,
        Height = 16,
        Margin = new Thickness(4, 0)
    };
}
