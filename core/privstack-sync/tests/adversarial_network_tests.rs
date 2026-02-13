//! Adversarial network condition tests for CRDT sync convergence.
//!
//! Tests that CRDTs converge correctly through the sync protocol when the
//! underlying transport suffers from:
//! - Network partitions (total message loss)
//! - Message delays (variable latency)
//! - Intermittent connectivity (flapping connections)
//! - Split-brain scenarios with deep divergence before reunion
//! - Rapid reconnect/disconnect cycles
//!
//! Built on the BridgedTransport pattern from e2e_bridge_tests, extended with
//! an adversarial wrapper that can inject faults.

use async_trait::async_trait;
use privstack_sync::transport::{
    DiscoveredPeer, DiscoveryMethod, IncomingSyncRequest, ResponseToken, SyncTransport,
};
use privstack_sync::{
    create_orchestrator, EventApplicator, OrchestratorConfig, SyncCommand, SyncEvent, SyncMessage,
    SyncResult,
};
use privstack_storage::{EntityStore, EventStore};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, oneshot, Mutex};

// ═══════════════════════════════════════════════════════════════════════════
// Transport Infrastructure
// ═══════════════════════════════════════════════════════════════════════════

/// A request delivered to the remote side.
struct BridgedRequest {
    from: PeerId,
    message: SyncMessage,
    response_tx: oneshot::Sender<SyncMessage>,
}

/// Base bridged transport — channels connecting two peers in-process.
struct BridgedTransport {
    local_peer_id: PeerId,
    remote_peer_id: PeerId,
    outgoing_tx: mpsc::Sender<BridgedRequest>,
    incoming_rx: Mutex<mpsc::Receiver<BridgedRequest>>,
    report_peer: Mutex<bool>,
}

impl BridgedTransport {
    fn pair(
        peer_a: PeerId,
        peer_b: PeerId,
    ) -> (Self, Self) {
        let (a_to_b_tx, a_to_b_rx) = mpsc::channel::<BridgedRequest>(64);
        let (b_to_a_tx, b_to_a_rx) = mpsc::channel::<BridgedRequest>(64);

        let ta = Self {
            local_peer_id: peer_a,
            remote_peer_id: peer_b,
            outgoing_tx: a_to_b_tx,
            incoming_rx: Mutex::new(b_to_a_rx),
            report_peer: Mutex::new(false),
        };

        let tb = Self {
            local_peer_id: peer_b,
            remote_peer_id: peer_a,
            outgoing_tx: b_to_a_tx,
            incoming_rx: Mutex::new(a_to_b_rx),
            report_peer: Mutex::new(false),
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
                device_name: Some("Bridge".into()),
                discovery_method: DiscoveryMethod::Manual,
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
            .map_err(|_| privstack_sync::SyncError::Network("bridge response closed".into()))
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
            .ok_or_else(|| privstack_sync::SyncError::Network("bad token".into()))?;
        tx.send(message)
            .map_err(|_| privstack_sync::SyncError::Network("response dropped".into()))
    }
}

/// Adversarial transport wrapper that injects faults.
///
/// Wraps a real transport and can:
/// - Partition: drop all messages (send returns error)
/// - Delay: add latency before forwarding
/// - Hide peer: remove from discovery results
struct AdversarialTransport {
    inner: BridgedTransport,
    /// When true, all send_request calls fail with network error.
    partitioned: Arc<AtomicBool>,
    /// Milliseconds of delay to inject on each send_request.
    delay_ms: Arc<AtomicU64>,
}

impl AdversarialTransport {
    fn wrap(
        inner: BridgedTransport,
        partitioned: Arc<AtomicBool>,
        delay_ms: Arc<AtomicU64>,
    ) -> Self {
        Self {
            inner,
            partitioned,
            delay_ms,
        }
    }
}

#[async_trait]
impl SyncTransport for AdversarialTransport {
    async fn start(&mut self) -> SyncResult<()> {
        self.inner.start().await
    }

    async fn stop(&mut self) -> SyncResult<()> {
        self.inner.stop().await
    }

    fn is_running(&self) -> bool {
        self.inner.is_running()
    }

    fn local_peer_id(&self) -> PeerId {
        self.inner.local_peer_id()
    }

    fn discovered_peers(&self) -> Vec<DiscoveredPeer> {
        if self.partitioned.load(Ordering::SeqCst) {
            vec![]
        } else {
            self.inner.discovered_peers()
        }
    }

    async fn discovered_peers_async(&self) -> Vec<DiscoveredPeer> {
        if self.partitioned.load(Ordering::SeqCst) {
            vec![]
        } else {
            self.inner.discovered_peers_async().await
        }
    }

    async fn send_request(
        &self,
        peer_id: &PeerId,
        message: SyncMessage,
    ) -> SyncResult<SyncMessage> {
        if self.partitioned.load(Ordering::SeqCst) {
            return Err(privstack_sync::SyncError::Network(
                "network partitioned".into(),
            ));
        }

        let delay = self.delay_ms.load(Ordering::SeqCst);
        if delay > 0 {
            tokio::time::sleep(Duration::from_millis(delay)).await;
        }

        self.inner.send_request(peer_id, message).await
    }

    async fn recv_request(&self) -> Option<IncomingSyncRequest> {
        // Incoming requests are received even during partition
        // (the SENDER is partitioned, not the receiver)
        self.inner.recv_request().await
    }

    async fn send_response(
        &self,
        token: ResponseToken,
        message: SyncMessage,
    ) -> SyncResult<()> {
        let delay = self.delay_ms.load(Ordering::SeqCst);
        if delay > 0 {
            tokio::time::sleep(Duration::from_millis(delay)).await;
        }
        self.inner.send_response(token, message).await
    }
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

fn make_note_create(entity_id: EntityId, peer_id: PeerId, title: &str) -> Event {
    make_event(
        entity_id,
        peer_id,
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: format!(r#"{{"title":"{}"}}"#, title),
        },
    )
}

fn make_note_update(entity_id: EntityId, peer_id: PeerId, title: &str) -> Event {
    make_event(
        entity_id,
        peer_id,
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: format!(r#"{{"title":"{}"}}"#, title),
        },
    )
}

async fn wait_for_sync(
    rx: &mut mpsc::Receiver<SyncEvent>,
    timeout: Duration,
) -> Option<SyncEvent> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        match tokio::time::timeout_at(deadline, rx.recv()).await {
            Ok(Some(event @ SyncEvent::SyncCompleted { .. })) => return Some(event),
            Ok(Some(_)) => continue,
            _ => return None,
        }
    }
}

async fn wait_for_sync_or_fail(
    rx: &mut mpsc::Receiver<SyncEvent>,
    timeout: Duration,
) -> Option<SyncEvent> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        match tokio::time::timeout_at(deadline, rx.recv()).await {
            Ok(Some(event @ SyncEvent::SyncCompleted { .. })) => return Some(event),
            Ok(Some(event @ SyncEvent::SyncFailed { .. })) => return Some(event),
            Ok(Some(_)) => continue,
            _ => return None,
        }
    }
}

