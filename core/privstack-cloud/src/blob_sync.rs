//! Blob upload/download for file attachments.
//!
//! Encrypts blobs with the entity DEK before uploading to S3, and registers
//! metadata with the API for quota tracking and share partner access.

use crate::api_client::CloudApiClient;
use crate::compaction::blob_s3_key;
use crate::credential_manager::CredentialManager;
use crate::error::{CloudError, CloudResult};
use crate::s3_transport::S3Transport;
use crate::types::*;
use privstack_crypto::{decrypt, encrypt, DerivedKey, EncryptedData};
use sha2::{Digest, Sha256};
use std::sync::Arc;
use tracing::debug;

/// Manages blob upload/download with encryption and quota tracking.
pub struct BlobSyncManager {
    api: Arc<CloudApiClient>,
    transport: Arc<S3Transport>,
    cred_manager: Arc<CredentialManager>,
}

impl BlobSyncManager {
    pub fn new(
        api: Arc<CloudApiClient>,
        transport: Arc<S3Transport>,
        cred_manager: Arc<CredentialManager>,
    ) -> Self {
        Self {
            api,
            transport,
            cred_manager,
        }
    }

    /// Uploads a blob encrypted with the entity DEK.
    pub async fn upload_blob(
        &self,
        user_id: i64,
        workspace_id: &str,
        blob_id: &str,
        entity_id: Option<&str>,
        data: &[u8],
        entity_dek: &DerivedKey,
    ) -> CloudResult<()> {
        // Encrypt blob
        let encrypted = encrypt(entity_dek, data)
            .map_err(|e| CloudError::Envelope(format!("blob encryption failed: {e}")))?;

        let encrypted_bytes = serde_json::to_vec(&encrypted)?;
        let s3_key = blob_s3_key(user_id, workspace_id, blob_id);
        let content_hash = hex::encode(Sha256::digest(data));

        // Upload to S3
        let creds = self.cred_manager.get_credentials().await?;
        self.transport
            .upload(&creds, &s3_key, encrypted_bytes.clone())
            .await?;

        // Register with API for quota tracking
        self.api
            .register_blob(&RegisterBlobRequest {
                workspace_id: workspace_id.to_string(),
                blob_id: blob_id.to_string(),
                entity_id: entity_id.map(|s| s.to_string()),
                s3_key: s3_key.clone(),
                size_bytes: encrypted_bytes.len() as u64,
                content_hash: Some(content_hash),
            })
            .await?;

        debug!("uploaded blob {blob_id} ({} bytes encrypted)", encrypted_bytes.len());
        Ok(())
    }

    /// Downloads and decrypts a blob.
    pub async fn download_blob(
        &self,
        s3_key: &str,
        entity_dek: &DerivedKey,
    ) -> CloudResult<Vec<u8>> {
        let creds = self.cred_manager.get_credentials().await?;
        let encrypted_bytes = self.transport.download(&creds, s3_key).await?;

        let encrypted: EncryptedData = serde_json::from_slice(&encrypted_bytes)?;

        decrypt(entity_dek, &encrypted)
            .map_err(|e| CloudError::Envelope(format!("blob decryption failed: {e}")))
    }

    /// Gets blob metadata for an entity.
    pub async fn get_entity_blobs(&self, entity_id: &str) -> CloudResult<Vec<BlobMeta>> {
        self.api.get_entity_blobs(entity_id).await
    }
}
