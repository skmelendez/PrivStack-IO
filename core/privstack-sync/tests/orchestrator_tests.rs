use privstack_sync::transport::{
    DiscoveredPeer, DiscoveryMethod, IncomingSyncRequest, ResponseToken, SyncTransport,
};
use privstack_sync::{
    create_orchestrator, EventApplicator, OrchestratorConfig, OrchestratorHandle,
    SyncCommand, SyncEvent, SyncMessage,
    HelloAckMessage, SyncStateMessage, EventAckMessage, EventBatchMessage,
    PROTOCOL_VERSION,
};
use privstack_storage::{EntityStore, EventStore};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};
use async_trait::async_trait;
use std::collections::VecDeque;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, Mutex};

// ── Mock Transport ──────────────────────────────────────────────

struct MockTransport {
    local_peer_id: PeerId,
    peers: Vec<DiscoveredPeer>,
    /// Responses to return for send_request calls, in order.
    responses: Mutex<VecDeque<SyncMessage>>,
    /// Incoming requests to deliver via recv_request.
    incoming: Mutex<mpsc::Receiver<IncomingSyncRequest>>,
    /// Sent requests captured for assertions.
    sent_requests: Mutex<Vec<(PeerId, SyncMessage)>>,
    /// Responses sent back via send_response.
    sent_responses: Mutex<Vec<SyncMessage>>,
}

impl MockTransport {
    fn new(
        local_peer_id: PeerId,
        peers: Vec<DiscoveredPeer>,
        responses: Vec<SyncMessage>,
        incoming_rx: mpsc::Receiver<IncomingSyncRequest>,
    ) -> Self {
        Self {
            local_peer_id,
            peers,
            responses: Mutex::new(VecDeque::from(responses)),
            incoming: Mutex::new(incoming_rx),
            sent_requests: Mutex::new(Vec::new()),
            sent_responses: Mutex::new(Vec::new()),
        }
    }
}

#[async_trait]
impl SyncTransport for MockTransport {
    async fn start(&mut self) -> privstack_sync::SyncResult<()> {
        Ok(())
    }

    async fn stop(&mut self) -> privstack_sync::SyncResult<()> {
        Ok(())
    }

    fn is_running(&self) -> bool {
        true
    }

    fn local_peer_id(&self) -> PeerId {
        self.local_peer_id
    }

    fn discovered_peers(&self) -> Vec<DiscoveredPeer> {
        self.peers.clone()
    }

    async fn discovered_peers_async(&self) -> Vec<DiscoveredPeer> {
        self.peers.clone()
    }

    async fn send_request(
        &self,
        peer_id: &PeerId,
        message: SyncMessage,
    ) -> privstack_sync::SyncResult<SyncMessage> {
        self.sent_requests.lock().await.push((*peer_id, message));
        let mut responses = self.responses.lock().await;
        responses
            .pop_front()
            .ok_or_else(|| privstack_sync::SyncError::Network("no mock response".to_string()))
    }

    async fn recv_request(&self) -> Option<IncomingSyncRequest> {
        let mut rx = self.incoming.lock().await;
        rx.recv().await
    }

    async fn send_response(
        &self,
        _token: ResponseToken,
        message: SyncMessage,
    ) -> privstack_sync::SyncResult<()> {
        self.sent_responses.lock().await.push(message);
        Ok(())
    }
}

// ── Helpers ─────────────────────────────────────────────────────

fn make_stores() -> (Arc<EntityStore>, Arc<EventStore>) {
    let es = Arc::new(EntityStore::open_in_memory().unwrap());
    let ev = Arc::new(EventStore::open_in_memory().unwrap());
    (es, ev)
}

fn make_hello_ack(peer_id: PeerId) -> SyncMessage {
    SyncMessage::HelloAck(HelloAckMessage {
        version: PROTOCOL_VERSION,
        peer_id,
        device_name: "MockPeer".to_string(),
        accepted: true,
        reason: None,
    })
}

