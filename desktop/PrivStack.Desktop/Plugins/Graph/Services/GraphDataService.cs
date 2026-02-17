using System.Text.Json;
using PrivStack.Sdk.Json;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Helpers;
using PrivStack.UI.Adaptive.Models;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Plugin;
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
/// Aggregates graph data from IGraphDataProvider plugins and legacy entity loading.
/// Providers contribute nodes, structural edges, content fields, and explicit links.
/// Entity types not covered by any provider fall back to the legacy SDK-based loading path.
/// </summary>
public sealed class GraphDataService
{
    private static readonly ILogger _log = Serilog.Log.ForContext<GraphDataService>();
    private readonly IPrivStackSdk _sdk;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly Random _random = new();

    private static readonly JsonSerializerOptions JsonOptions = SdkJsonOptions.Default;

    // Legacy entity type definitions for fallback loading (types not covered by providers)
    private static readonly (string EntityType, string LinkType, string NodeType)[] LegacyEntityTypes =
    [
        ("page", "page", "note"),
        ("task", "task", "task"),
        ("project", "project", "project"),
        ("contact", "contact", "contact"),
        ("journal_entry", "journal", "journal"),
        ("event", "event", "event"),
        ("deal", "deal", "deal"),
        ("transaction", "transaction", "transaction"),
        ("snippet", "snippet", "snippet"),
        ("rss_article", "rss_article", "rss"),
        ("credential", "credential", "credential"),
        ("vault_file", "file", "file"),
        ("company", "company", "company"),
        ("contact_group", "contact_group", "contact_group"),
        ("wiki_source", "wiki_source", "wiki_source"),
        ("web_clip", "web_clip", "web_clip"),
    ];

#pragma warning disable CS0067 // Event is reserved for future graph change notifications
    public event EventHandler<GraphDataChangedEventArgs>? GraphDataChanged;
#pragma warning restore CS0067

    public GraphDataService(IPrivStackSdk sdk, IPluginRegistry pluginRegistry)
    {
        _sdk = sdk;
        _pluginRegistry = pluginRegistry;
    }

