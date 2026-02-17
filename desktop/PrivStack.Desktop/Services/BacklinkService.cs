using System.Text.Json;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Helpers;
using PrivStack.Sdk.Json;
using PrivStack.Desktop.Services.Plugin;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Backlink entry representing an item that links TO the queried target.
/// </summary>
public sealed record BacklinkEntry(
    string SourceId,
    string SourceLinkType,
    string SourceTitle,
    string? SourceIcon,
    string? ContextSnippet,
    DateTimeOffset? ModifiedAt = null);

/// <summary>
/// Computes backlinks by aggregating IGraphDataProvider contributions and legacy entity loading.
/// Parses wiki-links from content fields and builds forward + reverse indices.
/// Caches the result until explicitly invalidated.
/// </summary>
public sealed class BacklinkService
{
    private static readonly ILogger _log = Log.ForContext<BacklinkService>();
    private static readonly JsonSerializerOptions JsonOptions = SdkJsonOptions.Default;

    private readonly IPrivStackSdk _sdk;
    private readonly IPluginRegistry _pluginRegistry;

    // Cached reverse index: "linkType:itemId" → list of backlink entries
    private Dictionary<string, List<BacklinkEntry>>? _reverseIndex;

    // Forward links for local graph: "linkType:itemId" → set of "linkType:itemId" targets
    private Dictionary<string, HashSet<string>>? _forwardLinks;

    // All known entities (for graph node titles): "linkType:itemId" → (title, linkType, modifiedAt, icon)
    private Dictionary<string, (string Title, string LinkType, DateTimeOffset? ModifiedAt, string? Icon)>? _entityMap;

    private readonly SemaphoreSlim _buildLock = new(1, 1);

    // Temporary storage for page parent_id during legacy index build
    private readonly Dictionary<string, string> _pageParentMap = new();

    // Temporary storage for wiki page source_id during legacy index build
    private readonly Dictionary<string, string> _pageWikiSourceMap = new();

    // Entity types to scan — derived from the shared EntityTypeMap.
    private static readonly (string EntityType, string LinkType, string Icon)[] EntityTypes =
        EntityTypeMap.All.Select(e => (e.EntityType, e.LinkType, e.Icon)).ToArray();

    public BacklinkService(IPrivStackSdk sdk, IPluginRegistry pluginRegistry)
    {
        _sdk = sdk;
        _pluginRegistry = pluginRegistry;
    }

    /// <summary>
    /// Returns all items that link TO the specified entity.
    /// </summary>
    public async Task<List<BacklinkEntry>> GetBacklinksAsync(string linkType, string itemId, CancellationToken ct = default)
    {
        await EnsureIndexBuiltAsync(ct);
        var key = $"{linkType}:{itemId}";
        List<BacklinkEntry>? entries = null;
        var found = _reverseIndex != null && _reverseIndex.TryGetValue(key, out entries);
        _log.Information("BacklinkService: GetBacklinks({Key}) -> {Count} results (entityMap has key: {HasKey})",
            key, found ? entries!.Count : 0,
            _entityMap?.ContainsKey(key) ?? false);
        return found ? entries! : [];
    }

    /// <summary>
    /// Returns all items that the specified entity links TO (forward/outgoing links).
    /// </summary>
    public async Task<List<BacklinkEntry>> GetForwardLinksAsync(string linkType, string itemId, CancellationToken ct = default)
    {
        await EnsureIndexBuiltAsync(ct);
        var key = $"{linkType}:{itemId}";
        var results = new List<BacklinkEntry>();

        if (_forwardLinks == null || _entityMap == null) return results;
        if (!_forwardLinks.TryGetValue(key, out var targetKeys)) return results;

        foreach (var targetKey in targetKeys)
        {
            if (!_entityMap.TryGetValue(targetKey, out var info)) continue;
            var parts = targetKey.Split(':', 2);
            if (parts.Length != 2) continue;

            results.Add(new BacklinkEntry(
                parts[1],
                info.LinkType,
                info.Title,
                info.Icon,
                null,
                info.ModifiedAt));
        }

        _log.Information("BacklinkService: GetForwardLinks({Key}) -> {Count} results", key, results.Count);
        return results;
    }

