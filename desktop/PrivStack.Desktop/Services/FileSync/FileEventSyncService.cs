using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Orchestrates file-based event sync by composing the outbound writer
/// and inbound scanner. Manages lifecycle tied to workspace changes.
/// </summary>
internal sealed class FileEventSyncService : IFileEventSyncService
{
    private static readonly ILogger _log = Log.ForContext<FileEventSyncService>();

    private readonly IWorkspaceService _workspaceService;
    private readonly IMasterPasswordCache _passwordCache;
    private readonly ISyncService _syncService;

    private FileEventOutboundWriter? _writer;
    private FileEventInboundScanner? _scanner;
    private byte[]? _key;
    private bool _disposed;

    public bool IsActive => _writer != null;

    public FileEventSyncService(
        IWorkspaceService workspaceService,
        IMasterPasswordCache passwordCache,
        ISyncService syncService)
    {
        _workspaceService = workspaceService;
        _passwordCache = passwordCache;
        _syncService = syncService;

        _workspaceService.WorkspaceChanged += OnWorkspaceChanged;
    }

    public void Start()
    {
        if (_disposed) return;

        var workspace = _workspaceService.GetActiveWorkspace();
        if (workspace == null)
        {
            _log.Debug("No active workspace — file sync not started");
            return;
        }

        var eventStoreDir = _workspaceService.ResolveEventStoreDir(workspace);
        if (eventStoreDir == null)
        {
            _log.Debug("Workspace {Id} has no event store — file sync not needed", workspace.Id);
            return;
        }

        var password = _passwordCache.Get();
        if (password == null)
        {
            _log.Warning("No cached password — cannot derive file sync key");
            return;
        }

        string localPeerId;
        try
        {
            localPeerId = _syncService.GetLocalPeerId();
            if (string.IsNullOrEmpty(localPeerId))
            {
                _log.Warning("No local peer ID available — file sync cannot start");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to get local peer ID — file sync cannot start");
            return;
        }

        var localWorkspaceDir = _workspaceService.ResolveWorkspaceDir(workspace);

        _log.Information("Starting file event sync for workspace {Id} at {Dir}", workspace.Id, eventStoreDir);

        try
        {
            Directory.CreateDirectory(eventStoreDir);

            _key = FileEventEncryption.DeriveKey(password, workspace.Id);
            _writer = new FileEventOutboundWriter(eventStoreDir, localPeerId, _key);
            _scanner = new FileEventInboundScanner(
                eventStoreDir, localPeerId, localWorkspaceDir, _key, _syncService);
            _scanner.Start();

            _log.Information("File event sync active — peer: {PeerId}, store: {Dir}", localPeerId, eventStoreDir);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to start file event sync");
            Stop();
        }
    }

    public void Stop()
    {
        _scanner?.Dispose();
        _scanner = null;

        _writer = null;

        FileEventEncryption.ClearKey(_key);
        _key = null;

        _log.Debug("File event sync stopped");
    }

    public void WriteEventFile(string entityId, string entityType, string payload)
    {
        _writer?.Write(entityId, entityType, payload);
    }

    private void OnWorkspaceChanged(object? sender, Models.Workspace workspace)
    {
        _log.Information("Workspace changed to {Id} — restarting file sync", workspace.Id);
        Stop();
        Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspaceService.WorkspaceChanged -= OnWorkspaceChanged;
        Stop();
    }
}
