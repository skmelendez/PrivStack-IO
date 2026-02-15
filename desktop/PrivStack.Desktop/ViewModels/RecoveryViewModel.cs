using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using Serilog;

namespace PrivStack.Desktop.ViewModels;

/// <summary>
/// Two-step recovery flow: (1) enter 12-word mnemonic, (2) set new password.
/// </summary>
public partial class RecoveryViewModel : ViewModelBase
{
    private static readonly ILogger _log = Serilog.Log.ForContext<RecoveryViewModel>();

    private readonly IAuthService _authService;
    private readonly IMasterPasswordCache? _passwordCache;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    private int _step = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;

    // Step 1: mnemonic input
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceedToStep2))]
    private string _mnemonic = string.Empty;

    [ObservableProperty]
    private string _mnemonicError = string.Empty;

    // Step 2: new password
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    [NotifyPropertyChangedFor(nameof(PasswordsMatch))]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    [NotifyPropertyChangedFor(nameof(PasswordsMatch))]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private string _resetError = string.Empty;

    [ObservableProperty]
    private bool _isResetting;

    public bool PasswordsMatch => NewPassword == ConfirmPassword;

    public bool CanProceedToStep2
    {
        get
        {
            var words = Mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 12;
        }
    }

    public bool CanReset => !string.IsNullOrWhiteSpace(NewPassword)
                         && NewPassword.Length >= 8
                         && PasswordsMatch
                         && !IsResetting;

    public event EventHandler? RecoveryCompleted;
    public event EventHandler? RecoveryCancelled;

    public RecoveryViewModel(IAuthService authService, IMasterPasswordCache? passwordCache = null)
    {
        _authService = authService;
        _passwordCache = passwordCache;
    }

    [RelayCommand]
    private void ProceedToStep2()
    {
        if (!CanProceedToStep2) return;

        MnemonicError = string.Empty;
        Step = 2;
    }

    [RelayCommand]
    private void BackToStep1()
    {
        ResetError = string.Empty;
        Step = 1;
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (!CanReset) return;

        IsResetting = true;
        ResetError = string.Empty;

        try
        {
            var mnemonic = Mnemonic.Trim();
            var password = NewPassword;

            await Task.Run(() => _authService.ResetWithUnifiedRecovery(mnemonic, password));

            _passwordCache?.Set(password);

            // Clear sensitive data
            Mnemonic = string.Empty;
            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;

            _log.Information("Password reset via recovery completed");
            RecoveryCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (PrivStackException ex) when (ex.ErrorCode == PrivStackError.InvalidRecoveryMnemonic)
        {
            ResetError = "Invalid recovery words. Please check your Emergency Kit and try again.";
            Step = 1;
            MnemonicError = ResetError;
        }
        catch (PrivStackException ex) when (ex.ErrorCode == PrivStackError.RecoveryNotConfigured)
        {
            ResetError = "Recovery is not configured for this workspace.";
        }
        catch (PrivStackException ex) when (ex.ErrorCode == PrivStackError.PasswordTooShort)
        {
            ResetError = "Password must be at least 8 characters.";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Recovery failed");
            ResetError = $"Recovery failed: {ex.Message}";
        }
        finally
        {
            IsResetting = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Mnemonic = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        RecoveryCancelled?.Invoke(this, EventArgs.Empty);
    }

    partial void OnMnemonicChanged(string value)
    {
        if (!string.IsNullOrEmpty(MnemonicError))
            MnemonicError = string.Empty;
        OnPropertyChanged(nameof(CanProceedToStep2));
    }

    partial void OnNewPasswordChanged(string value)
    {
        if (!string.IsNullOrEmpty(ResetError))
            ResetError = string.Empty;
    }

    partial void OnConfirmPasswordChanged(string value)
    {
        if (!string.IsNullOrEmpty(ResetError))
            ResetError = string.Empty;
    }
}
