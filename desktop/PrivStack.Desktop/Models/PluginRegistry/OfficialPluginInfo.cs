using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models.PluginRegistry;

/// <summary>
/// DTO representing an official plugin from the server registry.
/// Matches the JSON shape returned by GET /api/official-plugins.
/// </summary>
public sealed record OfficialPluginInfo
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("tagline")]
    public string? Tagline { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = "productivity";

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("min_app_version")]
    public string? MinAppVersion { get; init; }

    [JsonPropertyName("package_url")]
    public string PackageUrl { get; init; } = string.Empty;

    [JsonPropertyName("package_size_bytes")]
    public long? PackageSizeBytes { get; init; }

    [JsonPropertyName("checksum_sha256")]
    public string ChecksumSha256 { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = "PrivStack";

    [JsonPropertyName("navigation_order")]
    public int NavigationOrder { get; init; } = 1000;

    [JsonPropertyName("release_stage")]
    public string? ReleaseStage { get; init; }
}
