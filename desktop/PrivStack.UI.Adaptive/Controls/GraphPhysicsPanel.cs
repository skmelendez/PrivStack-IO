using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// Reusable 4-slider physics panel for graph views.
/// Bind the <see cref="Settings"/> property to a <see cref="GraphPhysicsSettings"/> instance.
/// Renders labeled sliders for Repel distance, Center force, Link distance, and Link force.
/// </summary>
public sealed class GraphPhysicsPanel : Border
{
    public static readonly StyledProperty<GraphPhysicsSettings?> SettingsProperty =
        AvaloniaProperty.Register<GraphPhysicsPanel, GraphPhysicsSettings?>(nameof(Settings));

    public GraphPhysicsSettings? Settings
    {
        get => GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public GraphPhysicsPanel()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(BuildSliderRow("Repel distance", nameof(GraphPhysicsSettings.RepelSlider), nameof(GraphPhysicsSettings.RepelDisplay)));
        stack.Children.Add(BuildSliderRow("Center force", nameof(GraphPhysicsSettings.CenterForceSlider), nameof(GraphPhysicsSettings.CenterForceDisplay)));
        stack.Children.Add(BuildSliderRow("Link distance", nameof(GraphPhysicsSettings.LinkDistanceSlider), nameof(GraphPhysicsSettings.LinkDistanceDisplay)));
        stack.Children.Add(BuildSliderRow("Link force", nameof(GraphPhysicsSettings.LinkForceSlider), nameof(GraphPhysicsSettings.LinkForceDisplay)));
        Child = stack;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SettingsProperty && Child is StackPanel stack)
        {
            stack.DataContext = change.NewValue;
        }
    }

    private static double GetDouble(string key, double fallback)
    {
        var app = Avalonia.Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }

    private static StackPanel BuildSliderRow(string label, string sliderBinding, string displayBinding)
    {
        var row = new StackPanel { Spacing = 2 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = GetDouble("ThemeFontSizeXs", 11),
            Opacity = 0.7,
        };
        Grid.SetColumn(labelBlock, 0);
        header.Children.Add(labelBlock);

        var displayBlock = new TextBlock
        {
            FontSize = GetDouble("ThemeFontSizeXs", 11),
            Opacity = 0.5,
            MinWidth = 32,
        };
        displayBlock[!TextBlock.TextProperty] = new Binding(displayBinding) { StringFormat = "{0:F0}%" };
        Grid.SetColumn(displayBlock, 1);
        header.Children.Add(displayBlock);

        row.Children.Add(header);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
        };
        slider[!RangeBase.ValueProperty] = new Binding(sliderBinding) { Mode = BindingMode.TwoWay };
        row.Children.Add(slider);

        return row;
    }
}
