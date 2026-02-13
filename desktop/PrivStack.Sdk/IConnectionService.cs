namespace PrivStack.Sdk;

/// <summary>
/// Represents a connected external service (e.g., GitHub, Google Calendar).
/// </summary>
public record ConnectionInfo(
    string Provider,
    string DisplayName,
    string? Username,
    string? AvatarUrl,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAt,
    bool IsValid);

/// <summary>
/// Service for managing authenticated connections to external services.
/// Plugins use this to check whether a connection exists and retrieve access tokens.
/// </summary>
public interface IConnectionService
{
    /// <summary>
    /// Gets connection info for the specified provider, or null if not connected.
    /// </summary>
    Task<ConnectionInfo?> GetConnectionAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user has an active connection for the given provider.
    /// </summary>
    Task<bool> IsConnectedAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the stored access token for the provider, or null if not connected.
    /// </summary>
    Task<string?> GetAccessTokenAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Raised when a connection is added or removed.
    /// The string argument is the provider name (e.g., "github").
    /// </summary>
    event Action<string>? ConnectionChanged;
}
