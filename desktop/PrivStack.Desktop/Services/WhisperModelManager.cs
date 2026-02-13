using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Manages Whisper model downloads and storage.
/// Models are downloaded from Hugging Face and stored locally.
/// </summary>
public sealed class WhisperModelManager : INotifyPropertyChanged
{
    private static readonly ILogger _log = Log.ForContext<WhisperModelManager>();
    private static readonly Lazy<WhisperModelManager> _instance = new(() => new WhisperModelManager());

    public static WhisperModelManager Instance => _instance.Value;

    // Model URLs from Hugging Face (ggml format for Whisper.NET)
    private static readonly Dictionary<string, (string Url, long ApproxSizeBytes)> ModelInfo = new()
    {
        ["tiny"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", 75_000_000),
        ["tiny.en"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin", 75_000_000),
        ["base"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", 142_000_000),
        ["base.en"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin", 142_000_000),
        ["small"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin", 466_000_000),
        ["small.en"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin", 466_000_000),
        ["medium"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", 1_500_000_000),
        ["medium.en"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin", 1_500_000_000),
    };

    private string? _cachedModelsDirectory;
    private readonly HttpClient _httpClient;
    private bool _isDownloading;
    private double _downloadProgress;
    private string? _downloadingModel;
    private CancellationTokenSource? _downloadCts;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<Exception>? DownloadFailed;

    /// <summary>
    /// Lazy models directory: workspace-scoped if active, root fallback otherwise.
    /// </summary>
    private string ModelsDirectory
    {
        get
        {
            var wsDir = DataPaths.WorkspaceDataDir;
            var target = wsDir != null
                ? Path.Combine(wsDir, "models", "whisper")
                : Path.Combine(DataPaths.BaseDir, "models", "whisper");

            if (_cachedModelsDirectory != target)
            {
                _cachedModelsDirectory = target;
                Directory.CreateDirectory(target);
            }

            return target;
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (_isDownloading != value)
            {
                _isDownloading = value;
                OnPropertyChanged(nameof(IsDownloading));
            }
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (Math.Abs(_downloadProgress - value) > 0.001)
            {
                _downloadProgress = value;
                OnPropertyChanged(nameof(DownloadProgress));
            }
        }
    }

    public string? DownloadingModel
    {
        get => _downloadingModel;
        private set
        {
            if (_downloadingModel != value)
            {
                _downloadingModel = value;
                OnPropertyChanged(nameof(DownloadingModel));
            }
        }
    }

    private WhisperModelManager()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PrivStack/1.0");
    }

    /// <summary>
    /// Gets the list of available model sizes.
    /// </summary>
    public IReadOnlyList<string> AvailableModels => ModelInfo.Keys.ToList();

    /// <summary>
    /// Gets the approximate size of a model in bytes.
    /// </summary>
    public long GetModelSize(string modelName)
    {
        return ModelInfo.TryGetValue(modelName, out var info) ? info.ApproxSizeBytes : 0;
    }

    /// <summary>
    /// Gets a human-readable size string for a model.
    /// </summary>
    public string GetModelSizeDisplay(string modelName)
    {
        var bytes = GetModelSize(modelName);
        return bytes switch
        {
            >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1} GB",
            >= 1_000_000 => $"{bytes / 1_000_000.0:F0} MB",
            _ => $"{bytes / 1_000.0:F0} KB"
        };
    }

    /// <summary>
    /// Gets the local file path for a model.
    /// </summary>
    public string GetModelPath(string modelName)
    {
        return Path.Combine(ModelsDirectory, $"ggml-{modelName}.bin");
    }

    /// <summary>
    /// Checks if a model is already downloaded.
    /// </summary>
    public bool IsModelDownloaded(string modelName)
    {
        var path = GetModelPath(modelName);
        return File.Exists(path);
    }

    /// <summary>
    /// Gets the default model name based on settings.
    /// </summary>
    public string GetDefaultModelName()
    {
        var settings = App.Services.GetRequiredService<Abstractions.IAppSettingsService>().Settings;
        return settings.WhisperModelSize ?? "base.en";
    }

    /// <summary>
    /// Downloads a Whisper model.
    /// </summary>
    public async Task DownloadModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            throw new InvalidOperationException("A download is already in progress");
        }

        if (!ModelInfo.TryGetValue(modelName, out var info))
        {
            throw new ArgumentException($"Unknown model: {modelName}", nameof(modelName));
        }

        var modelPath = GetModelPath(modelName);
        var tempPath = modelPath + ".tmp";

        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            IsDownloading = true;
            DownloadingModel = modelName;
            DownloadProgress = 0;

            _log.Information("Starting download of Whisper model {Model} from {Url}", modelName, info.Url);

            using var response = await _httpClient.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, _downloadCts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? info.ApproxSizeBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(_downloadCts.Token);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, _downloadCts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _downloadCts.Token);
                totalBytesRead += bytesRead;
                DownloadProgress = (double)totalBytesRead / totalBytes * 100;
            }

            // Rename temp file to final path
            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
            }
            File.Move(tempPath, modelPath);

            _log.Information("Successfully downloaded Whisper model {Model} to {Path}", modelName, modelPath);

            DownloadProgress = 100;
            DownloadCompleted?.Invoke(this, modelName);
        }
        catch (OperationCanceledException)
        {
            _log.Information("Download of model {Model} was cancelled", modelName);

            // Clean up temp file
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }

            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to download Whisper model {Model}", modelName);

            // Clean up temp file
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }

            DownloadFailed?.Invoke(this, ex);
            throw;
        }
        finally
        {
            IsDownloading = false;
            DownloadingModel = null;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    /// <summary>
    /// Cancels the current download.
    /// </summary>
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// Deletes a downloaded model.
    /// </summary>
    public void DeleteModel(string modelName)
    {
        var path = GetModelPath(modelName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _log.Information("Deleted Whisper model {Model}", modelName);
        }
    }

    /// <summary>
    /// Gets the total size of all downloaded models.
    /// </summary>
    public long GetTotalDownloadedSize()
    {
        long total = 0;
        foreach (var modelName in ModelInfo.Keys)
        {
            var path = GetModelPath(modelName);
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                total += fileInfo.Length;
            }
        }
        return total;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
