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
#[derive(Clone)]
pub struct DekRegistry {
    deks: Arc<RwLock<HashMap<String, DerivedKey>>>,
}

impl DekRegistry {
    pub fn new() -> Self {
        Self {
            deks: Arc::new(RwLock::new(HashMap::new())),
        }
    }

    /// Registers a DEK for an entity.
    pub async fn insert(&self, entity_id: String, dek: DerivedKey) {
        self.deks.write().await.insert(entity_id, dek);
    }

    /// Retrieves a cloned DEK for an entity.
    pub async fn get(&self, entity_id: &str) -> CloudResult<DerivedKey> {
        self.deks
            .read()
            .await
            .get(entity_id)
            .cloned()
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