    public async Task<GraphData> LoadGlobalGraphAsync()
    {
        var graphData = new GraphData();
        var edgeSet = new HashSet<(string, string)>();

        // --- Phase 1: Collect contributions from providers ---
        var coveredLinkTypes = new HashSet<string>();
        var allContentFields = new List<ContentField>();
        var allExplicitLinks = new List<ExplicitLinkContribution>();

        var providers = _pluginRegistry.GetCapabilityProviders<IGraphDataProvider>();
        if (providers.Count > 0)
        {
            var providerTasks = providers.Select(p => GetContributionSafeAsync(p)).ToArray();
            var contributions = await Task.WhenAll(providerTasks);

            foreach (var contribution in contributions)
            {
                if (contribution == null) continue;

                // Add nodes
                foreach (var node in contribution.Nodes)
                {
                    coveredLinkTypes.Add(node.LinkType);
                    graphData.Nodes[node.CompositeKey] = new GraphNode
                    {
                        Id = node.CompositeKey,
                        Title = node.Title,
                        NodeType = node.NodeType,
                        LinkType = node.LinkType,
                        Tags = node.Tags.ToList(),
                        ModifiedAt = node.ModifiedAt,
                        X = _random.NextDouble() * 1000 - 500,
                        Y = _random.NextDouble() * 1000 - 500,
                    };
                }

                // Add structural edges
                foreach (var edge in contribution.StructuralEdges)
                {
                    if (!graphData.Nodes.ContainsKey(edge.SourceKey) ||
                        !graphData.Nodes.ContainsKey(edge.TargetKey)) continue;
                    if (edgeSet.Add((edge.SourceKey, edge.TargetKey)))
                    {
                        graphData.Edges.Add(new GraphEdge
                        {
                            SourceId = edge.SourceKey,
                            TargetId = edge.TargetKey,
                            EdgeType = edge.EdgeType,
                            Label = edge.Label,
                        });
                    }
                }

                allContentFields.AddRange(contribution.ContentFields);
                allExplicitLinks.AddRange(contribution.ExplicitLinks);
            }

            _log.Information("GraphDataService: {ProviderCount} providers contributed {NodeCount} nodes, " +
                "{CoveredTypes} link types covered",
                providers.Count, graphData.Nodes.Count, coveredLinkTypes.Count);
        }

        // --- Phase 2: Legacy fallback for uncovered entity types ---
        var legacyTypes = LegacyEntityTypes
            .Where(et => !coveredLinkTypes.Contains(et.LinkType))
            .ToArray();

        if (legacyTypes.Length > 0)
        {
            var legacyTasks = legacyTypes
                .Select(et => LoadEntitiesAsync(et.EntityType, et.LinkType, et.NodeType))
                .ToArray();
            await Task.WhenAll(legacyTasks);

            foreach (var task in legacyTasks)
            {
                foreach (var node in task.Result)
                    graphData.Nodes[node.Id] = node;
            }

            _log.Information("GraphDataService: legacy fallback loaded {LegacyTypes} entity types, " +
                "total {NodeCount} nodes",
                legacyTypes.Length, graphData.Nodes.Count);

            // Legacy structural edges for uncovered types
            await CreateLegacyStructuralEdgesAsync(graphData, edgeSet, coveredLinkTypes);
        }

        // --- Phase 3: Wiki-link parsing from content fields ---
        ParseContentFieldLinks(allContentFields, graphData, edgeSet);

        // Legacy content parsing for nodes not covered by providers
        await ParseLegacyLinksAsync(graphData, edgeSet, coveredLinkTypes);

        // --- Phase 4: Explicit links from providers ---
        foreach (var link in allExplicitLinks)
        {
            if (link.SourceKey == link.TargetKey) continue;
            if (!graphData.Nodes.ContainsKey(link.SourceKey) ||
                !graphData.Nodes.ContainsKey(link.TargetKey)) continue;
            if (edgeSet.Add((link.SourceKey, link.TargetKey)))
            {
                graphData.Edges.Add(new GraphEdge
                {
                    SourceId = link.SourceKey,
                    TargetId = link.TargetKey,
                    EdgeType = "link",
                });
            }
        }

        _log.Information("GraphDataService: {EdgeCount} edges after all link parsing", edgeSet.Count);

        // --- Phase 5: Tag synthesis + link count calculation ---
        CreateTagNodesAndEdges(graphData, edgeSet);

        foreach (var edge in graphData.Edges)
        {
            var isStructural = edge.EdgeType is "tag" or "project" or "parent" or "group" or "company" or "wiki_source";
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

    // ========================================================================
    // Provider helpers
    // ========================================================================

    private static async Task<GraphContribution?> GetContributionSafeAsync(IGraphDataProvider provider)
    {
        try
        {
            return await provider.GetGraphContributionAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "GraphDataService: provider {Provider} failed", provider.GetType().Name);
            return null;
        }
    }

    private void ParseContentFieldLinks(
        IReadOnlyList<ContentField> contentFields,
        GraphData graphData,
        HashSet<(string, string)> edgeSet)
    {
        foreach (var field in contentFields)
        {
            var links = WikiLinkParser.ParseLinks(field.Content);
            foreach (var link in links)
            {
                if (field.OwnerKey == link.CompositeKey) continue;
                if (!graphData.Nodes.ContainsKey(link.CompositeKey)) continue;
                if (edgeSet.Add((field.OwnerKey, link.CompositeKey)))
                {
                    graphData.Edges.Add(new GraphEdge
                    {
                        SourceId = field.OwnerKey,
                        TargetId = link.CompositeKey,
                        EdgeType = "link",
                    });
                }
            }
        }
    }

    // ========================================================================
    // Tag synthesis (cross-cutting, stays in GraphDataService)
    // ========================================================================

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
                Id = tagNodeId, Title = $"#{tag}", NodeType = "tag", LinkType = "tag",
                Tags = [], ModifiedAt = DateTimeOffset.UtcNow,
                X = _random.NextDouble() * 1000 - 500, Y = _random.NextDouble() * 1000 - 500
            };

