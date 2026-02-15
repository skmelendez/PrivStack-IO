namespace PrivStack.Desktop.Native;

/// <summary>
/// Error codes returned by the native PrivStack library.
/// Must match the Rust PrivStackError enum.
/// </summary>
public enum PrivStackError
{
    /// <summary>Operation succeeded.</summary>
    Ok = 0,

    /// <summary>Null pointer argument.</summary>
    NullPointer = 1,

    /// <summary>Invalid UTF-8 string.</summary>
    InvalidUtf8 = 2,

    /// <summary>JSON serialization error.</summary>
    JsonError = 3,

    /// <summary>Storage error.</summary>
    StorageError = 4,

    /// <summary>Document not found.</summary>
    NotFound = 5,

    /// <summary>Handle not initialized.</summary>
    NotInitialized = 6,

    /// <summary>Sync not running.</summary>
    SyncNotRunning = 7,

    /// <summary>Sync already running.</summary>
    SyncAlreadyRunning = 8,

    /// <summary>Network/sync error.</summary>
    SyncError = 9,

    /// <summary>Peer not found.</summary>
    PeerNotFound = 10,

    /// <summary>Authentication error.</summary>
    AuthError = 11,

    /// <summary>Cloud storage error.</summary>
    CloudError = 12,

    /// <summary>License key format invalid.</summary>
    LicenseInvalidFormat = 13,

    /// <summary>License signature failed.</summary>
    LicenseInvalidSignature = 14,

    /// <summary>License expired.</summary>
    LicenseExpired = 15,

    /// <summary>License not activated.</summary>
    LicenseNotActivated = 16,

    /// <summary>License activation failed.</summary>
    LicenseActivationFailed = 17,

    /// <summary>Invalid sync code format.</summary>
    InvalidSyncCode = 18,

    /// <summary>Peer not trusted.</summary>
    PeerNotTrusted = 19,

    /// <summary>Pairing error.</summary>
    PairingError = 20,

    /// <summary>Vault is locked.</summary>
    VaultLocked = 21,

    /// <summary>Vault not found.</summary>
    VaultNotFound = 22,

    /// <summary>Plugin error (load, unload, or route failure).</summary>
    PluginError = 23,

    /// <summary>Plugin not found.</summary>
    PluginNotFound = 24,

    /// <summary>Plugin permission denied.</summary>
    PluginPermissionDenied = 25,

    /// <summary>Vault already initialized.</summary>
    VaultAlreadyInitialized = 26,

    /// <summary>Password too short (min 8 characters).</summary>
    PasswordTooShort = 27,

    /// <summary>Invalid argument.</summary>
    InvalidArgument = 28,

    /// <summary>Cloud sync error (S3 transport, outbox, or orchestration failure).</summary>
    CloudSyncError = 29,

    /// <summary>Cloud storage quota exceeded.</summary>
    QuotaExceeded = 30,

    /// <summary>Share permission denied.</summary>
    ShareDenied = 31,

    /// <summary>Envelope encryption/decryption error.</summary>
    EnvelopeError = 32,

    /// <summary>Cloud API authentication error.</summary>
    CloudAuthError = 33,

    /// <summary>Unknown error.</summary>
    Unknown = 99,
}
