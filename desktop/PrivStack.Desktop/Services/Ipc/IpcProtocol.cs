using System.Security.Cryptography;
using System.Text;

namespace PrivStack.Desktop.Services.Ipc;

/// <summary>
/// Shared protocol constants for PrivStack IPC (named pipes).
/// Length-prefixed JSON: 4-byte LE uint32 + UTF-8 JSON body. Max 16 MB per message.
/// </summary>
public static class IpcProtocol
{
    public const int MaxMessageSize = 16 * 1024 * 1024; // 16 MB
    public const int LengthPrefixSize = 4;
    public const string PipePrefix = "privstack-ipc-";

    /// <summary>
    /// Derives a deterministic pipe name from the workspace data path.
    /// Uses first 12 hex chars of SHA-256 to avoid collisions while keeping names short.
    /// </summary>
    public static string GetPipeName(string workspaceDataPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceDataPath));
        return PipePrefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
