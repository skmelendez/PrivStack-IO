namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Implemented by quick action form controls that need to signal the shell to close the overlay.
/// The shell subscribes to <see cref="CloseRequested"/> when the form is shown,
/// eliminating the need for plugins to use reflection to find MainWindowViewModel.
/// </summary>
public interface IQuickActionForm
{
    /// <summary>
    /// Raised when the form wants the overlay closed (after save or cancel).
    /// </summary>
    event Action? CloseRequested;

    /// <summary>
    /// Invokes the <see cref="CloseRequested"/> event. Call this from plugin save handlers
    /// instead of using reflection to close the overlay.
    /// </summary>
    void RequestClose();
}
