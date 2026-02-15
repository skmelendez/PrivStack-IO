//! Snapshot generation and compaction management.
//!
//! When an entity accumulates >50 batches, the client generates a snapshot
//! (full serialized state) and notifies the API. The API then deletes old
//! batches server-side (clients never have S3 DeleteObject permission).

use crate::api_client::CloudApiClient;
use crate::credential_manager::CredentialManager;
use crate::error::{CloudError, CloudResult};
use crate::s3_transport::S3Transport;
use privstack_crypto::{encrypt, DerivedKey};
use tracing::{debug, info};

/// Threshold: trigger compaction after this many batches per entity.
const COMPACTION_BATCH_THRESHOLD: usize = 50;

/// Checks if an entity needs compaction based on batch count.
pub fn needs_compaction(batch_count: usize) -> bool {
    batch_count > COMPACTION_BATCH_THRESHOLD
}

/// Generates a snapshot key for the S3 storage layout.
pub fn snapshot_s3_key(
    user_id: i64,
    workspace_id: &str,
    entity_id: &str,
    cursor_position: i64,
) -> String {
    format!("{user_id}/{workspace_id}/entities/{entity_id}/snapshot_{cursor_position}.enc")
}

/// Generates a batch key for the S3 storage layout.
pub fn batch_s3_key(
    user_id: i64,
    workspace_id: &str,
    entity_id: &str,
    cursor_start: i64,
    cursor_end: i64,
) -> String {
    format!(
        "{user_id}/{workspace_id}/entities/{entity_id}/batch_{cursor_start}_{cursor_end}.enc"
    )
}

/// Blob key for the S3 storage layout.
pub fn blob_s3_key(user_id: i64, workspace_id: &str, blob_id: &str) -> String {
    format!("{user_id}/{workspace_id}/blobs/{blob_id}.enc")
}

/// Private key storage key (passphrase-encrypted).
pub fn private_key_s3_key(user_id: i64, workspace_id: &str) -> String {
    format!("{user_id}/{workspace_id}/keys/private_key.enc")
}

/// Recovery key storage key (mnemonic-encrypted).
pub fn recovery_key_s3_key(user_id: i64, workspace_id: &str) -> String {
    format!("{user_id}/{workspace_id}/keys/private_key_recovery.enc")
}

/// Creates and uploads a snapshot, then notifies the API for compaction.
pub async fn create_snapshot(
    api: &CloudApiClient,
    transport: &S3Transport,
    cred_manager: &CredentialManager,
    user_id: i64,
    workspace_id: &str,
    entity_id: &str,
    entity_dek: &DerivedKey,
    serialized_state: &[u8],
    cursor_position: i64,
) -> CloudResult<()> {
    // Encrypt snapshot with entity DEK
    let encrypted = encrypt(entity_dek, serialized_state)
        .map_err(|e| CloudError::Envelope(format!("snapshot encryption failed: {e}")))?;

    let snapshot_bytes = serde_json::to_vec(&encrypted)?;
    let s3_key = snapshot_s3_key(user_id, workspace_id, entity_id, cursor_position);

    // Upload to S3
    let creds = cred_manager.get_credentials().await?;
    transport.upload(&creds, &s3_key, snapshot_bytes).await?;

    info!(
        "uploaded snapshot for entity {entity_id} at cursor {cursor_position}"
    );

    // Notify API to clean up old batches
    api.notify_snapshot(entity_id, workspace_id, &s3_key, cursor_position)
        .await?;

    debug!("notified API for compaction of entity {entity_id}");
    Ok(())
}
