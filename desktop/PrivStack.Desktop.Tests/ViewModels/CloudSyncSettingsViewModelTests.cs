using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Tests.ViewModels;

public class CloudSyncSettingsViewModelTests
{
    private static CloudSyncSettingsViewModel CreateVm(
        ICloudSyncService? cloudSync = null,
        IWorkspaceService? workspaceService = null,
        IMasterPasswordCache? passwordCache = null)
    {
        return new CloudSyncSettingsViewModel(
            cloudSync ?? Substitute.For<ICloudSyncService>(),
            workspaceService ?? Substitute.For<IWorkspaceService>(),
            new OAuthLoginService(),
            new PrivStackApiClient(),
            Substitute.For<IAppSettingsService>(),
            passwordCache ?? Substitute.For<IMasterPasswordCache>());
    }

    // ========================================
    // Visibility State Tests
    // ========================================

    [Fact]
    public void ShowsConnectButton_WhenNotAuthenticated()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(false);

        var vm = CreateVm(cloudSync);

        vm.ShowConnectButton.Should().BeTrue();
        vm.ShowDashboard.Should().BeFalse();
    }

    [Fact]
    public void ShowsDashboard_WhenAuthenticated()
    {
        var vm = CreateVm();
        vm.IsAuthenticated = true;

        vm.ShowDashboard.Should().BeTrue();
        vm.ShowConnectButton.Should().BeFalse();
    }

    [Fact]
    public void ShowEnableForWorkspace_WhenDashboardAndWorkspaceNotCloud()
    {
        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test",
            SyncTier = SyncTier.LocalOnly
        });

        var vm = CreateVm(workspaceService: workspaceService);
        vm.IsAuthenticated = true;

        vm.ShowEnableForWorkspace.Should().BeTrue();
        vm.IsWorkspaceCloudEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsWorkspaceCloudEnabled_WhenWorkspaceIsCloud()
    {
        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test",
            SyncTier = SyncTier.PrivStackCloud
        });

        var vm = CreateVm(workspaceService: workspaceService);
        vm.IsAuthenticated = true;

        vm.IsWorkspaceCloudEnabled.Should().BeTrue();
        vm.ShowEnableForWorkspace.Should().BeFalse();
    }

    // ========================================
    // Disconnect Tests
    // ========================================

    [Fact]
    public async Task DisconnectAsync_ClearsState()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        var vm = CreateVm(cloudSync);
        vm.IsAuthenticated = true;

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.IsAuthenticated.Should().BeFalse();
        vm.IsSyncing.Should().BeFalse();
        vm.Quota.Should().BeNull();
        vm.Devices.Should().BeEmpty();
    }

    // ========================================
    // Enable for Workspace Tests
    // ========================================

    [Fact]
    public async Task EnableForWorkspaceAsync_RegistersAndStartsSync()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.HasKeypair.Returns(true);
        cloudSync.GetStatus().Returns(new CloudSyncStatus { IsSyncing = true });
        cloudSync.ListDevices().Returns([]);

        var passwordCache = Substitute.For<IMasterPasswordCache>();
        passwordCache.Get().Returns("vault-password");

        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test"
        });

        var vm = CreateVm(cloudSync, workspaceService, passwordCache);
        vm.IsAuthenticated = true;

        await vm.EnableForWorkspaceCommand.ExecuteAsync(null);

        cloudSync.Received(1).RegisterWorkspace("ws1", "Test");
        cloudSync.Received(1).StartSync("ws1");
        vm.IsSyncing.Should().BeTrue();
    }

    [Fact]
    public async Task EnableForWorkspaceAsync_SetsUpKeypairAutomatically()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.HasKeypair.Returns(false);
        cloudSync.GetStatus().Returns(new CloudSyncStatus());
        cloudSync.ListDevices().Returns([]);

        var passwordCache = Substitute.For<IMasterPasswordCache>();
        passwordCache.Get().Returns("vault-password");

        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test"
        });

        var vm = CreateVm(cloudSync, workspaceService, passwordCache);
        vm.IsAuthenticated = true;

        await vm.EnableForWorkspaceCommand.ExecuteAsync(null);

        cloudSync.Received(1).SetupPassphrase("vault-password");
    }

    [Fact]
    public async Task EnableForWorkspaceAsync_FailsWhenVaultLocked()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.HasKeypair.Returns(false);

        var passwordCache = Substitute.For<IMasterPasswordCache>();
        passwordCache.Get().Returns(string.Empty);

        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test"
        });

        var vm = CreateVm(cloudSync, workspaceService, passwordCache);
        vm.IsAuthenticated = true;

        await vm.EnableForWorkspaceCommand.ExecuteAsync(null);

        vm.AuthError.Should().Contain("Vault is locked");
        cloudSync.DidNotReceive().SetupPassphrase(Arg.Any<string>());
    }

    // ========================================
    // Sync Dashboard Tests
    // ========================================

    [Fact]
    public async Task RefreshStatusAsync_UpdatesAllFields()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.GetStatus().Returns(new CloudSyncStatus
        {
            IsSyncing = true,
            PendingUploadCount = 5,
            ConnectedDevices = 2,
            LastSyncAt = new DateTime(2026, 2, 15, 10, 30, 0)
        });
        cloudSync.ListDevices().Returns(
        [
            new CloudDeviceInfo { DeviceId = "d1", DeviceName = "MacBook", Platform = "macOS" }
        ]);

        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test",
            CloudWorkspaceId = "cloud-ws-1"
        });
        cloudSync.GetQuota("ws1").Returns(new CloudQuota
        {
            StorageUsedBytes = 1024 * 1024,
            StorageQuotaBytes = 10UL * 1024 * 1024 * 1024,
            UsagePercent = 0.01
        });

        var vm = CreateVm(cloudSync, workspaceService);
        await vm.RefreshStatusCommand.ExecuteAsync(null);

        vm.IsSyncing.Should().BeTrue();
        vm.PendingUploadCount.Should().Be(5);
        vm.ConnectedDeviceCount.Should().Be(2);
        vm.Devices.Should().HaveCount(1);
        vm.Quota.Should().NotBeNull();
    }

    [Fact]
    public async Task StartSyncAsync_CallsService()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.GetStatus().Returns(new CloudSyncStatus { IsSyncing = true });
        cloudSync.ListDevices().Returns([]);
        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace { Id = "ws1", Name = "Test" });

        var vm = CreateVm(cloudSync, workspaceService);
        await vm.StartSyncCommand.ExecuteAsync(null);

        cloudSync.Received(1).StartSync("ws1");
        vm.IsSyncing.Should().BeTrue();
    }

    [Fact]
    public async Task StopSyncAsync_CallsService()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        var vm = CreateVm(cloudSync);
        vm.IsSyncing = true;

        await vm.StopSyncCommand.ExecuteAsync(null);

        cloudSync.Received(1).StopSync();
        vm.IsSyncing.Should().BeFalse();
    }

    // ========================================
    // Quota & Device Tests
    // ========================================

    [Fact]
    public async Task RefreshStatusAsync_UpdatesQuota()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.GetStatus().Returns(new CloudSyncStatus());
        cloudSync.GetQuota("ws1").Returns(new CloudQuota
        {
            StorageUsedBytes = 2UL * 1024 * 1024 * 1024,
            StorageQuotaBytes = 10UL * 1024 * 1024 * 1024,
            UsagePercent = 20.0
        });
        cloudSync.ListDevices().Returns([]);

        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace
        {
            Id = "ws1",
            Name = "Test",
            CloudWorkspaceId = "cws1"
        });

        var vm = CreateVm(cloudSync, workspaceService);
        await vm.RefreshStatusCommand.ExecuteAsync(null);

        vm.Quota.Should().NotBeNull();
        vm.Quota!.UsagePercent.Should().Be(20.0);
    }

    [Fact]
    public async Task RefreshStatusAsync_PopulatesDevices()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.GetStatus().Returns(new CloudSyncStatus());
        cloudSync.ListDevices().Returns(
        [
            new CloudDeviceInfo { DeviceId = "d1", DeviceName = "MacBook", Platform = "macOS" },
            new CloudDeviceInfo { DeviceId = "d2", DeviceName = "Desktop", Platform = "Windows" }
        ]);

        var workspaceService = Substitute.For<IWorkspaceService>();
        workspaceService.GetActiveWorkspace().Returns(new Workspace { Id = "ws1", Name = "Test" });

        var vm = CreateVm(cloudSync, workspaceService);
        await vm.RefreshStatusCommand.ExecuteAsync(null);

        vm.Devices.Should().HaveCount(2);
        vm.Devices[0].DeviceName.Should().Be("MacBook");
        vm.Devices[1].DeviceName.Should().Be("Desktop");
    }

    // ========================================
    // JWT Extraction Tests
    // ========================================

    [Fact]
    public void ExtractUserIdFromJwt_ParsesNumericSub()
    {
        var payload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"sub\":42}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"header.{payload}.signature";

        var userId = CloudSyncSettingsViewModel.ExtractUserIdFromJwt(jwt);

        userId.Should().Be(42);
    }

    [Fact]
    public void ExtractUserIdFromJwt_ParsesStringSub()
    {
        var payload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"sub\":\"123\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"header.{payload}.signature";

        var userId = CloudSyncSettingsViewModel.ExtractUserIdFromJwt(jwt);

        userId.Should().Be(123);
    }

    [Fact]
    public void ExtractUserIdFromJwt_ParsesIdClaim()
    {
        var payload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"id\":7,\"email\":\"a@b.com\",\"role\":\"user\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"header.{payload}.signature";

        var userId = CloudSyncSettingsViewModel.ExtractUserIdFromJwt(jwt);

        userId.Should().Be(7);
    }

    [Fact]
    public void ExtractUserIdFromJwt_ThrowsOnMissingSub()
    {
        var payload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"name\":\"test\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"header.{payload}.signature";

        var act = () => CloudSyncSettingsViewModel.ExtractUserIdFromJwt(jwt);

        act.Should().Throw<InvalidOperationException>();
    }
}
