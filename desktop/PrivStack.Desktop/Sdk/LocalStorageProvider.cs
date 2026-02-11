using System.Security.Cryptography;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Sdk;

/// <summary>
/// Default IStorageProvider that stores files on the local filesystem.
/// Always available, no plugin dependency required.
/// </summary>
internal sealed class LocalStorageProvider : IStorageProvider
{
    private static readonly ILogger _log = Log.ForContext<LocalStorageProvider>();

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif", ".svg"
    };

    private readonly string _storagePath;

    public LocalStorageProvider()
    {
        _storagePath = Path.Combine(PrivStack.Desktop.Services.DataPaths.BaseDir, "quill-images");
        Directory.CreateDirectory(_storagePath);
    }

    public string ProviderId => "default";
    public string DisplayName => "Local Storage";

    public Task<string> StoreFileAsync(string sourcePath, string fileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ext = Path.GetExtension(fileName);

        // Content-addressable: SHA-256 hash the file so duplicates reuse the same ID
        var hash = HashFile(sourcePath);
        var destPath = Path.Combine(_storagePath, hash + ext);

        if (File.Exists(destPath))
        {
            _log.Debug("LocalStorage: dedup hit for {FileName}, reusing {Id}", fileName, hash);
            return Task.FromResult(hash);
        }

        File.Copy(sourcePath, destPath, overwrite: false);
        _log.Debug("LocalStorage: stored {FileName} as {Id}", fileName, hash);

        return Task.FromResult(hash);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public Task<string?> RetrieveFileAsync(string fileId, string fileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Try to find the file by ID prefix (ID stored without extension)
        var ext = Path.GetExtension(fileName);
        var exactPath = Path.Combine(_storagePath, fileId + ext);
        if (File.Exists(exactPath))
            return Task.FromResult<string?>(exactPath);

        // Fallback: scan for any file starting with the ID
        var match = Directory.EnumerateFiles(_storagePath, fileId + ".*").FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<bool> DeleteFileAsync(string fileId, string fileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ext = Path.GetExtension(fileName);
        var exactPath = Path.Combine(_storagePath, fileId + ext);
        if (File.Exists(exactPath))
        {
            File.Delete(exactPath);
            _log.Debug("LocalStorage: deleted {FileId}{Ext}", fileId, ext);
            return Task.FromResult(true);
        }

        // Fallback: scan for any file starting with the ID
        var match = Directory.EnumerateFiles(_storagePath, fileId + ".*").FirstOrDefault();
        if (match != null)
        {
            File.Delete(match);
            _log.Debug("LocalStorage: deleted {Path}", match);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<StorageFileInfo>> SearchImagesAsync(string query, int maxResults = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var files = Directory.EnumerateFiles(_storagePath)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .Where(f => string.IsNullOrEmpty(query) ||
                        Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var info = new FileInfo(f);
                var id = Path.GetFileNameWithoutExtension(f);
                return new StorageFileInfo(id, info.Name, info.Length, info.LastWriteTimeUtc);
            })
            .OrderByDescending(f => f.ModifiedAtUtc)
            .Take(maxResults)
            .ToList();

        return Task.FromResult<IReadOnlyList<StorageFileInfo>>(files);
    }
}
