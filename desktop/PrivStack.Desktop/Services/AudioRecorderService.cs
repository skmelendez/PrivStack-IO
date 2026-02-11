using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Represents an available audio input device.
/// </summary>
public record AudioInputDevice(string Id, string Name);

/// <summary>
/// Cross-platform audio recording service.
/// Uses NAudio on Windows, ffmpeg/sox on macOS/Linux.
/// Records audio from the default microphone and saves to WAV format.
/// </summary>
public sealed class AudioRecorderService : INotifyPropertyChanged, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<AudioRecorderService>();
    private static readonly Lazy<AudioRecorderService> _instance = new(() => new AudioRecorderService());

    public static AudioRecorderService Instance => _instance.Value;

    // Windows (NAudio)
    private IWaveIn? _waveIn;
    private WaveFileWriter? _waveWriter;

    // macOS/Linux (Process-based)
    private Process? _recordingProcess;

    private string? _currentFilePath;
    private DateTime _recordingStartTime;
    private System.Timers.Timer? _durationTimer;
    private bool _isRecording;
    private TimeSpan _recordingDuration;
    private bool _disposed;

    /// <summary>
    /// The selected audio input device ID. Null means use system default.
    /// On macOS this is the avfoundation device index (e.g. "0", "1").
    /// On Windows this is the NAudio device number.
    /// </summary>
    public string? SelectedDeviceId { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingStopped;
    public event EventHandler<Exception>? RecordingError;

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (_isRecording != value)
            {
                _isRecording = value;
                OnPropertyChanged(nameof(IsRecording));
            }
        }
    }

    public TimeSpan RecordingDuration
    {
        get => _recordingDuration;
        private set
        {
            if (_recordingDuration != value)
            {
                _recordingDuration = value;
                OnPropertyChanged(nameof(RecordingDuration));
            }
        }
    }

    private AudioRecorderService()
    {
    }

    /// <summary>
    /// Enumerates available audio input devices on the current platform.
    /// </summary>
    public List<AudioInputDevice> GetAvailableDevices()
    {
        var devices = new List<AudioInputDevice>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                devices = GetWindowsDevices();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                devices = GetMacDevices();
            }
            else
            {
                devices = GetLinuxDevices();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to enumerate audio input devices");
        }

        return devices;
    }

    private static List<AudioInputDevice> GetWindowsDevices()
    {
        var devices = new List<AudioInputDevice>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add(new AudioInputDevice(i.ToString(), caps.ProductName));
        }
        return devices;
    }

    private static List<AudioInputDevice> GetMacDevices()
    {
        var devices = new List<AudioInputDevice>();
        if (!IsCommandAvailable("ffmpeg")) return devices;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-f avfoundation -list_devices true -i \"\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            // ffmpeg writes device list to stderr
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            // Parse lines like: [AVFoundation ...] [0] MacBook Pro Microphone
            bool inAudioSection = false;
            foreach (var line in stderr.Split('\n'))
            {
                if (line.Contains("AVFoundation audio devices:"))
                {
                    inAudioSection = true;
                    continue;
                }
                if (line.Contains("AVFoundation video devices:"))
                {
                    inAudioSection = false;
                    continue;
                }

                if (inAudioSection)
                {
                    // Match pattern: [index] Device Name
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d+)\]\s+(.+)");
                    if (match.Success)
                    {
                        var index = match.Groups[1].Value;
                        var name = match.Groups[2].Value.Trim();
                        devices.Add(new AudioInputDevice(index, name));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to enumerate macOS audio devices via ffmpeg");
        }

        return devices;
    }

    private static List<AudioInputDevice> GetLinuxDevices()
    {
        var devices = new List<AudioInputDevice>();

        // Try PulseAudio sources via ffmpeg
        if (!IsCommandAvailable("ffmpeg")) return devices;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-sources pulse",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // Parse lines like: * alsa_input.pci-0000_00_1f.3.analog-stereo [Built-in Audio Analog Stereo]
            foreach (var line in stdout.Split('\n'))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*\*?\s*(\S+)\s+\[(.+)\]");
                if (match.Success)
                {
                    var id = match.Groups[1].Value;
                    var name = match.Groups[2].Value.Trim();
                    if (id != "default")
                        devices.Add(new AudioInputDevice(id, name));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to enumerate Linux audio devices");
        }

        return devices;
    }

    /// <summary>
    /// Starts recording audio to a temporary WAV file.
    /// </summary>
    /// <returns>The path to the WAV file being recorded.</returns>
    public Task<string> StartRecordingAsync()
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Recording is already in progress");
        }

        // Create temp file for recording
        var tempDir = Path.Combine(Path.GetTempPath(), "PrivStack", "audio");
        Directory.CreateDirectory(tempDir);
        _currentFilePath = Path.Combine(tempDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        _log.Information("Starting audio recording to {FilePath}", _currentFilePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return StartWindowsRecordingAsync();
        }
        else
        {
            return StartUnixRecordingAsync();
        }
    }

    private Task<string> StartWindowsRecordingAsync()
    {
        try
        {
            var deviceNumber = 0;
            if (SelectedDeviceId != null && int.TryParse(SelectedDeviceId, out var parsed))
                deviceNumber = parsed;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                BufferMilliseconds = 50,
                WaveFormat = new WaveFormat(16000, 16, 1) // 16kHz, 16-bit, mono
            };

            _waveWriter = new WaveFileWriter(_currentFilePath!, _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnWindowsRecordingStopped;

            _waveIn.StartRecording();
            StartRecordingState();

            _log.Information("Windows audio recording started");
            return Task.FromResult(_currentFilePath!);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to start Windows audio recording");
            Cleanup();
            throw;
        }
    }

    private Task<string> StartUnixRecordingAsync()
    {
        try
        {
            // Try to find a recording tool
            var (command, args) = GetUnixRecordingCommand(_currentFilePath!);

            if (string.IsNullOrEmpty(command))
            {
                throw new InvalidOperationException(
                    "No audio recording tool found. Please install ffmpeg or sox.\n" +
                    "macOS: brew install ffmpeg\n" +
                    "Linux: sudo apt install ffmpeg");
            }

            _log.Information("Starting Unix recording with {Command}", command);

            _recordingProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _recordingProcess.Exited += OnUnixRecordingStopped;
            _recordingProcess.Start();

            StartRecordingState();

            _log.Information("Unix audio recording started with PID {Pid}", _recordingProcess.Id);
            return Task.FromResult(_currentFilePath!);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to start Unix audio recording");
            Cleanup();
            throw;
        }
    }

    private (string command, string args) GetUnixRecordingCommand(string outputPath)
    {
        // Try ffmpeg first (most common)
        if (IsCommandAvailable("ffmpeg"))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS uses avfoundation
                // SelectedDeviceId is the avfoundation audio device index, default "0"
                var deviceIndex = SelectedDeviceId ?? "0";
                return ("ffmpeg", $"-f avfoundation -i \":{deviceIndex}\" -ar 16000 -ac 1 -acodec pcm_s16le -y \"{outputPath}\"");
            }
            else
            {
                // Linux uses PulseAudio
                var deviceId = SelectedDeviceId ?? "default";
                return ("ffmpeg", $"-f pulse -i \"{deviceId}\" -ar 16000 -ac 1 -acodec pcm_s16le -y \"{outputPath}\"");
            }
        }

        // Try sox's rec command
        if (IsCommandAvailable("rec"))
        {
            return ("rec", $"-r 16000 -c 1 \"{outputPath}\"");
        }

        // Try sox directly
        if (IsCommandAvailable("sox"))
        {
            return ("sox", $"-d -r 16000 -c 1 \"{outputPath}\"");
        }

        return (string.Empty, string.Empty);
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var whichCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = whichCommand,
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            process?.WaitForExit(1000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void StartRecordingState()
    {
        _recordingStartTime = DateTime.Now;
        RecordingDuration = TimeSpan.Zero;
        IsRecording = true;

        // Start duration timer
        _durationTimer = new System.Timers.Timer(100);
        _durationTimer.Elapsed += (_, _) =>
        {
            RecordingDuration = DateTime.Now - _recordingStartTime;
        };
        _durationTimer.Start();

        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops the current recording and returns the path to the recorded WAV file.
    /// </summary>
    public async Task<string> StopRecordingAsync()
    {
        if (!IsRecording || _currentFilePath == null)
        {
            throw new InvalidOperationException("No recording in progress");
        }

        try
        {
            _log.Information("Stopping audio recording");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _waveIn?.StopRecording();
            }
            else
            {
                // Send SIGINT to ffmpeg/sox to stop gracefully
                if (_recordingProcess != null && !_recordingProcess.HasExited)
                {
                    // On Unix, send interrupt signal for graceful stop
                    try
                    {
                        Process.Start("kill", $"-INT {_recordingProcess.Id}")?.WaitForExit(1000);
                    }
                    catch
                    {
                        _recordingProcess.Kill();
                    }

                    // Wait for process to exit
                    await Task.Run(() => _recordingProcess.WaitForExit(3000));
                }
            }

            // Wait a bit for file to be finalized
            await Task.Delay(200);

            var filePath = _currentFilePath;
            Cleanup();

            // Verify file exists and has content
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException("Recording file was not created");
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 100)
            {
                throw new InvalidOperationException("Recording file is too small - audio may not have been captured");
            }

            _log.Information("Audio recording stopped, saved to {FilePath} ({Size} bytes)", filePath, fileInfo.Length);
            return filePath;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to stop audio recording");
            Cleanup();
            throw;
        }
    }

    /// <summary>
    /// Cancels the current recording and deletes the file.
    /// </summary>
    public void CancelRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        try
        {
            _log.Information("Cancelling audio recording");

            var filePath = _currentFilePath;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _waveIn?.StopRecording();
            }
            else
            {
                if (_recordingProcess != null && !_recordingProcess.HasExited)
                {
                    try
                    {
                        _recordingProcess.Kill();
                        _recordingProcess.WaitForExit(1000);
                    }
                    catch { }
                }
            }

            Cleanup();

            // Delete the incomplete file
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    _log.Debug("Deleted cancelled recording file: {FilePath}", filePath);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to delete cancelled recording file: {FilePath}", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cancelling recording");
            Cleanup();
        }
    }

    /// <summary>
    /// Checks if audio recording is available on this system.
    /// </summary>
    public bool IsRecordingAvailable()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return WaveInEvent.DeviceCount > 0;
            }

            // For macOS/Linux, check if ffmpeg or sox is available
            return IsCommandAvailable("ffmpeg") || IsCommandAvailable("rec") || IsCommandAvailable("sox");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a message describing what's needed for recording on this platform.
    /// </summary>
    public static string GetRecordingRequirements()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "A microphone must be connected.";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "ffmpeg is required. Install with: brew install ffmpeg";
        }
        else
        {
            return "ffmpeg is required. Install with: sudo apt install ffmpeg";
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveWriter != null && e.BytesRecorded > 0)
        {
            _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnWindowsRecordingStopped(object? sender, StoppedEventArgs e)
    {
        RecordingStopped?.Invoke(this, EventArgs.Empty);

        if (e.Exception != null)
        {
            _log.Error(e.Exception, "Recording stopped due to error");
            RecordingError?.Invoke(this, e.Exception);
        }
    }

    private void OnUnixRecordingStopped(object? sender, EventArgs e)
    {
        RecordingStopped?.Invoke(this, EventArgs.Empty);

        if (_recordingProcess?.ExitCode != 0 && _recordingProcess?.ExitCode != 255)
        {
            // 255 is often returned when killed with signal
            var error = new Exception($"Recording process exited with code {_recordingProcess?.ExitCode}");
            _log.Error(error, "Unix recording process exited with error");
            RecordingError?.Invoke(this, error);
        }
    }

    private void Cleanup()
    {
        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;

        // Windows cleanup
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnWindowsRecordingStopped;
            try { _waveIn.Dispose(); } catch { }
            _waveIn = null;
        }

        if (_waveWriter != null)
        {
            try { _waveWriter.Dispose(); } catch { }
            _waveWriter = null;
        }

        // Unix cleanup
        if (_recordingProcess != null)
        {
            _recordingProcess.Exited -= OnUnixRecordingStopped;
            try { _recordingProcess.Dispose(); } catch { }
            _recordingProcess = null;
        }

        _currentFilePath = null;
        IsRecording = false;
        RecordingDuration = TimeSpan.Zero;
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
        Cleanup();
    }
}
