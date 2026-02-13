//! Sync orchestrator — coordinates the sync process.
//!
//! The orchestrator is the "brain" that ties together:
//! - Peer discovery (from P2P transport)
//! - Sync engine (message production/consumption)
//! - Event application (applying received events)
//!
//! It owns all I/O. The engine is a pure state machine.

use crate::engine::SyncEngine;
use crate::pairing::PairingManager;
use crate::policy::{PersonalSyncPolicy, SyncPolicy};
use crate::protocol::{
    ErrorMessage, SyncMessage, SyncStateMessage, PROTOCOL_VERSION,
};
use crate::transport::SyncTransport;
use crate::{SyncConfig, SyncError, SyncResult};
use privstack_storage::{EntityStore, EventStore};
use privstack_types::{EntityId, Event, EventId, PeerId};
use std::collections::HashSet;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc;
type TokioMutex<T> = tokio::sync::Mutex<T>;
use tracing::{debug, error, info, warn};

/// Commands that can be sent to the orchestrator.
#[derive(Debug)]
pub enum SyncCommand {
    /// Trigger a sync with a specific peer.
    SyncWithPeer { peer_id: PeerId },
    /// Sync a specific entity with all connected peers.
    SyncEntity { entity_id: EntityId },
    /// Record a local event (from user edit).
    RecordLocalEvent { event: Event },
    /// Add an entity to the list of shared entities.
    ShareEntity { entity_id: EntityId },
    /// Share an entity with a specific peer (personal policy).
    ShareEntityWithPeer { entity_id: EntityId, peer_id: PeerId },
    /// Stop the orchestrator.
    Shutdown,
}

/// Events emitted by the orchestrator for the UI.
#[derive(Debug, Clone)]
pub enum SyncEvent {
    /// A new peer was discovered.
    PeerDiscovered {
        peer_id: PeerId,
        device_name: Option<String>,
    },
    /// Sync started with a peer.
    SyncStarted { peer_id: PeerId },
    /// Sync completed with a peer.
    SyncCompleted {
        peer_id: PeerId,
        events_sent: usize,
        events_received: usize,
    },
    /// Sync failed with a peer.
    SyncFailed { peer_id: PeerId, error: String },
    /// An entity was updated from sync.
    EntityUpdated { entity_id: EntityId },
}

/// Configuration for the sync orchestrator.
#[derive(Debug, Clone)]
pub struct OrchestratorConfig {
    /// Interval between periodic sync attempts.
    pub sync_interval: Duration,
    /// Interval to check for new peers.
    pub discovery_interval: Duration,
    /// Whether to auto-sync with discovered peers.
    pub auto_sync: bool,
    /// Max entities to sync per cycle. Large initial syncs are chunked across
    /// multiple cycles to avoid timeouts. 0 = unlimited.
    pub max_entities_per_sync: usize,
}

impl Default for OrchestratorConfig {
    fn default() -> Self {
        Self {
            sync_interval: Duration::from_secs(30),
            discovery_interval: Duration::from_secs(5),
            auto_sync: true,
            max_entities_per_sync: 100,
        }
    }
}

/// Handle to send commands to the orchestrator.
#[derive(Clone)]
pub struct OrchestratorHandle {
    command_tx: mpsc::Sender<SyncCommand>,
}

impl OrchestratorHandle {
    /// Sends a command to the orchestrator.
    pub async fn send(&self, cmd: SyncCommand) -> Result<(), mpsc::error::SendError<SyncCommand>> {
        self.command_tx.send(cmd).await
    }

    /// Records a local event (call this when user makes an edit).
    pub async fn record_event(&self, event: Event) -> SyncResult<()> {
        self.command_tx
            .send(SyncCommand::RecordLocalEvent { event })
            .await
            .map_err(|_| SyncError::ChannelClosed)
    }

    /// Triggers sync for an entity.
    pub async fn sync_entity(&self, entity_id: EntityId) -> SyncResult<()> {
        self.command_tx
            .send(SyncCommand::SyncEntity { entity_id })
            .await
            .map_err(|_| SyncError::ChannelClosed)
    }

    /// Shares an entity with a specific peer (personal policy).
    pub async fn share_entity_with_peer(&self, entity_id: EntityId, peer_id: PeerId) -> SyncResult<()> {
        self.command_tx
            .send(SyncCommand::ShareEntityWithPeer { entity_id, peer_id })
            .await
            .map_err(|_| SyncError::ChannelClosed)
    }

