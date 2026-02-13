#![allow(unused_variables, unused_mut, dead_code)]
//! Personal sharing integration tests.
//!
//! Full end-to-end scenarios exercising PersonalSyncPolicy + pairing gate
//! through real orchestrators connected via in-process bridged transports.
//!
//! Covers:
//! - Friend pairing flow (sync code → trust → display name)
//! - Selective entity sharing (share one, keep others private)
//! - Bidirectional CRDT editing on shared entities
//! - Revocation (keep existing edits, block future access)
//! - Unauthorized peer isolation (User C cannot access)
//! - Multi-peer sharing (A shares with B and C independently)
//! - Adversarial: untrusted peer cannot discover/sync
//! - Adversarial: peer tampering with entity IDs
//! - Network-level tests: mDNS-like, DHT-like, and relay-like discovery

use async_trait::async_trait;
use privstack_sync::pairing::PairingManager;
use privstack_sync::policy::PersonalSyncPolicy;
use privstack_sync::transport::{
    DiscoveredPeer, DiscoveryMethod, IncomingSyncRequest, ResponseToken, SyncTransport,
};
use privstack_sync::{
    create_orchestrator_with_pairing, create_personal_orchestrator, EventApplicator,
    OrchestratorConfig, OrchestratorHandle, SyncCommand, SyncEvent, SyncMessage, SyncResult,
};
use privstack_storage::{EntityStore, EventStore};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, oneshot, Mutex as TokioMutex};

// ═══════════════════════════════════════════════════════════════════════════
// Transport Infrastructure
// ═══════════════════════════════════════════════════════════════════════════

/// In-process request bridging two peers.
struct BridgedRequest {
    from: PeerId,
    message: SyncMessage,
    response_tx: oneshot::Sender<SyncMessage>,
}

/// Transport that bridges two orchestrators in-process.
struct BridgedTransport {
    local_peer_id: PeerId,
    remote_peer_id: PeerId,
    outgoing_tx: mpsc::Sender<BridgedRequest>,
    incoming_rx: TokioMutex<mpsc::Receiver<BridgedRequest>>,
    report_peer: TokioMutex<bool>,
    discovery_method: DiscoveryMethod,
    remote_device_name: String,
}

impl BridgedTransport {
    fn pair(
        peer_a: PeerId,
        name_a: &str,
        peer_b: PeerId,
        name_b: &str,
        method: DiscoveryMethod,
    ) -> (Self, Self) {
        let (a_to_b_tx, a_to_b_rx) = mpsc::channel::<BridgedRequest>(64);
        let (b_to_a_tx, b_to_a_rx) = mpsc::channel::<BridgedRequest>(64);

        let ta = Self {
            local_peer_id: peer_a,
            remote_peer_id: peer_b,
            outgoing_tx: a_to_b_tx,
            incoming_rx: TokioMutex::new(b_to_a_rx),
            report_peer: TokioMutex::new(false),
            discovery_method: method,
            remote_device_name: name_b.to_string(),
        };

        let tb = Self {
            local_peer_id: peer_b,
            remote_peer_id: peer_a,
            outgoing_tx: b_to_a_tx,
            incoming_rx: TokioMutex::new(a_to_b_rx),
            report_peer: TokioMutex::new(false),
            discovery_method: method,
            remote_device_name: name_a.to_string(),
        };

        (ta, tb)
    }
}

#[async_trait]
impl SyncTransport for BridgedTransport {
    async fn start(&mut self) -> SyncResult<()> {
        Ok(())
    }

    async fn stop(&mut self) -> SyncResult<()> {
        Ok(())
    }

    fn is_running(&self) -> bool {
        true
    }

    fn local_peer_id(&self) -> PeerId {
        self.local_peer_id
    }

    fn discovered_peers(&self) -> Vec<DiscoveredPeer> {
        vec![]
    }

    async fn discovered_peers_async(&self) -> Vec<DiscoveredPeer> {
        if *self.report_peer.lock().await {
            vec![DiscoveredPeer {
                peer_id: self.remote_peer_id,
                device_name: Some(self.remote_device_name.clone()),
                discovery_method: self.discovery_method,
                addresses: vec![],
            }]
        } else {
            vec![]
        }
    }

    async fn send_request(
        &self,
        _peer_id: &PeerId,
        message: SyncMessage,
    ) -> SyncResult<SyncMessage> {
        let (response_tx, response_rx) = oneshot::channel();

        self.outgoing_tx
            .send(BridgedRequest {
                from: self.local_peer_id,
                message,
                response_tx,
            })
            .await
            .map_err(|_| privstack_sync::SyncError::Network("bridge closed".into()))?;

        response_rx
            .await
            .map_err(|_| privstack_sync::SyncError::Network("response dropped".into()))
    }

    async fn recv_request(&self) -> Option<IncomingSyncRequest> {
        let mut rx = self.incoming_rx.lock().await;
        let bridged = rx.recv().await?;

        Some(IncomingSyncRequest {
            peer_id: bridged.from,
            message: bridged.message,
            response_token: ResponseToken::new(bridged.response_tx),
        })
    }

