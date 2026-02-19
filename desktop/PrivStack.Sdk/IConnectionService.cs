namespace PrivStack.Sdk;

/// <summary>
/// Represents a connected external service (e.g., GitHub, Google, Microsoft).
/// For multi-account providers, each account has a unique ConnectionId.
/// </summary>
public record ConnectionInfo(
    string Provider,
    string DisplayName,
    string? Username,
    string? AvatarUrl,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAt,
    bool IsValid,
    string? ConnectionId = null);

/// <summary>
/// Service for managing authenticated connections to external services.
/// Plugins use this to check whether a connection exists and retrieve access tokens.
/// Supports both single-connection providers (GitHub) and multi-account providers (Google, Microsoft).
/// </summary>
public interface IConnectionService
{
    // ── Single-connection methods (backward compatible) ─────────────

    /// <summary>
    /// Gets connection info for the specified provider, or null if not connected.
    /// For multi-account providers, returns the first available connection.
    /// </summary>
    Task<ConnectionInfo?> GetConnectionAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user has an active connection for the given provider.
    /// </summary>
    Task<bool> IsConnectedAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the stored access token for the provider, or null if not connected.
    /// For multi-account providers, returns the token for the first available connection.
    /// </summary>
    Task<string?> GetAccessTokenAsync(string provider, CancellationToken ct = default);

    // ── Multi-account methods ───────────────────────────────────────

    /// <summary>
    /// Gets all connections for the specified provider.
    /// </summary>
    Task<IReadOnlyList<ConnectionInfo>> GetConnectionsAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Gets a specific connection by provider and connection ID.
    /// </summary>
    Task<ConnectionInfo?> GetConnectionByIdAsync(string provider, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the access token for a specific connection, auto-refreshing if expired.
    /// </summary>
    Task<string?> GetAccessTokenByIdAsync(string provider, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Gets all connections for the provider that include the required scopes.
    /// </summary>
    Task<IReadOnlyList<ConnectionInfo>> GetConnectionsWithScopesAsync(
        string provider, IReadOnlyList<string> requiredScopes, CancellationToken ct = default);

    // ── Events ──────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a connection is added or removed.
    /// The string argument is the provider name (e.g., "github", "google").
    /// </summary>
    event Action<string>? ConnectionChanged;
}
