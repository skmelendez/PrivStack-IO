namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Capability interface for plugins that contribute nodes, structural edges,
/// content fields (for wiki-link parsing), and explicit links to the knowledge graph.
/// </summary>
public interface IGraphDataProvider
{
    /// <summary>
    /// Returns all graph data this plugin contributes: nodes, structural edges,
    /// content fields for wiki-link parsing, and explicit links.
    /// Called by GraphDataService and BacklinkService during index builds.
    /// </summary>
    Task<GraphContribution> GetGraphContributionAsync(CancellationToken ct = default);
}
