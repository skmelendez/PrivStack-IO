using System.Text;

namespace PrivStack.Bridge.Protocol;

/// <summary>
/// Reads Chrome/Firefox native messaging format from stdin:
/// 4-byte LE uint32 length prefix followed by UTF-8 JSON body.
/// </summary>
internal static class NativeMessageReader
{
    public static async Task<string?> ReadAsync(Stream input, CancellationToken ct)
    {
        var lengthBuf = new byte[4];
        var bytesRead = await ReadExactAsync(input, lengthBuf, ct);
        if (bytesRead == 0) return null; // stdin closed
        if (bytesRead < 4) return null;

        var length = (int)BitConverter.ToUInt32(lengthBuf, 0);
        if (length <= 0 || length > 16 * 1024 * 1024) return null;

        var bodyBuf = new byte[length];
        var bodyRead = await ReadExactAsync(input, bodyBuf, ct);
        if (bodyRead < length) return null;

        return Encoding.UTF8.GetString(bodyBuf);
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
}
