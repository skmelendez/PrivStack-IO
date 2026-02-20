using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace PrivStack.Desktop.Services.Connections;

/// <summary>
/// Response from GitHub's device code request endpoint.
/// </summary>
public record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary>
/// Successful token response from GitHub's device token endpoint.
/// </summary>
public record DeviceTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>
/// Implements GitHub's Device Flow (RFC 8628) for OAuth authentication
/// without requiring a localhost redirect server.
/// </summary>
public sealed class GitHubDeviceFlowService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<GitHubDeviceFlowService>();
    private static readonly HttpClient Http = CreateHttpClient();

    // GitHub App client ID — permissions (issues:write, contents:write) are
    // configured on the app itself at https://github.com/settings/apps
    private static readonly string ClientId =
        Environment.GetEnvironmentVariable("PRIVSTACK_GITHUB_CLIENT_ID")
        ?? throw new InvalidOperationException("PRIVSTACK_GITHUB_CLIENT_ID env var not set");

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PrivStack", "1.0"));
        return client;
    }

    /// <summary>
    /// Initiates the device flow by requesting a device code from GitHub.
    /// Permissions are configured on the GitHub App, not requested per-flow.
    /// </summary>
    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(
        CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
        });

        var response = await Http.PostAsync(
            "https://github.com/login/device/code", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DeviceCodeResponse>(json)
            ?? throw new InvalidOperationException("Failed to parse device code response");
    }

    /// <summary>
    /// Polls GitHub's token endpoint until the user authorizes the device,
    /// the code expires, or cancellation is requested.
    /// </summary>
    public async Task<DeviceTokenResponse> PollForTokenAsync(
        DeviceCodeResponse deviceCode, CancellationToken ct = default)
    {
        var interval = Math.Max(deviceCode.Interval, 5);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            var response = await Http.PostAsync(
                "https://github.com/login/oauth/access_token", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var tokenEl))
            {
                return JsonSerializer.Deserialize<DeviceTokenResponse>(json)
                    ?? throw new InvalidOperationException("Failed to parse token response");
            }

            if (root.TryGetProperty("error", out var errorEl))
            {
                var error = errorEl.GetString();
                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "expired_token":
                        throw new TimeoutException("Device code expired. Please try again.");
                    case "access_denied":
                        throw new OperationCanceledException("Authorization was denied by the user.");
                    default:
                        Log.Warning("Unexpected device flow error: {Error}", error);
                        throw new InvalidOperationException($"GitHub device flow error: {error}");
                }
            }
        }

        throw new TimeoutException("Device code expired before authorization completed.");
    }

    /// <summary>
    /// Fetches the authenticated user's profile from GitHub.
    /// </summary>
    public async Task<(string Username, string? AvatarUrl)> GetUserInfoAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var username = root.GetProperty("login").GetString() ?? "unknown";
        var avatarUrl = root.TryGetProperty("avatar_url", out var av) ? av.GetString() : null;

        return (username, avatarUrl);
    }
}
