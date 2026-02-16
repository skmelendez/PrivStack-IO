// ============================================================================
// File: ForceLayoutEngine.cs
// Description: Spiral-based layout engine for knowledge graph visualization.
//              Places nodes on a Fermat spiral (uniform density) in BFS order
//              so connected nodes are nearby. No spring physics.
// ============================================================================

using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Services;

public sealed class PhysicsParameters
{
    public double RepulsionStrength { get; set; } = -8000;
    public double LinkDistance { get; set; } = 900;
    public double LinkStrength { get; set; } = 0.15;
    public double CollisionStrength { get; set; } = 0.7;
    public double CenterStrength { get; set; } = 0.03;
    public double VelocityDecay { get; set; } = 0.6;
    public double MinSeparation { get; set; } = 90.0;
    public double Alpha { get; set; } = 1.0;
    public double AlphaMin { get; set; } = 0.001;
    public double AlphaDecay { get; set; } = 0.02;
}

public sealed class ForceLayoutEngine
{
    // Golden angle in radians — produces optimal sunflower packing
    private static readonly double GoldenAngle = Math.PI * (3 - Math.Sqrt(5));

    private readonly PhysicsParameters _params;
    private GraphData? _graphData;

    public ForceLayoutEngine(PhysicsParameters? parameters = null)
    {
        _params = parameters ?? new PhysicsParameters();
    }

    /// <summary>
    /// Copy tunable values from source into the live parameters.
    /// Does not touch runtime state (Alpha, AlphaMin, AlphaDecay).
    /// </summary>
    public void UpdateParameters(PhysicsParameters source)
    {
        _params.RepulsionStrength = source.RepulsionStrength;
        _params.LinkDistance = source.LinkDistance;
        _params.LinkStrength = source.LinkStrength;
        _params.CollisionStrength = source.CollisionStrength;
        _params.CenterStrength = source.CenterStrength;
        _params.VelocityDecay = source.VelocityDecay;
        _params.MinSeparation = source.MinSeparation;
    }

    public bool IsRunning => _params.Alpha > _params.AlphaMin;

    public void SetGraphData(GraphData data, bool preservePositions = false)
    {
        _graphData = data;

        if (preservePositions) return;

        var nodes = data.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        // BFS traversal — connected nodes end up adjacent in sequence,
        // so they land near each other on the spiral
        var ordered = ComputeBfsOrder(data);

        // Fermat spiral: r = spacing * sqrt(i), θ = i * golden_angle
        // Produces a sunflower pattern with uniform density
        var spacing = _params.MinSeparation * 2.5;

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == 0)
            {
                ordered[i].X = 0;
                ordered[i].Y = 0;
            }
            else
            {
                var r = spacing * Math.Sqrt(i);
                var theta = i * GoldenAngle;
                ordered[i].X = r * Math.Cos(theta);
                ordered[i].Y = r * Math.Sin(theta);
            }

            ordered[i].Vx = 0;
            ordered[i].Vy = 0;
        }
    }

    public void Reheat() => _params.Alpha = 1.0;

    public void Reheat(double alpha) => _params.Alpha = Math.Clamp(alpha, _params.AlphaMin, 1.0);

    /// <summary>
    /// Tick only enforces MinSeparation (post-drag cleanup).
    /// No forces, no springs — the spiral placement IS the layout.
    /// </summary>
    public void Tick()
    {
        if (_graphData is null || !IsRunning) return;

        var nodes = _graphData.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        EnforceMinSeparation(nodes);

        _params.Alpha += (_params.AlphaMin - _params.Alpha) * _params.AlphaDecay;
    }

    /// <summary>
    /// BFS from center node, producing an ordered list where connected nodes
    /// are adjacent in sequence. Within each BFS level, most-connected nodes
    /// come first so hub nodes stay near the spiral center.
    /// </summary>
    private static List<GraphNode> ComputeBfsOrder(GraphData data)
    {
        // Build adjacency list
        var adj = new Dictionary<string, List<string>>();
        foreach (var edge in data.Edges)
        {
            if (!adj.TryGetValue(edge.SourceId, out var srcList))
            {
                srcList = [];
                adj[edge.SourceId] = srcList;
            }
            srcList.Add(edge.TargetId);

            if (!adj.TryGetValue(edge.TargetId, out var tgtList))
            {
                tgtList = [];
                adj[edge.TargetId] = tgtList;
            }
            tgtList.Add(edge.SourceId);
        }

        // Root: depth-0 node first, then most-connected
        var root = data.Nodes.Values
            .OrderBy(n => n.Depth)
            .ThenByDescending(n => n.LinkCount)
            .First();

        var visited = new HashSet<string>();
        var order = new List<GraphNode>();
        var queue = new Queue<string>();

        queue.Enqueue(root.Id);
        visited.Add(root.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (data.Nodes.TryGetValue(id, out var node))
                order.Add(node);

            if (!adj.TryGetValue(id, out var neighbors)) continue;

            // Most-connected neighbors first → hub nodes near spiral center
            foreach (var neighbor in neighbors
                .OrderByDescending(n => data.Nodes.TryGetValue(n, out var nn) ? nn.LinkCount : 0))
            {
                if (visited.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        // Disconnected nodes (no path from root) go at the end
        foreach (var node in data.Nodes.Values)
        {
            if (visited.Add(node.Id))
                order.Add(node);
        }

        return order;
    }

    /// <summary>
    /// Hard exclusion zone: any two nodes closer than MinSeparation are pushed
    /// apart to exactly MinSeparation. Only force that runs — handles post-drag
    /// overlap resolution.
    /// </summary>
    private void EnforceMinSeparation(List<GraphNode> nodes)
    {
        var minSep = _params.MinSeparation;
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[j].X - nodes[i].X;
                var dy = nodes[j].Y - nodes[i].Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist >= minSep || dist <= 0) continue;

                var deficit = (minSep - dist) * 0.5;

                if (dist < 1)
                {
                    var angle = (i * 7 + j * 13) % 360 * Math.PI / 180.0;
                    dx = Math.Cos(angle);
                    dy = Math.Sin(angle);
                    dist = 1;
                }

                var mx = dx / dist * deficit;
                var my = dy / dist * deficit;

                if (!nodes[i].IsDragging && !nodes[i].IsPinned)
                {
                    nodes[i].X -= mx;
                    nodes[i].Y -= my;
                }
                if (!nodes[j].IsDragging && !nodes[j].IsPinned)
                {
                    nodes[j].X += mx;
                    nodes[j].Y += my;
                }
            }
        }
    }
}