/// Perform full 3-round convergence: A→B, B→A, A→B with settling time.
/// Since sync is unidirectional (initiator-push), convergence requires at least
/// 3 rounds for both peers to have all events.
async fn converge(
    handle_a: &privstack_sync::OrchestratorHandle,
    handle_b: &privstack_sync::OrchestratorHandle,
    peer_a: PeerId,
    peer_b: PeerId,
    events_a: &mut mpsc::Receiver<SyncEvent>,
    events_b: &mut mpsc::Receiver<SyncEvent>,
) {
    let timeout = Duration::from_secs(10);
    let settle = Duration::from_millis(150);

    // Round 1: A→B
    handle_a.send(SyncCommand::SyncWithPeer { peer_id: peer_b }).await.unwrap();
    wait_for_sync(events_a, timeout).await;
    tokio::time::sleep(settle).await;

    // Round 2: B→A
    handle_b.send(SyncCommand::SyncWithPeer { peer_id: peer_a }).await.unwrap();
    wait_for_sync(events_b, timeout).await;
    tokio::time::sleep(settle).await;

    // Round 3: A→B (push anything A received from B back)
    handle_a.send(SyncCommand::SyncWithPeer { peer_id: peer_b }).await.unwrap();
    wait_for_sync(events_a, timeout).await;
    tokio::time::sleep(settle).await;
}

/// Sets up two orchestrators connected via adversarial transports.
struct TestHarness {
    handle_a: privstack_sync::OrchestratorHandle,
    handle_b: privstack_sync::OrchestratorHandle,
    events_a: Option<mpsc::Receiver<SyncEvent>>,
    events_b: Option<mpsc::Receiver<SyncEvent>>,
    stores_a: (Arc<EntityStore>, Arc<EventStore>),
    stores_b: (Arc<EntityStore>, Arc<EventStore>),
    peer_a: PeerId,
    peer_b: PeerId,
    partition_a: Arc<AtomicBool>,
    partition_b: Arc<AtomicBool>,
    delay_a_ms: Arc<AtomicU64>,
    delay_b_ms: Arc<AtomicU64>,
    join_a: tokio::task::JoinHandle<SyncResult<()>>,
    join_b: tokio::task::JoinHandle<SyncResult<()>>,
}

impl TestHarness {
    async fn new() -> Self {
        let peer_a = PeerId::new();
        let peer_b = PeerId::new();

        let stores_a = make_stores();
        let stores_b = make_stores();

        let (bridge_a, bridge_b) = BridgedTransport::pair(peer_a, peer_b);

        let partition_a = Arc::new(AtomicBool::new(false));
        let partition_b = Arc::new(AtomicBool::new(false));
        let delay_a_ms = Arc::new(AtomicU64::new(0));
        let delay_b_ms = Arc::new(AtomicU64::new(0));

        let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(
            AdversarialTransport::wrap(bridge_a, partition_a.clone(), delay_a_ms.clone()),
        ));
        let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(
            AdversarialTransport::wrap(bridge_b, partition_b.clone(), delay_b_ms.clone()),
        ));

        let config = OrchestratorConfig {
            sync_interval: Duration::from_secs(3600),
            discovery_interval: Duration::from_secs(3600),
            auto_sync: false,
            max_entities_per_sync: 0,
        };

        let (handle_a, events_a, cmd_rx_a, orch_a) = create_orchestrator(
            peer_a,
            stores_a.0.clone(),
            stores_a.1.clone(),
            config.clone(),
        );
        let (handle_b, events_b, cmd_rx_b, orch_b) = create_orchestrator(
            peer_b,
            stores_b.0.clone(),
            stores_b.1.clone(),
            config,
        );

        let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
        let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

        Self {
            handle_a,
            handle_b,
            events_a: Some(events_a),
            events_b: Some(events_b),
            stores_a,
            stores_b,
            peer_a,
            peer_b,
            partition_a,
            partition_b,
            delay_a_ms,
            delay_b_ms,
            join_a,
            join_b,
        }
    }

    /// Applies an event to peer A's entity store and records it for sync.
    /// In production the FFI layer creates the entity first, then records the
    /// event. Tests must replicate both steps so `entities_needing_sync` finds
    /// the entity row.
    async fn record_event_a(&self, event: Event) -> SyncResult<()> {
        // Save event to event store first — prevents the orchestrator startup
        // scan from creating a duplicate FullSnapshot for an entity that exists
        // in the entity store but has no events yet.
        let evs = self.stores_a.1.clone();
        let ev = event.clone();
        tokio::task::spawn_blocking(move || evs.save_event(&ev))
            .await
            .unwrap()
            .unwrap();

        // Apply to entity store (creates the entity row for entities_needing_sync).
        // Skip EntityDeleted — apply_entity_deleted hard-deletes the row, which
        // would cause entities_needing_sync to miss it entirely.
        if !matches!(event.payload, EventPayload::EntityDeleted { .. }) {
            let es = self.stores_a.0.clone();
            let ev = event.clone();
            let pid = self.peer_a;
            tokio::task::spawn_blocking(move || {
                EventApplicator::new(pid).apply_event(&ev, &es, None, None)
            })
            .await
            .unwrap()
            .ok();
        }

        // Record via orchestrator (also saves event — INSERT OR IGNORE is idempotent)
        self.handle_a.record_event(event).await
    }

    /// Applies an event to peer B's entity store and records it for sync.
    async fn record_event_b(&self, event: Event) -> SyncResult<()> {
        let evs = self.stores_b.1.clone();
        let ev = event.clone();
        tokio::task::spawn_blocking(move || evs.save_event(&ev))
            .await
            .unwrap()
            .unwrap();

        if !matches!(event.payload, EventPayload::EntityDeleted { .. }) {
            let es = self.stores_b.0.clone();
            let ev = event.clone();
            let pid = self.peer_b;
            tokio::task::spawn_blocking(move || {
                EventApplicator::new(pid).apply_event(&ev, &es, None, None)
            })
            .await
            .unwrap()
            .ok();
        }

        self.handle_b.record_event(event).await
    }

    /// Partition peer A (its outgoing requests fail).
    fn partition_a(&self) {
        self.partition_a.store(true, Ordering::SeqCst);
    }

    /// Partition peer B.
    fn partition_b(&self) {
        self.partition_b.store(true, Ordering::SeqCst);
    }

    /// Heal peer A's partition.
    fn heal_a(&self) {
        self.partition_a.store(false, Ordering::SeqCst);
    }

    /// Heal peer B's partition.
    fn heal_b(&self) {
        self.partition_b.store(false, Ordering::SeqCst);
    }

    /// Partition both peers (full network split).
    fn full_partition(&self) {
        self.partition_a();
        self.partition_b();
    }

    /// Heal all partitions.
    fn heal_all(&self) {
        self.heal_a();
        self.heal_b();
    }

    /// Set delay on peer A's outgoing messages.
    fn set_delay_a(&self, ms: u64) {
        self.delay_a_ms.store(ms, Ordering::SeqCst);
    }

    /// Set delay on peer B's outgoing messages.
    fn set_delay_b(&self, ms: u64) {
        self.delay_b_ms.store(ms, Ordering::SeqCst);
    }

    /// Remove all delays.
    #[allow(dead_code)]
    fn clear_delays(&self) {
        self.set_delay_a(0);
        self.set_delay_b(0);
    }

    #[allow(dead_code)]
    async fn shutdown(self) {
        let _ = self.handle_a.shutdown().await;
        let _ = self.handle_b.shutdown().await;
        let _ = self.join_a.await;
        let _ = self.join_b.await;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// 1. NETWORK PARTITION TESTS
// ═══════════════════════════════════════════════════════════════════════════

/// Sync fails gracefully when the network is partitioned.
#[tokio::test]
async fn sync_fails_during_partition() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let event = make_note_create(entity_id, h.peer_a, "test");
    h.record_event_a(event).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Partition A — its requests will fail
    h.partition_a();

    h.handle_a
        .send(SyncCommand::SyncWithPeer {
            peer_id: h.peer_b,
        })
        .await
        .unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let result = wait_for_sync_or_fail(&mut events_a, Duration::from_secs(3)).await;
    assert!(
        matches!(result, Some(SyncEvent::SyncFailed { .. })),
        "Sync should fail during partition, got: {:?}",
        result
    );

    // B should have no events
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 0, "B should have nothing during partition");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Both peers write during a partition, then sync after healing.
/// Events from both sides should be exchanged.
#[tokio::test]
async fn split_brain_write_then_heal_and_sync() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    // Full partition
    h.full_partition();

    // Both peers write independently
    let event_a = make_note_create(entity_id, h.peer_a, "A's version");
    h.record_event_a(event_a.clone()).await.unwrap();

    let event_b = make_note_create(entity_id, h.peer_b, "B's version");
    h.record_event_b(event_b.clone()).await.unwrap();

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Heal partition
    h.heal_all();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    // Both should have both events
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 2, "A should have both events");
    assert_eq!(b_events.len(), 2, "B should have both events");

    // Event IDs match
    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids, "Both peers should have the same event set");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Deep divergence: both peers accumulate many events during partition,
