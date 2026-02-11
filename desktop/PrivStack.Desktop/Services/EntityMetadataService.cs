using System.Text.Json;
using PrivStack.Desktop.Models;
using PrivStack.Sdk;
using PrivStack.Sdk.Json;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Reads/writes universal metadata (tags, properties) stored in separate entity_metadata
/// entities so plugin models don't need to know about these fields.
/// Also manages property definitions and property groups.
/// </summary>
public sealed class EntityMetadataService
{
    private static readonly ILogger _log = Log.ForContext<EntityMetadataService>();
    private static readonly JsonSerializerOptions JsonOpts = SdkJsonOptions.Default;
    private const string MetaEntityType = "entity_metadata";
    private const string PropDefEntityType = "property_definition";
    private const string PropGroupEntityType = "property_group";
    private const string PropTemplateEntityType = "property_template";
    private const string PluginId = "privstack.system";

    private readonly IPrivStackSdk _sdk;
    private readonly InfoPanelService _infoPanelService;

    // Lazy-cached set of all known tags for autocomplete
    private List<string>? _allTagsCache;
    private readonly SemaphoreSlim _tagCacheLock = new(1, 1);

    // Lazy-cached property definitions and groups
    private List<PropertyDefinition>? _propDefCache;
    private List<PropertyGroup>? _propGroupCache;
    private readonly SemaphoreSlim _propDefCacheLock = new(1, 1);

    public EntityMetadataService(IPrivStackSdk sdk, InfoPanelService infoPanelService)
    {
        _sdk = sdk;
        _infoPanelService = infoPanelService;
    }

    // =========================================================================
    // Entity Metadata (tags + property values)
    // =========================================================================