    /// <summary>
    /// Returns forward + backward link neighbors via BFS up to the given depth.
    /// Optionally filters out nodes whose linkType is not in allowedLinkTypes (center node always kept).
    /// Returns (nodes, edges) suitable for NeuronGraphControl.
    /// </summary>
    public async Task<(List<JsonElement> Nodes, List<JsonElement> Edges)> GetLocalGraphAsync(
        string linkType, string itemId, int depth = 1, HashSet<string>? allowedLinkTypes = null,
        CancellationToken ct = default)
    {
        await EnsureIndexBuiltAsync(ct);

        var centerKey = $"{linkType}:{itemId}";
        var depthMap = new Dictionary<string, int> { [centerKey] = 0 };
        var frontier = new HashSet<string> { centerKey };

        for (var hop = 1; hop <= depth; hop++)
        {
            var nextFrontier = new HashSet<string>();
            foreach (var key in frontier)
            {
                if (_forwardLinks != null && _forwardLinks.TryGetValue(key, out var fwd))
                    foreach (var k in fwd)
                        if (!depthMap.ContainsKey(k))
                        {
                            depthMap[k] = hop;
                            nextFrontier.Add(k);
                        }

                if (_reverseIndex != null && _reverseIndex.TryGetValue(key, out var bl))
                    foreach (var entry in bl)
                    {
                        var k = $"{entry.SourceLinkType}:{entry.SourceId}";
                        if (!depthMap.ContainsKey(k))
                        {
                            depthMap[k] = hop;
                            nextFrontier.Add(k);
                        }
                    }
            }
            frontier = nextFrontier;
            if (frontier.Count == 0) break;
        }

        var neighborKeys = new HashSet<string>();
        if (_entityMap != null)
        {
            foreach (var key in depthMap.Keys)
            {
                if (key == centerKey) { neighborKeys.Add(key); continue; }
                if (!_entityMap.TryGetValue(key, out var info)) continue;
                if (allowedLinkTypes != null && !allowedLinkTypes.Contains(info.LinkType)) continue;
                neighborKeys.Add(key);
            }
        }

        var nodes = new List<JsonElement>();
        var edges = new List<JsonElement>();

        if (_entityMap == null) return (nodes, edges);

        foreach (var key in neighborKeys)
        {
            if (!_entityMap.TryGetValue(key, out var info)) continue;
            var nodeDepth = depthMap.GetValueOrDefault(key, 1);
            var node = JsonSerializer.SerializeToElement(new
            {
                id = key,
                title = info.Title,
                node_type = info.LinkType,
                link_type = info.LinkType,
                link_count = CountLinks(key),
                depth = nodeDepth,
            }, JsonOptions);
            nodes.Add(node);
        }

        if (_forwardLinks != null)
            foreach (var sourceKey in neighborKeys)
            {
                if (!_forwardLinks.TryGetValue(sourceKey, out var fwdSet)) continue;
                foreach (var target in fwdSet)
                    if (neighborKeys.Contains(target) && target != sourceKey)
                        edges.Add(JsonSerializer.SerializeToElement(new
                        {
                            source_id = sourceKey,
                            target_id = target,
                            edge_type = "link"
                        }, JsonOptions));
            }

        return (nodes, edges);
    }

    /// <summary>
    /// Returns the set of link types that have at least one entity in the index.
    /// </summary>
    public async Task<HashSet<string>> GetAvailableLinkTypesAsync(CancellationToken ct = default)
    {
        await EnsureIndexBuiltAsync(ct);
        var types = new HashSet<string>();
        if (_entityMap != null)
            foreach (var info in _entityMap.Values)
                types.Add(info.LinkType);
        return types;
    }

    /// <summary>
    /// Invalidates the cached index, forcing a rebuild on next query.
    /// </summary>
    public void Invalidate()
    {
        _log.Information("BacklinkService: cache invalidated");
        _reverseIndex = null;
        _forwardLinks = null;
        _entityMap = null;
    }

    private int CountLinks(string key)
    {
        var count = 0;
        if (_forwardLinks != null && _forwardLinks.TryGetValue(key, out var fwd))
            count += fwd.Count;
        if (_reverseIndex != null && _reverseIndex.TryGetValue(key, out var bl))
            count += bl.Count;
        return count;
    }