    /// Shares an entity for sync.
    pub async fn share_entity(&self, entity_id: EntityId) -> SyncResult<()> {
        self.command_tx
            .send(SyncCommand::ShareEntity { entity_id })
            .await
            .map_err(|_| SyncError::ChannelClosed)
    }

    /// Shuts down the orchestrator.
    pub async fn shutdown(&self) -> SyncResult<()> {
        self.command_tx
            .send(SyncCommand::Shutdown)
            .await
            .map_err(|_| SyncError::ChannelClosed)
    }
}

/// The sync orchestrator.
pub struct SyncOrchestrator {
    /// The sync engine (pure state machine, no I/O).
    engine: SyncEngine,
    /// The entity store (for applying sync results).
    entity_store: Arc<EntityStore>,
    /// The event store (for persisting sync events).
    event_store: Arc<EventStore>,
    /// Configuration.
    config: OrchestratorConfig,
    /// Entities we're sharing.
    shared_entities: HashSet<EntityId>,
    /// Peers we've synced with.
    synced_peers: HashSet<PeerId>,
    /// Event sender (for UI notifications).
    event_tx: mpsc::Sender<SyncEvent>,
    /// Optional pairing manager — if set, only trusted peers can sync.
    pairing_manager: Option<Arc<std::sync::Mutex<PairingManager>>>,
    /// Optional personal sync policy for per-peer entity sharing.
    personal_policy: Option<Arc<PersonalSyncPolicy>>,
}

impl SyncOrchestrator {
    /// Runs the orchestrator event loop with a transport.
    pub async fn run(
        mut self,
        transport: Arc<TokioMutex<dyn SyncTransport>>,
        mut command_rx: mpsc::Receiver<SyncCommand>,
    ) -> SyncResult<()> {
        let mut discovery_interval = tokio::time::interval(self.config.discovery_interval);
        let mut sync_interval = tokio::time::interval(self.config.sync_interval);

        // Pre-populate shared_entities from the entity store so existing data syncs immediately.
        // Also create snapshot events for any entities that have no events in the event store
        // (e.g. entities created before sync was started, or events lost during data wipe).
        {
            let entity_store = self.entity_store.clone();
            let event_store = self.event_store.clone();
            let peer_id = self.engine.peer_id();
            match tokio::task::spawn_blocking(move || {
                let ids = entity_store.list_all_entity_ids()?;
                let mut needs_snapshot: Vec<(EntityId, String, String)> = Vec::new();
                for id_str in &ids {
                    if let Ok(eid) = id_str.parse::<EntityId>() {
                        let has_events = event_store
                            .get_events_for_entity(&eid)
                            .map(|evts| !evts.is_empty())
                            .unwrap_or(false);
                        if !has_events {
                            if let Ok(Some(entity)) = entity_store.get_entity(id_str) {
                                needs_snapshot.push((eid, entity.entity_type, entity.data.to_string()));
                            }
                        }
                    }
                }
                // Create snapshot events for entities without events
                let snapshot_count = needs_snapshot.len();
                for (eid, etype, data) in needs_snapshot {
                    let event = Event::full_snapshot(eid, peer_id, &etype, &data);
                    if let Err(e) = event_store.save_event(&event) {
                        eprintln!("[SYNC] Failed to create snapshot event for {}: {}", eid, e);
                    }
                }
                Ok::<(Vec<String>, usize), privstack_storage::StorageError>((ids, snapshot_count))
            }).await {
                Ok(Ok((ids, snapshot_count))) => {
                    let count = ids.len();
                    for id in ids {
                        if let Ok(eid) = id.parse::<EntityId>() {
                            self.shared_entities.insert(eid);
                        }
                    }
                    info!("[SYNC] Pre-loaded {} entities for sync ({} needed snapshot events)", count, snapshot_count);
                }
                Ok(Err(e)) => {
                    warn!("[SYNC] Failed to pre-load entities from store: {}", e);
                }
                Err(e) => {
                    warn!("[SYNC] spawn_blocking panicked loading entity IDs: {}", e);
                }
            }
        }

        info!("[SYNC] Orchestrator started for peer {}", self.engine.peer_id());

        loop {
            tokio::select! {
                Some(cmd) = command_rx.recv() => {
                    debug!("[SYNC] Received command: {:?}", cmd);
                    match cmd {
                        SyncCommand::Shutdown => {
                            info!("[SYNC] Orchestrator shutting down");
                            break;
                        }
                        SyncCommand::RecordLocalEvent { event } => {
                            self.handle_local_event(event).await;
                        }
                        SyncCommand::ShareEntity { entity_id } => {
                            self.shared_entities.insert(entity_id);
                            info!("[SYNC] Now sharing entity: {}", entity_id);
                        }
                        SyncCommand::ShareEntityWithPeer { entity_id, peer_id } => {
                            self.shared_entities.insert(entity_id);
                            if let Some(policy) = &self.personal_policy {
                                policy.share(entity_id, peer_id).await;
                                info!("[SYNC] Shared entity {} with peer {}", entity_id, peer_id);
                            } else {
                                info!("[SYNC] ShareEntityWithPeer ignored — no personal policy");
                            }
                        }
                        SyncCommand::SyncEntity { entity_id } => {
                            info!("[SYNC] SyncEntity command for {}", entity_id);
                            self.sync_entity_to_all(&transport, entity_id).await;
                        }
                        SyncCommand::SyncWithPeer { peer_id } => {
                            info!("[SYNC] SyncWithPeer command for {}", peer_id);
                            self.sync_with_peer(&transport, peer_id).await;
                        }
                    }
                }

                incoming = async {
                    let transport_guard = transport.lock().await;
                    transport_guard.recv_request().await
                } => {
                    if let Some(request) = incoming {
                        self.handle_incoming_request(&transport, request).await;
                    }
                }

                _ = discovery_interval.tick() => {
                    debug!("[SYNC] Discovery interval tick");
                    self.check_for_new_peers(&transport).await;
                }

                _ = sync_interval.tick() => {
                    debug!("[SYNC] Sync interval tick");
                    self.periodic_sync(&transport).await;
                }
            }
        }

        Ok(())
    }