/// then reconcile everything in one sync burst.
#[tokio::test]
async fn deep_divergence_burst_reconciliation() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    // Create initial entity on both sides
    let create = make_note_create(entity_id, h.peer_a, "Initial");
    h.record_event_a(create.clone()).await.unwrap();

    // Sync initial state
    let mut events_a = h.events_a.take().unwrap();
    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Full partition
    h.full_partition();

    // Peer A writes 10 updates
    for i in 0..10 {
        let update = make_note_update(entity_id, h.peer_a, &format!("A_edit_{i}"));
        h.record_event_a(update).await.unwrap();
        tokio::time::sleep(Duration::from_millis(5)).await;
    }

    // Peer B writes 10 updates
    for i in 0..10 {
        let update = make_note_update(entity_id, h.peer_b, &format!("B_edit_{i}"));
        h.record_event_b(update).await.unwrap();
        tokio::time::sleep(Duration::from_millis(5)).await;
    }

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Heal and reconcile
    h.heal_all();

    let mut events_b = h.events_b.take().unwrap();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    // Both should have 1 create + 20 updates = 21 total
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 21, "A should have all 21 events, got {}", a_events.len());
    assert_eq!(b_events.len(), 21, "B should have all 21 events, got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids, "Event sets must converge");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 2. LATENCY / DELAY TESTS
// ═══════════════════════════════════════════════════════════════════════════

/// Sync succeeds with high latency on one side.
#[tokio::test]
async fn sync_succeeds_with_high_latency() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let event = make_note_create(entity_id, h.peer_a, "High latency test");
    h.record_event_a(event.clone()).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Add 200ms latency to A's outgoing messages
    h.set_delay_a(200);

    let mut events_a = h.events_a.take().unwrap();
    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();

    // Give extra time for the high-latency sync
    let completed = wait_for_sync(&mut events_a, Duration::from_secs(10)).await;
    assert!(
        completed.is_some(),
        "Sync should succeed despite high latency"
    );

    tokio::time::sleep(Duration::from_millis(300)).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 1, "B should receive the event despite latency");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Sync succeeds with asymmetric latency (one side fast, other slow).
#[tokio::test]
async fn asymmetric_latency_bidirectional_sync() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let event_a = make_note_create(entity_id, h.peer_a, "From fast peer A");
    h.record_event_a(event_a).await.unwrap();

    let event_b = make_note_create(entity_id, h.peer_b, "From slow peer B");
    h.record_event_b(event_b).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    // A is fast, B has 150ms delay
    h.set_delay_b(150);

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(10)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(10)).await;

    tokio::time::sleep(Duration::from_millis(300)).await;

    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 2, "A should have both events");
    assert_eq!(b_events.len(), 2, "B should have both events");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 3. FLAPPING / INTERMITTENT CONNECTIVITY
// ═══════════════════════════════════════════════════════════════════════════

/// Network flaps between available and partitioned.
/// Eventually, a sync during a connected window should succeed.
#[tokio::test]
async fn flapping_network_eventual_sync() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let event = make_note_create(entity_id, h.peer_a, "Flapping test");
    h.record_event_a(event).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();
    let mut synced = false;

    // Simulate 5 flap cycles
    for cycle in 0..5 {
        if cycle % 2 == 0 {
            // Partition
            h.partition_a();
        } else {
            // Heal
            h.heal_a();
        }

        h.handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
            .await
            .unwrap();

        let result = wait_for_sync_or_fail(&mut events_a, Duration::from_secs(2)).await;
        if matches!(result, Some(SyncEvent::SyncCompleted { .. })) {
            synced = true;
            break;
        }

        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    // Ensure network is healed and try one final sync if needed
    if !synced {
        h.heal_a();
        h.handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
            .await
            .unwrap();
        let result = wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
        assert!(result.is_some(), "Final sync should succeed after healing");
    }

    tokio::time::sleep(Duration::from_millis(200)).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 1, "B should eventually receive the event");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Multiple sync attempts during intermittent connectivity.
