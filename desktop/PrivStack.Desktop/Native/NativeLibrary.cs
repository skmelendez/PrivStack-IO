using System.Runtime.InteropServices;

namespace PrivStack.Desktop.Native;

/// <summary>
/// P/Invoke bindings for the native PrivStack Rust library.
/// Contains only lifecycle, auth, sync, cloud, license, and the generic SDK execute endpoint.
/// Domain-specific FFI calls are handled via the generic Execute() entry point.
/// </summary>
internal static partial class NativeLibrary
{
    private const string LibraryName = "privstack_ffi";

    /// <summary>
    /// Initializes the PrivStack runtime with the given database path.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_init", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError Init(string dbPath);

    /// <summary>
    /// Shuts down the PrivStack runtime and frees resources.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_shutdown")]
    public static partial void Shutdown();

    /// <summary>
    /// Returns the library version as a string.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_version")]
    public static partial nint Version();

    /// <summary>
    /// Frees a string allocated by the native library.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_free_string")]
    public static partial void FreeString(nint str);

    /// <summary>
    /// Frees bytes allocated by native library (cloud download, file content, etc.).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_free_bytes")]
    public static partial void FreeBytes(nint data, nuint len);

    // =========================================================================
    // Generic SDK Execute Endpoint
    // =========================================================================

    /// <summary>
    /// Generic JSON-in/JSON-out endpoint for SDK operations.
    /// All domain CRUD is routed through this single entry point.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_execute", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Execute(string requestJson);

    // =========================================================================
    // Entity Registration & Search
    // =========================================================================

    /// <summary>
    /// Registers a new entity type schema with the core engine.
    /// Returns 0 on success, negative on error.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_register_entity_type", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RegisterEntityType(string schemaJson);

    /// <summary>
    /// Cross-plugin search across all registered entity types.
    /// Returns a JSON response string pointer (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_search", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Search(string queryJson);

    // =========================================================================
    // App-Level Authentication
    // =========================================================================

    /// <summary>
    /// Checks if the app master password has been initialized.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_is_initialized")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AuthIsInitialized();

    /// <summary>
    /// Checks if the app is currently unlocked.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_is_unlocked")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AuthIsUnlocked();

    /// <summary>
    /// Initializes the app with a master password (first-time setup).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_initialize", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError AuthInitialize(string masterPassword);

    /// <summary>
    /// Unlocks the app with the master password.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_unlock", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError AuthUnlock(string masterPassword);

    /// <summary>
    /// Locks the app, securing all sensitive data.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_lock")]
    public static partial PrivStackError AuthLock();

    /// <summary>
    /// Changes the master password.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_change_password", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError AuthChangePassword(string oldPassword, string newPassword);

    // ============================================================
    // Recovery Functions
    // ============================================================

    /// <summary>
    /// Sets up recovery for the default vault. Returns a 12-word BIP39 mnemonic.
    /// Free the returned string with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_setup_recovery")]
    public static partial PrivStackError AuthSetupRecovery(out nint outMnemonic);

    /// <summary>
    /// Checks whether recovery is configured for the default vault.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_has_recovery")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AuthHasRecovery();

    /// <summary>
    /// Resets the master password using a recovery mnemonic.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_reset_with_recovery", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError AuthResetWithRecovery(string mnemonic, string newPassword);

    /// <summary>
    /// Resets the master password using a recovery mnemonic and recovers cloud keypair (best-effort).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_auth_reset_with_unified_recovery", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError AuthResetWithUnifiedRecovery(string mnemonic, string newPassword);

    // ============================================================
    // Database Maintenance
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_db_maintenance")]
    public static partial PrivStackError DbMaintenance();

