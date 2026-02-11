using System.Collections.Concurrent;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Polls IReminderProvider capabilities every 30 seconds and fires OS notifications
/// when reminders come due. Skips polling during focus mode.
/// </summary>
public sealed class ReminderSchedulerService : IDisposable
{
    private static readonly ILogger _log = Log.ForContext<ReminderSchedulerService>();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FireThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EvictionAge = TimeSpan.FromHours(24);
    private const int MaxFiredEntries = 5000;

    private readonly IPluginRegistry _pluginRegistry;
    private readonly ISystemNotificationService _notificationService;
    private readonly IAppSettingsService _appSettings;
    private readonly IFocusModeService _focusModeService;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firedKeys = new();

    private System.Timers.Timer? _timer;
    private int _polling; // 0 = idle, 1 = polling (Interlocked guard)
    private bool _disposed;

    public ReminderSchedulerService(
        IPluginRegistry pluginRegistry,
        ISystemNotificationService notificationService,
        IAppSettingsService appSettings,
        IFocusModeService focusModeService)
    {
        _pluginRegistry = pluginRegistry;
        _notificationService = notificationService;
        _appSettings = appSettings;
        _focusModeService = focusModeService;
    }

    /// <summary>
    /// Starts the 30-second poll timer and fires an initial poll to catch recent reminders.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;

        _timer = new System.Timers.Timer(PollInterval.TotalMilliseconds)
        {
            AutoReset = true,
        };
        _timer.Elapsed += async (_, _) => await PollAsync();
        _timer.Start();

        _log.Information("ReminderSchedulerService started (interval={Interval}s)", PollInterval.TotalSeconds);

        // Initial poll on background thread
        _ = Task.Run(PollAsync);
    }

    private async Task PollAsync()
    {
        if (_disposed) return;

        // Re-entrant guard
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;

        try
        {
            // Skip if notifications disabled
            if (!_appSettings.Settings.NotificationsEnabled)
                return;

            // Skip during focus mode
            if (_focusModeService.IsFocusMode)
                return;

            var now = DateTimeOffset.UtcNow;
            var windowStart = now - PollInterval;
            var windowEnd = now + TimeSpan.FromSeconds(90);

            var providers = _pluginRegistry.GetCapabilityProviders<IReminderProvider>();
            if (providers.Count == 0) return;

            foreach (var provider in providers)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var reminders = await provider.GetRemindersInWindowAsync(windowStart, windowEnd, cts.Token);

                    foreach (var reminder in reminders)
                    {
                        // Only fire if the reminder is due (within threshold of now)
                        if (reminder.FireAtUtc > now + FireThreshold)
                            continue;

                        // Dedup: skip if already fired
                        if (_firedKeys.ContainsKey(reminder.Key))
                            continue;

                        // Fire notification
                        _firedKeys[reminder.Key] = now;
                        _log.Debug("Firing reminder: {Key} - {Title}", reminder.Key, reminder.Title);

                        await _notificationService.SendNotificationAsync(
                            reminder.Title,
                            reminder.Body,
                            reminder.SourcePluginId switch
                            {
                                "privstack.tasks" => "Tasks",
                                "privstack.calendar" => "Calendar",
                                _ => null
                            },
                            _appSettings.Settings.NotificationSoundEnabled);
                    }
                }
                catch (OperationCanceledException)
                {
                    _log.Warning("Reminder provider timed out");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Error polling reminder provider");
                }
            }

            // Periodic cleanup of old entries
            EvictOldEntries(now);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected error in reminder poll");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void EvictOldEntries(DateTimeOffset now)
    {
        // Evict entries older than 24 hours
        var keysToRemove = _firedKeys
            .Where(kv => now - kv.Value > EvictionAge)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove)
            _firedKeys.TryRemove(key, out _);

        // Hard cap: if still too many, remove oldest
        if (_firedKeys.Count > MaxFiredEntries)
        {
            var oldest = _firedKeys
                .OrderBy(kv => kv.Value)
                .Take(_firedKeys.Count - MaxFiredEntries)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldest)
                _firedKeys.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Stops the poll timer and clears fired-key cache without disposing.
    /// Safe to call before a workspace switch; call Start() to resume afterward.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;

        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        _firedKeys.Clear();

        _log.Information("ReminderSchedulerService stopped (will restart on next Start)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        _firedKeys.Clear();

        _log.Information("ReminderSchedulerService disposed");
    }
}
