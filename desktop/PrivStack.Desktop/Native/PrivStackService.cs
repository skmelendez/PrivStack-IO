using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services;
using Serilog;

namespace PrivStack.Desktop.Native;

/// <summary>
/// High-level service for interacting with the PrivStack native library.
/// Provides lifecycle, authentication, sync, cloud, and license operations.
/// Domain CRUD operations are handled by plugins via IPrivStackSdk / SdkHost.
/// </summary>
public sealed class PrivStackService : IPrivStackNative
{
    private static readonly ILogger _log = Services.Log.ForContext<PrivStackService>();

    /// <summary>
    /// Shared JSON serializer options.
    /// </summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private bool _initialized;
    private bool _disposed;

    public PrivStackService() { }

    /// <summary>
    /// Gets whether the service is initialized.
    /// </summary>
    public bool IsInitialized => _initialized;

    /// <inheritdoc />
    public string NativeVersion => Version;

    /// <summary>
    /// Gets the native library version.
    /// </summary>
    public static string Version
    {
        get
        {
            var ptr = NativeLibrary.Version();
            return Marshal.PtrToStringUTF8(ptr) ?? "unknown";
        }
    }

    /// <summary>
    /// Initializes the PrivStack runtime with the given database path.
    /// </summary>
    /// <param name="dbPath">Path to the DuckDB database file.</param>
    /// <exception cref="PrivStackException">Thrown if initialization fails.</exception>
    public void Initialize(string dbPath)
    {
        ThrowIfDisposed();

        _log.Information("Initializing PrivStack native library with db: {DbPath}", dbPath);
        _log.Information("Native library version: {Version}", Version);

        // Pre-flight storage diagnostics
        LogStorageDiagnostics(dbPath);

        _log.Information("Calling native init_core...");
        var result = NativeLibrary.Init(dbPath);
        if (result != PrivStackError.Ok)
        {
            _log.Error("=== NATIVE INIT FAILED: {Result} ({ResultInt}) ===",
                result, (int)result);
            _log.Error("Base path: {DbPath}", dbPath);

            // Post-mortem: check if WAL files appeared during the failed init
            LogPostMortemDiagnostics(dbPath);

            // Flush logs immediately so they're captured even if app crashes
            Serilog.Log.CloseAndFlush();
            Services.Log.Initialize(); // Re-open logger after flush

            throw new PrivStackException($"Failed to initialize PrivStack: {result}", result);
        }

        _initialized = true;
        _log.Information("PrivStack native library initialized successfully");
    }

    /// <summary>
    /// Logs detailed file-level diagnostics before calling init_core.
    /// </summary>
    private static void LogStorageDiagnostics(string dbPath)
    {
        try
        {
            // The Rust side uses Path::with_extension, so "data.duckdb" becomes "data.vault.duckdb" etc.
            var basePath = dbPath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase)
                ? dbPath[..^".duckdb".Length]
                : dbPath;
            var dbDir = Path.GetDirectoryName(dbPath) ?? ".";

            _log.Information("[StorageDiag] === Pre-flight check ===");
            _log.Information("[StorageDiag] Base path: {Path}", basePath);
            _log.Information("[StorageDiag] Directory: {Dir} (exists={Exists})",
                dbDir, Directory.Exists(dbDir));

            // Check each database file
            string[] suffixes = ["vault.duckdb", "blobs.duckdb", "entities.duckdb", "events.duckdb"];
            foreach (var suffix in suffixes)
            {
                var filePath = $"{basePath}.{suffix}";
                CheckDatabaseFile(filePath);
            }

            // Check peer_id
            var peerIdPath = $"{basePath}.peer_id";
            _log.Information("[StorageDiag] peer_id exists: {Exists}", File.Exists(peerIdPath));

            // List everything in directory
            if (Directory.Exists(dbDir))
            {
                var files = Directory.GetFiles(dbDir);
                _log.Information("[StorageDiag] Directory has {Count} files: [{Files}]",
                    files.Length,
                    string.Join(", ", files.Select(Path.GetFileName)));
            }

            // Check write access
            try
            {
                var testPath = Path.Combine(dbDir, $".write_test_{Guid.NewGuid():N}");
                File.WriteAllText(testPath, "x");
                File.Delete(testPath);
                _log.Information("[StorageDiag] Directory is writable: true");
            }
            catch (Exception ex)
            {
                _log.Error("[StorageDiag] Directory is writable: FALSE — {Error}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[StorageDiag] Failed to run pre-flight diagnostics");
        }
    }