            foreach (var itemId in itemIds)
            {
                if (edgeSet.Add((itemId, tagNodeId)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = itemId, TargetId = tagNodeId, EdgeType = "tag", Label = tag });
                }
            }
        }
    }

    // ========================================================================
    // Legacy fallback (used until all plugins implement IGraphDataProvider)
    // ========================================================================

    private async Task<List<GraphNode>> LoadEntitiesAsync(string entityType, string linkType, string nodeType)
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

    /// <summary>
    /// Creates legacy structural edges only for entity types not covered by providers.
    /// </summary>
    private async Task CreateLegacyStructuralEdgesAsync(
        GraphData graphData, HashSet<(string, string)> edgeSet, HashSet<string> coveredLinkTypes)
    {
        if (!coveredLinkTypes.Contains("page"))
        {
            await CreateParentChildEdgesAsync(graphData, edgeSet);
            await CreateWikiSourceEdgesAsync(graphData, edgeSet);
        }
        if (!coveredLinkTypes.Contains("contact"))
            await CreateCompanyMembershipEdgesAsync(graphData, edgeSet);
        if (!coveredLinkTypes.Contains("contact_group"))
            await CreateGroupMembershipEdgesAsync(graphData, edgeSet);
        if (!coveredLinkTypes.Contains("task"))
            await CreateProjectMembershipEdgesAsync(graphData, edgeSet);
    }

    /// <summary>
    /// Parses wiki-links from legacy (non-provider) nodes by fetching content individually.
    /// </summary>
    private async Task ParseLegacyLinksAsync(
        GraphData graphData, HashSet<(string, string)> edgeSet, HashSet<string> coveredLinkTypes)
    {
        var legacyNodes = graphData.Nodes
            .Where(kv => !coveredLinkTypes.Contains(kv.Value.LinkType) && kv.Value.LinkType != "tag")
            .ToList();

        if (legacyNodes.Count == 0) return;

        var nodesWithContent = 0;
        foreach (var (nodeId, _) in legacyNodes)
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

                var allContent = WikiLinkParser.ExtractContentFromEntity(response.Data);
                if (!string.IsNullOrEmpty(allContent))
                {
                    nodesWithContent++;
                    var links = WikiLinkParser.ParseLinks(allContent);
                    foreach (var link in links)
                    {
                        if (nodeId == link.CompositeKey) continue;
                        if (!graphData.Nodes.ContainsKey(link.CompositeKey)) continue;
                        if (edgeSet.Add((nodeId, link.CompositeKey)))
                        {
                            graphData.Edges.Add(new GraphEdge
                            {
                                SourceId = nodeId,
                                TargetId = link.CompositeKey,
                                EdgeType = "link",
                            });
                        }
                    }
                }

                ParseExplicitLinksFromCustomFields(nodeId, response.Data, graphData, edgeSet);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "GraphDataService: failed to load content for {NodeId}", nodeId);
            }
        }

        if (nodesWithContent > 0)
            _log.Information("GraphDataService: legacy content parsed for {Count} nodes", nodesWithContent);
    }

    private static void ParseExplicitLinksFromCustomFields(
        string sourceKey, JsonElement item, GraphData graphData, HashSet<(string, string)> edgeSet)
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
                        SourceId = sourceKey, TargetId = projectKey, EdgeType = "project",
                    });
                }
            }
        }

        if (!item.TryGetProperty("custom_fields", out var customFields) ||
            customFields.ValueKind != JsonValueKind.Object) return;

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
                if (sourceKey == targetKey || !graphData.Nodes.ContainsKey(targetKey)) continue;
                if (edgeSet.Add((sourceKey, targetKey)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = sourceKey, TargetId = targetKey, EdgeType = "link" });
                }
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
                if (string.IsNullOrEmpty(linkType) || string.IsNullOrEmpty(itemId)) continue;
                var targetKey = $"{linkType}:{itemId}";
                if (sourceKey == targetKey || !graphData.Nodes.ContainsKey(targetKey)) continue;
                if (edgeSet.Add((sourceKey, targetKey)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = sourceKey, TargetId = targetKey, EdgeType = "link" });
                }
            }
        }
    }

    private async Task CreateParentChildEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph", Action = SdkAction.ReadList, EntityType = "page",
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
                    graphData.Edges.Add(new GraphEdge { SourceId = childKey, TargetId = parentKey, EdgeType = "parent" });
                    count++;
                }
            }
            if (count > 0) _log.Information("GraphDataService: created {Count} parent-child edges", count);
        }
        catch (Exception ex) { _log.Warning(ex, "GraphDataService: failed to load page parent-child relationships"); }
    }

    private async Task CreateWikiSourceEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph", Action = SdkAction.ReadList, EntityType = "page",
            });
            if (response.Data == null) return;

            var count = 0;
            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                if (!item.TryGetProperty("source_type", out var stProp) || stProp.GetString() != "github_wiki") continue;
                if (!item.TryGetProperty("source_id", out var siProp)) continue;
                var sourceId = siProp.GetString();
                if (string.IsNullOrEmpty(sourceId)) continue;

                var pageKey = $"page:{id}";
                var wikiKey = $"wiki_source:{sourceId}";
                if (!graphData.Nodes.ContainsKey(pageKey) || !graphData.Nodes.ContainsKey(wikiKey)) continue;

                if (edgeSet.Add((pageKey, wikiKey)))
                {
                    graphData.Edges.Add(new GraphEdge { SourceId = pageKey, TargetId = wikiKey, EdgeType = "wiki_source" });
                    count++;
                }
            }
            if (count > 0) _log.Information("GraphDataService: created {Count} wiki source edges", count);
        }
        catch (Exception ex) { _log.Warning(ex, "GraphDataService: failed to load wiki source relationships"); }
    }

    private async Task CreateCompanyMembershipEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph", Action = SdkAction.ReadList, EntityType = "contact",
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
                    graphData.Edges.Add(new GraphEdge { SourceId = contactKey, TargetId = companyKey, EdgeType = "company" });
                    count++;
                }
            }
            if (count > 0) _log.Information("GraphDataService: created {Count} company membership edges", count);
        }
        catch (Exception ex) { _log.Warning(ex, "GraphDataService: failed to load contact company relationships"); }
    }

    private async Task CreateGroupMembershipEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph", Action = SdkAction.ReadList, EntityType = "contact_group",
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
                        graphData.Edges.Add(new GraphEdge { SourceId = contactKey, TargetId = groupKey, EdgeType = "group" });
                        count++;
                    }
                }
            }
            if (count > 0) _log.Information("GraphDataService: created {Count} group membership edges", count);
        }
        catch (Exception ex) { _log.Warning(ex, "GraphDataService: failed to load contact group relationships"); }
    }

    private async Task CreateProjectMembershipEdgesAsync(GraphData graphData, HashSet<(string, string)> edgeSet)
    {
        try
        {
            var response = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
            {
                PluginId = "privstack.graph", Action = SdkAction.ReadList, EntityType = "task",
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
                    graphData.Edges.Add(new GraphEdge { SourceId = taskKey, TargetId = projectKey, EdgeType = "project" });
                    count++;
                }
            }
            if (count > 0) _log.Information("GraphDataService: created {Count} project membership edges", count);
        }
        catch (Exception ex) { _log.Warning(ex, "GraphDataService: failed to load task project relationships"); }
    }
}
