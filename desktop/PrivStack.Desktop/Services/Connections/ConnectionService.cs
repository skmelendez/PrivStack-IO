using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using Serilog;

namespace PrivStack.Desktop.Services.Connections;

/// <summary>
/// Non-sensitive metadata about a connection, stored in AppSettings.
/// Keys are either "provider" (single-account) or "provider:connectionId" (multi-account).
/// </summary>
public record ConnectionMetadataEntry(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("scopes")] List<string> Scopes,
    [property: JsonPropertyName("connected_at")] DateTimeOffset ConnectedAt,
    [property: JsonPropertyName("token_expiry")] DateTimeOffset? TokenExpiry = null);

/// <summary>
/// Manages external service connections, storing tokens in the encrypted vault
/// and metadata in AppSettings. Supports single-connection (GitHub) and
/// multi-account (Google, Microsoft) providers.
/// </summary>
public sealed class ConnectionService : IConnectionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConnectionService>();

    private const string VaultId = "connections";

    private readonly IPrivStackSdk _sdk;
    private readonly IAppSettingsService _appSettings;
    private readonly OAuthBrowserFlowService _browserFlow;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    public event Action<string>? ConnectionChanged;

    public ConnectionService(
        IPrivStackSdk sdk,
        IAppSettingsService appSettings,
        OAuthBrowserFlowService browserFlow)
    {
        _sdk = sdk;
        _appSettings = appSettings;
        _browserFlow = browserFlow;
    }

    // ════════════════════════════════════════════════════════════════
    // GitHub (single-account, backward compatible)
    // ════════════════════════════════════════════════════════════════

    public async Task ConnectGitHubAsync(
        string accessToken, IReadOnlyList<string> scopes,
        string username, string? avatarUrl,
        CancellationToken ct = default)
    {
        await EnsureVaultUnlockedAsync(ct);

        var tokenBytes = Encoding.UTF8.GetBytes(accessToken);
        await _sdk.VaultBlobStore(VaultId, "github.access_token", tokenBytes, ct);

        var metadata = new ConnectionMetadataEntry(
            username, avatarUrl, scopes.ToList(), DateTimeOffset.UtcNow);

        _appSettings.Settings.ConnectionMetadata["github"] = metadata;
        _appSettings.Save();

        Log.Information("GitHub connection established for @{Username}", username);
        ConnectionChanged?.Invoke("github");
    }

    // ════════════════════════════════════════════════════════════════
    // Multi-Account OAuth (Google, Microsoft)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Orchestrates the full browser OAuth flow for a multi-account provider.
    /// Returns the connection ID of the new connection.
    /// </summary>
    public async Task<string> ConnectOAuthAsync(
        OAuthProviderConfig config, CancellationToken ct = default)
    {
        await EnsureVaultUnlockedAsync(ct);

        var codeResult = await _browserFlow.AuthorizeAsync(config, ct);
        var tokenResponse = await _browserFlow.ExchangeCodeAsync(
            config, codeResult.Code, codeResult.RedirectUri, codeResult.CodeVerifier, ct);

        var (email, name) = OAuthBrowserFlowService.ExtractClaimsFromIdToken(tokenResponse.IdToken);

        var connectionId = Guid.NewGuid().ToString("N")[..12];
        var provider = config.ProviderId;
        var compositeKey = $"{provider}:{connectionId}";

        // Store tokens in vault
        await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.access_token",
            Encoding.UTF8.GetBytes(tokenResponse.AccessToken), ct);

        if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
        {
            await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.refresh_token",
                Encoding.UTF8.GetBytes(tokenResponse.RefreshToken), ct);
        }

        var expiry = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

        // Store metadata
        var metadata = new ConnectionMetadataEntry(
            Username: email ?? name,
            AvatarUrl: null,
            Scopes: config.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            ConnectedAt: DateTimeOffset.UtcNow,
            TokenExpiry: expiry);

        _appSettings.Settings.ConnectionMetadata[compositeKey] = metadata;
        _appSettings.Save();

        Log.Information("{Provider} OAuth connection established as {Email} (id: {ConnectionId})",
            config.ProviderDisplayName, email, connectionId);
        ConnectionChanged?.Invoke(provider);

        return connectionId;
    }

    /// <summary>
    /// Import an existing refresh token as a new connection (for migration from plugin-level tokens).
    /// Returns the new connection ID.
    /// </summary>
    public async Task<string> ImportConnectionAsync(
        string provider, string refreshToken, string email, IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        await EnsureVaultUnlockedAsync(ct);

        var connectionId = Guid.NewGuid().ToString("N")[..12];
        var compositeKey = $"{provider}:{connectionId}";

        await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.refresh_token",
            Encoding.UTF8.GetBytes(refreshToken), ct);

        // Immediately refresh to get a valid access token
        var config = OAuthProviderConfig.ForProvider(provider);
        if (config != null)
        {
            try
            {
                var tokenResponse = await _browserFlow.RefreshTokenAsync(config, refreshToken, ct);
                await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.access_token",
                    Encoding.UTF8.GetBytes(tokenResponse.AccessToken), ct);

                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                {
                    await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.refresh_token",
                        Encoding.UTF8.GetBytes(tokenResponse.RefreshToken), ct);
                }

                var metadata = new ConnectionMetadataEntry(
                    Username: email,
                    AvatarUrl: null,
                    Scopes: scopes.ToList(),
                    ConnectedAt: DateTimeOffset.UtcNow,
                    TokenExpiry: DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn));

                _appSettings.Settings.ConnectionMetadata[compositeKey] = metadata;
                _appSettings.Save();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to refresh imported token for {Provider}:{ConnectionId}, storing refresh token only",
                    provider, connectionId);

                var metadata = new ConnectionMetadataEntry(
                    Username: email,
                    AvatarUrl: null,
                    Scopes: scopes.ToList(),
                    ConnectedAt: DateTimeOffset.UtcNow,
                    TokenExpiry: null);

                _appSettings.Settings.ConnectionMetadata[compositeKey] = metadata;
                _appSettings.Save();
            }
        }

        Log.Information("Imported {Provider} connection for {Email} (id: {ConnectionId})",
            provider, email, connectionId);
        ConnectionChanged?.Invoke(provider);
        return connectionId;
    }

    // ════════════════════════════════════════════════════════════════
    // Disconnect
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Disconnects a single-key provider (e.g., "github").
    /// </summary>
    public async Task DisconnectAsync(string provider, CancellationToken ct = default)
    {
        try { await _sdk.VaultBlobDelete(VaultId, $"{provider}.access_token", ct); }
        catch (Exception ex) { Log.Warning(ex, "Failed to delete vault blob for {Provider}", provider); }

        _appSettings.Settings.ConnectionMetadata.Remove(provider);
        _appSettings.Save();

        Log.Information("Disconnected from {Provider}", provider);
        ConnectionChanged?.Invoke(provider);
    }

    /// <summary>
    /// Disconnects a specific multi-account connection.
    /// </summary>
    public async Task DisconnectByIdAsync(string provider, string connectionId, CancellationToken ct = default)
    {
        var compositeKey = $"{provider}:{connectionId}";

        try { await _sdk.VaultBlobDelete(VaultId, $"{compositeKey}.access_token", ct); }
        catch (Exception ex) { Log.Warning(ex, "Failed to delete access token for {Key}", compositeKey); }

        try { await _sdk.VaultBlobDelete(VaultId, $"{compositeKey}.refresh_token", ct); }
        catch (Exception ex) { Log.Warning(ex, "Failed to delete refresh token for {Key}", compositeKey); }

        _appSettings.Settings.ConnectionMetadata.Remove(compositeKey);
        _appSettings.Save();

        _refreshLocks.TryRemove(compositeKey, out _);

        Log.Information("Disconnected {Provider} connection {ConnectionId}", provider, connectionId);
        ConnectionChanged?.Invoke(provider);
    }

    // ════════════════════════════════════════════════════════════════
    // IConnectionService — Single-connection (backward compat)
    // ════════════════════════════════════════════════════════════════

    public Task<ConnectionInfo?> GetConnectionAsync(
        string provider, CancellationToken ct = default)
    {
        // Check single-key first (GitHub)
        if (_appSettings.Settings.ConnectionMetadata.TryGetValue(provider, out var meta))
            return Task.FromResult<ConnectionInfo?>(ToConnectionInfo(provider, null, meta));

        // Fall back to first multi-account connection
        var prefix = $"{provider}:";
        var first = _appSettings.Settings.ConnectionMetadata
            .FirstOrDefault(kv => kv.Key.StartsWith(prefix));

        if (first.Value != null)
        {
            var connId = first.Key[prefix.Length..];
            return Task.FromResult<ConnectionInfo?>(ToConnectionInfo(provider, connId, first.Value));
        }

        return Task.FromResult<ConnectionInfo?>(null);
    }

    public Task<bool> IsConnectedAsync(string provider, CancellationToken ct = default)
    {
        var hasKey = _appSettings.Settings.ConnectionMetadata.ContainsKey(provider);
        if (hasKey) return Task.FromResult(true);

        var prefix = $"{provider}:";
        var hasMulti = _appSettings.Settings.ConnectionMetadata.Keys.Any(k => k.StartsWith(prefix));
        return Task.FromResult(hasMulti);
    }

    public async Task<string?> GetAccessTokenAsync(
        string provider, CancellationToken ct = default)
    {
        // Single-key (GitHub)
        if (_appSettings.Settings.ConnectionMetadata.ContainsKey(provider))
            return await ReadTokenFromVault($"{provider}.access_token", ct);

        // First multi-account
        var prefix = $"{provider}:";
        var first = _appSettings.Settings.ConnectionMetadata.Keys.FirstOrDefault(k => k.StartsWith(prefix));
        if (first == null) return null;

        var connId = first[prefix.Length..];
        return await GetAccessTokenByIdAsync(provider, connId, ct);
    }

    // ════════════════════════════════════════════════════════════════
    // IConnectionService — Multi-account
    // ════════════════════════════════════════════════════════════════

    public Task<IReadOnlyList<ConnectionInfo>> GetConnectionsAsync(
        string provider, CancellationToken ct = default)
    {
        var prefix = $"{provider}:";
        var results = _appSettings.Settings.ConnectionMetadata
            .Where(kv => kv.Key.StartsWith(prefix))
            .Select(kv => ToConnectionInfo(provider, kv.Key[prefix.Length..], kv.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<ConnectionInfo>>(results);
    }

    public Task<ConnectionInfo?> GetConnectionByIdAsync(
        string provider, string connectionId, CancellationToken ct = default)
    {
        var compositeKey = $"{provider}:{connectionId}";
        if (!_appSettings.Settings.ConnectionMetadata.TryGetValue(compositeKey, out var meta))
            return Task.FromResult<ConnectionInfo?>(null);

        return Task.FromResult<ConnectionInfo?>(ToConnectionInfo(provider, connectionId, meta));
    }

    public async Task<string?> GetAccessTokenByIdAsync(
        string provider, string connectionId, CancellationToken ct = default)
    {
        var compositeKey = $"{provider}:{connectionId}";
        if (!_appSettings.Settings.ConnectionMetadata.TryGetValue(compositeKey, out var meta))
            return null;

        // Check if token needs refresh (expired or within 5-min buffer)
        if (meta.TokenExpiry.HasValue && meta.TokenExpiry.Value <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            var refreshed = await RefreshConnectionTokenAsync(provider, connectionId, compositeKey, ct);
            if (refreshed != null) return refreshed;
        }

        return await ReadTokenFromVault($"{compositeKey}.access_token", ct);
    }

    public Task<IReadOnlyList<ConnectionInfo>> GetConnectionsWithScopesAsync(
        string provider, IReadOnlyList<string> requiredScopes, CancellationToken ct = default)
    {
        var prefix = $"{provider}:";
        var results = _appSettings.Settings.ConnectionMetadata
            .Where(kv => kv.Key.StartsWith(prefix))
            .Where(kv => requiredScopes.All(s => kv.Value.Scopes.Contains(s)))
            .Select(kv => ToConnectionInfo(provider, kv.Key[prefix.Length..], kv.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<ConnectionInfo>>(results);
    }

    // ════════════════════════════════════════════════════════════════
    // Token Refresh
    // ════════════════════════════════════════════════════════════════

    private async Task<string?> RefreshConnectionTokenAsync(
        string provider, string connectionId, string compositeKey, CancellationToken ct)
    {
        var config = OAuthProviderConfig.ForProvider(provider);
        if (config == null) return null;

        var semaphore = _refreshLocks.GetOrAdd(compositeKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another thread may have refreshed)
            if (_appSettings.Settings.ConnectionMetadata.TryGetValue(compositeKey, out var meta)
                && meta.TokenExpiry.HasValue
                && meta.TokenExpiry.Value > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return await ReadTokenFromVault($"{compositeKey}.access_token", ct);
            }

            var refreshToken = await ReadTokenFromVault($"{compositeKey}.refresh_token", ct);
            if (string.IsNullOrEmpty(refreshToken))
            {
                Log.Warning("No refresh token for {Key} — cannot auto-refresh", compositeKey);
                return null;
            }

            var tokenResponse = await _browserFlow.RefreshTokenAsync(config, refreshToken, ct);

            await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.access_token",
                Encoding.UTF8.GetBytes(tokenResponse.AccessToken), ct);

            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                await _sdk.VaultBlobStore(VaultId, $"{compositeKey}.refresh_token",
                    Encoding.UTF8.GetBytes(tokenResponse.RefreshToken), ct);
            }

            var expiry = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            if (_appSettings.Settings.ConnectionMetadata.TryGetValue(compositeKey, out var existing))
            {
                _appSettings.Settings.ConnectionMetadata[compositeKey] = existing with { TokenExpiry = expiry };
                _appSettings.SaveDebounced();
            }

            Log.Debug("Refreshed access token for {Key}, expires at {Expiry}", compositeKey, expiry);
            return tokenResponse.AccessToken;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh token for {Key}", compositeKey);
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    private async Task EnsureVaultUnlockedAsync(CancellationToken ct)
    {
        var isUnlocked = await _sdk.VaultIsUnlocked(VaultId, ct);
        if (!isUnlocked)
        {
            Log.Error("Connections vault is locked — token storage will fail");
            throw new InvalidOperationException(
                "Cannot store connection credentials: vault is locked. Please restart the app and try again.");
        }
    }

    private async Task<string?> ReadTokenFromVault(string blobKey, CancellationToken ct)
    {
        try
        {
            var bytes = await _sdk.VaultBlobRead(VaultId, blobKey, ct);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read vault blob {Key}", blobKey);
            return null;
        }
    }

    private static ConnectionInfo ToConnectionInfo(string provider, string? connectionId, ConnectionMetadataEntry meta)
    {
        return new ConnectionInfo(
            Provider: provider,
            DisplayName: GetDisplayName(provider),
            Username: meta.Username,
            AvatarUrl: meta.AvatarUrl,
            Scopes: meta.Scopes.AsReadOnly(),
            ConnectedAt: meta.ConnectedAt,
            IsValid: true,
            ConnectionId: connectionId);
    }

    private static string GetDisplayName(string provider) => provider switch
    {
        "github" => "GitHub",
        "google" => "Google",
        "microsoft" => "Microsoft",
        _ => provider
    };
}
