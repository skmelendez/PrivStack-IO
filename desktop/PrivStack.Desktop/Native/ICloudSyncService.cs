using PrivStack.Desktop.Models;

namespace PrivStack.Desktop.Native;

/// <summary>
/// Interface for PrivStack Cloud Sync (S3-backed multi-device sync + sharing).
/// Distinct from <see cref="ICloudStorageService"/> which handles Google Drive / iCloud.
/// </summary>
public interface ICloudSyncService
{
    // ── Configuration ──
    void Configure(string configJson);

    // ── Authentication ──
    CloudAuthTokens Authenticate(string email, string password);
    void AuthenticateWithTokens(string accessToken, string refreshToken, long userId);
    void Logout();
    bool IsAuthenticated { get; }

    // ── Key Management ──
    string SetupPassphrase(string passphrase);
    void EnterPassphrase(string passphrase);
    void RecoverFromMnemonic(string mnemonic);
    bool HasKeypair { get; }

    // ── Workspaces ──
    CloudWorkspaceInfo RegisterWorkspace(string workspaceId, string name);
    List<CloudWorkspaceInfo> ListWorkspaces();
    void DeleteWorkspace(string workspaceId);
    CloudQuota GetQuota(string workspaceId);

    // ── Sync Engine ──
    void StartSync(string workspaceId);
    void StopSync();
    bool IsSyncing { get; }
    CloudSyncStatus GetStatus();
    void ForceFlush();

    // ── Sharing ──
    CloudShareInfo ShareEntity(string entityId, string entityType, string? entityName,
        string workspaceId, string recipientEmail, string permission);
    void RevokeShare(string entityId, string recipientEmail);
    void AcceptShare(string invitationToken);
    List<CloudShareInfo> ListEntityShares(string entityId);
    List<SharedWithMeInfo> GetSharedWithMe();

    // ── Devices ──
    void RegisterDevice(string name, string platform);
    List<CloudDeviceInfo> ListDevices();
}