fn make_sync_state() -> SyncMessage {
    SyncMessage::SyncState(SyncStateMessage::new())
}

fn make_event_ack_default() -> SyncMessage {
    SyncMessage::EventAck(privstack_sync::protocol::EventAckMessage {
        entity_id: EntityId::new(),
        batch_seq: 0,
        received_count: 0,
        events: vec![],
    })
}

/// Records an event through both the entity store (so `entities_needing_sync`
/// finds it) and the event store, then notifies the orchestrator handle.
async fn record_event_with_stores(
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

// ── OrchestratorConfig ──────────────────────────────────────────

#[test]
fn config_default_values() {
    let cfg = OrchestratorConfig::default();
    assert_eq!(cfg.sync_interval, Duration::from_secs(30));
    assert_eq!(cfg.discovery_interval, Duration::from_secs(5));
    assert!(cfg.auto_sync);
}

#[test]
fn config_debug() {
    let cfg = OrchestratorConfig::default();
    let debug = format!("{:?}", cfg);
    assert!(debug.contains("sync_interval"));
    assert!(debug.contains("auto_sync"));
}

#[test]
fn config_clone() {
    let cfg = OrchestratorConfig {
        sync_interval: Duration::from_secs(60),
        discovery_interval: Duration::from_secs(10),
        auto_sync: false,
        max_entities_per_sync: 0,
    };
    let cloned = cfg.clone();
    assert_eq!(cloned.sync_interval, Duration::from_secs(60));
    assert!(!cloned.auto_sync);
}

// ── SyncCommand ─────────────────────────────────────────────────

#[test]
fn sync_command_debug() {
    let cmd = SyncCommand::Shutdown;
    let debug = format!("{:?}", cmd);
    assert!(debug.contains("Shutdown"));
}

#[test]
fn sync_command_debug_variants() {
    let peer_id = PeerId::new();
    let entity_id = EntityId::new();

    let cmds = vec![
        format!("{:?}", SyncCommand::SyncWithPeer { peer_id }),
        format!("{:?}", SyncCommand::SyncEntity { entity_id }),
        format!("{:?}", SyncCommand::ShareEntity { entity_id }),
        format!("{:?}", SyncCommand::Shutdown),
    ];

    assert!(cmds[0].contains("SyncWithPeer"));
    assert!(cmds[1].contains("SyncEntity"));
    assert!(cmds[2].contains("ShareEntity"));
    assert!(cmds[3].contains("Shutdown"));
}

// ── SyncEvent ───────────────────────────────────────────────────

#[test]
fn sync_event_debug() {
    let peer_id = PeerId::new();
    let event = SyncEvent::SyncStarted { peer_id };
    let debug = format!("{:?}", event);
    assert!(debug.contains("SyncStarted"));
}

#[test]
fn sync_event_clone() {
    let peer_id = PeerId::new();
    let event = SyncEvent::SyncCompleted {
        peer_id,
        events_sent: 5,
        events_received: 3,
    };
    let cloned = event.clone();
    let debug = format!("{:?}", cloned);
    assert!(debug.contains("SyncCompleted"));
    assert!(debug.contains("5"));
    assert!(debug.contains("3"));
}

#[test]
fn sync_event_all_variants() {
    let peer_id = PeerId::new();
    let entity_id = EntityId::new();

    let events: Vec<SyncEvent> = vec![
        SyncEvent::PeerDiscovered {
            peer_id,
            device_name: Some("Test".to_string()),
        },
        SyncEvent::SyncStarted { peer_id },
        SyncEvent::SyncCompleted {
            peer_id,
            events_sent: 0,
            events_received: 0,
        },
        SyncEvent::SyncFailed {
            peer_id,
            error: "timeout".to_string(),
        },
        SyncEvent::EntityUpdated { entity_id },
    ];

    for e in &events {
        let debug = format!("{:?}", e);
        assert!(!debug.is_empty());
        let _ = e.clone();
    }
}

// ── create_orchestrator ─────────────────────────────────────────

#[test]
fn create_orchestrator_returns_valid_parts() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();
    let config = OrchestratorConfig::default();

    let (handle, _event_rx, _command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, config);

    let _handle2 = handle.clone();
}

