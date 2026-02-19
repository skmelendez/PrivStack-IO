namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Notifies the sync engine when local entities are created, updated, or deleted.
/// Called from SdkHost after successful mutations to register entities for P2P sync.
/// </summary>
public interface ISyncOutboundService
{
    /// <summary>
    /// Registers an entity for sync and debounces a snapshot recording.
    /// Safe to call rapidly — snapshots are coalesced with a 2-second trailing timer per entity.
    /// </summary>
    void NotifyEntityChanged(string entityId, string entityType, string? payload);

    /// <summary>
    /// Syncs an entity that is normally excluded (e.g. email) because it was linked
    /// to a synced entity. Bypasses the SyncExcludedTypes check.
    /// </summary>
    void PromoteEntityForSync(string entityId, string entityType, string? payload);

    /// <summary>
    /// Cancels all pending debounce timers. Called during workspace switches.
    /// </summary>
    void CancelAll();
}
