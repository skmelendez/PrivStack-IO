using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PrivStack.Bridge.Protocol;

/// <summary>
/// Connects to the PrivStack desktop app via named pipe and relays
/// length-prefixed JSON messages bidirectionally.
/// </summary>
internal sealed class PipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task<bool> ConnectAsync(string pipeName, int timeoutMs = 5000, CancellationToken ct = default)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await _pipe.ConnectAsync(timeoutMs, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> SendAndReceiveAsync(string json, CancellationToken ct)
    {
        if (_pipe == null || !_pipe.IsConnected) return null;

        // Write length-prefixed frame
        var body = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes((uint)body.Length);
        await _pipe.WriteAsync(length, ct);
        await _pipe.WriteAsync(body, ct);
        await _pipe.FlushAsync(ct);

        // Read response frame
        var respLenBuf = new byte[4];
        var read = await ReadExactAsync(_pipe, respLenBuf, ct);
        if (read < 4) return null;

        var respLen = (int)BitConverter.ToUInt32(respLenBuf, 0);
        if (respLen <= 0 || respLen > 16 * 1024 * 1024) return null;

        var respBuf = new byte[respLen];
        var bodyRead = await ReadExactAsync(_pipe, respBuf, ct);
        if (bodyRead < respLen) return null;

        return Encoding.UTF8.GetString(respBuf);
    }

    /// <summary>
    /// Discovers the pipe name by scanning for active PrivStack pipes.
    /// Falls back to trying common workspace paths.
    /// </summary>
    public static string? DiscoverPipeName()
    {
        // On Unix, named pipes live in /tmp as files
        if (!OperatingSystem.IsWindows())
        {
            var tmpDir = Path.GetTempPath();
            var prefix = "CoreFxPipe_privstack-ipc-";
            try
            {
                foreach (var file in Directory.GetFiles(tmpDir, prefix + "*"))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(prefix))
                        return name[("CoreFxPipe_".Length)..];
                }
            }
            catch { /* scan failed */ }
        }

        // Fallback: try the default data path
        var defaultPath = GetDefaultDataPath();
        if (defaultPath != null)
            return GetPipeName(defaultPath);

        return null;
    }

    private static string GetPipeName(string workspaceDataPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceDataPath));
        return "privstack-ipc-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string? GetDefaultDataPath()
    {
        string baseDir;
        if (OperatingSystem.IsMacOS())
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "PrivStack");
        else if (OperatingSystem.IsWindows())
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrivStack");
        else
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".privstack");

        if (!Directory.Exists(baseDir)) return null;

        // Look for workspace directories containing data files
        try
        {
            foreach (var dir in Directory.GetDirectories(baseDir, "workspace-*"))
            {
                var dbFiles = Directory.GetFiles(dir, "data.*.duckdb");
                if (dbFiles.Length > 0)
                    return dbFiles[0];
            }
        }
        catch { /* scan failed */ }

        return null;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe != null) await _pipe.DisposeAsync();
    }
}
