//! Cloud sync orchestrator.
//!
//! Main event loop that coordinates:
//! - Outbox flushing (adaptive intervals based on collaboration context)
//! - Polling for new data from other devices
//! - STS credential refresh
//! - Command processing (stop, force flush, share)
//!
//! Follows the same architecture as `SyncOrchestrator` in `privstack-sync`.

use crate::api_client::CloudApiClient;
use crate::compaction::batch_s3_key;
use crate::credential_manager::CredentialManager;
use crate::dek_registry::DekRegistry;
use crate::error::{CloudError, CloudResult};
use crate::outbox::Outbox;
use crate::s3_transport::S3Transport;
use crate::types::*;

use privstack_crypto::{decrypt, encrypt, EncryptedData};
use privstack_types::Event;
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc;
use tracing::{debug, error, info, warn};

/// Cloud sync engine — main orchestration loop.
pub struct CloudSyncEngine {
    api: Arc<CloudApiClient>,
    transport: Arc<S3Transport>,
    cred_manager: Arc<CredentialManager>,
    dek_registry: DekRegistry,
    outbox: Outbox,
    command_rx: mpsc::Receiver<CloudCommand>,
    event_tx: mpsc::Sender<Event>,
    user_id: i64,
    workspace_id: String,
    device_id: String,
    poll_interval: Duration,
    /// Per-entity cursor positions for this device.
    cursors: HashMap<String, i64>,
}

/// Handle for sending commands to the sync engine.
#[derive(Clone)]
pub struct CloudSyncHandle {
    command_tx: mpsc::Sender<CloudCommand>,
}

impl CloudSyncHandle {
    pub async fn stop(&self) -> CloudResult<()> {
        self.command_tx
            .send(CloudCommand::Stop)
            .await
            .map_err(|_| CloudError::Api("sync engine not running".to_string()))
    }

    pub async fn force_flush(&self) -> CloudResult<()> {
        self.command_tx
            .send(CloudCommand::ForceFlush)
            .await
            .map_err(|_| CloudError::Api("sync engine not running".to_string()))
    }

    pub async fn share_entity(
        &self,
        entity_id: String,
        entity_type: String,
        recipient_email: String,
        permission: SharePermission,
    ) -> CloudResult<()> {
        self.command_tx
            .send(CloudCommand::ShareEntity {
                entity_id,
                entity_type,
                recipient_email,
                permission,
            })
            .await
            .map_err(|_| CloudError::Api("sync engine not running".to_string()))
    }
}

/// Creates a cloud sync engine and its command handle.
pub fn create_cloud_sync_engine(
    api: Arc<CloudApiClient>,
    transport: Arc<S3Transport>,
    cred_manager: Arc<CredentialManager>,
    dek_registry: DekRegistry,
    event_tx: mpsc::Sender<Event>,
    user_id: i64,
    workspace_id: String,
    device_id: String,
    poll_interval: Duration,
) -> (CloudSyncHandle, CloudSyncEngine) {
    let (command_tx, command_rx) = mpsc::channel(64);

    let handle = CloudSyncHandle { command_tx };

    let engine = CloudSyncEngine {
        api,
        transport,
        cred_manager,
        dek_registry,
        outbox: Outbox::new(),
        command_rx,
        event_tx,
        user_id,
        workspace_id,
        device_id,
        poll_interval,
        cursors: HashMap::new(),
    };

    (handle, engine)
}

impl CloudSyncEngine {
    /// Runs the sync engine event loop.
    pub async fn run(&mut self) {
        info!(
            "cloud sync engine started for workspace {}",
            self.workspace_id
        );

        let mut flush_interval = tokio::time::interval(Duration::from_secs(5));
        let mut poll_interval = tokio::time::interval(self.poll_interval);
        let mut cred_check_interval = tokio::time::interval(Duration::from_secs(300));

        // Skip first immediate tick
        flush_interval.tick().await;
        poll_interval.tick().await;
        cred_check_interval.tick().await;

        loop {
            tokio::select! {
                _ = flush_interval.tick() => {
                    if self.outbox.should_flush() {
                        if let Err(e) = self.flush_outbox().await {
                            error!("outbox flush failed: {e}");
                        }
                    }
                }
                _ = poll_interval.tick() => {
                    if let Err(e) = self.poll_and_apply().await {
                        warn!("poll failed: {e}");
                    }
                }
                _ = cred_check_interval.tick() => {
                    if !self.cred_manager.has_valid_credentials().await {
                        debug!("proactively refreshing STS credentials");
                        if let Err(e) = self.cred_manager.refresh().await {
                            warn!("credential refresh failed: {e}");
                        }
                    }
                }
                cmd = self.command_rx.recv() => {
                    match cmd {
                        Some(CloudCommand::Stop) => {
                            info!("cloud sync engine stopping");
                            if !self.outbox.is_empty() {
                                let _ = self.flush_outbox().await;
                            }
                            break;
                        }
                        Some(CloudCommand::ForceFlush) => {
                            if !self.outbox.is_empty() {
                                if let Err(e) = self.flush_outbox().await {
                                    error!("force flush failed: {e}");
                                }
                            }
                        }
                        Some(CloudCommand::ShareEntity { .. }) => {
                            debug!("share entity command received — delegating to share manager");
                        }
                        None => {
                            info!("command channel closed, stopping sync engine");
                            break;
                        }
                    }
                }
            }
        }

        info!("cloud sync engine stopped");
    }

