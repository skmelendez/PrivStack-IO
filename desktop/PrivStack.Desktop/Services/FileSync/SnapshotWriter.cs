using PrivStack.Desktop.Models;
using ProtoBuf;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Serializes, encrypts, and writes a snapshot to the shared snapshot directory.
/// Uses atomic write (tmp → rename) for safe operation on network filesystems.
/// </summary>
internal static class SnapshotWriter
{
    private static readonly ILogger _log = Log.ForContext(nameof(SnapshotWriter));

    /// <summary>
    /// Writes an encrypted snapshot to {snapshotDir}/{peerId}.snap.
    /// </summary>
    public static void Write(
        SyncSnapshotData data,
        string snapshotDir,
        string peerId,
        byte[] key)
    {
        Directory.CreateDirectory(snapshotDir);

        // Serialize inner data to protobuf bytes
        byte[] plainBytes;
        using (var ms = new MemoryStream())
        {
            Serializer.Serialize(ms, data);
            plainBytes = ms.ToArray();
        }

        // Encrypt
        var (nonce, ciphertextWithTag) = FileEventEncryption.EncryptBytes(key, plainBytes);

        var envelope = new SyncSnapshot
        {
            Version = 1,
            PeerId = peerId,
            Timestamp = data.Timestamp,
            Nonce = nonce,
            Ciphertext = ciphertextWithTag,
        };

        // Atomic write: .tmp → rename
        var finalPath = Path.Combine(snapshotDir, $"{peerId}.snap");
        var tmpPath = finalPath + ".tmp";

        using (var fs = File.Create(tmpPath))
        {
            Serializer.Serialize(fs, envelope);
        }

        File.Move(tmpPath, finalPath, overwrite: true);

        _log.Information("Wrote snapshot: {Size} bytes ({Types} types) to {Path}",
            new FileInfo(finalPath).Length, data.EntityTypes.Count, finalPath);
    }
}