    async fn handle_local_event(&self, event: Event) {
        // Save to event store on a blocking thread.
        // Note: the FFI layer also saves directly for immediacy; the duplicate
        // INSERT OR IGNORE here is harmless.
        let store = self.event_store.clone();
        let ev = event.clone();
        let save_result = tokio::task::spawn_blocking(move || store.save_event(&ev)).await;
        match save_result {
            Ok(Ok(())) => {}
            Ok(Err(e)) => {
                warn!("[SYNC] Failed to save local event: {}", e);
                return;
            }
            Err(e) => {
                warn!("[SYNC] spawn_blocking panicked saving local event: {}", e);
                return;
            }
        }

        // Invalidate the sync ledger for this entity so every peer re-checks it.
        // This handles the race where periodic_sync already marked the entity as
        // synced before this command was processed.
        let entity_store = self.entity_store.clone();
        let eid_str = event.entity_id.to_string();
        let _ = tokio::task::spawn_blocking(move || {
            entity_store.invalidate_sync_ledger_for_entity(&eid_str)
        }).await;

        // Track in sync state
        self.engine.record_local_event(&event).await;
        debug!("[SYNC] Recorded local event {:?} for entity {}", event.id, event.entity_id);
    }

    /// Checks whether a peer is trusted via the pairing manager.
    /// Returns `true` if no pairing manager is set (open mode) or if the peer is trusted.
    fn is_peer_trusted_sync(&self, peer_id: &PeerId) -> bool {
        match &self.pairing_manager {
            None => true,
            Some(pm) => {
                let pm = pm.lock().unwrap();
                pm.is_trusted(&peer_id.to_string())
            }
        }
    }

