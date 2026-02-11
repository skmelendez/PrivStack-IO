using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Service for managing font and layout scaling at runtime for accessibility.
/// Updates all ThemeFontSize* and ThemeSpacing*/layout resources when scale changes.
/// </summary>
public class FontScaleService : IFontScaleService
{
    private static readonly ILogger _log = Log.ForContext<FontScaleService>();

    private double _scaleMultiplier = 1.0;
    private string _currentFontFamily = "system";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Maps font family setting keys to FontFamily values.
    /// "system" uses the default theme font; bundled fonts use avares:// URIs.
    /// </summary>
    /// <summary>
    /// Fallback fonts appended to every bundled font so that Unicode symbols
    /// (e.g. ★ ☆) still render when the primary face lacks the glyph.
    /// Mirrors the chain defined in each theme's ThemeFontSans resource.
    /// </summary>
    private const string FontFallbacks =
        ", Segoe UI, -apple-system, Noto Sans, sans-serif, Segoe UI Emoji, Noto Color Emoji";

    private static readonly Dictionary<string, string> FontFamilyMap = new()
    {
        ["system"] = "", // empty = use theme default (Inter + fallbacks)
        ["ibm-plex-sans"] = "avares://PrivStack.Desktop/Assets/Fonts#IBM Plex Sans" + FontFallbacks,
        ["lexend"] = "avares://PrivStack.Desktop/Assets/Fonts#Lexend" + FontFallbacks,
        ["nunito"] = "avares://PrivStack.Desktop/Assets/Fonts#Nunito" + FontFallbacks,
        ["atkinson"] = "avares://PrivStack.Desktop/Assets/Fonts#Atkinson Hyperlegible" + FontFallbacks,
        ["opendyslexic"] = "avares://PrivStack.Desktop/Assets/Fonts#OpenDyslexic" + FontFallbacks,
    };

    /// <summary>
    /// Base font sizes before scaling is applied.
    /// </summary>
    private static readonly Dictionary<string, double> BaseFontSizes = new()
    {
        ["ThemeFontSize2Xs"] = 10,
        ["ThemeFontSizeXs"] = 11,
        ["ThemeFontSizeXsSm"] = 12,
        ["ThemeFontSizeSm"] = 13,
        ["ThemeFontSizeSmMd"] = 14,
        ["ThemeFontSizeMd"] = 15,
        ["ThemeFontSizeLg"] = 17,
        ["ThemeFontSizeLgXl"] = 20,
        ["ThemeFontSizeXl"] = 22,
        ["ThemeFontSize2Xl"] = 26,
        ["ThemeFontSize3Xl"] = 34,
        ["ThemeFontSize4Xl"] = 48,
        // Line heights — scale proportionally with font sizes
        ["ThemeLineHeightXs"] = 14,
        ["ThemeLineHeightSm"] = 22,
        ["ThemeLineHeightMd"] = 24,
        ["ThemeLineHeightBase"] = 26,
        ["ThemeLineHeightLg"] = 30,
        ["ThemeLineHeightXl"] = 44,
    };

    /// <summary>
    /// Base spacing dimensions before scaling. These are fixed constants.
    /// </summary>
    private static readonly Dictionary<string, double> BaseSpacingSizes = new()
    {
        ["ThemeSpacingXs"] = 4,
        ["ThemeSpacingSm"] = 8,
        ["ThemeSpacingMd"] = 12,
        ["ThemeSpacingLg"] = 16,
        ["ThemeSpacingXl"] = 24,
        ["ThemeSpacing2Xl"] = 32,
        ["ThemePageMaxWidth"] = 1000,
        ["ThemeContentMaxWidth"] = 800,
        ["ThemeContentWideMinWidth"] = 600,
        ["ThemeContentWideMaxWidth"] = 960,
    };

    /// <summary>
    /// Base layout dimensions (widths) before scaling.
    /// Mutable — <see cref="ResponsiveLayoutService"/> feeds mode-adjusted values
    /// before scaling is applied.
    /// </summary>
    private readonly Dictionary<string, double> _baseLayoutSizes = new()
    {
        ["ThemeSidebarWidth"] = 260,
        ["ThemeDetailPanelWidth"] = 320,
        ["ThemeSidebarNarrowWidth"] = 200,
        ["ThemeInfoPanelWidth"] = 280,
    };

    private readonly IAppSettingsService _appSettings;

    /// <summary>
    /// Singleton accessor for code-behind usage in plugins.
    /// Set once during DI construction (registered as singleton).
    /// </summary>
    public static FontScaleService Instance { get; private set; } = null!;