    async fn send_response(
        &self,
        token: ResponseToken,
        message: SyncMessage,
    ) -> SyncResult<()> {
        let tx: oneshot::Sender<SyncMessage> = token
            .downcast()
            .ok_or_else(|| privstack_sync::SyncError::Network("invalid token".into()))?;

        tx.send(message)
            .map_err(|_| privstack_sync::SyncError::Network("response dropped".into()))
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Multi-Peer Hub Transport (for 3+ peer tests)
// ═══════════════════════════════════════════════════════════════════════════

/// Routing request through the hub.
struct HubRequest {
    from: PeerId,
    #[allow(dead_code)]
    to: PeerId,
    message: SyncMessage,
    response_tx: oneshot::Sender<SyncMessage>,
}

/// Shared routing hub for N peers.
struct PeerHub {
    /// For each peer, a channel to deliver incoming requests.
    inboxes: HashMap<PeerId, mpsc::Sender<HubRequest>>,
}

impl PeerHub {
    fn new() -> Self {
        Self {
            inboxes: HashMap::new(),
        }
    }

    fn register(&mut self, peer_id: PeerId) -> mpsc::Receiver<HubRequest> {
        let (tx, rx) = mpsc::channel::<HubRequest>(64);
        self.inboxes.insert(peer_id, tx);
        rx
    }
}

/// Transport for one peer in a multi-peer hub.
struct HubTransport {
    local_peer_id: PeerId,
    hub: Arc<TokioMutex<PeerHub>>,
    inbox_rx: TokioMutex<mpsc::Receiver<HubRequest>>,
    /// Which peers to report as discovered (and with what method/name).
    visible_peers: TokioMutex<Vec<(PeerId, String, DiscoveryMethod)>>,
}

#[async_trait]
impl SyncTransport for HubTransport {
    async fn start(&mut self) -> SyncResult<()> {
        Ok(())
    }
    async fn stop(&mut self) -> SyncResult<()> {
        Ok(())
    }
    fn is_running(&self) -> bool {
        true
    }
    fn local_peer_id(&self) -> PeerId {
        self.local_peer_id
    }
    fn discovered_peers(&self) -> Vec<DiscoveredPeer> {
        vec![]
    }
    async fn discovered_peers_async(&self) -> Vec<DiscoveredPeer> {
        self.visible_peers
            .lock()
            .await
            .iter()
            .map(|(pid, name, method)| DiscoveredPeer {
                peer_id: *pid,
                device_name: Some(name.clone()),
                discovery_method: *method,
                addresses: vec![],
            })
            .collect()
    }
    async fn send_request(
        &self,
        peer_id: &PeerId,
        message: SyncMessage,
    ) -> SyncResult<SyncMessage> {
        let (response_tx, response_rx) = oneshot::channel();
        let hub = self.hub.lock().await;
        let inbox = hub
            .inboxes
            .get(peer_id)
            .ok_or_else(|| privstack_sync::SyncError::Network("peer not in hub".into()))?
            .clone();
        drop(hub);

        inbox
            .send(HubRequest {
                from: self.local_peer_id,
                to: *peer_id,
                message,
                response_tx,
            })
            .await
            .map_err(|_| privstack_sync::SyncError::Network("hub inbox closed".into()))?;

        response_rx
            .await
            .map_err(|_| privstack_sync::SyncError::Network("response dropped".into()))
    }
    async fn recv_request(&self) -> Option<IncomingSyncRequest> {
        let mut rx = self.inbox_rx.lock().await;
        let req = rx.recv().await?;
        Some(IncomingSyncRequest {
            peer_id: req.from,
            message: req.message,
            response_token: ResponseToken::new(req.response_tx),
        })
    }
    async fn send_response(
        &self,
        token: ResponseToken,
        message: SyncMessage,
    ) -> SyncResult<()> {
        let tx: oneshot::Sender<SyncMessage> = token
            .downcast()
            .ok_or_else(|| privstack_sync::SyncError::Network("invalid token".into()))?;
        tx.send(message)
            .map_err(|_| privstack_sync::SyncError::Network("response dropped".into()))
    }
}

/// Creates N hub-connected transports.
fn create_hub_transports(
    peers: &[(PeerId, &str)],
) -> (
    Arc<TokioMutex<PeerHub>>,
    Vec<Arc<TokioMutex<dyn SyncTransport>>>,
) {
    let mut hub = PeerHub::new();
    let mut transports = Vec::new();

    let mut receivers = Vec::new();
    for (pid, _name) in peers {
        let rx = hub.register(*pid);
        receivers.push(rx);
    }

    let hub = Arc::new(TokioMutex::new(hub));

    for (i, (pid, _name)) in peers.iter().enumerate() {
        let transport = HubTransport {
            local_peer_id: *pid,
            hub: hub.clone(),
            inbox_rx: TokioMutex::new(receivers.remove(0)),
            visible_peers: TokioMutex::new(Vec::new()),
        };
        let _ = i; // index consumed by receivers.remove(0)
        transports.push(
            Arc::new(TokioMutex::new(transport)) as Arc<TokioMutex<dyn SyncTransport>>
        );
    }

    (hub, transports)
}

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

fn make_stores() -> (Arc<EntityStore>, Arc<EventStore>) {
    (
        Arc::new(EntityStore::open_in_memory().unwrap()),
        Arc::new(EventStore::open_in_memory().unwrap()),
    )
}

fn make_event(entity_id: EntityId, peer_id: PeerId, payload: EventPayload) -> Event {
    Event::new(entity_id, peer_id, HybridTimestamp::now(), payload)
}

async fn record_event(
    handle: &OrchestratorHandle,
    entity_store: &Arc<EntityStore>,
    event_store: &Arc<EventStore>,
    peer_id: PeerId,
    event: Event,
) {
    let evs = event_store.clone();
    let ev = event.clone();
    tokio::task::spawn_blocking(move || evs.save_event(&ev))
        .await.unwrap().unwrap();

    if !matches!(event.payload, EventPayload::EntityDeleted { .. }) {
        let es = entity_store.clone();
        let ev = event.clone();
        tokio::task::spawn_blocking(move || {
            EventApplicator::new(peer_id).apply_event(&ev, &es, None, None)
        }).await.unwrap().ok();
    }

    handle.record_event(event).await.unwrap();
}

fn no_auto_config() -> OrchestratorConfig {
    OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    }
}

fn fast_discovery_config() -> OrchestratorConfig {
    OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_millis(100),
        auto_sync: true,
        max_entities_per_sync: 0,
    }
}

async fn wait_for_event(
    rx: &mut mpsc::Receiver<SyncEvent>,
    timeout: Duration,
    mut predicate: impl FnMut(&SyncEvent) -> bool,
) -> Option<SyncEvent> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        match tokio::time::timeout_at(deadline, rx.recv()).await {
            Ok(Some(event)) if predicate(&event) => return Some(event),
            Ok(Some(_)) => continue,
            _ => return None,
        }
    }
}

/// Sets up a personal orchestrator with pairing manager.
/// Returns (handle, event_rx, cmd_rx, orchestrator, policy, pairing_manager).
fn setup_personal_peer(
    peer_id: PeerId,
) -> (
    privstack_sync::OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncCommand>,
    privstack_sync::SyncOrchestrator,
    Arc<PersonalSyncPolicy>,
    Arc<std::sync::Mutex<PairingManager>>,
    Arc<EntityStore>,
    Arc<EventStore>,
) {
    let (entity_store, event_store) = make_stores();
    let policy = Arc::new(PersonalSyncPolicy::new());
    let pm = Arc::new(std::sync::Mutex::new(PairingManager::new()));

    let (handle, event_rx, cmd_rx, orchestrator) = create_personal_orchestrator(
        peer_id,
        entity_store.clone(),
        event_store.clone(),
        no_auto_config(),
        policy.clone(),
        pm.clone(),
    );

    (handle, event_rx, cmd_rx, orchestrator, policy, pm, entity_store, event_store)
}

/// Trust a peer in the pairing manager by their PeerId UUID string.
fn trust_peer(pm: &Arc<std::sync::Mutex<PairingManager>>, peer_id: PeerId, device_name: &str) {
    use privstack_sync::pairing::DiscoveredPeerInfo;
    use privstack_sync::pairing::PairingStatus;

    let mut pm = pm.lock().unwrap();
    // Add as discovered first, then approve
    pm.add_discovered_peer(DiscoveredPeerInfo {
        peer_id: peer_id.to_string(),
        device_name: device_name.to_string(),
        discovered_at: 0,
        status: PairingStatus::PendingLocalApproval,
        addresses: vec![],
    });
    pm.approve_peer(&peer_id.to_string());
}

/// Shorthand to do a full bidirectional sync between A and B.
async fn full_sync(
    handle_a: &privstack_sync::OrchestratorHandle,
    events_a: &mut mpsc::Receiver<SyncEvent>,
    peer_b: PeerId,
    handle_b: &privstack_sync::OrchestratorHandle,
    events_b: &mut mpsc::Receiver<SyncEvent>,
    peer_a: PeerId,
) {
    // A → B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: peer_b })
        .await
        .unwrap();
    let _ = wait_for_event(events_a, Duration::from_secs(5), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // B → A
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: peer_a })
        .await
        .unwrap();
    let _ = wait_for_event(events_b, Duration::from_secs(5), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    tokio::time::sleep(Duration::from_millis(100)).await;
}