    async fn check_for_new_peers(&mut self, transport: &Arc<TokioMutex<dyn SyncTransport>>) {
        let transport_guard = transport.lock().await;
        let discovered = transport_guard.discovered_peers_async().await;
        drop(transport_guard);

        for peer in discovered {
            // Update device name in pairing manager whenever Identify provides one
            if let Some(ref name) = peer.device_name {
                if let Some(ref pm) = self.pairing_manager {
                    let mut pm = pm.lock().unwrap();
                    let peer_id_str = peer.peer_id.to_string();
                    if pm.update_device_name(&peer_id_str, name) {
                        info!("[SYNC] Updated device name for {}: {}", peer.peer_id, name);
                    }
                }
            }

            if self.synced_peers.contains(&peer.peer_id) {
                continue;
            }

            // Pairing gate: skip untrusted peers but register them for approval
            if !self.is_peer_trusted_sync(&peer.peer_id) {
                // Only show peers for approval if a sync code is set (user started pairing).
                // Without a sync code, mDNS discovers ALL PrivStack instances on the LAN
                // which would be confusing. The sync code gates who appears in the approval UI.
                if let Some(ref pm) = self.pairing_manager {
                    let mut pm = pm.lock().unwrap();
                    let has_sync_code = pm.current_code().is_some();
                    if has_sync_code {
                        let peer_id_str = peer.peer_id.to_string();
                        if pm.get_discovered_peer(&peer_id_str).is_none() {
                            use std::time::{SystemTime, UNIX_EPOCH};
                            let now = SystemTime::now()
                                .duration_since(UNIX_EPOCH)
                                .unwrap_or_default()
                                .as_secs();
                            pm.add_discovered_peer(crate::pairing::DiscoveredPeerInfo {
                                peer_id: peer_id_str,
                                device_name: peer.device_name.clone().unwrap_or_else(|| "Unknown Device".to_string()),
                                discovered_at: now,
                                status: crate::pairing::PairingStatus::PendingLocalApproval,
                                addresses: peer.addresses.clone(),
                            });
                            info!("[SYNC] Added untrusted peer {} to pairing manager for approval", peer.peer_id);
                        }
                    }
                }
                debug!("[SYNC] Skipping untrusted peer: {}", peer.peer_id);
                continue;
            }

            info!("[SYNC] New peer discovered: {} ({:?})", peer.peer_id, peer.device_name);

            let _ = self
                .event_tx
                .send(SyncEvent::PeerDiscovered {
                    peer_id: peer.peer_id,
                    device_name: peer.device_name.clone(),
                })
                .await;

            if self.config.auto_sync && !self.shared_entities.is_empty() {
                // Mark as synced so we don't re-emit PeerDiscovered or re-sync every tick.
                self.synced_peers.insert(peer.peer_id);
                let peer_id = peer.peer_id;
                self.sync_with_peer(transport, peer_id).await;
                return;
            }

            // No auto_sync or no entities yet — track as known so periodic_sync
            // picks up this peer once entities are shared.
            // Don't add to synced_peers yet: if auto_sync is enabled, we want
            // to re-check on the next discovery tick once entities exist.
            if !self.config.auto_sync {
                self.synced_peers.insert(peer.peer_id);
            }
        }
    }


