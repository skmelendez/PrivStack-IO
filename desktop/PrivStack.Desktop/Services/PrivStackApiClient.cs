using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivStack.Desktop.Models;

namespace PrivStack.Desktop.Services;

/// <summary>
/// HTTP client for authenticating with the PrivStack API and fetching license keys.
/// </summary>
public sealed class PrivStackApiClient
{
    public const string ApiBaseUrl = "https://privstack.io";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Authenticates with the PrivStack API using email/password.
    /// </summary>
    [Obsolete("Use OAuth2 PKCE flow via OAuthLoginService")]
    public async Task<LoginResponse> LoginAsync(string email, string password)
    {
        var payload = JsonSerializer.Serialize(new { email, password }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/auth/login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Client-Type", "desktop");

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body);
            throw new PrivStackApiException(error ?? $"Login failed (HTTP {(int)response.StatusCode})");
        }

        return JsonSerializer.Deserialize<LoginResponse>(body, JsonOptions)
               ?? throw new PrivStackApiException("Empty response from server");
    }

    /// <summary>
    /// Fetches the user's license key from the PrivStack API.
    /// </summary>
    public async Task<LicenseResponse> GetLicenseKeyAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/account/license");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body);
            throw new PrivStackApiException(error ?? $"Failed to fetch license (HTTP {(int)response.StatusCode})");
        }

        return JsonSerializer.Deserialize<LicenseResponse>(body, JsonOptions)
               ?? throw new PrivStackApiException("Empty response from server");
    }

    /// <summary>
    /// Fetches the list of official plugins from the PrivStack registry.
    /// </summary>
    public async Task<IReadOnlyList<Models.PluginRegistry.OfficialPluginInfo>> GetOfficialPluginsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/official-plugins");
        request.Headers.Add("X-Client-Type", "desktop");

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body);
            throw new PrivStackApiException(error ?? $"Failed to fetch official plugins (HTTP {(int)response.StatusCode})");
        }

        var result = JsonSerializer.Deserialize<OfficialPluginsResponse>(body, JsonOptions);
        return result?.Plugins ?? [];
    }

    /// <summary>
    /// Fetches the latest release info from the public endpoint (no auth required).
    /// </summary>
    public async Task<LatestReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/releases/latest");
        request.Headers.Add("X-Client-Type", "desktop");

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body);
            throw new PrivStackApiException(error ?? $"Failed to fetch latest release (HTTP {(int)response.StatusCode})");
        }

        return JsonSerializer.Deserialize<LatestReleaseInfo>(body, JsonOptions);
    }

    /// <summary>
    /// Fetches downloadable release artifacts with checksums (requires authentication).
    /// </summary>
    public async Task<AccountReleasesResponse?> GetAccountReleasesAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/account/releases");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body);
            throw new PrivStackApiException(error ?? $"Failed to fetch account releases (HTTP {(int)response.StatusCode})");
        }

        return JsonSerializer.Deserialize<AccountReleasesResponse>(body, JsonOptions);
    }

    /// <summary>
    /// Exchanges an OAuth2 authorization code for access and refresh tokens.
    /// </summary>
    public async Task<OAuthTokenResponse> ExchangeCodeForTokenAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            grant_type = "authorization_code",
            code,
            code_verifier = codeVerifier,
            redirect_uri = redirectUri,
            client_id = "privstack-desktop"
        }, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/connect/token")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(body);
            throw new PrivStackApiException(error ?? $"Token exchange failed (HTTP {(int)response.StatusCode})");
        }

        return JsonSerializer.Deserialize<OAuthTokenResponse>(body, JsonOptions)
               ?? throw new PrivStackApiException("Empty response from token endpoint");
    }

    /// <summary>
    /// Starts a free trial by submitting an email to the trial endpoint.
    /// Returns a signed license key on success.
    /// </summary>
    public async Task<TrialResponse> StartTrialAsync(string email, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { email }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/trial/start")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Client-Type", "desktop");

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var result = JsonSerializer.Deserialize<TrialResponse>(body, JsonOptions)
                     ?? new TrialResponse();

        if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(result.Error))
        {
            var errorMsg = TryParseError(body);
            return new TrialResponse { Error = errorMsg ?? $"Trial request failed (HTTP {(int)response.StatusCode})" };
        }

        return result;
    }

    /// <summary>
    /// Verifies a 6-digit email code and completes trial activation.
    /// </summary>
    public async Task<TrialResponse> VerifyTrialCodeAsync(string email, string code, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { email, code }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/trial/verify")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Client-Type", "desktop");

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var result = JsonSerializer.Deserialize<TrialResponse>(body, JsonOptions)
                     ?? new TrialResponse();

        if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(result.Error))
        {
            var errorMsg = TryParseError(body);
            return new TrialResponse { Error = errorMsg ?? $"Verification failed (HTTP {(int)response.StatusCode})" };
        }

        return result;
    }

    /// <summary>
    /// Lists cloud workspaces for the authenticated user (direct HTTP — no FFI required).
    /// Returns an empty list on HTTP error (user may not have cloud access).
    /// </summary>
    public async Task<List<CloudWorkspaceInfo>> ListCloudWorkspacesAsync(
        string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/cloud/workspaces");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var body = await response.Content.ReadAsStringAsync(ct);
            var wrapper = JsonSerializer.Deserialize<CloudWorkspacesResponse>(body, JsonOptions);
            return wrapper?.Workspaces ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Validates a license key against the server.
    /// Returns the validation response; on network/HTTP errors returns Valid = false with error detail.
    /// </summary>
    public async Task<LicenseValidationResponse> ValidateLicenseAsync(string licenseKey, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { key = licenseKey }, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/license/activate")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Client-Type", "desktop");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await Http.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var parsed = JsonSerializer.Deserialize<LicenseValidationResponse>(body, JsonOptions);
                if (parsed != null) return parsed with { Valid = false };

                var errorMsg = TryParseError(body);
                return new LicenseValidationResponse { Valid = false, Error = errorMsg ?? $"HTTP {(int)response.StatusCode}" };
            }

            return JsonSerializer.Deserialize<LicenseValidationResponse>(body, JsonOptions)
                   ?? new LicenseValidationResponse { Valid = false, Error = "Empty response" };
        }
        catch (OperationCanceledException)
        {
            return new LicenseValidationResponse { Valid = false, Error = "Request timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new LicenseValidationResponse { Valid = false, Error = ex.Message };
        }
    }

    private static string? TryParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
                return errorProp.GetString();
        }
        catch
        {
            // Not JSON or no error field
        }
        return null;
    }
}

