namespace PrivStack.Desktop.Services;

public record WorkspaceMigrationProgress(
    long BytesCopied,
    long TotalBytes,
    int FilesCopied,
    int TotalFiles,
    string CurrentFile,
    MigrationPhase Phase);

public enum MigrationPhase
{
    Calculating,
    Copying,
    Verifying,
    Reloading,
    CleaningUp,
    Complete,
    Failed
}
