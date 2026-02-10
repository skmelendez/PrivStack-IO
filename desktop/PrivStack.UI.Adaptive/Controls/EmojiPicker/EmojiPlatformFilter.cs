using System.Runtime.InteropServices;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Filters emojis based on the current OS and its supported Unicode emoji version.
/// Prevents monochrome/blank emoji rendering on older platforms.
/// </summary>
public static class EmojiPlatformFilter
{
    private static double? _cachedMaxVersion;

    /// <summary>
    /// Emojis that technically exist on Windows but render monochrome or poorly.
    /// </summary>
    private static readonly HashSet<string> WindowsExclusionList =
    [
        "\uD83D\uDDBC\uFE0F", // 🖼️ Framed Picture
        "\u2702\uFE0F",       // ✂️ Scissors
        "\u2611\uFE0F",       // ☑️ Ballot Box with Check
        "\u2618\uFE0F",       // ☘️ Shamrock
        "\u2697\uFE0F",       // ⚗️ Alembic
        "\u2696\uFE0F",       // ⚖️ Balance Scale
        "\u26B0\uFE0F",       // ⚰️ Coffin
        "\u26B1\uFE0F",       // ⚱️ Funeral Urn
        "\u2694\uFE0F",       // ⚔️ Crossed Swords
        "\u2692\uFE0F",       // ⚒️ Hammer and Pick
        "\u2699\uFE0F",       // ⚙️ Gear (sometimes mono on older Win10)
        "\u26D3\uFE0F",       // ⛓️ Chains
        "\u2709\uFE0F",       // ✉️ Envelope
        "\u270F\uFE0F",       // ✏️ Pencil
        "\u2712\uFE0F",       // ✒️ Black Nib
        "\u2708\uFE0F",       // ✈️ Airplane (sometimes mono)
    ];

    /// <summary>
    /// Returns the maximum Unicode emoji version supported by the current platform.
    /// </summary>
    public static double GetMaxSupportedVersion()
    {
        if (_cachedMaxVersion.HasValue)
            return _cachedMaxVersion.Value;

        _cachedMaxVersion = DetectMaxVersion();
        return _cachedMaxVersion.Value;
    }

    /// <summary>
    /// Filters a catalog to only include emojis renderable on the current platform.
    /// </summary>
    public static IReadOnlyList<EmojiCatalogEntry> Filter(IReadOnlyList<EmojiCatalogEntry> catalog)
    {
        var maxVersion = GetMaxSupportedVersion();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var result = new List<EmojiCatalogEntry>(catalog.Count);
        foreach (var entry in catalog)
        {
            if (!double.TryParse(entry.Version, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var emojiVersion))
                continue;

            if (emojiVersion > maxVersion)
                continue;

            if (isWindows && WindowsExclusionList.Contains(entry.Emoji))
                continue;

            result.Add(entry);
        }

        return result;
    }

    private static double DetectMaxVersion()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DetectWindowsVersion();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return DetectMacOSVersion();

        // Linux — assume Noto Color Emoji default coverage
        return 15.0;
    }

    private static double DetectWindowsVersion()
    {
        var osVersion = Environment.OSVersion.Version;
        var build = osVersion.Build;

        return build switch
        {
            >= 22621 => 15.0,  // Windows 11 22H2+
            >= 22000 => 13.1,  // Windows 11 21H2
            >= 19041 => 13.0,  // Windows 10 2004+
            >= 18362 => 12.0,  // Windows 10 1903+
            _ => 11.0,         // Older Windows 10
        };
    }

    private static double DetectMacOSVersion()
    {
        var osVersion = Environment.OSVersion.Version;
        var major = osVersion.Major;

        return major switch
        {
            >= 15 => 16.0,   // macOS 15 Sequoia
            >= 14 => 15.1,   // macOS 14 Sonoma
            >= 13 => 15.0,   // macOS 13 Ventura
            _ => 14.0,       // Older macOS
        };
    }
}
