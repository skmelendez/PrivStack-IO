//! Master password recovery for vault instances.
//!
//! Provides setup and recovery methods on `Vault` using BIP39 mnemonics.
//! The recovery blob (master key encrypted with a mnemonic-derived key)
//! is stored as a JSON value in the vault's meta table under the key
//! `"recovery_blob"`.

use duckdb::params;
use privstack_crypto::recovery::{create_recovery_blob, create_recovery_blob_with_mnemonic, open_recovery_blob, reencrypt_recovery_blob, RecoveryBlob};
use privstack_crypto::{decrypt, derive_key, encrypt, EncryptedData, KdfParams, Salt};

use crate::{Vault, VaultError, VaultManager, VaultResult, VERIFICATION_PLAINTEXT};

// ============================================================================
// Vault recovery methods
// ============================================================================

impl Vault {
    /// Generates a new BIP39 mnemonic and stores an encrypted recovery blob
    /// in the vault's meta table. Returns the 12-word mnemonic to show the user.
    ///
    /// The vault must be unlocked (key in memory).
    pub fn setup_recovery(&self) -> VaultResult<String> {
        let key = self.get_key().ok_or(VaultError::Locked)?;

        let (mnemonic, blob) =
            create_recovery_blob(&key).map_err(|e| VaultError::Crypto(e.to_string()))?;

        let blob_json =
            serde_json::to_vec(&blob).map_err(|e| VaultError::Storage(e.to_string()))?;

        let conn = self
            .conn
            .lock()
            .map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);

        // Upsert: INSERT OR REPLACE
        conn.execute(
            &format!(
                "INSERT OR REPLACE INTO {meta_table} (key, value) VALUES ('recovery_blob', ?)"
            ),
            params![blob_json],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        Ok(mnemonic)
    }

    /// Stores an encrypted recovery blob using a caller-provided mnemonic.
    ///
    /// Used by the unified recovery flow where a single mnemonic protects
    /// both the vault master key and the cloud keypair.
    pub fn setup_recovery_with_mnemonic(&self, mnemonic: &str) -> VaultResult<()> {
        let key = self.get_key().ok_or(VaultError::Locked)?;

        let blob = create_recovery_blob_with_mnemonic(&key, mnemonic)
            .map_err(|e| VaultError::Crypto(e.to_string()))?;

        let blob_json =
            serde_json::to_vec(&blob).map_err(|e| VaultError::Storage(e.to_string()))?;

        let conn = self
            .conn
            .lock()
            .map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);

        conn.execute(
            &format!(
                "INSERT OR REPLACE INTO {meta_table} (key, value) VALUES ('recovery_blob', ?)"
            ),
            params![blob_json],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        Ok(())
    }

    /// Checks whether a recovery blob has been configured for this vault.
    pub fn has_recovery(&self) -> VaultResult<bool> {
        let conn = self
            .conn
            .lock()
            .map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);

        let count: i64 = conn
            .query_row(
                &format!("SELECT COUNT(*) FROM {meta_table} WHERE key = 'recovery_blob'"),
                [],
                |row| row.get(0),
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        Ok(count > 0)
    }

    /// Resets the vault password using a recovery mnemonic.
    ///
    /// 1. Reads the stored recovery blob from meta.
    /// 2. Derives the old master key from the mnemonic.
    /// 3. Verifies the recovered key against the stored verification token.
    /// 4. Derives a new key from `new_password`.
    /// 5. Re-encrypts all vault blobs with the new key.
    /// 6. Updates salt, verification token, and recovery blob (so the same
    ///    mnemonic remains valid after password reset).
    /// 7. Returns `(old_key_bytes, new_key_bytes)` for the caller to
    ///    re-encrypt entity_store / blob_store.
    pub fn reset_password_with_recovery(
        &self,
        mnemonic: &str,
        new_password: &str,
    ) -> VaultResult<(Vec<u8>, Vec<u8>)> {
        if new_password.len() < 8 {
            return Err(VaultError::PasswordTooShort);
        }

        let conn = self
            .conn
            .lock()
            .map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        // 1. Read recovery blob
        let blob_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT value FROM {meta_table} WHERE key = 'recovery_blob'"),
                [],
                |row| row.get(0),
            )
            .map_err(|_| VaultError::RecoveryNotConfigured)?;

        let blob: RecoveryBlob = serde_json::from_slice(&blob_bytes)
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        // 2. Recover old master key from mnemonic
        let old_key =
            open_recovery_blob(&blob, mnemonic).map_err(|_| VaultError::InvalidRecoveryMnemonic)?;