    /// Adds a local event to the outbox.
    pub fn record_event(&mut self, event: Event) {
        self.outbox.push(event);
    }

    /// Flushes pending events as encrypted per-entity batches to S3.
    async fn flush_outbox(&mut self) -> CloudResult<()> {
        let events = self.outbox.take_pending();
        if events.is_empty() {
            return Ok(());
        }

        // Group events by entity_id
        let mut by_entity: HashMap<String, Vec<Event>> = HashMap::new();
        for event in events {
            let entity_id = event.entity_id.to_string();
            by_entity.entry(entity_id).or_default().push(event);
        }

        let creds = self.cred_manager.get_credentials().await?;

        for (entity_id, entity_events) in by_entity {
            let event_count = entity_events.len();
            let cursor_start = self.cursors.get(&entity_id).copied().unwrap_or(0);
            let cursor_end = cursor_start + event_count as i64;

            // Serialize and encrypt with entity DEK
            let serialized = serde_json::to_vec(&entity_events)?;
            let dek = self.dek_registry.get(&entity_id).await?;
            let encrypted = encrypt(&dek, &serialized)
                .map_err(|e| CloudError::Envelope(format!("batch encryption failed: {e}")))?;
            let encrypted_bytes = serde_json::to_vec(&encrypted)?;

            let s3_key = batch_s3_key(
                self.user_id,
                &self.workspace_id,
                &entity_id,
                cursor_start,
                cursor_end,
            );

            self.transport
                .upload(&creds, &s3_key, encrypted_bytes.clone())
                .await?;

            self.api
                .advance_cursor(&AdvanceCursorRequest {
                    workspace_id: self.workspace_id.clone(),
                    device_id: self.device_id.clone(),
                    entity_id: entity_id.clone(),
                    cursor_position: cursor_end,
                    batch_key: s3_key,
                    size_bytes: encrypted_bytes.len() as u64,
                    event_count: event_count as u32,
                })
                .await?;

            self.cursors.insert(entity_id.clone(), cursor_end);
            debug!("flushed {event_count} events for entity {entity_id} (cursor -> {cursor_end})");
        }

        Ok(())
    }

    /// Polls for new data from other devices, decrypts, and applies it.
    async fn poll_and_apply(&mut self) -> CloudResult<()> {
        let pending = self
            .api
            .get_pending_changes(&self.workspace_id, &self.device_id)
            .await?;

        if pending.entities.is_empty() {
            return Ok(());
        }

        for entity in &pending.entities {
            let dek = match self.dek_registry.get(&entity.entity_id).await {
                Ok(dek) => dek,
                Err(e) => {
                    warn!("skipping entity {} (no DEK available): {e}", entity.entity_id);
                    continue;
                }
            };

            debug!(
                "entity {} has {} new batches (cursor {} -> {})",
                entity.entity_id,
                entity.batches.len(),
                entity.device_cursor,
                entity.current_cursor
            );

            for batch in &entity.batches {
                let creds = self.cred_manager.get_credentials().await?;
                let data = self.transport.download(&creds, &batch.s3_key).await?;

                // Deserialize encrypted envelope and decrypt with entity DEK
                let encrypted: EncryptedData = match serde_json::from_slice(&data) {
                    Ok(enc) => enc,
                    Err(e) => {
                        warn!("failed to deserialize encrypted batch {}: {e}", batch.s3_key);
                        continue;
                    }
                };

                let plaintext = match decrypt(&dek, &encrypted) {
                    Ok(pt) => pt,
                    Err(e) => {
                        warn!("failed to decrypt batch {}: {e}", batch.s3_key);
                        continue;
                    }
                };

                match serde_json::from_slice::<Vec<Event>>(&plaintext) {
                    Ok(events) => {
                        for event in events {
                            if let Err(e) = self.event_tx.send(event).await {
                                error!("failed to send event to application: {e}");
                            }
                        }
                    }
                    Err(e) => {
                        warn!(
                            "failed to deserialize batch {}: {e}",
                            batch.s3_key
                        );
                    }
                }
            }
        }

        Ok(())
    }
}
