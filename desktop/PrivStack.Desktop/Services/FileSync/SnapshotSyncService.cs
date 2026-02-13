using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;
using PrivStack.Sdk.Messaging;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Orchestrates full-state snapshot sync. On start, imports the latest peer snapshot
/// to bootstrap data. On close, exports a full snapshot for other peers.
/// </summary>
internal sealed class SnapshotSyncService : ISnapshotSyncService
{
    private static readonly ILogger _log = Log.ForContext<SnapshotSyncService>();

    private readonly IWorkspaceService _workspaceService;
    private readonly IMasterPasswordCache _passwordCache;
    private readonly ISyncService _syncService;
    private readonly IPrivStackSdk _sdk;

    private string? _snapshotDir;
    private string? _peerId;
    private string? _workspaceId;
    private byte[]? _key;
    private bool _disposed;

    public bool IsActive => _snapshotDir != null;

    public SnapshotSyncService(
        IWorkspaceService workspaceService,
        IMasterPasswordCache passwordCache,
        ISyncService syncService,
        IPrivStackSdk sdk)
    {
        _workspaceService = workspaceService;
        _passwordCache = passwordCache;
        _syncService = syncService;
        _sdk = sdk;

        _workspaceService.WorkspaceChanged += OnWorkspaceChanged;
    }

    public async Task StartAsync()
    {
        if (_disposed) return;

        var workspace = _workspaceService.GetActiveWorkspace();
        if (workspace == null) return;

        var snapshotDir = _workspaceService.ResolveSnapshotDir(workspace);
        if (snapshotDir == null)
        {
            _log.Debug("Workspace {Id} has no snapshot dir — snapshot sync not needed", workspace.Id);
            return;
        }

        var password = _passwordCache.Get();
        if (password == null)
        {
            _log.Warning("No cached password — cannot derive snapshot key");
            return;
        }

        string peerId;
        try
        {
            peerId = _syncService.GetLocalPeerId();
            if (string.IsNullOrEmpty(peerId))
            {
                _log.Warning("No local peer ID — snapshot sync cannot start");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to get local peer ID for snapshot sync");
            return;
        }

        _snapshotDir = snapshotDir;
        _peerId = peerId;
        _workspaceId = workspace.Id;
        _key = FileEventEncryption.DeriveKey(password, workspace.Id);

        _log.Information("Snapshot sync active for workspace {Id} at {Dir}", workspace.Id, snapshotDir);

        // Import the latest peer snapshot on startup
        await ImportLatestSnapshotAsync();
    }

    public async Task ExportSnapshotAsync()
    {
        if (_snapshotDir == null || _key == null || _peerId == null || _workspaceId == null)
            return;

        try
        {
            var exporter = new SnapshotExporter(_sdk, _workspaceId);
            var data = await exporter.ExportAsync();

            SnapshotWriter.Write(data, _snapshotDir, _peerId, _key);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to export snapshot");
        }
    }

    public void Stop()
    {
        _snapshotDir = null;
        _peerId = null;
        _workspaceId = null;

        FileEventEncryption.ClearKey(_key);
        _key = null;

        _log.Debug("Snapshot sync stopped");
    }

    private Task ImportLatestSnapshotAsync()
    {
        if (_snapshotDir == null || _key == null || _peerId == null)
            return Task.CompletedTask;

        try
        {
            var snapshot = SnapshotReader.ReadLatestPeerSnapshot(_snapshotDir, _peerId, _key);
            if (snapshot == null)
            {
                _log.Debug("No peer snapshots found to import");
                return Task.CompletedTask;
            }

            var imported = 0;
            var skipped = 0;

            foreach (var typeSnapshot in snapshot.EntityTypes)
            {
                foreach (var jsonData in typeSnapshot.Items)
                {
                    try
                    {
                        var success = _syncService.ImportSyncEntity(typeSnapshot.EntityType, jsonData);
                        if (success)
                        {
                            imported++;
                            BroadcastEntitySynced(typeSnapshot.EntityType, jsonData);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Failed to import {Type} entity from snapshot", typeSnapshot.EntityType);
                        skipped++;
                    }
                }
            }

            _log.Information("Snapshot import complete: {Imported} imported, {Skipped} skipped",
                imported, skipped);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _log.Warning(ex, "Snapshot decryption failed — wrong key or corrupted");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to import peer snapshot");
        }

        return Task.CompletedTask;
    }

    private static void BroadcastEntitySynced(string entityType, string jsonData)
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

        if (entityId == null) return;

        WeakReferenceMessenger.Default.Send(new EntitySyncedMessage
        {
            EntityId = entityId,
            EntityType = entityType,
            JsonData = jsonData,
            IsRemoval = isRemoval,
        });
    }

    private async void OnWorkspaceChanged(object? sender, Models.Workspace workspace)
    {
        _log.Information("Workspace changed — exporting snapshot then restarting");

        // Export snapshot for the old workspace before switching
        await ExportSnapshotAsync();
        Stop();
        await StartAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspaceService.WorkspaceChanged -= OnWorkspaceChanged;
        Stop();
    }
}