    /// Full sync with a specific peer.
    /// Uses the sync_ledger table for per-entity tracking — survives restarts,
    /// handles chunking naturally, and scales to any number of peers.
    async fn sync_with_peer(&mut self, transport: &Arc<TokioMutex<dyn SyncTransport>>, peer_id: PeerId) {
        if self.shared_entities.is_empty() {
            debug!("[SYNC] No entities to sync");
            return;
        }

        let _ = self.event_tx.send(SyncEvent::SyncStarted { peer_id }).await;

        // Query the ledger for entities that need syncing with this peer.
        // Returns entities with no ledger entry (never synced) OR modified since last sync.
        let peer_id_str = peer_id.to_string();
        let store = self.entity_store.clone();
        let pid_str = peer_id_str.clone();
        let all_needing_sync = match tokio::task::spawn_blocking(move || {
            store.entities_needing_sync(&pid_str)
        }).await {
            Ok(Ok(ids)) => ids,
            Ok(Err(e)) => {
                warn!("[SYNC] Failed to query sync ledger: {}", e);
                Vec::new()
            }
            Err(e) => {
                warn!("[SYNC] spawn_blocking panicked querying sync ledger: {}", e);
                Vec::new()
            }
        };

        // Parse to EntityIds
        let mut entity_ids: Vec<EntityId> = all_needing_sync.iter()
            .filter_map(|id| id.parse::<EntityId>().ok())
            .collect();

        // Per-peer entity filtering: if selective sharing is configured,
        // only sync entities explicitly shared with this peer.
        if let Some(policy) = &self.personal_policy {
            if policy.has_selective_sharing().await {
                let peer_entities: HashSet<EntityId> =
                    policy.shared_entities(&peer_id).await.into_iter().collect();
                entity_ids.retain(|eid| peer_entities.contains(eid));
            }
        }

        if entity_ids.is_empty() {
            debug!("[SYNC] No changed entities to sync with {}", peer_id);
            let _ = self.event_tx.send(SyncEvent::SyncCompleted {
                peer_id,
                events_sent: 0,
                events_received: 0,
            }).await;
            return;
        }

        // Chunk large syncs. The ledger tracks per-entity, so remaining
        // entities will be picked up on the next cycle automatically.
        let total_needing_sync = entity_ids.len();
        if self.config.max_entities_per_sync > 0 && entity_ids.len() > self.config.max_entities_per_sync {
            entity_ids.truncate(self.config.max_entities_per_sync);
            info!("[SYNC] Chunked sync: processing {}/{} entities this cycle", entity_ids.len(), total_needing_sync);
        } else if total_needing_sync > 0 {
            info!("[SYNC] Syncing {} entities with peer {}", total_needing_sync, peer_id);
        }

        let mut events_sent = 0;
        let mut events_received = 0;

        // Step 1: Handshake
        let hello = self.engine.make_hello(entity_ids.clone());
        info!("[SYNC] Sending Hello to peer {} with {} entities", peer_id, entity_ids.len());

        let hello_response = {
            let tg = transport.lock().await;
            tg.send_request(&peer_id, hello).await
        };

        match hello_response {
            Ok(SyncMessage::HelloAck(ack)) => {
                if !ack.accepted {
                    warn!("[SYNC] Peer {} rejected: {:?}", peer_id, ack.reason);
                    let _ = self.event_tx.send(SyncEvent::SyncFailed {
                        peer_id,
                        error: ack.reason.unwrap_or_else(|| "rejected".to_string()),
                    }).await;
                    return;
                }
                if ack.version != PROTOCOL_VERSION {
                    warn!("[SYNC] Version mismatch with peer {}", peer_id);
                    let _ = self.event_tx.send(SyncEvent::SyncFailed {
                        peer_id,
                        error: format!("version mismatch: expected {PROTOCOL_VERSION}, got {}", ack.version),
                    }).await;
                    return;
                }
                info!("[SYNC] Handshake accepted by peer {} ({})", peer_id, ack.device_name);
            }
            Ok(other) => {
                warn!("[SYNC] Unexpected response to Hello: {:?}", other);
                let _ = self.event_tx.send(SyncEvent::SyncFailed {
                    peer_id,
                    error: "unexpected response to Hello".to_string(),
                }).await;
                return;
            }
            Err(e) => {
                warn!("[SYNC] Failed to send Hello to peer {}: {}", peer_id, e);
                let _ = self.event_tx.send(SyncEvent::SyncFailed {
                    peer_id,
                    error: e.to_string(),
                }).await;
                return;
            }
        }

        // Step 2: Request their sync state (include our known event IDs for bidirectional sync)
        let sync_req = self.engine.make_sync_request(entity_ids.clone(), &self.event_store).await;
        let state_response = {
            let tg = transport.lock().await;
            tg.send_request(&peer_id, sync_req).await
        };

        let peer_state: SyncStateMessage = match state_response {
            Ok(SyncMessage::SyncState(state)) => {
                info!("[SYNC] Received sync state from peer {} for {} entities", peer_id, state.clocks.len());
                state
            }
            Ok(other) => {
                warn!("[SYNC] Unexpected response to SyncRequest: {:?}", other);
                SyncStateMessage::new()
            }
            Err(e) => {
                warn!("[SYNC] Failed to get sync state from peer {}: {}", peer_id, e);
                SyncStateMessage::new()
            }
        };

        // Step 3: For each entity, compute and send missing events.
        // Track successfully synced entities to update the ledger.
        let mut entities_skipped = 0usize;
        let mut synced_entity_ids: Vec<String> = Vec::new();
        let now_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;

        for eid in &entity_ids {
            // Use the peer's known event IDs from their SyncState for exact delta.
            let peer_known_ids: HashSet<EventId> = peer_state
                .known_event_ids
                .get(eid)
                .map(|ids| ids.iter().copied().collect())
                .unwrap_or_default();

            let batches = self.engine.compute_event_batches_for_peer(
                &peer_id,
                *eid,
                &peer_known_ids,
                &self.event_store,
            ).await;

            // Skip round trip if we have nothing to send AND the peer has no
            // unknown events for this entity (nothing for a reverse-delta either).
            if batches.is_empty() {
                let our_known = self.engine.known_event_ids_from_store(eid, &self.event_store).await;
                let our_set: HashSet<EventId> = our_known.into_iter().collect();
                let peer_has_unknown = peer_known_ids.iter().any(|id| !our_set.contains(id));
                if !peer_has_unknown {
                    // Both sides agree. But only mark as synced if we actually HAVE events.
                    // If neither side has events, a snapshot might still be pending (debounce).
                    // Don't write a ledger entry — let the entity come back next cycle
                    // once the snapshot event exists.
                    if !our_set.is_empty() {
                        synced_entity_ids.push(eid.to_string());
                    }
                    entities_skipped += 1;
                    continue;
                }
                // Peer has events we don't — send empty batch to trigger reverse delta
            }

            let batches = if batches.is_empty() {
                vec![SyncMessage::EventBatch(
                    crate::protocol::EventBatchMessage {
                        entity_id: *eid,
                        events: Vec::new(),
                        is_final: true,
                        batch_seq: 0,
                    },
                )]
            } else {
                batches
            };

            let mut entity_synced = true;
            for batch_msg in batches {
                let batch_response = {
                    let tg = transport.lock().await;
                    tg.send_request(&peer_id, batch_msg).await
                };

                match batch_response {
                    Ok(SyncMessage::EventAck(ack)) => {
                        events_sent += ack.received_count;

                        // Handle bidirectional events from the ack
                        for event in &ack.events {
                            match self.apply_remote_event(&peer_id, event).await {
                                Ok(true) => events_received += 1,
                                Ok(false) => {}
                                Err(e) => warn!("[SYNC] Failed to apply event from ack: {}", e),
                            }
                        }
                    }
                    Ok(other) => {
                        warn!("[SYNC] Unexpected response to EventBatch: {:?}", other);
                        entity_synced = false;
                    }
                    Err(e) => {
                        error!("[SYNC] Failed to send events to peer {}: {}", peer_id, e);
                        entity_synced = false;
                    }
                }
            }

            if entity_synced {
                synced_entity_ids.push(eid.to_string());
            }
        }

        self.synced_peers.insert(peer_id);

        // Batch-update the sync ledger for all successfully synced entities
        if !synced_entity_ids.is_empty() {
            let store = self.entity_store.clone();
            let pid = peer_id_str.clone();
            let ids = synced_entity_ids.clone();
            let ledger_count = ids.len();
            if let Err(e) = tokio::task::spawn_blocking(move || {
                store.mark_entities_synced(&pid, &ids, now_ms)
            }).await {
                warn!("[SYNC] Failed to update sync ledger: {}", e);
            } else {
                debug!("[SYNC] Updated sync ledger: {} entities marked synced with {}", ledger_count, peer_id);
            }
        }

        // Update last_synced on TrustedPeer for UI display purposes
        if let Some(ref pm) = self.pairing_manager {
            let mut pm = pm.lock().unwrap();
            pm.mark_peer_synced(&peer_id.to_string());
        }

        let _ = self.event_tx.send(SyncEvent::SyncCompleted {
            peer_id,
            events_sent,
            events_received,
        }).await;

        info!(
            "[SYNC] Sync with peer {} complete: sent={}, received={}, synced={}, skipped={}, remaining={}",
            peer_id, events_sent, events_received, synced_entity_ids.len(), entities_skipped,
            total_needing_sync.saturating_sub(entity_ids.len())
        );
    }

