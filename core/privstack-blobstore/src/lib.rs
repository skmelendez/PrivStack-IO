//! Generic namespace-scoped blob storage with optional encryption.
//!
//! When a `DataEncryptor` is provided, blob `data` is encrypted at rest.
//! Content hashes are computed on the *plaintext* before encryption so
//! dedup checks remain valid across password changes.

use chrono::Utc;
use duckdb::{params, Connection};
use privstack_crypto::DataEncryptor;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::path::Path;
use std::sync::{Arc, Mutex};

// ============================================================================
// Error types
// ============================================================================

#[derive(Debug, thiserror::Error)]
pub enum BlobStoreError {
    #[error("blob not found: {0}/{1}")]
    NotFound(String, String),
    #[error("storage error: {0}")]
    Storage(String),
    #[error("encryption error: {0}")]
    Encryption(String),
}

pub type BlobStoreResult<T> = Result<T, BlobStoreError>;

// ============================================================================
// BlobMetadata
// ============================================================================

#[derive(Debug, Serialize)]
pub struct BlobMetadata {
    pub namespace: String,
    pub blob_id: String,
    pub size: i64,
    pub content_hash: Option<String>,
    pub metadata_json: Option<String>,
    pub created_at: i64,
    pub modified_at: i64,
}

// ============================================================================
// BlobStore
// ============================================================================

pub struct BlobStore {
    conn: Arc<Mutex<Connection>>,
    encryptor: Arc<dyn DataEncryptor>,
}

impl BlobStore {
    /// Open a blob store backed by a DuckDB file (no encryption).
    pub fn open(db_path: &Path) -> BlobStoreResult<Self> {
        let conn = if db_path.to_str() == Some(":memory:") {
            Connection::open_in_memory()
        } else {
            Connection::open(db_path)
        }
        .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        // Cap memory/threads — DuckDB defaults to ~80% RAM per connection
        if db_path.to_str() != Some(":memory:") {
            conn.execute_batch("PRAGMA memory_limit='128MB'; PRAGMA threads=1;")
                .map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        }

        let store = Self {
            conn: Arc::new(Mutex::new(conn)),
            encryptor: Arc::new(privstack_crypto::PassthroughEncryptor),
        };
        store.ensure_tables()?;
        Ok(store)
    }

    /// Open with an existing shared connection (no encryption).
    pub fn open_with_conn(conn: Arc<Mutex<Connection>>) -> BlobStoreResult<Self> {
        let store = Self {
            conn,
            encryptor: Arc::new(privstack_crypto::PassthroughEncryptor),
        };
        store.ensure_tables()?;
        Ok(store)
    }

    /// Open in-memory (no encryption).
    pub fn open_in_memory() -> BlobStoreResult<Self> {
        let conn =
            Connection::open_in_memory().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let store = Self {
            conn: Arc::new(Mutex::new(conn)),
            encryptor: Arc::new(privstack_crypto::PassthroughEncryptor),
        };
        store.ensure_tables()?;
        Ok(store)
    }

    /// Open a blob store with an encryptor for at-rest encryption.
    pub fn open_with_encryptor(
        db_path: &Path,
        encryptor: Arc<dyn DataEncryptor>,
    ) -> BlobStoreResult<Self> {
        let conn = if db_path.to_str() == Some(":memory:") {
            Connection::open_in_memory()
        } else {
            Connection::open(db_path)
        }
        .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        // Cap memory/threads — DuckDB defaults to ~80% RAM per connection
        if db_path.to_str() != Some(":memory:") {
            conn.execute_batch("PRAGMA memory_limit='128MB'; PRAGMA threads=1;")
                .map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        }

        let store = Self {
            conn: Arc::new(Mutex::new(conn)),
            encryptor,
        };
        store.ensure_tables()?;
        Ok(store)
    }

