// ============================================================================
// File: ForceLayoutEngine.cs
// Description: Force-directed layout engine for knowledge graph visualization.
//              Ported from PrivStack.Plugin.Graph to FluidUI.
// ============================================================================

using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Services;

public sealed class PhysicsParameters
{
    public double RepulsionStrength { get; set; } = -4000;
    public double LinkDistance { get; set; } = 500;
    public double LinkStrength { get; set; } = 0.25;
    public double CollisionStrength { get; set; } = 0.7;
    public double CenterStrength { get; set; } = 0.03;
    public double VelocityDecay { get; set; } = 0.6;
    public double MinSeparation { get; set; } = 60.0;
    public double Alpha { get; set; } = 1.0;
    public double AlphaMin { get; set; } = 0.001;
    public double AlphaDecay { get; set; } = 0.04;
}

public sealed class ForceLayoutEngine
{
    private const double MaxVelocity = 25.0;

    private readonly PhysicsParameters _params;
    private GraphData? _graphData;
    private HashSet<(string, string)> _connectedPairs = [];

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

        // Cache connected pairs for connectivity-aware repulsion
        _connectedPairs = [];
        foreach (var edge in data.Edges)
        {
            var a = string.CompareOrdinal(edge.SourceId, edge.TargetId) < 0
                ? (edge.SourceId, edge.TargetId)
                : (edge.TargetId, edge.SourceId);
            _connectedPairs.Add(a);
        }

        if (preservePositions) return;

