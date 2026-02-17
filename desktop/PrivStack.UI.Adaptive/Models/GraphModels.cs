// ============================================================================
// File: GraphModels.cs
// Description: Data models for the localized knowledge graph view.
//              Ported from PrivStack.Plugin.Graph to FluidUI.
// ============================================================================

using System.Text.Json.Serialization;

namespace PrivStack.UI.Adaptive.Models;

public record GraphNode
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("node_type")] public string NodeType { get; init; } = "note";
    [JsonPropertyName("link_type")] public string LinkType { get; init; } = "note";
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    [JsonPropertyName("link_count")] public int LinkCount { get; set; }
    [JsonPropertyName("wiki_link_count")] public int WikiLinkCount { get; set; }
    [JsonPropertyName("depth")] public int Depth { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = [];
    [JsonPropertyName("modified_at")] public DateTimeOffset ModifiedAt { get; init; }
    public bool IsPinned { get; set; }
    public bool IsDragging { get; set; }
    public bool IsHovered { get; set; }
    public bool IsSelected { get; set; }
    public double Radius => (6 + Math.Log(LinkCount + 1) * 4) * Math.Max(0.6, 1.0 - Depth * 0.15);
}

public record GraphEdge
{
    [JsonPropertyName("source_id")] public string SourceId { get; init; } = string.Empty;
    [JsonPropertyName("target_id")] public string TargetId { get; init; } = string.Empty;
    [JsonPropertyName("edge_type")] public string EdgeType { get; init; } = "link";
    public string? Label { get; init; }
    public bool IsHighlighted { get; set; }
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

    /// <summary>
    /// Assigns BFS depths from a root node. Disconnected nodes get maxDepth + 1.
    /// </summary>
    public void AssignBfsDepths(string? centerId)
    {
        var bfsRoot = centerId;
        if (string.IsNullOrEmpty(bfsRoot) || !Nodes.ContainsKey(bfsRoot))
        {
            bfsRoot = Nodes.Count > 0
                ? Nodes.OrderByDescending(kv => kv.Value.LinkCount).First().Key
                : null;
        }

        var depths = new Dictionary<string, int>();
        if (bfsRoot != null && Nodes.ContainsKey(bfsRoot))
        {
            var queue = new Queue<string>();
            queue.Enqueue(bfsRoot);
            depths[bfsRoot] = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var currentDepth = depths[current];
                foreach (var neighbor in GetNeighbors(current))
                {
                    if (depths.ContainsKey(neighbor)) continue;
                    depths[neighbor] = currentDepth + 1;
                    queue.Enqueue(neighbor);
                }
            }
        }

        var maxDepth = depths.Count > 0 ? depths.Values.Max() + 1 : 1;
        foreach (var (id, node) in Nodes)
            node.Depth = depths.GetValueOrDefault(id, maxDepth);
    }

    public static GraphData FromJson(
        IReadOnlyList<System.Text.Json.JsonElement> nodeElements,
        IReadOnlyList<System.Text.Json.JsonElement> edgeElements)
    {
        var data = new GraphData();

        foreach (var el in nodeElements)
        {
            var id = el.GetStringProp("id") ?? "";
            if (string.IsNullOrEmpty(id)) continue;

            data.Nodes[id] = new GraphNode
            {
                Id = id,
                Title = el.GetStringProp("title") ?? id,
                NodeType = el.GetStringProp("node_type") ?? "note",
                LinkType = el.GetStringProp("link_type") ?? "note",
                LinkCount = el.GetIntProp("link_count", 0),
                Depth = el.GetIntProp("depth", 0),
            };
        }

        foreach (var el in edgeElements)
        {
            var sourceId = el.GetStringProp("source_id") ?? "";
            var targetId = el.GetStringProp("target_id") ?? "";
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)) continue;
            if (!data.Nodes.ContainsKey(sourceId) || !data.Nodes.ContainsKey(targetId)) continue;

            data.Edges.Add(new GraphEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                EdgeType = el.GetStringProp("edge_type") ?? "link",
            });
        }

        // Update link counts from edges
        foreach (var edge in data.Edges)
        {
            if (data.Nodes.TryGetValue(edge.SourceId, out var src))
                src.LinkCount = Math.Max(src.LinkCount, 1);
            if (data.Nodes.TryGetValue(edge.TargetId, out var tgt))
                tgt.LinkCount = Math.Max(tgt.LinkCount, 1);
        }

        return data;
    }
}