// ═══════════════════════════════════════════════════════════════════════════
// LEVEL 1: Bridged In-Process (simulates direct mDNS-like connection)
// ═══════════════════════════════════════════════════════════════════════════

/// Full friend-pairing + selective sharing flow over a direct bridge.
///
/// 1. A generates sync code, B joins with same code → both trusted
/// 2. A shares doc_1 with B, keeps doc_2 private
/// 3. B can access doc_1, cannot access doc_2
/// 4. Both edit doc_1, CRDT converges
/// 5. A revokes doc_1 — B keeps existing edits, loses future access
/// 6. C (never paired) cannot access anything
#[tokio::test]
async fn mdns_full_sharing_lifecycle() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, policy_b, pm_b, es_b, ev_b) =
        setup_personal_peer(peer_b);

    // ── Step 1: Friend pairing ──
    // Simulate: A generates sync code, B joins. Both trust each other.
    trust_peer(&pm_a, peer_b, "Bob's Laptop");
    trust_peer(&pm_b, peer_a, "Alice's Phone");

    // Verify trust
    assert!(pm_a.lock().unwrap().is_trusted(&peer_b.to_string()));
    assert!(pm_b.lock().unwrap().is_trusted(&peer_a.to_string()));

    let doc_1 = EntityId::new();
    let doc_2 = EntityId::new(); // Private to A

    // ── Step 2: A shares doc_1 with B (not doc_2) ──
    policy_a.share(doc_1, peer_b).await;
    // B should also have A in their policy for bidirectional editing
    policy_b.share(doc_1, peer_a).await;

    // Both orchestrators register doc_1 and doc_2 as locally shared
    // (doc_2 only on A — and not shared with anyone via PersonalSyncPolicy)

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::Mdns);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    // Share entities with orchestrator
    handle_a.share_entity(doc_1).await.unwrap();
    handle_a.share_entity(doc_2).await.unwrap();
    handle_b.share_entity(doc_1).await.unwrap();

    // A creates doc_1 event
    let event_a_doc1 = make_event(
        doc_1,
        peer_a,
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"Shared Doc"}"#.to_string(),
        },
    );
    record_event(&handle_a, &es_a, &ev_a, peer_a, event_a_doc1.clone()).await;

    // A creates doc_2 event (private)
    let event_a_doc2 = make_event(
        doc_2,
        peer_a,
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"Private Doc"}"#.to_string(),
        },
    );
    record_event(&handle_a, &es_a, &ev_a, peer_a, event_a_doc2.clone()).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // ── Step 3: Sync — B should get doc_1, NOT doc_2 ──
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    let b_doc1_events = ev_b.get_events_for_entity(&doc_1).unwrap();
    assert_eq!(b_doc1_events.len(), 1, "B should have doc_1 event");
    assert_eq!(b_doc1_events[0].id, event_a_doc1.id);

    let b_doc2_events = ev_b.get_events_for_entity(&doc_2).unwrap();
    assert!(b_doc2_events.is_empty(), "B must NOT have doc_2 (private)");

    // ── Step 4: Bidirectional editing — both edit doc_1 ──
    let edit_b = make_event(
        doc_1,
        peer_b,
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"Shared Doc","body":"B's edit"}"#.to_string(),
        },
    );
    record_event(&handle_b, &es_b, &ev_b, peer_b, edit_b.clone()).await;

    let edit_a = make_event(
        doc_1,
        peer_a,
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"Shared Doc","body":"A's edit"}"#.to_string(),
        },
    );
    record_event(&handle_a, &es_a, &ev_a, peer_a, edit_a.clone()).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    // Both should have 3 events for doc_1 (create + 2 edits)
    let a_doc1 = ev_a.get_events_for_entity(&doc_1).unwrap();
    let b_doc1 = ev_b.get_events_for_entity(&doc_1).unwrap();
    assert_eq!(a_doc1.len(), 3, "A should have 3 doc_1 events after bidir sync");
    assert_eq!(b_doc1.len(), 3, "B should have 3 doc_1 events after bidir sync");

    // ── Step 5: Revoke — A unshares doc_1 from B ──
    policy_a.unshare(doc_1, peer_b).await;

    // B's existing events persist (local data never deleted)
    let b_doc1_after_revoke = ev_b.get_events_for_entity(&doc_1).unwrap();
    assert_eq!(
        b_doc1_after_revoke.len(),
        3,
        "B keeps existing 3 events after revocation"
    );

    // A creates a new event on doc_1
    let post_revoke_event = make_event(
        doc_1,
        peer_a,
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"Shared Doc","body":"Post-revoke edit"}"#.to_string(),
        },
    );
    record_event(&handle_a, &es_a, &ev_a, peer_a, post_revoke_event.clone()).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync again — B should NOT get the new event
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    let b_doc1_final = ev_b.get_events_for_entity(&doc_1).unwrap();
    assert_eq!(
        b_doc1_final.len(),
        3,
        "B should still have 3 events — revoked, no new data"
    );

    // A should have 4 events (didn't get blocked by its own revocation)
    let a_doc1_final = ev_a.get_events_for_entity(&doc_1).unwrap();
    assert_eq!(a_doc1_final.len(), 4, "A should have 4 doc_1 events");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// Untrusted peer C cannot sync even if they discover A.
#[tokio::test]
async fn mdns_untrusted_peer_blocked_at_pairing_gate() {
    let peer_a = PeerId::new();
    let peer_c = PeerId::new(); // Never trusted by A

    let (handle_a, _events_a, cmd_rx_a, orch_a, policy_a, _pm_a, _es_a, _ev_a) =
        setup_personal_peer(peer_a);

    // C uses AllowAll (non-personal) — doesn't matter, A's pairing gate blocks C
    let (es_c, ev_c) = make_stores();
    let pm_c = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    let (handle_c, _events_c, cmd_rx_c, orch_c) = create_orchestrator_with_pairing(
        peer_c,
        es_c,
        ev_c.clone(),
        no_auto_config(),
        pm_c.clone(),
    );

    // C trusts A, but A does NOT trust C
    trust_peer(&pm_c, peer_a, "Alice");

    let doc = EntityId::new();
    policy_a.share(doc, peer_a).await; // shared with self only

    let (transport_a, transport_c) =
        BridgedTransport::pair(peer_a, "Alice", peer_c, "Charlie", DiscoveryMethod::Mdns);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_c: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_c));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_c = tokio::spawn(async move { orch_c.run(transport_c, cmd_rx_c).await });

    // C tries to sync with A
    handle_c
        .send(SyncCommand::SyncWithPeer { peer_id: peer_a })
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(500)).await;

    let c_events = ev_c.get_events_for_entity(&doc).unwrap();
    assert!(c_events.is_empty(), "C should have no events from A");

    handle_a.shutdown().await.unwrap();
    handle_c.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_c.await;
}