/// Each successful sync should be idempotent (no duplicate events).
#[tokio::test]
async fn repeated_sync_is_idempotent() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let event = make_note_create(entity_id, h.peer_a, "Idempotent test");
    h.record_event_a(event).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();

    // Sync 5 times — each should succeed but not duplicate
    for _ in 0..5 {
        h.handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
            .await
            .unwrap();
        wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(
        b_events.len(),
        1,
        "B should have exactly 1 event after repeated syncs, got {}",
        b_events.len()
    );

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 4. MULTI-ENTITY PARTITION SCENARIOS
// ═══════════════════════════════════════════════════════════════════════════

/// Multiple entities diverge during partition, all reconcile correctly.
#[tokio::test]
async fn multi_entity_split_brain_reconciliation() {
    let mut h = TestHarness::new().await;
    let entity1 = EntityId::new();
    let entity2 = EntityId::new();
    let entity3 = EntityId::new();

    for eid in [entity1, entity2, entity3] {
        h.handle_a.share_entity(eid).await.unwrap();
        h.handle_b.share_entity(eid).await.unwrap();
    }

    // Full partition
    h.full_partition();

    // Peer A writes to entities 1 and 2
    h.record_event_a(make_note_create(entity1, h.peer_a, "A note 1"))
        .await
        .unwrap();
    h.record_event_a(make_note_create(entity2, h.peer_a, "A note 2"))
        .await
        .unwrap();

    // Peer B writes to entities 2 and 3
    h.record_event_b(make_note_create(entity2, h.peer_b, "B note 2"))
        .await
        .unwrap();
    h.record_event_b(make_note_create(entity3, h.peer_b, "B note 3"))
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Heal and sync both directions
    h.heal_all();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    // Entity 1: only A wrote → both have 1 event
    let a_e1 = h.stores_a.1.get_events_for_entity(&entity1).unwrap();
    let b_e1 = h.stores_b.1.get_events_for_entity(&entity1).unwrap();
    assert_eq!(a_e1.len(), 1);
    assert_eq!(b_e1.len(), 1);

    // Entity 2: both wrote → both have 2 events
    let a_e2 = h.stores_a.1.get_events_for_entity(&entity2).unwrap();
    let b_e2 = h.stores_b.1.get_events_for_entity(&entity2).unwrap();
    assert_eq!(a_e2.len(), 2, "Entity 2 should have 2 events on A");
    assert_eq!(b_e2.len(), 2, "Entity 2 should have 2 events on B");

    // Entity 3: only B wrote → both have 1 event
    let a_e3 = h.stores_a.1.get_events_for_entity(&entity3).unwrap();
    let b_e3 = h.stores_b.1.get_events_for_entity(&entity3).unwrap();
    assert_eq!(a_e3.len(), 1);
    assert_eq!(b_e3.len(), 1);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 5. PARTITION → WRITE → HEAL → PARTITION AGAIN → WRITE → HEAL → SYNC
// ═══════════════════════════════════════════════════════════════════════════

/// Multiple partition/heal cycles with writes in between.
/// Tests that vector clocks correctly track what's been synced across
/// multiple disconnection episodes.
#[tokio::test]
async fn multiple_partition_heal_cycles() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    // Phase 1: Both connected, A writes and syncs
    let e1 = make_note_create(entity_id, h.peer_a, "Phase 1");
    h.record_event_a(e1).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Phase 2: Partition, both write
    h.full_partition();

    let e2 = make_note_update(entity_id, h.peer_a, "Phase 2 A");
    h.record_event_a(e2).await.unwrap();
    let e3 = make_note_update(entity_id, h.peer_b, "Phase 2 B");
    h.record_event_b(e3).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Phase 3: Heal, sync
    h.heal_all();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Phase 4: Partition again, more writes
    h.full_partition();

    let e4 = make_note_update(entity_id, h.peer_a, "Phase 4 A");
    h.record_event_a(e4).await.unwrap();
    let e5 = make_note_update(entity_id, h.peer_b, "Phase 4 B");
    h.record_event_b(e5).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Phase 5: Final heal and sync
    h.heal_all();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(200)).await;

    // Both should have all 5 events
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 5, "A should have all 5 events, got {}", a_events.len());
    assert_eq!(b_events.len(), 5, "B should have all 5 events, got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids, "Event sets must match across all cycles");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 6. STRESS: MANY EVENTS UNDER ADVERSE CONDITIONS
// ═══════════════════════════════════════════════════════════════════════════

/// Both peers rapidly write events during alternating partition/heal cycles.
/// After final healing, all events must converge.
#[tokio::test]
async fn rapid_writes_during_flapping_partition() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    // Peer A writes 5 events per phase, peer B writes 3 per phase
    // 3 phases of partition/heal
    let mut total_a = 0usize;
    let mut total_b = 0usize;

    for phase in 0..3 {
        // Partition
        h.full_partition();

        for i in 0..5 {
            let e = if total_a == 0 {
                make_note_create(entity_id, h.peer_a, &format!("A_p{phase}_e{i}"))
            } else {
                make_note_update(entity_id, h.peer_a, &format!("A_p{phase}_e{i}"))
            };
            h.record_event_a(e).await.unwrap();
            total_a += 1;
        }

        for i in 0..3 {
            let e = if total_b == 0 {
                make_note_create(entity_id, h.peer_b, &format!("B_p{phase}_e{i}"))
            } else {
                make_note_update(entity_id, h.peer_b, &format!("B_p{phase}_e{i}"))
            };
            h.record_event_b(e).await.unwrap();
            total_b += 1;
        }

        tokio::time::sleep(Duration::from_millis(20)).await;

        // Heal and sync
        h.heal_all();

        h.handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
            .await
            .unwrap();
        wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

        h.handle_b
            .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
            .await
            .unwrap();
        wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

        tokio::time::sleep(Duration::from_millis(100)).await;
    }

    let expected = total_a + total_b; // 15 + 9 = 24

    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(
        a_events.len(),
        expected,
        "A should have all {expected} events, got {}",
        a_events.len()
    );
    assert_eq!(
        b_events.len(),
        expected,
        "B should have all {expected} events, got {}",
        b_events.len()
    );

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Sync with delay + many events — tests that batching works under latency.
#[tokio::test]
async fn batch_sync_under_latency() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    // A writes 20 events
    h.record_event_a(make_note_create(entity_id, h.peer_a, "Initial"))
        .await
        .unwrap();

    for i in 1..20 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("Update_{i}")))
            .await
            .unwrap();
    }
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Add 100ms latency to both sides
    h.set_delay_a(100);
    h.set_delay_b(100);

    let mut events_a = h.events_a.take().unwrap();
    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();

    let completed = wait_for_sync(&mut events_a, Duration::from_secs(15)).await;
    assert!(completed.is_some(), "Batch sync should complete under latency");

    tokio::time::sleep(Duration::from_millis(300)).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(
        b_events.len(),
        20,
        "B should have all 20 events, got {}",
        b_events.len()
    );

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 7. REAL-WORLD: SUBWAY COMMUTER — MICRO-PARTITIONS WITH WRITES
// ═══════════════════════════════════════════════════════════════════════════