    private async Task EnsureIndexBuiltAsync(CancellationToken ct)
    {
        if (_reverseIndex != null) return;

        await _buildLock.WaitAsync(ct);
        try
        {
            if (_reverseIndex != null) return;
            await BuildIndexAsync(ct);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private async Task BuildIndexAsync(CancellationToken ct)
    {
        _log.Information("BacklinkService: building index...");

        var entityMap = new Dictionary<string, (string Title, string LinkType, DateTimeOffset? ModifiedAt, string? Icon)>();
        var entityContents = new Dictionary<string, string>();
        var entityExplicitLinks = new Dictionary<string, List<string>>();
        var entityTags = new Dictionary<string, List<string>>();
        var coveredLinkTypes = new HashSet<string>();

        // --- Phase 1: Collect from IGraphDataProvider providers ---
        var providers = _pluginRegistry.GetCapabilityProviders<IGraphDataProvider>();
        if (providers.Count > 0)
        {
            var providerTasks = providers.Select(async p =>
            {
                try { return await p.GetGraphContributionAsync(ct); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "BacklinkService: provider {Provider} failed", p.GetType().Name);
                    return null;
                }
            }).ToArray();
            var contributions = await Task.WhenAll(providerTasks);

            foreach (var contribution in contributions)
            {
                if (contribution == null) continue;

                foreach (var node in contribution.Nodes)
                {
                    coveredLinkTypes.Add(node.LinkType);
                    entityMap[node.CompositeKey] = (node.Title, node.LinkType, node.ModifiedAt, node.Icon);
                    if (node.Tags.Count > 0)
                        entityTags[node.CompositeKey] = node.Tags.ToList();
                }

                foreach (var field in contribution.ContentFields)
                {
                    if (!string.IsNullOrEmpty(field.Content))
                        entityContents[field.OwnerKey] = field.Content;
                }

                // Convert structural edges to forward links
                foreach (var edge in contribution.StructuralEdges)
                {
                    if (!entityMap.ContainsKey(edge.SourceKey) || !entityMap.ContainsKey(edge.TargetKey)) continue;
                    AddForwardAndReverseLinks(edge.SourceKey, edge.TargetKey, entityMap,
                        entityContents, entityExplicitLinks, isExplicit: false);
                }

                foreach (var link in contribution.ExplicitLinks)
                {
                    if (!entityExplicitLinks.TryGetValue(link.SourceKey, out var links))
                    {
                        links = [];
                        entityExplicitLinks[link.SourceKey] = links;
                    }
                    links.Add(link.TargetKey);
                }
            }

            _log.Information("BacklinkService: {ProviderCount} providers contributed {NodeCount} entities, " +
                "{CoveredTypes} link types covered",
                providers.Count, entityMap.Count(kv => coveredLinkTypes.Contains(kv.Value.LinkType)),
                coveredLinkTypes.Count);
        }

        // --- Phase 2: Legacy entity loading for uncovered types ---
        var legacyEntityTypes = EntityTypes.Where(et => !coveredLinkTypes.Contains(et.LinkType)).ToArray();
        if (legacyEntityTypes.Length > 0)
        {
            var tasks = legacyEntityTypes.Select(et => LoadEntitiesAsync(et.EntityType, et.LinkType, ct)).ToArray();
            var results = await Task.WhenAll(tasks);

            for (var i = 0; i < results.Length; i++)
            {
                var linkType = legacyEntityTypes[i].LinkType;
                var entityType = legacyEntityTypes[i].EntityType;
                var icon = legacyEntityTypes[i].Icon;
                var withContent = 0;
                foreach (var (id, title, content, explicitLinks, modifiedAt, tags) in results[i])
                {
                    var key = $"{linkType}:{id}";
                    entityMap[key] = (title, linkType, modifiedAt, icon);
                    if (!string.IsNullOrEmpty(content))
                    {
                        entityContents[key] = content;
                        withContent++;
                    }
                    if (explicitLinks.Count > 0)
                        entityExplicitLinks[key] = explicitLinks;
                    if (tags.Count > 0)
                        entityTags[key] = tags;
                }
                _log.Information("BacklinkService: loaded {Count} {EntityType} entities ({WithContent} with content)",
                    results[i].Count, entityType, withContent);
            }
        }

        // For legacy entities without content from list view, load individually
        var legacyLinkTypeSet = new HashSet<string>(legacyEntityTypes.Select(et => et.LinkType));
        var entitiesToFetch = entityMap.Keys
            .Where(k => !entityContents.ContainsKey(k) && legacyLinkTypeSet.Contains(k.Split(':', 2)[0]))
            .ToList();
        if (entitiesToFetch.Count > 0)
        {
            _log.Information("BacklinkService: fetching content for {Count} entities missing from list view", entitiesToFetch.Count);
            var fetchTasks = entitiesToFetch.Select(async key =>
            {
                try
                {
                    var parts = key.Split(':', 2);
                    if (parts.Length != 2) return;
                    var linkType = parts[0];

                    var sdkEntityType = EntityTypes.FirstOrDefault(et => et.LinkType == linkType).EntityType;
                    if (sdkEntityType == null) return;

                    var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
                    {
                        PluginId = "privstack.graph",
                        Action = SdkAction.Read,
                        EntityType = sdkEntityType,
                        EntityId = parts[1],
                    }, ct);

                    if (response.Data.ValueKind == JsonValueKind.Undefined) return;

                    var content = WikiLinkParser.ExtractContentFromEntity(response.Data);
                    var explicitLinks = ExtractExplicitLinksFromCustomFields(response.Data);

                    if (!string.IsNullOrEmpty(content))
                        lock (entityContents) { entityContents[key] = content; }
                    if (explicitLinks.Count > 0)
                        lock (entityExplicitLinks) { entityExplicitLinks[key] = explicitLinks; }
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "BacklinkService: failed to fetch content for {Key}", key);
                }
            });
            await Task.WhenAll(fetchTasks);

            _log.Information("BacklinkService: after individual fetches, {ContentCount} entities now have content",
                entityContents.Count);
        }

