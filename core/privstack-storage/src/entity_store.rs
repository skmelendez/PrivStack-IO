//! Generic entity store — stores any entity type as JSON with indexed fields.
//!
//! When an encryptor is provided, `data_json` is stored as encrypted ciphertext.
//! Indexed columns (title, body, tags, search_text, booleans) remain plaintext
//! so that search/filter queries continue to work without decryption.

use crate::error::{StorageError, StorageResult};
use duckdb::{params, Connection};
use privstack_crypto::DataEncryptor;
use privstack_model::{Entity, EntitySchema, FieldType, IndexedField};
use std::path::Path;
use std::sync::{Arc, Mutex};

/// Generic entity store backed by DuckDB.
///
/// Stores entities of any type in a single `entities` table with
/// schema-driven field extraction for indexing and search.
#[derive(Clone)]
pub struct EntityStore {
    conn: Arc<Mutex<Connection>>,
    encryptor: Arc<dyn DataEncryptor>,
}

impl EntityStore {
    /// Opens or creates an entity store at the given path (no encryption).
    pub fn open(path: &Path) -> StorageResult<Self> {
        let conn = crate::open_duckdb_with_wal_recovery(path, "256MB", 2)?;
        initialize_entity_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
            encryptor: Arc::new(privstack_crypto::PassthroughEncryptor),
        })
    }

    /// Opens an in-memory entity store (for testing).
    pub fn open_in_memory() -> StorageResult<Self> {
        let conn = Connection::open_in_memory()?;
        initialize_entity_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
            encryptor: Arc::new(privstack_crypto::PassthroughEncryptor),
        })
    }

    /// Opens an entity store with an encryptor for at-rest encryption of `data_json`.
    pub fn open_with_encryptor(
        path: &Path,
        encryptor: Arc<dyn DataEncryptor>,
    ) -> StorageResult<Self> {
        let conn = crate::open_duckdb_with_wal_recovery(path, "256MB", 2)?;
        initialize_entity_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
            encryptor,
        })
    }

    /// Encrypt raw JSON bytes through the encryptor, returning a base64 string
    /// suitable for storage in the TEXT column.
    fn encrypt_data_json(&self, entity_id: &str, json_bytes: &[u8]) -> StorageResult<String> {
        if !self.encryptor.is_available() {
            // Encryptor not ready — store plaintext (pre-unlock state)
            return Ok(String::from_utf8_lossy(json_bytes).into_owned());
        }
        let ciphertext = self
            .encryptor
            .encrypt_bytes(entity_id, json_bytes)
            .map_err(|e| StorageError::Encryption(e.to_string()))?;
        Ok(base64_encode(&ciphertext))
    }

    /// Decrypt `data_json` from the database.  Handles both encrypted (base64)
    /// and legacy plaintext rows transparently.
    fn decrypt_data_json(&self, raw: &str) -> StorageResult<serde_json::Value> {
        // Fast path: if it parses as JSON directly, it's unencrypted (legacy or passthrough)
        if let Ok(val) = serde_json::from_str::<serde_json::Value>(raw) {
            return Ok(val);
        }
        // Otherwise treat as base64-encoded ciphertext
        let ciphertext = base64_decode(raw)
            .map_err(|e| StorageError::Encryption(format!("base64 decode: {e}")))?;
        let plaintext = self
            .encryptor
            .decrypt_bytes(&ciphertext)
            .map_err(|e| StorageError::Encryption(e.to_string()))?;
        let val: serde_json::Value = serde_json::from_slice(&plaintext)?;
        Ok(val)
    }

    /// Save (upsert) an entity with schema-driven field extraction.
    pub fn save_entity(&self, entity: &Entity, schema: &EntitySchema) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        let data_json_raw = serde_json::to_vec(&entity.data)?;

        let title = extract_field(&entity.data, &schema.indexed_fields, FieldType::Text, "/title");
        let body = extract_field(&entity.data, &schema.indexed_fields, FieldType::Text, "/body");
        let tags = extract_tags(&entity.data, &schema.indexed_fields);
        let tags_str: Option<String> = if tags.is_empty() {
            None
        } else {
            // DuckDB array literal
            Some(format!("[{}]", tags.iter().map(|t| format!("'{}'", t.replace('\'', "''"))).collect::<Vec<_>>().join(",")))
        };

        let search_text = build_search_text(&title, &body, &tags);

        // Auto-index Relation fields as entity_links
        extract_relations(&conn, entity, &schema.indexed_fields)?;

        // Auto-index Vector fields into entity_vectors
        extract_vectors(&conn, entity, &schema.indexed_fields)?;

        // Encrypt data_json (encryptor decides whether to actually encrypt)
        // Must drop conn before calling encrypt_data_json since it doesn't need conn
        drop(conn);
        let data_json = self.encrypt_data_json(&entity.id, &data_json_raw)?;
        let conn = self.conn.lock().unwrap();

        conn.execute(
            r#"
            INSERT OR REPLACE INTO entities (
                id, entity_type, data_json, title, body, tags,
                is_trashed, is_favorite, local_only,
                created_at, modified_at, created_by, search_text
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            "#,
            params![
                entity.id,
                entity.entity_type,
                data_json,
                title.as_deref(),
                body.as_deref(),
                tags_str.as_deref(),
                entity.data.pointer("/is_trashed").and_then(|v| v.as_bool()).unwrap_or(false),
                entity.data.pointer("/is_favorite").and_then(|v| v.as_bool()).unwrap_or(false),
                entity.data.pointer("/local_only").and_then(|v| v.as_bool()).unwrap_or(false),
                entity.created_at,
                entity.modified_at,
                entity.created_by,
                search_text,
            ],
        )?;

        Ok(())
    }

    /// Save an entity without a schema (no field extraction, just raw JSON).
    pub fn save_entity_raw(&self, entity: &Entity) -> StorageResult<()> {
        let data_json_raw = serde_json::to_vec(&entity.data)?;
        let data_json = self.encrypt_data_json(&entity.id, &data_json_raw)?;

        let conn = self.conn.lock().unwrap();
        conn.execute(
            r#"
            INSERT OR REPLACE INTO entities (
                id, entity_type, data_json,
                is_trashed, is_favorite, local_only,
                created_at, modified_at, created_by
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            "#,
            params![
                entity.id,
                entity.entity_type,
                data_json,
                entity.data.pointer("/is_trashed").and_then(|v| v.as_bool()).unwrap_or(false),
                entity.data.pointer("/is_favorite").and_then(|v| v.as_bool()).unwrap_or(false),
                entity.data.pointer("/local_only").and_then(|v| v.as_bool()).unwrap_or(false),
                entity.created_at,
                entity.modified_at,
                entity.created_by,
            ],
        )?;

        Ok(())
    }

    /// Get a single entity by ID.
    pub fn get_entity(&self, id: &str) -> StorageResult<Option<Entity>> {
        let conn = self.conn.lock().unwrap();

        let result = conn.query_row(
            "SELECT id, entity_type, data_json, created_at, modified_at, created_by, is_trashed FROM entities WHERE id = ?",
            params![id],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, bool>(6)?,
                ))
            },
        );

        match result {
            Ok((id, entity_type, data_json, created_at, modified_at, created_by, is_trashed)) => {
                drop(conn);
                let mut data = self.decrypt_data_json(&data_json)?;
                // Patch is_trashed from the authoritative DB column
                if let Some(obj) = data.as_object_mut() {
                    obj.insert("is_trashed".into(), serde_json::Value::Bool(is_trashed));
                }
                Ok(Some(Entity {
                    id,
                    entity_type,
                    data,
                    created_at,
                    modified_at,
                    created_by,
                }))
            }
            Err(duckdb::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    /// List entities of a given type, ordered by modified_at DESC.
    pub fn list_entities(
        &self,
        entity_type: &str,
        include_trashed: bool,
        limit: Option<usize>,
        offset: Option<usize>,
    ) -> StorageResult<Vec<Entity>> {
        let conn = self.conn.lock().unwrap();

        let mut sql = String::from(
            "SELECT id, entity_type, data_json, created_at, modified_at, created_by, is_trashed FROM entities WHERE entity_type = ?"
        );
        if !include_trashed {
            sql.push_str(" AND is_trashed = FALSE");
        }
        sql.push_str(" ORDER BY modified_at DESC");
        if let Some(lim) = limit {
            sql.push_str(&format!(" LIMIT {lim}"));
        }
        if let Some(off) = offset {
            sql.push_str(&format!(" OFFSET {off}"));
        }

        let mut stmt = conn.prepare(&sql)?;
        let rows: Vec<(String, String, String, i64, i64, String, bool)> = stmt
            .query_map(params![entity_type], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, bool>(6)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .collect();

        drop(stmt);
        drop(conn);

        let mut entities = Vec::with_capacity(rows.len());
        for (id, entity_type, data_json, created_at, modified_at, created_by, is_trashed) in rows {
            if let Ok(mut data) = self.decrypt_data_json(&data_json) {
                // Patch is_trashed from the authoritative DB column (trash_entity
                // only updates the column, not data_json)
                if let Some(obj) = data.as_object_mut() {
                    obj.insert("is_trashed".into(), serde_json::Value::Bool(is_trashed));
                }
                entities.push(Entity { id, entity_type, data, created_at, modified_at, created_by });
            }
        }
        Ok(entities)
    }

    /// Delete an entity by ID, cascading to all referencing tables.
    pub fn delete_entity(&self, id: &str) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "DELETE FROM entity_links WHERE source_id = ? OR target_id = ?",
            params![id, id],
        )?;
        conn.execute("DELETE FROM entity_vectors WHERE entity_id = ?", params![id])?;
        conn.execute("DELETE FROM sync_ledger WHERE entity_id = ?", params![id])?;
        conn.execute("DELETE FROM entities WHERE id = ?", params![id])?;
        Ok(())
    }

    /// Soft-delete (trash) an entity.
    pub fn trash_entity(&self, id: &str) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("UPDATE entities SET is_trashed = TRUE WHERE id = ?", params![id])?;
        Ok(())
    }

    /// Restore a trashed entity.
    pub fn restore_entity(&self, id: &str) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("UPDATE entities SET is_trashed = FALSE WHERE id = ?", params![id])?;
        Ok(())
    }

    /// Query entities by field filters applied against decrypted JSON data.
    ///
    /// Because `data_json` is encrypted at rest, SQL-level JSON extraction
    /// cannot operate on ciphertext.  Instead we fetch all rows for the
    /// entity type, decrypt each one, and apply the field filters in Rust.
    pub fn query_entities(
        &self,
        entity_type: &str,
        filters: &[(String, serde_json::Value)],
        include_trashed: bool,
        limit: Option<usize>,
    ) -> StorageResult<Vec<Entity>> {
        // No filters → delegate to list_entities (avoids duplicated SQL)
        if filters.is_empty() {
            return self.list_entities(entity_type, include_trashed, limit, None);
        }

        let conn = self.conn.lock().unwrap();

        let mut sql = String::from(
            "SELECT id, entity_type, data_json, created_at, modified_at, created_by, is_trashed \
             FROM entities WHERE entity_type = ?"
        );
        if !include_trashed {
            sql.push_str(" AND is_trashed = FALSE");
        }
        let sql = sql + " ORDER BY modified_at DESC";

        let mut stmt = conn.prepare(&sql)?;
        let rows: Vec<(String, String, String, i64, i64, String, bool)> = stmt
            .query_map(params![entity_type], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, bool>(6)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .collect();

        drop(stmt);
        drop(conn);

        let mut entities = Vec::new();
        for (id, entity_type, data_json, created_at, modified_at, created_by, is_trashed) in rows {
            if let Ok(mut data) = self.decrypt_data_json(&data_json) {
                // Patch is_trashed from the authoritative DB column
                if let Some(obj) = data.as_object_mut() {
                    obj.insert("is_trashed".into(), serde_json::Value::Bool(is_trashed));
                }

                // Apply every filter against the decrypted JSON
                let matches = filters.iter().all(|(field_path, expected)| {
                    let pointer = if field_path.starts_with('/') {
                        field_path.clone()
                    } else {
                        format!("/{field_path}")
                    };
                    match data.pointer(&pointer) {
                        Some(actual) => match_filter_value(actual, expected),
                        None => false,
                    }
                });

                if matches {
                    entities.push(Entity { id, entity_type, data, created_at, modified_at, created_by });
                    if let Some(lim) = limit {
                        if entities.len() >= lim {
                            break;
                        }
                    }
                }
            }
        }
        Ok(entities)
    }

    /// Search entities across all types (or a subset) using text matching.
    /// Searches against plaintext indexed columns (title, search_text).
    pub fn search(
        &self,
        query: &str,
        entity_types: Option<&[&str]>,
        limit: usize,
    ) -> StorageResult<Vec<Entity>> {
        let conn = self.conn.lock().unwrap();

        let pattern = format!("%{query}%");
        let mut sql = String::from(
            "SELECT id, entity_type, data_json, created_at, modified_at, created_by FROM entities WHERE is_trashed = FALSE AND (LOWER(search_text) LIKE LOWER(?) OR LOWER(title) LIKE LOWER(?))"
        );

        if let Some(types) = entity_types {
            if !types.is_empty() {
                let in_clause = types.iter().map(|t| format!("'{}'", t.replace('\'', "''"))).collect::<Vec<_>>().join(",");
                sql.push_str(&format!(" AND entity_type IN ({in_clause})"));
            }
        }

        sql.push_str(&format!(" ORDER BY modified_at DESC LIMIT {limit}"));

        let mut stmt = conn.prepare(&sql)?;
        let rows: Vec<(String, String, String, i64, i64, String)> = stmt
            .query_map(params![pattern, pattern], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, String>(5)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .collect();

        drop(stmt);
        drop(conn);

        let mut entities = Vec::with_capacity(rows.len());
        for (id, entity_type, data_json, created_at, modified_at, created_by) in rows {
            if let Ok(data) = self.decrypt_data_json(&data_json) {
                entities.push(Entity { id, entity_type, data, created_at, modified_at, created_by });
            }
        }
        Ok(entities)
    }

    /// Returns all entity IDs in the store (non-trashed).
    /// Used by the sync orchestrator on first sync with a new peer.
    pub fn list_all_entity_ids(&self) -> StorageResult<Vec<String>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare("SELECT id FROM entities WHERE is_trashed = FALSE")?;
        let ids: Vec<String> = stmt
            .query_map([], |row| row.get::<_, String>(0))?
            .filter_map(|r| r.ok())
            .collect();
        Ok(ids)
    }

    // ── Sync Ledger ──────────────────────────────────────────────

    /// Returns entity IDs that need syncing with a specific peer.
    /// An entity needs syncing if:
    ///   1. It has no ledger entry for this peer (never synced), OR
    ///   2. It was modified after the last sync with this peer.
    pub fn entities_needing_sync(&self, peer_id: &str) -> StorageResult<Vec<String>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT e.id FROM entities e \
             LEFT JOIN sync_ledger sl ON e.id = sl.entity_id AND sl.peer_id = ? \
             WHERE e.is_trashed = FALSE \
               AND e.local_only = FALSE \
               AND (sl.entity_id IS NULL OR e.modified_at > sl.synced_at) \
             ORDER BY e.modified_at ASC"
        )?;
        let ids: Vec<String> = stmt
            .query_map(params![peer_id], |row| row.get::<_, String>(0))?
            .filter_map(|r| r.ok())
            .collect();
        Ok(ids)
    }

    /// Marks a single entity as synced with a peer (upsert).
    pub fn mark_entity_synced(&self, peer_id: &str, entity_id: &str, synced_at_ms: i64) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT OR REPLACE INTO sync_ledger (peer_id, entity_id, synced_at) VALUES (?, ?, ?)",
            params![peer_id, entity_id, synced_at_ms],
        )?;
        Ok(())
    }

    /// Marks multiple entities as synced with a peer in a single transaction.
    pub fn mark_entities_synced(&self, peer_id: &str, entity_ids: &[String], synced_at_ms: i64) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "INSERT OR REPLACE INTO sync_ledger (peer_id, entity_id, synced_at) VALUES (?, ?, ?)"
        )?;
        for eid in entity_ids {
            stmt.execute(params![peer_id, eid, synced_at_ms])?;
        }
        Ok(())
    }

    /// Removes all sync ledger entries for a peer (e.g., when untrusting).
    pub fn clear_sync_ledger_for_peer(&self, peer_id: &str) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM sync_ledger WHERE peer_id = ?", params![peer_id])?;
        Ok(())
    }

    /// Removes all sync ledger entries for an entity across all peers.
    /// Forces the entity to be re-synced with every peer on the next cycle.
    pub fn invalidate_sync_ledger_for_entity(&self, entity_id: &str) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM sync_ledger WHERE entity_id = ?", params![entity_id])?;
        Ok(())
    }

    /// Count entities of a given type.
    pub fn count_entities(&self, entity_type: &str, include_trashed: bool) -> StorageResult<usize> {
        let conn = self.conn.lock().unwrap();

        let sql = if include_trashed {
            "SELECT COUNT(*) FROM entities WHERE entity_type = ?"
        } else {
            "SELECT COUNT(*) FROM entities WHERE entity_type = ? AND is_trashed = FALSE"
        };

        let count: i64 = conn.query_row(sql, params![entity_type], |row| row.get(0))?;
        Ok(count as usize)
    }

    /// Estimate storage bytes used by entities of a given type.
    /// Returns the sum of data_json column lengths for the specified entity type.
    pub fn estimate_storage_bytes(&self, entity_type: &str) -> StorageResult<usize> {
        let conn = self.conn.lock().unwrap();
        let sql = "SELECT COALESCE(SUM(LENGTH(data_json)), 0) FROM entities WHERE entity_type = ?";
        let bytes: i64 = conn.query_row(sql, params![entity_type], |row| row.get(0))?;
        Ok(bytes as usize)
    }

    /// Estimate storage bytes for multiple entity types at once.
    /// Returns a Vec of (entity_type, count, bytes) tuples.
    pub fn estimate_storage_by_types(&self, entity_types: &[&str]) -> StorageResult<Vec<(String, usize, usize)>> {
        let conn = self.conn.lock().unwrap();
        let mut results = Vec::with_capacity(entity_types.len());

        for entity_type in entity_types {
            let sql = "SELECT COUNT(*), COALESCE(SUM(LENGTH(data_json)), 0) FROM entities WHERE entity_type = ?";
            let (count, bytes): (i64, i64) = conn.query_row(sql, params![*entity_type], |row| {
                Ok((row.get(0)?, row.get(1)?))
            })?;
            results.push((entity_type.to_string(), count as usize, bytes as usize));
        }

        Ok(results)
    }

    /// Save a link between two entities.
    pub fn save_link(
        &self,
        source_type: &str,
        source_id: &str,
        target_type: &str,
        target_id: &str,
    ) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT OR IGNORE INTO entity_links (source_type, source_id, target_type, target_id) VALUES (?, ?, ?, ?)",
            params![source_type, source_id, target_type, target_id],
        )?;
        Ok(())
    }

    /// Remove a link between two entities.
    pub fn remove_link(
        &self,
        source_type: &str,
        source_id: &str,
        target_type: &str,
        target_id: &str,
    ) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "DELETE FROM entity_links WHERE source_type = ? AND source_id = ? AND target_type = ? AND target_id = ?",
            params![source_type, source_id, target_type, target_id],
        )?;
        Ok(())
    }

    /// Get all entities linked from a source entity.
    pub fn get_links_from(
        &self,
        source_type: &str,
        source_id: &str,
    ) -> StorageResult<Vec<(String, String)>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT target_type, target_id FROM entity_links WHERE source_type = ? AND source_id = ?"
        )?;
        let links = stmt
            .query_map(params![source_type, source_id], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })?
            .filter_map(|r| r.ok())
            .collect();
        Ok(links)
    }

    /// Get all entities linking to a target entity.
    pub fn get_links_to(
        &self,
        target_type: &str,
        target_id: &str,
    ) -> StorageResult<Vec<(String, String)>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT source_type, source_id FROM entity_links WHERE target_type = ? AND target_id = ?"
        )?;
        let links = stmt
            .query_map(params![target_type, target_id], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })?
            .filter_map(|r| r.ok())
            .collect();
        Ok(links)
    }

    /// Migrate unencrypted rows: detects plaintext `data_json` and encrypts them.
    /// Idempotent — already-encrypted rows (non-JSON base64) are skipped.
    pub fn migrate_unencrypted(&self) -> StorageResult<usize> {
        if !self.encryptor.is_available() {
            return Err(StorageError::Encryption(
                "encryptor unavailable — vault must be unlocked before migration".into(),
            ));
        }

        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, data_json FROM entities"
        )?;
        let rows: Vec<(String, String)> = stmt
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);
        drop(conn);

        let mut migrated = 0usize;
        for (id, raw) in &rows {
            // If it parses as valid JSON, it's unencrypted — encrypt it
            if serde_json::from_str::<serde_json::Value>(raw).is_ok() {
                let encrypted = self.encrypt_data_json(id, raw.as_bytes())?;
                // Only update if encryption actually changed the value
                if encrypted != *raw {
                    let conn = self.conn.lock().unwrap();
                    conn.execute(
                        "UPDATE entities SET data_json = ? WHERE id = ?",
                        params![encrypted, id],
                    )?;
                    drop(conn);
                    migrated += 1;
                }
            }
        }
        Ok(migrated)
    }

    /// Re-encrypt all rows with new key material after a password change.
    /// The encryptor's `reencrypt_bytes` re-wraps per-entity keys without
    /// touching content — O(n) rows but only ~100 bytes of crypto per row.
    pub fn re_encrypt_all(
        &self,
        old_key_bytes: &[u8],
        new_key_bytes: &[u8],
    ) -> StorageResult<usize> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare("SELECT id, data_json FROM entities")?;
        let rows: Vec<(String, String)> = stmt
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);

        let mut count = 0usize;
        for (id, raw) in &rows {
            // Skip plaintext rows (they haven't been encrypted yet)
            if serde_json::from_str::<serde_json::Value>(raw).is_ok() {
                continue;
            }
            let ciphertext = base64_decode(raw)
                .map_err(|e| StorageError::Encryption(format!("base64 decode: {e}")))?;
            let re = self
                .encryptor
                .reencrypt_bytes(&ciphertext, old_key_bytes, new_key_bytes)
                .map_err(|e| StorageError::Encryption(e.to_string()))?;
            let encoded = base64_encode(&re);
            conn.execute(
                "UPDATE entities SET data_json = ? WHERE id = ?",
                params![encoded, id],
            )?;
            count += 1;
        }
        Ok(count)
    }

    // =========================================================================
    // Plugin Fuel History Methods
    // =========================================================================

    /// Records a fuel consumption entry for a plugin.
    /// Maintains a rolling window of the last 1000 entries per plugin.
    pub fn record_fuel_consumption(&self, plugin_id: &str, fuel_consumed: u64) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(0);

        // Insert new record
        conn.execute(
            "INSERT INTO plugin_fuel_history (plugin_id, fuel_consumed, recorded_at) VALUES (?, ?, ?)",
            params![plugin_id, fuel_consumed as i64, now],
        )?;

        // Prune old records - keep only the most recent 1000 per plugin
        // First get the count, then delete if over limit
        let count: i64 = conn.query_row(
            "SELECT COUNT(*) FROM plugin_fuel_history WHERE plugin_id = ?",
            params![plugin_id],
            |row| row.get(0),
        )?;

        if count > 1000 {
            // Delete oldest records beyond the 1000 limit
            conn.execute(
                r#"
                DELETE FROM plugin_fuel_history
                WHERE plugin_id = ?
                  AND recorded_at < (
                    SELECT MIN(recorded_at) FROM (
                      SELECT recorded_at FROM plugin_fuel_history
                      WHERE plugin_id = ?
                      ORDER BY recorded_at DESC
                      LIMIT 1000
                    )
                  )
                "#,
                params![plugin_id, plugin_id],
            )?;
        }

        Ok(())
    }

    /// Gets fuel consumption metrics for a plugin.
    /// Returns (average, peak, count) for the stored history.
    pub fn get_fuel_metrics(&self, plugin_id: &str) -> StorageResult<(u64, u64, usize)> {
        let conn = self.conn.lock().unwrap();

        let mut stmt = conn.prepare(
            r#"
            SELECT
                COALESCE(AVG(fuel_consumed), 0) as avg_fuel,
                COALESCE(MAX(fuel_consumed), 0) as peak_fuel,
                COUNT(*) as call_count
            FROM plugin_fuel_history
            WHERE plugin_id = ?
            "#,
        )?;

        let (avg_fuel, peak_fuel, call_count): (f64, i64, i64) = stmt.query_row(params![plugin_id], |row| {
            Ok((row.get(0)?, row.get(1)?, row.get(2)?))
        })?;

        Ok((avg_fuel as u64, peak_fuel as u64, call_count as usize))
    }

    /// Clears all fuel history for a plugin (e.g., on reset).
    pub fn clear_fuel_history(&self, plugin_id: &str) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "DELETE FROM plugin_fuel_history WHERE plugin_id = ?",
            params![plugin_id],
        )?;
        Ok(())
    }

    // ── Cloud Sync Cursor Persistence ──

    /// Saves a cloud sync cursor value (e.g. per-entity cursor position or last_sync_at).
    pub fn save_cloud_cursor(&self, key: &str, value: i64) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(0);
        conn.execute(
            "INSERT OR REPLACE INTO cloud_sync_cursors (cursor_key, cursor_value, updated_at) VALUES (?, ?, ?)",
            params![key, value, now],
        )?;
        Ok(())
    }

    /// Loads all cloud sync cursors as key-value pairs.
    pub fn load_cloud_cursors(&self) -> StorageResult<Vec<(String, i64)>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT cursor_key, cursor_value FROM cloud_sync_cursors"
        )?;
        let rows: Vec<(String, i64)> = stmt
            .query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
            })?
            .filter_map(|r| r.ok())
            .collect();
        Ok(rows)
    }

    /// Clears all cloud sync cursors (e.g. when switching workspaces).
    pub fn clear_cloud_cursors(&self) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM cloud_sync_cursors", [])?;
        Ok(())
    }

    /// Runs database maintenance: purge orphaned/transient data, then checkpoint to reclaim space.
    /// Only cleans auxiliary tables — never touches real entity data.
    /// Note: DuckDB's VACUUM does NOT reclaim space. CHECKPOINT is the correct approach.
    pub fn run_maintenance(&self) -> StorageResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute_batch(
            "-- Orphaned rows in auxiliary tables (parent entity deleted but these weren't)
             DELETE FROM entity_vectors WHERE entity_id NOT IN (SELECT id FROM entities);
             DELETE FROM sync_ledger WHERE entity_id NOT IN (SELECT id FROM entities);
             DELETE FROM entity_links WHERE source_id NOT IN (SELECT id FROM entities)
                OR target_id NOT IN (SELECT id FROM entities);
             -- Transient data that rebuilds automatically on next sync
             DELETE FROM cloud_sync_cursors;
             DELETE FROM plugin_fuel_history;
             CHECKPOINT;"
        )?;
        Ok(())
    }

    /// Finds orphan entities whose (created_by, entity_type) don't match any known
    /// plugin schema. Returns JSON array of orphan summaries.
    /// `valid_types` is a list of (plugin_id, entity_type) pairs from registered plugins.
    pub fn find_orphan_entities(
        &self,
        valid_types: &[(String, String)],
    ) -> StorageResult<Vec<serde_json::Value>> {
        let conn = self.conn.lock().unwrap();

        // Get all distinct (created_by, entity_type) combos in the DB
        let mut stmt = conn.prepare(
            "SELECT created_by, entity_type, COUNT(*) as cnt
             FROM entities
             GROUP BY created_by, entity_type"
        )?;

        let db_types: Vec<(String, String, i64)> = stmt
            .query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .collect();

        let mut orphans = Vec::new();
        for (plugin_id, entity_type, count) in &db_types {
            let is_known = valid_types.iter().any(|(pid, etype)| {
                pid == plugin_id && etype == entity_type
            });
            if !is_known {
                orphans.push(serde_json::json!({
                    "plugin_id": plugin_id,
                    "entity_type": entity_type,
                    "count": count,
                }));
            }
        }
        Ok(orphans)
    }

    /// Deletes orphan entities whose (created_by, entity_type) don't match any known
    /// plugin schema. Also cascades to auxiliary tables. Returns count of deleted entities.
    pub fn delete_orphan_entities(
        &self,
        valid_types: &[(String, String)],
    ) -> StorageResult<usize> {
        let conn = self.conn.lock().unwrap();

        // Build WHERE clause to exclude known types
        if valid_types.is_empty() {
            return Ok(0);
        }

        // Find orphan IDs first
        let mut conditions = Vec::new();
        for (pid, etype) in valid_types {
            conditions.push(format!(
                "(created_by = '{}' AND entity_type = '{}')",
                pid.replace('\'', "''"),
                etype.replace('\'', "''"),
            ));
        }
        let where_known = conditions.join(" OR ");

        let query = format!(
            "SELECT id FROM entities WHERE NOT ({})",
            where_known
        );
        let mut stmt = conn.prepare(&query)?;
        let orphan_ids: Vec<String> = stmt
            .query_map([], |row| row.get::<_, String>(0))?
            .filter_map(|r| r.ok())
            .collect();

        if orphan_ids.is_empty() {
            return Ok(0);
        }

        // Delete from auxiliary tables first, then entities
        let id_list: Vec<String> = orphan_ids.iter().map(|id| {
            format!("'{}'", id.replace('\'', "''"))
        }).collect();
        let in_clause = id_list.join(",");

        conn.execute_batch(&format!(
            "DELETE FROM entity_vectors WHERE entity_id IN ({in_clause});
             DELETE FROM sync_ledger WHERE entity_id IN ({in_clause});
             DELETE FROM entity_links WHERE source_id IN ({in_clause}) OR target_id IN ({in_clause});
             DELETE FROM entities WHERE id IN ({in_clause});"
        ))?;

        Ok(orphan_ids.len())
    }

    /// Returns diagnostics for the entity store's own DuckDB connection.
    pub fn db_diagnostics(&self) -> StorageResult<serde_json::Value> {
        let conn = self.conn.lock().unwrap();
        Ok(scan_duckdb_connection(&conn))
    }
}

