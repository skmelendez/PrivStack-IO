using System.Text.Json;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using PrivStack.Sdk.Json;
using Serilog;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;

namespace PrivStack.Desktop.Sdk;

/// <summary>
/// Implements IPrivStackSdk by serializing SdkMessage to JSON, calling the generic
/// privstack_execute P/Invoke, and deserializing the response.
/// Uses a ReaderWriterLockSlim to prevent FFI calls during workspace switches.
/// </summary>
internal sealed class SdkHost : IPrivStackSdk, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<SdkHost>();
    private static readonly JsonSerializerOptions _jsonOptions = SdkJsonOptions.Default;

    private readonly IPrivStackRuntime _runtime;
    private readonly ReaderWriterLockSlim _switchLock = new();
    private volatile bool _isSwitching;
    private ISyncOutboundService? _syncOutbound;
    private Func<string, CancellationToken, Task<string?>>? _vaultUnlockPrompt;

    /// <summary>
    /// Raised when a mutation is blocked because the license is in read-only mode.
    /// </summary>
    public event EventHandler? LicenseReadOnlyBlocked;

    public SdkHost(IPrivStackRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Wires the outbound sync service. Called after DI construction.
    /// </summary>
    public void SetSyncOutbound(ISyncOutboundService service) => _syncOutbound = service;

    /// <summary>
    /// Wires the vault unlock prompt callback. The callback receives a vault ID and
    /// should show a password dialog to the user. Returns the entered password or null
    /// if cancelled.
    /// </summary>
    public void SetVaultUnlockPrompt(Func<string, CancellationToken, Task<string?>> prompt) =>
        _vaultUnlockPrompt = prompt;

    /// <summary>
    /// True when the native runtime is initialized and not mid-workspace-switch.
    /// </summary>
    public bool IsReady => _runtime.IsInitialized && !_isSwitching;

    /// <summary>
    /// Acquires the write lock, blocking all SDK calls during workspace switch.
    /// </summary>
    public void BeginWorkspaceSwitch()
    {
        _syncOutbound?.CancelAll();
        _isSwitching = true;
        _switchLock.EnterWriteLock();
        _log.Debug("SdkHost: workspace switch started, SDK calls blocked");
    }

    /// <summary>
    /// Releases the write lock, allowing SDK calls to resume.
    /// </summary>
    public void EndWorkspaceSwitch()
    {
        _switchLock.ExitWriteLock();
        _isSwitching = false;
        _log.Debug("SdkHost: workspace switch ended, SDK calls unblocked");
    }

    public Task<SdkResponse<TResult>> SendAsync<TResult>(SdkMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_switchLock.TryEnterReadLock(TimeSpan.FromSeconds(5)))
            return Task.FromResult(SdkResponse<TResult>.Fail("not_ready", "Workspace switch in progress"));

        try
        {
            if (!_runtime.IsInitialized)
                return Task.FromResult(SdkResponse<TResult>.Fail("not_ready", "Runtime not initialized"));

            var requestJson = JsonSerializer.Serialize(message, _jsonOptions);
            _log.Debug("SDK Execute: {EntityType}.{Action}", message.EntityType, message.Action);

            var responsePtr = NativeLib.Execute(requestJson);
            try
            {
                if (responsePtr == nint.Zero)
                {
                    _log.Warning("SDK {EntityType}.{Action} returned null pointer", message.EntityType, message.Action);
                    return Task.FromResult(SdkResponse<TResult>.Fail("ffi_error", "Execute returned null"));
                }

                var responseJson = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(responsePtr);
                if (string.IsNullOrEmpty(responseJson))
                {
                    _log.Warning("SDK {EntityType}.{Action} returned empty response", message.EntityType, message.Action);
                    return Task.FromResult(SdkResponse<TResult>.Fail("ffi_error", "Execute returned empty response"));
                }

                try
                {
                    var response = JsonSerializer.Deserialize<SdkResponse<TResult>>(responseJson, _jsonOptions);
                    if (response?.ErrorCode == "license_read_only")
                        LicenseReadOnlyBlocked?.Invoke(this, EventArgs.Empty);
                    if (response?.Success == true)
                        NotifySyncIfMutation(message, responseJson);
                    return Task.FromResult(response ?? SdkResponse<TResult>.Fail("json_error", "Failed to deserialize response"));
                }
                catch (JsonException ex)
                {
                    _log.Error(ex, "SDK deserialization failed for {EntityType}.{Action} -> {TargetType}. Response: {Response}",
                        message.EntityType, message.Action, typeof(TResult).Name,
                        responseJson.Length > 500 ? responseJson[..500] + "..." : responseJson);
                    return Task.FromResult(SdkResponse<TResult>.Fail("json_error",
                        $"Failed to deserialize {typeof(TResult).Name}: {ex.Message}"));
                }
            }
            finally
            {
                if (responsePtr != nint.Zero)
                {
                    NativeLib.FreeString(responsePtr);
                }
            }
        }
        finally
        {
            _switchLock.ExitReadLock();
        }
    }

    public Task<SdkResponse> SendAsync(SdkMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_switchLock.TryEnterReadLock(TimeSpan.FromSeconds(5)))
            return Task.FromResult(SdkResponse.Fail("not_ready", "Workspace switch in progress"));

        try
        {
            if (!_runtime.IsInitialized)
                return Task.FromResult(SdkResponse.Fail("not_ready", "Runtime not initialized"));

            var requestJson = JsonSerializer.Serialize(message, _jsonOptions);
            _log.Debug("SDK Execute: {EntityType}.{Action}", message.EntityType, message.Action);

            var responsePtr = NativeLib.Execute(requestJson);
            try
            {
                if (responsePtr == nint.Zero)
                {
                    return Task.FromResult(SdkResponse.Fail("ffi_error", "Execute returned null"));
                }

                var responseJson = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(responsePtr);
                if (string.IsNullOrEmpty(responseJson))
                {
                    return Task.FromResult(SdkResponse.Fail("ffi_error", "Execute returned empty response"));
                }

                var response = JsonSerializer.Deserialize<SdkResponse>(responseJson, _jsonOptions);
                if (response?.ErrorCode == "license_read_only")
                    LicenseReadOnlyBlocked?.Invoke(this, EventArgs.Empty);
                if (response != null && !response.Success)
                {
                    _log.Warning("SDK {EntityType}.{Action} failed: [{ErrorCode}] {ErrorMessage}",
                        message.EntityType, message.Action, response.ErrorCode, response.ErrorMessage);
                }
                else if (response?.Success == true)
                {
                    NotifySyncIfMutation(message, responseJson);
                }
                return Task.FromResult(response ?? SdkResponse.Fail("json_error", "Failed to deserialize response"));
            }
            finally
            {
                if (responsePtr != nint.Zero)
                {
                    NativeLib.FreeString(responsePtr);
                }
            }
        }
        finally
        {
            _switchLock.ExitReadLock();
        }
    }

    public async Task<int> CountAsync(string pluginId, string entityType, bool includeTrashed = false, CancellationToken ct = default)
    {
        var parameters = includeTrashed
            ? new Dictionary<string, string> { ["include_trashed"] = "true" }
            : null;

        var response = await SendAsync<CountResponse>(new SdkMessage
        {
            PluginId = pluginId,
            Action = SdkAction.Count,
            EntityType = entityType,
            Parameters = parameters,
        }, ct);

        return response is { Success: true, Data: not null } ? response.Data.Count : 0;
    }

    public Task<SdkResponse<TResult>> SearchAsync<TResult>(string query, string[]? entityTypes = null, int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_switchLock.TryEnterReadLock(TimeSpan.FromSeconds(5)))
            return Task.FromResult(SdkResponse<TResult>.Fail("not_ready", "Workspace switch in progress"));

        try
        {
            if (!_runtime.IsInitialized)
                return Task.FromResult(SdkResponse<TResult>.Fail("not_ready", "Runtime not initialized"));

            var searchQuery = new { query, entity_types = entityTypes, limit };
            var queryJson = JsonSerializer.Serialize(searchQuery, _jsonOptions);
            _log.Debug("SDK Search: {Query}", query);

            var responsePtr = NativeLib.Search(queryJson);
            try
            {
                if (responsePtr == nint.Zero)
                {
                    _log.Warning("SDK Search returned null pointer for query: {Query}", query);
                    return Task.FromResult(SdkResponse<TResult>.Fail("ffi_error", "Search returned null"));
                }

                var responseJson = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(responsePtr);
                if (string.IsNullOrEmpty(responseJson))
                {
                    _log.Warning("SDK Search returned empty response for query: {Query}", query);
                    return Task.FromResult(SdkResponse<TResult>.Fail("ffi_error", "Search returned empty response"));
                }

                try
                {
                    var response = JsonSerializer.Deserialize<SdkResponse<TResult>>(responseJson, _jsonOptions);
                    return Task.FromResult(response ?? SdkResponse<TResult>.Fail("json_error", "Failed to deserialize search response"));
                }
                catch (JsonException ex)
                {
                    _log.Error(ex, "SDK Search deserialization failed for {TargetType}. Response: {Response}",
                        typeof(TResult).Name,
                        responseJson.Length > 500 ? responseJson[..500] + "..." : responseJson);
                    return Task.FromResult(SdkResponse<TResult>.Fail("json_error",
                        $"Failed to deserialize {typeof(TResult).Name}: {ex.Message}"));
                }
            }
            finally
            {
                if (responsePtr != nint.Zero)
                {
                    NativeLib.FreeString(responsePtr);
                }
            }
        }
        finally
        {
            _switchLock.ExitReadLock();
        }
    }

    // =========================================================================
    // Database Maintenance
    // =========================================================================

    public Task RunDatabaseMaintenance(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_switchLock.TryEnterReadLock(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Workspace switch in progress");

        try
        {
            var result = NativeLib.DbMaintenance();
            if (result != PrivStackError.Ok)
                throw new InvalidOperationException($"Database maintenance failed: {result}");
            return Task.CompletedTask;
        }
        finally
        {
            _switchLock.ExitReadLock();
        }
    }

    // =========================================================================
    // Vault (Encrypted Blob Storage)
    // =========================================================================

    public Task<bool> VaultIsInitialized(string vaultId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out var notReady)) return Task.FromResult(false);
        try { return Task.FromResult(NativeLib.VaultIsInitialized(vaultId)); }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task VaultInitialize(string vaultId, string password, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            var result = NativeLib.VaultInitialize(vaultId, password);
            if (result != PrivStackError.Ok)
            {
                var message = result switch
                {
                    PrivStackError.PasswordTooShort => "Password must be at least 8 characters",
                    PrivStackError.VaultAlreadyInitialized => "Vault is already initialized",
                    PrivStackError.StorageError => "Storage error while initializing vault",
                    _ => $"VaultInitialize failed: {result}",
                };
                throw new InvalidOperationException(message);
            }
            return Task.CompletedTask;
        }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task VaultUnlock(string vaultId, string password, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            var result = NativeLib.VaultUnlock(vaultId, password);
            if (result != PrivStackError.Ok)
            {
                var message = result switch
                {
                    PrivStackError.AuthError => "Invalid password",
                    PrivStackError.VaultNotFound => "Vault not found",
                    PrivStackError.NotInitialized => "Vault has not been initialized",
                    PrivStackError.StorageError => "Storage error while unlocking vault",
                    _ => $"VaultUnlock failed: {result}",
                };
                throw new InvalidOperationException(message);
            }
            return Task.CompletedTask;
        }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task VaultLock(string vaultId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try { NativeLib.VaultLock(vaultId); return Task.CompletedTask; }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task<bool> VaultIsUnlocked(string vaultId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) return Task.FromResult(false);
        try { return Task.FromResult(NativeLib.VaultIsUnlocked(vaultId)); }
        finally { _switchLock.ExitReadLock(); }
    }

    public async Task<bool> RequestVaultUnlockAsync(string vaultId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Already unlocked — nothing to do
        if (await VaultIsUnlocked(vaultId, ct)) return true;

        if (_vaultUnlockPrompt == null)
        {
            _log.Warning("RequestVaultUnlockAsync called but no unlock prompt is registered");
            return false;
        }

        var password = await _vaultUnlockPrompt(vaultId, ct);
        if (string.IsNullOrEmpty(password)) return false;

        try
        {
            // Initialize if needed
            if (!await VaultIsInitialized(vaultId, ct))
                await VaultInitialize(vaultId, password, ct);

            await VaultUnlock(vaultId, password, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to unlock vault {VaultId} via user prompt", vaultId);
            return false;
        }
    }

    public Task VaultBlobStore(string vaultId, string blobId, byte[] data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    var result = NativeLib.VaultBlobStore(vaultId, blobId, (nint)ptr, (nuint)data.Length);
                    if (result != PrivStackError.Ok)
                        throw new InvalidOperationException($"VaultBlobStore failed: {result}");
                }
            }
            return Task.CompletedTask;
        }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task<byte[]> VaultBlobRead(string vaultId, string blobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            var result = NativeLib.VaultBlobRead(vaultId, blobId, out var outData, out var outLen);
            if (result != PrivStackError.Ok)
                throw new InvalidOperationException($"VaultBlobRead failed: {result}");
            try
            {
                var data = new byte[(int)outLen];
                System.Runtime.InteropServices.Marshal.Copy(outData, data, 0, (int)outLen);
                return Task.FromResult(data);
            }
            finally
            {
                NativeLib.FreeBytes(outData, outLen);
            }
        }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task VaultBlobDelete(string vaultId, string blobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            var result = NativeLib.VaultBlobDelete(vaultId, blobId);
            if (result != PrivStackError.Ok)
                throw new InvalidOperationException($"VaultBlobDelete failed: {result}");
            return Task.CompletedTask;
        }
        finally { _switchLock.ExitReadLock(); }
    }

    // =========================================================================
    // Blob (Unencrypted Blob Storage)
    // =========================================================================

    public Task BlobStore(string ns, string blobId, byte[] data, string? metadataJson = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    var result = NativeLib.BlobStore(ns, blobId, (nint)ptr, (nuint)data.Length, metadataJson);
                    if (result != PrivStackError.Ok)
                        throw new InvalidOperationException($"BlobStore failed: {result}");
                }
            }
            return Task.CompletedTask;
        }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task<byte[]> BlobRead(string ns, string blobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            var result = NativeLib.BlobRead(ns, blobId, out var outData, out var outLen);
            if (result != PrivStackError.Ok)
                throw new InvalidOperationException($"BlobRead failed: {result}");
            try
            {
                var data = new byte[(int)outLen];
                System.Runtime.InteropServices.Marshal.Copy(outData, data, 0, (int)outLen);
                return Task.FromResult(data);
            }
            finally
            {
                NativeLib.FreeBytes(outData, outLen);
            }
        }
        finally { _switchLock.ExitReadLock(); }
    }

    public Task BlobDelete(string ns, string blobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!AcquireReadLock(out _)) throw new InvalidOperationException("Workspace switch in progress");
        try
        {
            var result = NativeLib.BlobDelete(ns, blobId);
            if (result != PrivStackError.Ok)
                throw new InvalidOperationException($"BlobDelete failed: {result}");
            return Task.CompletedTask;
        }
        finally { _switchLock.ExitReadLock(); }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Attempts to acquire the read lock with a 5-second timeout.
    /// Returns false if the lock could not be acquired (workspace switch in progress).
    /// </summary>
    private bool AcquireReadLock(out string? error)
    {
        if (!_switchLock.TryEnterReadLock(TimeSpan.FromSeconds(5)))
        {
            error = "Workspace switch in progress";
            return false;
        }
        error = null;
        return true;
    }

    // =========================================================================
    // Sync Outbound Helpers
    // =========================================================================

    private static bool IsMutationAction(SdkAction action) => action is
        SdkAction.Create or SdkAction.Update or SdkAction.Delete or
        SdkAction.Trash or SdkAction.Restore;

    /// <summary>
    /// Notifies the outbound sync service after a successful mutation.
    /// Uses the response "data" (complete entity from Rust) for the sync snapshot,
    /// falling back to the request payload if no response data is available.
    /// </summary>
    private void NotifySyncIfMutation(SdkMessage message, string? responseJson = null)
    {
        if (_syncOutbound == null || !IsMutationAction(message.Action))
            return;

        var entityId = message.EntityId;

        // Try to extract the full entity from the response "data" field.
        // This is the complete entity after Rust processing (includes defaults, timestamps, etc.)
        string? snapshotPayload = null;
        if (!string.IsNullOrEmpty(responseJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("data", out var dataProp) &&
                    dataProp.ValueKind == JsonValueKind.Object)
                {
                    snapshotPayload = dataProp.GetRawText();

                    // Also extract entity ID from response data if not set
                    if (string.IsNullOrEmpty(entityId) &&
                        dataProp.TryGetProperty("id", out var idProp))
                    {
                        entityId = idProp.GetString();
                    }
                }
            }
            catch
            {
                // Fall through to request payload
            }
        }

        // Fallback: extract entity ID from request payload (for creates)
        if (string.IsNullOrEmpty(entityId) && !string.IsNullOrEmpty(message.Payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(message.Payload);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                    entityId = idProp.GetString();
            }
            catch
            {
                // Payload isn't valid JSON or has no "id" — skip sync notification
            }
        }

        if (string.IsNullOrEmpty(entityId)) return;

        // Prefer full entity from response; fall back to request payload
        _syncOutbound.NotifyEntityChanged(entityId, message.EntityType, snapshotPayload ?? message.Payload);
    }

    public void Dispose()
    {
        _switchLock.Dispose();
    }
}
