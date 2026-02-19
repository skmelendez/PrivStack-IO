namespace PrivStack.Sdk;

/// <summary>
/// Primary interface for plugin data operations. Plugins send structured messages
/// to the host which routes them to the Rust core via FFI.
/// </summary>
public interface IPrivStackSdk
{
    /// <summary>
    /// Whether the SDK backend is initialized and ready to accept requests.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Sends a message and deserializes the response payload into <typeparamref name="TResult"/>.
    /// </summary>
    Task<SdkResponse<TResult>> SendAsync<TResult>(SdkMessage message, CancellationToken ct = default);

    /// <summary>
    /// Sends a message that returns no payload (create, update, delete).
    /// </summary>
    Task<SdkResponse> SendAsync(SdkMessage message, CancellationToken ct = default);

    /// <summary>
    /// Returns the total count of entities of the given type.
    /// </summary>
    Task<int> CountAsync(string pluginId, string entityType, bool includeTrashed = false, CancellationToken ct = default);

    /// <summary>
    /// Searches across all registered entity types.
    /// </summary>
    Task<SdkResponse<TResult>> SearchAsync<TResult>(string query, string[]? entityTypes = null, int limit = 50, CancellationToken ct = default);

    // =========================================================================
    // Database Maintenance
    // =========================================================================

    /// <summary>
    /// Runs database maintenance (WAL checkpoint + vacuum) to reclaim disk space.
    /// </summary>
    Task RunDatabaseMaintenance(CancellationToken ct = default);

    /// <summary>
    /// Returns per-table database diagnostics as JSON (row counts, estimated sizes).
    /// </summary>
    string GetDatabaseDiagnostics();

    /// <summary>
    /// Finds orphan entities whose (plugin_id, entity_type) don't match any registered schema.
    /// Takes JSON array of valid types, returns JSON array of orphan summaries.
    /// </summary>
    string FindOrphanEntities(string validTypesJson);

    /// <summary>
    /// Deletes orphan entities and cascades to auxiliary tables. Returns JSON with deleted count.
    /// </summary>
    string DeleteOrphanEntities(string validTypesJson);

    // =========================================================================
    // Vault (Encrypted Blob Storage)
    // =========================================================================

    Task<bool> VaultIsInitialized(string vaultId, CancellationToken ct = default);
    Task VaultInitialize(string vaultId, string password, CancellationToken ct = default);
    Task VaultUnlock(string vaultId, string password, CancellationToken ct = default);
    Task VaultLock(string vaultId, CancellationToken ct = default);
    Task<bool> VaultIsUnlocked(string vaultId, CancellationToken ct = default);

    /// <summary>
    /// Requests the host to prompt the user for their master password and unlock
    /// the specified vault. Returns true if the vault was successfully unlocked.
    /// Plugins should call this when a vault operation fails due to a locked vault
    /// instead of implementing their own unlock UI.
    /// </summary>
    Task<bool> RequestVaultUnlockAsync(string vaultId, CancellationToken ct = default);

    Task VaultBlobStore(string vaultId, string blobId, byte[] data, CancellationToken ct = default);
    Task<byte[]> VaultBlobRead(string vaultId, string blobId, CancellationToken ct = default);
    Task VaultBlobDelete(string vaultId, string blobId, CancellationToken ct = default);

    // =========================================================================
    // Blob (Unencrypted Blob Storage)
    // =========================================================================

    Task BlobStore(string ns, string blobId, byte[] data, string? metadataJson = null, CancellationToken ct = default);
    Task<byte[]> BlobRead(string ns, string blobId, CancellationToken ct = default);
    Task BlobDelete(string ns, string blobId, CancellationToken ct = default);
}
