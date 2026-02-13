namespace PrivStack.Desktop.Services;

/// <summary>
/// Shared utility for resolving cloud storage provider paths.
/// </summary>
public static class CloudPathResolver
{
    public static string? GetGoogleDrivePath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var cloudStorage = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "CloudStorage");

            if (Directory.Exists(cloudStorage))
            {
                var googleDirs = Directory.GetDirectories(cloudStorage, "GoogleDrive-*");
                if (googleDirs.Length > 0)
                {
                    var myDrive = Path.Combine(googleDirs[0], "My Drive");
                    if (Directory.Exists(myDrive))
                        return myDrive;
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new[]
            {
                Path.Combine(userProfile, "Google Drive"),
                Path.Combine(userProfile, "My Drive"),
                Path.Combine(userProfile, "GoogleDrive")
            };

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                    return path;
            }
        }

        return null;
    }

    public static string? GetICloudPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var iCloudPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Mobile Documents", "com~apple~CloudDocs");

            if (Directory.Exists(iCloudPath))
                return iCloudPath;
        }
        else if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var iCloudPath = Path.Combine(userProfile, "iCloudDrive");

            if (Directory.Exists(iCloudPath))
                return iCloudPath;
        }

        return null;
    }
}
