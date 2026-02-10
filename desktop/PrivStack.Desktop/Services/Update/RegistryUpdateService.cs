using System.Net.Http;
using System.Security.Cryptography;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.Services.Update;

/// <summary>
/// Checks for, downloads, and applies application updates via the PrivStack registry API.
/// </summary>
public sealed class RegistryUpdateService : IUpdateService
{
    private static readonly ILogger Logger = Log.ForContext<RegistryUpdateService>();

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly PrivStackApiClient _apiClient;
    private readonly IAppSettingsService _appSettings;

    private string? _downloadedFilePath;
    private LatestReleaseInfo? _latestRelease;

    public string CurrentVersion
    {
        get
        {
            var ver = typeof(RegistryUpdateService).Assembly.GetName().Version;
            return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }
    }

    public event EventHandler<LatestReleaseInfo>? UpdateFound;
    public event EventHandler<Exception>? UpdateError;

    public RegistryUpdateService(PrivStackApiClient apiClient, IAppSettingsService appSettings)
    {
        _apiClient = apiClient;
        _appSettings = appSettings;
    }

    public async Task<LatestReleaseInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            Logger.Information("Checking for updates via registry API...");
            var release = await _apiClient.GetLatestReleaseAsync(ct);

            if (release == null || string.IsNullOrEmpty(release.Version))
            {
                Logger.Information("No release info returned from API");
                return null;
            }

            if (!Version.TryParse(release.Version, out var remoteVersion))
            {
                Logger.Warning("Could not parse remote version: {Version}", release.Version);
                return null;
            }

            var currentVersionStr = CurrentVersion;
            if (!Version.TryParse(currentVersionStr, out var currentVersion))
            {
                Logger.Warning("Could not parse current version: {Version}", currentVersionStr);
                return null;
            }

            if (remoteVersion <= currentVersion)
            {
                Logger.Information("Already up to date (current: {Current}, remote: {Remote})",
                    currentVersionStr, release.Version);
                return null;
            }

            // Check that a matching platform artifact exists
            var platform = PlatformDetector.GetPlatform();
            var arch = PlatformDetector.GetArch();
            var format = PlatformDetector.DetectCurrentInstallFormat();

            var hasMatch = release.AllReleases.Any(p =>
                p.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) &&
                p.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase) &&
                p.Format.Equals(format, StringComparison.OrdinalIgnoreCase));

            if (!hasMatch)
            {
                Logger.Warning("Update {Version} found but no matching artifact for {Platform}/{Arch}/{Format}",
                    release.Version, platform, arch, format);
                return null;
            }

            _latestRelease = release;
            Logger.Information("Update available: {Version} (current: {Current})",
                release.Version, currentVersionStr);
            UpdateFound?.Invoke(this, release);
            return release;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates");
            UpdateError?.Invoke(this, ex);
            return null;
        }
    }

    public async Task<string?> DownloadUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var token = _appSettings.Settings.AccessToken;
            if (string.IsNullOrEmpty(token))
            {
                Logger.Warning("No access token stored — cannot download update");
                return null;
            }

            Logger.Information("Fetching account release artifacts...");
            var accountReleases = await _apiClient.GetAccountReleasesAsync(token, ct);
            if (accountReleases == null || accountReleases.Releases.Count == 0)
            {
                Logger.Warning("No downloadable releases returned from account API");
                return null;
            }

            var platform = PlatformDetector.GetPlatform();
            var arch = PlatformDetector.GetArch();
            var format = PlatformDetector.DetectCurrentInstallFormat();

            var artifact = accountReleases.Releases.FirstOrDefault(r =>
                r.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) &&
                r.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase) &&
                r.Format.Equals(format, StringComparison.OrdinalIgnoreCase));

            if (artifact == null)
            {
                Logger.Warning("No matching artifact for {Platform}/{Arch}/{Format}",
                    platform, arch, format);
                return null;
            }

            // Download to updates directory
            var updatesDir = Path.Combine(DataPaths.BaseDir, "updates");
            Directory.CreateDirectory(updatesDir);
            var destPath = Path.Combine(updatesDir, artifact.Filename);

            Logger.Information("Downloading update artifact: {Filename} ({Size} bytes)",
                artifact.Filename, artifact.SizeBytes);

            await DownloadFileAsync(artifact.DownloadUrl, destPath, artifact.SizeBytes, progress, ct);

            // SHA-256 verify if checksum is provided
            if (!string.IsNullOrEmpty(artifact.ChecksumSha256))
            {
                Logger.Information("Verifying SHA-256 checksum...");
                var actualHash = await ComputeSha256Async(destPath, ct);

                if (!actualHash.Equals(artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error("Checksum mismatch! Expected: {Expected}, Actual: {Actual}",
                        artifact.ChecksumSha256, actualHash);
                    TryDeleteFile(destPath);
                    return null;
                }

                Logger.Information("Checksum verified OK");
            }

            _downloadedFilePath = destPath;
            progress?.Report(100);
            return destPath;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download update");
            UpdateError?.Invoke(this, ex);
            return null;
        }
    }

    public async Task<bool> ApplyUpdateAndRestartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_downloadedFilePath) || !File.Exists(_downloadedFilePath))
        {
            Logger.Warning("No downloaded update file to apply");
            return false;
        }

        try
        {
            var installer = UpdateInstallerFactory.Create();
            return await installer.ApplyAndRestartAsync(_downloadedFilePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply update");
            UpdateError?.Invoke(this, ex);
            return false;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        long expectedSize,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var fullUrl = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{PrivStackApiClient.ApiBaseUrl}{url}";

        using var response = await Http.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long bytesRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesRead += read;

            if (totalBytes > 0)
            {
                progress?.Report((int)(99.0 * bytesRead / totalBytes));
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