/// Scans a DuckDB connection and returns diagnostics: all tables, views, indexes, block info.
pub fn scan_duckdb_connection(conn: &duckdb::Connection) -> serde_json::Value {
    let mut tables = Vec::new();

    if let Ok(mut stmt) = conn.prepare(
        "SELECT schema_name, table_name, estimated_size, column_count FROM duckdb_tables()"
    ) {
        let rows: Vec<(String, String, i64, i64)> = stmt
            .query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, i64>(3)?,
                ))
            })
            .ok()
            .map(|iter| iter.filter_map(|r| r.ok()).collect())
            .unwrap_or_default();

        for (schema, name, estimated_size, column_count) in &rows {
            let qualified = if schema == "main" {
                format!("\"{}\"", name)
            } else {
                format!("\"{}\".\"{}\"", schema, name)
            };
            let row_count: i64 = conn
                .prepare(&format!("SELECT COUNT(*) FROM {}", qualified))
                .and_then(|mut s| s.query_row([], |r| r.get(0)))
                .unwrap_or(0);

            let display_name = if schema == "main" {
                name.clone()
            } else {
                format!("{}.{}", schema, name)
            };

            tables.push(serde_json::json!({
                "table": display_name,
                "schema": schema,
                "row_count": row_count,
                "estimated_size": estimated_size,
                "column_count": column_count,
            }));
        }
    }

    // Block-level allocation
    let mut db_sizes = Vec::new();
    if let Ok(mut size_stmt) = conn.prepare("SELECT * FROM pragma_database_size()") {
        db_sizes = size_stmt
            .query_map([], |row| {
                Ok(serde_json::json!({
                    "database_name": row.get::<_, String>(0).unwrap_or_default(),
                    "database_size": row.get::<_, String>(1).unwrap_or_default(),
                    "block_size": row.get::<_, i64>(2).unwrap_or(0),
                    "total_blocks": row.get::<_, i64>(3).unwrap_or(0),
                    "used_blocks": row.get::<_, i64>(4).unwrap_or(0),
                    "free_blocks": row.get::<_, i64>(5).unwrap_or(0),
                    "wal_size": row.get::<_, String>(6).unwrap_or_default(),
                }))
            })
            .ok()
            .map(|iter| iter.filter_map(|r| r.ok()).collect())
            .unwrap_or_default();
    }

    // Views
    let mut views = Vec::new();
    if let Ok(mut stmt) = conn.prepare("SELECT schema_name, view_name FROM duckdb_views()") {
        views = stmt
            .query_map([], |row| {
                Ok(serde_json::json!({
                    "schema": row.get::<_, String>(0).unwrap_or_default(),
                    "view": row.get::<_, String>(1).unwrap_or_default(),
                }))
            })
            .ok()
            .map(|iter| iter.filter_map(|r| r.ok()).collect())
            .unwrap_or_default();
    }

    // Indexes
    let mut indexes = Vec::new();
    if let Ok(mut stmt) = conn.prepare(
        "SELECT schema_name, table_name, index_name FROM duckdb_indexes()"
    ) {
        indexes = stmt
            .query_map([], |row| {
                Ok(serde_json::json!({
                    "schema": row.get::<_, String>(0).unwrap_or_default(),
                    "table": row.get::<_, String>(1).unwrap_or_default(),
                    "index": row.get::<_, String>(2).unwrap_or_default(),
                }))
            })
            .ok()
            .map(|iter| iter.filter_map(|r| r.ok()).collect())
            .unwrap_or_default();
    }

    serde_json::json!({
        "tables": tables,
        "databases": db_sizes,
        "views": views,
        "indexes": indexes,
    })
}

