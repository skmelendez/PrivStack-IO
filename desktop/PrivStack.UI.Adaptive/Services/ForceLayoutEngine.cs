// ============================================================================
// File: ForceLayoutEngine.cs
// Description: Spiral placement + radius-based repel + anchor springs.
//              Nodes repel within RepelRadius. Each node remembers its initial
//              spiral position and springs back toward it when displaced.
// ============================================================================

using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Services;

public sealed class PhysicsParameters
{
    /// <summary>Repel radius — nodes within this distance push apart.</summary>
    public double RepelRadius { get; set; } = 120.0;

    /// <summary>Spring strength pulling nodes back to their anchor (0-1).</summary>
    public double SpringStrength { get; set; } = 0.08;

    /// <summary>Min distance between connected nodes — pushed apart if closer.</summary>
    public double LinkDistance { get; set; } = 200.0;

    // Kept for interface compat (unused by engine)
    public double RepulsionStrength { get; set; } = -8000;
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
    private static readonly double GoldenAngle = Math.PI * (3 - Math.Sqrt(5));

    private readonly PhysicsParameters _params;
    private GraphData? _graphData;
    private Dictionary<string, (double X, double Y)> _anchors = new();

    public ForceLayoutEngine(PhysicsParameters? parameters = null)
    {
        _params = parameters ?? new PhysicsParameters();
    }

    public void UpdateParameters(PhysicsParameters source)
    {
        _params.RepelRadius = source.RepelRadius;
        _params.SpringStrength = source.SpringStrength;
        _params.LinkDistance = source.LinkDistance;
    }

    public bool IsRunning => _params.Alpha > _params.AlphaMin;

    public void SetGraphData(GraphData data, bool preservePositions = false)
    {
        _graphData = data;

        if (preservePositions) return;

        var nodes = data.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        var ordered = ComputeBfsOrder(data);

        // Fixed compact spiral — repel force expands from here
        const double spacing = 150.0;

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

        // Store initial positions as anchor points (resting positions)
        _anchors.Clear();
        foreach (var node in ordered)
            _anchors[node.Id] = (node.X, node.Y);
    }

    public void Reheat() => _params.Alpha = 1.0;

    public void Reheat(double alpha) => _params.Alpha = Math.Clamp(alpha, _params.AlphaMin, 1.0);

    public void Tick()
    {
        if (_graphData is null || !IsRunning) return;

        var nodes = _graphData.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        ApplyRepel(nodes);
        ApplyLinkMinDistance();
        ApplySpringToAnchor(nodes);

        _params.Alpha += (_params.AlphaMin - _params.Alpha) * _params.AlphaDecay;
    }

    /// <summary>
    /// Any two nodes closer than RepelRadius get pushed apart to exactly
    /// RepelRadius. Direct displacement, no velocity, no springs.
    /// </summary>
    private void ApplyRepel(List<GraphNode> nodes)
    {
        var radius = _params.RepelRadius;

        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[j].X - nodes[i].X;
                var dy = nodes[j].Y - nodes[i].Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist >= radius) continue;

                // Jitter coincident nodes
                if (dist < 1)
                {
                    var angle = (i * 7 + j * 13) % 360 * Math.PI / 180.0;
                    dx = Math.Cos(angle);
                    dy = Math.Sin(angle);
                    dist = 1;
                }

                // Push apart so they're exactly RepelRadius away
                var deficit = (radius - dist) * 0.5;
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

    /// <summary>
    /// Connected nodes closer than LinkDistance get pushed apart.
    /// Same displacement logic as repel but only for linked pairs.
    /// </summary>
    private void ApplyLinkMinDistance()
    {
        if (_graphData is null) return;
        var minDist = _params.LinkDistance;
        if (minDist <= 0) return;

        foreach (var edge in _graphData.Edges)
        {
            if (!_graphData.Nodes.TryGetValue(edge.SourceId, out var a)) continue;
            if (!_graphData.Nodes.TryGetValue(edge.TargetId, out var b)) continue;

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist >= minDist) continue;

            if (dist < 1)
            {
                dx = 1;
                dy = 0;
                dist = 1;
            }

            var deficit = (minDist - dist) * 0.5;
            var mx = dx / dist * deficit;
            var my = dy / dist * deficit;

            if (!a.IsDragging && !a.IsPinned) { a.X -= mx; a.Y -= my; }
            if (!b.IsDragging && !b.IsPinned) { b.X += mx; b.Y += my; }
        }
    }

    /// <summary>
    /// Pull each node back toward its initial spiral position (anchor).
    /// Strength is proportional to displacement — classic spring force.
    /// </summary>
    private void ApplySpringToAnchor(List<GraphNode> nodes)
    {
        var strength = _params.SpringStrength;

        foreach (var node in nodes)
        {
            if (node.IsDragging || node.IsPinned) continue;
            if (!_anchors.TryGetValue(node.Id, out var anchor)) continue;

            var dx = anchor.X - node.X;
            var dy = anchor.Y - node.Y;

            node.X += dx * strength;
            node.Y += dy * strength;
        }
    }

    private static List<GraphNode> ComputeBfsOrder(GraphData data)
    {
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

            foreach (var neighbor in neighbors
                .OrderByDescending(n => data.Nodes.TryGetValue(n, out var nn) ? nn.LinkCount : 0))
            {
                if (visited.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        foreach (var node in data.Nodes.Values)
        {
            if (visited.Add(node.Id))
                order.Add(node);
        }

        return order;
    }
}
