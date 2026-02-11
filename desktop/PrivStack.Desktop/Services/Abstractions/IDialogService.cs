using Avalonia.Controls;

namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Abstraction over system dialogs (confirmation, file picker).
/// </summary>
public interface IDialogService
{
    Window? Owner { get; }
    void SetOwner(Window owner);
    Task<bool> ShowConfirmationAsync(string title, string message, string confirmButtonText = "Confirm");
    Task<string?> ShowPasswordConfirmationAsync(string title, string message, string confirmButtonText = "Confirm");
    Task<string?> ShowOpenFileDialogAsync(string title, (string Name, string Extension)[] filters);
    Task<string?> ShowSaveFileDialogAsync(string title, string defaultFileName, (string Name, string Extension)[] filters);
    Task<string?> ShowOpenFolderDialogAsync(string title);
}
