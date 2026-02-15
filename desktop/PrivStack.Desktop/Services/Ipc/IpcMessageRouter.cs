using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using PrivStack.Sdk.Json;
using Serilog;

namespace PrivStack.Desktop.Services.Ipc;

/// <summary>
/// Routes incoming IPC messages to the appropriate service handler.
/// Validates auth tokens and dispatches by action prefix.
/// </summary>
public sealed class IpcMessageRouter
{
    private static readonly ILogger _log = Log.ForContext<IpcMessageRouter>();
    private static readonly JsonSerializerOptions _json = SdkJsonOptions.Default;

    private readonly IPrivStackSdk _sdk;
    private readonly IAppSettingsService _settings;

    public IpcMessageRouter(IPrivStackSdk sdk, IAppSettingsService settings)
    {
        _sdk = sdk;
        _settings = settings;
    }

    public async Task<string> RouteAsync(string requestJson, CancellationToken ct)
    {
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(requestJson, _json);
        }
        catch (JsonException ex)
        {
            _log.Warning(ex, "IPC: malformed request JSON");
            return ErrorResponse(null, "parse_error", "Malformed JSON");
        }

        if (request == null)
            return ErrorResponse(null, "parse_error", "Null request");

        // Validate auth token
        var expectedToken = _settings.Settings.BridgeAuthToken;
        if (!string.IsNullOrEmpty(expectedToken) && !ConstantTimeEquals(request.Token, expectedToken))
        {
            _log.Warning("IPC: invalid auth token for action {Action}", request.Action);
            return ErrorResponse(request.Id, "auth_error", "Invalid token");
        }