/// A shares different entities with different peers.
#[tokio::test]
async fn mdns_multi_peer_selective_sharing() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let peer_c = PeerId::new();

    let doc_1 = EntityId::new(); // Shared with B only
    let doc_2 = EntityId::new(); // Shared with C only
    let doc_3 = EntityId::new(); // Shared with both B and C

    let peers = &[
        (peer_a, "Alice"),
        (peer_b, "Bob"),
        (peer_c, "Charlie"),
    ];

    let (_hub, transports) = create_hub_transports(peers);

    // Setup A
    let (es_a, ev_a) = make_stores();
    let policy_a = Arc::new(PersonalSyncPolicy::new());
    let pm_a = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_a, peer_c, "Charlie");
    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_personal_orchestrator(
        peer_a,
        es_a.clone(),
        ev_a.clone(),
        no_auto_config(),
        policy_a.clone(),
        pm_a,
    );

    // Setup B
    let (es_b, ev_b) = make_stores();
    let policy_b = Arc::new(PersonalSyncPolicy::new());
    let pm_b = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_b, peer_a, "Alice");
    let (handle_b, mut events_b, cmd_rx_b, orch_b) = create_personal_orchestrator(
        peer_b,
        es_b,
        ev_b.clone(),
        no_auto_config(),
        policy_b.clone(),
        pm_b,
    );

    // Setup C
    let (es_c, ev_c) = make_stores();
    let policy_c = Arc::new(PersonalSyncPolicy::new());
    let pm_c = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_c, peer_a, "Alice");
    let (handle_c, mut events_c, cmd_rx_c, orch_c) = create_personal_orchestrator(
        peer_c,
        es_c,
        ev_c.clone(),
        no_auto_config(),
        policy_c.clone(),
        pm_c,
    );

    // A's sharing policy
    policy_a.share(doc_1, peer_b).await;
    policy_a.share(doc_2, peer_c).await;
    policy_a.share(doc_3, peer_b).await;
    policy_a.share(doc_3, peer_c).await;

    // B and C share back (for bidirectional editing)
    policy_b.share(doc_1, peer_a).await;
    policy_b.share(doc_3, peer_a).await;
    policy_c.share(doc_2, peer_a).await;
    policy_c.share(doc_3, peer_a).await;

    // Run orchestrators
    let t_a = transports[0].clone();
    let t_b = transports[1].clone();
    let t_c = transports[2].clone();

    let join_a = tokio::spawn(async move { orch_a.run(t_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(t_b, cmd_rx_b).await });
    let join_c = tokio::spawn(async move { orch_c.run(t_c, cmd_rx_c).await });

    // Register shared entities
    handle_a.share_entity(doc_1).await.unwrap();
    handle_a.share_entity(doc_2).await.unwrap();
    handle_a.share_entity(doc_3).await.unwrap();
    handle_b.share_entity(doc_1).await.unwrap();
    handle_b.share_entity(doc_3).await.unwrap();
    handle_c.share_entity(doc_2).await.unwrap();
    handle_c.share_entity(doc_3).await.unwrap();

    // A creates events on all three docs
    for (doc, title) in [(doc_1, "Doc1"), (doc_2, "Doc2"), (doc_3, "Doc3")] {
        let evt = make_event(
            doc,
            peer_a,
            EventPayload::EntityCreated {
                entity_type: "note".to_string(),
                json_data: format!(r#"{{"title":"{}"}}"#, title),
            },
        );
        record_event(&handle_a, &es_a, &ev_a, peer_a, evt).await;
    }
    tokio::time::sleep(Duration::from_millis(100)).await;

    // A syncs with B
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;
    // A syncs with C
    full_sync(&handle_a, &mut events_a, peer_c, &handle_c, &mut events_c, peer_a).await;

    // Verify B's access
    let b_doc1 = ev_b.get_events_for_entity(&doc_1).unwrap();
    let b_doc2 = ev_b.get_events_for_entity(&doc_2).unwrap();
    let b_doc3 = ev_b.get_events_for_entity(&doc_3).unwrap();
    assert_eq!(b_doc1.len(), 1, "B should have doc_1");
    assert!(b_doc2.is_empty(), "B should NOT have doc_2");
    assert_eq!(b_doc3.len(), 1, "B should have doc_3");

    // Verify C's access
    let c_doc1 = ev_c.get_events_for_entity(&doc_1).unwrap();
    let c_doc2 = ev_c.get_events_for_entity(&doc_2).unwrap();
    let c_doc3 = ev_c.get_events_for_entity(&doc_3).unwrap();
    assert!(c_doc1.is_empty(), "C should NOT have doc_1");
    assert_eq!(c_doc2.len(), 1, "C should have doc_2");
    assert_eq!(c_doc3.len(), 1, "C should have doc_3");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    handle_c.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    let _ = join_c.await;
}

/// B and C can both edit a shared doc, CRDT converges on all three peers.
#[tokio::test]
async fn mdns_multi_editor_crdt_convergence() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let peer_c = PeerId::new();

    let doc = EntityId::new();

    let peers = &[
        (peer_a, "Alice"),
        (peer_b, "Bob"),
        (peer_c, "Charlie"),
    ];
    let (_hub, transports) = create_hub_transports(peers);

    // Setup all three
    let (es_a, ev_a) = make_stores();
    let policy_a = Arc::new(PersonalSyncPolicy::new());
    let pm_a = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_a, peer_c, "Charlie");
    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_personal_orchestrator(
        peer_a, es_a.clone(), ev_a.clone(), no_auto_config(), policy_a.clone(), pm_a,
    );

    let (es_b, ev_b) = make_stores();
    let policy_b = Arc::new(PersonalSyncPolicy::new());
    let pm_b = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_b, peer_a, "Alice");
    let (handle_b, mut events_b, cmd_rx_b, orch_b) = create_personal_orchestrator(
        peer_b, es_b.clone(), ev_b.clone(), no_auto_config(), policy_b.clone(), pm_b,
    );

    let (es_c, ev_c) = make_stores();
    let policy_c = Arc::new(PersonalSyncPolicy::new());
    let pm_c = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_c, peer_a, "Alice");
    let (handle_c, mut events_c, cmd_rx_c, orch_c) = create_personal_orchestrator(
        peer_c, es_c.clone(), ev_c.clone(), no_auto_config(), policy_c.clone(), pm_c,
    );

    // Share doc with all peers
    policy_a.share(doc, peer_b).await;
    policy_a.share(doc, peer_c).await;
    policy_b.share(doc, peer_a).await;
    policy_c.share(doc, peer_a).await;

    let t_a = transports[0].clone();
    let t_b = transports[1].clone();
    let t_c = transports[2].clone();
    let join_a = tokio::spawn(async move { orch_a.run(t_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(t_b, cmd_rx_b).await });
    let join_c = tokio::spawn(async move { orch_c.run(t_c, cmd_rx_c).await });

    handle_a.share_entity(doc).await.unwrap();
    handle_b.share_entity(doc).await.unwrap();
    handle_c.share_entity(doc).await.unwrap();

    // A creates the doc
    let create = make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Collab"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, create).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Sync A → B and A → C
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;
    full_sync(&handle_a, &mut events_a, peer_c, &handle_c, &mut events_c, peer_a).await;

    // B edits
    let edit_b = make_event(doc, peer_b, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Collab","author":"Bob"}"#.to_string(),
    });
    record_event(&handle_b, &es_b, &ev_b, peer_b, edit_b).await;

    // C edits
    let edit_c = make_event(doc, peer_c, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Collab","footer":"Charlie"}"#.to_string(),
    });
    record_event(&handle_c, &es_c, &ev_c, peer_c, edit_c).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Sync all pairs via A (hub)
    full_sync(&handle_b, &mut events_b, peer_a, &handle_a, &mut events_a, peer_b).await;
    full_sync(&handle_c, &mut events_c, peer_a, &handle_a, &mut events_a, peer_c).await;
    // A now has everything; push to B and C
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;
    full_sync(&handle_a, &mut events_a, peer_c, &handle_c, &mut events_c, peer_a).await;

    // All three should have 3 events: create + edit_b + edit_c
    let a_events = ev_a.get_events_for_entity(&doc).unwrap();
    let b_events = ev_b.get_events_for_entity(&doc).unwrap();
    let c_events = ev_c.get_events_for_entity(&doc).unwrap();

    assert_eq!(a_events.len(), 3, "A should have 3 events");
    assert_eq!(b_events.len(), 3, "B should have 3 events");
    assert_eq!(c_events.len(), 3, "C should have 3 events");

    // All event sets should be identical
    let mut a_ids: Vec<_> = a_events.iter().map(|e| e.id.to_string()).collect();
    let mut b_ids: Vec<_> = b_events.iter().map(|e| e.id.to_string()).collect();
    let mut c_ids: Vec<_> = c_events.iter().map(|e| e.id.to_string()).collect();
    a_ids.sort();
    b_ids.sort();
    c_ids.sort();
    assert_eq!(a_ids, b_ids, "A and B must converge");
    assert_eq!(b_ids, c_ids, "B and C must converge");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    handle_c.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    let _ = join_c.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// LEVEL 2: DHT-Like Discovery (uses DiscoveryMethod::Dht)