/// Opens a DuckDB file read-only and returns diagnostics.
/// Returns None if the file doesn't exist or can't be opened.
pub fn scan_duckdb_file(path: &std::path::Path) -> Option<serde_json::Value> {
    if !path.exists() {
        return None;
    }
    let file_size = std::fs::metadata(path).map(|m| m.len() as i64).unwrap_or(0);
    let conn = duckdb::Connection::open_with_flags(
        path,
        duckdb::Config::default(),
    ).ok()?;

    let mut diag = scan_duckdb_connection(&conn);
    if let Some(obj) = diag.as_object_mut() {
        obj.insert("file_size".to_string(), serde_json::json!(file_size));
    }
    Some(diag)
}

// -- Field extraction helpers --

fn extract_field(
    data: &serde_json::Value,
    indexed_fields: &[IndexedField],
    target_type: FieldType,
    preferred_path: &str,
) -> Option<String> {
    // First try the preferred path
    if let Some(field) = indexed_fields.iter().find(|f| f.field_path == preferred_path && f.field_type == target_type) {
        if let Some(val) = data.pointer(&field.field_path) {
            return Some(val.as_str().unwrap_or(&val.to_string()).to_string());
        }
    }
    // Fall back to first field of matching type
    for field in indexed_fields {
        if field.field_type == target_type && field.field_path != preferred_path {
            if let Some(val) = data.pointer(&field.field_path) {
                return Some(val.as_str().unwrap_or(&val.to_string()).to_string());
            }
        }
    }
    None
}

