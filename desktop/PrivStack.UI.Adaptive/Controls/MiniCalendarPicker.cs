// ============================================================================
// File: MiniCalendarPicker.cs
// Description: A compact calendar date picker that displays a mini calendar
//              grid in a popup. Matches the PrivStack Calendar plugin's sidebar
//              mini-calendar style with month navigation, today highlight, and
//              selected-day ring. Drop-in replacement for CalendarDatePicker.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// Compact calendar picker that shows the selected date as a button and opens a
/// mini-calendar popup on click. Designed as a themed, self-contained replacement
/// for <see cref="CalendarDatePicker"/>.
/// </summary>
public sealed class MiniCalendarPicker : Border
{
    // -------------------------------------------------------------------------
    // Styled Properties
    // -------------------------------------------------------------------------

    public static readonly StyledProperty<DateTimeOffset?> SelectedDateProperty =
        AvaloniaProperty.Register<MiniCalendarPicker, DateTimeOffset?>(
            nameof(SelectedDate), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<MiniCalendarPicker, string>(
            nameof(Watermark), "Select date...");

    public static readonly StyledProperty<string> DateFormatProperty =
        AvaloniaProperty.Register<MiniCalendarPicker, string>(
            nameof(DateFormat), "MMM d, yyyy");

    public DateTimeOffset? SelectedDate
    {
        get => GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string DateFormat
    {
        get => GetValue(DateFormatProperty);
        set => SetValue(DateFormatProperty, value);
    }

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------

    private DateTimeOffset _displayMonth;
    private readonly List<MiniCalDay> _days = [];

    private readonly TextBlock _displayText;
    private TextBlock _monthLabel = null!;
    private readonly Popup _popup;
    private UniformGrid _dayGrid = null!;
    private readonly Button _triggerButton;
    private readonly Button _clearButton;
    private Border _popupBorder = null!;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public MiniCalendarPicker()
    {
        _displayMonth = DateTimeOffset.Now;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        // Trigger button — shows current date or watermark
        _displayText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var calendarIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(
                "M19 4h-1V2h-2v2H8V2H6v2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 " +
                "2-.9 2-2V6c0-1.1-.9-2-2-2zm0 16H5V10h14v10zm0-12H5V6h14v2z"),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clearButton = new Button
        {
            Content = "\u2715",
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
        };
        _clearButton.Click += (_, _) =>
        {
            SelectedDate = null;
            _popup!.IsOpen = false;
        };

        var triggerContent = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
        };
        triggerContent.Children.Add(calendarIcon);
        Grid.SetColumn(calendarIcon, 0);

        _displayText.Margin = new Thickness(8, 0, 8, 0);
        triggerContent.Children.Add(_displayText);
        Grid.SetColumn(_displayText, 1);

        triggerContent.Children.Add(_clearButton);
        Grid.SetColumn(_clearButton, 2);

        _triggerButton = new Button
        {
            Content = triggerContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _triggerButton.Click += (_, _) =>
        {
            if (SelectedDate.HasValue)
                _displayMonth = SelectedDate.Value;
            else
                _displayMonth = DateTimeOffset.Now;
            RebuildDayGrid();
            // Match popup width to trigger button
            if (_triggerButton.Bounds.Width > 0)
                _popupBorder.Width = _triggerButton.Bounds.Width;
            _popup!.IsOpen = !_popup.IsOpen;
        };

        // Popup calendar content
        var popupContent = BuildPopupContent();

        _popup = new Popup
        {
            PlacementTarget = _triggerButton,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = popupContent,
        };

        var root = new Panel();
        root.Children.Add(_triggerButton);
        root.Children.Add(_popup);

        Child = root;
        UpdateDisplayText();
    }

    // -------------------------------------------------------------------------
    // Popup content builder
    // -------------------------------------------------------------------------

    private Border BuildPopupContent()
    {
        // Month navigation
        var prevButton = new Button
        {
            Content = "\u25C2",
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            MinWidth = 0,
            MinHeight = 0,
        };
        prevButton.Click += (_, _) =>
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            RebuildDayGrid();
        };

        _monthLabel = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nextButton = new Button
        {
            Content = "\u25B8",
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            MinWidth = 0,
            MinHeight = 0,
        };
        nextButton.Click += (_, _) =>
        {
            _displayMonth = _displayMonth.AddMonths(1);
            RebuildDayGrid();
        };

        var navGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        navGrid.Children.Add(prevButton);
        Grid.SetColumn(prevButton, 0);
        navGrid.Children.Add(_monthLabel);
        Grid.SetColumn(_monthLabel, 1);
        navGrid.Children.Add(nextButton);
        Grid.SetColumn(nextButton, 2);

        // Day-of-week headers
        var headerGrid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var day in new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" })
        {
            var tb = new TextBlock
            {
                Text = day,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 10,
            };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("ThemeTextMutedBrush"));
            headerGrid.Children.Add(tb);
        }

        // Day grid
        _dayGrid = new UniformGrid { Columns = 7 };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(navGrid);
        stack.Children.Add(headerGrid);
        stack.Children.Add(_dayGrid);

        _popupBorder = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            MinWidth = 220,
        };
        _popupBorder.Bind(Border.BackgroundProperty,
            _popupBorder.GetResourceObservable("ThemeSurfaceElevatedBrush"));
        _popupBorder.Bind(Border.BorderBrushProperty,
            _popupBorder.GetResourceObservable("ThemeBorderSubtleBrush"));
        _popupBorder.BorderThickness = new Thickness(1);