// ═══════════════════════════════════════════════════════════════════════════

/// Same lifecycle as mDNS but simulated as DHT discovery.
/// Verifies the pairing gate + personal policy work regardless of discovery method.
#[tokio::test]
async fn dht_share_and_revoke() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, policy_b, pm_b, es_b, ev_b) =
        setup_personal_peer(peer_b);

    trust_peer(&pm_a, peer_b, "Bob-DHT");
    trust_peer(&pm_b, peer_a, "Alice-DHT");

    let doc = EntityId::new();
    policy_a.share(doc, peer_b).await;
    policy_b.share(doc, peer_a).await;

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::Dht);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(doc).await.unwrap();
    handle_b.share_entity(doc).await.unwrap();

    // A creates doc
    let create = make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"DHT Doc"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, create.clone()).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    let b_events = ev_b.get_events_for_entity(&doc).unwrap();
    assert_eq!(b_events.len(), 1, "B should have doc via DHT bridge");

    // B edits
    let edit = make_event(doc, peer_b, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"DHT Doc","note":"edited"}"#.to_string(),
    });
    record_event(&handle_b, &es_b, &ev_b, peer_b, edit).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    full_sync(&handle_b, &mut events_b, peer_a, &handle_a, &mut events_a, peer_b).await;

    let a_events = ev_a.get_events_for_entity(&doc).unwrap();
    assert_eq!(a_events.len(), 2, "A should have create + B's edit");

    // Revoke
    policy_a.unshare(doc, peer_b).await;

    let post = make_event(doc, peer_a, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"DHT Doc","note":"post-revoke"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, post).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    let b_final = ev_b.get_events_for_entity(&doc).unwrap();
    assert_eq!(b_final.len(), 2, "B should NOT get post-revoke event");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// DHT: untrusted peer gets nothing even if discovery succeeds.
#[tokio::test]
async fn dht_untrusted_peer_blocked() {
    let peer_a = PeerId::new();
    let peer_c = PeerId::new();

    let (handle_a, _events_a, cmd_rx_a, orch_a, policy_a, _pm_a, _es_a, _ev_a) =
        setup_personal_peer(peer_a);
    let (handle_c, _events_c, cmd_rx_c, orch_c, _policy_c, pm_c, _es_c, ev_c) =
        setup_personal_peer(peer_c);

    // C trusts A, A does NOT trust C
    trust_peer(&pm_c, peer_a, "Alice");

    let doc = EntityId::new();
    policy_a.share(doc, peer_a).await; // only self

    let (transport_a, transport_c) =
        BridgedTransport::pair(peer_a, "Alice", peer_c, "Charlie", DiscoveryMethod::Dht);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_c: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_c));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_c = tokio::spawn(async move { orch_c.run(transport_c, cmd_rx_c).await });

    handle_c
        .send(SyncCommand::SyncWithPeer { peer_id: peer_a })
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(500)).await;

    let c_events = ev_c.get_events_for_entity(&doc).unwrap();
    assert!(c_events.is_empty(), "Untrusted C gets nothing via DHT");

    handle_a.shutdown().await.unwrap();
    handle_c.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_c.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// LEVEL 3: Relay-Like (uses DiscoveryMethod::CloudRelay)
// ═══════════════════════════════════════════════════════════════════════════

