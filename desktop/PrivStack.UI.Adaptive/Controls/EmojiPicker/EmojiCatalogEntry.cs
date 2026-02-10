using System.Text.Json.Serialization;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Raw catalog entry matching the compact JSON shape in EmojiCatalog.json.
/// </summary>
public sealed record EmojiCatalogEntry(
    [property: JsonPropertyName("e")] string Emoji,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("k")] string Keywords,
    [property: JsonPropertyName("c")] string CategoryId,
    [property: JsonPropertyName("v")] string Version,
    [property: JsonPropertyName("st")] bool SupportsSkinTone = false);
