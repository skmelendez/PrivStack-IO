// ============================================================================
// File: ForceLayoutEngine.cs
// Description: Two-phase graph layout engine:
//              Phase 1: Fermat spiral placement (uniform density, BFS order)
//              Phase 2: Obsidian-style 4-force physics refinement
//                       (center, repel, link, link distance)
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
    private const double MaxVelocity = 50.0;
    private static readonly double GoldenAngle = Math.PI * (3 - Math.Sqrt(5));

    private readonly PhysicsParameters _params;
    private GraphData? _graphData;

    public ForceLayoutEngine(PhysicsParameters? parameters = null)
    {
        _params = parameters ?? new PhysicsParameters();
    }

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

    /// <summary>
    /// Phase 1: Place nodes on a Fermat spiral in BFS order.
    /// Connected nodes end up adjacent in the sequence → nearby on the spiral.
    /// This gives the physics a well-spread starting position instead of a
    /// random cluster.
    /// </summary>
    public void SetGraphData(GraphData data, bool preservePositions = false)
    {
        _graphData = data;

        if (preservePositions) return;

        var nodes = data.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        var ordered = ComputeBfsOrder(data);

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
    /// Phase 2: Obsidian-style 4-force physics refinement.
    /// All forces accumulate into velocity, then velocity updates position.
    /// Forces: repel (N-body), link (spring), center (gravity).
    /// MinSeparation runs last as a hard safety floor.
    /// </summary>
    public void Tick()
    {
        if (_graphData is null || !IsRunning) return;

        var nodes = _graphData.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        // 4-force physics (velocity-based)
        ApplyRepulsion(nodes);
        ApplyLinkForce(nodes);
        ApplyCenterForce(nodes);
        UpdatePositions(nodes);

        // Hard safety floor (direct displacement)
        EnforceMinSeparation(nodes);

        _params.Alpha += (_params.AlphaMin - _params.Alpha) * _params.AlphaDecay;
    }

    // ------------------------------------------------------------------
    // Force 1: Repel — N-body inverse-square repulsion
    // ------------------------------------------------------------------

    private void ApplyRepulsion(List<GraphNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[j].X - nodes[i].X;
                var dy = nodes[j].Y - nodes[i].Y;
                var distSq = dx * dx + dy * dy;
                if (distSq < 1) distSq = 1;
                var dist = Math.Sqrt(distSq);

                // RepulsionStrength is negative → pushes apart
                var force = _params.RepulsionStrength * _params.Alpha / distSq;
                var fx = dx / dist * force;
                var fy = dy / dist * force;

                if (!nodes[i].IsDragging && !nodes[i].IsPinned)
                {
                    nodes[i].Vx += fx;
                    nodes[i].Vy += fy;
                }
                if (!nodes[j].IsDragging && !nodes[j].IsPinned)
                {
                    nodes[j].Vx -= fx;
                    nodes[j].Vy -= fy;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Force 2 & 3: Link force + Link distance — spring toward LinkDistance
    // ------------------------------------------------------------------

    private void ApplyLinkForce(List<GraphNode> nodes)
    {
        if (_graphData is null) return;
        var nodeMap = _graphData.Nodes;

        foreach (var edge in _graphData.Edges)
        {
            if (!nodeMap.TryGetValue(edge.SourceId, out var source) ||
                !nodeMap.TryGetValue(edge.TargetId, out var target))
                continue;

            var dx = target.X - source.X;
            var dy = target.Y - source.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 1) dist = 1;

            // Spring: pull toward LinkDistance
            var displacement = dist - _params.LinkDistance;
            var force = displacement * _params.LinkStrength * _params.Alpha;
            var fx = dx / dist * force;
            var fy = dy / dist * force;

            if (!source.IsDragging && !source.IsPinned)
            {
                source.Vx += fx;
                source.Vy += fy;
            }
            if (!target.IsDragging && !target.IsPinned)
            {
                target.Vx -= fx;
                target.Vy -= fy;
            }
        }
    }

    // ------------------------------------------------------------------
    // Force 4: Center — pull centroid toward origin
    // ------------------------------------------------------------------

    private void ApplyCenterForce(List<GraphNode> nodes)
    {
        double cx = 0, cy = 0;
        foreach (var n in nodes) { cx += n.X; cy += n.Y; }
        cx /= nodes.Count;
        cy /= nodes.Count;

        foreach (var n in nodes)
        {
            if (n.IsDragging || n.IsPinned) continue;
            n.Vx -= cx * _params.CenterStrength;
            n.Vy -= cy * _params.CenterStrength;
        }
    }

    // ------------------------------------------------------------------
    // Velocity integration
    // ------------------------------------------------------------------

    private void UpdatePositions(List<GraphNode> nodes)
    {
        var decay = 1 - _params.VelocityDecay;

        foreach (var n in nodes)
        {
            if (n.IsDragging || n.IsPinned) continue;

            n.Vx *= decay;
            n.Vy *= decay;

            // Clamp velocity to prevent explosions
            var speed = Math.Sqrt(n.Vx * n.Vx + n.Vy * n.Vy);
            if (speed > MaxVelocity)
            {
                var ratio = MaxVelocity / speed;
                n.Vx *= ratio;
                n.Vy *= ratio;
            }

            n.X += n.Vx;
            n.Y += n.Vy;
        }
    }

    // ------------------------------------------------------------------
    // Hard safety floor — MinSeparation exclusion zone
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // BFS ordering for spiral placement
    // ------------------------------------------------------------------

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