fn extract_tags(data: &serde_json::Value, indexed_fields: &[IndexedField]) -> Vec<String> {
    let mut tags = Vec::new();
    for field in indexed_fields {
        if field.field_type == FieldType::Tag {
            if let Some(arr) = data.pointer(&field.field_path).and_then(|v| v.as_array()) {
                for item in arr {
                    if let Some(s) = item.as_str() {
                        tags.push(s.to_string());
                    }
                }
            }
        }
    }
    tags
}

/// Extracts Relation-typed fields and saves them as entity_links.
fn extract_relations(
    conn: &Connection,
    entity: &Entity,
    indexed_fields: &[IndexedField],
) -> StorageResult<()> {
    for field in indexed_fields {
        if field.field_type == FieldType::Relation {
            // Relation value: a string target entity ID, or an object with {type, id}
            if let Some(val) = entity.data.pointer(&field.field_path) {
                if let Some(target_id) = val.as_str() {
                    // Simple string relation — target type is unknown, use "_"
                    conn.execute(
                        "INSERT OR IGNORE INTO entity_links (source_type, source_id, target_type, target_id) VALUES (?, ?, '_', ?)",
                        params![entity.entity_type, entity.id, target_id],
                    )?;
                } else if let (Some(target_type), Some(target_id)) = (
                    val.pointer("/type").and_then(|v| v.as_str()),
                    val.pointer("/id").and_then(|v| v.as_str()),
                ) {
                    conn.execute(
                        "INSERT OR IGNORE INTO entity_links (source_type, source_id, target_type, target_id) VALUES (?, ?, ?, ?)",
                        params![entity.entity_type, entity.id, target_type, target_id],
                    )?;
                }
            }
        }
    }
    Ok(())
}

