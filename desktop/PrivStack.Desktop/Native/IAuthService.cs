namespace PrivStack.Desktop.Native;

/// <summary>
/// Handles app-level master password authentication.
/// </summary>
public interface IAuthService
{
    bool IsAuthInitialized();
    bool IsAuthUnlocked();
    void InitializeAuth(string masterPassword);
    void UnlockApp(string masterPassword);
    void LockApp();
    void ChangeAppPassword(string oldPassword, string newPassword);
    bool ValidateMasterPassword(string masterPassword);
    string SetupRecovery();
    bool HasRecovery();
    void ResetWithRecovery(string mnemonic, string newPassword);
    void ResetWithUnifiedRecovery(string mnemonic, string newPassword);
}