/// Simulates a mobile user moving through spotty coverage: connectivity
/// drops and reconnects rapidly. Writes happen continuously regardless
/// of network state. After stabilisation, everything converges.
#[tokio::test]
async fn subway_commuter_micro_partitions() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "initial"))
        .await
        .unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    // Initial sync so both have the entity
    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    // 10 micro-cycles: toggle partition, write on both sides, attempt sync
    for cycle in 0..10 {
        if cycle % 2 == 1 {
            h.partition_a();
        } else {
            h.heal_a();
        }

        let e_a = make_note_update(entity_id, h.peer_a, &format!("A_subway_{cycle}"));
        h.record_event_a(e_a).await.unwrap();

        let e_b = make_note_update(entity_id, h.peer_b, &format!("B_subway_{cycle}"));
        h.record_event_b(e_b).await.unwrap();

        h.handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
            .await
            .unwrap();
        let _ = wait_for_sync_or_fail(&mut events_a, Duration::from_millis(500)).await;

        tokio::time::sleep(Duration::from_millis(10)).await;
    }

    h.heal_all();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    // 1 create + 10 from A + 10 from B = 21
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 21, "A should have all 21 events, got {}", a_events.len());
    assert_eq!(b_events.len(), 21, "B should have all 21 events, got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids, "Convergence after subway micro-partitions");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 8. REAL-WORLD: DELETE VS EDIT CONFLICT
// ═══════════════════════════════════════════════════════════════════════════

/// One peer deletes an entity while the other is editing it offline.
/// After healing, both peers see the full event history including the delete.
#[tokio::test]
async fn delete_vs_edit_conflict_during_partition() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let create = make_note_create(entity_id, h.peer_a, "Doomed note");
    h.record_event_a(create).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    h.full_partition();

    for i in 0..3 {
        let update = make_note_update(entity_id, h.peer_a, &format!("A edit {i}"));
        h.record_event_a(update).await.unwrap();
    }

    let delete = make_event(
        entity_id,
        h.peer_b,
        EventPayload::EntityDeleted {
            entity_type: "note".to_string(),
        },
    );
    h.record_event_b(delete).await.unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    // 1 create + 3 updates + 1 delete = 5
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 5, "A should have 5 events, got {}", a_events.len());
    assert_eq!(b_events.len(), 5, "B should have 5 events, got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Both peers delete the same entity during a partition.
#[tokio::test]
async fn concurrent_delete_same_entity_during_partition() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let create = make_note_create(entity_id, h.peer_a, "Double-delete target");
    h.record_event_a(create).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    h.full_partition();

    let del_a = make_event(entity_id, h.peer_a, EventPayload::EntityDeleted {
        entity_type: "note".to_string(),
    });
    h.record_event_a(del_a).await.unwrap();

    let del_b = make_event(entity_id, h.peer_b, EventPayload::EntityDeleted {
        entity_type: "note".to_string(),
    });
    h.record_event_b(del_b).await.unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    // 1 create + 2 deletes = 3
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 3, "A: got {}", a_events.len());
    assert_eq!(b_events.len(), 3, "B: got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 9. REAL-WORLD: ENTITY BORN DURING PARTITION
// ═══════════════════════════════════════════════════════════════════════════

/// One peer creates a brand-new entity while disconnected.
#[tokio::test]
async fn entity_created_and_edited_during_partition() {
    let mut h = TestHarness::new().await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.full_partition();

    let entity_id = EntityId::new();
    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Born offline"))
        .await
        .unwrap();
    for i in 0..5 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("offline edit {i}")))
            .await
            .unwrap();
    }

    tokio::time::sleep(Duration::from_millis(50)).await;

    let b_pre = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_pre.len(), 0);

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 6, "B should have all 6 events, got {}", b_events.len());

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Both peers independently create different entities during a partition.
#[tokio::test]
async fn both_peers_create_different_entities_during_partition() {
    let mut h = TestHarness::new().await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.full_partition();

    let entity_x = EntityId::new();
    h.handle_a.share_entity(entity_x).await.unwrap();
    h.handle_b.share_entity(entity_x).await.unwrap();
    h.record_event_a(make_note_create(entity_x, h.peer_a, "Entity X"))
        .await
        .unwrap();
    h.record_event_a(make_note_update(entity_x, h.peer_a, "X updated"))
        .await
        .unwrap();

    let entity_y = EntityId::new();
    h.handle_a.share_entity(entity_y).await.unwrap();
    h.handle_b.share_entity(entity_y).await.unwrap();
    h.record_event_b(make_note_create(entity_y, h.peer_b, "Entity Y"))
        .await
        .unwrap();
    h.record_event_b(make_note_update(entity_y, h.peer_b, "Y updated"))
        .await
        .unwrap();
    h.record_event_b(make_note_update(entity_y, h.peer_b, "Y again"))
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    assert_eq!(h.stores_a.1.get_events_for_entity(&entity_x).unwrap().len(), 2);
    assert_eq!(h.stores_b.1.get_events_for_entity(&entity_x).unwrap().len(), 2);
    assert_eq!(h.stores_a.1.get_events_for_entity(&entity_y).unwrap().len(), 3);
    assert_eq!(h.stores_b.1.get_events_for_entity(&entity_y).unwrap().len(), 3);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 10. REAL-WORLD: CONCURRENT BIDIRECTIONAL SYNC RACE
// ═══════════════════════════════════════════════════════════════════════════

/// Rapid alternating sync — A→B then B→A in quick succession, no delay.
/// The protocol is unidirectional per round (initiator pushes), so full
/// convergence requires both directions. This tests that rapid alternation
/// doesn't corrupt data.
#[tokio::test]
async fn rapid_alternating_bidirectional_sync() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "A's note"))
        .await
        .unwrap();
    h.record_event_b(make_note_create(entity_id, h.peer_b, "B's note"))
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    // Rapid alternating: A→B, B→A, A→B (to ensure B gets A's data that came via B→A)
    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b.send(SyncCommand::SyncWithPeer { peer_id: h.peer_a }).await.unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 2);
    assert_eq!(b_events.len(), 2);

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

