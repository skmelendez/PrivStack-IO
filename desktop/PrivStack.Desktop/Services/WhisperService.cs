using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Service for speech-to-text transcription using Whisper.
/// Coordinates audio recording and transcription.
/// </summary>
public sealed class WhisperService : INotifyPropertyChanged, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<WhisperService>();
    private static readonly Lazy<WhisperService> _instance = new(() => new WhisperService());
    private static bool _nativeLibraryResolved;

    public static WhisperService Instance => _instance.Value;

    /// <summary>
    /// Configures runtime library order for Whisper.net.
    /// Must be called before any Whisper operations.
    /// </summary>
    public static void ConfigureNativeLibrary()
    {
        if (_nativeLibraryResolved) return;
        _nativeLibraryResolved = true;

        try
        {
            // Use CPU runtime - most reliable across all platforms
            // CUDA will be tried first on Windows/Linux if available
            RuntimeOptions.RuntimeLibraryOrder =
            [
                RuntimeLibrary.Cuda,
                RuntimeLibrary.Cpu
            ];
            _log.Information("Configured Whisper.net runtime library order (CUDA -> CPU)");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to configure Whisper runtime options, using defaults");
        }
    }

    private WhisperProcessor? _processor;
    private string? _loadedModelPath;
    private bool _isModelLoaded;
    private bool _lastBeamSearch;
    private bool _isRecording;
    private bool _isTranscribing;
    private string? _lastTranscription;
    private string? _errorMessage;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? TranscriptionCompleted;
    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingStopped;
    public event EventHandler<string>? Error;

    /// <summary>
    /// When true, uses beam search sampling (slower but more accurate).
    /// Takes effect on next model load.
    /// </summary>
    public bool BeamSearchEnabled { get; set; }

    public bool IsModelLoaded
    {
        get => _isModelLoaded;
        private set
        {
            if (_isModelLoaded != value)
            {
                _isModelLoaded = value;
                OnPropertyChanged(nameof(IsModelLoaded));
                OnPropertyChanged(nameof(IsReady));
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (_isRecording != value)
            {
                _isRecording = value;
                OnPropertyChanged(nameof(IsRecording));
                OnPropertyChanged(nameof(IsReady));
            }
        }
    }

    public bool IsTranscribing
    {
        get => _isTranscribing;
        private set
        {
            if (_isTranscribing != value)
            {
                _isTranscribing = value;
                OnPropertyChanged(nameof(IsTranscribing));
                OnPropertyChanged(nameof(IsReady));
            }
        }
    }

    public bool IsReady => IsModelLoaded && !IsRecording && !IsTranscribing;

    public string? LastTranscription
    {
        get => _lastTranscription;
        private set
        {
            if (_lastTranscription != value)
            {
                _lastTranscription = value;
                OnPropertyChanged(nameof(LastTranscription));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged(nameof(ErrorMessage));
            }
        }
    }

    public TimeSpan RecordingDuration => AudioRecorderService.Instance.RecordingDuration;

    private WhisperService()
    {
        // Subscribe to audio recorder events
        AudioRecorderService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioRecorderService.RecordingDuration))
            {
                OnPropertyChanged(nameof(RecordingDuration));
            }
        };
    }

    /// <summary>
    /// Initializes the Whisper model.
    /// </summary>
    public async Task InitializeAsync(string? modelPath = null)
    {
        if (IsModelLoaded && _loadedModelPath == modelPath && _lastBeamSearch == BeamSearchEnabled)
        {
            return;
        }

        try
        {
            ErrorMessage = null;

            // Configure native library resolution before loading
            ConfigureNativeLibrary();

            // Use default model if not specified
            modelPath ??= GetDefaultModelPath();

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                var modelName = WhisperModelManager.Instance.GetDefaultModelName();
                _log.Warning("Whisper model not found at {Path}, need to download {Model}", modelPath, modelName);
                ErrorMessage = $"Model not found. Please download the {modelName} model in Settings.";
                return;
            }

            _log.Information("Loading Whisper model from {Path}", modelPath);

            // Dispose existing processor
            _processor?.Dispose();

            // Load the model on a background thread
            var useBeamSearch = BeamSearchEnabled;
            await Task.Run(() =>
            {
                var builder = WhisperFactory.FromPath(modelPath)
                    .CreateBuilder()
                    .WithLanguage("en")
                    .WithThreads(Environment.ProcessorCount > 4 ? 4 : Environment.ProcessorCount);

                if (useBeamSearch)
                    builder.WithBeamSearchSamplingStrategy();

                _processor = builder.Build();
            });

            _loadedModelPath = modelPath;
            _lastBeamSearch = useBeamSearch;
            IsModelLoaded = true;

            _log.Information("Whisper model loaded successfully");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load Whisper model");
            ErrorMessage = $"Failed to load model: {ex.Message}";
            IsModelLoaded = false;
            throw;
        }
    }

    /// <summary>
    /// Starts recording audio for transcription.
    /// </summary>
    public async Task StartRecordingAsync()
    {
        if (!IsModelLoaded)
        {
            throw new InvalidOperationException("Whisper model not loaded. Call InitializeAsync first.");
        }

        if (IsRecording)
        {
            throw new InvalidOperationException("Recording is already in progress");
        }

        try
        {
            ErrorMessage = null;
            await AudioRecorderService.Instance.StartRecordingAsync();
            IsRecording = true;
            RecordingStarted?.Invoke(this, EventArgs.Empty);

            _log.Information("Speech-to-text recording started");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to start recording");
            ErrorMessage = $"Failed to start recording: {ex.Message}";
            IsRecording = false;
            throw;
        }
    }

    /// <summary>
    /// Stops recording and returns the transcribed text.
    /// </summary>
    public async Task<string> StopRecordingAndTranscribeAsync()
    {
        if (!IsRecording)
        {
            throw new InvalidOperationException("No recording in progress");
        }

        try
        {
            IsRecording = false;
            RecordingStopped?.Invoke(this, EventArgs.Empty);

            var audioPath = await AudioRecorderService.Instance.StopRecordingAsync();

            _log.Information("Recording stopped, starting transcription of {Path}", audioPath);

            IsTranscribing = true;

            var transcription = await TranscribeAudioFileAsync(audioPath);

            // Clean up the temp audio file
            try
            {
                if (File.Exists(audioPath))
                {
                    File.Delete(audioPath);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to delete temp audio file: {Path}", audioPath);
            }

            LastTranscription = transcription;
            TranscriptionCompleted?.Invoke(this, transcription);

            return transcription;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to stop recording and transcribe");
            ErrorMessage = $"Transcription failed: {ex.Message}";
            Error?.Invoke(this, ex.Message);
            throw;
        }
        finally
        {
            IsRecording = false;
            IsTranscribing = false;
        }
    }

    /// <summary>
    /// Cancels the current recording without transcribing.
    /// </summary>
    public void CancelRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        AudioRecorderService.Instance.CancelRecording();
        IsRecording = false;
        RecordingStopped?.Invoke(this, EventArgs.Empty);

        _log.Information("Speech-to-text recording cancelled");
    }

    /// <summary>
    /// Transcribes an audio file.
    /// </summary>
    public async Task<string> TranscribeAudioFileAsync(string audioPath)
    {
        if (!IsModelLoaded || _processor == null)
        {
            throw new InvalidOperationException("Whisper model not loaded");
        }

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file not found", audioPath);
        }

        try
        {
            _log.Debug("Transcribing audio file: {Path}", audioPath);

            var sb = new System.Text.StringBuilder();

            await Task.Run(async () =>
            {
                await using var fileStream = File.OpenRead(audioPath);

                TimeSpan? previousEnd = null;

                await foreach (var segment in _processor.ProcessAsync(fileStream))
                {
                    var text = segment.Text;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    // Insert a paragraph break when there's a pause >= 1s between segments
                    if (previousEnd.HasValue &&
                        (segment.Start - previousEnd.Value).TotalSeconds >= 1.0)
                    {
                        sb.Append("\n\n");
                    }
                    else if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(text.Trim());
                    previousEnd = segment.End;
                }
            });

            var transcription = sb.ToString().Trim();

            _log.Information("Transcription completed: {Length} characters", transcription.Length);

            return transcription;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to transcribe audio file: {Path}", audioPath);
            throw;
        }
    }

    /// <summary>
    /// Checks if speech-to-text is available.
    /// </summary>
    public bool IsAvailable()
    {
        var settings = App.Services.GetRequiredService<Abstractions.IAppSettingsService>().Settings;
        if (!settings.SpeechToTextEnabled)
        {
            return false;
        }

        return AudioRecorderService.Instance.IsRecordingAvailable();
    }

    private string? GetDefaultModelPath()
    {
        var modelName = WhisperModelManager.Instance.GetDefaultModelName();
        var modelPath = WhisperModelManager.Instance.GetModelPath(modelName);

        return File.Exists(modelPath) ? modelPath : null;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelRecording();
        _processor?.Dispose();
        _processor = null;
        IsModelLoaded = false;
    }
}
