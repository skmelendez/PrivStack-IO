using PrivStack.Desktop.Plugins.Graph.Models;

namespace PrivStack.Desktop.Plugins.Graph.Services;

public class PhysicsParameters
{
    public double RepulsionStrength { get; set; } = -750;
    public double LinkDistance { get; set; } = 290;
    public double LinkStrength { get; set; } = 0.24;
    public double CollisionStrength { get; set; } = 0.8;
    public double CenterStrength { get; set; } = 0.01;
    public double VelocityDecay { get; set; } = 0.4;
    public double Alpha { get; set; } = 1.0;
    public double AlphaMin { get; set; } = 0.001;
    public double AlphaDecay { get; set; } = 0.0228;
}

public class ForceLayoutEngine
{
    private readonly PhysicsParameters _params;
    private GraphData? _graphData;

    public ForceLayoutEngine(PhysicsParameters? parameters = null)
    {
        _params = parameters ?? new PhysicsParameters();
    }

    public PhysicsParameters Parameters => _params;
    public bool IsRunning => _params.Alpha > _params.AlphaMin;

    public void SetGraphData(GraphData data) => _graphData = data;

    public void Reheat() => _params.Alpha = 1.0;

    public void Tick()
    {
        if (_graphData == null || !IsRunning) return;

        var nodes = _graphData.Nodes.Values.ToList();
        if (nodes.Count == 0) return;

        ApplyManyBodyForce(nodes);
        ApplyLinkForce(nodes);
        ApplyCollisionForce(nodes);
        ApplyCenterForce(nodes);
        UpdatePositions(nodes);

        _params.Alpha += (_params.AlphaMin - _params.Alpha) * _params.AlphaDecay;
    }

    private void ApplyManyBodyForce(List<GraphNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[j].X - nodes[i].X;
                var dy = nodes[j].Y - nodes[i].Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) dist = 1;

                var force = _params.RepulsionStrength / (dist * dist);
                var fx = dx / dist * force;
                var fy = dy / dist * force;

                if (!nodes[i].IsDragging && !nodes[i].IsPinned) { nodes[i].Vx -= fx; nodes[i].Vy -= fy; }
                if (!nodes[j].IsDragging && !nodes[j].IsPinned) { nodes[j].Vx += fx; nodes[j].Vy += fy; }
            }
        }
    }

    private void ApplyLinkForce(List<GraphNode> nodes)
    {
        if (_graphData == null) return;
        var nodeMap = _graphData.Nodes;

        foreach (var edge in _graphData.Edges)
        {
            if (!nodeMap.TryGetValue(edge.SourceId, out var source) || !nodeMap.TryGetValue(edge.TargetId, out var target))
                continue;

            var dx = target.X - source.X;
            var dy = target.Y - source.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 1) dist = 1;

            var force = (dist - _params.LinkDistance) * _params.LinkStrength;
            var fx = dx / dist * force;
            var fy = dy / dist * force;

            if (!source.IsDragging && !source.IsPinned) { source.Vx += fx; source.Vy += fy; }
            if (!target.IsDragging && !target.IsPinned) { target.Vx -= fx; target.Vy -= fy; }
        }
    }

    private void ApplyCollisionForce(List<GraphNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[j].X - nodes[i].X;
                var dy = nodes[j].Y - nodes[i].Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var minDist = nodes[i].Radius + nodes[j].Radius;

                if (dist < minDist && dist > 0)
                {
                    var overlap = (minDist - dist) * _params.CollisionStrength * 0.5;
                    var fx = dx / dist * overlap;
                    var fy = dy / dist * overlap;

                    if (!nodes[i].IsDragging && !nodes[i].IsPinned) { nodes[i].X -= fx; nodes[i].Y -= fy; }
                    if (!nodes[j].IsDragging && !nodes[j].IsPinned) { nodes[j].X += fx; nodes[j].Y += fy; }
                }
            }
        }
    }

    private void ApplyCenterForce(List<GraphNode> nodes)
    {
        double cx = 0, cy = 0;
        foreach (var n in nodes) { cx += n.X; cy += n.Y; }
        cx /= nodes.Count; cy /= nodes.Count;

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
            n.Vx *= (1 - _params.VelocityDecay);
            n.Vy *= (1 - _params.VelocityDecay);
            n.X += n.Vx * _params.Alpha;
            n.Y += n.Vy * _params.Alpha;
        }
    }
}
