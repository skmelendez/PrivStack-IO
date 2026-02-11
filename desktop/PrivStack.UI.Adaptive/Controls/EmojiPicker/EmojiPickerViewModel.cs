using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>Represents an emoji category.</summary>
public sealed record EmojiCategory(string Id, string Name, string Icon);

/// <summary>Represents an emoji item.</summary>
public sealed record EmojiItem(string Emoji, string Name, string Keywords, string CategoryId,
    bool SupportsSkinTone = false);

/// <summary>
/// ViewModel for the shared Emoji Picker (Cmd/Ctrl+E).
/// Loads emojis from EmojiDataProvider with platform filtering and skin tone support.
/// </summary>
public sealed partial class EmojiPickerViewModel : ObservableObject
{
    private const int MaxRecentEmojis = 25;
    private static readonly string RecentEmojisPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PrivStack", "recent-emojis.json");

    private Action<string>? _onEmojiSelected;
    private List<EmojiItem> _allEmojis;
    private static List<string> _recentEmojisList = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "recent";

    [ObservableProperty]
    private EmojiCategory? _selectedCategoryItem;

    [ObservableProperty]
    private EmojiItem? _selectedEmoji;

    [ObservableProperty]
    private bool _isCategoryFocused;

    public ObservableCollection<EmojiCategory> Categories { get; } = [];
    public ObservableCollection<EmojiItem> FilteredEmojis { get; } = [];

    static EmojiPickerViewModel()
    {
        LoadRecentEmojis();
    }

    public EmojiPickerViewModel(Action<string> onEmojiSelected)
    {
        _onEmojiSelected = onEmojiSelected;
        _allEmojis = EmojiDataProvider.GetEmojis().ToList();
        InitializeCategories();
    }

    /// <summary>
    /// Rebinds the selection callback. Used when reusing a singleton picker
    /// across different contexts (e.g., page icon vs inline emoji insert).
    /// </summary>
    public void SetSelectionCallback(Action<string> onEmojiSelected)
    {
        _onEmojiSelected = onEmojiSelected;
    }

    /// <summary>
    /// Refreshes the emoji list after a skin tone preference change.
    /// </summary>
    public void RefreshAfterSkinToneChange()
    {
        EmojiDataProvider.RefreshSkinTone();
        _allEmojis = EmojiDataProvider.GetEmojis().ToList();
        FilterEmojis();
    }

    private void InitializeCategories()
    {
        foreach (var cat in EmojiCategoryDefinitions.All)
            Categories.Add(cat);
    }

    private static void LoadRecentEmojis()
    {
        try
        {
            if (File.Exists(RecentEmojisPath))
            {
                var json = File.ReadAllText(RecentEmojisPath);
                _recentEmojisList = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
        }
        catch
        {
            _recentEmojisList = [];
        }
    }

    private static void SaveRecentEmojis()
    {
        try
        {
            var dir = Path.GetDirectoryName(RecentEmojisPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_recentEmojisList);
            File.WriteAllText(RecentEmojisPath, json);
        }
        catch
        {
            // Non-critical -- ignore persistence failures
        }
    }

    private static void AddToRecentEmojis(string emoji)
    {
        _recentEmojisList.Remove(emoji);
        _recentEmojisList.Insert(0, emoji);
        if (_recentEmojisList.Count > MaxRecentEmojis)
            _recentEmojisList = _recentEmojisList.Take(MaxRecentEmojis).ToList();
        SaveRecentEmojis();
    }

    partial void OnSearchQueryChanged(string value)
    {
        FilterEmojis();
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        SelectedCategoryItem = Categories.FirstOrDefault(c => c.Id == value);
        if (string.IsNullOrEmpty(SearchQuery))
        {
            FilterEmojis();
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            SearchQuery = string.Empty;
            SelectedCategory = "recent";
            IsCategoryFocused = false;
            FilterEmojis();
            SelectedEmoji = FilteredEmojis.FirstOrDefault();
        }
    }

    private void FilterEmojis()
    {
        FilteredEmojis.Clear();

        if (!string.IsNullOrEmpty(SearchQuery))
        {
            var query = SearchQuery.ToLowerInvariant().Trim();
            var filtered = _allEmojis.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));

            foreach (var emoji in filtered.Take(100))
            {
                FilteredEmojis.Add(emoji);
            }
        }
        else if (SelectedCategory == "recent")
        {
            foreach (var recentEmoji in _recentEmojisList)
            {
                var item = _allEmojis.FirstOrDefault(e => e.Emoji == recentEmoji);
                if (item != null) FilteredEmojis.Add(item);
            }

            if (FilteredEmojis.Count == 0)
            {
                foreach (var emoji in _allEmojis.Take(25))
                {
                    FilteredEmojis.Add(emoji);
                }
            }
        }
        else
        {
            foreach (var emoji in _allEmojis.Where(e => e.CategoryId == SelectedCategory))
            {
                FilteredEmojis.Add(emoji);
            }
        }

