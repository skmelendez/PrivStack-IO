using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Represents a single editable color in the theme editor.
/// </summary>
public partial class ThemeColorItem : ObservableObject
{
    private readonly ThemeEditorViewModel _parent;

    public string Key { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private Color _color;

    [ObservableProperty]
    private string _hexText = string.Empty;

    /// <summary>
    /// The default color from the base theme (for per-color reset).
    /// </summary>
    public string DefaultHex { get; set; } = string.Empty;

    public ThemeColorItem(string key, string displayName, ThemeEditorViewModel parent)
    {
        Key = key;
        DisplayName = displayName;
        _parent = parent;
    }

    private bool _suppressColorSync;

    partial void OnColorChanged(Color value)
    {
        if (_suppressColorSync) return;

        var hex = $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}";
        if (hex != HexText)
        {
            _suppressColorSync = true;
            HexText = hex;
            _suppressColorSync = false;
        }
        _parent.OnColorItemChanged(this);
    }

    partial void OnHexTextChanged(string value)
    {
        if (_suppressColorSync) return;
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var parsed = Color.Parse(value);
            if (parsed != Color)
            {
                _suppressColorSync = true;
                Color = parsed;
                _suppressColorSync = false;
                _parent.OnColorItemChanged(this);
            }
        }
        catch
        {
            // Invalid hex, ignore
        }
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        if (!string.IsNullOrEmpty(DefaultHex))
        {
            HexText = DefaultHex;
        }
    }
}

/// <summary>
/// A group of color items for display in the editor.
/// </summary>
public class ThemeColorGroup
{
    public string Name { get; }
    public ObservableCollection<ThemeColorItem> Items { get; } = [];

    public ThemeColorGroup(string name) => Name = name;
}