        // Place center node at origin, arrange others by depth at evenly-spaced angles
        var placed = new HashSet<string>();
        var byDepth = data.Nodes.Values
            .GroupBy(n => n.Depth)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in byDepth)
        {
            var nodesAtDepth = group.ToList();
            if (group.Key == 0)
            {
                foreach (var n in nodesAtDepth)
                {
                    n.X = 0; n.Y = 0; n.Vx = 0; n.Vy = 0;
                    placed.Add(n.Id);
                }
                continue;
            }

            var radius = group.Key * _params.LinkDistance;
            var count = nodesAtDepth.Count;
            var angleStep = 2 * Math.PI / count;
            // Golden-angle offset per depth ring for visual separation
            var angleOffset = group.Key * 2.399;

            for (var i = 0; i < count; i++)
            {
                var angle = angleOffset + i * angleStep;
                nodesAtDepth[i].X = Math.Cos(angle) * radius;
                nodesAtDepth[i].Y = Math.Sin(angle) * radius;
                nodesAtDepth[i].Vx = 0;
                nodesAtDepth[i].Vy = 0;
                placed.Add(nodesAtDepth[i].Id);
            }
        }

        var rng = new Random(42);
        foreach (var node in data.Nodes.Values)
        {
            if (placed.Contains(node.Id)) continue;
            node.X = (rng.NextDouble() - 0.5) * 200;
            node.Y = (rng.NextDouble() - 0.5) * 200;
            node.Vx = 0;
            node.Vy = 0;
        }
    }

    public void Reheat() => _params.Alpha = 1.0;

    public void Reheat(double alpha) => _params.Alpha = Math.Clamp(alpha, _params.AlphaMin, 1.0);

    public void Tick()
    {
        if (_graphData is null || !IsRunning) return;

        var nodes = _graphData.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        // Velocity-based forces
        ApplyLinkForce(nodes);
        ApplyDepthRingForce(nodes);
        ApplyRadialSpreadForce(nodes);
        ApplyCenterForce(nodes);
        UpdatePositions(nodes);

        // Direct position displacement forces (applied AFTER velocity update
        // so they can't be damped away)
        ApplyManyBodyForce(nodes);
        ApplyEdgeNodeRepulsion(nodes);
        EnforceMinSeparation(nodes);

        _params.Alpha += (_params.AlphaMin - _params.Alpha) * _params.AlphaDecay;
    }

    /// <summary>
    /// N-body repulsion via direct position displacement.
    /// Connected pairs get reduced repulsion (springs handle them);
    /// unconnected pairs get amplified repulsion to separate clusters.
    /// </summary>
    private void ApplyManyBodyForce(List<GraphNode> nodes)
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

                // Connectivity-aware: reduce for connected (spring handles), amplify for unconnected
                var pairKey = string.CompareOrdinal(nodes[i].Id, nodes[j].Id) < 0
                    ? (nodes[i].Id, nodes[j].Id)
                    : (nodes[j].Id, nodes[i].Id);
                var multiplier = _connectedPairs.Contains(pairKey) ? 0.65 : 1.6;

                var force = _params.RepulsionStrength * _params.Alpha * multiplier / distSq;
                var fx = dx / dist * force;
                var fy = dy / dist * force;

                if (!nodes[i].IsDragging && !nodes[i].IsPinned) { nodes[i].X += fx; nodes[i].Y += fy; }
                if (!nodes[j].IsDragging && !nodes[j].IsPinned) { nodes[j].X -= fx; nodes[j].Y -= fy; }
            }
        }
    }

    /// <summary>
    /// Hard exclusion zone: any two nodes closer than MinSeparation are pushed
    /// apart to exactly MinSeparation. Runs every tick regardless of alpha,
    /// velocity, or any other force. This is the "safety field."
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

                // Push apart so they're exactly MinSeparation away
                var deficit = (minSep - dist) * 0.5;
                // Add a little jitter when nodes are nearly coincident
                if (dist < 1)
                {
                    var angle = (i * 7 + j * 13) % 360 * Math.PI / 180.0;
                    dx = Math.Cos(angle);
                    dy = Math.Sin(angle);
                    dist = 1;
                }

                var mx = dx / dist * deficit;
                var my = dy / dist * deficit;

                if (!nodes[i].IsDragging && !nodes[i].IsPinned) { nodes[i].X -= mx; nodes[i].Y -= my; }
                if (!nodes[j].IsDragging && !nodes[j].IsPinned) { nodes[j].X += mx; nodes[j].Y += my; }
            }
        }
    }

    /// <summary>
    /// Push non-endpoint nodes away from edge line segments they don't belong to.
    /// Prevents nodes from sitting on unrelated edges, reducing visual crossings.
    /// Uses direct displacement (like ManyBody) so it can't be damped away.
    /// </summary>
    private void ApplyEdgeNodeRepulsion(List<GraphNode> nodes)
    {
        if (_graphData is null) return;

        var range = _params.LinkDistance * 0.5;
        if (range < 20) range = 20;
        const double strength = 0.4;
        var nodeMap = _graphData.Nodes;

        foreach (var edge in _graphData.Edges)
        {
            if (!nodeMap.TryGetValue(edge.SourceId, out var src) ||
                !nodeMap.TryGetValue(edge.TargetId, out var tgt))
                continue;

            var edgeDx = tgt.X - src.X;
            var edgeDy = tgt.Y - src.Y;
            var edgeLenSq = edgeDx * edgeDx + edgeDy * edgeDy;
            if (edgeLenSq < 1) continue;

            foreach (var node in nodes)
            {
                if (node.IsDragging || node.IsPinned) continue;
                if (node.Id == edge.SourceId || node.Id == edge.TargetId) continue;

                // Project node onto edge line segment, clamped to [0, 1]
                var t = ((node.X - src.X) * edgeDx + (node.Y - src.Y) * edgeDy) / edgeLenSq;
                t = Math.Clamp(t, 0.0, 1.0);

                var closestX = src.X + t * edgeDx;
                var closestY = src.Y + t * edgeDy;

                var dx = node.X - closestX;
                var dy = node.Y - closestY;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist >= range || dist < 0.1) continue;

                // Smooth falloff: stronger push when closer to the edge
                var factor = strength * (1.0 - dist / range) * _params.Alpha;
                node.X += dx / dist * factor * range;
                node.Y += dy / dist * factor * range;
            }
        }
    }

    /// <summary>
    /// Spring force pulling linked nodes toward LinkDistance.
    /// Uses direct displacement (not velocity) so it actually works.
    /// </summary>
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

            // Direct displacement toward ideal distance
            var delta = (dist - _params.LinkDistance) * _params.LinkStrength * _params.Alpha;
            var fx = dx / dist * delta;
            var fy = dy / dist * delta;

            if (!source.IsDragging && !source.IsPinned) { source.X += fx; source.Y += fy; }
            if (!target.IsDragging && !target.IsPinned) { target.X -= fx; target.Y -= fy; }
        }
    }

    /// <summary>
    /// Pull each node toward its ideal depth ring (depth * LinkDistance from center).
    /// Prevents nodes from drifting into the wrong ring.
    /// </summary>
    private void ApplyDepthRingForce(List<GraphNode> nodes)
    {
        const double strength = 0.2;
        // Find the center node (depth 0)
        double cx = 0, cy = 0;
        var centerCount = 0;
        foreach (var n in nodes)
        {
            if (n.Depth == 0) { cx += n.X; cy += n.Y; centerCount++; }
        }
        if (centerCount > 0) { cx /= centerCount; cy /= centerCount; }

        foreach (var n in nodes)
        {
            if (n.IsDragging || n.IsPinned || n.Depth == 0) continue;

            var dx = n.X - cx;
            var dy = n.Y - cy;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 1) continue;

            var idealDist = n.Depth * _params.LinkDistance;
            var delta = (idealDist - dist) * strength * _params.Alpha;
            n.X += dx / dist * delta;
            n.Y += dy / dist * delta;
        }
    }

    /// <summary>
    /// For nodes sharing a common parent, apply a tangential force so they spread
    /// evenly around that parent instead of clumping on one side.
    /// </summary>
    private void ApplyRadialSpreadForce(List<GraphNode> nodes)
    {
        if (_graphData is null) return;
        const double strength = 0.8;

        // Build parent→children map from edges: a child is the deeper node
        var parentChildren = new Dictionary<string, List<GraphNode>>();
        var nodeMap = _graphData.Nodes;

        foreach (var edge in _graphData.Edges)
        {
            if (!nodeMap.TryGetValue(edge.SourceId, out var src) ||
                !nodeMap.TryGetValue(edge.TargetId, out var tgt))
                continue;

            var (parent, child) = src.Depth <= tgt.Depth ? (src, tgt) : (tgt, src);
            if (!parentChildren.TryGetValue(parent.Id, out var children))
            {
                children = [];
                parentChildren[parent.Id] = children;
            }
            if (!children.Contains(child))
                children.Add(child);
        }

        // For each parent with 2+ children, nudge children apart angularly
        foreach (var (parentId, children) in parentChildren)
        {
            if (children.Count < 2) continue;
            if (!nodeMap.TryGetValue(parentId, out var parent)) continue;

            var idealSep = 2 * Math.PI / children.Count;

            // Sort children by their current angle relative to parent
            var sorted = children
                .Select(c => (Node: c, Angle: Math.Atan2(c.Y - parent.Y, c.X - parent.X)))
                .OrderBy(x => x.Angle)
                .ToList();

            for (var i = 0; i < sorted.Count; i++)
            {
                var next = (i + 1) % sorted.Count;
                var angleDiff = sorted[next].Angle - sorted[i].Angle;
                if (angleDiff < 0) angleDiff += 2 * Math.PI;

                // Push apart when closer than ideal separation
                if (angleDiff < idealSep * 0.9)
                {
                    var pushStrength = strength * (idealSep - angleDiff) * _params.Alpha;

                    foreach (var (node, angle, dir) in new[]
                    {
                        (sorted[i].Node, sorted[i].Angle, -1.0),
                        (sorted[next].Node, sorted[next].Angle, 1.0),
                    })
                    {
                        if (node.IsDragging || node.IsPinned) continue;
                        var dist = Math.Sqrt(
                            (node.X - parent.X) * (node.X - parent.X) +
                            (node.Y - parent.Y) * (node.Y - parent.Y));
                        if (dist < 1) continue;
                        var tx = -(node.Y - parent.Y) / dist * dir;
                        var ty = (node.X - parent.X) / dist * dir;
                        node.X += tx * pushStrength;
                        node.Y += ty * pushStrength;
                    }
                }
            }
        }
    }

    private void ApplyCenterForce(List<GraphNode> nodes)
    {
        double cx = 0, cy = 0;
        foreach (var n in nodes) { cx += n.X; cy += n.Y; }
        cx /= nodes.Count;
        cy /= nodes.Count;

        foreach (var n in nodes)
        {
            if (n.IsDragging || n.IsPinned) continue;
            n.Vx += (0 - cx) * _params.CenterStrength;
            n.Vy += (0 - cy) * _params.CenterStrength;
        }
    }

    private void UpdatePositions(List<GraphNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.IsDragging || n.IsPinned) continue;
            n.Vx *= 1 - _params.VelocityDecay;
            n.Vy *= 1 - _params.VelocityDecay;

            var speed = Math.Sqrt(n.Vx * n.Vx + n.Vy * n.Vy);
            if (speed > MaxVelocity)
            {
                var ratio = MaxVelocity / speed;
                n.Vx *= ratio;
                n.Vy *= ratio;
            }

            n.X += n.Vx * _params.Alpha;
            n.Y += n.Vy * _params.Alpha;
        }
    }
}
