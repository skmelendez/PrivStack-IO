using PrivStack.Bridge.Protocol;

namespace PrivStack.Bridge;

/// <summary>
/// Native messaging bridge between browser extension and PrivStack desktop.
/// Launched by the browser, reads from stdin, relays to named pipe, writes to stdout.
/// Exits when stdin closes (browser disconnects).
/// </summary>
internal static class Program
{
    static async Task<int> Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var pipeName = PipeClient.DiscoverPipeName();
        if (pipeName == null)
        {
            await WriteError("PrivStack desktop app not running");
            return 1;
        }

        await using var pipe = new PipeClient();
        if (!await pipe.ConnectAsync(pipeName, timeoutMs: 3000, cts.Token))
        {
            await WriteError("Could not connect to PrivStack desktop");
            return 1;
        }

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Read native message from browser
                var request = await NativeMessageReader.ReadAsync(stdin, cts.Token);
                if (request == null) break; // stdin closed

                // Relay to desktop via named pipe
                var response = await pipe.SendAndReceiveAsync(request, cts.Token);
                if (response == null)
                {
                    await NativeMessageWriter.WriteAsync(stdout,
                        "{\"success\":false,\"error_code\":\"pipe_error\",\"error_message\":\"Desktop disconnected\"}",
                        cts.Token);
                    break;
                }

                // Write response back to browser
                await NativeMessageWriter.WriteAsync(stdout, response, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
        catch (Exception ex)
        {
            await WriteError($"Bridge error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static async Task WriteError(string message)
    {
        var json = $"{{\"success\":false,\"error_code\":\"bridge_error\",\"error_message\":\"{EscapeJson(message)}\"}}";
        try
        {
            await NativeMessageWriter.WriteAsync(Console.OpenStandardOutput(), json, CancellationToken.None);
        }
        catch { /* can't write, exit silently */ }
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
