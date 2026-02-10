using System.Text.Json;
using PrivStack.Desktop.Models;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Persists custom theme definitions as JSON files in {DataDir}/themes/.
/// </summary>
public class CustomThemeStore
{
    private static readonly ILogger _log = Log.ForContext<CustomThemeStore>();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private string ThemesDirectory => Path.Combine(DataPaths.BaseDir, "themes");

    /// <summary>
    /// Loads all custom themes from disk.
    /// </summary>
    public List<CustomThemeDefinition> LoadAll()
    {
        var dir = ThemesDirectory;
        if (!Directory.Exists(dir))
            return [];

        var themes = new List<CustomThemeDefinition>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var theme = JsonSerializer.Deserialize<CustomThemeDefinition>(json);
                if (theme != null && !string.IsNullOrEmpty(theme.Id))
                    themes.Add(theme);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to load custom theme from {File}", file);
            }
        }

        return themes;
    }

    /// <summary>
    /// Loads a specific custom theme by ID.
    /// </summary>
    public CustomThemeDefinition? Load(string id)
    {
        var path = GetThemePath(id);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CustomThemeDefinition>(json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load custom theme {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Saves a custom theme to disk.
    /// </summary>
    public void Save(CustomThemeDefinition theme)
    {
        var dir = ThemesDirectory;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        theme.ModifiedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(theme, _jsonOptions);
        File.WriteAllText(GetThemePath(theme.Id), json);
        _log.Information("Saved custom theme {Id} ({Name})", theme.Id, theme.Name);
    }

    /// <summary>
    /// Deletes a custom theme from disk.
    /// </summary>
    public bool Delete(string id)
    {
        var path = GetThemePath(id);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        _log.Information("Deleted custom theme {Id}", id);
        return true;
    }

    private string GetThemePath(string id)
    {
        // Sanitize ID for filesystem
        var safeId = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(ThemesDirectory, $"{safeId}.json");
    }
}
