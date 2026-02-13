using System.Runtime.InteropServices;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Detects whether a filesystem path resides on a network mount.
/// </summary>
internal static class NetworkPathDetector
{
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return IsNetworkPathWindows(path);

        return IsNetworkPathUnix(path);
    }

    private static bool IsNetworkPathWindows(string path)
    {
        // UNC paths (\\server\share)
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // Mapped network drive
        var root = Path.GetPathRoot(path);
        if (root is { Length: >= 2 } && char.IsLetter(root[0]) && root[1] == ':')
        {
            try
            {
                var info = new DriveInfo(root[..2]);
                return info.DriveType == DriveType.Network;
            }
            catch { /* DriveInfo can throw for invalid drives */ }
        }

        return false;
    }

    private static bool IsNetworkPathUnix(string path)
    {
        var normalized = path.TrimEnd('/');

        // Common network mount points on macOS / Linux
        // /Volumes/ on macOS (exclude the boot volume)
        if (normalized.StartsWith("/Volumes/", StringComparison.Ordinal)
            && !normalized.StartsWith("/Volumes/Macintosh HD", StringComparison.Ordinal))
            return true;

        if (normalized.StartsWith("/mnt/", StringComparison.Ordinal))
            return true;

        if (normalized.StartsWith("/net/", StringComparison.Ordinal))
            return true;

        return false;
    }
}