        SelectedEmoji = FilteredEmojis.FirstOrDefault();
    }

    [RelayCommand]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void SelectEmoji(EmojiItem? item)
    {
        if (item == null) return;
        AddToRecentEmojis(item.Emoji);
        _onEmojiSelected?.Invoke(item.Emoji);
        Close();
    }

    [RelayCommand]
    private void SelectCurrent()
    {
        if (SelectedEmoji != null)
            SelectEmoji(SelectedEmoji);
    }

    [RelayCommand]
    private void SelectCategory(EmojiCategory? category)
    {
        if (category != null)
        {
            SelectedCategory = category.Id;
            IsCategoryFocused = false;
        }
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (FilteredEmojis.Count == 0) return;
        var idx = SelectedEmoji != null ? FilteredEmojis.IndexOf(SelectedEmoji) : -1;
        var next = (idx + 1) % FilteredEmojis.Count;
        SelectedEmoji = FilteredEmojis[next];
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (FilteredEmojis.Count == 0) return;
        var idx = SelectedEmoji != null ? FilteredEmojis.IndexOf(SelectedEmoji) : 0;
        var prev = idx <= 0 ? FilteredEmojis.Count - 1 : idx - 1;
        SelectedEmoji = FilteredEmojis[prev];
    }

    [RelayCommand]
    private void SelectNextCategory()
    {
        if (Categories.Count == 0) return;
        var idx = SelectedCategoryItem != null ? Categories.IndexOf(SelectedCategoryItem) : -1;
        var next = (idx + 1) % Categories.Count;
        SelectedCategoryItem = Categories[next];
        SelectedCategory = SelectedCategoryItem.Id;
    }

    [RelayCommand]
    private void SelectPreviousCategory()
    {
        if (Categories.Count == 0) return;
        var idx = SelectedCategoryItem != null ? Categories.IndexOf(SelectedCategoryItem) : 0;
        var prev = idx <= 0 ? Categories.Count - 1 : idx - 1;
        SelectedCategoryItem = Categories[prev];
        SelectedCategory = SelectedCategoryItem.Id;
    }

    [RelayCommand]
    private void FocusCategories()
    {
        IsCategoryFocused = true;
        SelectedCategoryItem ??= Categories.FirstOrDefault();
    }

    [RelayCommand]
    private void FocusEmojis()
    {
        IsCategoryFocused = false;
        SelectedEmoji ??= FilteredEmojis.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectPreviousRow(int columnsPerRow)
    {
        if (FilteredEmojis.Count == 0 || SelectedEmoji == null) return;
        var idx = FilteredEmojis.IndexOf(SelectedEmoji);
        var prev = idx - columnsPerRow;
        if (prev >= 0)
            SelectedEmoji = FilteredEmojis[prev];
    }

    [RelayCommand]
    private void SelectNextRow(int columnsPerRow)
    {
        if (FilteredEmojis.Count == 0 || SelectedEmoji == null) return;
        var idx = FilteredEmojis.IndexOf(SelectedEmoji);
        var next = idx + columnsPerRow;
        if (next < FilteredEmojis.Count)
            SelectedEmoji = FilteredEmojis[next];
    }
}