        try
        {
            return request.Action switch
            {
                "ping" => SuccessResponse(request.Id, new { status = "ok" }),
                "clip.save" => await HandleClipSaveAsync(request, ct),
                "clip.snapshot" => await HandleClipSnapshotAsync(request, ct),
                "readlater.save" => await HandleReadLaterSaveAsync(request, ct),
                "readlater.sync_scroll" => await HandleScrollSyncAsync(request, ct),
                "entity.search" => await HandleEntitySearchAsync(request, ct),
                "entity.get_index" => await HandleEntityIndexAsync(request, ct),
                "snip.save" => await HandleSnipSaveAsync(request, ct),
                "transcribe.enqueue" => await HandleTranscribeAsync(request, ct),
                "history.batch_sync" => await HandleHistorySyncAsync(request, ct),
                _ => ErrorResponse(request.Id, "unknown_action", $"Unknown action: {request.Action}"),
            };
        }
        catch (Exception ex)
        {
            _log.Error(ex, "IPC: handler error for action {Action}", request.Action);
            return ErrorResponse(request.Id, "handler_error", ex.Message);
        }
    }

    private async Task<string> HandleClipSaveAsync(BridgeRequest req, CancellationToken ct)
    {
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "privstack.webclips",
            Action = SdkAction.Create,
            EntityType = "web_clip",
            Payload = req.Payload.Value.GetRawText(),
        }, ct);

        return response.Success
            ? SuccessResponse(req.Id, response.Data)
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Create failed");
    }

    private async Task<string> HandleClipSnapshotAsync(BridgeRequest req, CancellationToken ct)
    {
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        // Extract base64 image data and metadata
        var payload = req.Payload.Value;
        var imageData = payload.TryGetProperty("image_data", out var imgProp) ? imgProp.GetString() : null;
        if (string.IsNullOrEmpty(imageData))
            return ErrorResponse(req.Id, "missing_image", "image_data required");

        var blobId = Guid.NewGuid().ToString();
        var imageBytes = Convert.FromBase64String(imageData);
        await _sdk.BlobStore("webclips", blobId, imageBytes, ct: ct);

        // Create the clip entity with blob reference
        var clipPayload = new
        {
            id = Guid.NewGuid().ToString(),
            title = payload.TryGetProperty("title", out var t) ? t.GetString() : "Snapshot",
            url = payload.TryGetProperty("url", out var u) ? u.GetString() : "",
            domain = payload.TryGetProperty("domain", out var d) ? d.GetString() : "",
            content = "",
            clip_type = "snapshot",
            status = "unread",
            blob_id = blobId,
            clipped_at = DateTimeOffset.UtcNow.ToString("o"),
            tags = Array.Empty<string>(),
        };

        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "privstack.webclips",
            Action = SdkAction.Create,
            EntityType = "web_clip",
            Payload = JsonSerializer.Serialize(clipPayload, _json),
        }, ct);

        return response.Success
            ? SuccessResponse(req.Id, response.Data)
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Snapshot failed");
    }

    private async Task<string> HandleReadLaterSaveAsync(BridgeRequest req, CancellationToken ct)
    {
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        // Set status to "unread" for read-later items
        var raw = req.Payload.Value;
        var clipPayload = new
        {
            id = raw.TryGetProperty("id", out var idProp) ? idProp.GetString() : Guid.NewGuid().ToString(),
            title = raw.TryGetProperty("title", out var t) ? t.GetString() : "",
            url = raw.TryGetProperty("url", out var u) ? u.GetString() : "",
            domain = raw.TryGetProperty("domain", out var d) ? d.GetString() : "",
            content = raw.TryGetProperty("content", out var c) ? c.GetString() : "",
            clip_type = "article",
            status = "unread",
            scroll_position = 0.0,
            clipped_at = DateTimeOffset.UtcNow.ToString("o"),
            tags = Array.Empty<string>(),
        };

        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "privstack.webclips",
            Action = SdkAction.Create,
            EntityType = "web_clip",
            Payload = JsonSerializer.Serialize(clipPayload, _json),
        }, ct);

        return response.Success
            ? SuccessResponse(req.Id, response.Data)
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Save failed");
    }

    private async Task<string> HandleScrollSyncAsync(BridgeRequest req, CancellationToken ct)
    {
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        var raw = req.Payload.Value;
        var entityId = raw.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(entityId))
            return ErrorResponse(req.Id, "missing_id", "Entity id required");

        var scrollPos = raw.TryGetProperty("scroll_position", out var sp) ? sp.GetDouble() : 0.0;
        var updatePayload = new { scroll_position = scrollPos, status = "reading" };

        var response = await _sdk.SendAsync(new SdkMessage
        {
            PluginId = "privstack.webclips",
            Action = SdkAction.Update,
            EntityType = "web_clip",
            EntityId = entityId,
            Payload = JsonSerializer.Serialize(updatePayload, _json),
        }, ct);

        return response.Success
            ? SuccessResponse(req.Id, new { updated = true })
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Scroll sync failed");
    }

    private async Task<string> HandleEntitySearchAsync(BridgeRequest req, CancellationToken ct)
    {
        var raw = req.Payload?.ValueKind == JsonValueKind.Object ? req.Payload.Value : default;
        var query = raw.ValueKind != JsonValueKind.Undefined && raw.TryGetProperty("query", out var q)
            ? q.GetString() ?? "" : "";
        var limit = raw.ValueKind != JsonValueKind.Undefined && raw.TryGetProperty("limit", out var l)
            ? l.GetInt32() : 50;

        var response = await _sdk.SearchAsync<List<JsonElement>>(query, limit: limit, ct: ct);

        return response.Success
            ? SuccessResponse(req.Id, response.Data)
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Search failed");
    }

    private async Task<string> HandleEntityIndexAsync(BridgeRequest req, CancellationToken ct)
    {
        // Return a lightweight index of all entity names/types for the overlay
        var response = await _sdk.SearchAsync<List<JsonElement>>("", limit: 5000, ct: ct);
        if (!response.Success)
            return ErrorResponse(req.Id, "sdk_error", response.ErrorMessage ?? "Index failed");

        var terms = new List<object>();
        if (response.Data != null)
        {
            foreach (var item in response.Data)
            {
                var id = item.TryGetProperty("id", out var idP) ? idP.GetString() : null;
                var title = item.TryGetProperty("title", out var tP) ? tP.GetString()
                    : item.TryGetProperty("name", out var nP) ? nP.GetString()
                    : item.TryGetProperty("display_name", out var dnP) ? dnP.GetString() : null;
                var entityType = item.TryGetProperty("entity_type", out var etP) ? etP.GetString() : null;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                    terms.Add(new { id, title, entity_type = entityType });
            }
        }

        return SuccessResponse(req.Id, terms);
    }

    private async Task<string> HandleSnipSaveAsync(BridgeRequest req, CancellationToken ct)
    {
        // Same flow as snapshot but with clip_type = "snip"
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        var payload = req.Payload.Value;
        var imageData = payload.TryGetProperty("image_data", out var imgProp) ? imgProp.GetString() : null;
        if (string.IsNullOrEmpty(imageData))
            return ErrorResponse(req.Id, "missing_image", "image_data required");

        var blobId = Guid.NewGuid().ToString();
        var imageBytes = Convert.FromBase64String(imageData);
        await _sdk.BlobStore("webclips", blobId, imageBytes, ct: ct);

        var clipPayload = new
        {
            id = Guid.NewGuid().ToString(),
            title = payload.TryGetProperty("title", out var t) ? t.GetString() : "Visual Snip",
            url = payload.TryGetProperty("url", out var u) ? u.GetString() : "",
            domain = payload.TryGetProperty("domain", out var d) ? d.GetString() : "",
            content = payload.TryGetProperty("annotation", out var a) ? a.GetString() : "",
            clip_type = "snip",
            status = "unread",
            blob_id = blobId,
            clipped_at = DateTimeOffset.UtcNow.ToString("o"),
            tags = Array.Empty<string>(),
        };

        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "privstack.webclips",
            Action = SdkAction.Create,
            EntityType = "web_clip",
            Payload = JsonSerializer.Serialize(clipPayload, _json),
        }, ct);

        return response.Success
            ? SuccessResponse(req.Id, response.Data)
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Snip failed");
    }

    private async Task<string> HandleTranscribeAsync(BridgeRequest req, CancellationToken ct)
    {
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        var raw = req.Payload.Value;
        var jobPayload = new
        {
            id = Guid.NewGuid().ToString(),
            source_url = raw.TryGetProperty("source_url", out var u) ? u.GetString() : "",
            title = raw.TryGetProperty("title", out var t) ? t.GetString() : "Transcription",
            transcript = "",
            status = "queued",
            error_message = "",
            created_at = DateTimeOffset.UtcNow.ToString("o"),
            completed_at = "",
        };

        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "privstack.webclips",
            Action = SdkAction.Create,
            EntityType = "transcription_job",
            Payload = JsonSerializer.Serialize(jobPayload, _json),
        }, ct);

        return response.Success
            ? SuccessResponse(req.Id, response.Data)
            : ErrorResponse(req.Id, response.ErrorCode ?? "sdk_error", response.ErrorMessage ?? "Enqueue failed");
    }

    private async Task<string> HandleHistorySyncAsync(BridgeRequest req, CancellationToken ct)
    {
        if (req.Payload == null) return ErrorResponse(req.Id, "missing_payload", "Payload required");

        var entries = JsonSerializer.Deserialize<List<JsonElement>>(req.Payload.Value.GetRawText(), _json);
        if (entries == null || entries.Count == 0)
            return SuccessResponse(req.Id, new { synced = 0 });

        var synced = 0;
        foreach (var entry in entries)
        {
            var response = await _sdk.SendAsync(new SdkMessage
            {
                PluginId = "privstack.webclips",
                Action = SdkAction.Create,
                EntityType = "browsing_history",
                Payload = entry.GetRawText(),
            }, ct);

            if (response.Success) synced++;
        }

        return SuccessResponse(req.Id, new { synced });
    }

    private static string SuccessResponse(string? id, object? data) =>
        JsonSerializer.Serialize(new { id, success = true, data }, _json);

    private static string ErrorResponse(string? id, string code, string message) =>
        JsonSerializer.Serialize(new { id, success = false, error_code = code, error_message = message }, _json);

    private static bool ConstantTimeEquals(string? a, string? b)
    {
        if (a == null || b == null) return a == b;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}

/// <summary>
/// Incoming message from the bridge process.
/// </summary>
public sealed record BridgeRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("action")]
    public string Action { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("token")]
    public string? Token { get; init; }
}
