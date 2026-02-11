using System.Text.Json;
using System.Text.Json.Serialization;
using PrivStack.Sdk.Json;
using System.Text.RegularExpressions;
using PrivStack.Desktop.Plugins.Graph.Models;
using PrivStack.Sdk;
using Serilog;

namespace PrivStack.Desktop.Plugins.Graph.Services;

/// <summary>
/// Event data for graph changes.
/// </summary>
public sealed record GraphDataChangedEventArgs
{
    public required GraphChangeType ChangeType { get; init; }
    public string[] AffectedNoteIds { get; init; } = [];
}

public enum GraphChangeType { ItemAdded, ItemDeleted, LinkAdded, LinkRemoved, TagsChanged, FullRefresh }

/// <summary>
/// Aggregates data from all entity types via IPrivStackSdk to build the knowledge graph.
/// Uses generic ReadList to fetch entities without knowing their internal schema beyond
/// common fields (id, title, tags, content, modified_at).
/// </summary>
public sealed class GraphDataService
{
    private static readonly ILogger _log = Log.ForContext<GraphDataService>();
    private readonly IPrivStackSdk _sdk;
    private readonly Random _random = new();

    // Wiki-link format: [[type:id|Title]]
    private static readonly Regex WikiLinkPattern = new(
        @"\[\[([a-z]+(?:-[a-z]+)*):([^|]+)\|[^\]]+\]\]",
        RegexOptions.Compiled);

    // RTE markdown link format: [Title](privstack://type/id)
    private static readonly Regex PrivstackUrlPattern = new(
        @"privstack://([a-z]+(?:-[a-z]+)*)/([a-f0-9-]+)",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = SdkJsonOptions.Default;

#pragma warning disable CS0067 // Event is reserved for future graph change notifications
    public event EventHandler<GraphDataChangedEventArgs>? GraphDataChanged;
#pragma warning restore CS0067

    public GraphDataService(IPrivStackSdk sdk) => _sdk = sdk;

    public async Task<GraphData> LoadGlobalGraphAsync()
    {
        var graphData = new GraphData();
        var edgeSet = new HashSet<(string, string)>();

        // Load all entity types in parallel
        // LinkType MUST match ILinkableItemProvider.LinkType so wiki-link targets resolve correctly.
        var pageTask = LoadEntitiesAsync("page", "page", NodeType.Note);
        var taskTask = LoadEntitiesAsync("task", "task", NodeType.Task);
        var projectTask = LoadEntitiesAsync("project", "project", NodeType.Project);
        var contactTask = LoadEntitiesAsync("contact", "contact", NodeType.Contact);
        var journalTask = LoadEntitiesAsync("journal_entry", "journal", NodeType.Journal);
        var eventTask = LoadEntitiesAsync("event", "event", NodeType.Event);
        var dealTask = LoadEntitiesAsync("deal", "deal", NodeType.Deal);
        var transactionTask = LoadEntitiesAsync("transaction", "transaction", NodeType.Transaction);
        var snippetTask = LoadEntitiesAsync("snippet", "snippet", NodeType.Snippet);
        var rssTask = LoadEntitiesAsync("rss_article", "rss_article", NodeType.RssArticle);
        var credentialTask = LoadEntitiesAsync("credential", "credential", NodeType.Credential);
        var fileTask = LoadEntitiesAsync("vault_file", "file", NodeType.File);
        var companyTask = LoadEntitiesAsync("company", "company", NodeType.Company);
        var groupTask = LoadEntitiesAsync("contact_group", "contact_group", NodeType.ContactGroup);

        await Task.WhenAll(pageTask, taskTask, projectTask, contactTask, journalTask, eventTask,
            dealTask, transactionTask, snippetTask, rssTask, credentialTask, fileTask,
            companyTask, groupTask);

        // Add all nodes
        foreach (var nodes in new[] { pageTask.Result, taskTask.Result, projectTask.Result, contactTask.Result,
            journalTask.Result, eventTask.Result, dealTask.Result, transactionTask.Result,
            snippetTask.Result, rssTask.Result, credentialTask.Result, fileTask.Result,
            companyTask.Result, groupTask.Result })
            foreach (var node in nodes)
                graphData.Nodes[node.Id] = node;

        _log.Information("GraphDataService: loaded {NodeCount} nodes before link parsing", graphData.Nodes.Count);

        // Create parent-child edges from page hierarchy
        await CreateParentChildEdgesAsync(graphData, edgeSet);

        // Create structural membership edges (company, group, project)
        await CreateCompanyMembershipEdgesAsync(graphData, edgeSet);
        await CreateGroupMembershipEdgesAsync(graphData, edgeSet);
        await CreateProjectMembershipEdgesAsync(graphData, edgeSet);

        // Parse links from content fields
        await ParseLinksAsync(graphData, edgeSet);

        _log.Information("GraphDataService: {EdgeCount} wiki-link edges after parsing", edgeSet.Count);

        // Create tag nodes and edges
        CreateTagNodesAndEdges(graphData, edgeSet);

        // Calculate link counts
        foreach (var edge in graphData.Edges)
        {
            var isStructural = edge.Type is EdgeType.TagRelation or EdgeType.ProjectMembership or EdgeType.ParentChild or EdgeType.GroupMembership or EdgeType.CompanyMembership;
            if (graphData.Nodes.TryGetValue(edge.SourceId, out var source))
            {
                source.LinkCount++;
                if (!isStructural) source.WikiLinkCount++;
            }
            if (graphData.Nodes.TryGetValue(edge.TargetId, out var target))
            {
                target.LinkCount++;
                if (!isStructural) target.WikiLinkCount++;
            }
        }

        graphData.BuildAdjacencyList();
        return graphData;
    }

    public async Task<GraphData> LoadLocalGraphAsync(string itemId, int depth = 1)
    {
        var globalGraph = await LoadGlobalGraphAsync();
        if (!itemId.Contains(':') || !globalGraph.Nodes.ContainsKey(itemId))
            return new GraphData();
        return globalGraph.GetLocalGraph(itemId, depth);
    }

    private async Task<List<GraphNode>> LoadEntitiesAsync(string entityType, string linkType, NodeType nodeType)
    {
        var nodes = new List<GraphNode>();
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph",
                Action = SdkAction.ReadList,
                EntityType = entityType,
            });

