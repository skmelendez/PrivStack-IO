using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Wraps the cloud sync FFI layer and manages OS lifecycle hooks
/// for immediate flush on sleep/close.
/// </summary>
public sealed class CloudSyncService : ICloudSyncService, IDisposable
{
    private bool _disposed;
    private bool _lifecycleHooked;

    public void Configure(string configJson)
    {
        ThrowIfError(NativeLib.CloudSyncConfigure(configJson));
    }

    // ── Authentication ──

    public CloudAuthTokens Authenticate(string email, string password)
    {
        var err = NativeLib.CloudSyncAuthenticate(email, password, out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<CloudAuthTokens>(ptr);
    }

    public void AuthenticateWithTokens(string accessToken, string refreshToken, long userId)
    {
        ThrowIfError(NativeLib.CloudSyncAuthenticateWithTokens(accessToken, refreshToken, userId));
    }

    public void Logout()
    {
        ThrowIfError(NativeLib.CloudSyncLogout());
    }

    public bool IsAuthenticated => NativeLib.CloudSyncIsAuthenticated();

    // ── Key Management ──

    public string SetupPassphrase(string passphrase)
    {
        var err = NativeLib.CloudSyncSetupPassphrase(passphrase, out var ptr);
        ThrowIfError(err);
        return MarshalAndFree(ptr);
    }

    public void EnterPassphrase(string passphrase)
    {
        ThrowIfError(NativeLib.CloudSyncEnterPassphrase(passphrase));
    }

    public void RecoverFromMnemonic(string mnemonic)
    {
        ThrowIfError(NativeLib.CloudSyncRecoverFromMnemonic(mnemonic));
    }

    public bool HasKeypair => NativeLib.CloudSyncHasKeypair();

    // ── Workspaces ──

    public CloudWorkspaceInfo RegisterWorkspace(string workspaceId, string name)
    {
        var err = NativeLib.CloudSyncRegisterWorkspace(workspaceId, name, out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<CloudWorkspaceInfo>(ptr);
    }

    public List<CloudWorkspaceInfo> ListWorkspaces()
    {
        var err = NativeLib.CloudSyncListWorkspaces(out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<List<CloudWorkspaceInfo>>(ptr);
    }

    public void DeleteWorkspace(string workspaceId)
    {
        ThrowIfError(NativeLib.CloudSyncDeleteWorkspace(workspaceId));
    }

    public CloudQuota GetQuota(string workspaceId)
    {
        var err = NativeLib.CloudSyncGetQuota(workspaceId, out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<CloudQuota>(ptr);
    }

    // ── Sync Engine ──

    public void StartSync(string workspaceId)
    {
        ThrowIfError(NativeLib.CloudSyncStartSync(workspaceId));
        RegisterLifecycleHooks();
    }

    public void StopSync()
    {
        ThrowIfError(NativeLib.CloudSyncStopSync());
    }

    public bool IsSyncing => NativeLib.CloudSyncIsSyncing();

    public CloudSyncStatus GetStatus()
    {
        var err = NativeLib.CloudSyncGetStatus(out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<CloudSyncStatus>(ptr);
    }

    public void ForceFlush()
    {
        ThrowIfError(NativeLib.CloudSyncForceFlush());
    }

    // ── Sharing ──

    public CloudShareInfo ShareEntity(string entityId, string entityType, string? entityName,
        string workspaceId, string recipientEmail, string permission)
    {
        var err = NativeLib.CloudSyncShareEntity(
            entityId, entityType, entityName,
            workspaceId, recipientEmail, permission, out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<CloudShareInfo>(ptr);
    }

    public void RevokeShare(string entityId, string recipientEmail)
    {
        ThrowIfError(NativeLib.CloudSyncRevokeShare(entityId, recipientEmail));
    }

    public void AcceptShare(string invitationToken)
    {
        ThrowIfError(NativeLib.CloudSyncAcceptShare(invitationToken));
    }

    public List<CloudShareInfo> ListEntityShares(string entityId)
    {
        var err = NativeLib.CloudSyncListEntityShares(entityId, out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<List<CloudShareInfo>>(ptr);
    }

    public List<SharedWithMeInfo> GetSharedWithMe()
    {
        var err = NativeLib.CloudSyncGetSharedWithMe(out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<List<SharedWithMeInfo>>(ptr);
    }

    // ── Devices ──

    public void RegisterDevice(string name, string platform)
    {
        ThrowIfError(NativeLib.CloudSyncRegisterDevice(name, platform));
    }

    public List<CloudDeviceInfo> ListDevices()
    {
        var err = NativeLib.CloudSyncListDevices(out var ptr);
        ThrowIfError(err);
        return DeserializeAndFree<List<CloudDeviceInfo>>(ptr);
    }

    // ── OS Lifecycle Hooks ──

    private void RegisterLifecycleHooks()
    {
        if (_lifecycleHooked) return;
        _lifecycleHooked = true;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.ShutdownRequested += OnShutdownRequested;
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        FlushIfSyncing();
    }

    private void FlushIfSyncing()
    {
        try
        {
            if (NativeLib.CloudSyncIsAuthenticated() && NativeLib.CloudSyncIsSyncing())
                NativeLib.CloudSyncForceFlush();
        }
        catch
        {
            // Best-effort — don't crash on shutdown
        }
    }

    // ── Helpers ──

    private static T DeserializeAndFree<T>(nint ptr)
    {
        var json = MarshalAndFree(ptr);
        return JsonSerializer.Deserialize<T>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}");
    }

    private static string MarshalAndFree(nint ptr)
    {
        try
        {
            return Marshal.PtrToStringUTF8(ptr)
                   ?? throw new InvalidOperationException("Null string from FFI");
        }
        finally
        {
            NativeLib.FreeString(ptr);
        }
    }

    private static void ThrowIfError(PrivStackError err)
    {
        if (err != PrivStackError.Ok)
            throw new PrivStackException($"Cloud sync operation failed: {err}", err);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.ShutdownRequested -= OnShutdownRequested;

        // Flush and stop sync on dispose
        FlushIfSyncing();
    }
}
