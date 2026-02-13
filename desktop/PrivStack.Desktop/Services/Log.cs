using Serilog;
using Serilog.Events;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Static logging service using Serilog with rolling file support.
/// Logs are stored in the PrivStack application data folder.
/// </summary>
public static class Log
{
    private static ILogger? _logger;
    private static bool _isInitialized;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the configured Serilog logger instance.
    /// </summary>
    public static ILogger Logger
    {
        get
        {
            if (!_isInitialized)
            {
                Initialize();
            }
            return _logger ?? Serilog.Log.Logger;
        }
    }

    /// <summary>
    /// Initializes the logging system with rolling file configuration.
    /// Call this early in application startup.
    /// </summary>
    public static void Initialize()
    {
        lock (_lock)
        {
            if (_isInitialized) return;

            try
            {
                var logFolder = Path.Combine(DataPaths.BaseDir, "logs");
                Directory.CreateDirectory(logFolder);

                var logPath = Path.Combine(logFolder, "privstack-.log");

                var config = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .Enrich.WithThreadId()
                    .Enrich.WithMachineName()
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 10,
                        fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB per file
                        rollOnFileSizeLimit: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                        shared: true
                    );

#if DEBUG
                config = config.WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
                );
#endif

                _logger = config.CreateLogger();
                Serilog.Log.Logger = _logger;
                _isInitialized = true;

                _logger.Information("=== PrivStack Desktop Starting ===");
                _logger.Information("Log folder: {LogFolder}", logFolder);
                _logger.Information("App version: {Version}", GetAppVersion());
                _logger.Information("OS: {OS} {Architecture}", Environment.OSVersion, Environment.Is64BitOperatingSystem ? "x64" : "x86");
                _logger.Information(".NET Runtime: {Runtime}", Environment.Version);
            }
            catch (Exception ex)
            {
                // Fallback to console-only logging if file logging fails
                _logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console()
                    .CreateLogger();
                Serilog.Log.Logger = _logger;
                _isInitialized = true;

                _logger.Error(ex, "Failed to initialize file logging, using console only");
            }
        }
    }

    /// <summary>
    /// Reconfigures the logger to write to a workspace-specific log directory.
    /// Called after workspace selection or switch.
    /// </summary>
    public static void ReconfigureForWorkspace(string workspaceId)
    {
        lock (_lock)
        {
            try
            {
                var logFolder = Path.Combine(DataPaths.BaseDir, "logs", workspaceId);
                Directory.CreateDirectory(logFolder);

                var logPath = Path.Combine(logFolder, "privstack-.log");

                // Flush old logger
                Serilog.Log.CloseAndFlush();
                _isInitialized = false;
                _logger = null;

                var config = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .Enrich.WithThreadId()
                    .Enrich.WithMachineName()
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 10,
                        fileSizeLimitBytes: 10 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                        shared: true
                    );

#if DEBUG
                config = config.WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
                );
#endif

                _logger = config.CreateLogger();
                Serilog.Log.Logger = _logger;
                _isInitialized = true;

                _logger.Information("Logger reconfigured for workspace: {WorkspaceId}, path: {LogPath}", workspaceId, logPath);
            }
            catch (Exception ex)
            {
                // If reconfiguration fails, re-initialize default logger
                _isInitialized = false;
                Initialize();
                Logger.Error(ex, "Failed to reconfigure logger for workspace {WorkspaceId}", workspaceId);
            }
        }
    }

    /// <summary>
    /// Flushes and closes the logging system. Call on application shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_isInitialized) return;

            _logger?.Information("=== PrivStack Desktop Shutting Down ===");
            Serilog.Log.CloseAndFlush();
            _isInitialized = false;
            _logger = null;
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    // Convenience methods that mirror Serilog's API

    public static void Verbose(string messageTemplate) => Logger.Verbose(messageTemplate);
    public static void Verbose<T>(string messageTemplate, T propertyValue) => Logger.Verbose(messageTemplate, propertyValue);
    public static void Verbose<T0, T1>(string messageTemplate, T0 propertyValue0, T1 propertyValue1) => Logger.Verbose(messageTemplate, propertyValue0, propertyValue1);
    public static void Verbose<T0, T1, T2>(string messageTemplate, T0 propertyValue0, T1 propertyValue1, T2 propertyValue2) => Logger.Verbose(messageTemplate, propertyValue0, propertyValue1, propertyValue2);

    public static void Debug(string messageTemplate) => Logger.Debug(messageTemplate);
    public static void Debug<T>(string messageTemplate, T propertyValue) => Logger.Debug(messageTemplate, propertyValue);
    public static void Debug<T0, T1>(string messageTemplate, T0 propertyValue0, T1 propertyValue1) => Logger.Debug(messageTemplate, propertyValue0, propertyValue1);
    public static void Debug<T0, T1, T2>(string messageTemplate, T0 propertyValue0, T1 propertyValue1, T2 propertyValue2) => Logger.Debug(messageTemplate, propertyValue0, propertyValue1, propertyValue2);

    public static void Information(string messageTemplate) => Logger.Information(messageTemplate);
    public static void Information<T>(string messageTemplate, T propertyValue) => Logger.Information(messageTemplate, propertyValue);
    public static void Information<T0, T1>(string messageTemplate, T0 propertyValue0, T1 propertyValue1) => Logger.Information(messageTemplate, propertyValue0, propertyValue1);
    public static void Information<T0, T1, T2>(string messageTemplate, T0 propertyValue0, T1 propertyValue1, T2 propertyValue2) => Logger.Information(messageTemplate, propertyValue0, propertyValue1, propertyValue2);

    public static void Warning(string messageTemplate) => Logger.Warning(messageTemplate);
    public static void Warning<T>(string messageTemplate, T propertyValue) => Logger.Warning(messageTemplate, propertyValue);
    public static void Warning<T0, T1>(string messageTemplate, T0 propertyValue0, T1 propertyValue1) => Logger.Warning(messageTemplate, propertyValue0, propertyValue1);
    public static void Warning(Exception? exception, string messageTemplate) => Logger.Warning(exception, messageTemplate);

    public static void Error(string messageTemplate) => Logger.Error(messageTemplate);
    public static void Error<T>(string messageTemplate, T propertyValue) => Logger.Error(messageTemplate, propertyValue);
    public static void Error<T0, T1>(string messageTemplate, T0 propertyValue0, T1 propertyValue1) => Logger.Error(messageTemplate, propertyValue0, propertyValue1);
    public static void Error(Exception? exception, string messageTemplate) => Logger.Error(exception, messageTemplate);
    public static void Error<T>(Exception? exception, string messageTemplate, T propertyValue) => Logger.Error(exception, messageTemplate, propertyValue);
    public static void Error<T0, T1>(Exception? exception, string messageTemplate, T0 propertyValue0, T1 propertyValue1) => Logger.Error(exception, messageTemplate, propertyValue0, propertyValue1);

    public static void Fatal(string messageTemplate) => Logger.Fatal(messageTemplate);
    public static void Fatal(Exception? exception, string messageTemplate) => Logger.Fatal(exception, messageTemplate);
    public static void Fatal<T>(Exception? exception, string messageTemplate, T propertyValue) => Logger.Fatal(exception, messageTemplate, propertyValue);

    /// <summary>
    /// Creates a logger with context for a specific class.
    /// Usage: private static readonly ILogger _log = Log.ForContext<MyClass>();
    /// </summary>
    public static ILogger ForContext<T>() => Logger.ForContext<T>();

    /// <summary>
    /// Creates a logger with context for a specific source.
    /// </summary>
    public static ILogger ForContext(string sourceContext) => Logger.ForContext("SourceContext", sourceContext);
}
