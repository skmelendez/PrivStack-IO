using System.Text.Json;
using PrivStack.Desktop.Models;
using PrivStack.Sdk;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Reads all entities from the local database via SdkHost and
/// assembles them into a <see cref="SyncSnapshotData"/> for serialization.
/// Uses existing read_list FFI — no new Rust code needed.
/// </summary>
internal sealed class SnapshotExporter
{
    private static readonly ILogger _log = Log.ForContext<SnapshotExporter>();

    private readonly IPrivStackSdk _sdk;
    private readonly string _workspaceId;

    public SnapshotExporter(IPrivStackSdk sdk, string workspaceId)
    {
        _sdk = sdk;
        _workspaceId = workspaceId;
    }

    /// <summary>
    /// Exports all entities from every known entity type into a snapshot.
    /// </summary>
    public async Task<SyncSnapshotData> ExportAsync(CancellationToken ct = default)
    {
        var snapshot = new SyncSnapshotData
        {
            Version = 1,
            WorkspaceId = _workspaceId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        foreach (var typeInfo in EntityTypeMap.All)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var items = await ReadAllEntities(typeInfo.EntityType, ct);
                if (items.Count > 0)
                {
                    snapshot.EntityTypes.Add(new EntityTypeSnapshot
                    {
                        EntityType = typeInfo.EntityType,
                        Items = items,
                    });
                    _log.Debug("Snapshot: exported {Count} {Type} entities", items.Count, typeInfo.EntityType);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Snapshot: failed to export {Type} — skipping", typeInfo.EntityType);
            }
        }

        var totalEntities = snapshot.EntityTypes.Sum(e => e.Items.Count);
        _log.Information("Snapshot export complete: {Types} types, {Total} entities",
            snapshot.EntityTypes.Count, totalEntities);

        return snapshot;
    }

    private async Task<List<string>> ReadAllEntities(string entityType, CancellationToken ct)
    {
        var response = await _sdk.SendAsync<JsonElement>(new SdkMessage
        {
            PluginId = "system",
            Action = SdkAction.ReadList,
            EntityType = entityType,
            Parameters = new Dictionary<string, string> { ["include_trashed"] = "true" },
        }, ct);

        if (!response.Success || response.Data.ValueKind == JsonValueKind.Undefined)
            return [];

        var items = new List<string>();
        if (response.Data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in response.Data.EnumerateArray())
            {
                items.Add(element.GetRawText());
            }
        }

        return items;
    }
}