        // --- Phase 3: Build forward + reverse indices from wiki-links ---
        var reverseIndex = new Dictionary<string, List<BacklinkEntry>>();
        var forwardLinks = new Dictionary<string, HashSet<string>>();
        var totalLinksFound = 0;
        var totalLinksResolved = 0;

        foreach (var (sourceKey, content) in entityContents)
        {
            if (!entityMap.TryGetValue(sourceKey, out var sourceInfo)) continue;

            var parsedLinks = WikiLinkParser.ParseLinks(content);
            foreach (var link in parsedLinks)
            {
                ct.ThrowIfCancellationRequested();
                totalLinksFound++;

                if (sourceKey == link.CompositeKey) continue;
                if (!entityMap.ContainsKey(link.CompositeKey))
                {
                    _log.Debug("BacklinkService: unresolved link {SourceKey} -> {TargetKey}",
                        sourceKey, link.CompositeKey);
                    continue;
                }
                totalLinksResolved++;

                if (!forwardLinks.TryGetValue(sourceKey, out var fwdSet))
                {
                    fwdSet = [];
                    forwardLinks[sourceKey] = fwdSet;
                }
                fwdSet.Add(link.CompositeKey);

                if (!reverseIndex.TryGetValue(link.CompositeKey, out var entries))
                {
                    entries = [];
                    reverseIndex[link.CompositeKey] = entries;
                }

                var parts = sourceKey.Split(':', 2);
                var sourceId = parts.Length == 2 ? parts[1] : sourceKey;
                if (entries.Any(e => e.SourceId == sourceId && e.SourceLinkType == sourceInfo.LinkType))
                    continue;

                var snippet = WikiLinkParser.ExtractSnippet(content, link.MatchIndex);

                entries.Add(new BacklinkEntry(
                    sourceId, sourceInfo.LinkType, sourceInfo.Title, sourceInfo.Icon,
                    snippet, sourceInfo.ModifiedAt));
            }
        }

        // --- Phase 4: Process explicit links ---
        var explicitLinksFound = 0;
        var explicitLinksResolved = 0;
        foreach (var (sourceKey, explicitLinks) in entityExplicitLinks)
        {
            if (!entityMap.TryGetValue(sourceKey, out var sourceInfo)) continue;

            foreach (var targetKey in explicitLinks)
            {
                ct.ThrowIfCancellationRequested();
                explicitLinksFound++;

                if (sourceKey == targetKey) continue;
                if (!entityMap.ContainsKey(targetKey))
                {
                    _log.Debug("BacklinkService: unresolved explicit link {SourceKey} -> {TargetKey}",
                        sourceKey, targetKey);
                    continue;
                }
                explicitLinksResolved++;

                if (!forwardLinks.TryGetValue(sourceKey, out var fwdSet))
                {
                    fwdSet = [];
                    forwardLinks[sourceKey] = fwdSet;
                }
                fwdSet.Add(targetKey);

                if (!reverseIndex.TryGetValue(targetKey, out var entries))
                {
                    entries = [];
                    reverseIndex[targetKey] = entries;
                }

                var parts = sourceKey.Split(':', 2);
                var sourceId = parts.Length == 2 ? parts[1] : sourceKey;
                if (entries.Any(e => e.SourceId == sourceId && e.SourceLinkType == sourceInfo.LinkType))
                    continue;

                entries.Add(new BacklinkEntry(
                    sourceId, sourceInfo.LinkType, sourceInfo.Title, sourceInfo.Icon,
                    null, sourceInfo.ModifiedAt));
            }
        }