        // 3. Verify recovered key against stored verification token
        let verification_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT value FROM {meta_table} WHERE key = 'verification'"),
                [],
                |row| row.get(0),
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let verification: EncryptedData = serde_json::from_slice(&verification_bytes)
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        decrypt(&old_key, &verification).map_err(|_| VaultError::InvalidRecoveryMnemonic)?;

        let old_key_bytes = old_key.as_bytes().to_vec();

        // 4. Derive new key from new password
        let new_salt = Salt::random();
        let new_key = derive_key(new_password, &new_salt, &KdfParams::default())
            .map_err(|e| VaultError::Crypto(e.to_string()))?;

        let new_key_bytes = new_key.as_bytes().to_vec();

        // 5. Re-encrypt all vault blobs
        let mut stmt = conn
            .prepare(&format!(
                "SELECT blob_id, encrypted_data FROM {blobs_table}"
            ))
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let blobs: Vec<(String, Vec<u8>)> = stmt
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
            .map_err(|e| VaultError::Storage(e.to_string()))?
            .filter_map(|r| r.ok())
            .collect();

        drop(stmt);

        for (blob_id, enc_bytes) in &blobs {
            let enc: EncryptedData = serde_json::from_slice(enc_bytes)
                .map_err(|e| VaultError::Storage(e.to_string()))?;
            let plaintext =
                decrypt(&old_key, &enc).map_err(|e| VaultError::Crypto(e.to_string()))?;
            let new_enc =
                encrypt(&new_key, &plaintext).map_err(|e| VaultError::Crypto(e.to_string()))?;
            let new_enc_bytes =
                serde_json::to_vec(&new_enc).map_err(|e| VaultError::Storage(e.to_string()))?;

            conn.execute(
                &format!("UPDATE {blobs_table} SET encrypted_data = ? WHERE blob_id = ?"),
                params![new_enc_bytes, blob_id],
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;
        }

        // 6. Update salt + verification token
        let new_verification = encrypt(&new_key, VERIFICATION_PLAINTEXT)
            .map_err(|e| VaultError::Crypto(e.to_string()))?;
        let new_verification_bytes = serde_json::to_vec(&new_verification)
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        conn.execute(
            &format!("UPDATE {meta_table} SET value = ? WHERE key = 'salt'"),
            params![new_salt.as_bytes().to_vec()],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        conn.execute(
            &format!("UPDATE {meta_table} SET value = ? WHERE key = 'verification'"),
            params![new_verification_bytes],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        // 7. Re-encrypt recovery blob so the same mnemonic stays valid
        let new_blob = reencrypt_recovery_blob(&blob, mnemonic, &new_key)
            .map_err(|e| VaultError::Crypto(e.to_string()))?;
        let new_blob_json =
            serde_json::to_vec(&new_blob).map_err(|e| VaultError::Storage(e.to_string()))?;

        conn.execute(
            &format!("UPDATE {meta_table} SET value = ? WHERE key = 'recovery_blob'"),
            params![new_blob_json],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        drop(conn);

        // 8. Update in-memory key
        let mut key_guard = self.key.write().unwrap();
        *key_guard = Some(new_key);

        Ok((old_key_bytes, new_key_bytes))
    }
}

// ============================================================================
// VaultManager wrappers
// ============================================================================

impl VaultManager {
    /// Sets up recovery for a vault. Returns the 12-word mnemonic.
    pub fn setup_recovery(&self, vault_id: &str) -> VaultResult<String> {
        self.with_vault(vault_id, |v| v.setup_recovery())
    }

    /// Sets up recovery for a vault using a caller-provided mnemonic.
    pub fn setup_recovery_with_mnemonic(&self, vault_id: &str, mnemonic: &str) -> VaultResult<()> {
        self.with_vault(vault_id, |v| v.setup_recovery_with_mnemonic(mnemonic))
    }

    /// Checks whether recovery is configured for a vault.
    pub fn has_recovery(&self, vault_id: &str) -> VaultResult<bool> {
        self.with_vault(vault_id, |v| v.has_recovery())
    }

    /// Resets the password for the default vault using a recovery mnemonic.
    /// Returns `(old_key_bytes, new_key_bytes)` for re-encrypting external stores.
    pub fn reset_password_with_recovery(
        &self,
        vault_id: &str,
        mnemonic: &str,
        new_password: &str,
    ) -> VaultResult<(Vec<u8>, Vec<u8>)> {
        self.with_vault(vault_id, |v| {
            v.reset_password_with_recovery(mnemonic, new_password)
        })
    }
}