/// Extracts Vector-typed fields and stores embeddings in entity_vectors.
fn extract_vectors(
    conn: &Connection,
    entity: &Entity,
    indexed_fields: &[IndexedField],
) -> StorageResult<()> {
    // Clear stale vectors for this entity, then re-insert current ones
    conn.execute(
        "DELETE FROM entity_vectors WHERE entity_id = ?",
        params![entity.id],
    )?;

    for field in indexed_fields {
        if field.field_type == FieldType::Vector {
            let dim = field.vector_dim.unwrap_or(0);
            if let Some(arr) = entity.data.pointer(&field.field_path).and_then(|v| v.as_array()) {
                // Validate dimension matches schema
                if arr.len() != dim as usize {
                    continue;
                }
                // Build DuckDB array literal: [0.1, 0.2, ...]
                let components: Vec<String> = arr
                    .iter()
                    .filter_map(|v| v.as_f64().map(|f| f.to_string()))
                    .collect();
                if components.len() != dim as usize {
                    continue;
                }
                let array_literal = format!("[{}]", components.join(","));
                conn.execute(
                    &format!(
                        "INSERT INTO entity_vectors (entity_id, field_path, dim, embedding) VALUES (?, ?, ?, {}::DOUBLE[])",
                        array_literal
                    ),
                    params![entity.id, field.field_path, dim as i32],
                )?;
            }
        }
    }
    Ok(())
}

