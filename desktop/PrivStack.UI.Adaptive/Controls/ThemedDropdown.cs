// ============================================================================
// File: ThemedDropdown.cs
// Description: Standardized dropdown control wrapping a ComboBox with enforced
//              theme-aware styling, consistent padding/radius, and size variants.
// ============================================================================

using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// Size variant for <see cref="ThemedDropdown"/>.
/// </summary>
public enum DropdownSize
{
    Standard,
    Compact,
    Dense,
}

/// <summary>
/// A themed dropdown that wraps a native <see cref="ComboBox"/> with
/// standardized padding, corner radius, and background transitions.
/// </summary>
public sealed class ThemedDropdown : Border
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ThemedDropdown, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<ThemedDropdown, object?>(nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ThemedDropdown, int>(nameof(SelectedIndex), -1,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<ThemedDropdown, string?>(nameof(PlaceholderText));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<ThemedDropdown, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<DropdownSize> SizeProperty =
        AvaloniaProperty.Register<ThemedDropdown, DropdownSize>(nameof(Size), DropdownSize.Standard);

    public static readonly StyledProperty<bool> ShowBorderProperty =
        AvaloniaProperty.Register<ThemedDropdown, bool>(nameof(ShowBorder), false);

    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<ThemedDropdown, double>(nameof(MaxDropDownHeight), 300);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public DropdownSize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool ShowBorder
    {
        get => GetValue(ShowBorderProperty);
        set => SetValue(ShowBorderProperty, value);
    }

    public double MaxDropDownHeight
    {
        get => GetValue(MaxDropDownHeightProperty);
        set => SetValue(MaxDropDownHeightProperty, value);
    }

    /// <summary>Raised when the selected item changes.</summary>
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

    private readonly ComboBox _comboBox;
    private bool _suppressSelectionSync;

    public ThemedDropdown()
    {
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _comboBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 300,
        };

        _comboBox.Bind(ComboBox.ForegroundProperty,
            _comboBox.GetResourceObservable("ThemeTextPrimaryBrush"));
        _comboBox.Bind(ComboBox.BackgroundProperty,
            _comboBox.GetResourceObservable("ThemeSurfaceElevatedBrush"));

        _comboBox.SelectionChanged += OnInnerSelectionChanged;

        Child = _comboBox;
        ApplySizeVariant(DropdownSize.Standard);
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
        ApplyBorderStyle(ShowBorder);
        ApplyCornerRadius(Size);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
            _comboBox.ItemsSource = change.GetNewValue<IEnumerable?>();
        else if (change.Property == SelectedItemProperty)
            SyncSelectedItemToCombo(change.GetNewValue<object?>());
        else if (change.Property == SelectedIndexProperty)
            SyncSelectedIndexToCombo(change.GetNewValue<int>());
        else if (change.Property == PlaceholderTextProperty)
            _comboBox.PlaceholderText = change.GetNewValue<string?>();
        else if (change.Property == ItemTemplateProperty)
            _comboBox.ItemTemplate = change.GetNewValue<IDataTemplate?>();
        else if (change.Property == SizeProperty)
            ApplySizeVariant(change.GetNewValue<DropdownSize>());
        else if (change.Property == ShowBorderProperty)
            ApplyBorderStyle(change.GetNewValue<bool>());
        else if (change.Property == MaxDropDownHeightProperty)
            _comboBox.MaxDropDownHeight = change.GetNewValue<double>();
    }

    private void OnInnerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        _suppressSelectionSync = true;

        SetCurrentValue(SelectedItemProperty, _comboBox.SelectedItem);
        SetCurrentValue(SelectedIndexProperty, _comboBox.SelectedIndex);

        _suppressSelectionSync = false;
        SelectionChanged?.Invoke(this, e);
    }

    private void SyncSelectedItemToCombo(object? item)
    {
        if (_suppressSelectionSync) return;
        _suppressSelectionSync = true;
        _comboBox.SelectedItem = item;
        SetCurrentValue(SelectedIndexProperty, _comboBox.SelectedIndex);
        _suppressSelectionSync = false;
    }

    private void SyncSelectedIndexToCombo(int index)
    {
        if (_suppressSelectionSync) return;
        _suppressSelectionSync = true;
        _comboBox.SelectedIndex = index;
        SetCurrentValue(SelectedItemProperty, _comboBox.SelectedItem);
        _suppressSelectionSync = false;
    }

    private void ApplySizeVariant(DropdownSize size)
    {
        _comboBox.Padding = size switch
        {
            DropdownSize.Standard => new Thickness(12, 8),
            DropdownSize.Compact => new Thickness(8, 6),
            DropdownSize.Dense => new Thickness(4, 3),
            _ => new Thickness(12, 8),
        };
        ApplyCornerRadius(size);
    }

    private void ApplyCornerRadius(DropdownSize size)
    {
        var key = size == DropdownSize.Dense ? "ThemeRadiusXs" : "ThemeRadiusSm";
        if (TryGetResource(key, ActualThemeVariant, out var obj) && obj is CornerRadius cr)
            _comboBox.CornerRadius = cr;
        else
        {
            var app = Application.Current;
            if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var appObj) == true
                && appObj is CornerRadius appCr)
                _comboBox.CornerRadius = appCr;
            else
                _comboBox.CornerRadius = new CornerRadius(size == DropdownSize.Dense ? 4 : 6);
        }
    }

    private void ApplyBorderStyle(bool showBorder)
    {
        if (showBorder)
        {
            _comboBox.BorderThickness = new Thickness(1);
            _comboBox.BorderBrush = GetBrush("ThemeBorderSubtleBrush", Brushes.Gray);
        }
        else
        {
            _comboBox.BorderThickness = new Thickness(0);
            _comboBox.BorderBrush = null;
        }
    }

    private IBrush? GetBrush(string key, IBrush? fallback)
    {
        if (TryGetResource(key, ActualThemeVariant, out var obj) && obj is IBrush brush)
            return brush;
        var app = Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var appObj) == true
            && appObj is IBrush appBrush)
            return appBrush;
        return fallback;
    }
}
