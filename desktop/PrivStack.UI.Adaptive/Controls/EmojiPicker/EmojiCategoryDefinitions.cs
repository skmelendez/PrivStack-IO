namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Static category definitions shared by all emoji picker consumers.
/// </summary>
public static class EmojiCategoryDefinitions
{
    public static readonly EmojiCategory[] All =
    [
        new("recent",     "Recent",     "\uD83D\uDD53"),  // 🕓
        new("smileys",    "Smileys",    "\uD83D\uDE00"),  // 😀
        new("people",     "People",     "\uD83D\uDC4B"),  // 👋
        new("animals",    "Animals",    "\uD83D\uDC36"),  // 🐶
        new("food",       "Food",       "\uD83C\uDF54"),  // 🍔
        new("travel",     "Travel",     "\u2708\uFE0F"),  // ✈️
        new("activities", "Activities", "\u26BD"),         // ⚽
        new("objects",    "Objects",    "\uD83D\uDCA1"),  // 💡
        new("symbols",    "Symbols",    "\u2764\uFE0F"),  // ❤️
        new("flags",      "Flags",      "\uD83C\uDFF3\uFE0F"), // 🏳️
    ];
}
