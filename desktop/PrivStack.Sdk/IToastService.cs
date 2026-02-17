namespace PrivStack.Sdk;

/// <summary>
/// Toast notification types that determine styling and auto-dismiss duration.
/// </summary>
public enum ToastType
{
    Success,
    Info,
    Warning,
    Error
}

/// <summary>
/// Service for displaying transient toast notifications in the shell.
/// Plugins access this via <see cref="IPluginHost.Toast"/>.
/// </summary>
public interface IToastService
{
    /// <summary>
    /// Shows a toast notification with the given message and type.
    /// </summary>
    void Show(string message, ToastType type = ToastType.Info);

    /// <summary>
    /// Shows a toast notification with an action button.
    /// </summary>
    void Show(string message, ToastType type, string actionLabel, Action action);
}