fn build_search_text(title: &Option<String>, body: &Option<String>, tags: &[String]) -> String {
    let mut text = String::new();
    if let Some(t) = title {
        text.push_str(t);
        text.push(' ');
    }
    if let Some(b) = body {
        text.push_str(b);
        text.push(' ');
    }
    for tag in tags {
        text.push_str(tag);
        text.push(' ');
    }
    text
}

/// Compare a decrypted JSON value against a filter value.
fn match_filter_value(actual: &serde_json::Value, expected: &serde_json::Value) -> bool {
    match (actual, expected) {
        (serde_json::Value::String(a), serde_json::Value::String(e)) => a == e,
        (serde_json::Value::Number(a), serde_json::Value::Number(e)) => a == e,
        (serde_json::Value::Bool(a), serde_json::Value::Bool(e)) => a == e,
        // Filter value is always a string from the FFI layer — coerce actual to string
        (_, serde_json::Value::String(e)) => match actual {
            serde_json::Value::Number(n) => n.to_string() == *e,
            serde_json::Value::Bool(b) => b.to_string() == *e,
            _ => false,
        },
        _ => actual == expected,
    }
}

// -- Base64 helpers (avoid pulling in the full `base64` crate in storage) --

fn base64_encode(data: &[u8]) -> String {
    use std::fmt::Write;
    const CHARS: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = if chunk.len() > 1 { chunk[1] as u32 } else { 0 };
        let b2 = if chunk.len() > 2 { chunk[2] as u32 } else { 0 };
        let n = (b0 << 16) | (b1 << 8) | b2;
        let _ = write!(out, "{}", CHARS[((n >> 18) & 0x3F) as usize] as char);
        let _ = write!(out, "{}", CHARS[((n >> 12) & 0x3F) as usize] as char);
        if chunk.len() > 1 {
            let _ = write!(out, "{}", CHARS[((n >> 6) & 0x3F) as usize] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            let _ = write!(out, "{}", CHARS[(n & 0x3F) as usize] as char);
        } else {
            out.push('=');
        }
    }
    out
}

