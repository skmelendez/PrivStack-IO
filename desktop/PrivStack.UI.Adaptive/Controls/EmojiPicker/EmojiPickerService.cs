namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Static service for opening the unified emoji picker from anywhere in the app.
/// The host app (PrivStack.Desktop) registers the opener delegate at startup.
/// Consumers call <see cref="Open"/> to show the shared picker with a callback.
/// </summary>
public static class EmojiPickerService
{
    private static Action<Action<string>>? _opener;
    private static Action? _closer;

    /// <summary>
    /// Registers the opener and closer delegates. Called once by the host app at startup.
    /// </summary>
    public static void Register(Action<Action<string>> opener, Action closer)
    {
        _opener = opener;
        _closer = closer;
    }

    /// <summary>
    /// Opens the unified emoji picker. The callback fires when an emoji is selected.
    /// </summary>
    public static void Open(Action<string> onSelected)
    {
        _opener?.Invoke(onSelected);
    }

    /// <summary>
    /// Closes the emoji picker if it is currently open.
    /// </summary>
    public static void Close()
    {
        _closer?.Invoke();
    }

    /// <summary>
    /// Returns true if the service has been registered.
    /// </summary>
    public static bool IsRegistered => _opener is not null;
}
