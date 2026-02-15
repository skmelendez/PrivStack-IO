using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
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
            workspaceService ?? Substitute.For<IWorkspaceService>());
    }

    // ========================================
    // Auth State Tests
    // ========================================

    [Fact]
    public void ShowsAuthForm_WhenNotAuthenticated()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(false);

        var vm = CreateVm(cloudSync);

        vm.ShowAuthForm.Should().BeTrue();
        vm.ShowPassphraseSetup.Should().BeFalse();
        vm.ShowSyncDashboard.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticateAsync_SetsAuthenticated_OnSuccess()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(false);
        cloudSync.Authenticate("test@example.com", "password123")
            .Returns(new CloudAuthTokens
            {
                AccessToken = "tok",
                RefreshToken = "ref",
                UserId = 1,
                Email = "test@example.com"
            });

        var vm = CreateVm(cloudSync);
        vm.Email = "test@example.com";
        vm.Password = "password123";

        await vm.AuthenticateCommand.ExecuteAsync(null);

        vm.IsAuthenticated.Should().BeTrue();
        vm.AuthenticatedEmail.Should().Be("test@example.com");
        vm.Email.Should().BeEmpty();
        vm.Password.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticateAsync_SetsError_OnFailure()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.Authenticate(Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new PrivStackException("Invalid credentials", PrivStackError.AuthError));

        var vm = CreateVm(cloudSync);
        vm.Email = "test@example.com";
        vm.Password = "wrong";

        await vm.AuthenticateCommand.ExecuteAsync(null);

        vm.IsAuthenticated.Should().BeFalse();
        vm.AuthError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AuthenticateAsync_PreventsReentrantCalls()
    {
        var callCount = 0;
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.Authenticate(Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref callCount);
                Thread.Sleep(50);
                return new CloudAuthTokens { Email = "test@example.com" };
            });

        var vm = CreateVm(cloudSync);
        vm.Email = "test@example.com";
        vm.Password = "password123";

        // First call should proceed; second should be skipped (IsAuthenticating guard)
        var t1 = vm.AuthenticateCommand.ExecuteAsync(null);
        var t2 = vm.AuthenticateCommand.ExecuteAsync(null);
        await Task.WhenAll(t1, t2);

        callCount.Should().Be(1);
    }

    [Fact]
    public async Task LogoutAsync_ClearsState()
    {
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.IsAuthenticated.Returns(true);
        cloudSync.HasKeypair.Returns(true);

        var vm = CreateVm(cloudSync);
        vm.IsAuthenticated = true;
        vm.HasKeypair = true;
        vm.AuthenticatedEmail = "test@example.com";

        await vm.LogoutCommand.ExecuteAsync(null);

        vm.IsAuthenticated.Should().BeFalse();
        vm.HasKeypair.Should().BeFalse();
        vm.AuthenticatedEmail.Should().BeNull();
    }

    // ========================================
    // Passphrase Tests
    // ========================================

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
        vm.ShowAuthForm.Should().BeFalse();
        vm.ShowSyncDashboard.Should().BeFalse();
    }

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
        cloudSync.GetStatus().Returns(new CloudSyncStatus());

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
        cloudSync.GetStatus().Returns(new CloudSyncStatus());

        var vm = CreateVm(cloudSync);
        vm.ShowRecoveryForm = true;
        vm.RecoveryMnemonic = "word1 word2 word3";

        await vm.RecoverFromMnemonicCommand.ExecuteAsync(null);

        cloudSync.Received(1).RecoverFromMnemonic("word1 word2 word3");
        vm.ShowRecoveryForm.Should().BeFalse();
        vm.HasKeypair.Should().BeTrue();
    }

    // ========================================
    // Sync Dashboard Tests
    // ========================================

    [Fact]
    public void ShowsSyncDashboard_WhenAuthenticatedWithKeypair()
    {
        var vm = CreateVm();
        vm.IsAuthenticated = true;
        vm.HasKeypair = true;
        vm.NeedsPassphraseEntry = false;

        vm.ShowSyncDashboard.Should().BeTrue();
        vm.ShowAuthForm.Should().BeFalse();
        vm.ShowPassphraseSetup.Should().BeFalse();
    }

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
}