    fn ensure_tables(&self) -> BlobStoreResult<()> {
        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS blobs (
                namespace VARCHAR NOT NULL,
                blob_id VARCHAR NOT NULL,
                data BLOB NOT NULL,
                size BIGINT NOT NULL DEFAULT 0,
                content_hash VARCHAR,
                metadata_json VARCHAR,
                created_at BIGINT NOT NULL,
                modified_at BIGINT NOT NULL,
                PRIMARY KEY (namespace, blob_id)
            );",
        )
        .map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        Ok(())
    }

    /// Entity ID for per-blob encryption keys.
    fn blob_entity_id(namespace: &str, id: &str) -> String {
        format!("{namespace}/{id}")
    }

    /// Store a blob (encrypts data if encryptor is available).
    pub fn store(
        &self,
        namespace: &str,
        id: &str,
        data: &[u8],
        metadata_json: Option<&str>,
    ) -> BlobStoreResult<()> {
        // Hash plaintext before encryption for dedup
        let content_hash = hex_encode(Sha256::digest(data));
        let now = Utc::now().timestamp_millis();

        let stored_data = if self.encryptor.is_available() {
            let entity_id = Self::blob_entity_id(namespace, id);
            self.encryptor
                .encrypt_bytes(&entity_id, data)
                .map_err(|e| BlobStoreError::Encryption(e.to_string()))?
        } else {
            data.to_vec()
        };

        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        conn.execute(
            "INSERT OR REPLACE INTO blobs (namespace, blob_id, data, size, content_hash, metadata_json, created_at, modified_at)
             VALUES (?, ?, ?, ?, ?, ?, COALESCE((SELECT created_at FROM blobs WHERE namespace = ? AND blob_id = ?), ?), ?)",
            params![namespace, id, stored_data, data.len() as i64, content_hash, metadata_json, namespace, id, now, now],
        )
        .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        Ok(())
    }

    /// Read a blob (decrypts if encrypted).
    pub fn read(&self, namespace: &str, id: &str) -> BlobStoreResult<Vec<u8>> {
        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let raw: Vec<u8> = conn
            .query_row(
                "SELECT data FROM blobs WHERE namespace = ? AND blob_id = ?",
                params![namespace, id],
                |row| row.get(0),
            )
            .map_err(|_| BlobStoreError::NotFound(namespace.to_string(), id.to_string()))?;

        drop(conn);

        // Try decryption; if it fails (passthrough data), return raw
        if self.encryptor.is_available() {
            match self.encryptor.decrypt_bytes(&raw) {
                Ok(plaintext) => Ok(plaintext),
                Err(_) => Ok(raw), // Legacy unencrypted blob
            }
        } else {
            Ok(raw)
        }
    }

    /// Delete a blob.
    pub fn delete(&self, namespace: &str, id: &str) -> BlobStoreResult<()> {
        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let affected = conn
            .execute(
                "DELETE FROM blobs WHERE namespace = ? AND blob_id = ?",
                params![namespace, id],
            )
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        if affected == 0 {
            return Err(BlobStoreError::NotFound(
                namespace.to_string(),
                id.to_string(),
            ));
        }
        Ok(())
    }

    /// List blob metadata for a namespace.
    pub fn list(&self, namespace: &str) -> BlobStoreResult<Vec<BlobMetadata>> {
        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let mut stmt = conn
            .prepare(
                "SELECT namespace, blob_id, size, content_hash, metadata_json, created_at, modified_at
                 FROM blobs WHERE namespace = ? ORDER BY modified_at DESC",
            )
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        let items: Vec<BlobMetadata> = stmt
            .query_map(params![namespace], |row| {
                Ok(BlobMetadata {
                    namespace: row.get(0)?,
                    blob_id: row.get(1)?,
                    size: row.get(2)?,
                    content_hash: row.get(3)?,
                    metadata_json: row.get(4)?,
                    created_at: row.get(5)?,
                    modified_at: row.get(6)?,
                })
            })
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?
            .filter_map(|r| r.ok())
            .collect();

        Ok(items)
    }

    /// Update metadata for a blob.
    pub fn update_metadata(
        &self,
        namespace: &str,
        id: &str,
        metadata_json: &str,
    ) -> BlobStoreResult<()> {
        let now = Utc::now().timestamp_millis();
        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let affected = conn
            .execute(
                "UPDATE blobs SET metadata_json = ?, modified_at = ? WHERE namespace = ? AND blob_id = ?",
                params![metadata_json, now, namespace, id],
            )
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        if affected == 0 {
            return Err(BlobStoreError::NotFound(
                namespace.to_string(),
                id.to_string(),
            ));
        }
        Ok(())
    }

    /// Re-encrypt all blobs after a password change.
    pub fn re_encrypt_all(
        &self,
        old_key_bytes: &[u8],
        new_key_bytes: &[u8],
    ) -> BlobStoreResult<usize> {
        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let mut stmt = conn
            .prepare("SELECT namespace, blob_id, data FROM blobs")
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        let rows: Vec<(String, String, Vec<u8>)> = stmt
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);

        let mut count = 0usize;
        for (namespace, blob_id, raw) in &rows {
            // Try to re-encrypt; skip if it fails (likely unencrypted legacy data)
            match self
                .encryptor
                .reencrypt_bytes(raw, old_key_bytes, new_key_bytes)
            {
                Ok(re) => {
                    conn.execute(
                        "UPDATE blobs SET data = ? WHERE namespace = ? AND blob_id = ?",
                        params![re, namespace, blob_id],
                    )
                    .map_err(|e| BlobStoreError::Storage(e.to_string()))?;
                    count += 1;
                }
                Err(_) => continue, // Unencrypted blob, skip
            }
        }
        Ok(count)
    }

    /// Migrate unencrypted blobs: encrypts any blob whose data isn't already encrypted.
    pub fn migrate_unencrypted(&self) -> BlobStoreResult<usize> {
        if !self.encryptor.is_available() {
            return Err(BlobStoreError::Encryption(
                "encryptor unavailable — vault must be unlocked before migration".into(),
            ));
        }

        let conn = self.conn.lock().map_err(|e| BlobStoreError::Storage(e.to_string()))?;
        let mut stmt = conn
            .prepare("SELECT namespace, blob_id, data FROM blobs")
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?;

        let rows: Vec<(String, String, Vec<u8>)> = stmt
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);

        let mut migrated = 0usize;
        for (namespace, blob_id, raw) in &rows {
            // If decryption succeeds, it's already encrypted — skip
            if self.encryptor.decrypt_bytes(raw).is_ok() {
                continue;
            }
            // Raw unencrypted data — encrypt it
            let entity_id = Self::blob_entity_id(namespace, blob_id);
            let encrypted = self
                .encryptor
                .encrypt_bytes(&entity_id, raw)
                .map_err(|e| BlobStoreError::Encryption(e.to_string()))?;
            conn.execute(
                "UPDATE blobs SET data = ? WHERE namespace = ? AND blob_id = ?",
                params![encrypted, namespace, blob_id],
            )
            .map_err(|e| BlobStoreError::Storage(e.to_string()))?;
            migrated += 1;
        }
        Ok(migrated)
    }
}

fn hex_encode(bytes: impl AsRef<[u8]>) -> String {
    bytes
        .as_ref()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}
