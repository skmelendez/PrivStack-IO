//! Generic encrypted vault with blob storage.
//!
//! Provides password-protected vault instances backed by DuckDB.
//! Encryption keys never leave the vault — all encrypt/decrypt happens internally.
//!
//! Each vault has its own salt + verification token (Argon2id-derived).
//! Blobs are encrypted with ChaCha20-Poly1305 using the vault's derived key.

use chrono::Utc;
use duckdb::{params, Connection};
use privstack_crypto::{
    decrypt, decrypt_document, derive_key, encrypt, encrypt_document, reencrypt_document_key,
    DataEncryptor, DerivedKey, EncryptedData, EncryptedDocument, EncryptorError, EncryptorResult,
    KdfParams, Salt, KEY_SIZE,
};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex, RwLock};

// ============================================================================
// Error types
// ============================================================================

#[derive(Debug, thiserror::Error)]
pub enum VaultError {
    #[error("vault not initialized")]
    NotInitialized,
    #[error("vault is locked")]
    Locked,
    #[error("vault already initialized")]
    AlreadyInitialized,
    #[error("invalid password")]
    InvalidPassword,
    #[error("password too short (min 8 characters)")]
    PasswordTooShort,
    #[error("blob not found: {0}")]
    BlobNotFound(String),
    #[error("vault not found: {0}")]
    VaultNotFound(String),
    #[error("storage error: {0}")]
    Storage(String),
    #[error("crypto error: {0}")]
    Crypto(String),
}

pub type VaultResult<T> = Result<T, VaultError>;

// ============================================================================
// Vault — single encrypted vault instance
// ============================================================================

/// A single encrypted vault with its own password/key.
pub struct Vault {
    id: String,
    /// Sanitized ID safe for use in SQL table names (dots → underscores, etc.)
    table_prefix: String,
    conn: Arc<Mutex<Connection>>,
    key: Arc<RwLock<Option<DerivedKey>>>,
}

/// Sanitize a vault ID for use in SQL identifiers (table names).
/// Replaces any character that isn't alphanumeric or underscore with '_'.
fn sanitize_for_sql(id: &str) -> String {
    id.chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '_' { c } else { '_' })
        .collect()
}

/// Verification token: we encrypt a known plaintext with the derived key.
/// On unlock we decrypt it and check it matches.
const VERIFICATION_PLAINTEXT: &[u8] = b"privstack-vault-verification-token-v1";

impl Vault {
    /// Opens (or creates tables for) a vault with the given ID.
    pub fn open(id: &str, conn: Arc<Mutex<Connection>>) -> VaultResult<Self> {
        let vault = Self {
            table_prefix: sanitize_for_sql(id),
            id: id.to_string(),
            conn,
            key: Arc::new(RwLock::new(None)),
        };
        vault.ensure_tables()?;
        Ok(vault)
    }

    fn ensure_tables(&self) -> VaultResult<()> {
        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        conn.execute_batch(&format!(
            "CREATE TABLE IF NOT EXISTS {meta_table} (
                key VARCHAR PRIMARY KEY,
                value BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS {blobs_table} (
                blob_id VARCHAR PRIMARY KEY,
                encrypted_data BLOB NOT NULL,
                size BIGINT NOT NULL DEFAULT 0,
                content_hash VARCHAR,
                created_at BIGINT NOT NULL,
                modified_at BIGINT NOT NULL
            );"
        ))
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        Ok(())
    }

    /// Whether the vault has been initialized with a password.
    pub fn is_initialized(&self) -> bool {
        let conn = match self.conn.lock() {
            Ok(c) => c,
            Err(_) => return false,
        };
        let meta_table = format!("vault_{}_meta", self.table_prefix);
        let result: Result<i64, _> = conn.query_row(
            &format!("SELECT COUNT(*) FROM {meta_table} WHERE key = 'salt'"),
            [],
            |row| row.get(0),
        );
        matches!(result, Ok(n) if n > 0)
    }

    /// Initialize vault with a password (first-time setup).
    pub fn initialize(&self, password: &str) -> VaultResult<()> {
        if password.len() < 8 {
            return Err(VaultError::PasswordTooShort);
        }
        if self.is_initialized() {
            return Err(VaultError::AlreadyInitialized);
        }

        let salt = Salt::random();
        let key = derive_key(password, &salt, &KdfParams::default())
            .map_err(|e| VaultError::Crypto(e.to_string()))?;

        // Create verification token
        let verification = encrypt(&key, VERIFICATION_PLAINTEXT)
            .map_err(|e| VaultError::Crypto(e.to_string()))?;
        let verification_bytes =
            serde_json::to_vec(&verification).map_err(|e| VaultError::Storage(e.to_string()))?;

        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);

