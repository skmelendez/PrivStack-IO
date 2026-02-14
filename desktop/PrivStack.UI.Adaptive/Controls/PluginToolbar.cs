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
    private readonly ContentControl _actionsHost;
    private bool _suppressSearchSync;

    public PluginToolbar()
    {
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(16, 8, 16, 6);

        _titleBlock = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _titleBlock.Bind(TextBlock.FontSizeProperty,
            _titleBlock.GetResourceObservable("ThemeFontSizeLgXl"));
        _titleBlock.Bind(TextBlock.ForegroundProperty,
            _titleBlock.GetResourceObservable("ThemeTextPrimaryBrush"));

        _searchNormal = BuildSearchBox();
        _searchNormal.HorizontalAlignment = HorizontalAlignment.Center;
        _searchNormal.MinWidth = 200;
        _searchNormal.MaxWidth = 300;
        _searchNormal.Bind(IsVisibleProperty,
            _searchNormal.GetResourceObservable("ThemeIsNotCompactMode"));

        _searchCompact = BuildSearchBox();
        _searchCompact.HorizontalAlignment = HorizontalAlignment.Stretch;
        _searchCompact.Margin = new Thickness(0, 6, 0, 0);
        _searchCompact.Bind(IsVisibleProperty,
            _searchCompact.GetResourceObservable("ThemeIsCompactMode"));

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
        row0.Children.Add(_searchNormal);
        row0.Children.Add(_actionsHost);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Grid.SetRow(row0, 0);
        Grid.SetRow(_searchCompact, 1);
        Grid.SetRow(_subtitleBlock, 2);

        grid.Children.Add(row0);
        grid.Children.Add(_searchCompact);
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
        BorderBrush = GetBrush("ThemeBorderSubtleBrush", Brushes.Gray);
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
            _searchNormal.IsVisible = false;
            _searchCompact.IsVisible = false;
        }
        else
        {
            _searchNormal.ClearValue(IsVisibleProperty);
            _searchCompact.ClearValue(IsVisibleProperty);
            _searchNormal.Bind(IsVisibleProperty,
                _searchNormal.GetResourceObservable("ThemeIsNotCompactMode"));
            _searchCompact.Bind(IsVisibleProperty,
                _searchCompact.GetResourceObservable("ThemeIsCompactMode"));
        }
    }

    private TextBox BuildSearchBox()
    {
        var tb = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(6),
            Text = SearchText,
            Watermark = SearchWatermark,
        };
        tb.Bind(TextBox.BackgroundProperty,
            tb.GetResourceObservable("ThemeSurfaceElevatedBrush"));
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
