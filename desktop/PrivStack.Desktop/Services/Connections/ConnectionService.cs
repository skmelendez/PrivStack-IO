using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using Serilog;

namespace PrivStack.Desktop.Services.Connections;

/// <summary>
/// Non-sensitive metadata about a connection, stored in AppSettings.
/// </summary>
public record ConnectionMetadataEntry(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("scopes")] List<string> Scopes,
    [property: JsonPropertyName("connected_at")] DateTimeOffset ConnectedAt);

/// <summary>
/// Manages external service connections, storing tokens in the encrypted vault
/// and metadata in AppSettings.
/// </summary>
public sealed class ConnectionService : IConnectionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConnectionService>();

    private const string VaultId = "connections";

    private readonly IPrivStackSdk _sdk;
    private readonly IAppSettingsService _appSettings;

    public event Action<string>? ConnectionChanged;

    public ConnectionService(IPrivStackSdk sdk, IAppSettingsService appSettings)
    {
        _sdk = sdk;
        _appSettings = appSettings;
    }

    /// <summary>
    /// Stores a GitHub access token and fetches+persists user metadata.
    /// </summary>
    public async Task ConnectGitHubAsync(
        string accessToken, IReadOnlyList<string> scopes,
        string username, string? avatarUrl,
        CancellationToken ct = default)
    {
        await EnsureVaultUnlockedAsync(ct);

        // Store token in encrypted vault
        var tokenBytes = Encoding.UTF8.GetBytes(accessToken);
        await _sdk.VaultBlobStore(VaultId, "github.access_token", tokenBytes, ct);

        // Store non-sensitive metadata in settings
        var metadata = new ConnectionMetadataEntry(
            username, avatarUrl, scopes.ToList(), DateTimeOffset.UtcNow);

        _appSettings.Settings.ConnectionMetadata["github"] = metadata;
        _appSettings.Save();

        Log.Information("GitHub connection established for @{Username}", username);
        ConnectionChanged?.Invoke("github");
    }

    /// <summary>
    /// Removes a connection: deletes vault blobs and metadata.
    /// </summary>
    public async Task DisconnectAsync(string provider, CancellationToken ct = default)
    {
        try
        {
            await _sdk.VaultBlobDelete(VaultId, $"{provider}.access_token", ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete vault blob for {Provider}", provider);
        }

        _appSettings.Settings.ConnectionMetadata.Remove(provider);
        _appSettings.Save();

        Log.Information("Disconnected from {Provider}", provider);
        ConnectionChanged?.Invoke(provider);
    }

    public Task<ConnectionInfo?> GetConnectionAsync(
        string provider, CancellationToken ct = default)
    {
        if (!_appSettings.Settings.ConnectionMetadata.TryGetValue(provider, out var meta))
            return Task.FromResult<ConnectionInfo?>(null);

        var info = new ConnectionInfo(
            Provider: provider,
            DisplayName: GetDisplayName(provider),
            Username: meta.Username,
            AvatarUrl: meta.AvatarUrl,
            Scopes: meta.Scopes.AsReadOnly(),
            ConnectedAt: meta.ConnectedAt,
            IsValid: true);

        return Task.FromResult<ConnectionInfo?>(info);
    }

    public Task<bool> IsConnectedAsync(string provider, CancellationToken ct = default)
    {
        var connected = _appSettings.Settings.ConnectionMetadata.ContainsKey(provider);
        return Task.FromResult(connected);
    }

    public async Task<string?> GetAccessTokenAsync(
        string provider, CancellationToken ct = default)
    {
        if (!_appSettings.Settings.ConnectionMetadata.ContainsKey(provider))
            return null;

        try
        {
            var bytes = await _sdk.VaultBlobRead(VaultId, $"{provider}.access_token", ct);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read access token for {Provider}", provider);
            return null;
        }
    }

    private async Task EnsureVaultUnlockedAsync(CancellationToken ct)
    {
        var isUnlocked = await _sdk.VaultIsUnlocked(VaultId, ct);
        if (!isUnlocked)
        {
            Log.Error("Connections vault is locked — token storage will fail. " +
                       "This usually means the app was not fully unlocked before connecting");
            throw new InvalidOperationException(
                "Cannot store connection credentials: vault is locked. Please restart the app and try again.");
        }
    }

    private static string GetDisplayName(string provider) => provider switch
    {
        "github" => "GitHub",
        _ => provider
    };
}
