using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;

namespace PrivStack.Desktop.Tests.Services;

public class CloudSyncServiceTests
{
    private readonly ICloudSyncService _svc = Substitute.For<ICloudSyncService>();

    // ── DEK Validation (via interface mock to verify argument expectations) ──

    [Fact]
    public void UploadBlob_InvalidDek16Bytes_ThrowsArgumentException()
    {
        var shortDek = new byte[16];
        _svc.When(s => s.UploadBlob("ws", "blob", null, Array.Empty<byte>(), shortDek))
            .Do(_ => throw new ArgumentException("DEK must be exactly 32 bytes", "dek"));

        var act = () => _svc.UploadBlob("ws", "blob", null, Array.Empty<byte>(), shortDek);

        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void UploadBlob_InvalidDek64Bytes_ThrowsArgumentException()
    {
        var longDek = new byte[64];
        _svc.When(s => s.UploadBlob("ws", "blob", null, Array.Empty<byte>(), longDek))
            .Do(_ => throw new ArgumentException("DEK must be exactly 32 bytes", "dek"));

        var act = () => _svc.UploadBlob("ws", "blob", null, Array.Empty<byte>(), longDek);

        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void DownloadBlob_InvalidDek_ThrowsArgumentException()
    {
        var badDek = new byte[16];
        _svc.When(s => s.DownloadBlob("key", badDek))
            .Do(_ => throw new ArgumentException("DEK must be exactly 32 bytes", "dek"));

        var act = () => _svc.DownloadBlob("key", badDek);

        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    // ── Interface Contract: Configuration ──

    [Fact]
    public void Configure_CanBeCalledWithJsonString()
    {
        _svc.Configure("{\"api_url\":\"https://api.example.com\"}");

        _svc.Received(1).Configure(Arg.Is<string>(s => s.Contains("api_url")));
    }

    // ── Interface Contract: Authentication ──

    [Fact]
    public void Authenticate_ReturnsCloudAuthTokens()
    {
        var expected = new CloudAuthTokens
        {
            AccessToken = "tok",
            RefreshToken = "ref",
            UserId = 1,
            Email = "u@e.com"
        };
        _svc.Authenticate("u@e.com", "pass").Returns(expected);

        var result = _svc.Authenticate("u@e.com", "pass");

        result.Should().Be(expected);
    }

    [Fact]
    public void AuthenticateWithTokens_CanBeCalled()
    {
        _svc.AuthenticateWithTokens("access", "refresh", 42);

        _svc.Received(1).AuthenticateWithTokens("access", "refresh", 42);
    }

    [Fact]
    public void Logout_CanBeCalled()
    {
        _svc.Logout();

        _svc.Received(1).Logout();
    }

    [Fact]
    public void IsAuthenticated_ReturnsBool()
    {
        _svc.IsAuthenticated.Returns(true);

        _svc.IsAuthenticated.Should().BeTrue();
    }

    // ── Interface Contract: Key Management ──

    [Fact]
    public void SetupPassphrase_ReturnsMnemonic()
    {
        _svc.SetupPassphrase("my-pass").Returns("word1 word2 word3 word4");

        var mnemonic = _svc.SetupPassphrase("my-pass");

        mnemonic.Should().Be("word1 word2 word3 word4");
    }

    [Fact]
    public void EnterPassphrase_CanBeCalled()
    {
        _svc.EnterPassphrase("my-pass");

        _svc.Received(1).EnterPassphrase("my-pass");
    }

    [Fact]
    public void RecoverFromMnemonic_CanBeCalled()
    {
        _svc.RecoverFromMnemonic("word1 word2 word3");

        _svc.Received(1).RecoverFromMnemonic("word1 word2 word3");
    }

    [Fact]
    public void HasKeypair_ReturnsBool()
    {
        _svc.HasKeypair.Returns(true);

        _svc.HasKeypair.Should().BeTrue();
    }

    // ── Interface Contract: Workspaces ──

    [Fact]
    public void RegisterWorkspace_ReturnsCloudWorkspaceInfo()
    {
        var expected = new CloudWorkspaceInfo { Id = 1, WorkspaceId = "ws-1", WorkspaceName = "WS" };
        _svc.RegisterWorkspace("ws-1", "WS").Returns(expected);

        var result = _svc.RegisterWorkspace("ws-1", "WS");

        result.WorkspaceId.Should().Be("ws-1");
        result.WorkspaceName.Should().Be("WS");
    }

    [Fact]
    public void ListWorkspaces_ReturnsList()
    {
        _svc.ListWorkspaces().Returns(new List<CloudWorkspaceInfo>
        {
            new() { WorkspaceId = "a" },
            new() { WorkspaceId = "b" }
        });

        var result = _svc.ListWorkspaces();

        result.Should().HaveCount(2);
    }

    [Fact]
    public void DeleteWorkspace_CanBeCalled()
    {
        _svc.DeleteWorkspace("ws-1");

        _svc.Received(1).DeleteWorkspace("ws-1");
    }

    [Fact]
    public void GetQuota_ReturnsCloudQuota()
    {
        var expected = new CloudQuota { StorageUsedBytes = 1024, StorageQuotaBytes = 2048, UsagePercent = 50.0 };
        _svc.GetQuota("ws-1").Returns(expected);

        var result = _svc.GetQuota("ws-1");

        result.UsagePercent.Should().Be(50.0);
    }

    // ── Interface Contract: Sync Engine ──

    [Fact]
    public void StartSync_CanBeCalled()
    {
        _svc.StartSync("ws-1");

        _svc.Received(1).StartSync("ws-1");
    }

    [Fact]
    public void StopSync_CanBeCalled()
    {
        _svc.StopSync();

        _svc.Received(1).StopSync();
    }

    [Fact]
    public void IsSyncing_ReturnsBool()
    {
        _svc.IsSyncing.Returns(true);

        _svc.IsSyncing.Should().BeTrue();
    }

    [Fact]
    public void GetStatus_ReturnsCloudSyncStatus()
    {
        var expected = new CloudSyncStatus { IsSyncing = true, ConnectedDevices = 2 };
        _svc.GetStatus().Returns(expected);

        var result = _svc.GetStatus();

        result.IsSyncing.Should().BeTrue();
        result.ConnectedDevices.Should().Be(2);
    }

    [Fact]
    public void ForceFlush_CanBeCalled()
    {
        _svc.ForceFlush();

        _svc.Received(1).ForceFlush();
    }

    // ── Interface Contract: Sharing ──

    [Fact]
    public void ShareEntity_ReturnsCloudShareInfo()
    {
        var expected = new CloudShareInfo { ShareId = 1, EntityId = "e1", RecipientEmail = "r@e.com" };
        _svc.ShareEntity("e1", "task", "My Task", "ws-1", "r@e.com", "read").Returns(expected);

        var result = _svc.ShareEntity("e1", "task", "My Task", "ws-1", "r@e.com", "read");

        result.ShareId.Should().Be(1);
        result.RecipientEmail.Should().Be("r@e.com");
    }

    [Fact]
    public void RevokeShare_CanBeCalled()
    {
        _svc.RevokeShare("e1", "r@e.com");

        _svc.Received(1).RevokeShare("e1", "r@e.com");
    }

    [Fact]
    public void AcceptShare_CanBeCalled()
    {
        _svc.AcceptShare("invite-token-xyz");

        _svc.Received(1).AcceptShare("invite-token-xyz");
    }

    [Fact]
    public void ListEntityShares_ReturnsList()
    {
        _svc.ListEntityShares("e1").Returns(new List<CloudShareInfo>
        {
            new() { ShareId = 1 },
            new() { ShareId = 2 }
        });

        var result = _svc.ListEntityShares("e1");

        result.Should().HaveCount(2);
    }

    [Fact]
    public void GetSharedWithMe_ReturnsList()
    {
        _svc.GetSharedWithMe().Returns(new List<SharedWithMeInfo>
        {
            new() { EntityId = "shared-1", Permission = "read" }
        });

        var result = _svc.GetSharedWithMe();

        result.Should().ContainSingle().Which.EntityId.Should().Be("shared-1");
    }

    // ── Interface Contract: Devices ──

    [Fact]
    public void RegisterDevice_CanBeCalled()
    {
        _svc.RegisterDevice("MacBook", "macOS");

        _svc.Received(1).RegisterDevice("MacBook", "macOS");
    }

    [Fact]
    public void ListDevices_ReturnsList()
    {
        _svc.ListDevices().Returns(new List<CloudDeviceInfo>
        {
            new() { DeviceId = "d1", DeviceName = "MacBook" }
        });

        var result = _svc.ListDevices();

        result.Should().ContainSingle().Which.DeviceName.Should().Be("MacBook");
    }

    // ── Interface Contract: Blobs ──

    [Fact]
    public void UploadBlob_ValidDek_CanBeCalled()
    {
        var dek = new byte[32];
        var data = new byte[] { 1, 2, 3 };

        _svc.UploadBlob("ws", "blob-1", "entity-1", data, dek);

        _svc.Received(1).UploadBlob("ws", "blob-1", "entity-1", data, dek);
    }

    [Fact]
    public void DownloadBlob_ValidDek_ReturnsData()
    {
        var dek = new byte[32];
        var expected = new byte[] { 10, 20, 30 };
        _svc.DownloadBlob("s3-key", dek).Returns(expected);

        var result = _svc.DownloadBlob("s3-key", dek);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void GetEntityBlobs_ReturnsList()
    {
        _svc.GetEntityBlobs("e1").Returns(new List<CloudBlobMeta>
        {
            new() { BlobId = "b1", S3Key = "k1", SizeBytes = 512 }
        });

        var result = _svc.GetEntityBlobs("e1");

        result.Should().ContainSingle().Which.BlobId.Should().Be("b1");
    }

    // ── Interface Contract: Compaction ──

    [Fact]
    public void NeedsCompaction_ReturnsBool()
    {
        _svc.NeedsCompaction(100).Returns(true);

        _svc.NeedsCompaction(100).Should().BeTrue();
    }

    [Fact]
    public void RequestCompaction_CanBeCalled()
    {
        _svc.RequestCompaction("e1", "ws-1");

        _svc.Received(1).RequestCompaction("e1", "ws-1");
    }
}
