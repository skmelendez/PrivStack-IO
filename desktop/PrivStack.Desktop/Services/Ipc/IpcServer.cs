using System.IO.Pipes;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services.Ipc;

/// <summary>
/// Named-pipe IPC server for browser extension communication.
/// Listens for incoming connections and spawns a handler per client.
/// </summary>
public sealed class IpcServer : IIpcServer, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<IpcServer>();

    private readonly IpcMessageRouter _router;
    private readonly IWorkspaceService _workspaceService;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private string? _pipeName;

    public IpcServer(IpcMessageRouter router, IWorkspaceService workspaceService)
    {
        _router = router;
        _workspaceService = workspaceService;
    }

    public bool IsRunning => _listenerTask is { IsCompleted: false };
    public string? PipeName => _pipeName;

    public void Start()
    {
        if (IsRunning) return;

        var dataPath = _workspaceService.GetActiveDataPath();
        _pipeName = IpcProtocol.GetPipeName(dataPath);
        _cts = new CancellationTokenSource();

        _log.Information("IPC server starting on pipe: {PipeName}", _pipeName);
        _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _log.Information("IPC server stopped");
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName!,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);
                _log.Debug("IPC client connected");

                // Handle connection on its own task, start listening for next
                var handler = new IpcConnectionHandler(_router);
                var connPipe = pipe;
                pipe = null; // prevent dispose in finally
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler.HandleAsync(connPipe, ct);
                    }
                    finally
                    {
                        await connPipe.DisposeAsync();
                        _log.Debug("IPC client disconnected");
                    }
                }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "IPC listener error, restarting in 1s");
                await Task.Delay(1000, ct);
            }
            finally
            {
                if (pipe != null) await pipe.DisposeAsync();
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _listenerTask = null;
    }
}