            if (response.Data == null) return nodes;

            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var title = ExtractTitle(item, entityType);
                var tags = ExtractTags(item);
                var modifiedAt = ExtractDate(item);

                if (string.IsNullOrEmpty(id)) continue;

                nodes.Add(new GraphNode
                {
                    Id = $"{linkType}:{id}",
                    Title = title,
                    NodeType = nodeType,
                    LinkType = linkType,
                    Tags = tags,
                    ModifiedAt = modifiedAt,
                    X = _random.NextDouble() * 1000 - 500,
                    Y = _random.NextDouble() * 1000 - 500
                });
            }
        }
        catch { /* Skip entity types that fail to load */ }
        return nodes;
    }

    private static string ExtractTitle(JsonElement item, string entityType)
    {
        // Entity-specific fields first
        if (entityType is "contact")
        {
            if (item.TryGetProperty("display_name", out var dn) && !string.IsNullOrEmpty(dn.GetString()))
                return dn.GetString()!;
            var first = item.TryGetProperty("first_name", out var fn) ? fn.GetString() ?? "" : "";
            var last = item.TryGetProperty("last_name", out var ln) ? ln.GetString() ?? "" : "";
            var combined = $"{first} {last}".Trim();
            if (!string.IsNullOrEmpty(combined)) return combined;
        }

        if (entityType is "credential")
        {
            if (item.TryGetProperty("service_name", out var sn) && !string.IsNullOrEmpty(sn.GetString()))
                return sn.GetString()!;
            if (item.TryGetProperty("website", out var ws) && !string.IsNullOrEmpty(ws.GetString()))
                return ws.GetString()!;
            if (item.TryGetProperty("username", out var un) && !string.IsNullOrEmpty(un.GetString()))
                return un.GetString()!;
        }

        if (entityType is "transaction")
        {
            if (item.TryGetProperty("payee", out var payee) && !string.IsNullOrEmpty(payee.GetString()))
                return payee.GetString()!;
            if (item.TryGetProperty("memo", out var memo) && !string.IsNullOrEmpty(memo.GetString()))
                return memo.GetString()!;
        }

        if (entityType is "event")
        {
            if (item.TryGetProperty("summary", out var sum) && !string.IsNullOrEmpty(sum.GetString()))
                return sum.GetString()!;
        }

        if (entityType is "company")
        {
            if (item.TryGetProperty("name", out var cn) && !string.IsNullOrEmpty(cn.GetString()))
                return cn.GetString()!;
        }

        if (entityType is "contact_group")
        {
            if (item.TryGetProperty("name", out var gn) && !string.IsNullOrEmpty(gn.GetString()))
                return gn.GetString()!;
        }

        // Common fields — covers pages, tasks, projects, deals, snippets, journal, etc.
        foreach (var field in new[] { "title", "name", "display_name", "full_name", "subject", "label" })
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

        return entityType;
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

    private static DateTimeOffset ExtractDate(JsonElement item)
    {
        foreach (var field in new[] { "modified_at", "updated_at", "created_at", "start" })
        {
            if (item.TryGetProperty(field, out var dateProp) && dateProp.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(dateProp.GetString(), out var date)) return date;
            }
        }
        return DateTimeOffset.MinValue;
    }

    private async Task ParseLinksAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        var nodesWithContent = 0;
        var totalLinksFound = 0;

        // For each node, try to get content and parse links
        // This is best-effort - not all entity types have content fields
        foreach (var (nodeId, node) in graphData.Nodes.ToList())
        {
            try
            {
                var parts = nodeId.Split(':', 2);
                if (parts.Length != 2) continue;

                var entityType = parts[0] switch
                {
                    "journal" => "journal_entry",
                    "file" => "vault_file",
                    _ => parts[0]
                };

                var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
                {
                    PluginId = "privstack.graph",
                    Action = SdkAction.Read,
                    EntityType = entityType,
                    EntityId = parts[1],
                });

                if (response.Data.ValueKind == JsonValueKind.Undefined) continue;

                // Extract all text content (handles flat strings and structured block documents)
                var allContent = ExtractContentFromEntity(response.Data);
                if (!string.IsNullOrEmpty(allContent))
                {
                    nodesWithContent++;
                    var beforeCount = edgeSet.Count;
                    ParseLinksFromContent(nodeId, allContent, graphData, edgeSet);
                    var newEdges = edgeSet.Count - beforeCount;
                    if (newEdges > 0)
                        _log.Information("GraphDataService: {NodeId} -> {NewEdges} new edges from content", nodeId, newEdges);
                    totalLinksFound += WikiLinkPattern.Matches(allContent).Count
                                    + PrivstackUrlPattern.Matches(allContent).Count;
                }

                // Also parse explicit links from custom_fields (Task linked_items, task_links)
                var explicitBefore = edgeSet.Count;
                ParseExplicitLinksFromCustomFields(nodeId, response.Data, graphData, edgeSet);
                var explicitNew = edgeSet.Count - explicitBefore;
                if (explicitNew > 0)
                    _log.Information("GraphDataService: {NodeId} -> {NewEdges} new edges from explicit links", nodeId, explicitNew);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "GraphDataService: failed to load content for {NodeId}", nodeId);
            }
        }

        _log.Information("GraphDataService: parsed {NodesWithContent}/{TotalNodes} nodes with content, " +
            "{TotalLinks} wiki-link matches found", nodesWithContent, graphData.Nodes.Count, totalLinksFound);
    }

    private void ParseLinksFromContent(string sourceKey, string content, GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        // Collect matches from both link formats
        var allMatches = new List<Match>();
        allMatches.AddRange(WikiLinkPattern.Matches(content).Cast<Match>());
        allMatches.AddRange(PrivstackUrlPattern.Matches(content).Cast<Match>());

        foreach (var match in allMatches)
        {
            var linkType = match.Groups[1].Value;
            var linkId = match.Groups[2].Value;
            var targetKey = $"{linkType}:{linkId}";

            if (sourceKey == targetKey) continue;
            if (!graphData.Nodes.ContainsKey(targetKey))
            {
                _log.Debug("GraphDataService: unresolved link {Source} -> {Target} (match: {Match})",
                    sourceKey, targetKey, match.Value);
                continue;
            }
            if (edgeSet.Add((sourceKey, targetKey)))
            {
                graphData.Edges.Add(new GraphEdge { SourceId = sourceKey, TargetId = targetKey, Type = EdgeType.WikiLink });
            }
        }
    }

    /// <summary>
    /// Extracts explicit links from custom_fields (linked_items and task_links arrays)
    /// and top-level foreign-key fields (project_id).
    /// </summary>
    private void ParseExplicitLinksFromCustomFields(string sourceKey, JsonElement item, GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        // Top-level project_id (tasks → projects)
        if (item.TryGetProperty("project_id", out var projectIdProp) &&
            projectIdProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(projectIdProp.GetString()))
        {
            var projectKey = $"project:{projectIdProp.GetString()}";
            if (sourceKey != projectKey && graphData.Nodes.ContainsKey(projectKey))
            {
                if (edgeSet.Add((sourceKey, projectKey)))
                {
                    graphData.Edges.Add(new GraphEdge
                    {
                        SourceId = sourceKey,
                        TargetId = projectKey,
                        Type = EdgeType.ProjectMembership,
                    });
                }
            }
        }

        if (!item.TryGetProperty("custom_fields", out var customFields))
            return;

        if (customFields.ValueKind != JsonValueKind.Object)
            return;

        // Parse linked_items array: ["type:id", "type:id", ...]
        if (customFields.TryGetProperty("linked_items", out var linkedItems) &&
            linkedItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var linkElement in linkedItems.EnumerateArray())
            {
                if (linkElement.ValueKind != JsonValueKind.String) continue;
                var link = linkElement.GetString();
                if (string.IsNullOrEmpty(link)) continue;

                var parts = link.Split(':', 2);
                if (parts.Length != 2) continue;

                var targetKey = $"{parts[0]}:{parts[1]}";
                if (sourceKey == targetKey) continue;
                if (!graphData.Nodes.ContainsKey(targetKey))
                {
                    _log.Debug("GraphDataService: unresolved explicit link {Source} -> {Target}",
                        sourceKey, targetKey);
                    continue;
                }
                if (edgeSet.Add((sourceKey, targetKey)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = sourceKey, TargetId = targetKey, Type = EdgeType.WikiLink });
                }
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

                if (string.IsNullOrEmpty(linkType) || string.IsNullOrEmpty(itemId)) continue;

                var targetKey = $"{linkType}:{itemId}";
                if (sourceKey == targetKey) continue;
                if (!graphData.Nodes.ContainsKey(targetKey))
                {
                    _log.Debug("GraphDataService: unresolved task_link {Source} -> {Target}",
                        sourceKey, targetKey);
                    continue;
                }
                if (edgeSet.Add((sourceKey, targetKey)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = sourceKey, TargetId = targetKey, Type = EdgeType.WikiLink });
                }
            }
        }
    }

    /// <summary>
    /// Creates edges between contacts and their companies via company_id field.
    /// </summary>
    private async Task CreateCompanyMembershipEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph",
                Action = SdkAction.ReadList,
                EntityType = "contact",
            });

            if (response.Data == null) return;

            var count = 0;
            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                var companyId = item.TryGetProperty("company_id", out var cProp) ? cProp.GetString() : null;
                if (string.IsNullOrEmpty(companyId)) continue;

                var contactKey = $"contact:{id}";
                var companyKey = $"company:{companyId}";

                if (!graphData.Nodes.ContainsKey(contactKey) || !graphData.Nodes.ContainsKey(companyKey)) continue;

                if (edgeSet.Add((contactKey, companyKey)))
                {
                    graphData.Edges.Add(new GraphEdge
                    {
                        SourceId = contactKey,
                        TargetId = companyKey,
                        Type = EdgeType.CompanyMembership,
                    });
                    count++;
                }
            }

            if (count > 0)
                _log.Information("GraphDataService: created {Count} company membership edges", count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "GraphDataService: failed to load contact company relationships");
        }
    }

    /// <summary>
    /// Creates edges between contact groups and their member contacts via contact_ids field.
    /// </summary>
    private async Task CreateGroupMembershipEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph",
                Action = SdkAction.ReadList,
                EntityType = "contact_group",
            });

            if (response.Data == null) return;

            var count = 0;
            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                if (!item.TryGetProperty("contact_ids", out var cidsProp) || cidsProp.ValueKind != JsonValueKind.Array)
                    continue;

                var groupKey = $"contact_group:{id}";
                if (!graphData.Nodes.ContainsKey(groupKey)) continue;

                foreach (var cidEl in cidsProp.EnumerateArray())
                {
                    var contactId = cidEl.GetString();
                    if (string.IsNullOrEmpty(contactId)) continue;

                    var contactKey = $"contact:{contactId}";
                    if (!graphData.Nodes.ContainsKey(contactKey)) continue;

                    if (edgeSet.Add((contactKey, groupKey)))
                    {
                        graphData.Edges.Add(new GraphEdge
                        {
                            SourceId = contactKey,
                            TargetId = groupKey,
                            Type = EdgeType.GroupMembership,
                        });
                        count++;
                    }
                }
            }

            if (count > 0)
                _log.Information("GraphDataService: created {Count} group membership edges", count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "GraphDataService: failed to load contact group relationships");
        }
    }

    /// <summary>
    /// Creates edges between tasks and their projects via project_id field.
    /// </summary>
    private async Task CreateProjectMembershipEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph",
                Action = SdkAction.ReadList,
                EntityType = "task",
            });

            if (response.Data == null) return;

            var count = 0;
            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                var projectId = item.TryGetProperty("project_id", out var pProp) ? pProp.GetString() : null;
                if (string.IsNullOrEmpty(projectId)) continue;

                var taskKey = $"task:{id}";
                var projectKey = $"project:{projectId}";

                if (!graphData.Nodes.ContainsKey(taskKey) || !graphData.Nodes.ContainsKey(projectKey)) continue;

                if (edgeSet.Add((taskKey, projectKey)))
                {
                    graphData.Edges.Add(new GraphEdge
                    {
                        SourceId = taskKey,
                        TargetId = projectKey,
                        Type = EdgeType.ProjectMembership,
                    });
                    count++;
                }
            }

            if (count > 0)
                _log.Information("GraphDataService: created {Count} project membership edges", count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "GraphDataService: failed to load task project relationships");
        }
    }

    private void CreateTagNodesAndEdges(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        var tagToItems = new Dictionary<string, List<string>>();
        foreach (var (nodeId, node) in graphData.Nodes)
        {
            foreach (var tag in node.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (!tagToItems.TryGetValue(tag, out var items)) { items = []; tagToItems[tag] = items; }
                items.Add(nodeId);
            }
        }

        foreach (var (tag, itemIds) in tagToItems)
        {
            var tagNodeId = $"tag:{tag}";
            graphData.Nodes[tagNodeId] = new GraphNode
            {
                Id = tagNodeId, Title = $"#{tag}", NodeType = NodeType.Tag, LinkType = "tag",
                Tags = [], ModifiedAt = DateTimeOffset.UtcNow,
                X = _random.NextDouble() * 1000 - 500, Y = _random.NextDouble() * 1000 - 500
            };

            foreach (var itemId in itemIds)
            {
                if (edgeSet.Add((itemId, tagNodeId)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = itemId, TargetId = tagNodeId, Type = EdgeType.TagRelation, Label = tag });
                }
            }
        }
    }

    /// <summary>
    /// Creates ParentChild edges by reading parent_id from page entities.
    /// </summary>
    private async Task CreateParentChildEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph",
                Action = SdkAction.ReadList,
                EntityType = "page",
            });

            if (response.Data == null) return;

            var count = 0;
            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                if (!item.TryGetProperty("parent_id", out var parentProp)) continue;
                var parentId = parentProp.GetString();
                if (string.IsNullOrEmpty(parentId)) continue;

                var childKey = $"page:{id}";
                var parentKey = $"page:{parentId}";
                if (!graphData.Nodes.ContainsKey(childKey) || !graphData.Nodes.ContainsKey(parentKey)) continue;

                if (edgeSet.Add((childKey, parentKey)))
                {
                    graphData.Edges.Add(new GraphEdge
                    {
                        SourceId = childKey,
                        TargetId = parentKey,
                        Type = EdgeType.ParentChild,
                    });
                    count++;
                }
            }

            if (count > 0)
                _log.Information("GraphDataService: created {Count} parent-child edges", count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "GraphDataService: failed to load page parent-child relationships");
        }
    }

    /// <summary>
    /// Extracts all text content from an entity JSON element.
    /// Handles both flat string fields and structured block-based content.
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
                ExtractStringsFromJson(prop, parts);
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

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