    // ============================================================
    // Vault Management Functions
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultCreate(string vaultId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_initialize", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultInitialize(string vaultId, string password);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_unlock", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultUnlock(string vaultId, string password);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_lock", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultLock(string vaultId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_lock_all")]
    public static partial PrivStackError VaultLockAll();

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_is_initialized", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool VaultIsInitialized(string vaultId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_is_unlocked", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool VaultIsUnlocked(string vaultId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_change_password", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultChangePassword(string vaultId, string oldPassword, string newPassword);

    // ============================================================
    // Encrypted Blob Storage (Vault Blobs)
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_blob_store", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultBlobStore(string vaultId, string blobId, nint data, nuint dataLen);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_blob_read", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultBlobRead(string vaultId, string blobId, out nint outData, out nuint outLen);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_blob_delete", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultBlobDelete(string vaultId, string blobId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_vault_blob_list", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError VaultBlobList(string vaultId, out nint outJson);

    // ============================================================
    // Unencrypted Blob Storage
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_blob_store", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError BlobStore(string ns, string blobId, nint data, nuint dataLen, string? metadataJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_blob_read", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError BlobRead(string ns, string blobId, out nint outData, out nuint outLen);

    [LibraryImport(LibraryName, EntryPoint = "privstack_blob_delete", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError BlobDelete(string ns, string blobId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_blob_list", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError BlobList(string ns, out nint outJson);

    // ============================================================
    // Sync Functions
    // ============================================================

    /// <summary>
    /// Starts the P2P sync transport.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_start")]
    public static partial PrivStackError SyncStart();

    /// <summary>
    /// Stops the P2P sync transport.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_stop")]
    public static partial PrivStackError SyncStop();

    /// <summary>
    /// Returns whether sync is running.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_is_running")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SyncIsRunning();

    /// <summary>
    /// Gets the current sync status as JSON.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_get_status")]
    public static partial PrivStackError SyncGetStatus(out nint outJson);

    /// <summary>
    /// Gets the local peer ID.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_get_peer_id")]
    public static partial PrivStackError SyncGetPeerId(out nint outPeerId);

    /// <summary>
    /// Gets discovered peers as JSON array.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_get_peers")]
    public static partial PrivStackError SyncGetPeers(out nint outJson);

    /// <summary>
    /// Gets the count of discovered peers.
    /// Returns -1 if transport lock is busy.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_peer_count")]
    public static partial int SyncPeerCount();

    /// <summary>
    /// Shares a document for sync with other peers.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_share_document", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError SyncShareDocument(string documentId);

    /// <summary>
    /// Records a local event for sync (call when user makes an edit).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_record_event", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError SyncRecordEvent(string documentId, string eventJson);

    /// <summary>
    /// Polls for sync events (non-blocking).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_poll_events")]
    public static partial PrivStackError SyncPollEvents(out nint outJson);

    /// <summary>
    /// Triggers immediate sync for a document with all known peers.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_document", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError SyncDocument(string documentId);

    /// <summary>
    /// Records a full entity snapshot for sync.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_snapshot", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError SyncSnapshot(string documentId, string entityType, string jsonData);

    /// <summary>
    /// Imports an entity received from sync into the local store.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_sync_import_entity", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError SyncImportEntity(string entityType, string jsonData);

    // ============================================================
    // Pairing / Sync Code Functions
    // ============================================================

    /// <summary>
    /// Generates a new random sync code for pairing devices.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_generate_code")]
    public static partial PrivStackError PairingGenerateCode(out nint outJson);

    /// <summary>
    /// Sets the sync code from user input.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_set_code", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PairingSetCode(string code);

    /// <summary>
    /// Gets the current sync code, if any.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_get_code")]
    public static partial PrivStackError PairingGetCode(out nint outJson);

    /// <summary>
    /// Clears the current sync code.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_clear_code")]
    public static partial PrivStackError PairingClearCode();

    /// <summary>
    /// Gets all discovered peers pending approval.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_get_discovered_peers")]
    public static partial PrivStackError PairingGetDiscoveredPeers(out nint outJson);

    /// <summary>
    /// Approves a discovered peer, making them trusted.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_approve_peer", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PairingApprovePeer(string peerId);

    /// <summary>
    /// Rejects a discovered peer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_reject_peer", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PairingRejectPeer(string peerId);

    /// <summary>
    /// Gets all trusted peers.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_get_trusted_peers")]
    public static partial PrivStackError PairingGetTrustedPeers(out nint outJson);

    /// <summary>
    /// Removes a trusted peer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_remove_trusted_peer", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PairingRemoveTrustedPeer(string peerId);

    /// <summary>
    /// Checks if a peer is trusted.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_is_trusted", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PairingIsTrusted(string peerId);

    /// <summary>
    /// Saves the pairing state to JSON.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_save_state")]
    public static partial PrivStackError PairingSaveState(out nint outJson);

    /// <summary>
    /// Loads the pairing state from JSON.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_load_state", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PairingLoadState(string json);

    /// <summary>
    /// Gets the device name.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_get_device_name")]
    public static partial PrivStackError PairingGetDeviceName(out nint outName);

    /// <summary>
    /// Sets the device name.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_pairing_set_device_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PairingSetDeviceName(string name);

    // ============================================================
    // Cloud Sync Functions
    // ============================================================

    /// <summary>
    /// Initializes Google Drive cloud storage.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_init_google_drive", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudInitGoogleDrive(string clientId, string clientSecret);

    /// <summary>
    /// Initializes iCloud Drive storage.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_init_icloud", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudInitICloud(string? bundleId);

    /// <summary>
    /// Starts authentication for a cloud provider.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_authenticate")]
    public static partial PrivStackError CloudAuthenticate(CloudProvider provider, out nint outAuthUrl);

    /// <summary>
    /// Completes OAuth authentication with an authorization code.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_complete_auth", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudCompleteAuth(CloudProvider provider, string authCode);

    /// <summary>
    /// Checks if cloud storage is authenticated.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_is_authenticated")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CloudIsAuthenticated(CloudProvider provider);

    /// <summary>
    /// Lists files in cloud storage sync folder.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_list_files")]
    public static partial PrivStackError CloudListFiles(CloudProvider provider, out nint outJson);

    /// <summary>
    /// Uploads a file to cloud storage.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_upload", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudUpload(CloudProvider provider, string name, nint data, nuint dataLen, out nint outJson);

    /// <summary>
    /// Downloads a file from cloud storage.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_download", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudDownload(CloudProvider provider, string fileId, out nint outData, out nuint outLen);

    /// <summary>
    /// Deletes a file from cloud storage.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_delete", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudDelete(CloudProvider provider, string fileId);

    /// <summary>
    /// Gets the name of a cloud provider.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_cloud_provider_name")]
    public static partial nint CloudProviderName(CloudProvider provider);

    // ============================================================
    // Cloud Sync (S3-backed — privstack_cloudsync_* prefix)
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_configure", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncConfigure(string configJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_authenticate", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncAuthenticate(string email, string password, out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_authenticate_with_tokens", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncAuthenticateWithTokens(string accessToken, string refreshToken, long userId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_logout")]
    public static partial PrivStackError CloudSyncLogout();

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_is_authenticated")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CloudSyncIsAuthenticated();

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_setup_passphrase", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncSetupPassphrase(string passphrase, out nint outMnemonic);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_setup_unified_recovery", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncSetupUnifiedRecovery(string passphrase, out nint outMnemonic);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_enter_passphrase", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncEnterPassphrase(string passphrase);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_recover_from_mnemonic", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncRecoverFromMnemonic(string mnemonic);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_has_keypair")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CloudSyncHasKeypair();

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_register_workspace", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncRegisterWorkspace(string workspaceId, string name, out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_list_workspaces")]
    public static partial PrivStackError CloudSyncListWorkspaces(out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_delete_workspace", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncDeleteWorkspace(string workspaceId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_start_sync", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncStartSync(string workspaceId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_stop_sync")]
    public static partial PrivStackError CloudSyncStopSync();

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_is_syncing")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CloudSyncIsSyncing();

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_get_status")]
    public static partial PrivStackError CloudSyncGetStatus(out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_force_flush")]
    public static partial PrivStackError CloudSyncForceFlush();

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_get_quota", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncGetQuota(string workspaceId, out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_share_entity", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncShareEntity(
        string entityId, string entityType, string? entityName,
        string workspaceId, string recipientEmail, string permission,
        out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_revoke_share", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncRevokeShare(string entityId, string recipientEmail);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_accept_share", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncAcceptShare(string invitationToken);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_list_entity_shares", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncListEntityShares(string entityId, out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_get_shared_with_me")]
    public static partial PrivStackError CloudSyncGetSharedWithMe(out nint outJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_register_device", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncRegisterDevice(string name, string platform);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_list_devices")]
    public static partial PrivStackError CloudSyncListDevices(out nint outJson);

    // ── Cloud Sync Blobs ──

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_upload_blob", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial PrivStackError CloudSyncUploadBlob(
        string workspaceId, string blobId, string? entityId,
        byte* dataPtr, nuint dataLen, byte* dekPtr);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_download_blob", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial PrivStackError CloudSyncDownloadBlob(
        string s3Key, byte* dekPtr, out nint outPtr, out nuint outLen);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_free_blob_data")]
    public static partial void CloudSyncFreeBlobData(nint ptr, nuint len);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_get_entity_blobs", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncGetEntityBlobs(string entityId, out nint outJson);

    // ── Cloud Sync Compaction ──

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_needs_compaction")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CloudSyncNeedsCompaction(nuint batchCount);

    [LibraryImport(LibraryName, EntryPoint = "privstack_cloudsync_request_compaction", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError CloudSyncRequestCompaction(string entityId, string workspaceId);

    // ============================================================
    // License Functions
    // ============================================================

    /// <summary>
    /// Parses and validates a license key.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_parse", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError LicenseParse(string key, out nint outJson);

    /// <summary>
    /// Gets the license plan from a parsed key.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_get_plan", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError LicenseGetPlan(string key, out LicensePlan outPlan);

    /// <summary>
    /// Gets device information including fingerprint.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_device_info")]
    public static partial PrivStackError DeviceInfo(out nint outJson);

    /// <summary>
    /// Generates and returns the device fingerprint.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_device_fingerprint")]
    public static partial PrivStackError DeviceFingerprint(out nint outFingerprint);

    /// <summary>
    /// Activates a license key (offline activation).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_activate", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError LicenseActivate(string key, out nint outJson);

    /// <summary>
    /// Checks if a valid license is activated.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_check")]
    public static partial PrivStackError LicenseCheck(out nint outJson);

    /// <summary>
    /// Checks if the license is valid and usable.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_is_valid")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool LicenseIsValid();

    /// <summary>
    /// Gets the current license status.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_status")]
    public static partial PrivStackError LicenseStatus(out LicenseStatus outStatus);

    /// <summary>
    /// Gets the activated license plan.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_activated_plan")]
    public static partial PrivStackError LicenseActivatedPlan(out LicensePlan outPlan);

    /// <summary>
    /// Deactivates the current license.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_deactivate")]
    public static partial PrivStackError LicenseDeactivate();

    /// <summary>
    /// Returns the maximum number of devices for a license plan.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_max_devices")]
    public static partial uint LicenseMaxDevices(LicensePlan licensePlan);

    /// <summary>
    /// Returns whether a license plan includes priority support.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_license_has_priority_support")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool LicenseHasPrioritySupport(LicensePlan licensePlan);

    // ============================================================
    // Wasm Plugin Host Functions
    // ============================================================

    /// <summary>
    /// Loads a Wasm plugin into the plugin host manager.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_load", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginLoad(string metadataJson, string schemasJson, string permissionsJson);

    /// <summary>
    /// Unloads a plugin from the host manager.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_unload", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginUnload(string pluginId);

    /// <summary>
    /// Routes an SDK message to a loaded plugin. Returns a JSON response (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_route_sdk", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginRouteSdk(string pluginId, string messageJson);

    /// <summary>
    /// Lists all loaded plugins as a JSON array (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_list")]
    public static partial nint PluginList();

    /// <summary>
    /// Returns navigation items for all loaded plugins as JSON array (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_nav_items")]
    public static partial nint PluginGetNavItems();

    /// <summary>
    /// Returns the number of loaded plugins.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_count")]
    public static partial int PluginCount();

    /// <summary>
    /// Checks if a plugin is loaded.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_is_loaded", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PluginIsLoaded(string pluginId);

    /// <summary>
    /// Gets resource metrics for a specific plugin as JSON (free with FreeString).
    /// Returns memory, CPU fuel, and disk usage metrics.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_metrics", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginGetMetrics(string pluginId);

    /// <summary>
    /// Gets resource metrics for all loaded plugins as JSON (free with FreeString).
    /// Returns an array of objects with plugin_id and metrics.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_all_metrics")]
    public static partial nint PluginGetAllMetrics();

    /// <summary>
    /// Gets commands from a specific plugin as JSON (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_commands", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginGetCommands(string pluginId);

    /// <summary>
    /// Gets all linkable item providers across loaded plugins as JSON (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_link_providers")]
    public static partial nint PluginGetLinkProviders();

    /// <summary>
    /// Searches across all loaded plugins for linkable items matching a query.
    /// Returns a JSON array of results (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_search_items", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginSearchItems(string query, int maxResults);

    /// <summary>
    /// Navigates to a specific item within a plugin via its deep-link-target export.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_navigate_to_item", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginNavigateToItem(string pluginId, string itemId);

    /// <summary>
    /// Navigates to a specific item and returns its view data in one call.
    /// Used for hover prefetch - combines navigate_to_item + get_view_data.
    /// Returns JSON string (free with FreeString).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_entity_view_data", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginGetEntityViewData(string pluginId, string itemId);

    // ========================================
    // Plugin Install / Update (P6.4)
    // ========================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_install_ppk", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginInstallPpk(string ppkPath);

    [LibraryImport(LibraryName, EntryPoint = "privstack_ppk_inspect", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PpkInspect(string ppkPath);

    [LibraryImport(LibraryName, EntryPoint = "privstack_ppk_content_hash", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PpkContentHash(string ppkPath);

    // ========================================
    // Plugin Wasm Runtime (command routing)
    // ========================================

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_load_wasm", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginLoadWasm(string wasmPath, string? permissionsJson, out nint outPluginId);

    /// <summary>
    /// Loads multiple Wasm plugins in parallel. Input/output are JSON arrays.
    /// The returned pointer must be freed with <see cref="FreeString"/>.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_load_wasm_batch", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginLoadWasmBatch(string pluginsJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_send_command", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginSendCommand(string pluginId, string commandName, string argsJson);

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_view_state", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginGetViewState(string pluginId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_get_view_data", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PluginGetViewData(string pluginId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_activate", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginActivate(string pluginId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_navigated_to", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginNavigatedTo(string pluginId);

    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_navigated_from", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginNavigatedFrom(string pluginId);

    /// <summary>
    /// Updates the permission set for a loaded plugin at runtime.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_update_permissions", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginUpdatePermissions(string pluginId, string permissionsJson);

    /// <summary>
    /// Fetch a URL on behalf of a plugin, checking its Network permission.
    /// Returns raw bytes. Caller must free with <see cref="FreeBytes"/>.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "privstack_plugin_fetch_url", StringMarshalling = StringMarshalling.Utf8)]
    public static partial PrivStackError PluginFetchUrl(string pluginId, string url, out nint outData, out nuint outLen);

    /// <summary>
    /// Managed wrapper that fetches a URL through the Rust core with permission checks,
    /// returning the response body as a byte array.
    /// </summary>
    public static byte[]? PluginFetchUrlManaged(string pluginId, string url)
    {
        var err = PluginFetchUrl(pluginId, url, out var dataPtr, out var len);
        if (err != PrivStackError.Ok || dataPtr == nint.Zero || len == 0)
            return null;

        try
        {
            var bytes = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(dataPtr, bytes, 0, (int)len);
            return bytes;
        }
        finally
        {
            FreeBytes(dataPtr, len);
        }
    }
}

/// <summary>
/// Cloud storage provider types.
/// </summary>
public enum CloudProvider
{
    GoogleDrive = 0,
    ICloud = 1
}

/// <summary>
/// License plan tiers.
/// </summary>
public enum LicensePlan
{
    Monthly = 0,
    Annual = 1,
    Perpetual = 2,
    Trial = 3
}

/// <summary>
/// License activation status.
/// </summary>
public enum LicenseStatus
{
    Active = 0,
    Expired = 1,
    Grace = 2,
    ReadOnly = 3,
    NotActivated = 4
}
