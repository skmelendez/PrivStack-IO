// ============================================================================
// File: IconLabel.cs
// Description: Reusable control combining an optional PathIcon with a TextBlock.
//              Enforces consistent icon sizing, spacing, and theme-aware colors.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// Horizontal (or vertical) icon + label pair with standardized sizing and
/// theme-aware foreground. Hides icon when <see cref="IconData"/> is null,
/// hides text when <see cref="Text"/> is null/empty.
/// </summary>
public sealed class IconLabel : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<IconLabel, Geometry?>(nameof(IconData));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 14);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(Spacing), 6);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<IconLabel, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(FontSize), 0);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<IconLabel, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<IconLabel, Orientation>(nameof(Orientation), Orientation.Horizontal);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Geometry? IconData
    {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private readonly AvaloniaPath _icon;
    private readonly TextBlock _textBlock;
    private readonly StackPanel _panel;

    public IconLabel()
    {
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;

        _icon = new AvaloniaPath
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        _textBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        _panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _icon, _textBlock },
        };

        Child = _panel;

        // Apply defaults
        ApplyForeground(null);
        ApplyFontSize(0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyForeground(Foreground);
        ApplyFontSize(FontSize);
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyForeground(Foreground);
        ApplyFontSize(FontSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            var val = change.GetNewValue<string?>();
            _textBlock.Text = val;
            _textBlock.IsVisible = !string.IsNullOrEmpty(val);
        }
        else if (change.Property == IconDataProperty)
        {
            var data = change.GetNewValue<Geometry?>();
            _icon.Data = data;
            _icon.IsVisible = data is not null;
        }
        else if (change.Property == IconSizeProperty)
        {
            var size = change.GetNewValue<double>();
            _icon.Width = size;
            _icon.Height = size;
        }
        else if (change.Property == SpacingProperty)
            _panel.Spacing = change.GetNewValue<double>();
        else if (change.Property == ForegroundProperty)
            ApplyForeground(change.GetNewValue<IBrush?>());
        else if (change.Property == FontSizeProperty)
            ApplyFontSize(change.GetNewValue<double>());
        else if (change.Property == FontWeightProperty)
            _textBlock.FontWeight = change.GetNewValue<FontWeight>();
        else if (change.Property == OrientationProperty)
            _panel.Orientation = change.GetNewValue<Orientation>();
    }

    private void ApplyForeground(IBrush? brush)
    {
        if (brush is not null)
        {
            _icon.ClearValue(AvaloniaPath.FillProperty);
            _textBlock.ClearValue(TextBlock.ForegroundProperty);
            _icon.Fill = brush;
            _textBlock.Foreground = brush;
        }
        else
        {
            _icon.Bind(AvaloniaPath.FillProperty,
                _icon.GetResourceObservable("ThemeTextPrimaryBrush"));
            _textBlock.Bind(TextBlock.ForegroundProperty,
                _textBlock.GetResourceObservable("ThemeTextPrimaryBrush"));
        }
    }

    private void ApplyFontSize(double size)
    {
        if (size > 0)
        {
            _textBlock.ClearValue(TextBlock.FontSizeProperty);
            _textBlock.FontSize = size;
        }
        else
        {
            _textBlock.Bind(TextBlock.FontSizeProperty,
                _textBlock.GetResourceObservable("ThemeFontSizeSmMd"));
        }
    }
}