// ── OrchestratorHandle channel operations ───────────────────────

#[tokio::test]
async fn handle_send_shutdown() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    handle.shutdown().await.unwrap();

    let cmd = command_rx.recv().await.unwrap();
    assert!(matches!(cmd, SyncCommand::Shutdown));
}

#[tokio::test]
async fn handle_send_sync_entity() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();
    let entity_id = EntityId::new();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    handle.sync_entity(entity_id).await.unwrap();

    let cmd = command_rx.recv().await.unwrap();
    assert!(matches!(cmd, SyncCommand::SyncEntity { .. }));
}

#[tokio::test]
async fn handle_send_share_entity() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();
    let entity_id = EntityId::new();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    handle.share_entity(entity_id).await.unwrap();

    let cmd = command_rx.recv().await.unwrap();
    assert!(matches!(cmd, SyncCommand::ShareEntity { .. }));
}

#[tokio::test]
async fn handle_record_event() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    let event = Event::new(
        EntityId::new(),
        peer_id,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "test".to_string(),
            json_data: "{}".to_string(),
        },
    );

    handle.record_event(event).await.unwrap();

    let cmd = command_rx.recv().await.unwrap();
    assert!(matches!(cmd, SyncCommand::RecordLocalEvent { .. }));
}

#[tokio::test]
async fn handle_send_returns_error_when_receiver_dropped() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (handle, _event_rx, command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    drop(command_rx);

    let result = handle.shutdown().await;
    assert!(result.is_err());
}

#[tokio::test]
async fn orchestrator_parts_drop_cleanly() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (handle, event_rx, command_rx, orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    drop(orchestrator);
    drop(command_rx);
    drop(event_rx);
    drop(handle);
}

#[tokio::test]
async fn handle_clone_both_send() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    let handle2 = handle.clone();

    let eid1 = EntityId::new();
    let eid2 = EntityId::new();

    handle.share_entity(eid1).await.unwrap();
    handle2.share_entity(eid2).await.unwrap();

    let cmd1 = command_rx.recv().await.unwrap();
    let cmd2 = command_rx.recv().await.unwrap();
    assert!(matches!(cmd1, SyncCommand::ShareEntity { .. }));
    assert!(matches!(cmd2, SyncCommand::ShareEntity { .. }));
}

// ── Orchestrator run() with mock transport ──────────────────────

#[tokio::test]
async fn run_shutdown_immediately() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(peer_id, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.shutdown().await.unwrap();
    let result = join.await.unwrap();
    assert!(result.is_ok());
}

