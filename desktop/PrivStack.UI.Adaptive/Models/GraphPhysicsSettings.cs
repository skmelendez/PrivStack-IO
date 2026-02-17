using CommunityToolkit.Mvvm.ComponentModel;
using PrivStack.UI.Adaptive.Services;

namespace PrivStack.UI.Adaptive.Models;

/// <summary>
/// Composable physics slider state for graph views. Maps 0-100 slider values
/// to consumer-specific physical parameter ranges.
/// </summary>
public partial class GraphPhysicsSettings : ObservableObject
{
    private readonly double _repelMin, _repelRange;
    private readonly double _centerMin, _centerRange;
    private readonly double _distMin, _distRange;
    private readonly double _forceMin, _forceRange;

    public GraphPhysicsSettings(
        double repelMin, double repelMax,
        double centerMin, double centerMax,
        double distMin, double distMax,
        double forceMin, double forceMax)
    {
        _repelMin = repelMin;
        _repelRange = repelMax - repelMin;
        _centerMin = centerMin;
        _centerRange = centerMax - centerMin;
        _distMin = distMin;
        _distRange = distMax - distMin;
        _forceMin = forceMin;
        _forceRange = forceMax - forceMin;
    }

    [ObservableProperty] private double _repelSlider = 50;
    [ObservableProperty] private double _centerForceSlider = 50;
    [ObservableProperty] private double _linkDistanceSlider;
    [ObservableProperty] private double _linkForceSlider = 50;

    public double RepelRadius => _repelMin + (RepelSlider / 100.0 * _repelRange);
    public double CenterForce => _centerMin + (CenterForceSlider / 100.0 * _centerRange);
    public double LinkDistance => _distMin + (LinkDistanceSlider / 100.0 * _distRange);
    public double LinkForce => _forceMin + (LinkForceSlider / 100.0 * _forceRange);

    public double RepelDisplay => RepelSlider;
    public double CenterForceDisplay => CenterForceSlider;
    public double LinkDistanceDisplay => LinkDistanceSlider;
    public double LinkForceDisplay => LinkForceSlider;

    public event EventHandler? PhysicsChanged;

    public PhysicsParameters ToPhysicsParameters() => new()
    {
        RepelRadius = RepelRadius,
        CenterStrength = CenterForce,
        LinkDistance = LinkDistance,
        LinkStrength = LinkForce,
    };

    partial void OnRepelSliderChanged(double value)
    {
        OnPropertyChanged(nameof(RepelRadius));
        OnPropertyChanged(nameof(RepelDisplay));
        PhysicsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCenterForceSliderChanged(double value)
    {
        OnPropertyChanged(nameof(CenterForce));
        OnPropertyChanged(nameof(CenterForceDisplay));
        PhysicsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnLinkDistanceSliderChanged(double value)
    {
        OnPropertyChanged(nameof(LinkDistance));
        OnPropertyChanged(nameof(LinkDistanceDisplay));
        PhysicsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnLinkForceSliderChanged(double value)
    {
        OnPropertyChanged(nameof(LinkForce));
        OnPropertyChanged(nameof(LinkForceDisplay));
        PhysicsChanged?.Invoke(this, EventArgs.Empty);
    }
}
