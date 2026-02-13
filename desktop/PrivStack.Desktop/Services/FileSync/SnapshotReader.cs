using PrivStack.Desktop.Models;
using ProtoBuf;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Reads and decrypts snapshot files from peer directories.
/// Returns the most recent snapshot from any peer for bootstrapping.
/// </summary>
internal static class SnapshotReader
{
    private static readonly ILogger _log = Log.ForContext(nameof(SnapshotReader));

    /// <summary>
    /// Finds and decrypts the most recent peer snapshot in the snapshot directory.
    /// Skips the local peer's own snapshot. Returns null if no valid snapshots found.
    /// </summary>
    public static SyncSnapshotData? ReadLatestPeerSnapshot(
        string snapshotDir,
        string localPeerId,
        byte[] key)
    {
        if (!Directory.Exists(snapshotDir))
            return null;

        string[] snapFiles;
        try
        {
            snapFiles = Directory.GetFiles(snapshotDir, "*.snap");
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "Could not list snapshot files in {Dir}", snapshotDir);
            return null;
        }

        SyncSnapshotData? latest = null;
        long latestTimestamp = 0;

        foreach (var filePath in snapFiles)
        {
            var peerId = Path.GetFileNameWithoutExtension(filePath);
            if (peerId == localPeerId) continue;

            try
            {
                var data = ReadAndDecrypt(filePath, key);
                if (data != null && data.Timestamp > latestTimestamp)
                {
                    latest = data;
                    latestTimestamp = data.Timestamp;
                    _log.Debug("Found peer snapshot from {PeerId} at {Time}",
                        peerId, DateTimeOffset.FromUnixTimeMilliseconds(data.Timestamp));
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to read snapshot from peer {PeerId}", peerId);
            }
        }

        return latest;
    }

    private static SyncSnapshotData? ReadAndDecrypt(string filePath, byte[] key)
    {
        SyncSnapshot envelope;
        using (var fs = File.OpenRead(filePath))
        {
            envelope = Serializer.Deserialize<SyncSnapshot>(fs);
        }

        if (envelope.Version != 1)
        {
            _log.Warning("Unsupported snapshot version {Version}: {Path}", envelope.Version, filePath);
            return null;
        }

        var plainBytes = FileEventEncryption.DecryptBytes(key, envelope.Nonce, envelope.Ciphertext);

        using var ms = new MemoryStream(plainBytes);
        return Serializer.Deserialize<SyncSnapshotData>(ms);
    }
}