    /// <summary>
    /// Gets or sets the font scale multiplier (0.8 to 1.5).
    /// </summary>
    public double ScaleMultiplier
    {
        get => _scaleMultiplier;
        set
        {
            var clamped = Math.Clamp(value, 0.8, 1.5);
            if (Math.Abs(_scaleMultiplier - clamped) > 0.001)
            {
                _scaleMultiplier = clamped;
                ApplyScale();
                SaveScalePreference();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScaleMultiplier)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScaleDisplayText)));
            }
        }
    }

    /// <summary>
    /// Gets a display-friendly text for the current scale.
    /// </summary>
    public string ScaleDisplayText => _scaleMultiplier switch
    {
        <= 0.85 => "Small",
        <= 0.95 => "Medium",
        <= 1.05 => "Default",
        <= 1.15 => "Large",
        <= 1.30 => "Extra Large",
        _ => "Maximum"
    };

    /// <summary>
    /// Gets or sets the current font family key (e.g. "system", "atkinson", "opendyslexic").
    /// </summary>
    public string CurrentFontFamily
    {
        get => _currentFontFamily;
        set
        {
            if (_currentFontFamily != value && FontFamilyMap.ContainsKey(value))
            {
                _currentFontFamily = value;
                ApplyFontFamily();
                SaveFontFamilyPreference();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentFontFamily)));
            }
        }
    }

    public FontScaleService(IAppSettingsService appSettings)
    {
        _appSettings = appSettings;
        Instance = this;
        HostServices.FontScale = this;
        LoadScalePreference();
        LoadFontFamilyPreference();
    }

    /// <summary>
    /// Initializes the font scale service and applies the saved scale and font family.
    /// Call this after ThemeService has been initialized.
    /// </summary>
    public void Initialize()
    {
        ApplyScale();
        ApplyFontFamily();
        _log.Information("Font scale service initialized with {Scale}x multiplier ({DisplayText}), font family: {FontFamily}",
            _scaleMultiplier, ScaleDisplayText, _currentFontFamily);
    }

    /// <summary>
    /// Reapplies the current font scale and font family. Call this after theme changes
    /// since theme loading resets the font size and font family resources.
    /// </summary>
    public void ReapplyScale()
    {
        ApplyScale();
        ApplyFontFamily();
    }

    /// <summary>
    /// Gets a scaled font size for use in C# code-behind.
    /// </summary>
    /// <param name="baseSize">The base font size before scaling.</param>
    /// <returns>The scaled font size.</returns>
    public double GetScaledSize(double baseSize)
    {
        return Math.Round(baseSize * _scaleMultiplier);
    }

    private void ApplyScale()
    {
        var app = Application.Current;
        if (app == null)
        {
            _log.Warning("Cannot apply font scale: Application.Current is null");
            return;
        }

        try
        {
            var appResources = app.Resources;
            var updatedCount = 0;

            foreach (var (key, baseSize) in BaseFontSizes)
            {
                var scaledSize = Math.Round(baseSize * _scaleMultiplier);
                appResources[key] = scaledSize;
                updatedCount++;
            }

            foreach (var (key, baseSize) in BaseSpacingSizes)
            {
                var scaledSize = Math.Round(baseSize * _scaleMultiplier);
                appResources[key] = scaledSize;
                updatedCount++;
            }

            foreach (var (key, baseSize) in _baseLayoutSizes)
            {
                var scaledSize = Math.Round(baseSize * _scaleMultiplier);
                appResources[key] = scaledSize;
                updatedCount++;
            }

            _log.Debug("Applied font scale {Scale}x ({Count} resources updated)",
                _scaleMultiplier, updatedCount);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply font scale {Scale}x", _scaleMultiplier);
        }
    }

    /// <summary>
    /// Replaces the base layout dimensions with mode-adjusted values from
    /// <see cref="ResponsiveLayoutService"/>. Call this before <see cref="ReapplyScale"/>.
    /// </summary>
    public void SetBaseLayoutSizes(Dictionary<string, double> modeSizes)
    {
        foreach (var (key, value) in modeSizes)
        {
            _baseLayoutSizes[key] = value;
        }
    }

    private void LoadScalePreference()
    {
        var settings = _appSettings.Settings;
        _scaleMultiplier = Math.Clamp(settings.FontScaleMultiplier, 0.8, 1.5);
        _log.Debug("Loaded font scale preference: {Scale}x", _scaleMultiplier);
    }

    private void SaveScalePreference()
    {
        var settings = _appSettings.Settings;
        settings.FontScaleMultiplier = _scaleMultiplier;
        _appSettings.SaveDebounced();
        _log.Debug("Saved font scale preference: {Scale}x", _scaleMultiplier);
    }

    /// <summary>
    /// Applies the current font family to the application's ThemeFontSans resource.
    /// When "system" is selected, restores the theme's default font.
    /// </summary>
    private void ApplyFontFamily()
    {
        var app = Application.Current;
        if (app == null) return;

        if (!FontFamilyMap.TryGetValue(_currentFontFamily, out var fontSpec))
            return;

        try
        {
            if (string.IsNullOrEmpty(fontSpec))
            {
                // Restore theme default by reading from merged dictionaries
                foreach (var dict in app.Resources.MergedDictionaries)
                {
                    if (dict is Avalonia.Controls.ResourceDictionary rd &&
                        rd.TryGetValue("ThemeFontSans", out var themeFont) &&
                        themeFont is FontFamily defaultFont)
                    {
                        app.Resources["ThemeFontSans"] = defaultFont;
                        _log.Debug("Font family restored to theme default: {Font}", defaultFont);
                        return;
                    }
                }
                _log.Debug("Font family set to system default (no theme font found to restore)");
                return;
            }

            var fontFamily = new FontFamily(fontSpec);
            app.Resources["ThemeFontSans"] = fontFamily;
            _log.Debug("Applied font family: {Key} -> {Font}", _currentFontFamily, fontSpec);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply font family {Key}", _currentFontFamily);
        }
    }

    private void LoadFontFamilyPreference()
    {
        var key = _appSettings.Settings.FontFamily;
        if (FontFamilyMap.ContainsKey(key))
            _currentFontFamily = key;
        else
            _currentFontFamily = "system";
        _log.Debug("Loaded font family preference: {Key}", _currentFontFamily);
    }

    private void SaveFontFamilyPreference()
    {
        _appSettings.Settings.FontFamily = _currentFontFamily;
        _appSettings.SaveDebounced();
        _log.Debug("Saved font family preference: {Key}", _currentFontFamily);
    }
}