    /// Applies a remote event (from sync) to local stores.
    /// The `sender` is the peer that sent us this event, used for policy gating.
    async fn apply_remote_event(&self, sender: &PeerId, event: &Event) -> Result<bool, String> {
        // Policy gate: check if we should accept this event from sender
        match self
            .engine
            .policy()
            .on_event_receive(sender, &event.entity_id, &[event.clone()])
            .await
        {
            Ok(allowed) if allowed.is_empty() => {
                debug!(
                    "[SYNC] Policy filtered event {:?} from peer {} for entity {}",
                    event.id, sender, event.entity_id
                );
                return Ok(false);
            }
            Err(e) => {
                warn!(
                    "[SYNC] Policy denied event {:?} from peer {}: {}",
                    event.id, sender, e
                );
                return Ok(false);
            }
            _ => {}
        }

        let peer_id = self.engine.peer_id();
        let es = self.entity_store.clone();
        let ev = event.clone();

        let apply_result = tokio::task::spawn_blocking(move || {
            let applicator = crate::applicator::EventApplicator::new(peer_id);
            applicator.apply_event(&ev, &es, None, None)
        })
        .await
        .map_err(|e| format!("spawn_blocking panicked: {e}"))?;

        match apply_result {
            Ok(was_applied) => {
                if was_applied {
                    self.engine.record_local_event(event).await;

                    let evs = self.event_store.clone();
                    let ev = event.clone();
                    let save_result = tokio::task::spawn_blocking(move || evs.save_event(&ev)).await;
                    match save_result {
                        Ok(Err(e)) => warn!("[SYNC] Failed to save event to store: {}", e),
                        Err(e) => warn!("[SYNC] spawn_blocking panicked saving event: {}", e),
                        _ => {}
                    }

                    // Invalidate sync ledger so this event propagates to other peers.
                    let entity_store = self.entity_store.clone();
                    let eid_str = event.entity_id.to_string();
                    let _ = tokio::task::spawn_blocking(move || {
                        entity_store.invalidate_sync_ledger_for_entity(&eid_str)
                    }).await;

                    let _ = self.event_tx.send(SyncEvent::EntityUpdated {
                        entity_id: event.entity_id,
                    }).await;
                }
                Ok(was_applied)
            }
            Err(e) => Err(e.to_string()),
        }
    }

