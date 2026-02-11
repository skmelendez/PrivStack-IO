using System.Globalization;
using System.Text;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Fitzpatrick skin tone scale modifiers (Unicode 8.0+).
/// </summary>
public enum SkinTone
{
    None = 0,
    Light = 1,       // U+1F3FB 🏻
    MediumLight = 2, // U+1F3FC 🏼
    Medium = 3,      // U+1F3FD 🏽
    MediumDark = 4,  // U+1F3FE 🏾
    Dark = 5,        // U+1F3FF 🏿
}

/// <summary>
/// Helpers to apply Fitzpatrick skin tone modifiers to emoji strings.
/// </summary>
public static class SkinToneModifier
{
    // U+1F3FB through U+1F3FF
    private const int BaseCodePoint = 0x1F3FB;

    /// <summary>Emoji used to preview each skin tone option.</summary>
    public static readonly string[] PreviewEmojis =
    [
        "\uD83D\uDC4B",               // 👋 default yellow
        "\uD83D\uDC4B\uD83C\uDFFB",   // 👋🏻
        "\uD83D\uDC4B\uD83C\uDFFC",   // 👋🏼
        "\uD83D\uDC4B\uD83C\uDFFD",   // 👋🏽
        "\uD83D\uDC4B\uD83C\uDFFE",   // 👋🏾
        "\uD83D\uDC4B\uD83C\uDFFF",   // 👋🏿
    ];

    /// <summary>
    /// Applies a skin tone modifier to a base emoji.
    /// For simple emojis, appends modifier after the first codepoint.
    /// For ZWJ sequences, appends modifier after each person/hand codepoint.
    /// </summary>
    public static string Apply(string baseEmoji, SkinTone tone)
    {
        if (tone == SkinTone.None || string.IsNullOrEmpty(baseEmoji))
            return baseEmoji;

        var modifier = char.ConvertFromUtf32(BaseCodePoint + (int)tone - 1);
        var enumerator = StringInfo.GetTextElementEnumerator(baseEmoji);
        var sb = new StringBuilder();
        var isFirst = true;

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            sb.Append(element);

            // Insert modifier after the first codepoint that can accept it
            if (isFirst && element.Length <= 2 && !IsModifierOrVariation(element))
            {
                sb.Append(modifier);
                isFirst = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips any existing Fitzpatrick skin tone modifier from an emoji string.
    /// </summary>
    public static string Strip(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return emoji;

        var enumerator = StringInfo.GetTextElementEnumerator(emoji);
        var sb = new StringBuilder();

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var cp = char.ConvertToUtf32(element, 0);
            // Skip Fitzpatrick modifiers U+1F3FB-1F3FF
            if (cp >= 0x1F3FB && cp <= 0x1F3FF)
                continue;
            sb.Append(element);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates all 6 variants (default + 5 tones) for popover display.
    /// Strips any existing skin tone modifier first to prevent double-application.
    /// </summary>
    public static string[] GetVariants(string emoji)
    {
        var baseEmoji = Strip(emoji);
        return
        [
            baseEmoji,
            Apply(baseEmoji, SkinTone.Light),
            Apply(baseEmoji, SkinTone.MediumLight),
            Apply(baseEmoji, SkinTone.Medium),
            Apply(baseEmoji, SkinTone.MediumDark),
            Apply(baseEmoji, SkinTone.Dark),
        ];
    }

    private static bool IsModifierOrVariation(string element)
    {
        if (element.Length == 0) return false;
        var cp = char.ConvertToUtf32(element, 0);
        // Fitzpatrick modifiers U+1F3FB-1F3FF or Variation Selector VS16 U+FE0F
        return (cp >= 0x1F3FB && cp <= 0x1F3FF) || cp == 0xFE0F || cp == 0x200D;
    }
}