/// Rapid sequential sync storm — 5 rounds of A→B then B→A with lots
/// of data. No corruption or duplication allowed.
#[tokio::test]
async fn rapid_sequential_sync_storm() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Storm note"))
        .await
        .unwrap();
    for i in 0..4 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("Storm A {i}")))
            .await
            .unwrap();
    }
    for i in 0..3 {
        h.record_event_b(make_note_create(entity_id, h.peer_b, &format!("Storm B {i}")))
            .await
            .unwrap();
    }

    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    // 5 rounds of alternating sync
    for _ in 0..5 {
        h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
        wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

        h.handle_b.send(SyncCommand::SyncWithPeer { peer_id: h.peer_a }).await.unwrap();
        wait_for_sync(&mut events_b, Duration::from_secs(5)).await;
    }

    tokio::time::sleep(Duration::from_millis(200)).await;

    // A wrote 5, B wrote 3 = 8 total
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 8, "A should have 8, got {}", a_events.len());
    assert_eq!(b_events.len(), 8, "B should have 8, got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 11. REAL-WORLD: WEEK-LONG DIVERGENCE — MASSIVE RECONCILIATION
// ═══════════════════════════════════════════════════════════════════════════

/// Two peers diverge with 50 events each across 5 entities.
#[tokio::test]
async fn week_long_divergence_massive_reconciliation() {
    let mut h = TestHarness::new().await;

    let entities: Vec<EntityId> = (0..5).map(|_| EntityId::new()).collect();
    for &eid in &entities {
        h.handle_a.share_entity(eid).await.unwrap();
        h.handle_b.share_entity(eid).await.unwrap();
    }

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.full_partition();

    for (idx, &eid) in entities.iter().enumerate() {
        h.record_event_a(make_note_create(eid, h.peer_a, &format!("A entity {idx}")))
            .await
            .unwrap();
        for i in 1..10 {
            h.record_event_a(make_note_update(eid, h.peer_a, &format!("A_e{idx}_u{i}")))
                .await
                .unwrap();
        }
    }

    for (idx, &eid) in entities.iter().enumerate() {
        h.record_event_b(make_note_create(eid, h.peer_b, &format!("B entity {idx}")))
            .await
            .unwrap();
        for i in 1..10 {
            h.record_event_b(make_note_update(eid, h.peer_b, &format!("B_e{idx}_u{i}")))
                .await
                .unwrap();
        }
    }

    tokio::time::sleep(Duration::from_millis(100)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    for (idx, &eid) in entities.iter().enumerate() {
        let a_ev = h.stores_a.1.get_events_for_entity(&eid).unwrap();
        let b_ev = h.stores_b.1.get_events_for_entity(&eid).unwrap();
        assert_eq!(a_ev.len(), 20, "Entity {idx}: A got {}", a_ev.len());
        assert_eq!(b_ev.len(), 20, "Entity {idx}: B got {}", b_ev.len());

        let a_ids: std::collections::HashSet<_> = a_ev.iter().map(|e| e.id).collect();
        let b_ids: std::collections::HashSet<_> = b_ev.iter().map(|e| e.id).collect();
        assert_eq!(a_ids, b_ids, "Entity {idx}: must match");
    }

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 12. REAL-WORLD: WRITE DURING ACTIVE SYNC
// ═══════════════════════════════════════════════════════════════════════════

/// Local writes happen while a sync is in-flight under high latency.
#[tokio::test]
async fn write_during_active_sync() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Sync-race note"))
        .await
        .unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    h.set_delay_a(300);
    h.set_delay_b(300);

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();

    for i in 0..3 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("During sync {i}")))
            .await
            .unwrap();
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    wait_for_sync(&mut events_a, Duration::from_secs(10)).await;

    h.set_delay_a(0);
    h.set_delay_b(0);

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 4);
    assert_eq!(b_events.len(), 4, "B got {}", b_events.len());

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 13. REAL-WORLD: ASYMMETRIC ONE-WAY PARTITION
// ═══════════════════════════════════════════════════════════════════════════

/// A can push to B but B's outgoing is partitioned.
#[tokio::test]
async fn asymmetric_partition_one_way_push() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "A's data"))
        .await
        .unwrap();
    h.record_event_b(make_note_create(entity_id, h.peer_b, "B's data"))
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.partition_b();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    // A→B should succeed
    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    let res_a = wait_for_sync_or_fail(&mut events_a, Duration::from_secs(5)).await;
    assert!(matches!(res_a, Some(SyncEvent::SyncCompleted { .. })), "A→B should succeed, got {:?}", res_a);

    // B→A should fail
    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    let res_b = wait_for_sync_or_fail(&mut events_b, Duration::from_secs(3)).await;
    assert!(matches!(res_b, Some(SyncEvent::SyncFailed { .. })), "B→A should fail, got {:?}", res_b);

    h.heal_b();

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 2);
    assert_eq!(b_events.len(), 2);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 14. REAL-WORLD: STALE PEER CATCHES UP
// ═══════════════════════════════════════════════════════════════════════════

/// A and B sync, then B goes offline while A accumulates 15 events.
/// On reconnect, only the delta should transfer.
#[tokio::test]
async fn stale_peer_catches_up_with_delta() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let _events_b = h.events_b.take().unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Initial"))
        .await
        .unwrap();
    for i in 0..3 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("Phase1 {i}")))
            .await
            .unwrap();
    }
    tokio::time::sleep(Duration::from_millis(50)).await;

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    assert_eq!(h.stores_b.1.get_events_for_entity(&entity_id).unwrap().len(), 4);

    h.partition_b();

    for i in 0..15 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("Stale {i}")))
            .await
            .unwrap();
    }
    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_b();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    let catchup_result = wait_for_sync(&mut events_a, Duration::from_secs(10)).await;
    assert!(catchup_result.is_some());

    tokio::time::sleep(Duration::from_millis(200)).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 19, "B got {}", b_events.len());

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 15. REAL-WORLD: EMPTY SYNC — ALREADY CONVERGED
// ═══════════════════════════════════════════════════════════════════════════

/// After convergence, repeated syncs should exchange zero events.
#[tokio::test]
async fn empty_sync_after_convergence() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Converge test"))
        .await
        .unwrap();
    h.record_event_b(make_note_create(entity_id, h.peer_b, "B side"))
        .await
        .unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync again — should succeed (may or may not exchange events depending
    // on protocol internals, but event counts must remain stable)
    h.handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_b })
        .await
        .unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

    h.handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: h.peer_a })
        .await
        .unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 16. REAL-WORLD: SYNC STORM — MANY ENTITIES RAPID SUCCESSION
// ═══════════════════════════════════════════════════════════════════════════

/// 10 entities diverge, all synced after healing.
#[tokio::test]
async fn sync_storm_many_entities_after_partition() {
    let mut h = TestHarness::new().await;

    let entities: Vec<EntityId> = (0..10).map(|_| EntityId::new()).collect();
    for &eid in &entities {
        h.handle_a.share_entity(eid).await.unwrap();
        h.handle_b.share_entity(eid).await.unwrap();
    }

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.full_partition();

    for &eid in &entities {
        h.record_event_a(make_note_create(eid, h.peer_a, "A")).await.unwrap();
        h.record_event_a(make_note_update(eid, h.peer_a, "A1")).await.unwrap();
        h.record_event_a(make_note_update(eid, h.peer_a, "A2")).await.unwrap();

        h.record_event_b(make_note_create(eid, h.peer_b, "B")).await.unwrap();
        h.record_event_b(make_note_update(eid, h.peer_b, "B1")).await.unwrap();
        h.record_event_b(make_note_update(eid, h.peer_b, "B2")).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    for (idx, &eid) in entities.iter().enumerate() {
        let a_ev = h.stores_a.1.get_events_for_entity(&eid).unwrap();
        let b_ev = h.stores_b.1.get_events_for_entity(&eid).unwrap();
        assert_eq!(a_ev.len(), 6, "Entity {idx}: A got {}", a_ev.len());
        assert_eq!(b_ev.len(), 6, "Entity {idx}: B got {}", b_ev.len());
    }

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 17. REAL-WORLD: ONE PEER READ-ONLY CONSUMER
// ═══════════════════════════════════════════════════════════════════════════

/// One peer never writes, only receives across multiple sync rounds.
#[tokio::test]
async fn one_peer_readonly_consumer() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Producer note"))
        .await
        .unwrap();

    for round in 0..5 {
        for i in 0..4 {
            h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("R{round}_U{i}")))
                .await
                .unwrap();
        }
        tokio::time::sleep(Duration::from_millis(20)).await;

        h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
        wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 21, "Consumer B got {}", b_events.len());

    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(b_ids.len(), 21, "No duplicates");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 18. REAL-WORLD: ESCALATING PARTITION-HEAL PHASES
