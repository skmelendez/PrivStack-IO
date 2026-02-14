using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.Controls;

public partial class PropertyEditorControl : UserControl
{
    public PropertyEditorControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PropertyValueViewModel vm)
        {
            BuildEditor(vm);
            WireEditButton(vm);
        }
    }

    private void WireEditButton(PropertyValueViewModel vm)
    {
        var editBtn = this.FindControl<Button>("EditDefButton");
        if (editBtn == null) return;
        editBtn.Click -= OnEditDefButtonClick;
        editBtn.Click += OnEditDefButtonClick;
    }

    private void OnEditDefButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not PropertyValueViewModel propVm) return;

        var parent = this.Parent as Control;
        while (parent != null)
        {
            if (parent.DataContext is InfoPanelViewModel infoPanelVm)
            {
                infoPanelVm.EditPropertyDefinitionCommand.Execute(propVm);
                return;
            }
            parent = parent.Parent as Control;
        }
    }

    private void BuildEditor(PropertyValueViewModel vm)
    {
        var host = this.FindControl<ContentControl>("EditorHost");
        if (host == null) return;

        host.Content = vm.Type switch
        {
            PropertyType.Text => BuildTextEditor(vm),
            PropertyType.Number => BuildNumberEditor(vm),
            PropertyType.Date => BuildDateEditor(vm),
            PropertyType.Checkbox => BuildCheckboxEditor(vm),
            PropertyType.Select => BuildSelectEditor(vm),
            PropertyType.MultiSelect => BuildMultiSelectEditor(vm),
            PropertyType.Url => BuildUrlEditor(vm),
            PropertyType.Relation => BuildRelationEditor(vm),
            _ => BuildTextEditor(vm),
        };
    }

    private static Control BuildTextEditor(PropertyValueViewModel vm)
    {
        return new TextBox
        {
            [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.TextValue)) { Mode = Avalonia.Data.BindingMode.TwoWay },
            Watermark = "Empty",
            Classes = { "prop-input" },
        };
    }

    private static Control BuildNumberEditor(PropertyValueViewModel vm)
    {
        var numericUpDown = new NumericUpDown
        {
            [!NumericUpDown.ValueProperty] = new Avalonia.Data.Binding(nameof(vm.NumberValue)) { Mode = Avalonia.Data.BindingMode.TwoWay },
            FormatString = "G",
            Increment = 1,
            Minimum = decimal.MinValue,
            Maximum = decimal.MaxValue,
            ShowButtonSpinner = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        numericUpDown.SetValue(BackgroundProperty, Brushes.Transparent);
        numericUpDown.SetValue(ForegroundProperty, GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White);
        numericUpDown.SetValue(BorderBrushProperty, Brushes.Transparent);
        numericUpDown.BorderThickness = new Thickness(0);
        numericUpDown.Padding = new Thickness(4, 2);
        numericUpDown.FontSize = ThemeDouble("ThemeFontSizeSmMd", 13);
        numericUpDown.MinHeight = 0;
        return numericUpDown;
    }

    private static Control BuildDateEditor(PropertyValueViewModel vm)
    {
        var picker = new CalendarDatePicker
        {
            [!CalendarDatePicker.SelectedDateProperty] = new Avalonia.Data.Binding(nameof(vm.DateValue))
            {
                Mode = Avalonia.Data.BindingMode.TwoWay,
                Converter = new DateTimeOffsetToDateTimeConverter(),
            },
            Watermark = "Select date...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        picker.SetValue(ForegroundProperty, GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White);
        picker.FontSize = ThemeDouble("ThemeFontSizeSmMd", 13);
        return picker;
    }

    private static Control BuildCheckboxEditor(PropertyValueViewModel vm)
    {
        var toggle = new ToggleSwitch
        {
            [!ToggleSwitch.IsCheckedProperty] = new Avalonia.Data.Binding(nameof(vm.CheckboxValue)) { Mode = Avalonia.Data.BindingMode.TwoWay },
            OnContent = "Yes",
            OffContent = "No",
        };
        toggle.SetValue(ForegroundProperty, GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White);
        toggle.FontSize = ThemeDouble("ThemeFontSizeSmMd", 13);
        return toggle;
    }

    private static Control BuildSelectEditor(PropertyValueViewModel vm)
    {
        var comboBox = new ComboBox
        {
            ItemsSource = vm.Options ?? [],
            [!ComboBox.SelectedItemProperty] = new Avalonia.Data.Binding(nameof(vm.SelectedOption)) { Mode = Avalonia.Data.BindingMode.TwoWay },
            PlaceholderText = "Select...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        comboBox.SetValue(BackgroundProperty, Brushes.Transparent);
        comboBox.SetValue(ForegroundProperty, GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White);
        comboBox.SetValue(BorderBrushProperty, Brushes.Transparent);
        comboBox.BorderThickness = new Thickness(0);
        comboBox.Padding = new Thickness(4, 2);
        comboBox.FontSize = ThemeDouble("ThemeFontSizeSmMd", 13);
        comboBox.MinHeight = 0;
        return comboBox;
    }

    private static Control BuildMultiSelectEditor(PropertyValueViewModel vm)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        if (vm.Options != null)
        {
            foreach (var option in vm.Options)
            {
                var isChecked = vm.SelectedOptions.Contains(option);
                var cb = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = option,
                        FontSize = ThemeDouble("ThemeFontSizeSm", 12),
                        Foreground = GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 1, 8, 1),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                var capturedOption = option;
                cb.IsCheckedChanged += (_, _) => _ = vm.ToggleMultiSelectOptionAsync(capturedOption);
                panel.Children.Add(cb);
            }
        }
        return panel;
    }

    private static Control BuildUrlEditor(PropertyValueViewModel vm)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
        };

        var textBox = new TextBox
        {
            [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.UrlValue)) { Mode = Avalonia.Data.BindingMode.TwoWay },
            Watermark = "https://...",
            Classes = { "prop-input" },
        };
        Grid.SetColumn(textBox, 0);

        var linkIcon = new IconControl
        {
            Icon = "ExternalLink",
            Size = 12,
            StrokeThickness = 1.5,
            Stroke = GetResource("ThemeTextMutedBrush") as IBrush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        Grid.SetColumn(linkIcon, 1);

        grid.Children.Add(textBox);
        grid.Children.Add(linkIcon);
        return grid;
    }

    private static Control BuildRelationEditor(PropertyValueViewModel vm)
    {
        var container = new StackPanel { Spacing = 4 };

        // Chip display area
        var chipPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        RebuildChips(chipPanel, vm);
        vm.RelationItems.CollectionChanged += (_, _) => RebuildChips(chipPanel, vm);
        container.Children.Add(chipPanel);

        // Search input + popup
        var searchBox = new TextBox
        {
            Watermark = "Search entities...",
            Classes = { "prop-input" },
        };

        var resultsList = new StackPanel { Spacing = 0 };
        var popup = new Popup
        {
            PlacementTarget = searchBox,
            Placement = PlacementMode.Bottom,
            MaxHeight = 200,
            MinWidth = 220,
            Child = new Border
            {
                Background = GetResource("ThemeSurfaceElevatedBrush") as IBrush ?? Brushes.Black,
                BorderBrush = GetResource("ThemeBorderSubtleBrush") as IBrush ?? Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = new ScrollViewer
                {
                    MaxHeight = 180,
                    Content = resultsList,
                },
            },
        };

        DispatcherTimer? debounceTimer = null;

        searchBox.TextChanged += (_, _) =>
        {
            debounceTimer?.Stop();
            var query = searchBox.Text?.Trim() ?? "";
            if (query.Length < 2)
            {
                popup.IsOpen = false;
                resultsList.Children.Clear();
                return;
            }

            debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounceTimer.Tick += async (_, _) =>
            {
                debounceTimer.Stop();
                if (vm.EntitySearcher == null) return;

                try
                {
                    var results = await vm.EntitySearcher(query, vm.Definition.AllowedLinkTypes, 10);
                    resultsList.Children.Clear();
                    if (results.Count == 0)
                    {
                        resultsList.Children.Add(new TextBlock
                        {
                            Text = "No results",
                            FontSize = ThemeDouble("ThemeFontSizeSm", 12),
                            Foreground = GetResource("ThemeTextMutedBrush") as IBrush ?? Brushes.Gray,
                            Margin = new Thickness(6, 4),
                        });
                    }
                    else
                    {
                        foreach (var item in results)
                        {
                            var row = BuildSearchResultRow(item, vm, searchBox, popup);
                            resultsList.Children.Add(row);
                        }
                    }
                    popup.IsOpen = true;
                }
                catch
                {
                    // Ignore search errors
                }
            };
            debounceTimer.Start();
        };

        searchBox.LostFocus += (_, _) =>
        {
            // Delay close so click on popup item registers
            DispatcherTimer.RunOnce(() => { if (!popup.IsPointerOver) popup.IsOpen = false; },
                TimeSpan.FromMilliseconds(200));
        };

        container.Children.Add(searchBox);
        container.Children.Add(popup);
        return container;
    }

    private static Control BuildSearchResultRow(LinkableItem item, PropertyValueViewModel vm, TextBox searchBox, Popup popup)
    {
        var btn = new Button
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            MinHeight = 0,
            CornerRadius = new CornerRadius(4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new IconControl
                    {
                        Icon = item.Icon ?? "Link",
                        Size = 12,
                        StrokeThickness = 1.5,
                        Stroke = GetResource("ThemeTextMutedBrush") as IBrush ?? Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = item.Title,
                        FontSize = ThemeDouble("ThemeFontSizeSmMd", 13),
                        Foreground = GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = item.LinkType,
                        FontSize = ThemeDouble("ThemeFontSizeXsSm", 11),
                        Foreground = GetResource("ThemeTextMutedBrush") as IBrush ?? Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        var hoverBrush = GetResource("ThemeHoverBrush") as IBrush ?? Brushes.DarkGray;
        btn.PointerEntered += (_, _) => btn.Background = hoverBrush;
        btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;

        btn.Click += async (_, _) =>
        {
            await vm.AddRelationAsync(item);
            searchBox.Text = "";
            popup.IsOpen = false;
        };
        return btn;
    }

    private static void RebuildChips(WrapPanel panel, PropertyValueViewModel vm)
    {
        panel.Children.Clear();
        foreach (var item in vm.RelationItems)
        {
            var chip = new Border
            {
                Background = GetResource("ThemeSurfaceElevatedBrush") as IBrush ?? Brushes.DarkGray,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 4, 2),
                Margin = new Thickness(0, 0, 4, 2),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new IconControl
                        {
                            Icon = item.Icon ?? "Link",
                            Size = 10,
                            StrokeThickness = 1.5,
                            Stroke = GetResource("ThemeTextMutedBrush") as IBrush ?? Brushes.Gray,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = item.Title,
                            FontSize = ThemeDouble("ThemeFontSizeSm", 12),
                            Foreground = GetResource("ThemeTextPrimaryBrush") as IBrush ?? Brushes.White,
                            VerticalAlignment = VerticalAlignment.Center,
                            MaxWidth = 140,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            };

            var removeBtn = new Button
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(3),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new IconControl
                {
                    Icon = "X",
                    Size = 8,
                    StrokeThickness = 1.5,
                    Stroke = GetResource("ThemeTextMutedBrush") as IBrush ?? Brushes.Gray,
                },
            };
            var capturedItem = item;
            removeBtn.Click += async (_, _) => await vm.RemoveRelationAsync(capturedItem);

            ((StackPanel)chip.Child).Children.Add(removeBtn);
            panel.Children.Add(chip);
        }
    }

    private static object? GetResource(string key) =>
        Application.Current?.FindResource(key);

    private static double ThemeDouble(string key, double fallback)
    {
        var app = Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }
}

/// <summary>
/// Converts between DateTimeOffset? (ViewModel) and DateTime? (CalendarDatePicker).
/// </summary>
internal sealed class DateTimeOffsetToDateTimeConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
            return dto.LocalDateTime;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is DateTime dt)
            return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        return null;
    }
}