        // --- Phase 5: Legacy parent-child relationships ---
        var parentChildCount = 0;
        foreach (var (key, info) in entityMap)
        {
            if (info.LinkType != "page") continue;
            var parts = key.Split(':', 2);
            if (parts.Length != 2) continue;

            if (!_pageParentMap.TryGetValue(parts[1], out var parentId)) continue;
            if (string.IsNullOrEmpty(parentId)) continue;

            var parentKey = $"page:{parentId}";
            if (!entityMap.ContainsKey(parentKey)) continue;

            if (!forwardLinks.TryGetValue(key, out var fwdSet))
            {
                fwdSet = [];
                forwardLinks[key] = fwdSet;
            }
            fwdSet.Add(parentKey);

            if (!reverseIndex.TryGetValue(parentKey, out var entries))
            {
                entries = [];
                reverseIndex[parentKey] = entries;
            }

            if (entries.All(e => e.SourceId != parts[1] || e.SourceLinkType != "page"))
            {
                entries.Add(new BacklinkEntry(
                    parts[1], "page", info.Title, info.Icon,
                    "Child page", info.ModifiedAt));
                parentChildCount++;
            }

            if (!reverseIndex.TryGetValue(key, out var childEntries))
            {
                childEntries = [];
                reverseIndex[key] = childEntries;
            }
            var parentInfo = entityMap[parentKey];
            if (childEntries.All(e => e.SourceId != parentId || e.SourceLinkType != "page"))
            {
                childEntries.Add(new BacklinkEntry(
                    parentId, "page", parentInfo.Title, parentInfo.Icon,
                    "Parent page", parentInfo.ModifiedAt));
            }
        }
        _pageParentMap.Clear();

        if (parentChildCount > 0)
            _log.Information("BacklinkService: added {Count} parent-child relationships", parentChildCount);

        // --- Phase 6: Legacy wiki source relationships ---
        var wikiSourceCount = 0;
        foreach (var (pageId, sourceId) in _pageWikiSourceMap)
        {
            var pageKey = $"page:{pageId}";
            var wikiKey = $"wiki_source:{sourceId}";
            if (!entityMap.ContainsKey(pageKey) || !entityMap.ContainsKey(wikiKey)) continue;

            if (!forwardLinks.TryGetValue(pageKey, out var fwdSet))
            {
                fwdSet = [];
                forwardLinks[pageKey] = fwdSet;
            }
            fwdSet.Add(wikiKey);

            if (!reverseIndex.TryGetValue(wikiKey, out var entries))
            {
                entries = [];
                reverseIndex[wikiKey] = entries;
            }
            var pageInfo = entityMap[pageKey];
            if (entries.All(e => e.SourceId != pageId || e.SourceLinkType != "page"))
            {
                entries.Add(new BacklinkEntry(
                    pageId, "page", pageInfo.Title, pageInfo.Icon,
                    "Wiki page", pageInfo.ModifiedAt));
                wikiSourceCount++;
            }

            if (!reverseIndex.TryGetValue(pageKey, out var pageEntries))
            {
                pageEntries = [];
                reverseIndex[pageKey] = pageEntries;
            }
            var wikiInfo = entityMap[wikiKey];
            if (pageEntries.All(e => e.SourceId != sourceId || e.SourceLinkType != "wiki_source"))
            {
                pageEntries.Add(new BacklinkEntry(
                    sourceId, "wiki_source", wikiInfo.Title, wikiInfo.Icon,
                    "Wiki source", wikiInfo.ModifiedAt));
            }
        }
        _pageWikiSourceMap.Clear();

        if (wikiSourceCount > 0)
            _log.Information("BacklinkService: added {Count} wiki source relationships", wikiSourceCount);