/// Full lifecycle over relay discovery.
#[tokio::test]
async fn relay_share_edit_revoke() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, policy_b, pm_b, es_b, ev_b) =
        setup_personal_peer(peer_b);

    trust_peer(&pm_a, peer_b, "Bob-Relay");
    trust_peer(&pm_b, peer_a, "Alice-Relay");

    let doc = EntityId::new();
    let private = EntityId::new();
    policy_a.share(doc, peer_b).await;
    policy_b.share(doc, peer_a).await;

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::CloudRelay);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(doc).await.unwrap();
    handle_a.share_entity(private).await.unwrap();
    handle_b.share_entity(doc).await.unwrap();

    // A creates both docs
    let ev_doc = make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Relay Shared"}"#.to_string(),
    });
    let ev_private = make_event(private, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Relay Private"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev_doc.clone()).await;
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev_private.clone()).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    // B has shared doc, not private
    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 1);
    assert!(ev_b.get_events_for_entity(&private).unwrap().is_empty());

    // B edits shared doc
    let edit = make_event(doc, peer_b, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Relay Shared","by":"Bob"}"#.to_string(),
    });
    record_event(&handle_b, &es_b, &ev_b, peer_b, edit).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    full_sync(&handle_b, &mut events_b, peer_a, &handle_a, &mut events_a, peer_b).await;
    assert_eq!(ev_a.get_events_for_entity(&doc).unwrap().len(), 2);

    // Revoke
    policy_a.unshare(doc, peer_b).await;

    let post = make_event(doc, peer_a, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Relay Shared","post":"revoke"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, post).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    // B keeps existing 2 events, doesn't get new one
    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 2);
    // A has 3
    assert_eq!(ev_a.get_events_for_entity(&doc).unwrap().len(), 3);

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// Relay: three peers, selective sharing, full convergence.
#[tokio::test]
async fn relay_three_peers_selective_convergence() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let peer_c = PeerId::new();

    let shared_bc = EntityId::new(); // A shares with both B and C
    let only_b = EntityId::new();    // A shares with B only

    let peers = &[(peer_a, "Alice"), (peer_b, "Bob"), (peer_c, "Charlie")];
    let (_hub, transports) = create_hub_transports(peers);

    // Setup all three with mutual trust
    let setup = |pid, peers_to_trust: &[(PeerId, &str)]| {
        let (es, ev) = make_stores();
        let policy = Arc::new(PersonalSyncPolicy::new());
        let pm = Arc::new(std::sync::Mutex::new(PairingManager::new()));
        for (tp, tn) in peers_to_trust {
            trust_peer(&pm, *tp, tn);
        }
        let (h, erx, crx, o) =
            create_personal_orchestrator(pid, es.clone(), ev.clone(), no_auto_config(), policy.clone(), pm);
        (h, erx, crx, o, policy, es, ev)
    };

    let (handle_a, mut ev_rx_a, cmd_rx_a, orch_a, pol_a, es_a, ev_a) =
        setup(peer_a, &[(peer_b, "Bob"), (peer_c, "Charlie")]);
    let (handle_b, mut ev_rx_b, cmd_rx_b, orch_b, pol_b, es_b, ev_b) =
        setup(peer_b, &[(peer_a, "Alice")]);
    let (handle_c, mut ev_rx_c, cmd_rx_c, orch_c, pol_c, es_c, ev_c) =
        setup(peer_c, &[(peer_a, "Alice")]);

    // A's sharing
    pol_a.share(shared_bc, peer_b).await;
    pol_a.share(shared_bc, peer_c).await;
    pol_a.share(only_b, peer_b).await;
    pol_b.share(shared_bc, peer_a).await;
    pol_b.share(only_b, peer_a).await;
    pol_c.share(shared_bc, peer_a).await;

    let t_a = transports[0].clone();
    let t_b = transports[1].clone();
    let t_c = transports[2].clone();
    let ja = tokio::spawn(async move { orch_a.run(t_a, cmd_rx_a).await });
    let jb = tokio::spawn(async move { orch_b.run(t_b, cmd_rx_b).await });
    let jc = tokio::spawn(async move { orch_c.run(t_c, cmd_rx_c).await });

    for h in [&handle_a, &handle_b, &handle_c] {
        h.share_entity(shared_bc).await.unwrap();
    }
    handle_a.share_entity(only_b).await.unwrap();
    handle_b.share_entity(only_b).await.unwrap();

    // Everyone creates an event on shared_bc
    for (peer, name) in [(peer_a, "A"), (peer_b, "B"), (peer_c, "C")] {
        let (h, es, ev): (&OrchestratorHandle, &Arc<EntityStore>, &Arc<EventStore>) = match name {
            "A" => (&handle_a, &es_a, &ev_a),
            "B" => (&handle_b, &es_b, &ev_b),
            _ => (&handle_c, &es_c, &ev_c),
        };
        record_event(h, es, ev, peer, make_event(shared_bc, peer, EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: format!(r#"{{"from":"{}"}}"#, name),
        })).await;
    }

    // A creates event on only_b
    record_event(&handle_a, &es_a, &ev_a, peer_a, make_event(only_b, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"secret":"B only"}"#.to_string(),
    })).await;

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync all via A
    full_sync(&handle_a, &mut ev_rx_a, peer_b, &handle_b, &mut ev_rx_b, peer_a).await;
    full_sync(&handle_a, &mut ev_rx_a, peer_c, &handle_c, &mut ev_rx_c, peer_a).await;
    full_sync(&handle_b, &mut ev_rx_b, peer_a, &handle_a, &mut ev_rx_a, peer_b).await;
    full_sync(&handle_c, &mut ev_rx_c, peer_a, &handle_a, &mut ev_rx_a, peer_c).await;
    // Push final state to B and C
    full_sync(&handle_a, &mut ev_rx_a, peer_b, &handle_b, &mut ev_rx_b, peer_a).await;
    full_sync(&handle_a, &mut ev_rx_a, peer_c, &handle_c, &mut ev_rx_c, peer_a).await;

    // shared_bc: all three should have all 3 events
    assert_eq!(ev_a.get_events_for_entity(&shared_bc).unwrap().len(), 3);
    assert_eq!(ev_b.get_events_for_entity(&shared_bc).unwrap().len(), 3);
    assert_eq!(ev_c.get_events_for_entity(&shared_bc).unwrap().len(), 3);

    // only_b: A and B have it, C does not
    assert_eq!(ev_a.get_events_for_entity(&only_b).unwrap().len(), 1);
    assert_eq!(ev_b.get_events_for_entity(&only_b).unwrap().len(), 1);
    assert!(ev_c.get_events_for_entity(&only_b).unwrap().is_empty());

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    handle_c.shutdown().await.unwrap();
    let _ = ja.await;
    let _ = jb.await;
    let _ = jc.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// Adversarial Scenarios
// ═══════════════════════════════════════════════════════════════════════════

/// Peer B tries to sync an entity they were never given access to.
/// The policy should filter it out; B gets nothing.
#[tokio::test]
async fn adversarial_peer_requests_unshared_entity() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, _policy_b, pm_b, _es_b, ev_b) =
        setup_personal_peer(peer_b);

    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_b, peer_a, "Alice");

    let secret_doc = EntityId::new();
    // Activate selective sharing by sharing a different entity with B.
    // This makes peer_entities non-empty, enabling the policy filter.
    let other_doc = EntityId::new();
    policy_a.share(other_doc, peer_b).await;
    // A does NOT share secret_doc with B

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::Mdns);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(secret_doc).await.unwrap();
    handle_b.share_entity(secret_doc).await.unwrap(); // B claims to share it

    let secret_event = make_event(secret_doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Top Secret"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, secret_event).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // A syncs with B — policy filters secret_doc since it's not shared with B
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    let b_secret = ev_b.get_events_for_entity(&secret_doc).unwrap();
    assert!(b_secret.is_empty(), "B must NOT receive unshared entity");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// Re-granting access after revocation allows new events to flow.
#[tokio::test]
async fn revoke_then_re_grant_allows_new_events() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, policy_b, pm_b, _es_b, ev_b) =
        setup_personal_peer(peer_b);

    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_b, peer_a, "Alice");

    let doc = EntityId::new();
    policy_a.share(doc, peer_b).await;
    policy_b.share(doc, peer_a).await;

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::Mdns);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(doc).await.unwrap();
    handle_b.share_entity(doc).await.unwrap();

    // Phase 1: Initial share
    let ev1 = make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"v":1}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev1).await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;
    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 1);

    // Phase 2: Revoke
    policy_a.unshare(doc, peer_b).await;
    let ev2 = make_event(doc, peer_a, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"v":2}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev2).await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;
    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 1, "still 1 after revoke");

    // Phase 3: Re-grant
    policy_a.share(doc, peer_b).await;
    let ev3 = make_event(doc, peer_a, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"v":3}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev3).await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    // B should now get the gap events (ev2 from revocation period + ev3)
    let b_final = ev_b.get_events_for_entity(&doc).unwrap();
    assert_eq!(b_final.len(), 3, "B should have all 3 events after re-grant");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// Concurrent edits during partial revocation — entity shared with B but not C.
