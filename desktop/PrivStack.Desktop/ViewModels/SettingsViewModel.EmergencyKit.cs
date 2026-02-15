using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.EmergencyKit;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Emergency Kit management in Settings > Security.
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _hasEmergencyKit;

    [ObservableProperty]
    private bool _isRegeneratingKit;

    [ObservableProperty]
    private string[] _regeneratedWords = [];

    [ObservableProperty]
    private bool _showRegeneratedWords;

    [ObservableProperty]
    private bool _hasDownloadedRegeneratedKit;

    [ObservableProperty]
    private string _emergencyKitError = string.Empty;

    /// <summary>
    /// Called during settings load to check whether recovery is configured.
    /// </summary>
    private void LoadEmergencyKitStatus()
    {
        try
        {
            HasEmergencyKit = _authService.HasRecovery();
        }
        catch
        {
            HasEmergencyKit = false;
        }
    }

    /// <summary>
    /// True when cloud sync is active and has a keypair — determines whether
    /// to generate a unified recovery kit or a vault-only emergency kit.
    /// </summary>
    private bool IsCloudActive
    {
        get
        {
            try
            {
                var cloudSync = App.Services.GetRequiredService<ICloudSyncService>();
                return cloudSync.IsAuthenticated && cloudSync.HasKeypair;
            }
            catch { return false; }
        }
    }

    [RelayCommand]
    private async Task RegenerateEmergencyKitAsync()
    {
        EmergencyKitError = string.Empty;
        IsRegeneratingKit = true;
        ShowRegeneratedWords = false;
        HasDownloadedRegeneratedKit = false;

        try
        {
            string mnemonic;
            if (IsCloudActive)
            {
                var cloudSync = App.Services.GetRequiredService<ICloudSyncService>();
                var passwordCache = App.Services.GetRequiredService<IMasterPasswordCache>();
                var vaultPassword = passwordCache.Get();
                if (string.IsNullOrEmpty(vaultPassword))
                {
                    EmergencyKitError = "Vault is locked. Please unlock the app first.";
                    return;
                }
                mnemonic = await Task.Run(() => cloudSync.SetupUnifiedRecovery(vaultPassword));
            }
            else
            {
                mnemonic = await Task.Run(() => _authService.SetupRecovery());
            }

            RegeneratedWords = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ShowRegeneratedWords = true;
            HasEmergencyKit = true;
            Log.Information("Emergency Kit regenerated ({Count} words, unified={Unified})",
                RegeneratedWords.Length, IsCloudActive);
        }
        catch (PrivStackException ex)
        {
            EmergencyKitError = $"Failed to regenerate: {ex.Message}";
            Log.Error(ex, "Emergency Kit regeneration failed");
        }
        catch (Exception ex)
        {
            EmergencyKitError = $"Failed to regenerate: {ex.Message}";
            Log.Error(ex, "Emergency Kit regeneration failed");
        }
        finally
        {
            IsRegeneratingKit = false;
        }
    }

    [RelayCommand]
    private async Task DownloadRegeneratedKitAsync()
    {
        if (RegeneratedWords.Length == 0) return;

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

            var workspaceService = App.Services.GetRequiredService<IWorkspaceService>();
            var workspaceName = workspaceService.GetActiveWorkspace()?.Name ?? "PrivStack";

            if (IsCloudActive)
                UnifiedRecoveryKitPdfService.Generate(RegeneratedWords, workspaceName, file.Path.LocalPath);
            else
                EmergencyKitPdfService.Generate(RegeneratedWords, workspaceName, file.Path.LocalPath);
            HasDownloadedRegeneratedKit = true;
            Log.Information("Regenerated Emergency Kit PDF saved");
        }
        catch (Exception ex)
        {
            EmergencyKitError = $"Failed to save PDF: {ex.Message}";
            Log.Error(ex, "Failed to save regenerated Emergency Kit PDF");
        }
    }
}
