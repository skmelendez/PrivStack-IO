namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Describes a required OAuth connection for a plugin.
/// </summary>
/// <param name="Provider">Internal provider key (e.g., "google", "microsoft").</param>
/// <param name="ProviderDisplayName">Human-readable name (e.g., "Google", "Microsoft").</param>
/// <param name="RequiredScopes">OAuth scopes this plugin needs from the provider.</param>
public record ConnectionRequirement(
    string Provider,
    string ProviderDisplayName,
    IReadOnlyList<string> RequiredScopes);

/// <summary>
/// Capability interface for plugins that require OAuth connections.
/// Plugins declare which providers they need; the shell shows connection
/// UI only for providers demanded by at least one active plugin.
/// </summary>
public interface IConnectionConsumer
{
    IReadOnlyList<ConnectionRequirement> RequiredConnections { get; }
}