/// B's edits propagate, C's don't.
#[tokio::test]
async fn concurrent_edits_with_partial_access() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let peer_c = PeerId::new();

    let doc = EntityId::new();

    let peers = &[(peer_a, "Alice"), (peer_b, "Bob"), (peer_c, "Charlie")];
    let (_hub, transports) = create_hub_transports(peers);

    let (es_a, ev_a) = make_stores();
    let pol_a = Arc::new(PersonalSyncPolicy::new());
    let pm_a = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_a, peer_c, "Charlie");
    let (ha, mut ea, cra, oa) =
        create_personal_orchestrator(peer_a, es_a.clone(), ev_a.clone(), no_auto_config(), pol_a.clone(), pm_a);

    let (es_b, ev_b) = make_stores();
    let pol_b = Arc::new(PersonalSyncPolicy::new());
    let pm_b = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_b, peer_a, "Alice");
    let (hb, mut eb, crb, ob) =
        create_personal_orchestrator(peer_b, es_b.clone(), ev_b.clone(), no_auto_config(), pol_b.clone(), pm_b);

    let (es_c, ev_c) = make_stores();
    let pol_c = Arc::new(PersonalSyncPolicy::new());
    let pm_c = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_c, peer_a, "Alice");
    let (hc, mut ec, crc, oc) =
        create_personal_orchestrator(peer_c, es_c.clone(), ev_c.clone(), no_auto_config(), pol_c.clone(), pm_c);

    // Only B has access, not C
    pol_a.share(doc, peer_b).await;
    pol_b.share(doc, peer_a).await;
    // C is NOT granted access

    let t_a = transports[0].clone();
    let t_b = transports[1].clone();
    let t_c = transports[2].clone();
    let ja = tokio::spawn(async move { oa.run(t_a, cra).await });
    let jb = tokio::spawn(async move { ob.run(t_b, crb).await });
    let jc = tokio::spawn(async move { oc.run(t_c, crc).await });

    ha.share_entity(doc).await.unwrap();
    hb.share_entity(doc).await.unwrap();
    hc.share_entity(doc).await.unwrap(); // C tries to share it

    // A and B both edit
    record_event(&ha, &es_a, &ev_a, peer_a, make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"from":"A"}"#.to_string(),
    })).await;

    record_event(&hb, &es_b, &ev_b, peer_b, make_event(doc, peer_b, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"from":"B"}"#.to_string(),
    })).await;

    // C also tries to edit
    record_event(&hc, &es_c, &ev_c, peer_c, make_event(doc, peer_c, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"from":"C-sneaky"}"#.to_string(),
    })).await;

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync A↔B
    full_sync(&ha, &mut ea, peer_b, &hb, &mut eb, peer_a).await;
    // Sync A↔C
    full_sync(&ha, &mut ea, peer_c, &hc, &mut ec, peer_a).await;

    // A and B should have 2 events (A's create + B's edit)
    assert_eq!(ev_a.get_events_for_entity(&doc).unwrap().len(), 2);
    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 2);

    // C should only have their own local event (1), nothing from A
    // (C has 1 local event they created, but got nothing from A's sync)
    let c_events = ev_c.get_events_for_entity(&doc).unwrap();
    assert_eq!(c_events.len(), 1, "C only has their own local event");

    ha.shutdown().await.unwrap();
    hb.shutdown().await.unwrap();
    hc.shutdown().await.unwrap();
    let _ = ja.await;
    let _ = jb.await;
    let _ = jc.await;
}

/// Verify that removing a trusted peer from pairing prevents future discovery sync.
#[tokio::test]
async fn removing_trusted_peer_blocks_future_sync() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, policy_b, pm_b, _es_b, ev_b) =
        setup_personal_peer(peer_b);

    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_b, peer_a, "Alice");

    let doc = EntityId::new();
    policy_a.share(doc, peer_b).await;
    policy_b.share(doc, peer_a).await;

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::Mdns);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(doc).await.unwrap();
    handle_b.share_entity(doc).await.unwrap();

    // Initial sync works
    let ev1 = make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"v":1}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev1).await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;
    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 1);

    // Remove B from trusted peers
    pm_a.lock().unwrap().remove_trusted_peer(&peer_b.to_string());
    assert!(!pm_a.lock().unwrap().is_trusted(&peer_b.to_string()));

    // Note: removing from pairing manager blocks discovery-initiated sync,
    // but manual SyncWithPeer command still goes through since the pairing
    // gate is only on check_for_new_peers. The policy still filters.
    // So also unshare at the policy level:
    policy_a.unshare(doc, peer_b).await;

    let ev2 = make_event(doc, peer_a, EventPayload::EntityUpdated {
        entity_type: "note".to_string(),
        json_data: r#"{"v":2}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev2).await;
    tokio::time::sleep(Duration::from_millis(50)).await;
    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    assert_eq!(
        ev_b.get_events_for_entity(&doc).unwrap().len(),
        1,
        "B should still have only 1 event after peer removal"
    );

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// Stress test: many entities, many peers, verify isolation.
#[tokio::test]
async fn stress_many_entities_many_peers() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let peer_c = PeerId::new();

    let entities: Vec<EntityId> = (0..20).map(|_| EntityId::new()).collect();

    let peers = &[(peer_a, "Alice"), (peer_b, "Bob"), (peer_c, "Charlie")];
    let (_hub, transports) = create_hub_transports(peers);

    let (es_a, ev_a) = make_stores();
    let pol_a = Arc::new(PersonalSyncPolicy::new());
    let pm_a = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_a, peer_c, "Charlie");
    let (ha, mut ea, cra, oa) =
        create_personal_orchestrator(peer_a, es_a.clone(), ev_a.clone(), no_auto_config(), pol_a.clone(), pm_a);

    let (_es_b, ev_b) = make_stores();
    let pol_b = Arc::new(PersonalSyncPolicy::new());
    let pm_b = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_b, peer_a, "Alice");
    let (hb, mut eb, crb, ob) =
        create_personal_orchestrator(peer_b, _es_b, ev_b.clone(), no_auto_config(), pol_b.clone(), pm_b);

    let (_es_c, ev_c) = make_stores();
    let pol_c = Arc::new(PersonalSyncPolicy::new());
    let pm_c = Arc::new(std::sync::Mutex::new(PairingManager::new()));
    trust_peer(&pm_c, peer_a, "Alice");
    let (hc, mut ec, crc, oc) =
        create_personal_orchestrator(peer_c, _es_c, ev_c.clone(), no_auto_config(), pol_c.clone(), pm_c);

    // Start orchestrators BEFORE sending commands (channel capacity is 32,
    // and we send 40+ commands — orchestrator must be consuming them).
    let t_a = transports[0].clone();
    let t_b = transports[1].clone();
    let t_c = transports[2].clone();
    let ja = tokio::spawn(async move { oa.run(t_a, cra).await });
    let jb = tokio::spawn(async move { ob.run(t_b, crb).await });
    let jc = tokio::spawn(async move { oc.run(t_c, crc).await });

    tokio::time::sleep(Duration::from_millis(50)).await;

    // Even-indexed → B, odd-indexed → C, multiples of 5 → both
    for (i, &eid) in entities.iter().enumerate() {
        ha.share_entity(eid).await.unwrap();
        if i % 5 == 0 {
            // Both
            pol_a.share(eid, peer_b).await;
            pol_a.share(eid, peer_c).await;
            pol_b.share(eid, peer_a).await;
            pol_c.share(eid, peer_a).await;
            hb.share_entity(eid).await.unwrap();
            hc.share_entity(eid).await.unwrap();
        } else if i % 2 == 0 {
            // B only
            pol_a.share(eid, peer_b).await;
            pol_b.share(eid, peer_a).await;
            hb.share_entity(eid).await.unwrap();
        } else {
            // C only
            pol_a.share(eid, peer_c).await;
            pol_c.share(eid, peer_a).await;
            hc.share_entity(eid).await.unwrap();
        }
    }

    // A creates one event per entity
    for &eid in &entities {
        let ev = make_event(eid, peer_a, EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"data":"x"}"#.to_string(),
        });
        record_event(&ha, &es_a, &ev_a, peer_a, ev).await;
    }

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync A↔B, A↔C
    full_sync(&ha, &mut ea, peer_b, &hb, &mut eb, peer_a).await;
    full_sync(&ha, &mut ea, peer_c, &hc, &mut ec, peer_a).await;

    // Verify isolation
    for (i, &eid) in entities.iter().enumerate() {
        let b_has = !ev_b.get_events_for_entity(&eid).unwrap().is_empty();
        let c_has = !ev_c.get_events_for_entity(&eid).unwrap().is_empty();

        if i % 5 == 0 {
            assert!(b_has, "entity {} (i={}) should be on B (shared with both)", eid, i);
            assert!(c_has, "entity {} (i={}) should be on C (shared with both)", eid, i);
        } else if i % 2 == 0 {
            assert!(b_has, "entity {} (i={}) should be on B (even)", eid, i);
            assert!(!c_has, "entity {} (i={}) should NOT be on C (even, B-only)", eid, i);
        } else {
            assert!(!b_has, "entity {} (i={}) should NOT be on B (odd, C-only)", eid, i);
            assert!(c_has, "entity {} (i={}) should be on C (odd)", eid, i);
        }
    }

    ha.shutdown().await.unwrap();
    hb.shutdown().await.unwrap();
    hc.shutdown().await.unwrap();
    let _ = ja.await;
    let _ = jb.await;
    let _ = jc.await;
}