/// <summary>
/// ViewModel for the theme editor dialog.
/// </summary>
public partial class ThemeEditorViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;
    private readonly CustomThemeStore _themeStore;
    private readonly IAppSettingsService _appSettings;

    private Dictionary<string, object> _snapshot = new();
    private CustomThemeDefinition? _editingTheme;

    /// <summary>
    /// Fired when the editor is closed (save or cancel).
    /// The bool argument is true if a theme was saved.
    /// </summary>
    public event Action<bool>? EditorClosed;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _themeName = string.Empty;

    [ObservableProperty]
    private bool _isLightVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isExistingTheme;

    public bool CanDelete => IsExistingTheme;

    public ObservableCollection<ThemeColorGroup> ColorGroups { get; } = [];

    public ThemeEditorViewModel(
        IThemeService themeService,
        CustomThemeStore themeStore,
        IAppSettingsService appSettings)
    {
        _themeService = themeService;
        _themeStore = themeStore;
        _appSettings = appSettings;
    }

    /// <summary>
    /// Opens the editor to customize the current theme.
    /// </summary>
    public void OpenForCurrentTheme()
    {
        // Snapshot current state for cancel
        _snapshot = _themeService.SnapshotCurrentColors();

        // Determine base theme and colors
        var currentCustomId = _themeService.CurrentCustomThemeId;
        Dictionary<string, string> currentColors;
        string basedOn;
        bool isLight;

        if (currentCustomId != null)
        {
            // Editing an existing custom theme
            _editingTheme = _themeStore.Load(currentCustomId);
            if (_editingTheme != null)
            {
                currentColors = new Dictionary<string, string>(_editingTheme.Colors);
                basedOn = _editingTheme.BasedOn ?? _themeService.CurrentTheme.ToString();
                isLight = _editingTheme.IsLightVariant;
                ThemeName = _editingTheme.Name;
                IsExistingTheme = true;
            }
            else
            {
                // Fallback: treat as customizing the current built-in theme
                currentColors = _themeService.GetBuiltInThemeColors(_themeService.CurrentTheme);
                basedOn = _themeService.CurrentTheme.ToString();
                isLight = IsLightThemeVariant(_themeService.CurrentTheme);
                ThemeName = $"Custom {basedOn}";
                _editingTheme = null;
                IsExistingTheme = false;
            }
        }
        else
        {
            // Customizing a built-in theme
            currentColors = _themeService.GetBuiltInThemeColors(_themeService.CurrentTheme);
            basedOn = _themeService.CurrentTheme.ToString();
            isLight = IsLightThemeVariant(_themeService.CurrentTheme);
            ThemeName = $"Custom {basedOn}";
            _editingTheme = null;
            IsExistingTheme = false;
        }

        IsLightVariant = isLight;

        // Get default (base theme) colors for per-color reset
        var builtInTheme = Enum.TryParse<AppTheme>(basedOn, out var parsed) ? parsed : AppTheme.Dark;
        var defaultColors = _themeService.GetBuiltInThemeColors(builtInTheme);

        // Build color groups
        ColorGroups.Clear();
        foreach (var group in ThemeColorKeys.Groups)
        {
            var grp = new ThemeColorGroup(group.Name);
            foreach (var keyInfo in group.Keys)
            {
                var item = new ThemeColorItem(keyInfo.Key, keyInfo.DisplayName, this);
                var hex = currentColors.GetValueOrDefault(keyInfo.Key)
                       ?? defaultColors.GetValueOrDefault(keyInfo.Key)
                       ?? "#FF000000";
                item.DefaultHex = defaultColors.GetValueOrDefault(keyInfo.Key) ?? hex;

                try
                {
                    item.Color = Color.Parse(hex);
                }
                catch
                {
                    item.Color = Colors.Black;
                }

                grp.Items.Add(item);
            }
            ColorGroups.Add(grp);
        }

        IsOpen = true;
    }

    /// <summary>
    /// Called by ThemeColorItem when a color changes for live preview.
    /// </summary>
    internal void OnColorItemChanged(ThemeColorItem item)
    {
        _themeService.ApplyColorOverride(item.Key, item.HexText);
    }

    [RelayCommand]
    private void Save()
    {
        var theme = _editingTheme ?? new CustomThemeDefinition();

        if (!IsExistingTheme || string.IsNullOrEmpty(theme.Id))
        {
            // Generate an ID from the name
            theme.Id = GenerateThemeId(ThemeName);
            theme.CreatedAt = DateTime.UtcNow;
        }

        theme.Name = ThemeName;
        theme.IsLightVariant = IsLightVariant;
        theme.ModifiedAt = DateTime.UtcNow;

        if (string.IsNullOrEmpty(theme.BasedOn))
            theme.BasedOn = _themeService.CurrentTheme.ToString();

        // Collect all colors
        theme.Colors.Clear();
        foreach (var group in ColorGroups)
        {
            foreach (var item in group.Items)
            {
                theme.Colors[item.Key] = item.HexText;
            }
        }

        _themeStore.Save(theme);
        _themeService.SaveThemePreference($"custom:{theme.Id}");

        // Apply the saved theme formally
        _themeService.ApplyCustomTheme(theme);

        IsOpen = false;
        EditorClosed?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        // Restore the snapshot
        _themeService.RestoreSnapshot(_snapshot);
        IsOpen = false;
        EditorClosed?.Invoke(false);
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        // Reset all colors to the base theme defaults
        foreach (var group in ColorGroups)
        {
            foreach (var item in group.Items)
            {
                if (!string.IsNullOrEmpty(item.DefaultHex))
                {
                    item.HexText = item.DefaultHex;
                }
            }
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (_editingTheme == null || string.IsNullOrEmpty(_editingTheme.Id)) return;

        _themeStore.Delete(_editingTheme.Id);

        // Restore to the built-in base theme
        _themeService.RestoreSnapshot(_snapshot);

        // Set preference back to built-in
        var basedOn = Enum.TryParse<AppTheme>(_editingTheme.BasedOn, out var parsed)
            ? parsed : AppTheme.Dark;
        _themeService.CurrentTheme = basedOn;

        IsOpen = false;
        EditorClosed?.Invoke(true);
    }

    private static string GenerateThemeId(string name)
    {
        var id = name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        // Remove invalid chars
        id = new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

        if (string.IsNullOrEmpty(id))
            id = "custom-theme";

        // Add timestamp suffix for uniqueness
        id += $"-{DateTime.UtcNow:yyyyMMddHHmmss}";
        return id;
    }

    private static bool IsLightThemeVariant(AppTheme theme)
    {
        return theme is AppTheme.Light or AppTheme.Sage or AppTheme.Lavender or AppTheme.Azure;
    }
}