    async fn sync_entity_to_all(&mut self, transport: &Arc<TokioMutex<dyn SyncTransport>>, _entity_id: EntityId) {
        let peers: Vec<PeerId> = self.synced_peers.iter().copied().collect();
        for peer_id in peers {
            self.sync_with_peer(transport, peer_id).await;
        }
    }

    async fn periodic_sync(&mut self, transport: &Arc<TokioMutex<dyn SyncTransport>>) {
        if self.synced_peers.is_empty() {
            return;
        }
        let peers: Vec<PeerId> = self.synced_peers.iter().copied().collect();
        for peer_id in peers {
            self.sync_with_peer(transport, peer_id).await;
        }
    }

    /// Handles an incoming request from the transport layer.
    async fn handle_incoming_request(
        &mut self,
        transport: &Arc<TokioMutex<dyn SyncTransport>>,
        request: crate::transport::IncomingSyncRequest,
    ) {
        let peer_id = request.peer_id;
        info!("[SYNC] Received incoming request from peer {}", peer_id);

        let response = match request.message {
            SyncMessage::Hello(ref hello) => {
                info!("[SYNC] Received Hello from {} ({})", hello.peer_id, hello.device_name);
                self.engine.handle_hello(hello).await
            }

            SyncMessage::SyncRequest(ref req) => {
                info!("[SYNC] Received SyncRequest for {} entities", req.entity_ids.len());
                self.engine.handle_sync_request(&peer_id, req, &self.event_store).await
            }

            SyncMessage::EventBatch(ref batch) => {
                info!("[SYNC] Received {} events for entity {} from peer {}", batch.events.len(), batch.entity_id, peer_id);
                let (ack, updated_entities) = self.engine.handle_event_batch(
                    &peer_id,
                    batch,
                    &self.entity_store,
                    &self.event_store,
                ).await;

                for eid in &updated_entities {
                    // Invalidate sync ledger so received events propagate to other peers.
                    // EventApplicator sets modified_at to the event's wall_time which may
                    // be older than the sync ledger's synced_at, so explicit invalidation
                    // is needed to ensure forwarding.
                    let entity_store = self.entity_store.clone();
                    let eid_str = eid.to_string();
                    let _ = tokio::task::spawn_blocking(move || {
                        entity_store.invalidate_sync_ledger_for_entity(&eid_str)
                    }).await;

                    let _ = self.event_tx.send(SyncEvent::EntityUpdated {
                        entity_id: *eid,
                    }).await;
                }

                info!("[SYNC] Processed events from peer {}", peer_id);
                ack
            }

            other => {
                warn!("[SYNC] Unexpected message type: {:?}", other);
                SyncMessage::Error(ErrorMessage::new(1, "unexpected message type"))
            }
        };

        let transport_guard = transport.lock().await;
        if let Err(e) = transport_guard.send_response(request.response_token, response).await {
            warn!("[SYNC] Failed to send response: {}", e);
        }
    }
}

/// Creates an orchestrator and returns the pieces needed to run it.
pub fn create_orchestrator(
    peer_id: PeerId,
    entity_store: Arc<EntityStore>,
    event_store: Arc<EventStore>,
    config: OrchestratorConfig,
) -> (
    OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncCommand>,
    SyncOrchestrator,
) {
    create_orchestrator_with_policy(
        peer_id,
        entity_store,
        event_store,
        config,
        Arc::new(crate::policy::AllowAllPolicy),
    )
}

