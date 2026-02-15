using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Tests.ViewModels;

/// <summary>
/// Tests for cloud sync state in StorageStatePillViewModel.
/// Note: Full instantiation requires App.Services (SyncViewModel creates SyncPairingViewModel).
/// These tests verify the model types and CloudSyncStatus behavior used by the pill.
/// </summary>
public class StorageStatePillCloudTests
{
    [Fact]
    public void CloudSyncStatus_ReportsCorrectState_WhenSyncing()
    {
        var status = new CloudSyncStatus
        {
            IsSyncing = true,
            PendingUploadCount = 5,
            ConnectedDevices = 2,
            LastSyncAt = new DateTime(2026, 2, 15, 10, 30, 0)
        };

        status.IsSyncing.Should().BeTrue();
        status.PendingUploadCount.Should().Be(5);
    }

    [Fact]
    public void CloudSyncStatus_ReportsNoPending_WhenSynced()
    {
        var status = new CloudSyncStatus
        {
            IsSyncing = true,
            PendingUploadCount = 0,
            ConnectedDevices = 1
        };

        status.IsSyncing.Should().BeTrue();
        status.PendingUploadCount.Should().Be(0);
    }

    [Fact]
    public void CloudSyncStatus_ReportsPaused_WhenNotSyncing()
    {
        var status = new CloudSyncStatus
        {
            IsSyncing = false,
            IsAuthenticated = true,
            PendingUploadCount = 0
        };

        status.IsSyncing.Should().BeFalse();
        status.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void SyncTier_PrivStackCloud_IsDistinctFromLocalOnly()
    {
        var workspace = new Workspace
        {
            Id = "ws1",
            Name = "Test",
            SyncTier = SyncTier.PrivStackCloud,
            CloudWorkspaceId = "cloud-ws-1"
        };

        workspace.SyncTier.Should().Be(SyncTier.PrivStackCloud);
        workspace.CloudWorkspaceId.Should().NotBeNull();
    }

    [Fact]
    public void SyncTier_LocalOnly_IsDefault()
    {
        var workspace = new Workspace
        {
            Id = "ws1",
            Name = "Test"
        };

        workspace.SyncTier.Should().Be(SyncTier.LocalOnly);
        workspace.CloudWorkspaceId.Should().BeNull();
    }
}