        conn.execute(
            &format!("INSERT INTO {meta_table} (key, value) VALUES ('salt', ?)"),
            params![salt.as_bytes().to_vec()],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        conn.execute(
            &format!("INSERT INTO {meta_table} (key, value) VALUES ('verification', ?)"),
            params![verification_bytes],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        // Store key in memory
        let mut key_guard = self.key.write().unwrap();
        *key_guard = Some(key);

        Ok(())
    }

    /// Unlock the vault with a password.
    pub fn unlock(&self, password: &str) -> VaultResult<()> {
        if !self.is_initialized() {
            return Err(VaultError::NotInitialized);
        }

        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);

        // Read salt
        let salt_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT value FROM {meta_table} WHERE key = 'salt'"),
                [],
                |row| row.get(0),
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let salt = if salt_bytes.len() == 16 {
            let mut arr = [0u8; 16];
            arr.copy_from_slice(&salt_bytes);
            Salt::from_bytes(arr)
        } else {
            return Err(VaultError::Storage("invalid salt length".into()));
        };

        let key = derive_key(password, &salt, &KdfParams::default())
            .map_err(|e| VaultError::Crypto(e.to_string()))?;

        // Verify password by decrypting verification token
        let verification_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT value FROM {meta_table} WHERE key = 'verification'"),
                [],
                |row| row.get(0),
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let verification: EncryptedData = serde_json::from_slice(&verification_bytes)
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let decrypted =
            decrypt(&key, &verification).map_err(|_| VaultError::InvalidPassword)?;

        if decrypted != VERIFICATION_PLAINTEXT {
            return Err(VaultError::InvalidPassword);
        }

        drop(conn);

        let mut key_guard = self.key.write().unwrap();
        *key_guard = Some(key);

        Ok(())
    }

    /// Lock the vault (clear key from memory).
    pub fn lock(&self) {
        let mut key_guard = self.key.write().unwrap();
        *key_guard = None;
    }

    /// Whether the vault is currently unlocked.
    pub fn is_unlocked(&self) -> bool {
        self.key.read().unwrap().is_some()
    }

    /// Clone the current derived key (if unlocked).
    pub(crate) fn get_key(&self) -> Option<DerivedKey> {
        self.key.read().unwrap().clone()
    }

    /// Change the vault password. Re-encrypts all blobs.
    pub fn change_password(&self, old_password: &str, new_password: &str) -> VaultResult<()> {
        if new_password.len() < 8 {
            return Err(VaultError::PasswordTooShort);
        }

        // Verify old password first
        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let meta_table = format!("vault_{}_meta", self.table_prefix);
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        let salt_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT value FROM {meta_table} WHERE key = 'salt'"),
                [],
                |row| row.get(0),
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let old_salt = if salt_bytes.len() == 16 {
            let mut arr = [0u8; 16];
            arr.copy_from_slice(&salt_bytes);
            Salt::from_bytes(arr)
        } else {
            return Err(VaultError::Storage("invalid salt length".into()));
        };

        let old_key = derive_key(old_password, &old_salt, &KdfParams::default())
            .map_err(|e| VaultError::Crypto(e.to_string()))?;

        // Verify old password
        let verification_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT value FROM {meta_table} WHERE key = 'verification'"),
                [],
                |row| row.get(0),
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let verification: EncryptedData = serde_json::from_slice(&verification_bytes)
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        decrypt(&old_key, &verification).map_err(|_| VaultError::InvalidPassword)?;

        // Derive new key
        let new_salt = Salt::random();
        let new_key = derive_key(new_password, &new_salt, &KdfParams::default())
            .map_err(|e| VaultError::Crypto(e.to_string()))?;

        // Re-encrypt all blobs
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

        // Update salt and verification
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

        drop(conn);

        // Update in-memory key
        let mut key_guard = self.key.write().unwrap();
        *key_guard = Some(new_key);

        Ok(())
    }

