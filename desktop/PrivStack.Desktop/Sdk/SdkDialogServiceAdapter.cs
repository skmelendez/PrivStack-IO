using Avalonia.Controls;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Sdk;

/// <summary>
/// Bridges the SDK's ISdkDialogService to the Desktop DialogService.
/// </summary>
internal sealed class SdkDialogServiceAdapter : ISdkDialogService
{
    private readonly IDialogService _dialogService;

    public SdkDialogServiceAdapter(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public Task<bool> ShowConfirmationAsync(string title, string message, string confirmButtonText = "Confirm")
        => _dialogService.ShowConfirmationAsync(title, message, confirmButtonText);

    public Task<string?> ShowOpenFileDialogAsync(string title, (string Name, string Extension)[] filters)
        => _dialogService.ShowOpenFileDialogAsync(title, filters);

    public Task<string?> ShowSaveFileDialogAsync(string title, string defaultFileName, (string Name, string Extension)[] filters)
        => _dialogService.ShowSaveFileDialogAsync(title, defaultFileName, filters);

    public Task<string?> ShowOpenFolderDialogAsync(string title)
        => _dialogService.ShowOpenFolderDialogAsync(title);

    public async Task<TResult?> ShowDialogAsync<TResult>(Func<object> windowFactory) where TResult : class
    {
        var owner = _dialogService.Owner;
        if (owner == null) return default;

        var windowObj = windowFactory();
        if (windowObj is not Window dialog)
            throw new ArgumentException("Factory must return an Avalonia Window");

        return await dialog.ShowDialog<TResult?>(owner);
    }
}
