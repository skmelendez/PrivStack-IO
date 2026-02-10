using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

public partial class EmojiPicker : UserControl
{
    private const int ColumnsPerRow = 6;
    private const int LongPressMs = 500;

    private static IBrush HighlightBrush => Application.Current?.FindResource("ThemePrimaryMutedBrush") as IBrush
        ?? new SolidColorBrush(Color.FromArgb(40, 100, 100, 255));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    private DispatcherTimer? _longPressTimer;
    private Border? _longPressBorder;
    private SkinTonePopover? _skinTonePopover;

    public EmojiPicker()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == "IsOpen")
                {
                    var isOpen = DataContext?.GetType().GetProperty("IsOpen")?.GetValue(DataContext) as bool?;
                    if (isOpen == true)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var searchBox = this.FindControl<TextBox>("SearchBox");
                            searchBox?.Focus();
                            searchBox?.SelectAll();
                            UpdateHighlights();
                        }, DispatcherPriority.Background);
                    }
                    else
                    {
                        HideSkinTonePopover();
                    }
                }
                else if (args.PropertyName is "IsCategoryFocused" or "SelectedCategoryItem" or "SelectedEmoji")
                {
                    Dispatcher.UIThread.Post(UpdateHighlights, DispatcherPriority.Background);
                }
            };
        }
    }

    private object? GetProp(object? obj, string name) =>
        obj?.GetType().GetProperty(name)?.GetValue(obj);

    private ICommand? GetCommand(string name) =>
        DataContext?.GetType().GetProperty(name)?.GetValue(DataContext) as ICommand;

    private void UpdateHighlights()
    {
        var dc = DataContext;
        if (dc == null) return;

        var isCategoryFocused = GetProp(dc, "IsCategoryFocused") as bool? ?? false;
        var selectedCategoryItem = GetProp(dc, "SelectedCategoryItem");
        var selectedEmoji = GetProp(dc, "SelectedEmoji");

        var categoryItems = this.FindControl<ItemsControl>("CategoryTabs");
        if (categoryItems != null)
        {
            foreach (var container in categoryItems.GetRealizedContainers())
            {
                if (container is ContentPresenter { Child: Border border })
                {
                    var category = border.DataContext;
                    var isSelected = isCategoryFocused && ReferenceEquals(selectedCategoryItem, category);
                    border.Background = isSelected ? HighlightBrush : TransparentBrush;
                }
            }
        }

        var emojiGrid = this.FindControl<ItemsControl>("EmojiGrid");
        if (emojiGrid != null)
        {
            Border? selectedBorder = null;
            foreach (var container in emojiGrid.GetRealizedContainers())
            {
                if (container is ContentPresenter { Child: Border border })
                {
                    var emoji = border.DataContext;
                    var isSelected = !isCategoryFocused && ReferenceEquals(selectedEmoji, emoji);
                    border.Background = isSelected ? HighlightBrush : TransparentBrush;
                    if (isSelected)
                        selectedBorder = border;
                }
            }

            selectedBorder?.BringIntoView();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var dc = DataContext;
        if (dc == null) return;

        var isOpen = GetProp(dc, "IsOpen") as bool?;
        if (isOpen != true) return;

        var isCategoryFocused = GetProp(dc, "IsCategoryFocused") as bool? ?? false;

        var searchBox = this.FindControl<TextBox>("SearchBox");
        var searchQuery = GetProp(dc, "SearchQuery") as string;
        var isSearchEmpty = string.IsNullOrEmpty(searchBox?.Text);
        var isAtSearchStart = searchBox == null || searchBox.CaretIndex == 0;
        var isAtSearchEnd = searchBox == null || searchBox.CaretIndex >= (searchBox.Text?.Length ?? 0);

        switch (e.Key)
        {
            case Key.Escape:
                if (_skinTonePopover is { IsVisible: true })
                {
                    HideSkinTonePopover();
                }
                else if (!string.IsNullOrEmpty(searchQuery))
                {
                    dc.GetType().GetProperty("SearchQuery")?.SetValue(dc, string.Empty);
                }
                else
                {
                    GetCommand("CloseCommand")?.Execute(null);
                }
                e.Handled = true;
                break;

            case Key.Enter:
                if (isCategoryFocused)
                {
                    GetCommand("FocusEmojisCommand")?.Execute(null);
                }
                else
                {
                    var selectedEmoji = GetProp(dc, "SelectedEmoji");
                    if (selectedEmoji != null)
                    {
                        GetCommand("SelectEmojiCommand")?.Execute(selectedEmoji);
                    }
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (isCategoryFocused)
                {
                    searchBox?.Focus();
                }
                else
                {
                    var selectedEmoji = GetProp(dc, "SelectedEmoji");
                    var filteredEmojis = GetProp(dc, "FilteredEmojis");
                    var currentIndex = 0;
                    if (selectedEmoji != null && filteredEmojis != null)
                    {
                        var indexOfMethod = filteredEmojis.GetType().GetMethod("IndexOf");
                        if (indexOfMethod != null)
                            currentIndex = (int)(indexOfMethod.Invoke(filteredEmojis, [selectedEmoji]) ?? 0);
                    }

                    if (currentIndex < ColumnsPerRow)
                    {
                        GetCommand("FocusCategoriesCommand")?.Execute(null);
                    }
                    else
                    {
                        GetCommand("SelectPreviousRowCommand")?.Execute(ColumnsPerRow);
                    }
                }
                e.Handled = true;
                break;

            case Key.Down:
                if (isCategoryFocused)
                {
                    GetCommand("FocusEmojisCommand")?.Execute(null);
                }
                else
                {
                    var selectedEmoji = GetProp(dc, "SelectedEmoji");
                    var filteredEmojis = GetProp(dc, "FilteredEmojis");
                    var count = filteredEmojis?.GetType().GetProperty("Count")?.GetValue(filteredEmojis) as int? ?? 0;

                    if (selectedEmoji == null && count > 0)
                    {
                        GetCommand("FocusEmojisCommand")?.Execute(null);
                    }
                    else
                    {
                        GetCommand("SelectNextRowCommand")?.Execute(ColumnsPerRow);
                    }
                }
                e.Handled = true;
                break;

            case Key.Left:
                if (isCategoryFocused)
                {
                    GetCommand("SelectPreviousCategoryCommand")?.Execute(null);
                    e.Handled = true;
                }
                else if (isSearchEmpty || isAtSearchStart)
                {
                    GetCommand("SelectPreviousCommand")?.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Right:
                if (isCategoryFocused)
                {
                    GetCommand("SelectNextCategoryCommand")?.Execute(null);
                    e.Handled = true;
                }
                else if (isSearchEmpty || isAtSearchEnd)
                {
                    GetCommand("SelectNextCommand")?.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        HideSkinTonePopover();
        GetCommand("CloseCommand")?.Execute(null);
    }

    private void OnEmojiPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;

        var emojiItem = border.DataContext as EmojiItem;
        if (emojiItem == null) return;

        // Check if this emoji supports skin tones for long-press behavior
        if (emojiItem.SupportsSkinTone)
        {
            _longPressBorder = border;
            StartLongPressTimer(emojiItem, border);

            // Subscribe to pointer release to cancel long-press and do normal click
            border.PointerReleased += OnEmojiPointerReleased;
        }
        else
        {
            GetCommand("SelectEmojiCommand")?.Execute(emojiItem);
        }
    }

    private void OnEmojiPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border)
            border.PointerReleased -= OnEmojiPointerReleased;

        CancelLongPress();

        // Normal click — select the emoji
        if (_longPressBorder?.DataContext is EmojiItem item)
        {
            GetCommand("SelectEmojiCommand")?.Execute(item);
        }
        _longPressBorder = null;
    }

    private void StartLongPressTimer(EmojiItem item, Border border)
    {
        CancelLongPress();
        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LongPressMs),
        };
        _longPressTimer.Tick += (_, _) =>
        {
            CancelLongPress();
            border.PointerReleased -= OnEmojiPointerReleased;
            _longPressBorder = null;
            ShowSkinTonePopover(item, border);
        };
        _longPressTimer.Start();
    }

    private void CancelLongPress()
    {
        _longPressTimer?.Stop();
        _longPressTimer = null;
    }

    private void ShowSkinTonePopover(EmojiItem item, Border anchor)
    {
        HideSkinTonePopover();

        _skinTonePopover = new SkinTonePopover();
        _skinTonePopover.SkinToneSelected += tone =>
        {
            SkinToneService.PreferredTone = tone;
            if (DataContext is EmojiPickerViewModel vm)
                vm.RefreshAfterSkinToneChange();
            HideSkinTonePopover();
        };

        // Position above the anchor border
        var pos = anchor.TranslatePoint(new Point(0, -44), this) ?? new Point(0, 0);
        _skinTonePopover.Show(item.Emoji, pos);

        // Add to the overlay grid (as a sibling of the picker container)
        var overlayPanel = this.FindControl<Grid>("OverlayGrid");
        overlayPanel?.Children.Add(_skinTonePopover);
    }

    private void HideSkinTonePopover()
    {
        if (_skinTonePopover is null) return;

        var overlayPanel = this.FindControl<Grid>("OverlayGrid");
        overlayPanel?.Children.Remove(_skinTonePopover);
        _skinTonePopover = null;
    }

    private void OnCategoryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            var category = border.DataContext;
            if (category != null)
            {
                GetCommand("SelectCategoryCommand")?.Execute(category);
                DataContext?.GetType().GetProperty("IsCategoryFocused")?.SetValue(DataContext, true);
                UpdateHighlights();
            }
        }
    }
}