        // --- Phase 7: Tag virtual nodes and edges ---
        var tagNodeCount = 0;
        var tagToItems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entityKey, tags) in entityTags)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (!tagToItems.TryGetValue(tag, out var items)) { items = []; tagToItems[tag] = items; }
                items.Add(entityKey);
            }
        }

        foreach (var (tag, itemKeys) in tagToItems)
        {
            var tagKey = $"tag:{tag}";
            entityMap[tagKey] = ($"#{tag}", "tag", null, "Tag");
            tagNodeCount++;

            foreach (var itemKey in itemKeys)
            {
                if (!forwardLinks.TryGetValue(itemKey, out var fwdSet))
                {
                    fwdSet = [];
                    forwardLinks[itemKey] = fwdSet;
                }
                fwdSet.Add(tagKey);

                if (!reverseIndex.TryGetValue(tagKey, out var entries))
                {
                    entries = [];
                    reverseIndex[tagKey] = entries;
                }

                var parts = itemKey.Split(':', 2);
                if (parts.Length != 2) continue;
                if (!entityMap.TryGetValue(itemKey, out var itemInfo)) continue;

                if (entries.All(e => e.SourceId != parts[1] || e.SourceLinkType != itemInfo.LinkType))
                {
                    entries.Add(new BacklinkEntry(
                        parts[1], itemInfo.LinkType, itemInfo.Title, itemInfo.Icon,
                        null, itemInfo.ModifiedAt));
                }
            }
        }

        if (tagNodeCount > 0)
            _log.Information("BacklinkService: created {Count} tag nodes with edges", tagNodeCount);

        _entityMap = entityMap;
        _forwardLinks = forwardLinks;
        _reverseIndex = reverseIndex;

        var contentCount = entityContents.Count;
        entityContents.Clear();
        entityExplicitLinks.Clear();
        entityTags.Clear();

        _log.Information("BacklinkService: index built — {EntityCount} entities, {ContentCount} with content, " +
            "{LinksFound} wiki-links found, {LinksResolved} resolved, " +
            "{ExplicitFound} explicit links found, {ExplicitResolved} resolved, " +
            "{BacklinkCount} backlink targets, {ForwardCount} forward sources, {TagNodes} tag nodes",
            entityMap.Count, contentCount, totalLinksFound, totalLinksResolved,
            explicitLinksFound, explicitLinksResolved,
            reverseIndex.Count, forwardLinks.Count, tagNodeCount);
    }

    // ========================================================================
    // Legacy entity loading (used for types not covered by providers)
    // ========================================================================

    private async Task<List<(string Id, string Title, string? Content, List<string> ExplicitLinks, DateTimeOffset? ModifiedAt, List<string> Tags)>> LoadEntitiesAsync(
        string entityType, string linkType, CancellationToken ct)
    {
        var items = new List<(string Id, string Title, string? Content, List<string> ExplicitLinks, DateTimeOffset? ModifiedAt, List<string> Tags)>();
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph",
                Action = SdkAction.ReadList,
                EntityType = entityType,
            }, ct);

            if (response.Data == null) return items;

            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                var title = ExtractTitle(item);
                var content = WikiLinkParser.ExtractContentFromEntity(item);
                var explicitLinks = ExtractExplicitLinksFromCustomFields(item);
                var modifiedAt = ExtractModifiedAt(item);
                var tags = ExtractTags(item);

                if (entityType == "page"
                    && item.TryGetProperty("parent_id", out var parentProp)
                    && parentProp.ValueKind == JsonValueKind.String)
                {
                    var parentId = parentProp.GetString();
                    if (!string.IsNullOrEmpty(parentId))
                        lock (_pageParentMap) { _pageParentMap[id] = parentId; }
                }

                if (entityType == "page"
                    && item.TryGetProperty("source_type", out var srcTypeProp)
                    && srcTypeProp.GetString() == "github_wiki"
                    && item.TryGetProperty("source_id", out var srcIdProp)
                    && srcIdProp.ValueKind == JsonValueKind.String)
                {
                    var sourceId = srcIdProp.GetString();
                    if (!string.IsNullOrEmpty(sourceId))
                        lock (_pageWikiSourceMap) { _pageWikiSourceMap[id] = sourceId; }
                }

                items.Add((id, title, content, explicitLinks, modifiedAt, tags));
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "BacklinkService: failed to load {EntityType}", entityType);
        }
        return items;
    }

    private static string ExtractTitle(JsonElement item)
    {
        if (item.TryGetProperty("display_name", out var dn) && !string.IsNullOrEmpty(dn.GetString()))
            return dn.GetString()!;

        var first = item.TryGetProperty("first_name", out var fn) ? fn.GetString() ?? "" : "";
        var last = item.TryGetProperty("last_name", out var ln) ? ln.GetString() ?? "" : "";
        var combined = $"{first} {last}".Trim();
        if (!string.IsNullOrEmpty(combined)) return combined;

        foreach (var field in new[] { "title", "name", "full_name", "subject", "label",
                                      "service_name", "payee", "summary" })
        {
            if (item.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(prop.GetString()))
                return prop.GetString()!;
        }

        if (item.TryGetProperty("description", out var desc) && !string.IsNullOrEmpty(desc.GetString()))
        {
            var d = desc.GetString()!;
            return d.Length > 40 ? d[..37] + "..." : d;
        }

        return "";
    }

    private static DateTimeOffset? ExtractModifiedAt(JsonElement item)
    {
        foreach (var field in new[] { "modified_at", "updated_at", "created_at" })
        {
            if (item.TryGetProperty(field, out var dateProp) && dateProp.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(dateProp.GetString(), out var date))
                    return date;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts explicit links from custom_fields (linked_items and task_links arrays),
    /// top-level linked_items (sticky notes), and foreign-key fields (project_id).
    /// </summary>
    private static List<string> ExtractExplicitLinksFromCustomFields(JsonElement item)
    {
        var links = new List<string>();

        if (item.TryGetProperty("project_id", out var projectIdProp) &&
            projectIdProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(projectIdProp.GetString()))
        {
            links.Add($"project:{projectIdProp.GetString()}");
        }

        if (item.TryGetProperty("linked_items", out var topLinkedItems) &&
            topLinkedItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var linkElement in topLinkedItems.EnumerateArray())
            {
                if (linkElement.ValueKind != JsonValueKind.String) continue;
                var link = linkElement.GetString();
                if (string.IsNullOrEmpty(link)) continue;

                var match = WikiLinkParser.PrivstackUrlPattern.Match(link);
                if (match.Success)
                    links.Add($"{match.Groups[1].Value}:{match.Groups[2].Value}");
                else if (link.Contains(':'))
                    links.Add(link);
            }
        }

        if (item.TryGetProperty("custom_fields", out var customFields) &&
            customFields.ValueKind == JsonValueKind.Object)
        {
            if (customFields.TryGetProperty("linked_items", out var linkedItems) &&
                linkedItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var linkElement in linkedItems.EnumerateArray())
                {
                    if (linkElement.ValueKind != JsonValueKind.String) continue;
                    var link = linkElement.GetString();
                    if (!string.IsNullOrEmpty(link) && link.Contains(':'))
                        links.Add(link);
                }
            }

            if (customFields.TryGetProperty("task_links", out var taskLinks) &&
                taskLinks.ValueKind == JsonValueKind.Array)
            {
                foreach (var linkObj in taskLinks.EnumerateArray())
                {
                    if (linkObj.ValueKind != JsonValueKind.Object) continue;
                    var linkType = linkObj.TryGetProperty("link_type", out var ltProp) ? ltProp.GetString() : null;
                    var itemId = linkObj.TryGetProperty("item_id", out var idProp) ? idProp.GetString() : null;
                    if (!string.IsNullOrEmpty(linkType) && !string.IsNullOrEmpty(itemId))
                        links.Add($"{linkType}:{itemId}");
                }
            }
        }

        return links;
    }

    private static List<string> ExtractTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tagsProp)) return [];
        if (tagsProp.ValueKind != JsonValueKind.Array) return [];
        return tagsProp.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString() ?? "")
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
    }

    private static void AddForwardAndReverseLinks(
        string sourceKey, string targetKey,
        Dictionary<string, (string Title, string LinkType, DateTimeOffset? ModifiedAt, string? Icon)> entityMap,
        Dictionary<string, string> entityContents,
        Dictionary<string, List<string>> entityExplicitLinks,
        bool isExplicit)
    {
        // Structural edges from providers are tracked as explicit links for the index
        if (!entityExplicitLinks.TryGetValue(sourceKey, out var links))
        {
            links = [];
            entityExplicitLinks[sourceKey] = links;
        }
        links.Add(targetKey);
    }
}
