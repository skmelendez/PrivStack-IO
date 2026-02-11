using System.Text.Json;
using System.Text.RegularExpressions;
using PrivStack.Sdk;
using PrivStack.Sdk.Json;
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
/// Computes backlinks by loading all entities, parsing wiki-links from content fields,
/// and building a reverse index. Caches the result until explicitly invalidated.
/// </summary>
public sealed class BacklinkService
{
    private static readonly ILogger _log = Log.ForContext<BacklinkService>();
    private static readonly JsonSerializerOptions JsonOptions = SdkJsonOptions.Default;

    // Wiki-link format: [[type:id|Title]]
    private static readonly Regex WikiLinkPattern = new(
        @"\[\[([a-z]+(?:-[a-z]+)*):([^|]+)\|[^\]]+\]\]",
        RegexOptions.Compiled);

    // RTE markdown link format: [Title](privstack://type/id)
    private static readonly Regex PrivstackUrlPattern = new(
        @"privstack://([a-z]+(?:-[a-z]+)*)/([a-f0-9-]+)",
        RegexOptions.Compiled);

    private readonly IPrivStackSdk _sdk;

    // Cached reverse index: "linkType:itemId" → list of backlink entries
    private Dictionary<string, List<BacklinkEntry>>? _reverseIndex;

    // Forward links for local graph: "linkType:itemId" → set of "linkType:itemId" targets
    private Dictionary<string, HashSet<string>>? _forwardLinks;

    // All known entities (for graph node titles): "linkType:itemId" → (title, linkType, modifiedAt, icon)
    private Dictionary<string, (string Title, string LinkType, DateTimeOffset? ModifiedAt, string? Icon)>? _entityMap;

    private readonly SemaphoreSlim _buildLock = new(1, 1);

    // Temporary storage for page parent_id during index build
    private readonly Dictionary<string, string> _pageParentMap = new();

    // Entity types to scan — derived from the shared EntityTypeMap.
    private static readonly (string EntityType, string LinkType, string Icon)[] EntityTypes =
        EntityTypeMap.All.Select(e => (e.EntityType, e.LinkType, e.Icon)).ToArray();

    public BacklinkService(IPrivStackSdk sdk) => _sdk = sdk;

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

