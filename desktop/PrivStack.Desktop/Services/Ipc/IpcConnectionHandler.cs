using System.Buffers;
using System.IO.Pipes;
using System.Text;
using Serilog;

namespace PrivStack.Desktop.Services.Ipc;

/// <summary>
/// Handles a single named-pipe connection: reads length-prefixed JSON frames,
/// dispatches to <see cref="IpcMessageRouter"/>, and writes responses back.
/// </summary>
internal sealed class IpcConnectionHandler
{
    private static readonly ILogger _log = Log.ForContext<IpcConnectionHandler>();
    private readonly IpcMessageRouter _router;

    public IpcConnectionHandler(IpcMessageRouter router) => _router = router;

    public async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await ReadFrameAsync(pipe, ct);
                if (request == null) break; // clean disconnect

                var response = await _router.RouteAsync(request, ct);

                await WriteFrameAsync(pipe, response, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* pipe broken */ }
        catch (Exception ex)
        {
            _log.Error(ex, "IPC connection handler error");
        }
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var lengthBuf = new byte[IpcProtocol.LengthPrefixSize];
        var bytesRead = await ReadExactAsync(stream, lengthBuf, ct);
        if (bytesRead == 0) return null; // EOF
        if (bytesRead < IpcProtocol.LengthPrefixSize) return null;

        var length = (int)BitConverter.ToUInt32(lengthBuf, 0);
        if (length <= 0 || length > IpcProtocol.MaxMessageSize)
        {
            _log.Warning("IPC frame length out of range: {Length}", length);
            return null;
        }

        var bodyBuf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var bodyRead = await ReadExactAsync(stream, bodyBuf.AsMemory(0, length), ct);
            if (bodyRead < length) return null;
            return Encoding.UTF8.GetString(bodyBuf, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bodyBuf);
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes((uint)body.Length);

        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0) return totalRead; // EOF
            totalRead += read;
        }
        return totalRead;
    }
}
