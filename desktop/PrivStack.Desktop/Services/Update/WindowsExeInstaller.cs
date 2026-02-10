using System.Diagnostics;
using Serilog;

namespace PrivStack.Desktop.Services.Update;

/// <summary>
/// Applies an update for Windows .exe installs by launching the installer.
/// </summary>
public sealed class WindowsExeInstaller : IUpdateInstaller
{
    private static readonly ILogger Logger = Log.ForContext<WindowsExeInstaller>();

    public Task<bool> ApplyAndRestartAsync(string filePath)
    {
        try
        {
            Logger.Information("Launching Windows installer: {Path}", filePath);

            // Pass the current install directory so Inno Setup updates in-place
            var currentDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            // Inno Setup uses /VERYSILENT (not /S which is NSIS convention)
            // /DIR= ensures the update installs to the same location
            // /CLOSEAPPLICATIONS lets Inno Setup close the running app if needed
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                Arguments = $"/VERYSILENT /DIR=\"{currentDir}\" /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });

            // Exit current process — the installer will handle the rest
            Environment.Exit(0);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to launch Windows installer");
            return Task.FromResult(false);
        }
    }

    public Task ApplyOnExitAsync(string filePath)
    {
        var updatesDir = Path.Combine(DataPaths.BaseDir, "updates");
        Directory.CreateDirectory(updatesDir);
        var stagingPath = Path.Combine(updatesDir, "pending-exe");
        File.WriteAllText(stagingPath, filePath);
        Logger.Information("Staged Windows update for next launch: {Path}", filePath);
        return Task.CompletedTask;
    }
}
