using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Horizontal popover showing 6 skin tone variants for a given base emoji.
/// </summary>
public partial class SkinTonePopover : UserControl
{
    public event Action<SkinTone>? SkinToneSelected;

    public SkinTonePopover()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the popover with variants of the given base emoji.
    /// </summary>
    public void Show(string baseEmoji, Point position)
    {
        var panel = this.FindControl<StackPanel>("TonePanel");
        if (panel is null) return;

        panel.Children.Clear();

        var variants = SkinToneModifier.GetVariants(baseEmoji);
        for (var i = 0; i < variants.Length; i++)
        {
            var tone = (SkinTone)i;
            var border = new Border
            {
                Width = 40,
                Height = 36,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = variants[i],
                    FontSize = GetDouble("ThemeFontSize2Xl", 22),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var currentTone = tone;
            border.PointerPressed += (_, _) =>
            {
                SkinToneSelected?.Invoke(currentTone);
                IsVisible = false;
            };
            border.PointerEntered += (s, _) =>
            {
                if (s is Border b)
                    b.Background = Application.Current?.FindResource("ThemePrimaryMutedBrush") as IBrush
                                   ?? Brushes.Transparent;
            };
            border.PointerExited += (s, _) =>
            {
                if (s is Border b)
                    b.Background = Brushes.Transparent;
            };

            panel.Children.Add(border);
        }

        Margin = new Thickness(position.X, position.Y, 0, 0);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }

    private static double GetDouble(string key, double fallback)
    {
        var app = Avalonia.Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }
}
