using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models;

/// <summary>
/// Represents a user-created custom theme stored as JSON.
/// </summary>
public class CustomThemeDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("based_on")]
    public string? BasedOn { get; set; }

    [JsonPropertyName("is_light_variant")]
    public bool IsLightVariant { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modified_at")]
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new();
}

/// <summary>
/// Static helper defining all theme color keys, their display names, and groupings.
/// </summary>
public static class ThemeColorKeys
{
    public record ColorKeyInfo(string Key, string DisplayName);

    public record ColorGroup(string Name, ColorKeyInfo[] Keys);

    public static readonly ColorGroup[] Groups =
    [
        new("Background", [
            new("ThemeBackground", "Background"),
            new("ThemeSurface", "Surface"),
            new("ThemeSurfaceElevated", "Surface Elevated"),
            new("ThemeSurfaceGlass", "Surface Glass"),
        ]),
        new("Border", [
            new("ThemeBorder", "Border"),
            new("ThemeBorderSubtle", "Border Subtle"),
        ]),
        new("Text", [
            new("ThemeTextPrimary", "Primary"),
            new("ThemeTextSecondary", "Secondary"),
            new("ThemeTextMuted", "Muted"),
            new("ThemeTextOnAccent", "On Accent"),
        ]),
        new("Primary Accent", [
            new("ThemePrimary", "Primary"),
            new("ThemePrimaryHover", "Primary Hover"),
            new("ThemePrimaryMuted", "Primary Muted"),
            new("ThemePrimaryGlow", "Primary Glow"),
        ]),
        new("Secondary Accent", [
            new("ThemeSecondary", "Secondary"),
            new("ThemeSecondaryHover", "Secondary Hover"),
            new("ThemeSecondaryMuted", "Secondary Muted"),
        ]),
        new("Semantic", [
            new("ThemeSuccess", "Success"),
            new("ThemeSuccessMuted", "Success Muted"),
            new("ThemeWarning", "Warning"),
            new("ThemeWarningMuted", "Warning Muted"),
            new("ThemeDanger", "Danger"),
            new("ThemeDangerMuted", "Danger Muted"),
        ]),
        new("Interaction", [
            new("ThemeHover", "Hover"),
            new("ThemeSelected", "Selected"),
            new("ThemePressed", "Pressed"),
        ]),
        new("Special", [
            new("ThemeSelection", "Text Selection"),
            new("ThemeScrollTrack", "Scroll Track"),
            new("ThemeScrollThumb", "Scroll Thumb"),
            new("ThemeScrollThumbHover", "Scroll Thumb Hover"),
        ]),
        new("Badges & Overlays", [
            new("ThemeAlphaBadge", "Alpha Badge"),
            new("ThemeBetaBadge", "Beta Badge"),
            new("ThemeModalBackdrop", "Modal Backdrop"),
            new("ThemeModalBackdropHeavy", "Modal Backdrop Heavy"),
        ]),
        new("Brand", [
            new("ThemeGoogleBrand", "Google"),
            new("ThemeICloudBrand", "iCloud"),
        ]),
        new("Highlights", [
            new("ThemeHighlightYellow", "Yellow"),
            new("ThemeHighlightBlue", "Blue"),
            new("ThemeHighlightGreen", "Green"),
            new("ThemeHighlightPink", "Pink"),
            new("ThemeHighlightPurple", "Purple"),
            new("ThemeHighlightOrange", "Orange"),
        ]),
        new("Calendar", [
            new("ThemeCalendarEvent", "Event"),
            new("ThemeCalendarEventMuted", "Event Muted"),
        ]),
        new("System Accent (Fluent Override)", [
            new("SystemAccentColor", "Accent"),
            new("SystemAccentColorDark1", "Accent Dark 1"),
            new("SystemAccentColorDark2", "Accent Dark 2"),
            new("SystemAccentColorDark3", "Accent Dark 3"),
            new("SystemAccentColorLight1", "Accent Light 1"),
            new("SystemAccentColorLight2", "Accent Light 2"),
            new("SystemAccentColorLight3", "Accent Light 3"),
        ]),
    ];

    /// <summary>
    /// All color keys in a flat list.
    /// </summary>
    public static readonly string[] AllKeys = Groups
        .SelectMany(g => g.Keys)
        .Select(k => k.Key)
        .ToArray();
}
