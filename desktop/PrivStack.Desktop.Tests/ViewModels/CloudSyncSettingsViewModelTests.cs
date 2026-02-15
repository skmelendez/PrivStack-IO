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
        IWorkspaceService? workspaceService = null)
    {
        return new CloudSyncSettingsViewModel(
            cloudSync ?? Substitute.For<ICloudSyncService>(),
            workspaceService ?? Substitute.For<IWorkspaceService>(),
            new OAuthLoginService(),
            new PrivStackApiClient(),
            Substitute.For<IAppSettingsService>());
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
        vm.ShowPassphraseSetup.Should().BeFalse();
        vm.ShowDashboard.Should().BeFalse();
    }

    [Fact]
    public void ShowsPassphraseSetup_WhenAuthenticated_NoKeypair()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(true);
        cloudSync.HasKeypair.Returns(false);

        var vm = CreateVm(cloudSync);
        vm.IsAuthenticated = true;
        vm.HasKeypair = false;
        vm.NeedsPassphraseEntry = false;

        vm.ShowPassphraseSetup.Should().BeTrue();
        vm.ShowConnectButton.Should().BeFalse();
        vm.ShowDashboard.Should().BeFalse();
    }

    [Fact]
    public void ShowsDashboard_WhenAuthenticatedWithKeypair()
    {
        var vm = CreateVm();
        vm.IsAuthenticated = true;
        vm.HasKeypair = true;
        vm.NeedsPassphraseEntry = false;

        vm.ShowDashboard.Should().BeTrue();
        vm.ShowConnectButton.Should().BeFalse();
        vm.ShowPassphraseSetup.Should().BeFalse();
    }

    [Fact]
    public void ShowsPassphraseEntry_WhenNeedsPassphrase()
    {
        var vm = CreateVm();
        vm.IsAuthenticated = true;
        vm.HasKeypair = true;
        vm.NeedsPassphraseEntry = true;

        vm.ShowPassphraseEntry.Should().BeTrue();
        vm.ShowDashboard.Should().BeFalse();
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
        vm.HasKeypair = true;
        vm.NeedsPassphraseEntry = false;

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
        vm.HasKeypair = true;
        vm.NeedsPassphraseEntry = false;

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
        vm.HasKeypair = true;

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.IsAuthenticated.Should().BeFalse();
        vm.HasKeypair.Should().BeFalse();
        vm.IsSyncing.Should().BeFalse();
    }

    // ========================================
    // Passphrase Tests
    // ========================================

    [Fact]
    public async Task SetupPassphraseAsync_ValidatesMatch()
    {
        var vm = CreateVm();
        vm.Passphrase = "password123";
        vm.ConfirmPassphrase = "differentpassword";

        await vm.SetupPassphraseCommand.ExecuteAsync(null);

        vm.PassphraseError.Should().Be("Passphrases do not match.");
    }

    [Fact]
    public async Task SetupPassphraseAsync_ValidatesMinLength()
    {
        var vm = CreateVm();
        vm.Passphrase = "short";
        vm.ConfirmPassphrase = "short";

        await vm.SetupPassphraseCommand.ExecuteAsync(null);

        vm.PassphraseError.Should().Contain("at least 8");
    }

    [Fact]
    public async Task SetupPassphraseAsync_ShowsMnemonic_OnSuccess()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.SetupPassphrase("password123").Returns("word1 word2 word3 word4");

        var vm = CreateVm(cloudSync);
        vm.Passphrase = "password123";
        vm.ConfirmPassphrase = "password123";

        await vm.SetupPassphraseCommand.ExecuteAsync(null);

        vm.MnemonicWords.Should().Be("word1 word2 word3 word4");
        vm.ShowMnemonic.Should().BeTrue();
        vm.HasKeypair.Should().BeTrue();
    }

    [Fact]
    public async Task EnterPassphraseAsync_CallsService()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(true);
        cloudSync.HasKeypair.Returns(true);

        var vm = CreateVm(cloudSync);
        vm.NeedsPassphraseEntry = true;
        vm.EnterPassphrase = "my-passphrase";

        await vm.EnterPassphraseCommand.ExecuteAsync(null);

        cloudSync.Received(1).EnterPassphrase("my-passphrase");
        vm.NeedsPassphraseEntry.Should().BeFalse();
    }

    [Fact]
    public async Task RecoverFromMnemonicAsync_CallsService()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(true);

        var vm = CreateVm(cloudSync);
        vm.ShowRecoveryForm = true;
        vm.RecoveryMnemonic = "word1 word2 word3";

        await vm.RecoverFromMnemonicCommand.ExecuteAsync(null);

        cloudSync.Received(1).RecoverFromMnemonic("word1 word2 word3");
        vm.ShowRecoveryForm.Should().BeFalse();
        vm.HasKeypair.Should().BeTrue();
    }

    [Fact]
    public void ShowRecovery_TogglesRecoveryForm()
    {
        var vm = CreateVm();
        vm.NeedsPassphraseEntry = true;

        vm.ShowRecoveryCommand.Execute(null);

        vm.ShowRecoveryForm.Should().BeTrue();
        vm.NeedsPassphraseEntry.Should().BeFalse();
    }

    [Fact]
    public void CancelRecovery_RestoresPassphraseEntry()
    {
        var vm = CreateVm();
        vm.HasKeypair = true;
        vm.ShowRecoveryForm = true;

        vm.CancelRecoveryCommand.Execute(null);

        vm.ShowRecoveryForm.Should().BeFalse();
        vm.NeedsPassphraseEntry.Should().BeTrue();
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
        // JWT with payload: {"sub": 42}
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
