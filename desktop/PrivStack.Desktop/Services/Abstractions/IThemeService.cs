using System.ComponentModel;
using PrivStack.Desktop.Models;

namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Abstraction over theme management.
/// </summary>
public interface IThemeService : INotifyPropertyChanged
{
    AppTheme CurrentTheme { get; set; }
    bool IsDarkTheme { get; }
    bool IsLightTheme { get; }
    bool IsHighContrastTheme { get; }

    /// <summary>
    /// The currently active custom theme ID, or null if using a built-in theme.
    /// </summary>
    string? CurrentCustomThemeId { get; }

    void Initialize();

    /// <summary>
    /// Extracts color resources from a built-in theme AXAML into a dictionary.
    /// </summary>
    Dictionary<string, string> GetBuiltInThemeColors(AppTheme theme);

    /// <summary>
    /// Applies a custom theme: loads the base theme AXAML for layout constants,
    /// then overrides all colors from the definition.
    /// </summary>
    void ApplyCustomTheme(CustomThemeDefinition theme);

    /// <summary>
    /// Updates a single color key and its corresponding brush for live preview.
    /// </summary>
    void ApplyColorOverride(string key, string hex);

    /// <summary>
    /// Saves the theme preference (built-in name or "custom:{id}").
    /// </summary>
    void SaveThemePreference(string themeString);

    /// <summary>
    /// Snapshots all current color resources for later restoration.
    /// </summary>
    Dictionary<string, object> SnapshotCurrentColors();

    /// <summary>
    /// Restores color resources from a previously taken snapshot.
    /// </summary>
    void RestoreSnapshot(Dictionary<string, object> snapshot);
}