    /// Store an encrypted blob.
    pub fn store_blob(&self, blob_id: &str, data: &[u8]) -> VaultResult<()> {
        let key_guard = self.key.read().unwrap();
        let key = key_guard.as_ref().ok_or(VaultError::Locked)?;

        let encrypted =
            encrypt(key, data).map_err(|e| VaultError::Crypto(e.to_string()))?;
        let enc_bytes =
            serde_json::to_vec(&encrypted).map_err(|e| VaultError::Storage(e.to_string()))?;

        let content_hash = hex::encode(Sha256::digest(data));
        let now = Utc::now().timestamp_millis();

        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        conn.execute(
            &format!(
                "INSERT OR REPLACE INTO {blobs_table} (blob_id, encrypted_data, size, content_hash, created_at, modified_at)
                 VALUES (?, ?, ?, ?, COALESCE((SELECT created_at FROM {blobs_table} WHERE blob_id = ?), ?), ?)"
            ),
            params![blob_id, enc_bytes, data.len() as i64, content_hash, blob_id, now, now],
        )
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        Ok(())
    }

    /// Read and decrypt a blob.
    pub fn read_blob(&self, blob_id: &str) -> VaultResult<Vec<u8>> {
        let key_guard = self.key.read().unwrap();
        let key = key_guard.as_ref().ok_or(VaultError::Locked)?;

        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        let enc_bytes: Vec<u8> = conn
            .query_row(
                &format!("SELECT encrypted_data FROM {blobs_table} WHERE blob_id = ?"),
                params![blob_id],
                |row| row.get(0),
            )
            .map_err(|_| VaultError::BlobNotFound(blob_id.to_string()))?;

        let encrypted: EncryptedData = serde_json::from_slice(&enc_bytes)
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        decrypt(key, &encrypted).map_err(|e| VaultError::Crypto(e.to_string()))
    }

    /// Delete a blob.
    pub fn delete_blob(&self, blob_id: &str) -> VaultResult<()> {
        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        let affected = conn
            .execute(
                &format!("DELETE FROM {blobs_table} WHERE blob_id = ?"),
                params![blob_id],
            )
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        if affected == 0 {
            return Err(VaultError::BlobNotFound(blob_id.to_string()));
        }
        Ok(())
    }

    /// List all blob IDs in this vault.
    pub fn list_blobs(&self) -> VaultResult<Vec<String>> {
        let conn = self.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let blobs_table = format!("vault_{}_blobs", self.table_prefix);

        let mut stmt = conn
            .prepare(&format!("SELECT blob_id FROM {blobs_table} ORDER BY modified_at DESC"))
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let ids: Vec<String> = stmt
            .query_map([], |row| row.get(0))
            .map_err(|e| VaultError::Storage(e.to_string()))?
            .filter_map(|r| r.ok())
            .collect();

        Ok(ids)
    }

    /// Get the vault ID.
    pub fn id(&self) -> &str {
        &self.id
    }
}

// ============================================================================
// VaultManager — manages multiple vaults sharing a connection
// ============================================================================

/// Manages multiple named vaults, all sharing a single DuckDB connection.
pub struct VaultManager {
    vaults: RwLock<HashMap<String, Vault>>,
    conn: Arc<Mutex<Connection>>,
}

/// Blob metadata for JSON serialization.
#[derive(Debug, Serialize)]
pub struct BlobInfo {
    pub blob_id: String,
    pub size: i64,
    pub content_hash: Option<String>,
    pub created_at: i64,
    pub modified_at: i64,
}

impl VaultManager {
    /// Open a vault manager backed by a DuckDB file.
    pub fn open(db_path: &Path) -> VaultResult<Self> {
        let conn = if db_path.to_str() == Some(":memory:") {
            Connection::open_in_memory()
        } else {
            Connection::open(db_path)
        }
        .map_err(|e| VaultError::Storage(e.to_string()))?;

        // Cap memory/threads — DuckDB defaults to ~80% RAM per connection
        if db_path.to_str() != Some(":memory:") {
            conn.execute_batch("PRAGMA memory_limit='64MB'; PRAGMA threads=1;")
                .map_err(|e| VaultError::Storage(e.to_string()))?;
        }

        Ok(Self {
            vaults: RwLock::new(HashMap::new()),
            conn: Arc::new(Mutex::new(conn)),
        })
    }

    /// Open a vault manager with an in-memory database.
    pub fn open_in_memory() -> VaultResult<Self> {
        let conn =
            Connection::open_in_memory().map_err(|e| VaultError::Storage(e.to_string()))?;
        Ok(Self {
            vaults: RwLock::new(HashMap::new()),
            conn: Arc::new(Mutex::new(conn)),
        })
    }