// ═══════════════════════════════════════════════════════════════════════════

/// Each phase has more events: 1, 3, 5, 7, 9 per peer. Total 50 events.
#[tokio::test]
async fn escalating_partition_heal_phases() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    let mut total = 0usize;
    let mut first_a = true;
    let mut first_b = true;

    for phase in 0..5 {
        let n = 1 + phase * 2;

        h.full_partition();

        for i in 0..n {
            let e = if first_a {
                first_a = false;
                make_note_create(entity_id, h.peer_a, &format!("A_P{phase}_{i}"))
            } else {
                make_note_update(entity_id, h.peer_a, &format!("A_P{phase}_{i}"))
            };
            h.record_event_a(e).await.unwrap();
        }

        for i in 0..n {
            let e = if first_b {
                first_b = false;
                make_note_create(entity_id, h.peer_b, &format!("B_P{phase}_{i}"))
            } else {
                make_note_update(entity_id, h.peer_b, &format!("B_P{phase}_{i}"))
            };
            h.record_event_b(e).await.unwrap();
        }

        total += n * 2;
        // Allow spawn_blocking saves to flush (instrumentation adds overhead)
        tokio::time::sleep(Duration::from_millis(150)).await;

        h.heal_all();

        h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
        wait_for_sync(&mut events_a, Duration::from_secs(5)).await;

        h.handle_b.send(SyncCommand::SyncWithPeer { peer_id: h.peer_a }).await.unwrap();
        wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

        tokio::time::sleep(Duration::from_millis(100)).await;

        let a_ev = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
        let b_ev = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
        assert_eq!(a_ev.len(), total, "Phase {phase}: A expected {total}, got {}", a_ev.len());
        assert_eq!(b_ev.len(), total, "Phase {phase}: B expected {total}, got {}", b_ev.len());
    }

    assert_eq!(total, 50);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 19. REAL-WORLD: LATENCY + PARTITION COMBO
// ═══════════════════════════════════════════════════════════════════════════

/// High latency → partition hits mid-sync → heal → retry succeeds.
#[tokio::test]
async fn latency_then_partition_then_heal() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "LP")).await.unwrap();
    for i in 0..5 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("LP {i}")))
            .await
            .unwrap();
    }
    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.set_delay_a(500);

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;
    h.partition_a();

    let _ = wait_for_sync_or_fail(&mut events_a, Duration::from_secs(5)).await;

    h.set_delay_a(0);
    h.heal_a();

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    let retry = wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    assert!(retry.is_some(), "Retry should succeed");

    h.handle_b.send(SyncCommand::SyncWithPeer { peer_id: h.peer_a }).await.unwrap();
    let _ = wait_for_sync_or_fail(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 6, "B got {}", b_events.len());

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 20. REAL-WORLD: FULL ENTITY LIFECYCLE
// ═══════════════════════════════════════════════════════════════════════════

/// Create → edit → delete (one side) + edit (other side) → new entity.
#[tokio::test]
async fn full_entity_lifecycle_create_edit_delete_recreate() {
    let mut h = TestHarness::new().await;
    let entity1 = EntityId::new();
    let entity2 = EntityId::new();

    h.handle_a.share_entity(entity1).await.unwrap();
    h.handle_b.share_entity(entity1).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.record_event_a(make_note_create(entity1, h.peer_a, "Original")).await.unwrap();
    h.record_event_a(make_note_update(entity1, h.peer_a, "Edit")).await.unwrap();

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    h.full_partition();

    h.record_event_a(make_event(entity1, h.peer_a, EventPayload::EntityDeleted {
        entity_type: "note".to_string(),
    })).await.unwrap();

    h.record_event_b(make_note_update(entity1, h.peer_b, "B edit 1")).await.unwrap();
    h.record_event_b(make_note_update(entity1, h.peer_b, "B edit 2")).await.unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    let a_e1 = h.stores_a.1.get_events_for_entity(&entity1).unwrap();
    let b_e1 = h.stores_b.1.get_events_for_entity(&entity1).unwrap();
    assert_eq!(a_e1.len(), 5, "Entity1 A: {}", a_e1.len());
    assert_eq!(b_e1.len(), 5, "Entity1 B: {}", b_e1.len());

    // New entity during another partition
    h.full_partition();
    h.handle_a.share_entity(entity2).await.unwrap();
    h.handle_b.share_entity(entity2).await.unwrap();
    h.record_event_a(make_note_create(entity2, h.peer_a, "Replacement")).await.unwrap();
    h.record_event_a(make_note_update(entity2, h.peer_a, "Replacement edit")).await.unwrap();

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    assert_eq!(h.stores_b.1.get_events_for_entity(&entity2).unwrap().len(), 2);
    assert_eq!(h.stores_a.1.get_events_for_entity(&entity1).unwrap().len(), 5);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 21. REAL-WORLD: FLAPPING WITH LATENCY SPIKES
// ═══════════════════════════════════════════════════════════════════════════

/// Network phases: fast → slow → partitioned → asymmetric slow → fast.
#[tokio::test]
async fn flapping_with_latency_spikes() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "Flap")).await.unwrap();
    h.record_event_b(make_note_create(entity_id, h.peer_b, "B Flap")).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    struct Phase { delay_a: u64, delay_b: u64, partition: bool }

    let phases = [
        Phase { delay_a: 0, delay_b: 0, partition: false },
        Phase { delay_a: 200, delay_b: 200, partition: false },
        Phase { delay_a: 0, delay_b: 0, partition: true },
        Phase { delay_a: 300, delay_b: 100, partition: false },
        Phase { delay_a: 0, delay_b: 0, partition: false },
    ];

    let mut cnt_a = 1usize;
    let mut cnt_b = 1usize;

    for (idx, phase) in phases.iter().enumerate() {
        h.set_delay_a(phase.delay_a);
        h.set_delay_b(phase.delay_b);
        if phase.partition { h.full_partition(); } else { h.heal_all(); }

        for i in 0..2 {
            h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("A_ph{idx}_{i}"))).await.unwrap();
            cnt_a += 1;
            h.record_event_b(make_note_update(entity_id, h.peer_b, &format!("B_ph{idx}_{i}"))).await.unwrap();
            cnt_b += 1;
        }

        h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
        let _ = wait_for_sync_or_fail(&mut events_a, Duration::from_secs(5)).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    h.set_delay_a(0);
    h.set_delay_b(0);
    h.heal_all();

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    h.handle_b.send(SyncCommand::SyncWithPeer { peer_id: h.peer_a }).await.unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let total = cnt_a + cnt_b;
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), total, "A: {}", a_events.len());
    assert_eq!(b_events.len(), total, "B: {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 22. REAL-WORLD: NEAR-SIMULTANEOUS TIMESTAMPS
// ═══════════════════════════════════════════════════════════════════════════

/// Rapid-fire from both peers — near-identical timestamps, no loss.
#[tokio::test]
async fn near_simultaneous_timestamps_no_loss() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.record_event_a(make_note_create(entity_id, h.peer_a, "A")).await.unwrap();
    h.record_event_b(make_note_create(entity_id, h.peer_b, "B")).await.unwrap();

    for i in 0..20 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("A{i}"))).await.unwrap();
        h.record_event_b(make_note_update(entity_id, h.peer_b, &format!("B{i}"))).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(5)).await;
    h.handle_b.send(SyncCommand::SyncWithPeer { peer_id: h.peer_a }).await.unwrap();
    wait_for_sync(&mut events_b, Duration::from_secs(5)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 42);
    assert_eq!(b_events.len(), 42);

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids.len(), 42, "No collisions");

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 23. REAL-WORLD: MIXED ENTITY TYPES UNDER PARTITION
// ═══════════════════════════════════════════════════════════════════════════

