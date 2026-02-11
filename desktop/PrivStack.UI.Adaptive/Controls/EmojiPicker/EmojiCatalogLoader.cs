using System.Reflection;
using System.Text.Json;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Loads and caches the embedded EmojiCatalog.json resource.
/// Thread-safe, loaded once per app lifetime.
/// </summary>
public static class EmojiCatalogLoader
{
    private static readonly object Lock = new();
    private static List<EmojiCatalogEntry>? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Returns the full emoji catalog from the embedded resource.
    /// Result is cached after the first call.
    /// </summary>
    public static IReadOnlyList<EmojiCatalogEntry> Load()
    {
        if (_cached is not null)
            return _cached;

        lock (Lock)
        {
            if (_cached is not null)
                return _cached;

            _cached = LoadFromResource();
            return _cached;
        }
    }

    /// <summary>
    /// Clears the cached catalog (mainly for testing).
    /// </summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _cached = null;
        }
    }

    private static List<EmojiCatalogEntry> LoadFromResource()
    {
        var assembly = typeof(EmojiCatalogLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("EmojiCatalog.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new InvalidOperationException(
                "EmojiCatalog.json embedded resource not found. " +
                "Ensure the file is marked as an EmbeddedResource in the .csproj.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var entries = JsonSerializer.Deserialize<List<EmojiCatalogEntry>>(stream, JsonOptions);
        return entries ?? [];
    }
}