    /// Create (or get) a vault by ID.
    pub fn create_vault(&self, vault_id: &str) -> VaultResult<()> {
        let mut vaults = self.vaults.write().unwrap();
        if !vaults.contains_key(vault_id) {
            let vault = Vault::open(vault_id, self.conn.clone())?;
            vaults.insert(vault_id.to_string(), vault);
        }
        Ok(())
    }

    /// Get a reference to a vault (creates it if needed).
    fn get_or_create_vault(&self, vault_id: &str) -> VaultResult<()> {
        let vaults = self.vaults.read().unwrap();
        if vaults.contains_key(vault_id) {
            return Ok(());
        }
        drop(vaults);
        self.create_vault(vault_id)
    }

    /// Execute a closure with a vault reference.
    fn with_vault<F, T>(&self, vault_id: &str, f: F) -> VaultResult<T>
    where
        F: FnOnce(&Vault) -> VaultResult<T>,
    {
        self.get_or_create_vault(vault_id)?;
        let vaults = self.vaults.read().unwrap();
        let vault = vaults
            .get(vault_id)
            .ok_or_else(|| VaultError::VaultNotFound(vault_id.to_string()))?;
        f(vault)
    }

    pub fn is_initialized(&self, vault_id: &str) -> bool {
        self.get_or_create_vault(vault_id).ok();
        let vaults = self.vaults.read().unwrap();
        vaults
            .get(vault_id)
            .map_or(false, |v| v.is_initialized())
    }

    pub fn is_unlocked(&self, vault_id: &str) -> bool {
        let vaults = self.vaults.read().unwrap();
        vaults.get(vault_id).map_or(false, |v| v.is_unlocked())
    }

    pub fn initialize(&self, vault_id: &str, password: &str) -> VaultResult<()> {
        self.with_vault(vault_id, |v| v.initialize(password))
    }

    pub fn unlock(&self, vault_id: &str, password: &str) -> VaultResult<()> {
        self.with_vault(vault_id, |v| v.unlock(password))
    }

    pub fn lock(&self, vault_id: &str) {
        let vaults = self.vaults.read().unwrap();
        if let Some(v) = vaults.get(vault_id) {
            v.lock();
        }
    }

    pub fn lock_all(&self) {
        let vaults = self.vaults.read().unwrap();
        for v in vaults.values() {
            v.lock();
        }
    }

    /// Unlock all initialized vaults with the same master password.
    /// Ensures the "default" vault is loaded first and rejects the attempt
    /// if no initialized vaults exist (prevents empty-loop bypass).
    pub fn unlock_all(&self, password: &str) -> VaultResult<()> {
        // Ensure the default vault is in the map so it participates in the loop
        self.get_or_create_vault("default")?;

        let vaults = self.vaults.read().unwrap();
        let mut unlocked_any = false;
        for v in vaults.values() {
            if v.is_initialized() && !v.is_unlocked() {
                v.unlock(password)?;
                unlocked_any = true;
            }
        }

        // If nothing was unlocked and nothing was already unlocked,
        // the vault store has no initialized vaults — reject
        if !unlocked_any && !vaults.values().any(|v| v.is_unlocked()) {
            return Err(VaultError::NotInitialized);
        }

        Ok(())
    }

    /// Change password for all initialized vaults.
    pub fn change_password_all(&self, old_password: &str, new_password: &str) -> VaultResult<()> {
        let vaults = self.vaults.read().unwrap();
        for v in vaults.values() {
            if v.is_initialized() {
                v.change_password(old_password, new_password)?;
            }
        }
        Ok(())
    }

    pub fn change_password(
        &self,
        vault_id: &str,
        old_password: &str,
        new_password: &str,
    ) -> VaultResult<()> {
        self.with_vault(vault_id, |v| v.change_password(old_password, new_password))
    }

    pub fn store_blob(&self, vault_id: &str, blob_id: &str, data: &[u8]) -> VaultResult<()> {
        self.with_vault(vault_id, |v| v.store_blob(blob_id, data))
    }

    pub fn read_blob(&self, vault_id: &str, blob_id: &str) -> VaultResult<Vec<u8>> {
        self.with_vault(vault_id, |v| v.read_blob(blob_id))
    }

    pub fn delete_blob(&self, vault_id: &str, blob_id: &str) -> VaultResult<()> {
        self.with_vault(vault_id, |v| v.delete_blob(blob_id))
    }

