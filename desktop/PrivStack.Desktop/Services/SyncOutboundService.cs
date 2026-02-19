using System.Collections.Concurrent;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Bridges local entity mutations to the P2P sync engine.
/// Immediately registers entities for sync (cheap HashSet insert in Rust),
/// then debounces snapshot recordings to avoid flooding during rapid edits.
/// Also forwards events to file-based sync when active.
/// </summary>
internal sealed class SyncOutboundService : ISyncOutboundService, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<SyncOutboundService>();
    private const int DebounceMs = 2000;

    /// <summary>
    /// Entity types excluded from all sync channels by default. These are fetchable from
    /// external sources (e.g. IMAP) and only waste storage/bandwidth if synced.
    /// Cross-plugin links to these entities are stored on the linking entity, which
    /// IS synced — so the reference is preserved across devices.
    /// Use <see cref="PromoteEntityForSync"/> to bypass this exclusion for linked entities.
    /// </summary>
    internal static readonly HashSet<string> SyncExcludedTypes = ["email_message", "email_folder"];

    private readonly ISyncService _syncService;
    private readonly ICloudSyncService _cloudSync;
    private readonly ConcurrentDictionary<string, DebounceEntry> _pending = new();
    private IFileEventSyncService? _fileEventSync;
    private bool _disposed;

    public SyncOutboundService(ISyncService syncService, ICloudSyncService cloudSync)
    {
        _syncService = syncService;
        _cloudSync = cloudSync;
    }

    /// <summary>
    /// Wires in the file-based event sync service for cloud/NAS sync.
    /// Called after DI container build to resolve circular dependency.
    /// </summary>
    public void SetFileEventSync(IFileEventSyncService service)
    {
        _fileEventSync = service;
    }

    public void NotifyEntityChanged(string entityId, string entityType, string? payload)
    {
        if (_disposed) return;

        // Skip all sync for excluded entity types (e.g. email — fetched from IMAP per-device)
        if (SyncExcludedTypes.Contains(entityType)) return;

        EnqueueSync(entityId, entityType, payload);
    }

    public void PromoteEntityForSync(string entityId, string entityType, string? payload)
    {
        if (_disposed) return;

        // Bypasses the SyncExcludedTypes check — used when a normally-excluded entity
        // gets linked to a synced entity and needs to be pushed once.
        EnqueueSync(entityId, entityType, payload);
    }

    private void EnqueueSync(string entityId, string entityType, string? payload)
    {
        // Register for sync immediately (idempotent HashSet insert, very cheap)
        try
        {
            _syncService.ShareDocumentForSync(entityId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to share document {EntityId} for sync", entityId);
            return;
        }

        // No payload means we can't record a snapshot (e.g., Delete returns no body)
        if (string.IsNullOrEmpty(payload)) return;

        // Debounce the snapshot recording: reset timer if entity already pending
        _pending.AddOrUpdate(
            entityId,
            _ => CreateEntry(entityId, entityType, payload),
            (_, existing) =>
            {
                existing.EntityType = entityType;
                existing.Payload = payload;
                existing.Timer.Change(DebounceMs, Timeout.Infinite);
                return existing;
            });
    }

    public void CancelAll()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var entry))
            {
                entry.Timer.Dispose();
            }
        }
    }

    private DebounceEntry CreateEntry(string entityId, string entityType, string payload)
    {
        var entry = new DebounceEntry
        {
            EntityType = entityType,
            Payload = payload,
        };
        entry.Timer = new Timer(_ => OnDebounceElapsed(entityId), null, DebounceMs, Timeout.Infinite);
        return entry;
    }

    private void OnDebounceElapsed(string entityId)
    {
        if (!_pending.TryRemove(entityId, out var entry)) return;

        try
        {
            entry.Timer.Dispose();
            _syncService.RecordSyncSnapshot(entityId, entry.EntityType, entry.Payload);
            _log.Debug("Recorded sync snapshot for {EntityType} {EntityId}", entry.EntityType, entityId);

            // Also write to file-based event store if active (cloud/NAS sync)
            _fileEventSync?.WriteEventFile(entityId, entry.EntityType, entry.Payload);

            // Push to cloud sync engine if running
            if (_cloudSync.IsSyncing)
            {
                try
                {
                    _cloudSync.PushEvent(entityId, entry.EntityType, entry.Payload);
                }
                catch (Exception cloudEx)
                {
                    _log.Warning(cloudEx, "Failed to push cloud event for {EntityId}", entityId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to record sync snapshot for {EntityId}", entityId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
    }

    private sealed class DebounceEntry
    {
        public Timer Timer { get; set; } = null!;
        public string EntityType { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }
}
