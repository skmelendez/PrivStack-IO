//! Encryption layer for PrivStack.
//!
//! Provides per-entity encryption using:
//! - Argon2id for key derivation from passwords
//! - ChaCha20-Poly1305 for authenticated encryption
//! - Secure key management with zeroization
//!
//! # Architecture
//!
//! The encryption uses a two-tier key system:
//!
//! 1. **Master Key**: Derived from the user's password using Argon2id.
//!    This key is never stored - it's derived each time the user unlocks.
//!
//! 2. **Entity Key**: A random key generated for each entity.
//!    The entity key is encrypted with the master key and stored
//!    alongside the encrypted data.
//!
//! This architecture allows:
//! - Changing the password without re-encrypting all data
//! - Sharing individual entities by sharing just that entity's key
//! - Forward secrecy (compromising one entity key doesn't affect others)

mod cipher;
mod document;
pub mod encryptor;
pub mod envelope;
mod error;
mod key;
pub mod recovery;

pub use cipher::{
    decrypt, decrypt_string, encrypt, encrypt_string, EncryptedData, NONCE_SIZE, TAG_SIZE,
};
pub use document::{
    decrypt_document, encrypt_document, reencrypt_document_key, EncryptedDocument,
    EncryptedDocumentMetadata,
};
pub use encryptor::{DataEncryptor, EncryptorError, EncryptorResult, PassthroughEncryptor};
pub use envelope::{
    decrypt_private_key, decrypt_private_key_with_mnemonic, encrypt_private_key,
    encrypt_private_key_with_mnemonic, generate_cloud_keypair, generate_recovery_mnemonic,
    mnemonic_to_key, open_dek, seal_dek, CloudKeyPair, PassphraseProtectedKey, SealedEnvelope,
};
pub use error::{CryptoError, CryptoResult};
pub use key::{derive_key, generate_random_key, DerivedKey, KdfParams, Salt, KEY_SIZE, SALT_SIZE};
pub use recovery::{create_recovery_blob, create_recovery_blob_with_mnemonic, open_recovery_blob, reencrypt_recovery_blob, RecoveryBlob};
