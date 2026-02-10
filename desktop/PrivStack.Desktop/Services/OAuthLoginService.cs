using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Handles OAuth2 Authorization Code Flow with PKCE for desktop login.
/// Opens the system browser, listens on a loopback port for the callback,
/// and returns the authorization code.
/// </summary>
public sealed class OAuthLoginService : IDisposable
{
    private HttpListener? _listener;

    /// <summary>
    /// Generates a cryptographically random code verifier (RFC 7636).
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Computes the S256 code challenge from a code verifier.
    /// </summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Generates a random state parameter for CSRF protection.
    /// </summary>
    public static string GenerateState()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>
    /// Opens the authorize URL in the system browser, starts a loopback HTTP listener,
    /// and waits for the OAuth callback with the authorization code.
    /// </summary>
    /// <param name="authorizeUrl">The full authorize URL (without redirect_uri — it will be appended).</param>
    /// <param name="expectedState">The state value to validate on callback.</param>
    /// <param name="ct">Cancellation token (e.g., from user clicking Cancel).</param>
    /// <returns>The callback result containing the authorization code.</returns>
    public async Task<OAuthCallbackResult> AuthorizeAsync(string authorizeUrl, string expectedState, CancellationToken ct)
    {
        // Get an ephemeral port
        var port = GetEphemeralPort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        // Start HTTP listener
        _listener = new HttpListener();
        _listener.Prefixes.Add(redirectUri);
        _listener.Start();

        // Build final URL with redirect_uri and open browser
        var separator = authorizeUrl.Contains('?') ? '&' : '?';
        var fullUrl = $"{authorizeUrl}{separator}redirect_uri={Uri.EscapeDataString(redirectUri)}";

        Process.Start(new ProcessStartInfo(fullUrl) { UseShellExecute = true });

        // Wait for callback with 5-minute timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var context = await _listener.GetContextAsync().WaitAsync(linkedCts.Token);
            var query = context.Request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];

            if (!string.IsNullOrEmpty(error))
            {
                await ServeResponseAsync(context, false, "Authorization was denied.");
                throw new OAuthException($"Authorization error: {error}");
            }

            if (string.IsNullOrEmpty(code))
            {
                await ServeResponseAsync(context, false, "No authorization code received.");
                throw new OAuthException("No authorization code received from callback.");
            }

            if (state != expectedState)
            {
                await ServeResponseAsync(context, false, "Security validation failed.");
                throw new OAuthException("State parameter mismatch — possible CSRF attack.");
            }

            await ServeResponseAsync(context, true, "Authorization successful! You can close this tab and return to PrivStack.");

            return new OAuthCallbackResult(code, state, redirectUri);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new OAuthException("Authorization timed out. Please try again.");
        }
        finally
        {
            StopListener();
        }
    }

    private static async Task ServeResponseAsync(HttpListenerContext context, bool success, string message)
    {
        var color = success ? "#22c55e" : "#ef4444";
        var icon = success ? "&#10003;" : "&#10007;";
        var html = $"""
            <!DOCTYPE html>
            <html>
            <head><title>PrivStack Authorization</title></head>
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

    private void StopListener()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // Ignore cleanup errors
        }
        _listener = null;
    }

    public void Dispose()
    {
        StopListener();
    }
}

/// <summary>
/// Result from a successful OAuth callback.
/// </summary>
public record OAuthCallbackResult(string Code, string State, string RedirectUri);

/// <summary>
/// Exception for OAuth flow errors.
/// </summary>
public class OAuthException : Exception
{
    public OAuthException(string message) : base(message) { }
}
