using System.Text.Json.Serialization;

namespace PrivStack.Desktop.Models;

/// <summary>
/// License key information.
/// </summary>
public record LicenseInfo
{
    [JsonPropertyName("raw")]
    public string Raw { get; init; } = string.Empty;

    [JsonPropertyName("plan")]
    public string Plan { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("sub")]
    public long Sub { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("issued_at_ms")]
    public long IssuedAtMs { get; init; }

    [JsonPropertyName("expires_at_ms")]
    public long? ExpiresAtMs { get; init; }

    [JsonPropertyName("grace_days_remaining")]
    public int? GraceDaysRemaining { get; init; }

    /// <summary>
    /// Gets the issue date.
    /// </summary>
    public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeMilliseconds(IssuedAtMs);

    /// <summary>
    /// Gets the expiration date if applicable.
    /// </summary>
    public DateTimeOffset? ExpiresAt => ExpiresAtMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs.Value)
        : null;
}

/// <summary>
/// License activation information.
/// </summary>
public record ActivationInfo
{
    [JsonPropertyName("license_key")]
    public string LicenseKey { get; init; } = string.Empty;

    [JsonPropertyName("plan")]
    public string Plan { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("sub")]
    public long Sub { get; init; }

    [JsonPropertyName("activated_at_ms")]
    public long ActivatedAtMs { get; init; }

    [JsonPropertyName("expires_at_ms")]
    public long? ExpiresAtMs { get; init; }

    [JsonPropertyName("device_fingerprint")]
    public string DeviceFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("is_valid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("grace_days_remaining")]
    public int? GraceDaysRemaining { get; init; }

    /// <summary>
    /// Gets the activation date.
    /// </summary>
    public DateTimeOffset ActivatedAt => DateTimeOffset.FromUnixTimeMilliseconds(ActivatedAtMs);

    /// <summary>
    /// Gets the expiration date if applicable.
    /// </summary>
    public DateTimeOffset? ExpiresAt => ExpiresAtMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs.Value)
        : null;
}

/// <summary>
/// Device information for licensing.
/// </summary>
public record DeviceInfo
{
    [JsonPropertyName("os_name")]
    public string OsName { get; init; } = string.Empty;

    [JsonPropertyName("os_version")]
    public string OsVersion { get; init; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; init; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>
/// Server-side license validation response from /api/license/activate.
/// </summary>
public record LicenseValidationResponse
{
    [JsonPropertyName("valid")] public bool Valid { get; init; }
    [JsonPropertyName("plan")] public string Plan { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}
