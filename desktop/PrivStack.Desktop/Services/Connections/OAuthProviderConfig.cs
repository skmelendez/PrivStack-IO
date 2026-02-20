namespace PrivStack.Desktop.Services.Connections;

/// <summary>
/// OAuth2 provider configuration for shell-level connections.
/// Contains endpoints, combined scopes, and client IDs for Google and Microsoft.
/// </summary>
public sealed record OAuthProviderConfig
{
    public required string ProviderId { get; init; }
    public required string ProviderDisplayName { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string AuthorizeEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    public required string Scopes { get; init; }

    /// <summary>
    /// Returns the OAuth config for a provider ID, or null if not supported.
    /// </summary>
    public static OAuthProviderConfig? ForProvider(string providerId) => providerId switch
    {
        "google" => Google,
        "microsoft" => Microsoft,
        _ => null
    };

    /// <summary>
    /// Whether a provider supports shell-level OAuth connections.
    /// </summary>
    public static bool IsOAuthProvider(string providerId) =>
        providerId is "google" or "microsoft";

    // ── Google ───────────────────────────────────────────────────────
    // Combined scopes: Gmail + Calendar + identity
    public static readonly OAuthProviderConfig Google = new()
    {
        ProviderId = "google",
        ProviderDisplayName = "Google",
        ClientId = Environment.GetEnvironmentVariable("PRIVSTACK_GOOGLE_CLIENT_ID")
            ?? throw new InvalidOperationException("PRIVSTACK_GOOGLE_CLIENT_ID env var not set"),
        ClientSecret = Environment.GetEnvironmentVariable("PRIVSTACK_GOOGLE_CLIENT_SECRET"),
        AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth2.googleapis.com/token",
        Scopes = "https://mail.google.com/ https://www.googleapis.com/auth/calendar email openid",
    };

    // ── Microsoft ───────────────────────────────────────────────────
    // Combined scopes: Outlook IMAP/SMTP + identity
    public static readonly OAuthProviderConfig Microsoft = new()
    {
        ProviderId = "microsoft",
        ProviderDisplayName = "Microsoft",
        ClientId = Environment.GetEnvironmentVariable("PRIVSTACK_MICROSOFT_CLIENT_ID")
            ?? throw new InvalidOperationException("PRIVSTACK_MICROSOFT_CLIENT_ID env var not set"),
        AuthorizeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        Scopes = "https://outlook.office365.com/IMAP.AccessAsUser.All https://outlook.office365.com/SMTP.Send offline_access openid email",
    };
}
