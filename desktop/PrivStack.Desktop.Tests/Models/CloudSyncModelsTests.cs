using System.Text.Json;
using PrivStack.Desktop.Models;

namespace PrivStack.Desktop.Tests.Models;

public class CloudSyncModelsTests
{
    // ── SyncTier ──

    [Fact]
    public void SyncTier_LocalOnly_Exists()
    {
        SyncTier.LocalOnly.Should().BeDefined();
    }

    [Fact]
    public void SyncTier_DefaultValue_IsLocalOnly()
    {
        default(SyncTier).Should().Be(SyncTier.LocalOnly);
    }

    [Theory]
    [InlineData(SyncTier.LocalOnly, 0)]
    [InlineData(SyncTier.NetworkRelay, 1)]
    [InlineData(SyncTier.PrivStackCloud, 2)]
    public void SyncTier_IntegerCasts_AreCorrect(SyncTier tier, int expected)
    {
        ((int)tier).Should().Be(expected);
    }

    // ── CloudSyncStatus ──

    [Fact]
    public void CloudSyncStatus_JsonRoundTrip()
    {
        var status = new CloudSyncStatus
        {
            IsSyncing = true,
            IsAuthenticated = true,
            ActiveWorkspace = "ws-123",
            PendingUploadCount = 5,
            LastSyncAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ConnectedDevices = 3
        };

        var json = JsonSerializer.Serialize(status);
        var deserialized = JsonSerializer.Deserialize<CloudSyncStatus>(json);

        deserialized.Should().NotBeNull();
        deserialized!.IsSyncing.Should().BeTrue();
        deserialized.IsAuthenticated.Should().BeTrue();
        deserialized.ActiveWorkspace.Should().Be("ws-123");
        deserialized.PendingUploadCount.Should().Be(5);
        deserialized.LastSyncAt.Should().Be(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        deserialized.ConnectedDevices.Should().Be(3);
    }

    [Fact]
    public void CloudSyncStatus_Defaults_AllFalseNullZero()
    {
        var status = new CloudSyncStatus();

        status.IsSyncing.Should().BeFalse();
        status.IsAuthenticated.Should().BeFalse();
        status.ActiveWorkspace.Should().BeNull();
        status.PendingUploadCount.Should().Be(0);
        status.LastSyncAt.Should().BeNull();
        status.ConnectedDevices.Should().Be(0);
    }

    [Fact]
    public void CloudSyncStatus_JsonPropertyNames_AreSnakeCase()
    {
        var status = new CloudSyncStatus { IsSyncing = true, PendingUploadCount = 7 };
        var json = JsonSerializer.Serialize(status);

        json.Should().Contain("\"is_syncing\"");
        json.Should().Contain("\"is_authenticated\"");
        json.Should().Contain("\"active_workspace\"");
        json.Should().Contain("\"pending_upload_count\"");
        json.Should().Contain("\"last_sync_at\"");
        json.Should().Contain("\"connected_devices\"");
    }

    [Fact]
    public void CloudSyncStatus_NullableLastSyncAt_SerializesCorrectly()
    {
        var status = new CloudSyncStatus { LastSyncAt = null };
        var json = JsonSerializer.Serialize(status);
        var deserialized = JsonSerializer.Deserialize<CloudSyncStatus>(json);

        deserialized!.LastSyncAt.Should().BeNull();
    }

    // ── CloudQuota ──

    [Fact]
    public void CloudQuota_JsonRoundTrip()
    {
        var quota = new CloudQuota
        {
            StorageUsedBytes = 5_368_709_120,
            StorageQuotaBytes = 10_737_418_240,
            UsagePercent = 50.0
        };

        var json = JsonSerializer.Serialize(quota);
        var deserialized = JsonSerializer.Deserialize<CloudQuota>(json);

        deserialized.Should().NotBeNull();
        deserialized!.StorageUsedBytes.Should().Be(5_368_709_120);
        deserialized.StorageQuotaBytes.Should().Be(10_737_418_240);
        deserialized.UsagePercent.Should().Be(50.0);
    }

    [Fact]
    public void CloudQuota_FormatBytes_ZeroBytes()
    {
        var quota = new CloudQuota { StorageUsedBytes = 0, StorageQuotaBytes = 1024 };
        quota.UsedDisplay.Should().Be("0 B");
    }

    [Fact]
    public void CloudQuota_FormatBytes_Kilobytes()
    {
        var quota = new CloudQuota { StorageUsedBytes = 512 * 1024, StorageQuotaBytes = 1024 * 1024 };
        quota.UsedDisplay.Should().Be("512.0 KB");
    }

    [Fact]
    public void CloudQuota_FormatBytes_Megabytes()
    {
        var quota = new CloudQuota { StorageUsedBytes = 50 * 1024UL * 1024, StorageQuotaBytes = 100 * 1024UL * 1024 };
        quota.UsedDisplay.Should().Be("50.0 MB");
    }

    [Fact]
    public void CloudQuota_FormatBytes_Gigabytes()
    {
        var quota = new CloudQuota { StorageUsedBytes = 2UL * 1024 * 1024 * 1024, StorageQuotaBytes = 10UL * 1024 * 1024 * 1024 };
        quota.UsedDisplay.Should().Be("2.0 GB");
    }

    [Fact]
    public void CloudQuota_Summary_FormatsCorrectly()
    {
        var quota = new CloudQuota
        {
            StorageUsedBytes = 512 * 1024,
            StorageQuotaBytes = 10UL * 1024 * 1024 * 1024,
            UsagePercent = 0.005
        };

        quota.Summary.Should().Be("512.0 KB / 10.0 GB");
    }

    [Fact]
    public void CloudQuota_SeverityBrush_NotNull_WhenLow()
    {
        var quota = new CloudQuota { UsagePercent = 50.0 };
        quota.SeverityBrush.Should().NotBeNull();
    }

    [Fact]
    public void CloudQuota_SeverityBrush_NotNull_AtExactly80()
    {
        var quota = new CloudQuota { UsagePercent = 80.0 };
        quota.SeverityBrush.Should().NotBeNull();
    }

    [Fact]
    public void CloudQuota_SeverityBrush_NotNull_JustAbove80()
    {
        var quota = new CloudQuota { UsagePercent = 80.1 };
        quota.SeverityBrush.Should().NotBeNull();
    }

    [Fact]
    public void CloudQuota_SeverityBrush_NotNull_AtExactly95()
    {
        var quota = new CloudQuota { UsagePercent = 95.0 };
        quota.SeverityBrush.Should().NotBeNull();
    }

    [Fact]
    public void CloudQuota_SeverityBrush_NotNull_JustAbove95()
    {
        var quota = new CloudQuota { UsagePercent = 95.1 };
        quota.SeverityBrush.Should().NotBeNull();
    }

    // ── CloudWorkspaceInfo ──

    [Fact]
    public void CloudWorkspaceInfo_JsonRoundTrip()
    {
        var info = new CloudWorkspaceInfo
        {
            Id = 42,
            UserId = 7,
            WorkspaceId = "ws-abc",
            WorkspaceName = "My Workspace",
            S3Prefix = "users/7/ws-abc/",
            StorageUsedBytes = 1024 * 1024,
            StorageQuotaBytes = 10UL * 1024 * 1024 * 1024,
            CreatedAt = new DateTime(2025, 3, 15, 8, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(info);
        var deserialized = JsonSerializer.Deserialize<CloudWorkspaceInfo>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(42);
        deserialized.UserId.Should().Be(7);
        deserialized.WorkspaceId.Should().Be("ws-abc");
        deserialized.WorkspaceName.Should().Be("My Workspace");
        deserialized.S3Prefix.Should().Be("users/7/ws-abc/");
        deserialized.StorageUsedBytes.Should().Be(1024UL * 1024);
        deserialized.StorageQuotaBytes.Should().Be(10UL * 1024 * 1024 * 1024);
    }

    [Fact]
    public void CloudWorkspaceInfo_Defaults()
    {
        var info = new CloudWorkspaceInfo();

        info.Id.Should().Be(0);
        info.UserId.Should().Be(0);
        info.WorkspaceId.Should().BeEmpty();
        info.WorkspaceName.Should().BeEmpty();
        info.S3Prefix.Should().BeEmpty();
        info.StorageUsedBytes.Should().Be(0);
        info.StorageQuotaBytes.Should().Be(0);
    }

    [Fact]
    public void CloudWorkspaceInfo_StorageDisplay_FormatsCorrectly()
    {
        var info = new CloudWorkspaceInfo { StorageUsedBytes = 50UL * 1024 * 1024 };
        info.StorageDisplay.Should().Be("50.0 MB used");
    }

    [Fact]
    public void CloudWorkspaceInfo_StorageDisplay_ZeroBytes()
    {
        var info = new CloudWorkspaceInfo { StorageUsedBytes = 0 };
        info.StorageDisplay.Should().Be("0 B used");
    }

    // ── CloudAuthTokens ──

    [Fact]
    public void CloudAuthTokens_JsonRoundTrip()
    {
        var tokens = new CloudAuthTokens
        {
            AccessToken = "access-xyz",
            RefreshToken = "refresh-abc",
            UserId = 99,
            Email = "user@example.com"
        };

        var json = JsonSerializer.Serialize(tokens);
        var deserialized = JsonSerializer.Deserialize<CloudAuthTokens>(json);

        deserialized.Should().NotBeNull();
        deserialized!.AccessToken.Should().Be("access-xyz");
        deserialized.RefreshToken.Should().Be("refresh-abc");
        deserialized.UserId.Should().Be(99);
        deserialized.Email.Should().Be("user@example.com");
    }

    [Fact]
    public void CloudAuthTokens_Defaults()
    {
        var tokens = new CloudAuthTokens();

        tokens.AccessToken.Should().BeEmpty();
        tokens.RefreshToken.Should().BeEmpty();
        tokens.UserId.Should().Be(0);
        tokens.Email.Should().BeEmpty();
    }

    [Fact]
    public void CloudAuthTokens_RecordEquality()
    {
        var a = new CloudAuthTokens { AccessToken = "tok", RefreshToken = "ref", UserId = 1, Email = "a@b.com" };
        var b = new CloudAuthTokens { AccessToken = "tok", RefreshToken = "ref", UserId = 1, Email = "a@b.com" };

        a.Should().Be(b);
    }

    // ── CloudShareInfo ──

    [Fact]
    public void CloudShareInfo_JsonRoundTrip()
    {
        var share = new CloudShareInfo
        {
            ShareId = 10,
            EntityId = "entity-1",
            EntityType = "task",
            EntityName = "My Task",
            RecipientEmail = "other@example.com",
            Permission = "write",
            Status = "accepted",
            CreatedAt = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            AcceptedAt = new DateTime(2025, 5, 2, 0, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(share);
        var deserialized = JsonSerializer.Deserialize<CloudShareInfo>(json);

        deserialized.Should().NotBeNull();
        deserialized!.ShareId.Should().Be(10);
        deserialized.EntityId.Should().Be("entity-1");
        deserialized.EntityType.Should().Be("task");
        deserialized.EntityName.Should().Be("My Task");
        deserialized.RecipientEmail.Should().Be("other@example.com");
        deserialized.Permission.Should().Be("write");
        deserialized.Status.Should().Be("accepted");
        deserialized.AcceptedAt.Should().NotBeNull();
    }

    [Fact]
    public void CloudShareInfo_Defaults()
    {
        var share = new CloudShareInfo();

        share.ShareId.Should().Be(0);
        share.EntityId.Should().BeEmpty();
        share.EntityType.Should().BeEmpty();
        share.EntityName.Should().BeNull();
        share.RecipientEmail.Should().BeEmpty();
        share.Permission.Should().Be("read");
        share.Status.Should().Be("pending");
        share.AcceptedAt.Should().BeNull();
    }

    [Fact]
    public void CloudShareInfo_NullableEntityName_SerializesCorrectly()
    {
        var share = new CloudShareInfo { EntityName = null, AcceptedAt = null };
        var json = JsonSerializer.Serialize(share);
        var deserialized = JsonSerializer.Deserialize<CloudShareInfo>(json);

        deserialized!.EntityName.Should().BeNull();
        deserialized.AcceptedAt.Should().BeNull();
    }

    // ── SharedWithMeInfo ──

    [Fact]
    public void SharedWithMeInfo_JsonRoundTrip()
    {
        var shared = new SharedWithMeInfo
        {
            EntityId = "entity-5",
            EntityType = "page",
            EntityName = "Shared Page",
            OwnerUserId = 42,
            WorkspaceId = "ws-owner",
            Permission = "read"
        };

        var json = JsonSerializer.Serialize(shared);
        var deserialized = JsonSerializer.Deserialize<SharedWithMeInfo>(json);

        deserialized.Should().NotBeNull();
        deserialized!.EntityId.Should().Be("entity-5");
        deserialized.EntityType.Should().Be("page");
        deserialized.EntityName.Should().Be("Shared Page");
        deserialized.OwnerUserId.Should().Be(42);
        deserialized.WorkspaceId.Should().Be("ws-owner");
        deserialized.Permission.Should().Be("read");
    }

    [Fact]
    public void SharedWithMeInfo_Defaults()
    {
        var shared = new SharedWithMeInfo();

        shared.EntityId.Should().BeEmpty();
        shared.EntityType.Should().BeEmpty();
        shared.EntityName.Should().BeNull();
        shared.OwnerUserId.Should().Be(0);
        shared.WorkspaceId.Should().BeEmpty();
        shared.Permission.Should().Be("read");
    }

    [Fact]
    public void SharedWithMeInfo_NullableEntityName_SerializesCorrectly()
    {
        var shared = new SharedWithMeInfo { EntityName = null };
        var json = JsonSerializer.Serialize(shared);
        var deserialized = JsonSerializer.Deserialize<SharedWithMeInfo>(json);

        deserialized!.EntityName.Should().BeNull();
    }

    // ── CloudBlobMeta ──

    [Fact]
    public void CloudBlobMeta_JsonRoundTrip()
    {
        var blob = new CloudBlobMeta
        {
            BlobId = "blob-1",
            EntityId = "entity-9",
            S3Key = "users/7/ws/blob-1",
            SizeBytes = 4096,
            ContentHash = "sha256:abc123"
        };

        var json = JsonSerializer.Serialize(blob);
        var deserialized = JsonSerializer.Deserialize<CloudBlobMeta>(json);

        deserialized.Should().NotBeNull();
        deserialized!.BlobId.Should().Be("blob-1");
        deserialized.EntityId.Should().Be("entity-9");
        deserialized.S3Key.Should().Be("users/7/ws/blob-1");
        deserialized.SizeBytes.Should().Be(4096);
        deserialized.ContentHash.Should().Be("sha256:abc123");
    }

    [Fact]
    public void CloudBlobMeta_NullableOptionals()
    {
        var blob = new CloudBlobMeta { BlobId = "b", S3Key = "k", EntityId = null, ContentHash = null };
        var json = JsonSerializer.Serialize(blob);
        var deserialized = JsonSerializer.Deserialize<CloudBlobMeta>(json);

        deserialized!.EntityId.Should().BeNull();
        deserialized.ContentHash.Should().BeNull();
    }

    [Fact]
    public void CloudBlobMeta_Defaults()
    {
        var blob = new CloudBlobMeta();

        blob.BlobId.Should().BeEmpty();
        blob.EntityId.Should().BeNull();
        blob.S3Key.Should().BeEmpty();
        blob.SizeBytes.Should().Be(0);
        blob.ContentHash.Should().BeNull();
    }

    // ── CloudDeviceInfo ──

    [Fact]
    public void CloudDeviceInfo_JsonRoundTrip()
    {
        var device = new CloudDeviceInfo
        {
            DeviceId = "dev-1",
            DeviceName = "MacBook Pro",
            Platform = "macOS",
            LastSeenAt = new DateTime(2025, 7, 1, 10, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(device);
        var deserialized = JsonSerializer.Deserialize<CloudDeviceInfo>(json);

        deserialized.Should().NotBeNull();
        deserialized!.DeviceId.Should().Be("dev-1");
        deserialized.DeviceName.Should().Be("MacBook Pro");
        deserialized.Platform.Should().Be("macOS");
        deserialized.LastSeenAt.Should().Be(new DateTime(2025, 7, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void CloudDeviceInfo_AllNullOptionals()
    {
        var device = new CloudDeviceInfo { DeviceId = "dev-2", DeviceName = null, Platform = null, LastSeenAt = null };
        var json = JsonSerializer.Serialize(device);
        var deserialized = JsonSerializer.Deserialize<CloudDeviceInfo>(json);

        deserialized!.DeviceName.Should().BeNull();
        deserialized.Platform.Should().BeNull();
        deserialized.LastSeenAt.Should().BeNull();
    }

    [Fact]
    public void CloudDeviceInfo_Defaults()
    {
        var device = new CloudDeviceInfo();

        device.DeviceId.Should().BeEmpty();
        device.DeviceName.Should().BeNull();
        device.Platform.Should().BeNull();
        device.LastSeenAt.Should().BeNull();
    }
}
