// ============================================================================
// File: PluginToolbar.cs
// Description: Reusable toolbar control for plugins. Provides title, search,
//              subtitle, and an actions slot with responsive compact layout.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// A standardized plugin toolbar with title, search box, subtitle, and an
/// actions content slot. Automatically switches to stacked layout in compact mode.
/// </summary>
public sealed class PluginToolbar : Border
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PluginToolbar, string>(nameof(Title), "");

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PluginToolbar, string?>(nameof(Subtitle));

    public static readonly StyledProperty<string> SearchWatermarkProperty =
        AvaloniaProperty.Register<PluginToolbar, string>(nameof(SearchWatermark), "Search");

    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<PluginToolbar, string>(nameof(SearchText), "",
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsSearchVisibleProperty =
        AvaloniaProperty.Register<PluginToolbar, bool>(nameof(IsSearchVisible), true);

    public static readonly StyledProperty<Control?> ActionsProperty =
        AvaloniaProperty.Register<PluginToolbar, Control?>(nameof(Actions));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string SearchWatermark
    {
        get => GetValue(SearchWatermarkProperty);
        set => SetValue(SearchWatermarkProperty, value);
    }

    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public bool IsSearchVisible
    {
        get => GetValue(IsSearchVisibleProperty);
        set => SetValue(IsSearchVisibleProperty, value);
    }

    public Control? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    private readonly TextBlock _titleBlock;
    private readonly TextBlock _subtitleBlock;
    private readonly TextBox _searchNormal;
    private readonly TextBox _searchCompact;
    private readonly Border _searchNormalPill;
    private readonly Border _searchCompactPill;
    private readonly ContentControl _actionsHost;
    private bool _suppressSearchSync;

    public PluginToolbar()
    {
        BorderThickness = new Thickness(0);
        Padding = new Thickness(20, 12, 20, 10);

        _titleBlock = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _titleBlock.Bind(TextBlock.FontSizeProperty,
            _titleBlock.GetResourceObservable("ThemeFontSizeHeading2"));
        _titleBlock.Bind(TextBlock.ForegroundProperty,
            _titleBlock.GetResourceObservable("ThemeTextPrimaryBrush"));

        _searchNormal = BuildSearchBox();
        _searchNormalPill = BuildSearchPill(_searchNormal);
        _searchNormalPill.HorizontalAlignment = HorizontalAlignment.Center;
        _searchNormalPill.MinWidth = 200;
        _searchNormalPill.MaxWidth = 300;
        _searchNormalPill.Bind(IsVisibleProperty,
            _searchNormalPill.GetResourceObservable("ThemeIsNotCompactMode"));

        _searchCompact = BuildSearchBox();
        _searchCompactPill = BuildSearchPill(_searchCompact);
        _searchCompactPill.HorizontalAlignment = HorizontalAlignment.Stretch;
        _searchCompactPill.Margin = new Thickness(0, 6, 0, 0);
        _searchCompactPill.Bind(IsVisibleProperty,
            _searchCompactPill.GetResourceObservable("ThemeIsCompactMode"));

        _actionsHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _subtitleBlock = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
        };
        _subtitleBlock.Bind(TextBlock.FontSizeProperty,
            _subtitleBlock.GetResourceObservable("ThemeFontSizeSm"));
        _subtitleBlock.Bind(TextBlock.ForegroundProperty,
            _subtitleBlock.GetResourceObservable("ThemeTextMutedBrush"));

        var row0 = new Grid { MinHeight = 32 };
        row0.Children.Add(_titleBlock);
        row0.Children.Add(_searchNormalPill);
        row0.Children.Add(_actionsHost);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Grid.SetRow(row0, 0);
        Grid.SetRow(_searchCompactPill, 1);
        Grid.SetRow(_subtitleBlock, 2);

        grid.Children.Add(row0);
        grid.Children.Add(_searchCompactPill);
        grid.Children.Add(_subtitleBlock);

        Child = grid;

        // Apply initial values
        _titleBlock.Text = Title;
        _subtitleBlock.Text = Subtitle;
        _subtitleBlock.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _searchNormal.Watermark = SearchWatermark;
        _searchCompact.Watermark = SearchWatermark;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme();
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        Background = GetBrush("ThemeSurfaceBrush", Brushes.Transparent);
        BorderBrush = Brushes.Transparent;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty)
            _titleBlock.Text = change.GetNewValue<string>();
        else if (change.Property == SubtitleProperty)
        {
            var val = change.GetNewValue<string?>();
            _subtitleBlock.Text = val;
            _subtitleBlock.IsVisible = !string.IsNullOrEmpty(val);
        }
        else if (change.Property == SearchWatermarkProperty)
        {
            var wm = change.GetNewValue<string>();
            _searchNormal.Watermark = wm;
            _searchCompact.Watermark = wm;
        }
        else if (change.Property == SearchTextProperty)
            SyncSearchToBoxes(change.GetNewValue<string>());
        else if (change.Property == IsSearchVisibleProperty)
            UpdateSearchVisibility();
        else if (change.Property == ActionsProperty)
            _actionsHost.Content = change.GetNewValue<Control?>();
    }

    private void SyncSearchToBoxes(string text)
    {
        if (_suppressSearchSync) return;
        _suppressSearchSync = true;
        _searchNormal.Text = text;
        _searchCompact.Text = text;
        _suppressSearchSync = false;
    }

    private void UpdateSearchVisibility()
    {
        if (!IsSearchVisible)
        {
            _searchNormalPill.IsVisible = false;
            _searchCompactPill.IsVisible = false;
        }
        else
        {
            _searchNormalPill.ClearValue(IsVisibleProperty);
            _searchCompactPill.ClearValue(IsVisibleProperty);
            _searchNormalPill.Bind(IsVisibleProperty,
                _searchNormalPill.GetResourceObservable("ThemeIsNotCompactMode"));
            _searchCompactPill.Bind(IsVisibleProperty,
                _searchCompactPill.GetResourceObservable("ThemeIsCompactMode"));
        }
    }

    private TextBox BuildSearchBox()
    {
        var tb = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(0),
            Background = Brushes.Transparent,
            Text = SearchText,
            Watermark = SearchWatermark,
        };
        tb.Bind(TextBox.ForegroundProperty,
            tb.GetResourceObservable("ThemeTextPrimaryBrush"));

        tb.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            if (_suppressSearchSync) return;
            _suppressSearchSync = true;
            SearchText = args.GetNewValue<string?>() ?? "";
            _suppressSearchSync = false;
        };

        return tb;
    }

    private Border BuildSearchPill(TextBox inner)
    {
        // Magnifying glass icon (Material Design filled search icon)
        var searchIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(
                "M15.5 14h-.79l-.28-.27A6.47 6.47 0 0016 9.5 " +
                "6.5 6.5 0 109.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5z" +
                "m-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z"),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        searchIcon.Bind(Avalonia.Controls.Shapes.Path.FillProperty,
            searchIcon.GetResourceObservable("ThemeTextMutedBrush"));

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { searchIcon, inner }
        };

        var pill = new Border
        {
            CornerRadius = new CornerRadius(9999),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        pill.Bind(Border.BackgroundProperty,
            pill.GetResourceObservable("ThemeSurfaceElevatedBrush"));
        pill.Bind(Border.BorderBrushProperty,
            pill.GetResourceObservable("ThemeBorderSubtleBrush"));

        return pill;
    }

    private IBrush? GetBrush(string key, IBrush? fallback)
    {
        if (this.TryGetResource(key, ActualThemeVariant, out var obj) && obj is IBrush brush)
            return brush;
        var app = Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var appObj) == true
            && appObj is IBrush appBrush)
            return appBrush;
        return fallback;
    }
}
