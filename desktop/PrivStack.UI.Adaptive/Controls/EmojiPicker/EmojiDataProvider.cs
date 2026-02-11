namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Central access point for emoji data. Loads the catalog, applies platform filtering
/// and skin tone preferences, and exposes data in formats needed by all consumers.
/// </summary>
public static class EmojiDataProvider
{
    private static readonly object Lock = new();
    private static IReadOnlyList<EmojiItem>? _filteredEmojis;
    private static IReadOnlyList<EmojiItem>? _skinToneApplied;
    private static IReadOnlyDictionary<string, string>? _keywordLookup;
    private static (string Icon, string Name, string[] Emojis)[]? _categoryGroups;

    // Preserve raw filtered entries for skin tone reapplication
    private static IReadOnlyList<EmojiCatalogEntry>? _rawFiltered;

    /// <summary>
    /// Returns all emojis filtered for the current platform, with preferred skin tone applied.
    /// </summary>
    public static IReadOnlyList<EmojiItem> GetEmojis()
    {
        EnsureLoaded();
        return _skinToneApplied!;
    }

    /// <summary>
    /// Returns keyword lookup: emoji string → space-separated keywords.
    /// Used by AdaptiveViewRenderer for search compatibility.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetKeywordLookup()
    {
        EnsureLoaded();
        return _keywordLookup!;
    }

    /// <summary>
    /// Returns category groups with icon, name, and emoji arrays.
    /// Used by AdaptiveViewRenderer for its emoji picker overlay.
    /// </summary>
    public static (string Icon, string Name, string[] Emojis)[] GetCategoryGroups()
    {
        EnsureLoaded();
        return _categoryGroups!;
    }

    /// <summary>
    /// Invalidates the skin-tone-applied cache and regenerates.
    /// Call after changing the preferred skin tone.
    /// </summary>
    public static void RefreshSkinTone()
    {
        lock (Lock)
        {
            if (_rawFiltered is null) return;
            _skinToneApplied = BuildEmojiItems(_rawFiltered);
            _categoryGroups = BuildCategoryGroups(_skinToneApplied);
        }
    }

    /// <summary>
    /// Forces a full reload (mainly for testing).
    /// </summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _filteredEmojis = null;
            _skinToneApplied = null;
            _keywordLookup = null;
            _categoryGroups = null;
            _rawFiltered = null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_skinToneApplied is not null)
            return;

        lock (Lock)
        {
            if (_skinToneApplied is not null)
                return;

            var catalog = EmojiCatalogLoader.Load();
            _rawFiltered = EmojiPlatformFilter.Filter(catalog);
            _filteredEmojis = BuildEmojiItems(_rawFiltered);
            _skinToneApplied = _filteredEmojis;
            _keywordLookup = BuildKeywordLookup(_rawFiltered);
            _categoryGroups = BuildCategoryGroups(_skinToneApplied);

            // Apply skin tone if a preference exists
            if (SkinToneService.PreferredTone != SkinTone.None)
            {
                _skinToneApplied = BuildEmojiItems(_rawFiltered);
                _categoryGroups = BuildCategoryGroups(_skinToneApplied);
            }
        }
    }

    private static IReadOnlyList<EmojiItem> BuildEmojiItems(IReadOnlyList<EmojiCatalogEntry> entries)
    {
        var items = new List<EmojiItem>(entries.Count);
        foreach (var entry in entries)
        {
            var emoji = SkinToneService.ApplyPreferred(entry.Emoji, entry.SupportsSkinTone);
            items.Add(new EmojiItem(emoji, entry.Name, entry.Keywords, entry.CategoryId,
                entry.SupportsSkinTone));
        }
        return items;
    }

    private static IReadOnlyDictionary<string, string> BuildKeywordLookup(
        IReadOnlyList<EmojiCatalogEntry> entries)
    {
        var dict = new Dictionary<string, string>(entries.Count);
        foreach (var entry in entries)
        {
            // Use the potentially skin-tone-modified emoji as key
            var emoji = SkinToneService.ApplyPreferred(entry.Emoji, entry.SupportsSkinTone);
            dict.TryAdd(emoji, entry.Keywords);
        }
        return dict;
    }

    private static (string Icon, string Name, string[] Emojis)[] BuildCategoryGroups(
        IReadOnlyList<EmojiItem> items)
    {
        var categories = EmojiCategoryDefinitions.All;
        var groups = new List<(string Icon, string Name, string[] Emojis)>();

        foreach (var cat in categories)
        {
            if (cat.Id == "recent") continue; // Recent is dynamic, not from catalog

            var emojis = items
                .Where(e => e.CategoryId == cat.Id)
                .Select(e => e.Emoji)
                .ToArray();

            if (emojis.Length > 0)
                groups.Add((cat.Icon, cat.Name, emojis));
        }

        return groups.ToArray();
    }
}
