using System.Collections.ObjectModel;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Represents a single active toast notification.
/// </summary>
public sealed class ActiveToast
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Message { get; init; } = string.Empty;
    public ToastType Type { get; init; }
    public string FriendlyLabel { get; init; } = string.Empty;
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>CSS-style class name for XAML style selectors (e.g., "toast-success").</summary>
    public string TypeClass => $"toast-{Type.ToString().ToLowerInvariant()}";

    public bool IsSuccess => Type == ToastType.Success;
    public bool IsInfo => Type == ToastType.Info;
    public bool IsWarning => Type == ToastType.Warning;
    public bool IsError => Type == ToastType.Error;
}

/// <summary>
/// Manages a queue of transient toast notifications displayed in the shell.
/// Thread-safe: all collection mutations are dispatched to the UI thread.
/// </summary>
public sealed class ToastService : IToastService
{
    private const int MaxVisible = 4;
    private readonly ObservableCollection<ActiveToast> _toasts = [];
    private readonly IUiDispatcher _dispatcher;

    public ReadOnlyObservableCollection<ActiveToast> Toasts { get; }

    public ToastService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Toasts = new ReadOnlyObservableCollection<ActiveToast>(_toasts);
    }

    public void Show(string message, ToastType type = ToastType.Info)
    {
        ShowInternal(message, type, null, null);
    }

    public void Show(string message, ToastType type, string actionLabel, Action action)
    {
        ShowInternal(message, type, actionLabel, action);
    }

    public void Dismiss(ActiveToast toast)
    {
        _dispatcher.Post(() => _toasts.Remove(toast));
    }

    private void ShowInternal(string message, ToastType type, string? actionLabel, Action? action)
    {
        var toast = new ActiveToast
        {
            Message = message,
            Type = type,
            FriendlyLabel = GetDisplayLabel(type),
            ActionLabel = actionLabel,
            Action = action
        };

        _dispatcher.Post(() =>
        {
            _toasts.Insert(0, toast);

            // Trim oldest if over cap
            while (_toasts.Count > MaxVisible)
                _toasts.RemoveAt(_toasts.Count - 1);
        });

        // Schedule auto-dismiss
        var delay = GetDismissDelay(type);
        _ = Task.Delay(delay).ContinueWith(_ => Dismiss(toast), TaskScheduler.Default);
    }

    public static string GetDisplayLabel(ToastType type) => type switch
    {
        ToastType.Success => "All Set",
        ToastType.Info => "FYI",
        ToastType.Warning => "Heads Up",
        ToastType.Error => "Action Needed",
        _ => "Notice"
    };

    private static TimeSpan GetDismissDelay(ToastType type) => type switch
    {
        ToastType.Success => TimeSpan.FromSeconds(5),
        ToastType.Info => TimeSpan.FromSeconds(5),
        ToastType.Warning => TimeSpan.FromSeconds(7),
        ToastType.Error => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(5)
    };
}