/// <summary>
/// Exception for PrivStack API errors.
/// </summary>
public class PrivStackApiException : Exception
{
    public PrivStackApiException(string message) : base(message) { }
}

// --- Response DTOs ---

public record LoginResponse
{
    [JsonPropertyName("user")]
    public LoginUser? User { get; init; }

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }
}

public record LoginUser
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

public record LicenseResponse
{
    [JsonPropertyName("license")]
    public LicenseData? License { get; init; }
}

public record LicenseData
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("plan")]
    public string Plan { get; init; } = string.Empty;

    [JsonPropertyName("subscription_status")]
    public string? SubscriptionStatus { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }

    [JsonPropertyName("issued_at")]
    public string? IssuedAt { get; init; }
}

public record OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("cloud_config")]
    public OAuthCloudConfig? CloudConfig { get; init; }
}

public record OAuthCloudConfig
{
    [JsonPropertyName("api_base_url")]
    public string ApiBaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("s3_bucket")]
    public string S3Bucket { get; init; } = string.Empty;

    [JsonPropertyName("s3_region")]
    public string S3Region { get; init; } = string.Empty;

    [JsonPropertyName("s3_endpoint_override")]
    public string? S3EndpointOverride { get; init; }

    [JsonPropertyName("credential_refresh_margin_secs")]
    public int CredentialRefreshMarginSecs { get; init; }

    [JsonPropertyName("poll_interval_secs")]
    public int PollIntervalSecs { get; init; }
}

public record OfficialPluginsResponse
{
    [JsonPropertyName("plugins")]
    public List<Models.PluginRegistry.OfficialPluginInfo>? Plugins { get; init; }
}

public record CloudWorkspacesResponse
{
    [JsonPropertyName("workspaces")]
    public List<CloudWorkspaceInfo>? Workspaces { get; init; }
}

public record TrialResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("license_key")]
    public string? LicenseKey { get; init; }

    [JsonPropertyName("trial_days")]
    public int TrialDays { get; init; }

    [JsonPropertyName("requires_verification")]
    public bool RequiresVerification { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("user_id")]
    public long? UserId { get; init; }

    [JsonPropertyName("cloud_config")]
    public OAuthCloudConfig? CloudConfig { get; init; }
}
