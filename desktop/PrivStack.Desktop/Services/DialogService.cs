using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Views.Dialogs;
using Serilog;

namespace PrivStack.Desktop.Services;

public class DialogService : IDialogService
{
    private static readonly ILogger _log = Services.Log.ForContext<DialogService>();
    private Window? _owner;

    /// <summary>
    /// Gets the current owner window for dialog hosting.
    /// </summary>
    public Window? Owner => _owner;

    public DialogService() { }

    public void SetOwner(Window owner)
    {
        _owner = owner;
        _log.Debug("Dialog owner set to {WindowType}", owner.GetType().Name);
    }

    public async Task<bool> ShowConfirmationAsync(string title, string message, string confirmButtonText = "Confirm")
    {
        if (_owner == null)
        {
            _log.Warning("ShowConfirmationAsync called but owner is null");
            return false;
        }

        try
        {
            var dialog = new ConfirmationWindow();
            dialog.SetContent(title, message, confirmButtonText);
            await dialog.ShowDialog<bool?>(_owner);
            return dialog.Confirmed;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error showing ConfirmationWindow");
            return false;
        }
    }

    public async Task<string?> ShowPasswordConfirmationAsync(string title, string message, string confirmButtonText = "Confirm")
    {
        if (_owner == null)
        {
            _log.Warning("ShowPasswordConfirmationAsync called but owner is null");
            return null;
        }

        try
        {
            var dialog = new PasswordConfirmationWindow();
            dialog.SetContent(title, message, confirmButtonText);
            await dialog.ShowDialog<bool?>(_owner);
            return dialog.Confirmed ? dialog.Password : null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error showing PasswordConfirmationWindow");
            return null;
        }
    }

    public async Task<string?> ShowVaultUnlockAsync(
        string? pluginName = null,
        string? pluginIcon = null)
    {
        if (_owner == null)
        {
            _log.Warning("ShowVaultUnlockAsync called but owner is null");
            return null;
        }

        try
        {
            var dialog = new PasswordConfirmationWindow();
            dialog.SetContent(
                "Vault Unlock Requested",
                "Enter your master password to unlock the vault.",
                "Unlock",
                pluginName,
                pluginIcon);
            await dialog.ShowDialog<bool?>(_owner);
            return dialog.Confirmed ? dialog.Password : null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error showing vault unlock dialog");
            return null;
        }
    }

    public async Task<string?> ShowOpenFileDialogAsync(string title, (string Name, string Extension)[] filters)
    {
        if (_owner == null)
        {
            _log.Warning("ShowOpenFileDialogAsync called but owner is null");
            return null;
        }

        try
        {
            var storageProvider = _owner.StorageProvider;
            var fileTypes = filters.Select(f => new FilePickerFileType(f.Name)
            {
                Patterns = new[] { f.Extension }
            }).ToList();

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = fileTypes
            });

            return files.FirstOrDefault()?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error showing open file dialog");
            throw;
        }
    }

    public async Task<string?> ShowSaveFileDialogAsync(string title, string defaultFileName, (string Name, string Extension)[] filters)
    {
        if (_owner == null)
        {
            _log.Warning("ShowSaveFileDialogAsync called but owner is null");
            return null;
        }

        try
        {
            var storageProvider = _owner.StorageProvider;
            var fileTypes = filters.Select(f => new FilePickerFileType(f.Name)
            {
                Patterns = new[] { f.Extension }
            }).ToList();

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = defaultFileName,
                FileTypeChoices = fileTypes
            });

            return file?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error showing save file dialog");
            throw;
        }
    }

    public async Task<string?> ShowOpenFolderDialogAsync(string title)
    {
        if (_owner == null)
        {
            _log.Warning("ShowOpenFolderDialogAsync called but owner is null");
            return null;
        }

        try
        {
            var storageProvider = _owner.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

            return folders.FirstOrDefault()?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error showing open folder dialog");
            throw;
        }
    }
}
