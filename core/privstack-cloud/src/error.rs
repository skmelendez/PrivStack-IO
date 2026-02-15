//! Cloud sync error types.

use thiserror::Error;

/// Result type for cloud operations.
pub type CloudResult<T> = Result<T, CloudError>;

/// Errors that can occur in cloud sync operations.
#[derive(Debug, Error)]
pub enum CloudError {
    #[error("S3 operation failed: {0}")]
    S3(String),

    #[error("API request failed: {0}")]
    Api(String),

    #[error("storage quota exceeded: used {used} of {quota} bytes")]
    QuotaExceeded { used: u64, quota: u64 },

    #[error("STS credentials expired or invalid")]
    CredentialExpired,

    #[error("entity lock contention: {0}")]
    LockContention(String),

    #[error("share operation denied: {0}")]
    ShareDenied(String),

    #[error("envelope encryption error: {0}")]
    Envelope(String),

    #[error("authentication required")]
    AuthRequired,

    #[error("authentication failed: {0}")]
    AuthFailed(String),

    #[error("serialization error: {0}")]
    Serialization(#[from] serde_json::Error),

    #[error("HTTP error: {0}")]
    Http(#[from] reqwest::Error),

    #[error("crypto error: {0}")]
    Crypto(#[from] privstack_crypto::CryptoError),

    #[error("not found: {0}")]
    NotFound(String),

    #[error("invalid configuration: {0}")]
    Config(String),
}
