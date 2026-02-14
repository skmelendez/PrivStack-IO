using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivStack.Sdk;

/// <summary>
/// Property value types for custom entity properties.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SdkPropertyType>))]
public enum SdkPropertyType
{
    Text, Number, Date, Checkbox, Select, MultiSelect, Url
}

/// <summary>
/// Lightweight property definition exposed to plugins via IPropertyService.
/// </summary>
public sealed record SdkPropertyDefinition(
    string Id,
    string Name,
    SdkPropertyType Type,
    string? Description,
    List<string>? Options,
    string? DefaultValue,
    string? Icon,
    int SortOrder,
    string? GroupId);

/// <summary>
/// Entity metadata (tags + property values) returned to plugins.
/// </summary>
public sealed record SdkEntityMetadata(
    string EntityId,
    string LinkType,
    List<string> Tags,
    Dictionary<string, JsonElement> Properties);

/// <summary>
/// Service for reading and writing entity metadata (tags, custom properties).
/// Plugins use this to display and edit properties inline without
/// depending on PrivStack.Desktop types.
/// </summary>
public interface IPropertyService
{
    /// <summary>
    /// Loads tags and property values for an entity.
    /// </summary>
    Task<SdkEntityMetadata> GetMetadataAsync(string linkType, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Returns all user-defined property definitions.
    /// </summary>
    Task<IReadOnlyList<SdkPropertyDefinition>> GetPropertyDefinitionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the tag list for an entity.
    /// </summary>
    Task UpdateTagsAsync(string linkType, string entityId, List<string> tags, CancellationToken ct = default);

    /// <summary>
    /// Sets a single property value on an entity.
    /// </summary>
    Task UpdatePropertyAsync(string linkType, string entityId, string propertyDefId, JsonElement value, CancellationToken ct = default);

    /// <summary>
    /// Returns all known tags across all entities (for autocomplete).
    /// </summary>
    Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default);
}
