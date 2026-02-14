using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Plugins.Graph.Models;

public enum NodeType { Note, Task, Contact, Event, Journal, Company, ContactGroup, RssArticle, Snippet, Tag, Project, Deal, Transaction, Credential, File, WikiSource }

public record GraphNode
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("node_type")] public NodeType NodeType { get; init; } = NodeType.Note;
    [JsonPropertyName("link_type")] public string LinkType { get; init; } = "page";
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public int LinkCount { get; set; }
    public int WikiLinkCount { get; set; }
    public bool IsPinned { get; set; }
    public bool IsDragging { get; set; }
    public bool IsHovered { get; set; }
    public bool IsSelected { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = [];
    [JsonPropertyName("modified_at")] public DateTimeOffset ModifiedAt { get; init; }
    public double Radius => 6 + Math.Log(LinkCount + 1) * 4;
}

public enum OrphanFilterMode { Hide, Show, Only }
public enum EdgeType { WikiLink, Backlink, TagRelation, ProjectMembership, ParentChild, GroupMembership, CompanyMembership, WikiSourceMembership }

public record GraphEdge
{
    [JsonPropertyName("source_id")] public string SourceId { get; init; } = string.Empty;
    [JsonPropertyName("target_id")] public string TargetId { get; init; } = string.Empty;
    public EdgeType Type { get; init; } = EdgeType.WikiLink;
    public bool IsHighlighted { get; set; }
    public string? Label { get; init; }
}

public class GraphData
{
    public Dictionary<string, GraphNode> Nodes { get; init; } = new();
    public List<GraphEdge> Edges { get; init; } = [];
    public Dictionary<string, HashSet<string>> AdjacencyList { get; init; } = new();
    public int NodeCount => Nodes.Count;
    public int EdgeCount => Edges.Count;

    public void BuildAdjacencyList()
    {
        AdjacencyList.Clear();
        foreach (var edge in Edges)
        {
            if (!AdjacencyList.ContainsKey(edge.SourceId)) AdjacencyList[edge.SourceId] = [];
            if (!AdjacencyList.ContainsKey(edge.TargetId)) AdjacencyList[edge.TargetId] = [];
            AdjacencyList[edge.SourceId].Add(edge.TargetId);
            AdjacencyList[edge.TargetId].Add(edge.SourceId);
        }
    }

    public IEnumerable<string> GetNeighbors(string nodeId) =>
        AdjacencyList.TryGetValue(nodeId, out var neighbors) ? neighbors : Enumerable.Empty<string>();

    public GraphData GetLocalGraph(string centerId, int depth = 1)
    {
        var visited = new HashSet<string> { centerId };
        var frontier = new HashSet<string> { centerId };
        for (int i = 0; i < depth; i++)
        {
            var newFrontier = new HashSet<string>();
            foreach (var nodeId in frontier)
                foreach (var neighbor in GetNeighbors(nodeId))
                    if (visited.Add(neighbor)) newFrontier.Add(neighbor);
            frontier = newFrontier;
        }
        var localNodes = Nodes.Where(kv => visited.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var localEdges = Edges.Where(e => visited.Contains(e.SourceId) && visited.Contains(e.TargetId)).ToList();
        var localGraph = new GraphData();
        foreach (var kv in localNodes) localGraph.Nodes[kv.Key] = kv.Value;
        foreach (var edge in localEdges) localGraph.Edges.Add(edge);
        localGraph.BuildAdjacencyList();
        return localGraph;
    }
}

public class GraphFilter
{
    public HashSet<string> IncludeTags { get; init; } = [];
    public HashSet<string> ExcludeTags { get; init; } = [];
    public HashSet<NodeType> IncludeNodeTypes { get; init; } = [];
    public int MinLinkCount { get; set; }
    public OrphanFilterMode OrphanMode { get; set; } = OrphanFilterMode.Show;
    public bool ShowOrphanedTags { get; set; } = true;
    public DateTimeOffset? ModifiedAfter { get; set; }
    public DateTimeOffset? ModifiedBefore { get; set; }
    public bool IsLocalView { get; set; }
    public string? CenterNoteId { get; set; }
    public int LocalDepth { get; set; } = 1;
}