        // BFS traversal up to requested depth
        for (var hop = 1; hop <= depth; hop++)
        {
            var nextFrontier = new HashSet<string>();
            foreach (var key in frontier)
            {
                // Forward links
                if (_forwardLinks != null && _forwardLinks.TryGetValue(key, out var fwd))
                    foreach (var k in fwd)
                        if (!depthMap.ContainsKey(k))
                        {
                            depthMap[k] = hop;
                            nextFrontier.Add(k);
                        }

                // Backlinks
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

        // Apply link type filter (center node always kept)
        var neighborKeys = new HashSet<string>();
        if (_entityMap != null)
        {
            foreach (var key in depthMap.Keys)
            {
                if (key == centerKey)
                {
                    neighborKeys.Add(key);
                    continue;
                }
                if (!_entityMap.TryGetValue(key, out var info)) continue;
                if (allowedLinkTypes != null && !allowedLinkTypes.Contains(info.LinkType)) continue;
                neighborKeys.Add(key);
            }
        }

        // Build JSON nodes and edges for NeuronGraphControl
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

        // All edges between nodes in the final set
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
    /// Ensures the index is built first.
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
            if (_reverseIndex != null) return; // double-check after lock
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
        var entityContents = new Dictionary<string, string>(); // key → concatenated content
        var entityExplicitLinks = new Dictionary<string, List<string>>(); // key → list of "type:id" explicit links

        // Load all entity types in parallel
        var tasks = EntityTypes.Select(et => LoadEntitiesAsync(et.EntityType, et.LinkType, ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
        {
            var linkType = EntityTypes[i].LinkType;
            var entityType = EntityTypes[i].EntityType;
            var icon = EntityTypes[i].Icon;
            var withContent = 0;
            foreach (var (id, title, content, explicitLinks, modifiedAt) in results[i])
            {
                var key = $"{linkType}:{id}";
                entityMap[key] = (title, linkType, modifiedAt, icon);
                if (!string.IsNullOrEmpty(content))
                {
                    entityContents[key] = content;
                    withContent++;
                }
                if (explicitLinks.Count > 0)
                {
                    entityExplicitLinks[key] = explicitLinks;
                }
            }
            _log.Information("BacklinkService: loaded {Count} {EntityType} entities ({WithContent} with content)",
                results[i].Count, entityType, withContent);
        }

        // For entities without content from list view, load individually
        var entitiesToFetch = entityMap.Keys.Where(k => !entityContents.ContainsKey(k)).ToList();
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

                    // Reverse-map linkType back to SDK entity type
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

                    var content = ExtractContentFromEntity(response.Data);
                    var explicitLinks = ExtractExplicitLinksFromCustomFields(response.Data);

                    if (!string.IsNullOrEmpty(content))
                    {
                        lock (entityContents)
                        {
                            entityContents[key] = content;
                        }
                    }
                    if (explicitLinks.Count > 0)
                    {
                        lock (entityExplicitLinks)
                        {
                            entityExplicitLinks[key] = explicitLinks;
                        }
                    }
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

        // Parse links from content to build forward + reverse indices
        // Supports both [[type:id|title]] wiki-links and privstack://type/id URLs
        var reverseIndex = new Dictionary<string, List<BacklinkEntry>>();
        var forwardLinks = new Dictionary<string, HashSet<string>>();
        var totalLinksFound = 0;
        var totalLinksResolved = 0;

        foreach (var (sourceKey, content) in entityContents)
        {
            if (!entityMap.TryGetValue(sourceKey, out var sourceInfo)) continue;

            // Collect matches from both patterns
            var allMatches = new List<Match>();
            allMatches.AddRange(WikiLinkPattern.Matches(content).Cast<Match>());
            allMatches.AddRange(PrivstackUrlPattern.Matches(content).Cast<Match>());

            foreach (var match in allMatches)
            {
                ct.ThrowIfCancellationRequested();
                var targetLinkType = match.Groups[1].Value;
                var targetId = match.Groups[2].Value;
                var targetKey = $"{targetLinkType}:{targetId}";
                totalLinksFound++;

                if (sourceKey == targetKey) continue;
                if (!entityMap.ContainsKey(targetKey))
                {
                    _log.Debug("BacklinkService: unresolved link {SourceKey} -> {TargetKey} (target not in entity map)",
                        sourceKey, targetKey);
                    continue;
                }
                totalLinksResolved++;

                // Forward link
                if (!forwardLinks.TryGetValue(sourceKey, out var fwdSet))
                {
                    fwdSet = [];
                    forwardLinks[sourceKey] = fwdSet;
                }
                fwdSet.Add(targetKey);

                // Reverse link (backlink)
                if (!reverseIndex.TryGetValue(targetKey, out var entries))
                {
                    entries = [];
                    reverseIndex[targetKey] = entries;
                }

                // Avoid duplicate backlinks from same source
                var parts = sourceKey.Split(':', 2);
                var sourceId = parts.Length == 2 ? parts[1] : sourceKey;
                if (entries.Any(e => e.SourceId == sourceId && e.SourceLinkType == sourceInfo.LinkType))
                    continue;

                // Extract context snippet (surrounding text)
                var snippet = ExtractSnippet(content, match.Index, 60);

                entries.Add(new BacklinkEntry(
                    sourceId,
                    sourceInfo.LinkType,
                    sourceInfo.Title,
                    sourceInfo.Icon,
                    snippet,
                    sourceInfo.ModifiedAt));
            }
        }

        // Process explicit links from custom_fields (Task linked_items, task_links)
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

                // Forward link
                if (!forwardLinks.TryGetValue(sourceKey, out var fwdSet))
                {
                    fwdSet = [];
                    forwardLinks[sourceKey] = fwdSet;
                }
                fwdSet.Add(targetKey);

                // Reverse link (backlink)
                if (!reverseIndex.TryGetValue(targetKey, out var entries))
                {
                    entries = [];
                    reverseIndex[targetKey] = entries;
                }

                // Avoid duplicate backlinks from same source
                var parts = sourceKey.Split(':', 2);
                var sourceId = parts.Length == 2 ? parts[1] : sourceKey;
                if (entries.Any(e => e.SourceId == sourceId && e.SourceLinkType == sourceInfo.LinkType))
                    continue;

                entries.Add(new BacklinkEntry(
                    sourceId,
                    sourceInfo.LinkType,
                    sourceInfo.Title,
                    sourceInfo.Icon,
                    null, // No snippet for explicit links
                    sourceInfo.ModifiedAt));
            }
        }

        // Add parent-child relationships from page hierarchy
        var parentChildCount = 0;
        foreach (var (key, info) in entityMap)
        {
            if (info.LinkType != "page") continue;
            var parts = key.Split(':', 2);
            if (parts.Length != 2) continue;

            // Check if this page was loaded with parent_id info
            if (!_pageParentMap.TryGetValue(parts[1], out var parentId)) continue;
            if (string.IsNullOrEmpty(parentId)) continue;

            var parentKey = $"page:{parentId}";
            if (!entityMap.ContainsKey(parentKey)) continue;

            // Forward link: child → parent
            if (!forwardLinks.TryGetValue(key, out var fwdSet))
            {
                fwdSet = [];
                forwardLinks[key] = fwdSet;
            }
            fwdSet.Add(parentKey);

            // Reverse link: parent has backlink from child
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

            // Also add reverse: child sees parent as backlink
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

        _entityMap = entityMap;
        _forwardLinks = forwardLinks;
        _reverseIndex = reverseIndex;

        var contentCount = entityContents.Count;

        // Release temporary content strings — they can be large and are no longer needed
        // once the link indices have been built.
        entityContents.Clear();
        entityExplicitLinks.Clear();

        _log.Information("BacklinkService: index built — {EntityCount} entities, {ContentCount} with content, " +
            "{LinksFound} wiki-links found, {LinksResolved} resolved, " +
            "{ExplicitFound} explicit links found, {ExplicitResolved} resolved, " +
            "{BacklinkCount} backlink targets, {ForwardCount} forward sources",
            entityMap.Count, contentCount, totalLinksFound, totalLinksResolved,
            explicitLinksFound, explicitLinksResolved,
            reverseIndex.Count, forwardLinks.Count);
    }

    private async Task<List<(string Id, string Title, string? Content, List<string> ExplicitLinks, DateTimeOffset? ModifiedAt)>> LoadEntitiesAsync(
        string entityType, string linkType, CancellationToken ct)
    {
        var items = new List<(string Id, string Title, string? Content, List<string> ExplicitLinks, DateTimeOffset? ModifiedAt)>();
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

                // Try to extract content from various fields
                var content = ExtractContentFromEntity(item);

                // Extract explicit links from custom_fields (Tasks linked_items, task_links)
                var explicitLinks = ExtractExplicitLinksFromCustomFields(item);

                // Extract modified_at timestamp
                var modifiedAt = ExtractModifiedAt(item);

                // Capture parent_id for page entities (used for parent-child relationships)
                if (entityType == "page"
                    && item.TryGetProperty("parent_id", out var parentProp)
                    && parentProp.ValueKind == JsonValueKind.String)
                {
                    var parentId = parentProp.GetString();
                    if (!string.IsNullOrEmpty(parentId))
                    {
                        lock (_pageParentMap)
                        {
                            _pageParentMap[id] = parentId;
                        }
                    }
                }

                items.Add((id, title, content, explicitLinks, modifiedAt));
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
        // Entity-specific fields (contacts, credentials, transactions, events)
        if (item.TryGetProperty("display_name", out var dn) && !string.IsNullOrEmpty(dn.GetString()))
            return dn.GetString()!;

        var first = item.TryGetProperty("first_name", out var fn) ? fn.GetString() ?? "" : "";
        var last = item.TryGetProperty("last_name", out var ln) ? ln.GetString() ?? "" : "";
        var combined = $"{first} {last}".Trim();
        if (!string.IsNullOrEmpty(combined)) return combined;

        // Common fields — covers pages, tasks, projects, deals, snippets, journal, etc.
        foreach (var field in new[] { "title", "name", "full_name", "subject", "label",
                                      "service_name", "payee", "summary" })
        {
            if (item.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(prop.GetString()))
                return prop.GetString()!;
        }

        // Truncated description as last resort
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

    private static string? ExtractSnippet(string content, int matchIndex, int radius)
    {
        var start = Math.Max(0, matchIndex - radius);
        var end = Math.Min(content.Length, matchIndex + radius);

        // Extend to word boundaries
        while (start > 0 && content[start] != ' ' && content[start] != '\n') start--;
        while (end < content.Length && content[end] != ' ' && content[end] != '\n') end++;

        var snippet = content[start..end].Trim();
        if (start > 0) snippet = "..." + snippet;
        if (end < content.Length) snippet += "...";

        // Remove the link markup for cleaner display
        snippet = WikiLinkPattern.Replace(snippet, m =>
        {
            var display = m.Value;
            var pipeIdx = display.IndexOf('|');
            return pipeIdx >= 0 ? display[(pipeIdx + 1)..^2] : display;
        });

        return string.IsNullOrWhiteSpace(snippet) ? null : snippet;
    }

    /// <summary>
    /// Extracts explicit links from custom_fields (linked_items and task_links arrays),
    /// top-level linked_items (sticky notes), and foreign-key fields (project_id).
    /// Returns a list of "type:id" strings.
    /// </summary>
    private static List<string> ExtractExplicitLinksFromCustomFields(JsonElement item)
    {
        var links = new List<string>();

        // Top-level project_id (tasks → projects)
        if (item.TryGetProperty("project_id", out var projectIdProp) &&
            projectIdProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(projectIdProp.GetString()))
        {
            links.Add($"project:{projectIdProp.GetString()}");
        }

        // Top-level linked_items (e.g. sticky notes store privstack://type/id URIs)
        if (item.TryGetProperty("linked_items", out var topLinkedItems) &&
            topLinkedItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var linkElement in topLinkedItems.EnumerateArray())
            {
                if (linkElement.ValueKind != JsonValueKind.String) continue;
                var link = linkElement.GetString();
                if (string.IsNullOrEmpty(link)) continue;

                // Convert privstack://type/id URI to type:id format
                var match = PrivstackUrlPattern.Match(link);
                if (match.Success)
                {
                    links.Add($"{match.Groups[1].Value}:{match.Groups[2].Value}");
                }
                else if (link.Contains(':'))
                {
                    links.Add(link);
                }
            }
        }

        if (item.TryGetProperty("custom_fields", out var customFields) &&
            customFields.ValueKind == JsonValueKind.Object)
        {
            // Parse linked_items array: ["type:id", "type:id", ...]
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

            // Parse task_links array: [{ "link_type": "...", "item_id": "...", ... }, ...]
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

    /// <summary>
    /// Extracts all text content from an entity JSON element.
    /// Handles both flat string fields and structured block-based content (Notes pages).
    /// </summary>
    private static string? ExtractContentFromEntity(JsonElement item)
    {
        var parts = new List<string>();

        foreach (var field in new[] { "content", "description", "notes", "body" })
        {
            if (!item.TryGetProperty(field, out var prop)) continue;

            if (prop.ValueKind == JsonValueKind.String)
            {
                var text = prop.GetString();
                if (!string.IsNullOrEmpty(text))
                    parts.Add(text);
            }
            else if (prop.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                // Structured content (e.g. Notes block-based documents)
                // Recursively extract all string values from the JSON tree
                ExtractStringsFromJson(prop, parts);
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    /// <summary>
    /// Recursively extracts all string values from a JSON element tree.
    /// This captures text from block-based content structures (paragraphs, headings,
    /// list items, etc.) as well as privstack:// URLs embedded in the content.
    /// </summary>
    private static void ExtractStringsFromJson(JsonElement element, List<string> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrEmpty(text))
                    results.Add(text);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    ExtractStringsFromJson(property.Value, results);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractStringsFromJson(item, results);
                break;
        }
    }
}
