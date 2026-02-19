using System.ComponentModel;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Manages local LLM model downloads and storage.
/// Follows the same pattern as WhisperModelManager: workspace-scoped directory,
/// HuggingFace downloads, temp-file-and-rename, INotifyPropertyChanged for UI binding.
/// </summary>
public sealed class AiModelManager : INotifyPropertyChanged
{
    private static readonly ILogger _log = Log.ForContext<AiModelManager>();

    private static readonly Dictionary<string, (string Url, long ApproxSizeBytes)> ModelInfo = new()
    {
        ["phi-3-mini-4k"] = (
            "https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-gguf/resolve/main/Phi-3-mini-4k-instruct-q4.gguf",
            2_300_000_000),
        ["llama-3.2-1b"] = (
            "https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_K_M.gguf",
            800_000_000),
        ["mistral-7b"] = (
            "https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF/resolve/main/mistral-7b-instruct-v0.2.Q4_K_M.gguf",
            4_100_000_000),
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

    private string ModelsDirectory
    {
        get
        {
            var wsDir = DataPaths.WorkspaceDataDir;
            var target = wsDir != null
                ? Path.Combine(wsDir, "models", "llm")
                : Path.Combine(DataPaths.BaseDir, "models", "llm");

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
        private set { if (_isDownloading != value) { _isDownloading = value; OnPropertyChanged(nameof(IsDownloading)); } }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set { if (Math.Abs(_downloadProgress - value) > 0.001) { _downloadProgress = value; OnPropertyChanged(nameof(DownloadProgress)); } }
    }

    public string? DownloadingModel
    {
        get => _downloadingModel;
        private set { if (_downloadingModel != value) { _downloadingModel = value; OnPropertyChanged(nameof(DownloadingModel)); } }
    }

    public AiModelManager()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PrivStack/1.0");
    }

    public IReadOnlyList<string> AvailableModels => ModelInfo.Keys.ToList();

    public long GetModelSize(string modelName) =>
        ModelInfo.TryGetValue(modelName, out var info) ? info.ApproxSizeBytes : 0;

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

    public string GetModelPath(string modelName) =>
        Path.Combine(ModelsDirectory, $"{modelName}.gguf");

    public bool IsModelDownloaded(string modelName) =>
        File.Exists(GetModelPath(modelName));

    public async Task DownloadModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
            throw new InvalidOperationException("A download is already in progress");

        if (!ModelInfo.TryGetValue(modelName, out var info))
            throw new ArgumentException($"Unknown model: {modelName}", nameof(modelName));

        var modelPath = GetModelPath(modelName);
        var tempPath = modelPath + ".tmp";

        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            IsDownloading = true;
            DownloadingModel = modelName;
            DownloadProgress = 0;

            _log.Information("Starting download of LLM model {Model} from {Url}", modelName, info.Url);

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

            if (File.Exists(modelPath)) File.Delete(modelPath);
            File.Move(tempPath, modelPath);

            _log.Information("Successfully downloaded LLM model {Model} to {Path}", modelName, modelPath);
            DownloadProgress = 100;
            DownloadCompleted?.Invoke(this, modelName);
        }
        catch (OperationCanceledException)
        {
            _log.Information("Download of LLM model {Model} was cancelled", modelName);
            if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to download LLM model {Model}", modelName);
            if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { /* ignore */ }
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

    public void CancelDownload() => _downloadCts?.Cancel();

    public void DeleteModel(string modelName)
    {
        var path = GetModelPath(modelName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _log.Information("Deleted LLM model {Model}", modelName);
        }
    }

    public long GetTotalDownloadedSize()
    {
        long total = 0;
        foreach (var modelName in ModelInfo.Keys)
        {
            var path = GetModelPath(modelName);
            if (File.Exists(path))
                total += new FileInfo(path).Length;
        }
        return total;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
