using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Sdk.Messaging;
using ProtoBuf;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Scans the shared event store directory for new event files from other peers.
/// Uses a periodic timer (30s) as the primary mechanism for reliable network FS operation,
/// with an optional FileSystemWatcher for faster detection on local/responsive filesystems.
/// </summary>
internal sealed class FileEventInboundScanner : IDisposable
{
    private static readonly ILogger _log = Log.ForContext<FileEventInboundScanner>();

    private readonly string _eventStoreDir;
    private readonly string _localPeerId;
    private readonly string _statePath;
    private readonly byte[] _key;
    private readonly ISyncService _syncService;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private int _scanning; // re-entrancy guard
    private FileEventSyncState _state;
    private bool _disposed;

    // Debounce for FileSystemWatcher triggers
    private Timer? _watcherDebounce;
    private const int WatcherDebounceMs = 2000;

    public FileEventInboundScanner(
        string eventStoreDir,
        string localPeerId,
        string localWorkspaceDir,
        byte[] key,
        ISyncService syncService)
    {
        _eventStoreDir = eventStoreDir;
        _localPeerId = localPeerId;
        _key = key;
        _syncService = syncService;
        _statePath = Path.Combine(localWorkspaceDir, ".file_sync_state.json");
        _state = LoadState();
    }

    public void Start()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        // Start the polling loop
        _ = PollLoopAsync(_cts.Token);

        // Best-effort FileSystemWatcher for faster detection
        StartWatcher();

        _log.Information("File event inbound scanner started for {Dir}", _eventStoreDir);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _timer?.Dispose();
        _timer = null;

        StopWatcher();

        _watcherDebounce?.Dispose();
        _watcherDebounce = null;

        _log.Information("File event inbound scanner stopped");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Run an immediate scan on start
        ScanAllPeers();

        try
        {
            while (_timer != null && await _timer.WaitForNextTickAsync(ct))
            {
                ScanAllPeers();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
    }

    private void ScanAllPeers()
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;

        try
        {
            if (!Directory.Exists(_eventStoreDir)) return;

            var peerDirs = Directory.GetDirectories(_eventStoreDir);
            var stateChanged = false;

            foreach (var peerDir in peerDirs)
            {
                var peerId = Path.GetFileName(peerDir);
                if (peerId == _localPeerId) continue;

                stateChanged |= ScanPeerDirectory(peerId, peerDir);
            }

            if (stateChanged)
                SaveState();
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "IO error scanning event store — network FS may be unavailable");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error scanning event store directory");
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private bool ScanPeerDirectory(string peerId, string peerDir)
    {
        _state.ProcessedWatermarks.TryGetValue(peerId, out var watermark);
        watermark ??= string.Empty;

        string[] files;
        try
        {
            files = Directory.GetFiles(peerDir, "*.evt");
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "Could not list files in peer directory: {PeerId}", peerId);
            return false;
        }

        var processed = false;
        var lastProcessed = watermark;

        foreach (var filePath in files)
        {
            var filename = Path.GetFileName(filePath);

            // Skip files at or before the watermark
            if (string.CompareOrdinal(filename, watermark) <= 0) continue;

            // Skip temp files
            if (filename.EndsWith(".tmp", StringComparison.Ordinal)) continue;

            if (ProcessEventFile(peerId, filePath))
            {
                lastProcessed = filename;
                processed = true;
            }
        }

        if (processed)
        {
            _state.ProcessedWatermarks[peerId] = lastProcessed;
        }

        return processed;
    }

    private bool ProcessEventFile(string peerId, string filePath)
    {
        try
        {
            FileEventEnvelope envelope;
            using (var fs = File.OpenRead(filePath))
            {
                envelope = Serializer.Deserialize<FileEventEnvelope>(fs);
            }

            if (envelope.Version != 1)
            {
                _log.Warning("Unsupported event file version {Version}: {Path}", envelope.Version, filePath);
                return true;
            }

            var plaintext = FileEventEncryption.Decrypt(_key, envelope.Nonce, envelope.Ciphertext);

            var success = _syncService.ImportSyncEntity(envelope.EntityType, plaintext);
            if (success)
            {
                _log.Information("Imported file event from peer {PeerId}: {EntityType}/{EventId}",
                    peerId, envelope.EntityType, envelope.EventId);
                BroadcastEntitySynced(envelope, plaintext);
            }
            else
            {
                _log.Warning("Failed to import file event from {PeerId}: {EntityType}/{EventId}",
                    peerId, envelope.EntityType, envelope.EventId);
            }

            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _log.Warning(ex, "Decryption failed for event file: {Path} — wrong key or corrupted", filePath);
            return true; // Advance past corrupted files
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "IO error reading event file: {Path} — may be partially written", filePath);
            return false; // Don't advance watermark — retry next cycle
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected error processing event file: {Path}", filePath);
            return true;
        }
    }

    private static void BroadcastEntitySynced(FileEventEnvelope envelope, string jsonData)
    {
        string? entityId = null;
        bool isRemoval = false;

        try
        {
            using var doc = JsonDocument.Parse(jsonData);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
                entityId = idProp.GetString();
            if (doc.RootElement.TryGetProperty("is_trashed", out var trashedProp))
                isRemoval = trashedProp.GetBoolean();
        }
        catch
        {
            // Non-critical
        }

        WeakReferenceMessenger.Default.Send(new EntitySyncedMessage
        {
            EntityId = entityId ?? envelope.EventId,
            EntityType = envelope.EntityType,
            JsonData = jsonData,
            IsRemoval = isRemoval,
        });
    }

    private void StartWatcher()
    {
        try
        {
            if (!Directory.Exists(_eventStoreDir)) return;

            _watcher = new FileSystemWatcher(_eventStoreDir)
            {
                Filter = "*.evt",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.Error += (_, e) =>
                _log.Warning(e.GetException(), "FileSystemWatcher error — falling back to polling");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not start FileSystemWatcher — polling only");
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce: multiple FS events may fire rapidly
        _watcherDebounce?.Dispose();
        _watcherDebounce = new Timer(_ => ScanAllPeers(), null, WatcherDebounceMs, Timeout.Infinite);
    }

    private void StopWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private FileEventSyncState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var json = File.ReadAllText(_statePath);
                return System.Text.Json.JsonSerializer.Deserialize<FileEventSyncState>(json)
                    ?? new FileEventSyncState();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load file sync state — starting fresh");
        }

        return new FileEventSyncState();
    }

    private void SaveState()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_state,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to save file sync state");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
