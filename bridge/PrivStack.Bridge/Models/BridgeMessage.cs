using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivStack.Bridge.Models;

/// <summary>
/// Message sent from the browser extension through the bridge to the desktop app.
/// </summary>
public sealed record BridgeRequest
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("action")] public string Action { get; init; } = "";
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
}

/// <summary>
/// Response from the desktop app back through the bridge to the extension.
/// </summary>
public sealed record BridgeResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; init; }
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; init; }
}