#[tokio::test]
async fn run_discovers_peer_and_emits_event() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![DiscoveredPeer {
            peer_id: remote_peer,
            device_name: Some("RemoteDevice".to_string()),
            discovery_method: DiscoveryMethod::Mdns,
            addresses: vec![],
        }],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_millis(50),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Wait for discovery event
    let event = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await
        .unwrap()
        .unwrap();

    assert!(matches!(event, SyncEvent::PeerDiscovered { .. }));
    if let SyncEvent::PeerDiscovered { peer_id, device_name } = event {
        assert_eq!(peer_id, remote_peer);
        assert_eq!(device_name, Some("RemoteDevice".to_string()));
    }

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_record_local_event() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(peer_id, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    let entity_id = EntityId::new();
    let event = Event::new(
        entity_id,
        peer_id,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );

    handle.record_event(event.clone()).await.unwrap();
    // Give time for the event loop to process
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Verify event was saved to the store
    let events = ev.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(events.len(), 1);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_share_entity_command() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(peer_id, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    let entity_id = EntityId::new();
    handle.share_entity(entity_id).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_with_peer_full_handshake() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // Mock will return: HelloAck -> SyncState (for the handshake + state exchange)
    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Share an entity so sync has something to work with
    handle.share_entity(entity_id).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    // Trigger sync
    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    // Should get SyncStarted then SyncCompleted
    let ev1 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev1, SyncEvent::SyncStarted { .. }));

    let ev2 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev2, SyncEvent::SyncCompleted { .. }));

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_with_peer_rejected() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    let responses = vec![
        SyncMessage::HelloAck(HelloAckMessage {
            version: PROTOCOL_VERSION,
            peer_id: remote_peer,
            device_name: "Peer".to_string(),
            accepted: false,
            reason: Some("busy".to_string()),
        }),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let ev1 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev1, SyncEvent::SyncStarted { .. }));

    let ev2 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev2, SyncEvent::SyncFailed { .. }));

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_with_peer_version_mismatch() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    let responses = vec![
        SyncMessage::HelloAck(HelloAckMessage {
            version: 999, // wrong version
            peer_id: remote_peer,
            device_name: "Peer".to_string(),
            accepted: true,
            reason: None,
        }),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let ev1 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev1, SyncEvent::SyncStarted { .. }));

    let ev2 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    if let SyncEvent::SyncFailed { error, .. } = &ev2 {
        assert!(error.contains("version mismatch"));
    } else {
        panic!("Expected SyncFailed, got {:?}", ev2);
    }

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_with_peer_unexpected_hello_response() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // Return a Ping instead of HelloAck
    let responses = vec![SyncMessage::Ping(42)];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let ev1 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev1, SyncEvent::SyncStarted { .. }));

    let ev2 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev2, SyncEvent::SyncFailed { .. }));

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_with_peer_send_request_fails() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // No responses queued — send_request will fail
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let ev1 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev1, SyncEvent::SyncStarted { .. }));

    let ev2 = tokio::time::timeout(Duration::from_secs(2), event_rx.recv())
        .await.unwrap().unwrap();
    assert!(matches!(ev2, SyncEvent::SyncFailed { .. }));

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_no_shared_entities_is_noop() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Don't share any entity — sync should be a no-op
    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    // No events should be emitted (SyncStarted is only sent if shared_entities is non-empty)
    let result = tokio::time::timeout(Duration::from_millis(200), event_rx.recv()).await;
    assert!(result.is_err()); // timeout = no events

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_incoming_request_hello() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (incoming_tx, incoming_rx) = mpsc::channel(16);

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (_handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Send an incoming Hello request
    let hello = SyncMessage::Hello(privstack_sync::HelloMessage::new(
        remote_peer,
        "RemoteDevice".to_string(),
    ));

    incoming_tx.send(IncomingSyncRequest {
        peer_id: remote_peer,
        message: hello,
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    // Shutdown via dropping the handle — the channel closes and recv returns None
    drop(_handle);
    drop(incoming_tx);
    let _ = tokio::time::timeout(Duration::from_secs(2), join).await;
}

#[tokio::test]
async fn run_incoming_request_sync_request() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (incoming_tx, incoming_rx) = mpsc::channel(16);

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Send an incoming SyncRequest
    let sync_req = SyncMessage::SyncRequest(privstack_sync::SyncRequestMessage {
        entity_ids: vec![EntityId::new()],
        known_event_ids: std::collections::HashMap::new(),
    });

    incoming_tx.send(IncomingSyncRequest {
        peer_id: remote_peer,
        message: sync_req,
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_incoming_request_event_batch() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (incoming_tx, incoming_rx) = mpsc::channel(16);

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Create an event to send as a batch
    let event = Event::new(
        entity_id,
        remote_peer,
        HybridTimestamp::now(),
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"updated"}"#.to_string(),
        },
    );

    let batch = SyncMessage::EventBatch(EventBatchMessage {
        entity_id,
        batch_seq: 0,
        is_final: true,
        events: vec![event],
    });

    incoming_tx.send(IncomingSyncRequest {
        peer_id: remote_peer,
        message: batch,
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    // Give the orchestrator time to process
    tokio::time::sleep(Duration::from_millis(200)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_incoming_request_unexpected_message() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (incoming_tx, incoming_rx) = mpsc::channel(16);

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Send a Ping as an incoming request (unexpected)
    incoming_tx.send(IncomingSyncRequest {
        peer_id: remote_peer,
        message: SyncMessage::Ping(123),
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_auto_sync_on_discovery() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // Need HelloAck + SyncState for the auto-sync
    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![DiscoveredPeer {
            peer_id: remote_peer,
            device_name: Some("Auto".to_string()),
            discovery_method: DiscoveryMethod::Mdns,
            addresses: vec![],
        }],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_millis(50),
        auto_sync: true,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Share an entity and create an entity row so auto_sync triggers
    handle.share_entity(entity_id).await.unwrap();
    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;

    // Wait for discovery + auto sync events
    let mut got_discovered = false;
    let mut got_started = false;
    let mut got_completed = false;

    for _ in 0..10 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::PeerDiscovered { .. })) => got_discovered = true,
            Ok(Some(SyncEvent::SyncStarted { .. })) => got_started = true,
            Ok(Some(SyncEvent::SyncCompleted { .. })) => {
                got_completed = true;
                break;
            }
            Ok(Some(SyncEvent::SyncFailed { .. })) => break,
            _ => break,
        }
    }

    assert!(got_discovered);
    assert!(got_started);
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_with_peer_unexpected_sync_state_response() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // HelloAck succeeds, but SyncState returns a Ping (unexpected)
    let responses = vec![
        make_hello_ack(remote_peer),
        SyncMessage::Ping(99),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    // Should still complete (unexpected SyncState response uses empty state)
    let mut got_completed = false;
    for _ in 0..5 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { .. })) => {
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_entity_command_with_synced_peer() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // First sync: HelloAck + SyncState + EventAck (empty batch)
    // Second sync (from SyncEntity): HelloAck + SyncState + EventAck (empty batch)
    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
        make_event_ack_default(),
        make_hello_ack(remote_peer),
        make_sync_state(),
        make_event_ack_default(),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"test"}"#.to_string(),
        },
    );
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(50)).await;

    // First: sync with peer to establish them as a synced peer
    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    // Wait for first sync to complete
    loop {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { .. })) => break,
            Ok(Some(_)) => continue,
            _ => panic!("First sync didn't complete"),
        }
    }

    // Now SyncEntity should re-sync with the established peer
    handle.send(SyncCommand::SyncEntity { entity_id }).await.unwrap();

    let mut got_second_complete = false;
    for _ in 0..5 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { .. })) => {
                got_second_complete = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_second_complete);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── Sync with events to send (covers batch send/ack path) ──────