        _popupBorder.Child = stack;
        return _popupBorder;
    }

    // -------------------------------------------------------------------------
    // Day grid generation
    // -------------------------------------------------------------------------

    private void RebuildDayGrid()
    {
        _dayGrid.Children.Clear();
        _days.Clear();

        _monthLabel.Text = _displayMonth.ToString("MMMM yyyy");

        var firstOfMonth = new DateTimeOffset(
            _displayMonth.Year, _displayMonth.Month, 1, 0, 0, 0, _displayMonth.Offset);
        var startOffset = (int)firstOfMonth.DayOfWeek;
        var displayStart = firstOfMonth.AddDays(-startOffset);

        var daysInMonth = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);
        var lastOfMonth = firstOfMonth.AddDays(daysInMonth - 1);
        var endOffset = 6 - (int)lastOfMonth.DayOfWeek;
        var totalDays = startOffset + daysInMonth + endOffset;

        var today = DateTime.Today;
        var selectedDate = SelectedDate?.LocalDateTime.Date;

        for (var i = 0; i < totalDays; i++)
        {
            var date = displayStart.AddDays(i);
            var isCurrentMonth = date.Month == _displayMonth.Month &&
                                 date.Year == _displayMonth.Year;
            var isToday = date.LocalDateTime.Date == today;
            var isSelected = selectedDate.HasValue &&
                             date.LocalDateTime.Date == selectedDate.Value;

            var day = new MiniCalDay(date, date.Day, isCurrentMonth, isToday, isSelected);
            _days.Add(day);
            _dayGrid.Children.Add(BuildDayCell(day));
        }
    }

    private Button BuildDayCell(MiniCalDay day)
    {
        // Day number text
        var dayText = new TextBlock
        {
            Text = day.DayNumber.ToString(),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (day.IsToday)
        {
            dayText.Bind(TextBlock.ForegroundProperty,
                dayText.GetResourceObservable("ThemeTextOnAccentBrush"));
            dayText.FontWeight = FontWeight.Bold;
        }
        else if (!day.IsCurrentMonth)
        {
            dayText.Bind(TextBlock.ForegroundProperty,
                dayText.GetResourceObservable("ThemeTextMutedBrush"));
            dayText.Opacity = 0.5;
        }
        else
        {
            dayText.Bind(TextBlock.ForegroundProperty,
                dayText.GetResourceObservable("ThemeTextPrimaryBrush"));
        }

        // Container panel with highlight circles
        var panel = new Panel { Width = 26, Height = 24 };

        // Today circle (solid primary)
        if (day.IsToday)
        {
            var todayCircle = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            todayCircle.Bind(Border.BackgroundProperty,
                todayCircle.GetResourceObservable("ThemePrimaryBrush"));
            panel.Children.Add(todayCircle);
        }

        // Selected ring (outline)
        if (day.IsSelected)
        {
            var selectedRing = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            selectedRing.Bind(Border.BorderBrushProperty,
                selectedRing.GetResourceObservable("ThemePrimaryBrush"));
            panel.Children.Add(selectedRing);
        }

        panel.Children.Add(dayText);

        var btn = new Button
        {
            Content = panel,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Width = 28,
            Height = 32,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        btn.Click += (_, _) =>
        {
            SelectedDate = day.Date;
            _popup.IsOpen = false;
        };

        return btn;
    }

    // -------------------------------------------------------------------------
    // Display text
    // -------------------------------------------------------------------------

    private void UpdateDisplayText()
    {
        if (SelectedDate.HasValue)
        {
            _displayText.Text = SelectedDate.Value.LocalDateTime.ToString(DateFormat);
            _displayText.Bind(TextBlock.ForegroundProperty,
                _displayText.GetResourceObservable("ThemeTextPrimaryBrush"));
            _clearButton.IsVisible = true;
        }
        else
        {
            _displayText.Text = Watermark;
            _displayText.Bind(TextBlock.ForegroundProperty,
                _displayText.GetResourceObservable("ThemeTextMutedBrush"));
            _clearButton.IsVisible = false;
        }
    }

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

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
        _triggerButton.Bind(Button.ForegroundProperty,
            _triggerButton.GetResourceObservable("ThemeTextPrimaryBrush"));
        _clearButton.Bind(Button.ForegroundProperty,
            _clearButton.GetResourceObservable("ThemeTextMutedBrush"));

        var icon = (_triggerButton.Content as Grid)?.Children[0] as Avalonia.Controls.Shapes.Path;
        icon?.Bind(Avalonia.Controls.Shapes.Path.FillProperty,
            icon.GetResourceObservable("ThemeTextMutedBrush"));

        _monthLabel.Bind(TextBlock.ForegroundProperty,
            _monthLabel.GetResourceObservable("ThemeTextPrimaryBrush"));
        _monthLabel.Bind(TextBlock.FontSizeProperty,
            _monthLabel.GetResourceObservable("ThemeFontSizeSm"));

        UpdateDisplayText();
    }

    // -------------------------------------------------------------------------
    // Property change handler
    // -------------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedDateProperty)
        {
            UpdateDisplayText();
            if (_popup.IsOpen)
                RebuildDayGrid();
        }
        else if (change.Property == WatermarkProperty || change.Property == DateFormatProperty)
        {
            UpdateDisplayText();
        }
    }

    // -------------------------------------------------------------------------
    // Day model (internal)
    // -------------------------------------------------------------------------

    private sealed record MiniCalDay(
        DateTimeOffset Date,
        int DayNumber,
        bool IsCurrentMonth,
        bool IsToday,
        bool IsSelected);
}
