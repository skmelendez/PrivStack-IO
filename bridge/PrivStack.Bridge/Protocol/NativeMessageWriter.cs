using System.Text;

namespace PrivStack.Bridge.Protocol;

/// <summary>
/// Writes Chrome/Firefox native messaging format to stdout:
/// 4-byte LE uint32 length prefix followed by UTF-8 JSON body.
/// </summary>
internal static class NativeMessageWriter
{
    public static async Task WriteAsync(Stream output, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes((uint)body.Length);

        await output.WriteAsync(length, ct);
        await output.WriteAsync(body, ct);
        await output.FlushAsync(ct);
    }
}
