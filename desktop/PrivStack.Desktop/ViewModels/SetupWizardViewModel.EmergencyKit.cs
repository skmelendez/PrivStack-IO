using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.EmergencyKit;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Emergency Kit step logic for the setup wizard.
/// Handles mnemonic display and PDF download.
/// </summary>
public partial class SetupWizardViewModel
{
    [ObservableProperty]
    private string[] _recoveryWords = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _hasDownloadedKit;

    [ObservableProperty]
    private bool _isDownloadingKit;

    /// <summary>
    /// Called after password initialization to set up recovery and populate words.
    /// </summary>
    private void SetupRecoveryMnemonic()
    {
        try
        {
            var mnemonic = _authService.SetupRecovery();
            RecoveryWords = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Log.Information("Recovery mnemonic generated ({Count} words)", RecoveryWords.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set up recovery mnemonic");
            SetupError = $"Failed to generate recovery key: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadEmergencyKitAsync()
    {
        if (RecoveryWords.Length == 0) return;

        IsDownloadingKit = true;
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;
            var topLevel = TopLevel.GetTopLevel(lifetime?.MainWindow ?? lifetime?.Windows.FirstOrDefault());
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Emergency Kit",
                SuggestedFileName = $"PrivStack-Emergency-Kit-{DateTime.Now:yyyy-MM-dd}",
                FileTypeChoices =
                [
                    new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }
                ],
                DefaultExtension = "pdf"
            });

            if (file == null) return;

            var outputPath = file.Path.LocalPath;
            EmergencyKitPdfService.Generate(RecoveryWords, WorkspaceName, outputPath);
            HasDownloadedKit = true;
            Log.Information("Emergency Kit PDF saved to {Path}", outputPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save Emergency Kit PDF");
            SetupError = $"Failed to save Emergency Kit: {ex.Message}";
        }
        finally
        {
            IsDownloadingKit = false;
        }
    }

    private SetupStep CompleteEmergencyKitStep()
    {
        return SetupStep.Complete;
    }
}
