using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Available themes in PrivStack.
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    Sage,
    Lavender,
    Azure,
    Slate,
    Ember,
}

/// <summary>
/// Service for managing application themes at runtime.
/// Handles switching between Dark, Light, and High Contrast themes.
/// Uses direct resource dictionary updates for DynamicResource bindings to work.
/// </summary>
public class ThemeService : IThemeService
{
    private static readonly ILogger _log = Log.ForContext<ThemeService>();

    private AppTheme _currentTheme = AppTheme.Dark;
    private string? _currentCustomThemeId;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IAppSettingsService _appSettings;
    private readonly IFontScaleService _fontScaleService;
    private readonly IResponsiveLayoutService _responsiveLayoutService;

    /// <summary>
    /// Gets or sets the current theme.
    /// </summary>
    public AppTheme CurrentTheme
    {
        get => _currentTheme;
        set
        {
            if (_currentTheme != value || _currentCustomThemeId != null)
            {
                _currentCustomThemeId = null;
                _currentTheme = value;
                ApplyTheme(value);
                SaveThemePreference(value.ToString());
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTheme)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDarkTheme)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLightTheme)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighContrastTheme)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCustomThemeId)));
            }
        }
    }

    /// <inheritdoc/>
    public string? CurrentCustomThemeId => _currentCustomThemeId;

    /// <summary>
    /// Gets whether the current theme is Dark.
    /// </summary>
    public bool IsDarkTheme => _currentTheme == AppTheme.Dark;

    /// <summary>
    /// Gets whether the current theme is Light.
    /// </summary>
    public bool IsLightTheme => _currentTheme == AppTheme.Light;

    /// <summary>
    /// Gets whether the current theme is High Contrast.
    /// </summary>
    public bool IsHighContrastTheme => false;

    public ThemeService(IAppSettingsService appSettings, IFontScaleService fontScaleService,
        IResponsiveLayoutService responsiveLayoutService)
    {
        _appSettings = appSettings;
        _fontScaleService = fontScaleService;
        _responsiveLayoutService = responsiveLayoutService;
        // Load saved theme preference
        LoadThemePreference();
    }

    /// <summary>
    /// Initializes the theme service and applies the saved theme.
    /// Call this after the Application has been initialized.
    /// </summary>
    public void Initialize()
    {
        // If we loaded a custom theme preference, try to apply it
        if (_currentCustomThemeId != null)
        {
            var store = new CustomThemeStore();
            var customTheme = store.Load(_currentCustomThemeId);
            if (customTheme != null)
            {
                ApplyCustomTheme(customTheme);
                _log.Information("Theme service initialized with custom theme {Id}", _currentCustomThemeId);
                return;
            }
            // Custom theme file missing, fall back to Dark
            _log.Warning("Custom theme {Id} not found, falling back to Dark", _currentCustomThemeId);
            _currentCustomThemeId = null;
            _currentTheme = AppTheme.Dark;
            SaveThemePreference("Dark");
        }

        ApplyTheme(_currentTheme);
        _log.Information("Theme service initialized with {Theme} theme", _currentTheme);
    }

    /// <inheritdoc/>
    public Dictionary<string, string> GetBuiltInThemeColors(AppTheme theme)
    {
        var colors = new Dictionary<string, string>();
        var themeUri = GetThemeUri(theme);
        var themeDictionary = LoadResourceDictionary(themeUri);
        if (themeDictionary == null) return colors;

        foreach (var kvp in themeDictionary)
        {
            if (kvp.Key is string key && kvp.Value is Color color)
            {
                colors[key] = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }
        }

        return colors;
    }

    /// <inheritdoc/>
    public void ApplyCustomTheme(CustomThemeDefinition theme)
    {
        var app = Application.Current;
        if (app == null) return;

        try
        {
            // Determine the base theme for layout constants
            var baseTheme = AppTheme.Dark;
            if (!string.IsNullOrEmpty(theme.BasedOn) && Enum.TryParse<AppTheme>(theme.BasedOn, out var parsed))
                baseTheme = parsed;

            // Load the base theme (gets layout constants, font sizes, etc.)
            var themeUri = GetThemeUri(baseTheme);
            var themeDictionary = LoadResourceDictionary(themeUri);
            if (themeDictionary == null) return;

            // Apply all base theme resources
            var appResources = app.Resources;
            foreach (var kvp in themeDictionary)
            {
                if (kvp.Key is string key)
                    appResources[key] = kvp.Value;
            }

            // Override with custom colors
            foreach (var (key, hex) in theme.Colors)
            {
                ApplyColorOverrideInternal(appResources, key, hex);
            }

            // Set the theme variant
            app.RequestedThemeVariant = theme.IsLightVariant ? ThemeVariant.Light : ThemeVariant.Dark;

            _currentTheme = baseTheme;
            _currentCustomThemeId = theme.Id;

            _responsiveLayoutService.ReapplyLayout();

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTheme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCustomThemeId)));

            _log.Information("Applied custom theme {Id} ({Name})", theme.Id, theme.Name);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply custom theme {Id}", theme.Id);
        }
    }

    /// <inheritdoc/>
    public void ApplyColorOverride(string key, string hex)
    {
        var app = Application.Current;
        if (app == null) return;

        ApplyColorOverrideInternal(app.Resources, key, hex);
    }

    /// <inheritdoc/>
    public void SaveThemePreference(string themeString)
    {
        var settings = _appSettings.Settings;
        settings.Theme = themeString;
        _appSettings.Save();
        _log.Debug("Saved theme preference: {Theme}", themeString);
    }

    /// <inheritdoc/>
    public Dictionary<string, object> SnapshotCurrentColors()
    {
        var snapshot = new Dictionary<string, object>();
        var app = Application.Current;
        if (app == null) return snapshot;

        foreach (var key in ThemeColorKeys.AllKeys)
        {
            if (app.Resources.TryGetResource(key, null, out var colorVal) && colorVal != null)
                snapshot[key] = colorVal;

            var brushKey = key + "Brush";
            if (app.Resources.TryGetResource(brushKey, null, out var brushVal) && brushVal != null)
                snapshot[brushKey] = brushVal;
        }

        // Also snapshot aliased brushes
        string[] aliasedBrushKeys =
        [
            "ThemeLinkBrush",
            "ThemeNavBackgroundBrush", "ThemeNavBorderBrush", "ThemeNavBorderSubtleBrush",
            "ThemeNavTextBrush", "ThemeNavTextHoverBrush", "ThemeNavHoverBrush", "ThemeNavSelectedBrush",
            "ThemeTableHeaderBrush", "ThemeTableStripeBrush",
            "ThemeTableBlueHeaderBrush", "ThemeTableBlueStripeBrush",
            "ThemeTableGreenHeaderBrush", "ThemeTableGreenStripeBrush",
            "ThemeTablePurpleHeaderBrush", "ThemeTablePurpleStripeBrush",
            "ThemeTableOrangeHeaderBrush", "ThemeTableOrangeStripeBrush",
            "ThemeCodeBlockBrush", "ThemeCodeBlockGutterBrush"
        ];

        foreach (var key in aliasedBrushKeys)
        {
            if (app.Resources.TryGetResource(key, null, out var val) && val != null)
                snapshot[key] = val;
        }

        return snapshot;
    }

    /// <inheritdoc/>
    public void RestoreSnapshot(Dictionary<string, object> snapshot)
    {
        var app = Application.Current;
        if (app == null) return;

        foreach (var (key, value) in snapshot)
        {
            app.Resources[key] = value;
        }

        _responsiveLayoutService.ReapplyLayout();
    }

    private static void ApplyColorOverrideInternal(IResourceDictionary resources, string key, string hex)
    {
        try
        {
            var color = Color.Parse(hex);
            resources[key] = color;

            // Update the corresponding brush
            var brushKey = key + "Brush";
            resources[brushKey] = new SolidColorBrush(color);

            // Handle aliased brushes that point to the same color
            switch (key)
            {
                case "ThemePrimary":
                    resources["ThemeLinkBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeSurface":
                    resources["ThemeNavBackgroundBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeBorder":
                    resources["ThemeNavBorderBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeBorderSubtle":
                    resources["ThemeNavBorderSubtleBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeTextMuted":
                    resources["ThemeNavTextBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeTextPrimary":
                    resources["ThemeNavTextHoverBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeHover":
                    resources["ThemeNavHoverBrush"] = new SolidColorBrush(color);
                    resources["ThemeTableHeaderBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeSelected":
                    resources["ThemeNavSelectedBrush"] = new SolidColorBrush(color);
                    break;
                case "ThemeBackground":
                    resources["ThemeCodeBlockBrush"] = new SolidColorBrush(color);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning("Failed to parse color {Key}={Hex}: {Error}", key, hex, ex.Message);
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null)
        {
            _log.Warning("Cannot apply theme: Application.Current is null");
            return;
        }

        try
        {
            // Load the theme dictionary
            var themeUri = GetThemeUri(theme);
            var themeDictionary = LoadResourceDictionary(themeUri);

            if (themeDictionary == null)
            {
                _log.Error("Failed to load theme dictionary from {Uri}", themeUri);
                return;
            }

            // Directly update each resource in the app's resource dictionary
            // This triggers DynamicResource bindings to update
            var appResources = app.Resources;
            var updatedCount = 0;

            foreach (var kvp in themeDictionary)
            {
                if (kvp.Key is string key)
                {
                    appResources[key] = kvp.Value;
                    updatedCount++;
                }
            }

            // Update the RequestedThemeVariant for FluentTheme compatibility
            app.RequestedThemeVariant = IsLightThemeVariant(theme) ? ThemeVariant.Light : ThemeVariant.Dark;

            // Reapply responsive layout first (sets mode-adjusted base values),
            // then font scaling applies multiplier on top
            _responsiveLayoutService.ReapplyLayout();

            _log.Information("Applied {Theme} theme successfully ({Count} resources updated)", theme, updatedCount);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply {Theme} theme", theme);
        }
    }

    private static ResourceDictionary? LoadResourceDictionary(Uri uri)
    {
        try
        {
            var loaded = AvaloniaXamlLoader.Load(uri);
            if (loaded is ResourceDictionary rd)
            {
                return rd;
            }
            _log.Warning("Loaded resource from {Uri} is not a ResourceDictionary: {Type}", uri, loaded?.GetType().Name);
            return null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load ResourceDictionary from {Uri}", uri);
            return null;
        }
    }

    private static Uri GetThemeUri(AppTheme theme)
    {
        var fileName = theme switch
        {
            AppTheme.Dark => "DarkTheme",
            AppTheme.Light => "LightTheme",
            AppTheme.Sage => "SageTheme",
            AppTheme.Lavender => "LavenderTheme",
            AppTheme.Azure => "AzureTheme",
            AppTheme.Slate => "SlateTheme",
            AppTheme.Ember => "EmberTheme",
            _ => "DarkTheme"
        };

        return new Uri($"avares://PrivStack.Desktop/Styles/Themes/{fileName}.axaml");
    }

    private static bool IsLightThemeVariant(AppTheme theme)
    {
        return theme is AppTheme.Light
            or AppTheme.Sage
            or AppTheme.Lavender
            or AppTheme.Azure;
    }

    private void LoadThemePreference()
    {
        var settings = _appSettings.Settings;
        var themeStr = settings.Theme ?? "Dark";

        if (themeStr.StartsWith("custom:"))
        {
            _currentCustomThemeId = themeStr[7..];
            _currentTheme = AppTheme.Dark; // Will be overridden during Initialize
            _log.Debug("Loaded custom theme preference: {Id}", _currentCustomThemeId);
        }
        else
        {
            _currentTheme = Enum.TryParse<AppTheme>(themeStr, out var parsed) ? parsed : AppTheme.Dark;
            _log.Debug("Loaded theme preference: {Theme}", _currentTheme);
        }
    }
}
