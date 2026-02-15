//! Master password recovery via BIP39 mnemonic.
//!
//! Stores the master key encrypted with a mnemonic-derived key so users
//! can regain access without destroying their data.

use crate::error::{CryptoError, CryptoResult};
use crate::key::DerivedKey;
use crate::{decrypt, encrypt, EncryptedData};
use crate::envelope::{generate_recovery_mnemonic, mnemonic_to_key};
use serde::{Deserialize, Serialize};

/// Master key encrypted with a mnemonic-derived key for offline recovery.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RecoveryBlob {
    /// Master key bytes encrypted with the mnemonic-derived key.
    pub encrypted_key: EncryptedData,
    /// Unix timestamp when this blob was created.
    pub created_at: i64,
}

/// Generates a new BIP39 mnemonic and encrypts the current master key with it.
///
/// Returns `(mnemonic, blob)` — the mnemonic must be shown to the user once
/// and never stored digitally. The blob is persisted in vault metadata.
pub fn create_recovery_blob(master_key: &DerivedKey) -> CryptoResult<(String, RecoveryBlob)> {
    let mnemonic = generate_recovery_mnemonic()?;
    let recovery_key_bytes = mnemonic_to_key(&mnemonic)?;
    let recovery_key = DerivedKey::from_bytes(recovery_key_bytes);

    let encrypted_key = encrypt(&recovery_key, master_key.as_bytes())?;
    let created_at = chrono::Utc::now().timestamp();

    Ok((mnemonic, RecoveryBlob { encrypted_key, created_at }))
}

/// Decrypts the master key from a recovery blob using the user's mnemonic.
pub fn open_recovery_blob(blob: &RecoveryBlob, mnemonic: &str) -> CryptoResult<DerivedKey> {
    let recovery_key_bytes = mnemonic_to_key(mnemonic)?;
    let recovery_key = DerivedKey::from_bytes(recovery_key_bytes);

    let plaintext = decrypt(&recovery_key, &blob.encrypted_key)?;
    if plaintext.len() != 32 {
        return Err(CryptoError::InvalidKeyLength {
            expected: 32,
            actual: plaintext.len(),
        });
    }

    let mut bytes = [0u8; 32];
    bytes.copy_from_slice(&plaintext);
    Ok(DerivedKey::from_bytes(bytes))
}

/// Encrypts the master key with a caller-provided mnemonic (no new mnemonic generated).
///
/// Used by the unified recovery flow to re-encrypt the vault recovery blob
/// with the same mnemonic that protects the cloud keypair.
pub fn create_recovery_blob_with_mnemonic(
    master_key: &DerivedKey,
    mnemonic: &str,
) -> CryptoResult<RecoveryBlob> {
    let recovery_key_bytes = mnemonic_to_key(mnemonic)?;
    let recovery_key = DerivedKey::from_bytes(recovery_key_bytes);

    let encrypted_key = encrypt(&recovery_key, master_key.as_bytes())?;
    let created_at = chrono::Utc::now().timestamp();

    Ok(RecoveryBlob { encrypted_key, created_at })
}

/// Re-encrypts a recovery blob for a new master key using the same mnemonic.
///
/// This keeps the user's existing Emergency Kit PDF valid after a password change.
pub fn reencrypt_recovery_blob(
    blob: &RecoveryBlob,
    mnemonic: &str,
    new_master_key: &DerivedKey,
) -> CryptoResult<RecoveryBlob> {
    // Verify mnemonic is valid by opening the old blob
    let _ = open_recovery_blob(blob, mnemonic)?;

    let recovery_key_bytes = mnemonic_to_key(mnemonic)?;
    let recovery_key = DerivedKey::from_bytes(recovery_key_bytes);
    let encrypted_key = encrypt(&recovery_key, new_master_key.as_bytes())?;

    Ok(RecoveryBlob {
        encrypted_key,
        created_at: blob.created_at,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::key::generate_random_key;

    #[test]
    fn round_trip_recovery_blob() {
        let master_key = generate_random_key();
        let (mnemonic, blob) = create_recovery_blob(&master_key).unwrap();

        let recovered = open_recovery_blob(&blob, &mnemonic).unwrap();
        assert_eq!(master_key.as_bytes(), recovered.as_bytes());
    }

    #[test]
    fn wrong_mnemonic_fails() {
        let master_key = generate_random_key();
        let (_mnemonic, blob) = create_recovery_blob(&master_key).unwrap();

        let wrong = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        assert!(open_recovery_blob(&blob, wrong).is_err());
    }

    #[test]
    fn round_trip_recovery_blob_with_mnemonic() {
        let master_key = generate_random_key();
        let mnemonic = crate::envelope::generate_recovery_mnemonic().unwrap();

        let blob = create_recovery_blob_with_mnemonic(&master_key, &mnemonic).unwrap();
        let recovered = open_recovery_blob(&blob, &mnemonic).unwrap();
        assert_eq!(master_key.as_bytes(), recovered.as_bytes());
    }

    #[test]
    fn same_mnemonic_decrypts_both_vault_blob_and_cloud_key() {
        let master_key = generate_random_key();
        let mnemonic = crate::envelope::generate_recovery_mnemonic().unwrap();

        // Vault blob encrypted with shared mnemonic
        let vault_blob = create_recovery_blob_with_mnemonic(&master_key, &mnemonic).unwrap();

        // Cloud keypair encrypted with same mnemonic (simulated as another key)
        let cloud_secret = generate_random_key();
        let cloud_blob = create_recovery_blob_with_mnemonic(&cloud_secret, &mnemonic).unwrap();

        // Both should decrypt with the same mnemonic
        let recovered_vault = open_recovery_blob(&vault_blob, &mnemonic).unwrap();
        let recovered_cloud = open_recovery_blob(&cloud_blob, &mnemonic).unwrap();

        assert_eq!(master_key.as_bytes(), recovered_vault.as_bytes());
        assert_eq!(cloud_secret.as_bytes(), recovered_cloud.as_bytes());
    }

    #[test]
    fn reencrypt_preserves_mnemonic_validity() {
        let old_key = generate_random_key();
        let (mnemonic, blob) = create_recovery_blob(&old_key).unwrap();

        let new_key = generate_random_key();
        let new_blob = reencrypt_recovery_blob(&blob, &mnemonic, &new_key).unwrap();

        let recovered = open_recovery_blob(&new_blob, &mnemonic).unwrap();
        assert_eq!(new_key.as_bytes(), recovered.as_bytes());
    }
}