fn base64_decode(input: &str) -> Result<Vec<u8>, String> {
    fn val(c: u8) -> Result<u8, String> {
        match c {
            b'A'..=b'Z' => Ok(c - b'A'),
            b'a'..=b'z' => Ok(c - b'a' + 26),
            b'0'..=b'9' => Ok(c - b'0' + 52),
            b'+' => Ok(62),
            b'/' => Ok(63),
            _ => Err(format!("invalid base64 char: {c}")),
        }
    }
    let bytes: Vec<u8> = input.bytes().filter(|&b| b != b'=' && b != b'\n' && b != b'\r').collect();
    let mut out = Vec::with_capacity(bytes.len() * 3 / 4);
    for chunk in bytes.chunks(4) {
        if chunk.len() < 2 {
            return Err("truncated base64".into());
        }
        let a = val(chunk[0])? as u32;
        let b = val(chunk[1])? as u32;
        let c = if chunk.len() > 2 { val(chunk[2])? as u32 } else { 0 };
        let d = if chunk.len() > 3 { val(chunk[3])? as u32 } else { 0 };
        let n = (a << 18) | (b << 12) | (c << 6) | d;
        out.push((n >> 16) as u8);
        if chunk.len() > 2 {
            out.push((n >> 8) as u8);
        }
        if chunk.len() > 3 {
            out.push(n as u8);
        }
    }
    Ok(out)
}