    pub fn list_blobs(&self, vault_id: &str) -> VaultResult<Vec<BlobInfo>> {
        self.get_or_create_vault(vault_id)?;
        let vaults = self.vaults.read().unwrap();
        let vault = vaults
            .get(vault_id)
            .ok_or_else(|| VaultError::VaultNotFound(vault_id.to_string()))?;

        let conn = vault.conn.lock().map_err(|e| VaultError::Storage(e.to_string()))?;
        let blobs_table = format!("vault_{}_blobs", sanitize_for_sql(vault_id));

        let mut stmt = conn
            .prepare(&format!(
                "SELECT blob_id, size, content_hash, created_at, modified_at FROM {blobs_table} ORDER BY modified_at DESC"
            ))
            .map_err(|e| VaultError::Storage(e.to_string()))?;

        let infos: Vec<BlobInfo> = stmt
            .query_map([], |row| {
                Ok(BlobInfo {
                    blob_id: row.get(0)?,
                    size: row.get(1)?,
                    content_hash: row.get(2)?,
                    created_at: row.get(3)?,
                    modified_at: row.get(4)?,
                })
            })
            .map_err(|e| VaultError::Storage(e.to_string()))?
            .filter_map(|r| r.ok())
            .collect();

        Ok(infos)
    }
}

// ============================================================================
// DataEncryptor implementation — routes entity/blob encryption through vault
// ============================================================================

impl DataEncryptor for VaultManager {
    fn encrypt_bytes(&self, entity_id: &str, data: &[u8]) -> EncryptorResult<Vec<u8>> {
        let key = self.default_key()?;
        let doc = encrypt_document(entity_id, data, &key)
            .map_err(|e| EncryptorError::Crypto(e.to_string()))?;
        serde_json::to_vec(&doc).map_err(|e| EncryptorError::Serialization(e.to_string()))
    }

    fn decrypt_bytes(&self, data: &[u8]) -> EncryptorResult<Vec<u8>> {
        let key = self.default_key()?;
        let doc: EncryptedDocument =
            serde_json::from_slice(data).map_err(|e| EncryptorError::Serialization(e.to_string()))?;
        decrypt_document(&doc, &key).map_err(|e| EncryptorError::Crypto(e.to_string()))
    }

    fn reencrypt_bytes(
        &self,
        data: &[u8],
        old_key_bytes: &[u8],
        new_key_bytes: &[u8],
    ) -> EncryptorResult<Vec<u8>> {
        let old_key = key_from_bytes(old_key_bytes)?;
        let new_key = key_from_bytes(new_key_bytes)?;
        let doc: EncryptedDocument =
            serde_json::from_slice(data).map_err(|e| EncryptorError::Serialization(e.to_string()))?;
        let re = reencrypt_document_key(&doc, &old_key, &new_key)
            .map_err(|e| EncryptorError::Crypto(e.to_string()))?;
        serde_json::to_vec(&re).map_err(|e| EncryptorError::Serialization(e.to_string()))
    }

    fn is_available(&self) -> bool {
        self.is_unlocked("default")
    }
}

impl VaultManager {
    /// Get the current default vault key bytes (for re-encryption orchestration).
    /// Returns None if the vault is locked.
    pub fn default_key_bytes(&self) -> Option<Vec<u8>> {
        self.default_key().ok().map(|k| k.as_bytes().to_vec())
    }

    /// Get the derived key of the "default" vault, or error if locked.
    fn default_key(&self) -> EncryptorResult<DerivedKey> {
        self.get_or_create_vault("default")
            .map_err(|e| EncryptorError::Crypto(e.to_string()))?;
        let vaults = self.vaults.read().unwrap();
        let vault = vaults
            .get("default")
            .ok_or(EncryptorError::Unavailable)?;
        vault.get_key().ok_or(EncryptorError::Unavailable)
    }
}

fn key_from_bytes(bytes: &[u8]) -> EncryptorResult<DerivedKey> {
    if bytes.len() != KEY_SIZE {
        return Err(EncryptorError::Crypto(format!(
            "invalid key length: expected {KEY_SIZE}, got {}",
            bytes.len()
        )));
    }
    let mut arr = [0u8; KEY_SIZE];
    arr.copy_from_slice(bytes);
    Ok(DerivedKey::from_bytes(arr))
}

// Need hex for content_hash output
mod hex {
    pub fn encode(bytes: impl AsRef<[u8]>) -> String {
        bytes
            .as_ref()
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect()
    }
}