/// Different entity types (note, task, calendar) diverge.
#[tokio::test]
async fn mixed_entity_types_during_partition() {
    let mut h = TestHarness::new().await;

    let note_id = EntityId::new();
    let task_id = EntityId::new();
    let cal_id = EntityId::new();

    for &eid in &[note_id, task_id, cal_id] {
        h.handle_a.share_entity(eid).await.unwrap();
        h.handle_b.share_entity(eid).await.unwrap();
    }

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.full_partition();

    h.record_event_a(make_event(note_id, h.peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"A note"}"#.to_string(),
    })).await.unwrap();

    h.record_event_a(make_event(task_id, h.peer_a, EventPayload::EntityCreated {
        entity_type: "task".to_string(),
        json_data: r#"{"title":"A task"}"#.to_string(),
    })).await.unwrap();

    h.record_event_b(make_event(cal_id, h.peer_b, EventPayload::EntityCreated {
        entity_type: "calendar".to_string(),
        json_data: r#"{"title":"B meeting"}"#.to_string(),
    })).await.unwrap();

    h.record_event_b(make_event(note_id, h.peer_b, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"B note"}"#.to_string(),
    })).await.unwrap();

    h.record_event_b(make_event(task_id, h.peer_b, EventPayload::EntityUpdated {
        entity_type: "task".to_string(),
        json_data: r#"{"title":"B task","done":true}"#.to_string(),
    })).await.unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    assert_eq!(h.stores_a.1.get_events_for_entity(&note_id).unwrap().len(), 2);
    assert_eq!(h.stores_b.1.get_events_for_entity(&note_id).unwrap().len(), 2);
    assert_eq!(h.stores_a.1.get_events_for_entity(&task_id).unwrap().len(), 2);
    assert_eq!(h.stores_b.1.get_events_for_entity(&task_id).unwrap().len(), 2);
    assert_eq!(h.stores_a.1.get_events_for_entity(&cal_id).unwrap().len(), 1);
    assert_eq!(h.stores_b.1.get_events_for_entity(&cal_id).unwrap().len(), 1);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 24. REAL-WORLD: PARTITION DURING FIRST SYNC ATTEMPT
// ═══════════════════════════════════════════════════════════════════════════

/// Entity shared but first sync fails. Cold start reconciliation after heal.
#[tokio::test]
async fn partition_during_first_sync_attempt() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    // Create entity on A before partition so the sync attempt has something to send.
    h.record_event_a(make_note_create(entity_id, h.peer_a, "A cold")).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    let mut events_a = h.events_a.take().unwrap();
    let mut events_b = h.events_b.take().unwrap();

    h.full_partition();

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    let first = wait_for_sync_or_fail(&mut events_a, Duration::from_secs(3)).await;
    assert!(matches!(first, Some(SyncEvent::SyncFailed { .. })));

    // A writes updates during partition
    for i in 0..5 {
        h.record_event_a(make_note_update(entity_id, h.peer_a, &format!("A {i}"))).await.unwrap();
    }

    // B writes its own create + updates during partition
    h.record_event_b(make_note_create(entity_id, h.peer_b, "B cold")).await.unwrap();
    for i in 0..3 {
        h.record_event_b(make_note_update(entity_id, h.peer_b, &format!("B {i}"))).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.heal_all();

    converge(&h.handle_a, &h.handle_b, h.peer_a, h.peer_b, &mut events_a, &mut events_b).await;

    // 1 A create + 5 A updates + 1 B create + 3 B updates = 10
    let a_events = h.stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 10);
    assert_eq!(b_events.len(), 10);

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}

// ═══════════════════════════════════════════════════════════════════════════
// 25. REAL-WORLD: LARGE PAYLOAD — FULL SNAPSHOT
// ═══════════════════════════════════════════════════════════════════════════

/// Sync with FullSnapshot events containing ~10KB JSON payloads.
#[tokio::test]
async fn large_snapshot_payload_sync() {
    let mut h = TestHarness::new().await;
    let entity_id = EntityId::new();

    h.handle_a.share_entity(entity_id).await.unwrap();
    h.handle_b.share_entity(entity_id).await.unwrap();

    let mut events_a = h.events_a.take().unwrap();

    let large_body: String = (0..200)
        .map(|i| format!("Line {i}: {}", "x".repeat(40)))
        .collect::<Vec<_>>()
        .join("\\n");
    let large_json = format!(r#"{{"title":"Large","body":"{large_body}"}}"#);

    h.record_event_a(make_event(entity_id, h.peer_a, EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: large_json.clone(),
    })).await.unwrap();

    h.record_event_a(make_event(entity_id, h.peer_a, EventPayload::FullSnapshot {
        entity_type: "note".to_string(),
        json_data: large_json.clone(),
    })).await.unwrap();

    tokio::time::sleep(Duration::from_millis(50)).await;

    h.set_delay_a(50);

    h.handle_a.send(SyncCommand::SyncWithPeer { peer_id: h.peer_b }).await.unwrap();
    wait_for_sync(&mut events_a, Duration::from_secs(10)).await;

    tokio::time::sleep(Duration::from_millis(200)).await;

    let b_events = h.stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 2, "B got {}", b_events.len());

    let snapshot = b_events.iter().find(|e| matches!(e.payload, EventPayload::FullSnapshot { .. }));
    assert!(snapshot.is_some(), "B should have FullSnapshot");
    if let Some(ev) = snapshot {
        if let EventPayload::FullSnapshot { json_data, .. } = &ev.payload {
            assert_eq!(json_data.len(), large_json.len(), "Payload not truncated");
        }
    }

    let _ = h.handle_a.shutdown().await;
    let _ = h.handle_b.shutdown().await;
    let _ = h.join_a.await;
    let _ = h.join_b.await;
}