    private static void CheckDatabaseFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var walPath = $"{filePath}.wal";

        if (!File.Exists(filePath))
        {
            _log.Warning("[StorageDiag] {File}: MISSING", name);
            return;
        }

        var info = new FileInfo(filePath);
        _log.Information("[StorageDiag] {File}: size={Size} bytes, modified={Modified}, readonly={ReadOnly}",
            name, info.Length, info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"), info.IsReadOnly);

        // Check DuckDB header magic
        try
        {
            using var fs = File.OpenRead(filePath);
            var header = new byte[64];
            var bytesRead = fs.Read(header, 0, 64);
            if (bytesRead >= 16)
            {
                // Bytes 8-11 should be "DUCK"
                var magic = System.Text.Encoding.ASCII.GetString(header, 8, 4);
                // Version string is at offset 0x34
                var versionBytes = header[0x34..Math.Min(0x40, bytesRead)];
                var version = System.Text.Encoding.ASCII.GetString(versionBytes).TrimEnd('\0');
                _log.Information("[StorageDiag] {File}: magic={Magic}, version={Version}",
                    name, magic, version);
            }
        }
        catch (Exception ex)
        {
            _log.Error("[StorageDiag] {File}: failed to read header — {Error}", name, ex.Message);
        }

        // Check for WAL file
        if (File.Exists(walPath))
        {
            var walInfo = new FileInfo(walPath);
            _log.Warning("[StorageDiag] {File}: WAL FILE EXISTS! size={Size} bytes",
                Path.GetFileName(walPath), walInfo.Length);
        }

        // Try exclusive open to check for locks
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            _log.Information("[StorageDiag] {File}: exclusive lock OK (no other process holding it)", name);
        }
        catch (IOException ex)
        {
            _log.Error("[StorageDiag] {File}: CANNOT GET EXCLUSIVE LOCK — {Error}", name, ex.Message);
        }
    }

    /// <summary>
    /// After init_core fails, check what state the files are in.
    /// </summary>
    private static void LogPostMortemDiagnostics(string dbPath)
    {
        try
        {
            var basePath = dbPath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase)
                ? dbPath[..^".duckdb".Length]
                : dbPath;
            var dbDir = Path.GetDirectoryName(dbPath) ?? ".";

            _log.Error("[StorageDiag] === Post-mortem after init failure ===");

            // Check for WAL files that appeared during the failed init
            string[] suffixes = ["vault.duckdb", "blobs.duckdb", "entities.duckdb", "events.duckdb"];
            foreach (var suffix in suffixes)
            {
                var walPath = $"{basePath}.{suffix}.wal";
                if (File.Exists(walPath))
                {
                    var walInfo = new FileInfo(walPath);
                    _log.Error("[StorageDiag] {WalFile}: appeared during init! size={Size} bytes",
                        Path.GetFileName(walPath), walInfo.Length);
                }
            }

            // Re-list directory to see if anything changed
            if (Directory.Exists(dbDir))
            {
                var files = Directory.GetFiles(dbDir);
                _log.Error("[StorageDiag] Directory now has {Count} files: [{Files}]",
                    files.Length,
                    string.Join(", ", files.Select(Path.GetFileName)));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[StorageDiag] Post-mortem diagnostics failed");
        }
    }

    /// <summary>
    /// Shuts down the native library without marking the service as disposed,
    /// allowing re-initialization with a different database path (workspace switch).
    /// </summary>
    public void Shutdown()
    {
        ThrowIfDisposed();

        if (_initialized)
        {
            if (IsSyncRunning())
            {
                try { StopSync(); } catch { /* ignore */ }
            }

            NativeLibrary.Shutdown();
            _initialized = false;
            _log.Information("PrivStack native library shut down (ready for re-init)");
        }
    }

    // =========================================================================
    // App-Level Authentication
    // =========================================================================

    /// <summary>
    /// Checks if the app master password has been set up.
    /// </summary>
    public bool IsAuthInitialized()
    {
        ThrowIfNotInitialized();
        return NativeLibrary.AuthIsInitialized();
    }

    /// <summary>
    /// Checks if the app is currently unlocked.
    /// </summary>
    public bool IsAuthUnlocked()
    {
        ThrowIfNotInitialized();
        return NativeLibrary.AuthIsUnlocked();
    }

    /// <summary>
    /// Initializes the app with a master password (first-time setup).
    /// Sets the master password for all secure features.
    /// </summary>
    /// <param name="masterPassword">The master password to set (min 8 characters).</param>
    /// <exception cref="PrivStackException">Thrown if initialization fails.</exception>
    public void InitializeAuth(string masterPassword)
    {
        ThrowIfNotInitialized();

        _log.Information("Initializing app master password");
        var result = NativeLibrary.AuthInitialize(masterPassword);
        if (result != PrivStackError.Ok)
        {
            _log.Error("Failed to initialize master password: {Result}", result);
            throw new PrivStackException($"Failed to initialize master password: {result}", result);
        }
        _log.Information("Master password initialized successfully");

        EnsureStandardVaults(masterPassword);
    }

    /// <summary>
    /// Unlocks the app with the master password.
    /// Unlocks all secure features (passwords, files, etc.).
    /// </summary>
    /// <param name="masterPassword">The master password.</param>
    /// <exception cref="PrivStackException">Thrown if unlock fails (wrong password).</exception>
    public void UnlockApp(string masterPassword)
    {
        ThrowIfNotInitialized();

        _log.Information("Unlocking app with master password");
        var result = NativeLibrary.AuthUnlock(masterPassword);
        if (result != PrivStackError.Ok)
        {
            _log.Warning("Failed to unlock app: {Result}", result);
            throw new PrivStackException($"Invalid master password", result);
        }
        _log.Information("App unlocked successfully");

        EnsureStandardVaults(masterPassword);
    }

    /// <summary>
    /// Locks the app, securing all sensitive data.
    /// Clears encryption keys from memory.
    /// </summary>
    public void LockApp()
    {
        ThrowIfNotInitialized();

        _log.Information("Locking app");

        var result = NativeLibrary.AuthLock();
        if (result != PrivStackError.Ok)
        {
            _log.Error("Failed to lock app: {Result}", result);
            throw new PrivStackException($"Failed to lock app: {result}", result);
        }
        _log.Information("App locked successfully");
    }

    /// <summary>
    /// Changes the master password.
    /// Requires the app to be unlocked.
    /// </summary>
    /// <param name="oldPassword">The current master password.</param>
    /// <param name="newPassword">The new master password (min 8 characters).</param>
    /// <exception cref="PrivStackException">Thrown if password change fails.</exception>
    public void ChangeAppPassword(string oldPassword, string newPassword)
    {
        ThrowIfNotInitialized();

        _log.Information("Changing app master password");
        var result = NativeLibrary.AuthChangePassword(oldPassword, newPassword);
        if (result != PrivStackError.Ok)
        {
            _log.Error("Failed to change master password: {Result}", result);
            throw new PrivStackException($"Failed to change master password: {result}", result);
        }
        _log.Information("Master password changed successfully");
    }

    /// <summary>
    /// Validates the master password without changing the lock state.
    /// Used for re-authentication to sensitive features.
    /// </summary>
    /// <param name="masterPassword">The master password to validate.</param>
    /// <returns>True if the password is correct.</returns>
    public bool ValidateMasterPassword(string masterPassword)
    {
        ThrowIfNotInitialized();

        // The app must already be unlocked to use this feature
        if (!IsAuthUnlocked())
        {
            _log.Warning("ValidateMasterPassword called but app is not unlocked");
            return false;
        }

        // Re-calling AuthUnlock with the correct password will succeed without side effects
        // This validates the password against the stored hash
        var result = NativeLibrary.AuthUnlock(masterPassword);
        if (result == PrivStackError.Ok)
        {
            _log.Debug("Master password validated successfully");
            return true;
        }

        _log.Debug("Master password validation failed: {Result}", result);
        return false;
    }

    // ============================================================
    // Standard Vault Initialization
    // ============================================================

    /// <summary>
    /// Standard vaults that are automatically initialized and unlocked
    /// alongside the master password. These use the same master password
    /// as the app-level auth, unlike user-managed vaults (e.g., Files).
    /// </summary>
    private static readonly string[] StandardVaultIds = ["connections"];

    /// <summary>
    /// Ensures all standard vaults are created, initialized, and unlocked.
    /// Called after successful auth init or unlock while the master password is still in scope.
    /// </summary>
    private void EnsureStandardVaults(string masterPassword)
    {
        foreach (var vaultId in StandardVaultIds)
        {
            try
            {
                if (!NativeLibrary.VaultIsInitialized(vaultId))
                {
                    _log.Information("Initializing standard vault: {VaultId}", vaultId);
                    NativeLibrary.VaultCreate(vaultId); // Idempotent — creates if missing

                    var initResult = NativeLibrary.VaultInitialize(vaultId, masterPassword);
                    if (initResult != PrivStackError.Ok && initResult != PrivStackError.VaultAlreadyInitialized)
                    {
                        _log.Warning("Failed to initialize standard vault {VaultId}: {Result}", vaultId, initResult);
                        continue;
                    }
                }

                if (!NativeLibrary.VaultIsUnlocked(vaultId))
                {
                    var unlockResult = NativeLibrary.VaultUnlock(vaultId, masterPassword);
                    if (unlockResult != PrivStackError.Ok)
                        _log.Warning("Failed to unlock standard vault {VaultId}: {Result}", vaultId, unlockResult);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to ensure standard vault {VaultId}", vaultId);
            }
        }
    }

    // ============================================================
    // Sync Methods
    // ============================================================

    /// <summary>
    /// Starts the P2P sync transport and begins ingesting sync events.
    /// </summary>
    public void StartSync()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.SyncStart();
        if (result != PrivStackError.Ok && result != PrivStackError.SyncAlreadyRunning)
        {
            throw new PrivStackException($"Failed to start sync: {result}", result);
        }

        // Start background ingestion of sync events
        App.Services.GetRequiredService<Services.Abstractions.ISyncIngestionService>().Start();
    }

    /// <summary>
    /// Stops the P2P sync transport and stops ingesting sync events.
    /// </summary>
    public void StopSync()
    {
        ThrowIfNotInitialized();

        // Stop background ingestion first
        App.Services.GetRequiredService<Services.Abstractions.ISyncIngestionService>().Stop();

        var result = NativeLibrary.SyncStop();
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to stop sync: {result}", result);
        }
    }

    /// <summary>
    /// Returns whether sync is running.
    /// </summary>
    public bool IsSyncRunning()
    {
        return _initialized && NativeLibrary.SyncIsRunning();
    }

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    public string GetSyncStatus()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.SyncGetStatus(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get sync status: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? "{}";
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Gets the current sync status and deserializes it.
    /// </summary>
    public T GetSyncStatus<T>()
    {
        var json = GetSyncStatus();
        return JsonSerializer.Deserialize<T>(json) ?? throw new PrivStackException("Failed to deserialize sync status", PrivStackError.JsonError);
    }

    /// <summary>
    /// Gets the local peer ID.
    /// </summary>
    public string GetLocalPeerId()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.SyncGetPeerId(out var peerIdPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get peer ID: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(peerIdPtr) ?? string.Empty;
        }
        finally
        {
            NativeLibrary.FreeString(peerIdPtr);
        }
    }

    /// <summary>
    /// Gets discovered peers.
    /// </summary>
    public string GetDiscoveredPeers()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.SyncGetPeers(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get peers: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? "[]";
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Gets discovered peers and deserializes them.
    /// </summary>
    public List<T> GetDiscoveredPeers<T>()
    {
        var json = GetDiscoveredPeers();
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    /// <summary>
    /// Gets the count of discovered peers.
    /// Returns 0 if not initialized, -1 if transport lock is busy.
    /// </summary>
    public int GetPeerCount()
    {
        return _initialized ? NativeLibrary.SyncPeerCount() : 0;
    }

    /// <summary>
    /// Shares a document for sync with other peers.
    /// </summary>
    public void ShareDocumentForSync(string documentId)
    {
        if (!_initialized || !IsSyncRunning()) return;

        var result = NativeLibrary.SyncShareDocument(documentId);
        if (result != PrivStackError.Ok && result != PrivStackError.SyncNotRunning)
        {
            _log.Warning("Failed to share document {DocumentId} for sync: {Result}", documentId, result);
        }
    }

    /// <summary>
    /// Triggers immediate sync for a document with all known peers.
    /// </summary>
    public void TriggerDocumentSync(string documentId)
    {
        if (!_initialized || !IsSyncRunning()) return;

        var result = NativeLibrary.SyncDocument(documentId);
        if (result != PrivStackError.Ok && result != PrivStackError.SyncNotRunning)
        {
            _log.Warning("Failed to trigger sync for document {DocumentId}: {Result}", documentId, result);
        }
    }

    /// <summary>
    /// Polls for sync events (non-blocking).
    /// Returns a JSON array of sync events that have occurred since last poll.
    /// </summary>
    public string PollSyncEvents()
    {
        if (!_initialized || !IsSyncRunning()) return "[]";

        var result = NativeLibrary.SyncPollEvents(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            return "[]";
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? "[]";
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Polls for sync events and deserializes them.
    /// </summary>
    public List<T> PollSyncEvents<T>()
    {
        var json = PollSyncEvents();
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    /// <summary>
    /// Records a full entity snapshot for sync.
    /// </summary>
    public void RecordSyncSnapshot(string documentId, string entityType, string jsonData)
    {
        if (!_initialized || !IsSyncRunning()) return;

        var result = NativeLibrary.SyncSnapshot(documentId, entityType, jsonData);
        if (result != PrivStackError.Ok && result != PrivStackError.SyncNotRunning)
        {
            _log.Warning("Failed to record snapshot for {EntityType}/{DocumentId}: {Result}", entityType, documentId, result);
        }
    }

    /// <summary>
    /// Imports an entity received from a sync peer into the local store.
    /// Returns true if import succeeded.
    /// </summary>
    public bool ImportSyncEntity(string entityType, string jsonData)
    {
        if (!_initialized) return false;

        var result = NativeLibrary.SyncImportEntity(entityType, jsonData);
        if (result != PrivStackError.Ok)
        {
            _log.Warning("Failed to import {EntityType} from sync: {Result}", entityType, result);
            return false;
        }
        return true;
    }

    // ============================================================
    // Pairing / Sync Code Methods
    // ============================================================

    /// <summary>
    /// Generates a new random sync code for pairing devices.
    /// </summary>
    public SyncCode GenerateSyncCode()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingGenerateCode(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to generate sync code: {result}", result);
        }

        try
        {
            var json = Marshal.PtrToStringUTF8(jsonPtr) ?? "{}";
            return JsonSerializer.Deserialize<SyncCode>(json)
                ?? throw new PrivStackException("Failed to deserialize sync code", PrivStackError.JsonError);
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Sets the sync code from user input (for joining an existing sync group).
    /// </summary>
    public void SetSyncCode(string code)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingSetCode(code);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to set sync code: {result}", result);
        }
    }

    /// <summary>
    /// Gets the current sync code, if any.
    /// </summary>
    public SyncCode? GetSyncCode()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingGetCode(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get sync code: {result}", result);
        }

        try
        {
            var json = Marshal.PtrToStringUTF8(jsonPtr) ?? "null";
            if (json == "null") return null;
            return JsonSerializer.Deserialize<SyncCode>(json);
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Clears the current sync code and stops pairing mode.
    /// </summary>
    public void ClearSyncCode()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingClearCode();
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to clear sync code: {result}", result);
        }
    }

    /// <summary>
    /// Gets all discovered peers pending approval.
    /// </summary>
    public List<PairingPeerInfo> GetPairingDiscoveredPeers()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingGetDiscoveredPeers(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get discovered peers: {result}", result);
        }

        try
        {
            var json = Marshal.PtrToStringUTF8(jsonPtr) ?? "[]";
            return JsonSerializer.Deserialize<List<PairingPeerInfo>>(json) ?? [];
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Approves a discovered peer, making them trusted.
    /// </summary>
    public void ApprovePeer(string peerId)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingApprovePeer(peerId);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to approve peer: {result}", result);
        }
    }

    /// <summary>
    /// Rejects a discovered peer.
    /// </summary>
    public void RejectPeer(string peerId)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingRejectPeer(peerId);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to reject peer: {result}", result);
        }
    }

    /// <summary>
    /// Gets all trusted peers.
    /// </summary>
    public List<TrustedPeer> GetTrustedPeers()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingGetTrustedPeers(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get trusted peers: {result}", result);
        }

        try
        {
            var json = Marshal.PtrToStringUTF8(jsonPtr) ?? "[]";
            return JsonSerializer.Deserialize<List<TrustedPeer>>(json) ?? [];
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Removes a trusted peer.
    /// </summary>
    public void RemoveTrustedPeer(string peerId)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingRemoveTrustedPeer(peerId);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to remove trusted peer: {result}", result);
        }
    }

    /// <summary>
    /// Checks if a peer is trusted.
    /// </summary>
    public bool IsPeerTrusted(string peerId)
    {
        return _initialized && NativeLibrary.PairingIsTrusted(peerId);
    }

    /// <summary>
    /// Saves the pairing state to JSON (for persistence).
    /// </summary>
    public string SavePairingState()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingSaveState(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to save pairing state: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? "{}";
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Loads the pairing state from JSON.
    /// </summary>
    public void LoadPairingState(string json)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingLoadState(json);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to load pairing state: {result}", result);
        }
    }

    /// <summary>
    /// Gets the device name.
    /// </summary>
    public string GetDeviceName()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingGetDeviceName(out var namePtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get device name: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(namePtr) ?? Environment.MachineName;
        }
        finally
        {
            NativeLibrary.FreeString(namePtr);
        }
    }

    /// <summary>
    /// Sets the device name.
    /// </summary>
    public void SetDeviceName(string name)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.PairingSetDeviceName(name);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to set device name: {result}", result);
        }
    }

    // ============================================================
    // Cloud Sync Methods
    // ============================================================

    /// <summary>
    /// Initializes Google Drive cloud storage.
    /// </summary>
    public void InitGoogleDrive(string clientId, string clientSecret)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudInitGoogleDrive(clientId, clientSecret);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to initialize Google Drive: {result}", result);
        }
    }

    /// <summary>
    /// Initializes iCloud Drive storage.
    /// </summary>
    public void InitICloud(string? bundleId = null)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudInitICloud(bundleId);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to initialize iCloud: {result}", result);
        }
    }

    /// <summary>
    /// Authenticates with a cloud provider. Returns an OAuth URL if user interaction is needed.
    /// </summary>
    public string? CloudAuthenticate(CloudProvider provider)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudAuthenticate(provider, out var authUrlPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to authenticate with cloud: {result}", result);
        }

        if (authUrlPtr == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(authUrlPtr);
        }
        finally
        {
            NativeLibrary.FreeString(authUrlPtr);
        }
    }

    /// <summary>
    /// Completes OAuth authentication with an authorization code.
    /// </summary>
    public void CloudCompleteAuth(CloudProvider provider, string authCode)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudCompleteAuth(provider, authCode);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to complete cloud auth: {result}", result);
        }
    }

    /// <summary>
    /// Checks if cloud storage is authenticated.
    /// </summary>
    public bool IsCloudAuthenticated(CloudProvider provider)
    {
        return _initialized && NativeLibrary.CloudIsAuthenticated(provider);
    }

    /// <summary>
    /// Lists files in cloud storage sync folder.
    /// </summary>
    public string ListCloudFiles(CloudProvider provider)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudListFiles(provider, out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to list cloud files: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? "[]";
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Lists cloud files and deserializes them.
    /// </summary>
    public List<T> ListCloudFiles<T>(CloudProvider provider)
    {
        var json = ListCloudFiles(provider);
        return JsonSerializer.Deserialize<List<T>>(json) ?? [];
    }

    /// <summary>
    /// Uploads a file to cloud storage.
    /// </summary>
    public string CloudUpload(CloudProvider provider, string name, byte[] data)
    {
        ThrowIfNotInitialized();

        unsafe
        {
            fixed (byte* dataPtr = data)
            {
                var result = NativeLibrary.CloudUpload(provider, name, (nint)dataPtr, (nuint)data.Length, out var jsonPtr);
                if (result != PrivStackError.Ok)
                {
                    throw new PrivStackException($"Failed to upload to cloud: {result}", result);
                }

                try
                {
                    return Marshal.PtrToStringUTF8(jsonPtr) ?? throw new PrivStackException("Null JSON response", PrivStackError.Unknown);
                }
                finally
                {
                    NativeLibrary.FreeString(jsonPtr);
                }
            }
        }
    }

    /// <summary>
    /// Uploads a file to cloud storage and deserializes the result.
    /// </summary>
    public T CloudUpload<T>(CloudProvider provider, string name, byte[] data)
    {
        var json = CloudUpload(provider, name, data);
        return JsonSerializer.Deserialize<T>(json) ?? throw new PrivStackException("Failed to deserialize cloud file", PrivStackError.JsonError);
    }

    /// <summary>
    /// Downloads a file from cloud storage.
    /// </summary>
    public byte[] CloudDownload(CloudProvider provider, string fileId)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudDownload(provider, fileId, out var dataPtr, out var dataLen);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to download from cloud: {result}", result);
        }

        try
        {
            var data = new byte[dataLen];
            Marshal.Copy(dataPtr, data, 0, (int)dataLen);
            return data;
        }
        finally
        {
            NativeLibrary.FreeBytes(dataPtr, dataLen);
        }
    }

    /// <summary>
    /// Deletes a file from cloud storage.
    /// </summary>
    public void CloudDelete(CloudProvider provider, string fileId)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.CloudDelete(provider, fileId);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to delete cloud file: {result}", result);
        }
    }

    /// <summary>
    /// Gets the name of a cloud provider.
    /// </summary>
    public static string GetCloudProviderName(CloudProvider provider)
    {
        var ptr = NativeLibrary.CloudProviderName(provider);
        return Marshal.PtrToStringUTF8(ptr) ?? "Unknown";
    }

    // ============================================================
    // License Methods
    // ============================================================

    /// <summary>
    /// Parses and validates a license key.
    /// </summary>
    public string ParseLicenseKey(string key)
    {
        var result = NativeLibrary.LicenseParse(key, out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to parse license key: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? throw new PrivStackException("Null JSON response", PrivStackError.Unknown);
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Parses and validates a license key, returning typed info.
    /// </summary>
    public T ParseLicenseKey<T>(string key)
    {
        var json = ParseLicenseKey(key);
        return JsonSerializer.Deserialize<T>(json) ?? throw new PrivStackException("Failed to deserialize license info", PrivStackError.JsonError);
    }

    /// <summary>
    /// Gets the license plan from a license key.
    /// </summary>
    public LicensePlan GetLicensePlan(string key)
    {
        var result = NativeLibrary.LicenseGetPlan(key, out var licensePlan);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get license plan: {result}", result);
        }
        return licensePlan;
    }

    /// <summary>
    /// Gets device information including fingerprint.
    /// </summary>
    public string GetDeviceInfo()
    {
        var result = NativeLibrary.DeviceInfo(out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get device info: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? throw new PrivStackException("Null JSON response", PrivStackError.Unknown);
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Gets device information and deserializes it.
    /// </summary>
    public T GetDeviceInfo<T>()
    {
        var json = GetDeviceInfo();
        return JsonSerializer.Deserialize<T>(json) ?? throw new PrivStackException("Failed to deserialize device info", PrivStackError.JsonError);
    }

    /// <summary>
    /// Gets the device fingerprint.
    /// </summary>
    public string GetDeviceFingerprint()
    {
        var result = NativeLibrary.DeviceFingerprint(out var fingerprintPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get device fingerprint: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(fingerprintPtr) ?? string.Empty;
        }
        finally
        {
            NativeLibrary.FreeString(fingerprintPtr);
        }
    }

    /// <summary>
    /// Activates a license key (offline activation).
    /// </summary>
    public string ActivateLicense(string key)
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.LicenseActivate(key, out var jsonPtr);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to activate license: {result}", result);
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr) ?? throw new PrivStackException("Null JSON response", PrivStackError.Unknown);
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Activates a license key and returns typed activation info.
    /// </summary>
    public T ActivateLicense<T>(string key)
    {
        var json = ActivateLicense(key);
        return JsonSerializer.Deserialize<T>(json) ?? throw new PrivStackException("Failed to deserialize activation info", PrivStackError.JsonError);
    }

    /// <summary>
    /// Checks if a valid license is activated.
    /// </summary>
    public string? CheckLicense()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.LicenseCheck(out var jsonPtr);
        if (result == PrivStackError.LicenseNotActivated)
        {
            return null;
        }

        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to check license: {result}", result);
        }

        if (jsonPtr == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(jsonPtr);
        }
        finally
        {
            NativeLibrary.FreeString(jsonPtr);
        }
    }

    /// <summary>
    /// Checks if a valid license is activated and returns typed info.
    /// </summary>
    public T? CheckLicense<T>() where T : class
    {
        var json = CheckLicense();
        if (json == null) return null;
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>
    /// Checks if the license is valid and usable.
    /// </summary>
    public bool IsLicenseValid()
    {
        return _initialized && NativeLibrary.LicenseIsValid();
    }

    /// <summary>
    /// Gets the current license status.
    /// </summary>
    public LicenseStatus GetLicenseStatus()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.LicenseStatus(out var status);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get license status: {result}", result);
        }
        return status;
    }

    /// <summary>
    /// Gets the activated license plan.
    /// </summary>
    public LicensePlan GetActivatedLicensePlan()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.LicenseActivatedPlan(out var licensePlan);
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to get activated license plan: {result}", result);
        }
        return licensePlan;
    }

    /// <summary>
    /// Deactivates the current license.
    /// </summary>
    public void DeactivateLicense()
    {
        ThrowIfNotInitialized();

        var result = NativeLibrary.LicenseDeactivate();
        if (result != PrivStackError.Ok)
        {
            throw new PrivStackException($"Failed to deactivate license: {result}", result);
        }
    }

    /// <summary>
    /// Returns the maximum number of devices for a license plan.
    /// </summary>
    public static uint GetMaxDevices(LicensePlan licensePlan)
    {
        return NativeLibrary.LicenseMaxDevices(licensePlan);
    }

    /// <summary>
    /// Returns whether a license plan includes priority support.
    /// </summary>
    public static bool HasPrioritySupport(LicensePlan licensePlan)
    {
        return NativeLibrary.LicenseHasPrioritySupport(licensePlan);
    }

    // ============================================================
    // Dispose
    // ============================================================

    public void Dispose()
    {
        if (_disposed) return;

        if (_initialized)
        {
            // Stop sync before shutdown
            if (IsSyncRunning())
            {
                try { StopSync(); } catch { /* ignore */ }
            }

            NativeLibrary.Shutdown();
            _initialized = false;
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfNotInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("PrivStack service is not initialized. Call Initialize() first.");
        }
    }
}

/// <summary>
/// Exception thrown when a PrivStack operation fails.
/// </summary>
public class PrivStackException : Exception
{
    /// <summary>
    /// Gets the error code.
    /// </summary>
    public PrivStackError ErrorCode { get; }

    /// <summary>
    /// Creates a new PrivStackException.
    /// </summary>
    public PrivStackException(string message, PrivStackError errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }
}