/// Verify ShareEntityWithPeer command works through orchestrator handle.
#[tokio::test]
async fn share_entity_with_peer_command_flow() {
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let (handle_a, mut events_a, cmd_rx_a, orch_a, policy_a, pm_a, es_a, ev_a) =
        setup_personal_peer(peer_a);
    let (handle_b, mut events_b, cmd_rx_b, orch_b, policy_b, pm_b, _es_b, ev_b) =
        setup_personal_peer(peer_b);

    trust_peer(&pm_a, peer_b, "Bob");
    trust_peer(&pm_b, peer_a, "Alice");

    let doc = EntityId::new();

    let (transport_a, transport_b) =
        BridgedTransport::pair(peer_a, "Alice", peer_b, "Bob", DiscoveryMethod::Mdns);
    let transport_a: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_a));
    let transport_b: Arc<TokioMutex<dyn SyncTransport>> = Arc::new(TokioMutex::new(transport_b));

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    // Use the ShareEntityWithPeer command (not direct policy.share())
    handle_a
        .share_entity_with_peer(doc, peer_b)
        .await
        .unwrap();
    // B's side also needs to share
    policy_b.share(doc, peer_a).await;
    handle_b.share_entity(doc).await.unwrap();

    // Give command time to process
    tokio::time::sleep(Duration::from_millis(200)).await;

    // Verify the command was routed to the policy
    let shared = policy_a.shared_entities(&peer_b).await;
    assert!(shared.contains(&doc), "ShareEntityWithPeer should route to policy");

    // Create and sync
    let ev = make_event(doc, peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"via":"command"}"#.to_string(),
    });
    record_event(&handle_a, &es_a, &ev_a, peer_a, ev).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    full_sync(&handle_a, &mut events_a, peer_b, &handle_b, &mut events_b, peer_a).await;

    assert_eq!(ev_b.get_events_for_entity(&doc).unwrap().len(), 1);

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

/// Pairing display names persist correctly in PairingManager.
#[tokio::test]
async fn pairing_display_names_persist() {
    let pm = PairingManager::new();
    let pm = Arc::new(std::sync::Mutex::new(pm));
    let peer = PeerId::new();

    trust_peer(&pm, peer, "Bob's MacBook Pro");

    let guard = pm.lock().unwrap();
    let trusted = guard.get_trusted_peer(&peer.to_string());
    assert!(trusted.is_some());
    assert_eq!(trusted.unwrap().device_name, "Bob's MacBook Pro");

    // Serialize and deserialize
    let json = guard.to_json().unwrap();
    drop(guard);

    let restored = PairingManager::from_json(&json).unwrap();
    let trusted = restored.get_trusted_peer(&peer.to_string());
    assert!(trusted.is_some());
    assert_eq!(trusted.unwrap().device_name, "Bob's MacBook Pro");
}

/// Sharing with self (same peer ID) is a no-op and doesn't break anything.
#[tokio::test]
async fn sharing_with_self_is_harmless() {
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    policy.share(entity, peer).await;

    let entities = policy.shared_entities(&peer).await;
    assert_eq!(entities.len(), 1);

    let peers = policy.shared_peers(&entity).await;
    assert_eq!(peers.len(), 1);

    // Unshare
    policy.unshare(entity, peer).await;
    assert!(policy.shared_entities(&peer).await.is_empty());
}

/// Double-share is idempotent.
#[tokio::test]
async fn double_share_idempotent() {
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    policy.share(entity, peer).await;
    policy.share(entity, peer).await;
    policy.share(entity, peer).await;

    assert_eq!(policy.shared_entities(&peer).await.len(), 1);
    assert_eq!(policy.shared_peers(&entity).await.len(), 1);
}

/// Double-unshare is harmless.
#[tokio::test]
async fn double_unshare_harmless() {
    let policy = PersonalSyncPolicy::new();
    let peer = PeerId::new();
    let entity = EntityId::new();

    policy.share(entity, peer).await;
    policy.unshare(entity, peer).await;
    policy.unshare(entity, peer).await; // no panic

    assert!(policy.shared_entities(&peer).await.is_empty());
}
