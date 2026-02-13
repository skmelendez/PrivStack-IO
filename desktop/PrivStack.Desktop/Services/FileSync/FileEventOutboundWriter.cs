using PrivStack.Desktop.Models;
using ProtoBuf;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Writes encrypted event files to the local peer's subdirectory
/// within the shared event store. Uses atomic write (tmp → rename)
/// for safe operation on network filesystems.
/// Files are serialized as Protocol Buffers for minimal size.
/// </summary>
internal sealed class FileEventOutboundWriter
{
    private static readonly ILogger _log = Log.ForContext<FileEventOutboundWriter>();

    /// <summary>
    /// Event files older than this are pruned. Events are short-lived deltas;
    /// snapshots handle long-term state for new/offline devices.
    /// </summary>
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// Only run pruning once per this interval to avoid hammering the network FS.
    /// </summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly string _outboundDir;
    private readonly string _localPeerId;
    private readonly byte[] _key;
    private DateTimeOffset _lastPruneTime = DateTimeOffset.MinValue;

    public FileEventOutboundWriter(string eventStoreDir, string localPeerId, byte[] key)
    {
        _localPeerId = localPeerId;
        _key = key;
        _outboundDir = Path.Combine(eventStoreDir, localPeerId);
        Directory.CreateDirectory(_outboundDir);
    }

    /// <summary>
    /// Encrypts and writes a single event file.
    /// Filename format: {unix_ms}_{uuid}.evt for natural sort order.
    /// </summary>
    public void Write(string entityId, string entityType, string payload)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var filename = $"{timestampMs}_{eventId}.evt";

        try
        {
            var (nonce, ciphertextWithTag) = FileEventEncryption.Encrypt(_key, payload);

            var envelope = new FileEventEnvelope
            {
                Version = 1,
                PeerId = _localPeerId,
                EventId = entityId,
                Timestamp = timestampMs,
                EntityType = entityType,
                Nonce = nonce,
                Ciphertext = ciphertextWithTag,
            };

            // Atomic write: serialize to .tmp then rename
            var finalPath = Path.Combine(_outboundDir, filename);
            var tmpPath = finalPath + ".tmp";

            using (var fs = File.Create(tmpPath))
            {
                Serializer.Serialize(fs, envelope);
            }

            File.Move(tmpPath, finalPath);

            _log.Debug("Wrote file event: {Filename} for {EntityType}/{EntityId}",
                filename, entityType, entityId);

            PruneExpiredFiles();
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "Failed to write file event {Filename} — will retry next cycle", filename);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected error writing file event {Filename}", filename);
        }
    }

    /// <summary>
    /// Deletes event files from this peer's outbound directory that are older
    /// than the retention period. Only runs once per PruneInterval to avoid
    /// hammering the network FS on every write.
    /// </summary>
    private void PruneExpiredFiles()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPruneTime < PruneInterval) return;
        _lastPruneTime = now;

        var cutoffMs = now.Subtract(RetentionPeriod).ToUnixTimeMilliseconds();

        try
        {
            var files = Directory.GetFiles(_outboundDir, "*.evt");
            var pruned = 0;

            foreach (var filePath in files)
            {
                var filename = Path.GetFileName(filePath);

                // Parse timestamp from filename: {unix_ms}_{uuid}.evt
                var underscoreIdx = filename.IndexOf('_');
                if (underscoreIdx <= 0) continue;

                if (!long.TryParse(filename.AsSpan(0, underscoreIdx), out var fileTimestampMs))
                    continue;

                if (fileTimestampMs >= cutoffMs) continue;

                try
                {
                    File.Delete(filePath);
                    pruned++;
                }
                catch (IOException)
                {
                    // Best effort — file may be locked or on unreliable network FS
                }
            }

            if (pruned > 0)
            {
                _log.Information("Pruned {Count} expired event files (>{Days}d old) from outbound dir",
                    pruned, RetentionPeriod.TotalDays);
            }
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "Failed to prune expired event files");
        }
    }
}
