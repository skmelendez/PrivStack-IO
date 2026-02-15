using System.Security.Cryptography;
using System.Text.Json;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Registers the native messaging host manifest for browser extension communication.
/// Called on startup to ensure the bridge binary is discoverable by Chrome/Firefox/Edge/Brave.
/// Also handles auth token generation for first-time pairing.
/// </summary>
public static class NativeMessagingRegistrar
{
    private static readonly ILogger _log = Log.ForContext(typeof(NativeMessagingRegistrar));
    private const string HostName = "io.privstack.bridge";

    /// <summary>
    /// Writes native messaging host manifests to browser-specific directories.
    /// Also generates a bridge auth token if one doesn't exist.
    /// </summary>
    public static void Register(string bridgePath, IAppSettingsService settings)
    {
        EnsureAuthToken(settings);

        var manifest = new
        {
            name = HostName,
            description = "PrivStack Desktop Bridge",
            path = bridgePath,
            type = "stdio",
            // Chrome uses allowed_origins, Firefox uses allowed_extensions
            allowed_origins = new[] { "chrome-extension://*/" },
        };

        var firefoxManifest = new
        {
            name = HostName,
            description = "PrivStack Desktop Bridge",
            path = bridgePath,
            type = "stdio",
            allowed_extensions = new[] { "companion@privstack.io" },
        };

        var chromeJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var firefoxJson = JsonSerializer.Serialize(firefoxManifest, new JsonSerializerOptions { WriteIndented = true });
        var fileName = $"{HostName}.json";

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            WriteManifest(Path.Combine(home, "Library/Application Support/Google/Chrome/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, "Library/Application Support/Chromium/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, "Library/Application Support/BraveSoftware/Brave-Browser/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, "Library/Application Support/Microsoft Edge/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, "Library/Application Support/Mozilla/NativeMessagingHosts", fileName), firefoxJson);
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            WriteManifest(Path.Combine(home, ".config/google-chrome/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, ".config/chromium/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, ".config/BraveSoftware/Brave-Browser/NativeMessagingHosts", fileName), chromeJson);
            WriteManifest(Path.Combine(home, ".mozilla/native-messaging-hosts", fileName), firefoxJson);
        }
        else if (OperatingSystem.IsWindows())
        {
            // On Windows, we write the manifest to a known location and register via registry
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var manifestDir = Path.Combine(appData, "PrivStack", "NativeMessagingHosts");
            var manifestPath = Path.Combine(manifestDir, fileName);

            WriteManifest(manifestPath, chromeJson);

            // Registry entries for Chrome, Edge, Brave (they all use HKCU\Software\Google\Chrome\...)
            RegisterWindowsHost("Google\\Chrome", manifestPath);
            RegisterWindowsHost("Microsoft\\Edge", manifestPath);
            RegisterWindowsHost("Chromium", manifestPath);

            // Firefox uses its own registry path
            var firefoxManifestPath = Path.Combine(manifestDir, $"{HostName}.firefox.json");
            WriteManifest(firefoxManifestPath, firefoxJson);
            RegisterWindowsHost("Mozilla", firefoxManifestPath, isFirefox: true);
        }

        _log.Information("Native messaging host registered for bridge at {Path}", bridgePath);
    }

    private static void WriteManifest(string path, string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not write native messaging manifest to {Path}", path);
        }
    }

    private static void RegisterWindowsHost(string browserKey, string manifestPath, bool isFirefox = false)
    {
        try
        {
            var keyPath = isFirefox
                ? $"Software\\Mozilla\\NativeMessagingHosts\\{HostName}"
                : $"Software\\{browserKey}\\NativeMessagingHosts\\{HostName}";

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
            key?.SetValue("", manifestPath);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not register native messaging host in registry for {Browser}", browserKey);
        }
    }

    /// <summary>
    /// Generates a random auth token for bridge communication if one doesn't exist.
    /// </summary>
    private static void EnsureAuthToken(IAppSettingsService settings)
    {
        if (!string.IsNullOrEmpty(settings.Settings.BridgeAuthToken)) return;

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        settings.Settings.BridgeAuthToken = Convert.ToBase64String(tokenBytes);
        settings.Save();

        _log.Information("Generated new bridge auth token");
    }
}
