namespace PrivStack.Sdk;

/// <summary>
/// Indicates the maturity stage of a plugin or feature.
/// </summary>
public enum ReleaseStage
{
    Release,
    Beta,
    Alpha
}

/// <summary>
/// Plugin identification and display metadata.
/// </summary>
public sealed record PluginMetadata
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Version Version { get; init; }
    public string Author { get; init; } = "PrivStack";
    public string? Icon { get; init; }
    public int NavigationOrder { get; init; } = 1000;
    public PluginCategory Category { get; init; } = PluginCategory.Utility;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Version? MinAppVersion { get; init; }
    public bool CanDisable { get; init; } = true;
    public bool IsExperimental { get; init; }
    public bool IsHardLocked { get; init; }
    public string? HardLockedReason { get; init; }
    public string? WebsiteUrl { get; init; }

    /// <summary>
    /// Whether this plugin supports the InfoPanel (backlinks/local graph).
    /// Set to false for global view plugins like Graph/Nexus.
    /// </summary>
    public bool SupportsInfoPanel { get; init; } = true;

    /// <summary>
    /// The maturity stage of this plugin (Alpha, Beta, or Release).
    /// </summary>
    public ReleaseStage ReleaseStage { get; init; } = ReleaseStage.Release;

    public override string ToString() => $"{Name} ({Id}) v{Version}";
}
