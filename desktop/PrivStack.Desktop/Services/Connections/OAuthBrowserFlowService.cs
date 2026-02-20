using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Serilog;

namespace PrivStack.Desktop.Services.Connections;

/// <summary>
/// Generic PKCE Authorization Code Flow with loopback listener.
/// Used by ConnectionService to authorize Google and Microsoft OAuth connections.
/// </summary>
public sealed class OAuthBrowserFlowService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OAuthBrowserFlowService>();
    private static readonly HttpClient Http = new();

    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateState()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>
    /// Opens the system browser, listens on a loopback port, returns the auth code + redirect URI.
    /// </summary>
    public async Task<OAuthCodeResult> AuthorizeAsync(
        OAuthProviderConfig config, CancellationToken ct)
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = GenerateState();

        var port = GetEphemeralPort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authorizeUrl = $"{config.AuthorizeEndpoint}" +
                           $"?client_id={Uri.EscapeDataString(config.ClientId)}" +
                           $"&response_type=code" +
                           $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                           $"&scope={Uri.EscapeDataString(config.Scopes)}" +
                           $"&state={Uri.EscapeDataString(state)}" +
                           $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                           $"&code_challenge_method=S256" +
                           $"&access_type=offline" +
                           $"&prompt=consent";

        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(linkedCts.Token);
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];
            var error = query["error"];

            if (!string.IsNullOrEmpty(error))
            {
                await ServeResponseAsync(context, false, "Authorization was denied.");
                throw new InvalidOperationException($"OAuth authorization error: {error}");
            }

            if (string.IsNullOrEmpty(code))
            {
                await ServeResponseAsync(context, false, "No authorization code received.");
                throw new InvalidOperationException("No authorization code received from callback.");
            }

            if (returnedState != state)
            {
                await ServeResponseAsync(context, false, "Security validation failed.");
                throw new InvalidOperationException("State parameter mismatch — possible CSRF attack.");
            }

            await ServeResponseAsync(context, true,
                "Authorization successful! You can close this tab and return to PrivStack.");

            return new OAuthCodeResult(code, redirectUri, codeVerifier);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("OAuth authorization timed out. Please try again.");
        }
    }

    /// <summary>
    /// Exchange an authorization code for tokens.
    /// </summary>
    public async Task<OAuthTokenResponse> ExchangeCodeAsync(
        OAuthProviderConfig config, string code, string redirectUri,
        string codeVerifier, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        };

        if (!string.IsNullOrEmpty(config.ClientSecret))
            parameters["client_secret"] = config.ClientSecret;

        var content = new FormUrlEncodedContent(parameters);
        var response = await Http.PostAsync(config.TokenEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(ct);
        return token ?? throw new InvalidOperationException("Empty token response from provider.");
    }

    /// <summary>
    /// Refresh an expired access token using a refresh token.
    /// </summary>
    public async Task<OAuthTokenResponse> RefreshTokenAsync(
        OAuthProviderConfig config, string refreshToken, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        };

        if (!string.IsNullOrEmpty(config.ClientSecret))
            parameters["client_secret"] = config.ClientSecret;

        var content = new FormUrlEncodedContent(parameters);
        var response = await Http.PostAsync(config.TokenEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(ct);
        return token ?? throw new InvalidOperationException("Empty token response from provider.");
    }

    /// <summary>
    /// Extract email and name claims from a JWT id_token (without signature validation).
    /// </summary>
    public static (string? Email, string? Name) ExtractClaimsFromIdToken(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return (null, null);

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
            var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

            return (email, name);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task ServeResponseAsync(HttpListenerContext context, bool success, string message)
    {
        var color = success ? "#22c55e" : "#ef4444";
        var icon = success ? "&#10003;" : "&#10007;";
        var html = $"""
            <!DOCTYPE html>
            <html>
            <head><title>PrivStack - Authorization</title></head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; display: flex; justify-content: center; align-items: center; min-height: 100vh; margin: 0; background: #0f0f0f; color: #e0e0e0;">
              <div style="text-align: center; padding: 2rem;">
                <div style="font-size: 3rem; color: {color}; margin-bottom: 1rem;">{icon}</div>
                <p style="font-size: 1.1rem;">{message}</p>
              </div>
            </body>
            </html>
            """;

        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
    }

    private static int GetEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>
/// Result from a successful OAuth browser callback, including the code verifier for PKCE exchange.
/// </summary>
public record OAuthCodeResult(string Code, string RedirectUri, string CodeVerifier);

/// <summary>
/// OAuth2 token endpoint response.
/// </summary>
public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}