    /// <summary>
    /// Loads metadata for an entity: timestamps from the entity itself + tags/properties
    /// from the separate entity_metadata record.
    /// </summary>
    public async Task<EntityMetadata> GetMetadataAsync(string linkType, string entityId, CancellationToken ct = default)
    {
        var sdkEntityType = EntityTypeMap.GetEntityType(linkType);
        if (sdkEntityType == null)
            return new EntityMetadata(entityId, linkType, null, null, null, null, null, null, [], new());

        // Read the actual entity (for timestamps/title) and the metadata entity in parallel
        var entityTask = ReadEntityAsync(sdkEntityType, entityId, ct);
        var metaTask = ReadMetaEntityAsync(entityId, ct);

        await Task.WhenAll(entityTask, metaTask);

        var entity = await entityTask;
        var meta = await metaTask;

        // Extract timestamps, title, preview, and parent from the actual entity
        string? title = null;
        string? preview = null;
        DateTimeOffset? createdAt = null;
        DateTimeOffset? modifiedAt = null;
        string? parentId = null;
        string? parentTitle = null;

        if (entity.ValueKind != JsonValueKind.Undefined)
        {
            title = ExtractString(entity, "title")
                    ?? ExtractString(entity, "name")
                    ?? ExtractString(entity, "full_name");
            createdAt = ExtractDate(entity, "created_at");
            modifiedAt = ExtractDate(entity, "modified_at")
                         ?? ExtractDate(entity, "updated_at");
            preview = ExtractPreview(entity, 120);
            parentId = ExtractString(entity, "parent_id");
        }

        // Look up parent title if this entity has a parent
        if (parentId != null && sdkEntityType != null)
        {
            try
            {
                var parentEntity = await ReadEntityAsync(sdkEntityType, parentId, ct);
                if (parentEntity.ValueKind != JsonValueKind.Undefined)
                {
                    parentTitle = ExtractString(parentEntity, "title")
                                  ?? ExtractString(parentEntity, "name")
                                  ?? ExtractString(parentEntity, "full_name");
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Failed to read parent entity {ParentId}", parentId);
            }
        }

        // Extract tags/properties from the metadata entity
        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, JsonElement>();

        if (meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsProp.EnumerateArray())
                {
                    var s = t.GetString();
                    if (!string.IsNullOrEmpty(s))
                        tagSet.Add(s);
                }
            }

            if (meta.TryGetProperty("properties", out var propsProp) && propsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in propsProp.EnumerateObject())
                    properties[p.Name] = p.Value.Clone();
            }
        }

        // Merge tags stored directly on the entity model (e.g. TaskItem.Tags, Contact.Tags)
        if (entity.ValueKind == JsonValueKind.Object &&
            entity.TryGetProperty("tags", out var entityTagsProp) && entityTagsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in entityTagsProp.EnumerateArray())
            {
                var s = t.GetString();
                if (!string.IsNullOrEmpty(s))
                    tagSet.Add(s);
            }
        }

        var tags = tagSet.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        return new EntityMetadata(entityId, linkType, title, preview, createdAt, modifiedAt, parentId, parentTitle, tags, properties);
    }

    /// <summary>
    /// Updates the tags for an entity, creating the metadata entity if it doesn't exist.
    /// </summary>
    public async Task UpdateTagsAsync(string linkType, string entityId, List<string> tags, CancellationToken ct = default)
    {
        var metaId = $"emeta-{entityId}";
        var existing = await ReadMetaEntityAsync(entityId, ct);
        var isNew = existing.ValueKind == JsonValueKind.Undefined;

        // Build the metadata payload, preserving existing properties
        var properties = new Dictionary<string, JsonElement>();
        if (!isNew && existing.TryGetProperty("properties", out var propsProp) && propsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in propsProp.EnumerateObject())
                properties[p.Name] = p.Value.Clone();
        }

        await SaveMetaEntityAsync(metaId, linkType, entityId, tags, properties, isNew, ct);

        AddToTagCache(tags);
        _log.Debug("EntityMetadataService: saved {TagCount} tags for {LinkType}:{EntityId}", tags.Count, linkType, entityId);
    }

    /// <summary>
    /// Updates a single property value on an entity's metadata, creating the metadata entity if needed.
    /// </summary>
    public async Task UpdatePropertyAsync(string linkType, string entityId, string propertyDefId, JsonElement value, CancellationToken ct = default)
    {
        var metaId = $"emeta-{entityId}";
        var existing = await ReadMetaEntityAsync(entityId, ct);
        var isNew = existing.ValueKind == JsonValueKind.Undefined;

        var tags = ExtractTagsList(existing);
        var properties = ExtractPropertiesDict(existing);
        properties[propertyDefId] = value.Clone();

        await SaveMetaEntityAsync(metaId, linkType, entityId, tags, properties, isNew, ct);
        _log.Debug("EntityMetadataService: updated property {PropId} for {LinkType}:{EntityId}", propertyDefId, linkType, entityId);
    }

    /// <summary>
    /// Removes a property value from an entity's metadata.
    /// </summary>
    public async Task RemovePropertyAsync(string linkType, string entityId, string propertyDefId, CancellationToken ct = default)
    {
        var existing = await ReadMetaEntityAsync(entityId, ct);
        if (existing.ValueKind == JsonValueKind.Undefined) return;

        var metaId = $"emeta-{entityId}";
        var tags = ExtractTagsList(existing);
        var properties = ExtractPropertiesDict(existing);

        if (!properties.Remove(propertyDefId)) return;

        await SaveMetaEntityAsync(metaId, linkType, entityId, tags, properties, false, ct);
        _log.Debug("EntityMetadataService: removed property {PropId} from {LinkType}:{EntityId}", propertyDefId, linkType, entityId);
    }

    // =========================================================================
    // Tag Autocomplete Cache
    // =========================================================================

    /// <summary>
    /// Returns all known tags across all entities (lazy-cached).
    /// </summary>
    public async Task<List<string>> GetAllTagsAsync(CancellationToken ct = default)
    {
        if (_allTagsCache != null)
            return _allTagsCache;

        await _tagCacheLock.WaitAsync(ct);
        try
        {
            if (_allTagsCache != null)
                return _allTagsCache;

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = PluginId,
                Action = SdkAction.ReadList,
                EntityType = MetaEntityType,
            }, ct);

            if (response.Data != null)
            {
                foreach (var item in response.Data)
                {
                    if (item.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tagsProp.EnumerateArray())
                        {
                            var s = t.GetString();
                            if (!string.IsNullOrEmpty(s))
                                tags.Add(s);
                        }
                    }
                }
            }

            _allTagsCache = tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            _log.Debug("EntityMetadataService: tag cache built with {Count} unique tags", _allTagsCache.Count);
            return _allTagsCache;
        }
        finally
        {
            _tagCacheLock.Release();
        }
    }

    /// <summary>
    /// Clears the tag autocomplete cache, forcing a rebuild on next access.
    /// </summary>
    public void InvalidateTagCache()
    {
        _allTagsCache = null;
    }

    // =========================================================================
    // Property Definitions
    // =========================================================================

    /// <summary>
    /// Returns all property definitions (lazy-cached).
    /// </summary>
    public async Task<List<PropertyDefinition>> GetPropertyDefinitionsAsync(CancellationToken ct = default)
    {
        if (_propDefCache != null)
            return _propDefCache;

        await _propDefCacheLock.WaitAsync(ct);
        try
        {
            if (_propDefCache != null)
                return _propDefCache;

            var response = await _sdk.SendAsync<List<PropertyDefinition>>(new SdkMessage
            {
                PluginId = PluginId,
                Action = SdkAction.ReadList,
                EntityType = PropDefEntityType,
            }, ct);

            _propDefCache = response.Data?.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToList() ?? [];
            _log.Debug("EntityMetadataService: loaded {Count} property definitions", _propDefCache.Count);
            return _propDefCache;
        }
        finally
        {
            _propDefCacheLock.Release();
        }
    }

    /// <summary>
    /// Creates a new property definition.
    /// </summary>
    public async Task<PropertyDefinition> CreatePropertyDefinitionAsync(PropertyDefinition definition, CancellationToken ct = default)
    {
        var def = definition with { Id = string.IsNullOrEmpty(definition.Id) ? Guid.NewGuid().ToString() : definition.Id };
        var payload = JsonSerializer.Serialize(def, JsonOpts);

        var response = await _sdk.SendAsync<PropertyDefinition>(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Create,
            EntityType = PropDefEntityType,
            EntityId = def.Id,
            Payload = payload,
        }, ct);

        InvalidatePropertyDefCache();
        var result = response.Data ?? def;
        _log.Debug("EntityMetadataService: created property definition '{Name}' ({Id})", result.Name, result.Id);
        return result;
    }

    /// <summary>
    /// Updates an existing property definition.
    /// </summary>
    public async Task UpdatePropertyDefinitionAsync(PropertyDefinition definition, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(definition, JsonOpts);
        await _sdk.SendAsync(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Update,
            EntityType = PropDefEntityType,
            EntityId = definition.Id,
            Payload = payload,
        }, ct);

        InvalidatePropertyDefCache();
        _log.Debug("EntityMetadataService: updated property definition '{Name}' ({Id})", definition.Name, definition.Id);
    }

    /// <summary>
    /// Deletes a property definition.
    /// </summary>
    public async Task DeletePropertyDefinitionAsync(string definitionId, CancellationToken ct = default)
    {
        await _sdk.SendAsync(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Delete,
            EntityType = PropDefEntityType,
            EntityId = definitionId,
        }, ct);

        InvalidatePropertyDefCache();
        _log.Debug("EntityMetadataService: deleted property definition {Id}", definitionId);
    }

    // =========================================================================
    // Property Groups
    // =========================================================================

    /// <summary>
    /// Returns all property groups (lazy-cached).
    /// </summary>
    public async Task<List<PropertyGroup>> GetPropertyGroupsAsync(CancellationToken ct = default)
    {
        if (_propGroupCache != null)
            return _propGroupCache;

        var response = await _sdk.SendAsync<List<PropertyGroup>>(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.ReadList,
            EntityType = PropGroupEntityType,
        }, ct);

        _propGroupCache = response.Data?.OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToList() ?? [];
        _log.Debug("EntityMetadataService: loaded {Count} property groups", _propGroupCache.Count);
        return _propGroupCache;
    }

    /// <summary>
    /// Creates a new property group.
    /// </summary>
    public async Task<PropertyGroup> CreatePropertyGroupAsync(PropertyGroup group, CancellationToken ct = default)
    {
        var g = group with { Id = string.IsNullOrEmpty(group.Id) ? Guid.NewGuid().ToString() : group.Id };
        var payload = JsonSerializer.Serialize(g, JsonOpts);

        var response = await _sdk.SendAsync<PropertyGroup>(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Create,
            EntityType = PropGroupEntityType,
            EntityId = g.Id,
            Payload = payload,
        }, ct);

        InvalidatePropertyGroupCache();
        var result = response.Data ?? g;
        _log.Debug("EntityMetadataService: created property group '{Name}' ({Id})", result.Name, result.Id);
        return result;
    }

    /// <summary>
    /// Updates an existing property group.
    /// </summary>
    public async Task UpdatePropertyGroupAsync(PropertyGroup group, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(group, JsonOpts);
        await _sdk.SendAsync(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Update,
            EntityType = PropGroupEntityType,
            EntityId = group.Id,
            Payload = payload,
        }, ct);

        InvalidatePropertyGroupCache();
        _log.Debug("EntityMetadataService: updated property group '{Name}' ({Id})", group.Name, group.Id);
    }

    /// <summary>
    /// Deletes a property group. Definitions in this group become ungrouped.
    /// </summary>
    public async Task DeletePropertyGroupAsync(string groupId, CancellationToken ct = default)
    {
        await _sdk.SendAsync(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Delete,
            EntityType = PropGroupEntityType,
            EntityId = groupId,
        }, ct);

        InvalidatePropertyGroupCache();
        _log.Debug("EntityMetadataService: deleted property group {Id}", groupId);
    }

    // =========================================================================
    // Property Templates
    // =========================================================================

    /// <summary>
    /// Returns all property templates.
    /// </summary>
    public async Task<List<PropertyTemplate>> GetPropertyTemplatesAsync(CancellationToken ct = default)
    {
        var response = await _sdk.SendAsync<List<PropertyTemplate>>(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.ReadList,
            EntityType = PropTemplateEntityType,
        }, ct);

        return response.Data?.OrderBy(t => t.Name).ToList() ?? [];
    }

    /// <summary>
    /// Creates a new property template.
    /// </summary>
    public async Task<PropertyTemplate> CreatePropertyTemplateAsync(PropertyTemplate template, CancellationToken ct = default)
    {
        var t = template with { Id = string.IsNullOrEmpty(template.Id) ? Guid.NewGuid().ToString() : template.Id };
        var payload = JsonSerializer.Serialize(t, JsonOpts);

        var response = await _sdk.SendAsync<PropertyTemplate>(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Create,
            EntityType = PropTemplateEntityType,
            EntityId = t.Id,
            Payload = payload,
        }, ct);

        var result = response.Data ?? t;
        _log.Debug("EntityMetadataService: created template '{Name}' ({Id})", result.Name, result.Id);
        return result;
    }

    /// <summary>
    /// Updates an existing property template.
    /// </summary>
    public async Task UpdatePropertyTemplateAsync(PropertyTemplate template, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(template, JsonOpts);
        await _sdk.SendAsync(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Update,
            EntityType = PropTemplateEntityType,
            EntityId = template.Id,
            Payload = payload,
        }, ct);

        _log.Debug("EntityMetadataService: updated template '{Name}' ({Id})", template.Name, template.Id);
    }

    /// <summary>
    /// Deletes a property template.
    /// </summary>
    public async Task DeletePropertyTemplateAsync(string templateId, CancellationToken ct = default)
    {
        await _sdk.SendAsync(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.Delete,
            EntityType = PropTemplateEntityType,
            EntityId = templateId,
        }, ct);

        _log.Debug("EntityMetadataService: deleted template {Id}", templateId);
    }

    /// <summary>
    /// Applies a template to an entity: creates missing property definitions from inline defs,
    /// and stamps default values onto the entity's metadata for each template entry.
    /// </summary>
    public async Task ApplyTemplateAsync(string linkType, string entityId, PropertyTemplate template, CancellationToken ct = default)
    {
        var existingDefs = await GetPropertyDefinitionsAsync(ct);
        var existingDefIds = existingDefs.ToDictionary(d => d.Id);

        var existing = await ReadMetaEntityAsync(entityId, ct);
        var isNew = existing.ValueKind == JsonValueKind.Undefined;
        var tags = ExtractTagsList(existing);
        var properties = ExtractPropertiesDict(existing);

        foreach (var entry in template.Entries)
        {
            // Ensure the property definition exists
            if (!existingDefIds.ContainsKey(entry.PropertyDefId) && entry.InlineDefinition != null)
            {
                var created = await CreatePropertyDefinitionAsync(
                    entry.InlineDefinition with { Id = entry.PropertyDefId }, ct);
                existingDefIds[created.Id] = created;
            }

            // Stamp all template entries so the property appears on the entity
            if (!properties.ContainsKey(entry.PropertyDefId))
            {
                properties[entry.PropertyDefId] = entry.DefaultValue != null
                    ? JsonSerializer.SerializeToElement(entry.DefaultValue)
                    : JsonSerializer.SerializeToElement("");
            }
        }

        var metaId = $"emeta-{entityId}";
        await SaveMetaEntityAsync(metaId, linkType, entityId, tags, properties, isNew, ct);

        _log.Debug("EntityMetadataService: applied template '{Name}' to {LinkType}:{EntityId} ({EntryCount} entries)",
            template.Name, linkType, entityId, template.Entries.Count);
    }

    // =========================================================================
    // Default Template Seeding
    // =========================================================================

    /// <summary>
    /// Seeds default property definitions and templates if none exist yet.
    /// Called once when the user first opens the template manager.
    /// </summary>
    public async Task SeedDefaultTemplatesAsync(CancellationToken ct = default)
    {
        var existing = await GetPropertyTemplatesAsync(ct);
        if (existing.Count > 0) return;

        _log.Information("EntityMetadataService: seeding default property definitions and templates");

        try
        {
            // Property definitions — metadata that complements (not replicates) existing plugins.
            // Tasks handles: status, priority, due dates, assignees, completion.
            // Calendar handles: event dates, times, recurrence.
            // Tags system handles: freeform categorization.
            // Properties are for: attribution, references, ratings, typed categorization, custom text/numbers.

            var url = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "URL", Type = PropertyType.Url, Icon = "Link", SortOrder = 10
            }, ct);

            var source = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Source", Type = PropertyType.Text, Icon = "ExternalLink", SortOrder = 20
            }, ct);

            var author = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Author", Type = PropertyType.Text, Icon = "User", SortOrder = 30
            }, ct);

            var rating = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Rating", Type = PropertyType.Number, SortOrder = 40
            }, ct);

            var category = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Category", Type = PropertyType.Select, Icon = "Folder",
                Options = ["Work", "Personal", "Research", "Finance", "Health"], SortOrder = 50
            }, ct);

            var genre = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Genre", Type = PropertyType.Select, Icon = "Folder",
                Options = ["Fiction", "Non-Fiction", "Technical", "Biography", "Self-Help"], SortOrder = 60
            }, ct);

            var difficulty = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Difficulty", Type = PropertyType.Select,
                Options = ["Beginner", "Intermediate", "Advanced", "Expert"], SortOrder = 70
            }, ct);

            var language = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Language", Type = PropertyType.Text, SortOrder = 80
            }, ct);

            var version = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Version", Type = PropertyType.Text, SortOrder = 90
            }, ct);

            var cost = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Cost", Type = PropertyType.Number, SortOrder = 100
            }, ct);

            var location = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Location", Type = PropertyType.Text, SortOrder = 110
            }, ct);

            var progress = await CreatePropertyDefinitionAsync(new PropertyDefinition
            {
                Name = "Progress", Type = PropertyType.Select,
                Options = ["Not Started", "25%", "50%", "75%", "Complete"], SortOrder = 120
            }, ct);

            // --- Templates ---

            // Reading List — track books, articles, and resources
            await CreatePropertyTemplateAsync(new PropertyTemplate
            {
                Name = "Reading List",
                Description = "Track books, articles, and reading materials",
                Entries =
                [
                    new() { PropertyDefId = author.Id },
                    new() { PropertyDefId = genre.Id },
                    new() { PropertyDefId = rating.Id },
                    new() { PropertyDefId = url.Id },
                    new() { PropertyDefId = progress.Id, DefaultValue = "Not Started" },
                ]
            }, ct);

            // Research Notes — capture source material and references
            await CreatePropertyTemplateAsync(new PropertyTemplate
            {
                Name = "Research Notes",
                Description = "Capture research with sources and attribution",
                Entries =
                [
                    new() { PropertyDefId = source.Id },
                    new() { PropertyDefId = url.Id },
                    new() { PropertyDefId = author.Id },
                    new() { PropertyDefId = category.Id },
                ]
            }, ct);

            // Learning Resource — courses, tutorials, documentation
            await CreatePropertyTemplateAsync(new PropertyTemplate
            {
                Name = "Learning Resource",
                Description = "Track courses, tutorials, and learning materials",
                Entries =
                [
                    new() { PropertyDefId = url.Id },
                    new() { PropertyDefId = author.Id },
                    new() { PropertyDefId = difficulty.Id },
                    new() { PropertyDefId = language.Id },
                    new() { PropertyDefId = progress.Id, DefaultValue = "Not Started" },
                ]
            }, ct);

            // Recipe — cooking and preparation
            await CreatePropertyTemplateAsync(new PropertyTemplate
            {
                Name = "Recipe",
                Description = "Store recipes with metadata",
                Entries =
                [
                    new() { PropertyDefId = source.Id },
                    new() { PropertyDefId = rating.Id },
                    new() { PropertyDefId = difficulty.Id },
                    new() { PropertyDefId = category.Id },
                ]
            }, ct);

            // Decision Record — document decisions and context
            await CreatePropertyTemplateAsync(new PropertyTemplate
            {
                Name = "Decision Record",
                Description = "Document architectural or business decisions",
                Entries =
                [
                    new() { PropertyDefId = category.Id },
                    new() { PropertyDefId = version.Id },
                ]
            }, ct);

            _log.Information("EntityMetadataService: seeded {Count} default templates", 5);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to seed default templates");
        }
    }

    // =========================================================================
    // Orphaned Metadata Validation
    // =========================================================================

    public record MetadataValidationResult(int TotalScanned, int OrphansRemoved, int SkippedUnknownType);

    /// <summary>
    /// Scans all entity_metadata records and deletes any whose target entity no longer exists.
    /// Unknown target types are skipped (not deleted) since orphan status can't be confirmed.
    /// </summary>
    public async Task<MetadataValidationResult> ValidateAndCleanOrphansAsync(
        LinkProviderCacheService linkProviderCache, CancellationToken ct = default)
    {
        _log.Information("ValidateAndCleanOrphans: starting metadata validation");

        var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
        {
            PluginId = PluginId,
            Action = SdkAction.ReadList,
            EntityType = MetaEntityType,
        }, ct);

        var metaRecords = response.Data;
        if (metaRecords is not { Count: > 0 })
        {
            _log.Information("ValidateAndCleanOrphans: no metadata records found");
            return new MetadataValidationResult(0, 0, 0);
        }

        // Extract (metaId, targetType, targetId) from each record
        var entries = new List<(string MetaId, string TargetType, string TargetId)>();
        foreach (var record in metaRecords)
        {
            var metaId = record.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var targetType = record.TryGetProperty("target_type", out var typeProp) ? typeProp.GetString() : null;
            var targetId = record.TryGetProperty("target_id", out var tidProp) ? tidProp.GetString() : null;

            if (metaId != null && targetType != null && targetId != null)
                entries.Add((metaId, targetType, targetId));
        }

        var totalScanned = entries.Count;
        int skippedUnknownType = 0;

        // For each unique target type, load all valid entity IDs
        var validIdsByType = new Dictionary<string, HashSet<string>>();

        foreach (var group in entries.GroupBy(e => e.TargetType))
        {
            var linkType = group.Key;
            var entityType = EntityTypeMap.GetEntityType(linkType);
            if (entityType == null)
            {
                _log.Warning("ValidateAndCleanOrphans: unknown target_type '{TargetType}', skipping {Count} records",
                    linkType, group.Count());
                skippedUnknownType += group.Count();
                continue;
            }

            var pluginId = linkProviderCache.GetPluginIdForLinkType(linkType) ?? PluginId;

            try
            {
                var entityResponse = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
                {
                    PluginId = pluginId,
                    Action = SdkAction.ReadList,
                    EntityType = entityType,
                }, ct);

                var ids = new HashSet<string>();
                if (entityResponse.Data != null)
                {
                    foreach (var entity in entityResponse.Data)
                    {
                        var id = entity.TryGetProperty("id", out var eProp) ? eProp.GetString() : null;
                        if (id != null) ids.Add(id);
                    }
                }

                validIdsByType[linkType] = ids;
                _log.Debug("ValidateAndCleanOrphans: {Count} valid IDs for {LinkType}", ids.Count, linkType);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "ValidateAndCleanOrphans: failed to load entities for {LinkType}, skipping", linkType);
                skippedUnknownType += group.Count();
            }
        }

        // Delete metadata records whose target entity no longer exists
        int orphansRemoved = 0;
        foreach (var entry in entries)
        {
            if (!validIdsByType.TryGetValue(entry.TargetType, out var ids))
                continue;

            if (!ids.Contains(entry.TargetId))
            {
                try
                {
                    await _sdk.SendAsync(new SdkMessage
                    {
                        PluginId = PluginId,
                        Action = SdkAction.Delete,
                        EntityType = MetaEntityType,
                        EntityId = entry.MetaId,
                    }, ct);
                    orphansRemoved++;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "ValidateAndCleanOrphans: failed to delete orphan {MetaId}", entry.MetaId);
                }
            }
        }

        InvalidateAll();

        _log.Information("ValidateAndCleanOrphans: scanned {Total}, removed {Orphans}, skipped {Skipped}",
            totalScanned, orphansRemoved, skippedUnknownType);

        return new MetadataValidationResult(totalScanned, orphansRemoved, skippedUnknownType);
    }

    // =========================================================================
    // Cache Invalidation
    // =========================================================================

    public void InvalidatePropertyDefCache() => _propDefCache = null;
    public void InvalidatePropertyGroupCache() => _propGroupCache = null;

    public void InvalidateAll()
    {
        _allTagsCache = null;
        _propDefCache = null;
        _propGroupCache = null;
    }

    // =========================================================================
    // Private Helpers
    // =========================================================================

    private async Task SaveMetaEntityAsync(
        string metaId, string linkType, string entityId,
        List<string> tags, Dictionary<string, JsonElement> properties,
        bool isNew, CancellationToken ct)
    {
        var payload = new
        {
            id = metaId,
            target_type = linkType,
            target_id = entityId,
            tags,
            properties
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
        _log.Debug("SaveMetaEntityAsync: {Action} {MetaId} payload={Payload}",
            isNew ? "Create" : "Update", metaId, payloadJson);

        SdkResponse response;
        if (isNew)
        {
            response = await _sdk.SendAsync(new SdkMessage
            {
                PluginId = PluginId,
                Action = SdkAction.Create,
                EntityType = MetaEntityType,
                EntityId = metaId,
                Payload = payloadJson,
            }, ct);
        }
        else
        {
            response = await _sdk.SendAsync(new SdkMessage
            {
                PluginId = PluginId,
                Action = SdkAction.Update,
                EntityType = MetaEntityType,
                EntityId = metaId,
                Payload = payloadJson,
            }, ct);
        }

        if (!response.Success)
        {
            _log.Error("SaveMetaEntityAsync: {Action} FAILED for {MetaId}: [{ErrorCode}] {ErrorMessage}",
                isNew ? "Create" : "Update", metaId, response.ErrorCode, response.ErrorMessage);
        }
    }

    private static List<string> ExtractTagsList(JsonElement meta)
    {
        var tags = new List<string>();
        if (meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("tags", out var tagsProp) &&
            tagsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagsProp.EnumerateArray())
            {
                var s = t.GetString();
                if (!string.IsNullOrEmpty(s))
                    tags.Add(s);
            }
        }
        return tags;
    }

    private static Dictionary<string, JsonElement> ExtractPropertiesDict(JsonElement meta)
    {
        var properties = new Dictionary<string, JsonElement>();
        if (meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("properties", out var propsProp) &&
            propsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in propsProp.EnumerateObject())
                properties[p.Name] = p.Value.Clone();
        }
        return properties;
    }

    private void AddToTagCache(List<string> tags)
    {
        if (_allTagsCache == null) return;
        var changed = false;
        foreach (var tag in tags)
        {
            if (!_allTagsCache.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                _allTagsCache.Add(tag);
                changed = true;
            }
        }
        if (changed)
            _allTagsCache.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> ReadEntityAsync(string sdkEntityType, string entityId, CancellationToken ct)
    {
        try
        {
            var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
            {
                PluginId = PluginId,
                Action = SdkAction.Read,
                EntityType = sdkEntityType,
                EntityId = entityId,
            }, ct);
            return response.Data;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "EntityMetadataService: failed to read {EntityType}/{EntityId}", sdkEntityType, entityId);
            return default;
        }
    }

    private async Task<JsonElement> ReadMetaEntityAsync(string entityId, CancellationToken ct)
    {
        try
        {
            var metaId = $"emeta-{entityId}";
            var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
            {
                PluginId = PluginId,
                Action = SdkAction.Read,
                EntityType = MetaEntityType,
                EntityId = metaId,
            }, ct);

            if (!response.Success)
            {
                _log.Debug("ReadMetaEntityAsync: Read FAILED for {MetaId}: [{ErrorCode}] {ErrorMessage}",
                    metaId, response.ErrorCode, response.ErrorMessage);
                return default;
            }

            _log.Debug("ReadMetaEntityAsync: {MetaId} -> {ValueKind}", metaId, response.Data.ValueKind);
            return response.Data;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "EntityMetadataService: no metadata entity for {EntityId}", entityId);
            return default;
        }
    }

    private static string? ExtractString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static DateTimeOffset? ExtractDate(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(prop.GetString(), out var date))
                return date;
        }
        return null;
    }

    /// <summary>
    /// Extracts a plain-text preview from the entity's content fields (content, description, notes, body).
    /// Strips structural JSON and returns the first N characters of readable text.
    /// </summary>
    private static string? ExtractPreview(JsonElement entity, int maxLength)
    {
        var parts = new List<string>();

        foreach (var field in new[] { "content", "description", "notes", "body" })
        {
            if (!entity.TryGetProperty(field, out var prop)) continue;

            if (prop.ValueKind == JsonValueKind.String)
            {
                var text = prop.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }
            else if (prop.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                ExtractTextStringsFromJson(prop, parts);
            }
        }

        if (parts.Count == 0) return null;

        var combined = string.Join(" ", parts.Select(p => p.Trim()));

        // Strip wiki-link markup [[type:id|Title]] → Title
        combined = System.Text.RegularExpressions.Regex.Replace(
            combined, @"\[\[[^\]|]+\|([^\]]+)\]\]", "$1");

        // Strip privstack:// URLs
        combined = System.Text.RegularExpressions.Regex.Replace(
            combined, @"\[([^\]]+)\]\(privstack://[^)]+\)", "$1");

        // Strip HTML tags (e.g. <span style="color:blue">text</span> → text)
        combined = System.Text.RegularExpressions.Regex.Replace(combined, @"<[^>]+>", "");

        // Strip HTML entities (e.g. &amp; → &, &nbsp; → space)
        combined = System.Net.WebUtility.HtmlDecode(combined);

        // Collapse whitespace
        combined = System.Text.RegularExpressions.Regex.Replace(combined, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(combined)) return null;
        return combined.Length > maxLength ? combined[..maxLength] + "..." : combined;
    }

    private static void ExtractTextStringsFromJson(JsonElement element, List<string> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    results.Add(text);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // Skip non-text fields to avoid noise (ids, types, urls, etc.)
                    if (property.Name is "id" or "type" or "block_type" or "language"
                        or "url" or "src" or "href" or "checked") continue;
                    ExtractTextStringsFromJson(property.Value, results);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractTextStringsFromJson(item, results);
                break;
        }
    }
}
