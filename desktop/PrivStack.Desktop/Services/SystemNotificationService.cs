using System.Diagnostics;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Sends native OS notifications via platform-specific shell commands.
/// macOS: osascript, Windows: PowerShell ToastNotification, Linux: notify-send.
/// Thread-safe — all calls use Process.Start (no UI thread required).
/// </summary>
public class SystemNotificationService : ISystemNotificationService
{
    private static readonly ILogger _log = Log.ForContext<SystemNotificationService>();

    public async Task<bool> SendNotificationAsync(string title, string body, string? subtitle = null, bool playSound = true)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return await SendMacNotificationAsync(title, body, subtitle, playSound);

            if (OperatingSystem.IsWindows())
                return await SendWindowsNotificationAsync(title, body, subtitle, playSound);

            if (OperatingSystem.IsLinux())
                return await SendLinuxNotificationAsync(title, body);

            _log.Warning("Unsupported OS for system notifications");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send system notification");
            return false;
        }
    }

    private static async Task<bool> SendMacNotificationAsync(string title, string body, string? subtitle, bool playSound)
    {
        var escapedTitle = EscapeAppleScript(title);
        var escapedBody = EscapeAppleScript(body);

        var script = subtitle != null
            ? $"display notification \"{escapedBody}\" with title \"{escapedTitle}\" subtitle \"{EscapeAppleScript(subtitle)}\""
            : $"display notification \"{escapedBody}\" with title \"{escapedTitle}\"";

        if (playSound)
            script += " sound name \"default\"";

        return await RunProcessWithArgsAsync("osascript", ["-e", script]);
    }

    private static async Task<bool> SendWindowsNotificationAsync(string title, string body, string? subtitle, bool playSound)
    {
        var displayBody = subtitle != null ? $"{subtitle}\n{body}" : body;
        var escapedTitle = EscapePowerShell(title);
        var escapedBody = EscapePowerShell(displayBody);

        var audioElement = playSound ? "" : "<audio silent='true'/>";

        var ps = $"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
                 $"[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null; " +
                 $"$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; " +
                 $"$template = '<toast><visual><binding template=\"ToastGeneric\"><text>{escapedTitle}</text><text>{escapedBody}</text></binding></visual>{audioElement}</toast>'; " +
                 $"$xml.LoadXml($template); " +
                 $"$toast = New-Object Windows.UI.Notifications.ToastNotification $xml; " +
                 $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('PrivStack').Show($toast)";

        return await RunProcessWithArgsAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command", ps]);
    }

    private static async Task<bool> SendLinuxNotificationAsync(string title, string body)
    {
        return await RunProcessWithArgsAsync("notify-send", [title, body, "--app-name=PrivStack"]);
    }

    private static async Task<bool> RunProcessWithArgsAsync(string fileName, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrEmpty(stderr))
                    _log.Debug("Process {FileName} stderr: {Error}", fileName, stderr);
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Process {FileName} failed", fileName);
            return false;
        }
    }

    private static string EscapeAppleScript(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapePowerShell(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