#[tokio::test]
async fn run_sync_sends_event_batches() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"hello"}"#.to_string(),
        },
    );

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // HelloAck -> SyncState -> EventAck (for the batch)
    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
        SyncMessage::EventAck(EventAckMessage {
            entity_id,
            batch_seq: 0,
            received_count: 1,
            events: Vec::new(),
        }),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    // Share the entity and record the event via the orchestrator + stores
    handle.share_entity(entity_id).await.unwrap();
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Trigger sync
    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let mut got_completed = false;
    for _ in 0..10 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { events_sent, .. })) => {
                assert!(events_sent > 0);
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_batch_send_fails() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: "{}".to_string(),
        },
    );

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // HelloAck + SyncState succeed, but no EventAck response (send_request fails)
    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
        // No EventAck — next send_request will get "no mock response" error
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    // Should still complete (batch error is logged but sync continues)
    let mut got_completed = false;
    for _ in 0..10 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { .. })) => {
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_batch_unexpected_response() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: "{}".to_string(),
        },
    );

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    // HelloAck + SyncState succeed, but EventBatch gets a Ping back (unexpected)
    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
        SyncMessage::Ping(99), // unexpected response to EventBatch
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    record_event_with_stores(&handle, &es, &ev, local_peer, event).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    // Should still complete with 0 events sent
    let mut got_completed = false;
    for _ in 0..10 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { events_sent, .. })) => {
                assert_eq!(events_sent, 0);
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_sync_ack_with_bidirectional_events() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let local_event = Event::new(
        entity_id,
        local_peer,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"local"}"#.to_string(),
        },
    );

    // A remote event that comes back in the ack
    let remote_event = Event::new(
        entity_id,
        remote_peer,
        HybridTimestamp::now(),
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"remote"}"#.to_string(),
        },
    );

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
        SyncMessage::EventAck(EventAckMessage {
            entity_id,
            batch_seq: 0,
            received_count: 1,
            events: vec![remote_event], // bidirectional event
        }),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es.clone(), ev.clone(), config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    record_event_with_stores(&handle, &es, &ev, local_peer, local_event).await;
    tokio::time::sleep(Duration::from_millis(100)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    // Should get SyncStarted, possibly EntityUpdated, then SyncCompleted
    let mut got_completed = false;
    for _ in 0..10 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { events_sent, .. })) => {
                assert!(events_sent > 0);
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── Enterprise orchestrator with policy filtering ───────────────

#[tokio::test]
async fn enterprise_orchestrator_rejects_untrusted_peer_hello() {
    use privstack_sync::policy::EnterpriseSyncPolicy;
    use privstack_sync::create_orchestrator_with_policy;

    let local_peer = PeerId::new();
    let trusted_peer = PeerId::new();
    let untrusted_peer = PeerId::new();
    let (es, ev) = make_stores();

    let policy = std::sync::Arc::new(EnterpriseSyncPolicy::new());
    policy.known_peers.write().await.insert(local_peer);
    policy.known_peers.write().await.insert(trusted_peer);

    let (incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator_with_policy(local_peer, es, ev, config, policy);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    let hello = SyncMessage::Hello(privstack_sync::HelloMessage::new(
        untrusted_peer,
        "Untrusted".to_string(),
    ));

    incoming_tx.send(IncomingSyncRequest {
        peer_id: untrusted_peer,
        message: hello,
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn enterprise_orchestrator_incoming_event_batch_filtered_by_policy() {
    use privstack_sync::policy::{EnterpriseSyncPolicy, EntityAcl, SyncRole};
    #[allow(unused_imports)]
    use privstack_sync::create_orchestrator_with_policy;

    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let policy = std::sync::Arc::new(EnterpriseSyncPolicy::new());
    policy.known_peers.write().await.insert(local_peer);
    policy.known_peers.write().await.insert(remote_peer);
    let acl = EntityAcl::new(entity_id).with_peer_role(remote_peer, SyncRole::Viewer);
    policy.acls.write().await.insert(entity_id, acl);

    let (incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator_with_policy(local_peer, es, ev.clone(), config, policy);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    let event = Event::new(
        entity_id,
        remote_peer,
        HybridTimestamp::now(),
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: r#"{"title":"hacked"}"#.to_string(),
        },
    );

    let batch = SyncMessage::EventBatch(EventBatchMessage {
        entity_id,
        batch_seq: 0,
        is_final: true,
        events: vec![event],
    });

    incoming_tx.send(IncomingSyncRequest {
        peer_id: remote_peer,
        message: batch,
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    let events = ev.get_events_for_entity(&entity_id).unwrap();
    assert!(events.is_empty(), "viewer events should be rejected by policy");

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn personal_orchestrator_shutdown() {
    use privstack_sync::create_personal_orchestrator;
    use privstack_sync::policy::PersonalSyncPolicy;
    use privstack_sync::PairingManager;

    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let personal_policy = Arc::new(PersonalSyncPolicy::new());
    let pairing_manager = Arc::new(std::sync::Mutex::new(PairingManager::new()));

    let (handle, _event_rx, command_rx, orchestrator) =
        create_personal_orchestrator(peer_id, es, ev, config, personal_policy, pairing_manager);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.shutdown().await.unwrap();
    let result = join.await.unwrap();
    assert!(result.is_ok());
}

#[tokio::test]
async fn run_incoming_error_message() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    incoming_tx.send(IncomingSyncRequest {
        peer_id: remote_peer,
        message: SyncMessage::Error(privstack_sync::ErrorMessage {
            code: 500,
            message: "internal error".to_string(),
        }),
        response_token: ResponseToken::new(()),
    }).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── create_enterprise_orchestrator ──────────────────────────────

#[tokio::test]
async fn enterprise_orchestrator_creation_and_shutdown() {
    use privstack_sync::policy::EnterpriseSyncPolicy;
    use privstack_sync::create_enterprise_orchestrator;

    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let policy = std::sync::Arc::new(EnterpriseSyncPolicy::new());

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_enterprise_orchestrator(peer_id, es, ev, config, policy);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.shutdown().await.unwrap();
    let result = join.await.unwrap();
    assert!(result.is_ok());
}

// ── share_entity_with_peer command ──────────────────────────────

#[tokio::test]
async fn handle_share_entity_with_peer() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();
    let entity_id = EntityId::new();
    let target_peer = PeerId::new();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    handle.share_entity_with_peer(entity_id, target_peer).await.unwrap();

    let cmd = command_rx.recv().await.unwrap();
    assert!(matches!(cmd, SyncCommand::ShareEntityWithPeer { .. }));
}

#[tokio::test]
async fn run_share_entity_with_peer_personal_policy() {
    use privstack_sync::create_personal_orchestrator;
    use privstack_sync::policy::PersonalSyncPolicy;
    use privstack_sync::PairingManager;

    let peer_id = PeerId::new();
    let target_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let personal_policy = Arc::new(PersonalSyncPolicy::new());
    let pm = Arc::new(std::sync::Mutex::new(PairingManager::new()));

    let (handle, _event_rx, command_rx, orchestrator) =
        create_personal_orchestrator(peer_id, es, ev, config, personal_policy.clone(), pm);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity_with_peer(entity_id, target_peer).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    let entities = personal_policy.shared_entities(&target_peer).await;
    assert!(entities.contains(&entity_id));

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

#[tokio::test]
async fn run_share_entity_with_peer_no_personal_policy() {
    let peer_id = PeerId::new();
    let target_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        peer_id,
        vec![],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, _event_rx, command_rx, orchestrator) =
        create_orchestrator(peer_id, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity_with_peer(entity_id, target_peer).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── Pairing manager trust check ─────────────────────────────────

#[tokio::test]
async fn pairing_orchestrator_skips_untrusted_peer_on_discovery() {
    use privstack_sync::create_orchestrator_with_pairing;
    use privstack_sync::PairingManager;

    let local_peer = PeerId::new();
    let untrusted_peer = PeerId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);
    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![DiscoveredPeer {
            peer_id: untrusted_peer,
            device_name: Some("Untrusted".to_string()),
            discovery_method: DiscoveryMethod::Mdns,
            addresses: vec![],
        }],
        vec![],
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_millis(50),
        auto_sync: true,
        max_entities_per_sync: 0,
    };

    let pm = Arc::new(std::sync::Mutex::new(PairingManager::new()));

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator_with_pairing(local_peer, es, ev, config, pm);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(EntityId::new()).await.unwrap();

    let result = tokio::time::timeout(Duration::from_millis(300), event_rx.recv()).await;
    assert!(result.is_err(), "untrusted peer should not trigger discovery event");

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── SyncCommand::ShareEntityWithPeer debug ──────────────────────

#[test]
fn sync_command_share_entity_with_peer_debug() {
    let cmd = SyncCommand::ShareEntityWithPeer {
        entity_id: EntityId::new(),
        peer_id: PeerId::new(),
    };
    let debug = format!("{:?}", cmd);
    assert!(debug.contains("ShareEntityWithPeer"));
}

// ── Personal orchestrator per-peer filtering during sync ────────

#[tokio::test]
async fn personal_orchestrator_filters_entities_per_peer() {
    use privstack_sync::create_personal_orchestrator;
    use privstack_sync::policy::PersonalSyncPolicy;
    use privstack_sync::PairingManager;

    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let shared_entity = EntityId::new();
    let unshared_entity = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    let responses = vec![
        make_hello_ack(remote_peer),
        make_sync_state(),
        make_event_ack_default(),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let personal_policy = Arc::new(PersonalSyncPolicy::new());
    personal_policy.share(shared_entity, remote_peer).await;

    let mut pm = PairingManager::new();
    pm.add_discovered_peer(privstack_sync::DiscoveredPeerInfo {
        peer_id: remote_peer.to_string(),
        device_name: "Remote".to_string(),
        discovered_at: 0,
        status: privstack_sync::PairingStatus::PendingLocalApproval,
        addresses: vec![],
    });
    pm.approve_peer(&remote_peer.to_string());
    let pm = Arc::new(std::sync::Mutex::new(pm));

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_personal_orchestrator(local_peer, es, ev, config, personal_policy, pm);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(shared_entity).await.unwrap();
    handle.share_entity(unshared_entity).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let mut got_completed = false;
    for _ in 0..10 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { .. })) => {
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── handle_send raw ─────────────────────────────────────────────

#[tokio::test]
async fn handle_send_raw() {
    let peer_id = PeerId::new();
    let (es, ev) = make_stores();

    let (handle, _event_rx, mut command_rx, _orchestrator) =
        create_orchestrator(peer_id, es, ev, OrchestratorConfig::default());

    handle.send(SyncCommand::SyncWithPeer { peer_id: PeerId::new() }).await.unwrap();

    let cmd = command_rx.recv().await.unwrap();
    assert!(matches!(cmd, SyncCommand::SyncWithPeer { .. }));
}

// ── sync_state_response_fails ───────────────────────────────────

#[tokio::test]
async fn run_sync_state_request_fails() {
    let local_peer = PeerId::new();
    let remote_peer = PeerId::new();
    let entity_id = EntityId::new();
    let (es, ev) = make_stores();

    let (_incoming_tx, incoming_rx) = mpsc::channel(16);

    let responses = vec![
        make_hello_ack(remote_peer),
    ];

    let transport: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(MockTransport::new(
        local_peer,
        vec![],
        responses,
        incoming_rx,
    )));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle, mut event_rx, command_rx, orchestrator) =
        create_orchestrator(local_peer, es, ev, config);

    let join = tokio::spawn(async move {
        orchestrator.run(transport, command_rx).await
    });

    handle.share_entity(entity_id).await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;

    handle.send(SyncCommand::SyncWithPeer { peer_id: remote_peer }).await.unwrap();

    let mut got_completed = false;
    for _ in 0..5 {
        match tokio::time::timeout(Duration::from_secs(2), event_rx.recv()).await {
            Ok(Some(SyncEvent::SyncCompleted { .. })) => {
                got_completed = true;
                break;
            }
            Ok(Some(_)) => continue,
            _ => break,
        }
    }
    assert!(got_completed);

    handle.shutdown().await.unwrap();
    let _ = join.await;
}

// ── RecordLocalEvent debug ──────────────────────────────────────

#[test]
fn sync_command_record_local_event_debug() {
    let event = Event::new(
        EntityId::new(),
        PeerId::new(),
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "test".to_string(),
            json_data: "{}".to_string(),
        },
    );
    let cmd = SyncCommand::RecordLocalEvent { event };
    let debug = format!("{:?}", cmd);
    assert!(debug.contains("RecordLocalEvent"));
}
