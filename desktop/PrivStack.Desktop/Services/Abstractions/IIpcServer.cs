namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Named-pipe IPC server for browser extension bridge communication.
/// </summary>
public interface IIpcServer
{
    bool IsRunning { get; }
    string? PipeName { get; }
    void Start();
    void Stop();
}
