//! Thread-safe registry of per-entity data encryption keys (DEKs).
//!
//! The sync engine reads DEKs from this registry to encrypt outgoing
//! batches and decrypt incoming batches. The application layer populates
//! it when entities are loaded, created, or received via sharing.

use crate::error::{CloudError, CloudResult};
use privstack_crypto::DerivedKey;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::RwLock;

/// Thread-safe entity DEK registry for cloud sync encryption.
///
/// Supports both per-entity DEKs (for future sharing granularity) and a
/// workspace-level default DEK used as fallback when no entity-specific
/// key has been registered.
#[derive(Clone)]
pub struct DekRegistry {
    deks: Arc<RwLock<HashMap<String, DerivedKey>>>,
    default_dek: Arc<RwLock<Option<DerivedKey>>>,
}

impl DekRegistry {
    pub fn new() -> Self {
        Self {
            deks: Arc::new(RwLock::new(HashMap::new())),
            default_dek: Arc::new(RwLock::new(None)),
        }
    }

    /// Sets the workspace-level default DEK used for all entities that
    /// don't have a per-entity key registered.
    pub async fn set_default(&self, dek: DerivedKey) {
        *self.default_dek.write().await = Some(dek);
    }

    /// Registers a DEK for an entity.
    pub async fn insert(&self, entity_id: String, dek: DerivedKey) {
        self.deks.write().await.insert(entity_id, dek);
    }

    /// Retrieves a cloned DEK for an entity.
    ///
    /// Looks up a per-entity key first, then falls back to the workspace
    /// default DEK. Returns an error only if neither is available.
    pub async fn get(&self, entity_id: &str) -> CloudResult<DerivedKey> {
        if let Some(dek) = self.deks.read().await.get(entity_id).cloned() {
            return Ok(dek);
        }
        self.default_dek
            .read()
            .await
            .clone()
            .ok_or_else(|| {
                CloudError::Envelope(format!("no DEK registered for entity {entity_id}"))
            })
    }

    /// Removes a DEK (e.g. after entity deletion or DEK rotation).
    pub async fn remove(&self, entity_id: &str) -> Option<DerivedKey> {
        self.deks.write().await.remove(entity_id)
    }

    /// Returns the number of registered DEKs.
    pub async fn len(&self) -> usize {
        self.deks.read().await.len()
    }

    /// Returns true if no DEKs are registered.
    pub async fn is_empty(&self) -> bool {
        self.deks.read().await.is_empty()
    }
}

impl Default for DekRegistry {
    fn default() -> Self {
        Self::new()
    }
}
