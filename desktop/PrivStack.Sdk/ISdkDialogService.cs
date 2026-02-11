namespace PrivStack.Sdk;

/// <summary>
/// Framework-agnostic dialog service for plugins. Provides basic dialog
/// operations without coupling to Avalonia or any specific UI framework.
/// </summary>
public interface ISdkDialogService
{
    /// <summary>
    /// Shows a confirmation dialog and returns the user's choice.
    /// </summary>
    Task<bool> ShowConfirmationAsync(string title, string message, string confirmButtonText = "Confirm");

    /// <summary>
    /// Shows an open file dialog and returns the selected file path.
    /// </summary>
    Task<string?> ShowOpenFileDialogAsync(string title, (string Name, string Extension)[] filters);

    /// <summary>
    /// Shows a save file dialog and returns the selected file path.
    /// </summary>
    Task<string?> ShowSaveFileDialogAsync(string title, string defaultFileName, (string Name, string Extension)[] filters);

    /// <summary>
    /// Shows an open folder dialog and returns the selected folder path.
    /// </summary>
    Task<string?> ShowOpenFolderDialogAsync(string title);

    /// <summary>
    /// Shows a plugin-created dialog as a modal window hosted by the shell.
    /// The factory must return an Avalonia Window; the host provides the owner
    /// window and calls ShowDialog.
    /// </summary>
    Task<TResult?> ShowDialogAsync<TResult>(Func<object> windowFactory) where TResult : class;
}