/// Creates an orchestrator with a custom sync policy.
pub fn create_orchestrator_with_policy(
    peer_id: PeerId,
    entity_store: Arc<EntityStore>,
    event_store: Arc<EventStore>,
    config: OrchestratorConfig,
    policy: Arc<dyn SyncPolicy>,
) -> (
    OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncCommand>,
    SyncOrchestrator,
) {
    let engine = SyncEngine::with_policy(peer_id, SyncConfig::default(), policy);
    let (command_tx, command_rx) = mpsc::channel(32);
    let (event_tx, event_rx) = mpsc::channel(64);

    let handle = OrchestratorHandle {
        command_tx: command_tx.clone(),
    };

    let orchestrator = SyncOrchestrator {
        engine,
        entity_store,
        event_store,
        config,
        shared_entities: HashSet::new(),
        synced_peers: HashSet::new(),
        event_tx,
        pairing_manager: None,
        personal_policy: None,
    };

    (handle, event_rx, command_rx, orchestrator)
}

/// Creates an orchestrator with AllowAllPolicy + pairing gate.
/// Discovered peers must be trusted in the pairing manager before syncing.
pub fn create_orchestrator_with_pairing(
    peer_id: PeerId,
    entity_store: Arc<EntityStore>,
    event_store: Arc<EventStore>,
    config: OrchestratorConfig,
    pairing_manager: Arc<std::sync::Mutex<PairingManager>>,
) -> (
    OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncCommand>,
    SyncOrchestrator,
) {
    let engine = SyncEngine::with_policy(
        peer_id,
        SyncConfig::default(),
        Arc::new(crate::policy::AllowAllPolicy),
    );
    let (command_tx, command_rx) = mpsc::channel(32);
    let (event_tx, event_rx) = mpsc::channel(64);

    let handle = OrchestratorHandle {
        command_tx: command_tx.clone(),
    };

    let orchestrator = SyncOrchestrator {
        engine,
        entity_store,
        event_store,
        config,
        shared_entities: HashSet::new(),
        synced_peers: HashSet::new(),
        event_tx,
        pairing_manager: Some(pairing_manager),
        personal_policy: None,
    };

    (handle, event_rx, command_rx, orchestrator)
}

/// Creates an orchestrator with a PersonalSyncPolicy + pairing gate.
/// Combines per-peer entity sharing with trusted-peer discovery gating.
pub fn create_personal_orchestrator(
    peer_id: PeerId,
    entity_store: Arc<EntityStore>,
    event_store: Arc<EventStore>,
    config: OrchestratorConfig,
    policy: Arc<PersonalSyncPolicy>,
    pairing_manager: Arc<std::sync::Mutex<PairingManager>>,
) -> (
    OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncCommand>,
    SyncOrchestrator,
) {
    let engine = SyncEngine::with_policy(peer_id, SyncConfig::default(), policy.clone());
    let (command_tx, command_rx) = mpsc::channel(32);
    let (event_tx, event_rx) = mpsc::channel(64);

    let handle = OrchestratorHandle {
        command_tx: command_tx.clone(),
    };

    let orchestrator = SyncOrchestrator {
        engine,
        entity_store,
        event_store,
        config,
        shared_entities: HashSet::new(),
        synced_peers: HashSet::new(),
        event_tx,
        pairing_manager: Some(pairing_manager),
        personal_policy: Some(policy),
    };

    (handle, event_rx, command_rx, orchestrator)
}

/// Creates an orchestrator wired for enterprise use with ACL-as-CRDT event handling.
/// Attaches an `AclApplicator` to the engine so ACL events are applied to the policy.
pub fn create_enterprise_orchestrator(
    peer_id: PeerId,
    entity_store: Arc<EntityStore>,
    event_store: Arc<EventStore>,
    config: OrchestratorConfig,
    policy: Arc<crate::policy::EnterpriseSyncPolicy>,
) -> (
    OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncCommand>,
    SyncOrchestrator,
) {
    let mut engine = SyncEngine::with_policy(peer_id, SyncConfig::default(), policy.clone());

    let acl_applicator = Arc::new(crate::acl_applicator::AclApplicator::new(policy));
    engine.set_acl_handler(acl_applicator);

    let (command_tx, command_rx) = mpsc::channel(32);
    let (event_tx, event_rx) = mpsc::channel(64);

    let handle = OrchestratorHandle {
        command_tx: command_tx.clone(),
    };

    let orchestrator = SyncOrchestrator {
        engine,
        entity_store,
        event_store,
        config,
        shared_entities: HashSet::new(),
        synced_peers: HashSet::new(),
        event_tx,
        pairing_manager: None,
        personal_policy: None,
    };

    (handle, event_rx, command_rx, orchestrator)
}