// -- Schema --

fn initialize_entity_schema(conn: &Connection) -> StorageResult<()> {
    // Migration: add local_only column to existing entities table BEFORE the main
    // batch, because CREATE TABLE IF NOT EXISTS won't add new columns to an existing
    // table, but the batch references local_only in a CREATE INDEX statement.
    let entities_exists: bool = conn
        .query_row(
            "SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_name = 'entities'",
            [],
            |row| row.get(0),
        )
        .unwrap_or(false);

    if entities_exists {
        let has_local_only = conn
            .execute("SELECT local_only FROM entities LIMIT 0", [])
            .is_ok();
        if !has_local_only {
            let _ = conn.execute_batch(
                r#"
                ALTER TABLE entities ADD COLUMN local_only BOOLEAN DEFAULT FALSE;
                "#,
            );
        }
    }

    conn.execute_batch(
        r#"
        CREATE TABLE IF NOT EXISTS entities (
            id VARCHAR PRIMARY KEY,
            entity_type VARCHAR NOT NULL,
            data_json TEXT NOT NULL,
            title VARCHAR,
            body TEXT,
            tags VARCHAR[],
            is_trashed BOOLEAN DEFAULT FALSE,
            is_favorite BOOLEAN DEFAULT FALSE,
            local_only BOOLEAN DEFAULT FALSE,
            created_at BIGINT NOT NULL,
            modified_at BIGINT NOT NULL,
            created_by VARCHAR NOT NULL,
            search_text TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(entity_type);
        CREATE INDEX IF NOT EXISTS idx_entities_modified ON entities(modified_at DESC);
        CREATE INDEX IF NOT EXISTS idx_entities_trashed ON entities(is_trashed);
        CREATE INDEX IF NOT EXISTS idx_entities_favorite ON entities(is_favorite);
        CREATE INDEX IF NOT EXISTS idx_entities_local_only ON entities(local_only);

        CREATE TABLE IF NOT EXISTS entity_links (
            source_type VARCHAR NOT NULL,
            source_id VARCHAR NOT NULL,
            target_type VARCHAR NOT NULL,
            target_id VARCHAR NOT NULL,
            PRIMARY KEY (source_type, source_id, target_type, target_id)
        );

        CREATE TABLE IF NOT EXISTS entity_vectors (
            entity_id VARCHAR NOT NULL,
            field_path VARCHAR NOT NULL,
            dim INTEGER NOT NULL,
            embedding DOUBLE[],
            PRIMARY KEY (entity_id, field_path)
        );

        -- Sync ledger: tracks which entities have been synced with which peer.
        -- Used for incremental sync — only entities missing or modified since
        -- last sync with a specific peer need to be exchanged.
        CREATE TABLE IF NOT EXISTS sync_ledger (
            peer_id   VARCHAR NOT NULL,
            entity_id VARCHAR NOT NULL,
            synced_at BIGINT NOT NULL,
            PRIMARY KEY (peer_id, entity_id)
        );

        -- Cloud sync cursor persistence: stores per-entity cursor positions
        -- and last_sync_at so the engine resumes where it left off on restart.
        CREATE TABLE IF NOT EXISTS cloud_sync_cursors (
            cursor_key VARCHAR PRIMARY KEY,
            cursor_value BIGINT NOT NULL,
            updated_at BIGINT NOT NULL
        );

        -- Plugin fuel consumption history for metrics tracking
        CREATE TABLE IF NOT EXISTS plugin_fuel_history (
            plugin_id VARCHAR NOT NULL,
            fuel_consumed BIGINT NOT NULL,
            recorded_at BIGINT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_plugin_fuel_plugin_id ON plugin_fuel_history(plugin_id);
        CREATE INDEX IF NOT EXISTS idx_plugin_fuel_recorded_at ON plugin_fuel_history(plugin_id, recorded_at DESC);
        "#,
    )?;

    // Migration: drop plugin_fuel_history if it has the old schema with 'id' column
    // Try a simple insert - if it fails due to 'id' column, recreate the table
    let needs_migration = conn
        .execute(
            "INSERT INTO plugin_fuel_history (plugin_id, fuel_consumed, recorded_at) VALUES ('__migration_test__', 0, 0)",
            [],
        )
        .is_err();

    if needs_migration {
        // Old schema exists - drop and recreate
        let _ = conn.execute_batch(
            r#"
            DROP TABLE IF EXISTS plugin_fuel_history;
            CREATE TABLE plugin_fuel_history (
                plugin_id VARCHAR NOT NULL,
                fuel_consumed BIGINT NOT NULL,
                recorded_at BIGINT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_plugin_fuel_plugin_id ON plugin_fuel_history(plugin_id);
            CREATE INDEX IF NOT EXISTS idx_plugin_fuel_recorded_at ON plugin_fuel_history(plugin_id, recorded_at DESC);
            "#,
        );
    } else {
        // Clean up test row
        let _ = conn.execute(
            "DELETE FROM plugin_fuel_history WHERE plugin_id = '__migration_test__'",
            [],
        );
    }

    Ok(())
}
