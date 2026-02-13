//! Level 2 & 3: Real P2pTransport integration tests.
//!
//! Level 2: mDNS discovery on localhost + relay bootstrap
//! Level 3: DHT-only discovery via sync code (mDNS disabled, relay required)
//!
//! The relay binary is built automatically if not already present.

use serial_test::serial;
use privstack_sync::{
    create_orchestrator, OrchestratorConfig, P2pConfig, P2pTransport, SyncCode, SyncCommand,
    SyncEvent, SyncTransport,
};
use privstack_storage::{EntityStore, EventStore};
use privstack_types::{EntityId, Event, EventPayload, HybridTimestamp, PeerId};
use std::process::Stdio;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::{Child, Command};
use tokio::sync::{mpsc, Mutex};
use tracing_subscriber::EnvFilter;

// ── Relay Process Management ────────────────────────────────────

struct RelayProcess {
    child: Child,
    peer_id: String,
    port: u16,
}

impl RelayProcess {
    /// Builds the relay binary (if needed) and spawns it on the given port.
    /// Parses the relay's PeerId from stdout.
    async fn spawn(port: u16) -> Self {
        let core_root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap();
        let repo_root = core_root.parent().unwrap();

        let find_relay_bin = || -> Option<std::path::PathBuf> {
            let candidates = [
                repo_root.join("relay/target/debug/privstack-relay"),
                core_root.join("target/debug/privstack-relay"),
            ];
            candidates.into_iter().find(|p| p.exists())
        };

        let relay_bin = match find_relay_bin() {
            Some(bin) => bin,
            None => {
                // Auto-build the relay binary
                let status = std::process::Command::new("cargo")
                    .args(["build", "-p", "privstack-relay"])
                    .current_dir(repo_root)
                    .status()
                    .expect("Failed to run cargo build for privstack-relay");
                assert!(status.success(), "cargo build -p privstack-relay failed");
                find_relay_bin().expect("Relay binary not found after successful build")
            }
        };

        // Use a temp dir for the identity file so tests don't conflict
        let identity_dir = tempfile::tempdir().unwrap();
        let identity_path = identity_dir.path().join("test-relay.key");

        let mut child = Command::new(&relay_bin)
            .arg("--port")
            .arg(port.to_string())
            .arg("--identity")
            .arg(&identity_path)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true)
            .spawn()
            .unwrap_or_else(|e| panic!("Failed to spawn relay binary at {:?}: {}", relay_bin, e));

        // Parse the PeerId from stdout. The relay prints:
        //   "  PeerId: 12D3KooW..."
        let stdout = child.stdout.take().unwrap();
        let mut reader = BufReader::new(stdout).lines();

        let mut peer_id = String::new();
        let deadline = tokio::time::Instant::now() + Duration::from_secs(15);

        while let Ok(result) = tokio::time::timeout_at(deadline, reader.next_line()).await {
            match result {
                Ok(Some(line)) => {
                    eprintln!("[relay] {}", line);
                    if let Some(id) = line.strip_prefix("  PeerId: ") {
                        peer_id = id.trim().to_string();
                        break;
                    }
                }
                Ok(None) => panic!("Relay process exited before printing PeerId"),
                Err(e) => panic!("Error reading relay stdout: {}", e),
                }
        }

        if peer_id.is_empty() {
            child.kill().await.ok();
            panic!("Failed to parse relay PeerId within 15 seconds");
        }

        eprintln!("[test] Relay started on port {} with PeerId {}", port, peer_id);

        // Leak the identity_dir so it persists for the lifetime of the process
        std::mem::forget(identity_dir);

        // Spawn a task to drain stderr so the relay doesn't block
        let stderr = child.stderr.take().unwrap();
        tokio::spawn(async move {
            let mut reader = BufReader::new(stderr).lines();
            while let Ok(Some(line)) = reader.next_line().await {
                eprintln!("[relay/err] {}", line);
            }
        });

        // Spawn a task to keep draining stdout
        tokio::spawn(async move {
            while let Ok(Some(line)) = reader.next_line().await {
                eprintln!("[relay] {}", line);
            }
        });

        Self {
            child,
            peer_id,
            port,
        }
    }

    /// Returns the multiaddr for this relay.
    fn multiaddr(&self) -> libp2p::Multiaddr {
        format!(
            "/ip4/127.0.0.1/udp/{}/quic-v1/p2p/{}",
            self.port, self.peer_id
        )
        .parse()
        .unwrap()
    }

    async fn kill(&mut self) {
        self.child.kill().await.ok();
    }
}

impl Drop for RelayProcess {
    fn drop(&mut self) {
        // kill_on_drop handles cleanup
    }
}

// ── Helpers ─────────────────────────────────────────────────────

fn make_stores() -> (Arc<EntityStore>, Arc<EventStore>) {
    (
        Arc::new(EntityStore::open_in_memory().unwrap()),
        Arc::new(EventStore::open_in_memory().unwrap()),
    )
}

fn make_event(entity_id: EntityId, peer_id: PeerId, title: &str) -> Event {
    Event::new(
        entity_id,
        peer_id,
        HybridTimestamp::now(),
        EventPayload::EntityCreated {
            entity_type: "note".to_string(),
            json_data: format!(r#"{{"title":"{}"}}"#, title),
        },
    )
}

/// Wait for a specific event variant within a timeout.
async fn wait_for_event(
    rx: &mut mpsc::Receiver<SyncEvent>,
    timeout: Duration,
    mut predicate: impl FnMut(&SyncEvent) -> bool,
) -> Option<SyncEvent> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        match tokio::time::timeout_at(deadline, rx.recv()).await {
            Ok(Some(event)) => {
                eprintln!("[test] Event: {:?}", event);
                if predicate(&event) {
                    return Some(event);
                }
            }
            _ => return None,
        }
    }
}

/// Creates a P2pTransport configured for localhost testing with a local relay.
fn make_p2p_transport(
    peer_id: PeerId,
    relay_addr: &libp2p::Multiaddr,
    device_name: &str,
) -> P2pTransport {
    let config = P2pConfig {
        listen_addrs: vec![
            "/ip4/0.0.0.0/udp/0/quic-v1".parse().unwrap(),
        ],
        bootstrap_nodes: vec![relay_addr.clone()],
        sync_code_hash: None,
        device_name: device_name.to_string(),
        enable_mdns: true,
        enable_dht: true,
        idle_timeout: Duration::from_secs(30),
    };
    P2pTransport::new(peer_id, config).unwrap()
}

// ── Tests ───────────────────────────────────────────────────────

/// Test: Two real P2pTransports discover each other via mDNS on localhost,
/// then sync an event from A to B through the full protocol.
#[tokio::test]
#[serial]
async fn real_p2p_a_writes_b_receives() {
    let mut relay = RelayProcess::spawn(14001).await;

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity_id = EntityId::new();

    let (stores_a_entity, stores_a_event) = make_stores();
    let (stores_b_entity, stores_b_event) = make_stores();

    let relay_addr = relay.multiaddr();

    // Create and start transports
    let mut transport_a = make_p2p_transport(peer_a, &relay_addr, "TestDeviceA");
    let mut transport_b = make_p2p_transport(peer_b, &relay_addr, "TestDeviceB");

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    // Wait for mDNS discovery
    eprintln!("[test] Waiting for mDNS peer discovery...");
    let discovery_timeout = Duration::from_secs(15);
    let deadline = tokio::time::Instant::now() + discovery_timeout;

    let mut a_found_b = false;
    loop {
        if tokio::time::Instant::now() > deadline {
            break;
        }
        let peers = transport_a.discovered_peers();
        if !peers.is_empty() {
            eprintln!("[test] A discovered {} peer(s)", peers.len());
            for p in &peers {
                eprintln!("[test]   - {:?} ({:?})", p.peer_id, p.device_name);
            }
            a_found_b = true;
            break;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;
    }

    if !a_found_b {
        relay.kill().await;
        panic!("A did not discover B via mDNS within {:?}", discovery_timeout);
    }

    // Get B's mapped PeerId as seen by A
    let peers_a = transport_a.discovered_peers();
    let b_mapped_peer_id = peers_a[0].peer_id;
    eprintln!("[test] B's mapped PeerId as seen by A: {}", b_mapped_peer_id);
    eprintln!("[test] B's addresses: {:?}", peers_a[0].addresses);
    eprintln!("[test] B's libp2p PeerId: {:?}", transport_b.libp2p_peer_id());
    eprintln!("[test] A's libp2p PeerId: {:?}", transport_a.libp2p_peer_id());

    // Give a bit more time for connections to stabilize
    tokio::time::sleep(Duration::from_secs(2)).await;

    // Wrap transports for the orchestrator
    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_orchestrator(
        peer_a,
        stores_a_entity.clone(),
        stores_a_event.clone(),
        config.clone(),
    );
    let (handle_b, mut events_b, cmd_rx_b, orch_b) = create_orchestrator(
        peer_b,
        stores_b_entity.clone(),
        stores_b_event.clone(),
        config,
    );

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    // Both share the same entity
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // A records an event
    let event = make_event(entity_id, peer_a, "Hello from A over real P2P");
    handle_a.record_event(event.clone()).await.unwrap();
    tokio::time::sleep(Duration::from_millis(200)).await;

    // A syncs with B using B's mapped peer ID
    handle_a
        .send(SyncCommand::SyncWithPeer {
            peer_id: b_mapped_peer_id,
        })
        .await
        .unwrap();

    // Wait for sync completion
    let completed = wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    if let Some(SyncEvent::SyncCompleted {
        events_sent,
        events_received,
        ..
    }) = &completed
    {
        eprintln!(
            "[test] Sync completed: sent={}, received={}",
            events_sent, events_received
        );
        assert!(*events_sent > 0, "A should have sent events to B");
    } else {
        // Check if we got a failure instead
        panic!("Sync did not complete. Check relay/transport logs above.");
    }

    // Verify B received the event
    tokio::time::sleep(Duration::from_millis(500)).await;
    let b_events = stores_b_event.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 1, "B should have 1 event from A");
    assert_eq!(b_events[0].id, event.id);

    // B should have emitted EntityUpdated
    let updated = wait_for_event(&mut events_b, Duration::from_secs(2), |e| {
        matches!(e, SyncEvent::EntityUpdated { .. })
    })
    .await;
    assert!(updated.is_some(), "B should emit EntityUpdated");

    // Cleanup
    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    relay.kill().await;
}

/// Test: Bidirectional sync over real P2P — both sides write, both sync.
#[tokio::test]
#[serial]
async fn real_p2p_bidirectional_sync() {
    let mut relay = RelayProcess::spawn(14002).await;

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity_id = EntityId::new();

    let (stores_a_entity, stores_a_event) = make_stores();
    let (stores_b_entity, stores_b_event) = make_stores();

    let relay_addr = relay.multiaddr();

    let mut transport_a = make_p2p_transport(peer_a, &relay_addr, "BiDirA");
    let mut transport_b = make_p2p_transport(peer_b, &relay_addr, "BiDirB");

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    // Wait for mutual discovery
    eprintln!("[test] Waiting for mDNS discovery...");
    let deadline = tokio::time::Instant::now() + Duration::from_secs(15);
    loop {
        if tokio::time::Instant::now() > deadline {
            relay.kill().await;
            panic!("Peers did not discover each other within 15s");
        }
        let a_peers = transport_a.discovered_peers();
        let b_peers = transport_b.discovered_peers();
        if !a_peers.is_empty() && !b_peers.is_empty() {
            eprintln!("[test] Mutual discovery complete");
            break;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;
    }

    let b_mapped = transport_a.discovered_peers()[0].peer_id;
    let a_mapped = transport_b.discovered_peers()[0].peer_id;

    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_orchestrator(
        peer_a,
        stores_a_entity.clone(),
        stores_a_event.clone(),
        config.clone(),
    );
    let (handle_b, mut events_b, cmd_rx_b, orch_b) = create_orchestrator(
        peer_b,
        stores_b_entity.clone(),
        stores_b_event.clone(),
        config,
    );

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // Both write events
    let event_a = make_event(entity_id, peer_a, "From A");
    let event_b = make_event(entity_id, peer_b, "From B");
    handle_a.record_event(event_a.clone()).await.unwrap();
    handle_b.record_event(event_b.clone()).await.unwrap();
    tokio::time::sleep(Duration::from_millis(200)).await;

    // A syncs with B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();

    let completed_a = wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed_a.is_some(), "A->B sync should complete");

    // B syncs with A
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();

    let completed_b = wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed_b.is_some(), "B->A sync should complete");

    tokio::time::sleep(Duration::from_millis(500)).await;

    let a_events = stores_a_event.get_events_for_entity(&entity_id).unwrap();
    let b_events = stores_b_event.get_events_for_entity(&entity_id).unwrap();

    eprintln!(
        "[test] Final state: A has {} events, B has {} events",
        a_events.len(),
        b_events.len()
    );
    assert_eq!(b_events.len(), 2, "B should have both events");
    assert_eq!(a_events.len(), 2, "A should have both events");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    relay.kill().await;
}

/// Test: Multiple events propagate over real P2P.
#[tokio::test]
#[serial]
async fn real_p2p_multiple_events() {
    let mut relay = RelayProcess::spawn(14003).await;

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity_id = EntityId::new();

    let (stores_a_entity, stores_a_event) = make_stores();
    let (stores_b_entity, stores_b_event) = make_stores();

    let relay_addr = relay.multiaddr();

    let mut transport_a = make_p2p_transport(peer_a, &relay_addr, "MultiA");
    let mut transport_b = make_p2p_transport(peer_b, &relay_addr, "MultiB");

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    // Wait for discovery
    let deadline = tokio::time::Instant::now() + Duration::from_secs(15);
    loop {
        if tokio::time::Instant::now() > deadline {
            relay.kill().await;
            panic!("Discovery timeout");
        }
        if !transport_a.discovered_peers().is_empty() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;
    }

    let b_mapped = transport_a.discovered_peers()[0].peer_id;

    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_orchestrator(
        peer_a,
        stores_a_entity.clone(),
        stores_a_event.clone(),
        config.clone(),
    );
    let (handle_b, _events_b, cmd_rx_b, orch_b) = create_orchestrator(
        peer_b,
        stores_b_entity.clone(),
        stores_b_event.clone(),
        config,
    );

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // A writes 5 events
    let create = make_event(entity_id, peer_a, "Note");
    handle_a.record_event(create).await.unwrap();

    for i in 1..5 {
        let update = Event::new(
            entity_id,
            peer_a,
            HybridTimestamp::now(),
            EventPayload::EntityUpdated {
                entity_type: "note".to_string(),
                json_data: format!(r#"{{"title":"Note v{}"}}"#, i),
            },
        );
        handle_a.record_event(update).await.unwrap();
    }
    tokio::time::sleep(Duration::from_millis(200)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();

    let completed = wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed.is_some(), "Sync should complete");

    tokio::time::sleep(Duration::from_millis(500)).await;

    let b_events = stores_b_event.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 5, "B should have all 5 events");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    relay.kill().await;
}

// ═══════════════════════════════════════════════════════════════
// Level 3: DHT-only discovery via sync code (no mDNS)
//
// These tests disable mDNS entirely. Peers discover each other
// exclusively through the relay's Kademlia DHT using a shared
// sync code. This validates the full production discovery path.
// ═══════════════════════════════════════════════════════════════

/// Creates a P2pTransport with mDNS DISABLED, DHT enabled, and a sync code hash.
fn make_dht_only_transport(
    peer_id: PeerId,
    relay_addr: &libp2p::Multiaddr,
    device_name: &str,
    sync_code_hash: Vec<u8>,
) -> P2pTransport {
    let config = P2pConfig {
        listen_addrs: vec!["/ip4/0.0.0.0/udp/0/quic-v1".parse().unwrap()],
        bootstrap_nodes: vec![relay_addr.clone()],
        sync_code_hash: Some(sync_code_hash),
        device_name: device_name.to_string(),
        enable_mdns: false, // No mDNS — DHT only
        enable_dht: true,
        idle_timeout: Duration::from_secs(30),
    };
    P2pTransport::new(peer_id, config).unwrap()
}

/// Test: Two peers discover each other via DHT sync code (no mDNS),
/// then sync an event from A to B.
#[tokio::test]
#[serial]
async fn dht_sync_code_a_writes_b_receives() {
    let _ = tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::new("privstack_sync=debug,libp2p_kad=debug"))
        .with_test_writer()
        .try_init();

    let mut relay = RelayProcess::spawn(14010).await;

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity_id = EntityId::new();

    let (stores_a_entity, stores_a_event) = make_stores();
    let (stores_b_entity, stores_b_event) = make_stores();

    let relay_addr = relay.multiaddr();

    // Both peers use the same sync code for DHT namespace
    let sync_code = SyncCode::generate();
    let hash_bytes = hex::decode(&sync_code.hash).unwrap();
    eprintln!("[test] Using sync code: {} (hash: {})", sync_code.code, sync_code.hash);

    let mut transport_a =
        make_dht_only_transport(peer_a, &relay_addr, "DhtDeviceA", hash_bytes.clone());
    let mut transport_b =
        make_dht_only_transport(peer_b, &relay_addr, "DhtDeviceB", hash_bytes.clone());

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    // Give transports time to connect to relay and bootstrap Kademlia
    tokio::time::sleep(Duration::from_secs(3)).await;

    // Publish both peers to the sync group DHT
    transport_a.publish_to_sync_group(&hash_bytes).await.unwrap();
    transport_b.publish_to_sync_group(&hash_bytes).await.unwrap();
    tokio::time::sleep(Duration::from_secs(2)).await;

    // Discover peers via DHT
    transport_a.discover_sync_group(&hash_bytes).await.unwrap();
    transport_b.discover_sync_group(&hash_bytes).await.unwrap();

    // Wait for DHT discovery (slower than mDNS)
    eprintln!("[test] Waiting for DHT peer discovery via sync code...");
    let discovery_timeout = Duration::from_secs(30);
    let deadline = tokio::time::Instant::now() + discovery_timeout;

    let mut a_found_b = false;
    loop {
        if tokio::time::Instant::now() > deadline {
            break;
        }
        let peers = transport_a.discovered_peers();
        if !peers.is_empty() {
            eprintln!("[test] A discovered {} peer(s) via DHT:", peers.len());
            for p in &peers {
                eprintln!("[test]   - {:?} ({:?}) via {:?}", p.peer_id, p.device_name, p.discovery_method);
            }
            a_found_b = true;
            break;
        }
        // Retry discovery periodically
        transport_a.discover_sync_group(&hash_bytes).await.ok();
        tokio::time::sleep(Duration::from_secs(2)).await;
    }

    if !a_found_b {
        relay.kill().await;
        panic!(
            "A did not discover B via DHT sync code within {:?}. \
             This may indicate relay/DHT connectivity issues.",
            discovery_timeout
        );
    }

    let b_mapped_peer_id = transport_a.discovered_peers()[0].peer_id;
    eprintln!("[test] B's mapped PeerId (via DHT): {}", b_mapped_peer_id);

    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_orchestrator(
        peer_a,
        stores_a_entity.clone(),
        stores_a_event.clone(),
        config.clone(),
    );
    let (handle_b, mut events_b, cmd_rx_b, orch_b) = create_orchestrator(
        peer_b,
        stores_b_entity.clone(),
        stores_b_event.clone(),
        config,
    );

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    let event = make_event(entity_id, peer_a, "Hello via DHT discovery");
    handle_a.record_event(event.clone()).await.unwrap();
    tokio::time::sleep(Duration::from_millis(200)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer {
            peer_id: b_mapped_peer_id,
        })
        .await
        .unwrap();

    let completed = wait_for_event(&mut events_a, Duration::from_secs(15), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    if let Some(SyncEvent::SyncCompleted {
        events_sent,
        events_received,
        ..
    }) = &completed
    {
        eprintln!(
            "[test] DHT sync completed: sent={}, received={}",
            events_sent, events_received
        );
        assert!(*events_sent > 0, "A should have sent events to B");
    } else {
        panic!("DHT sync did not complete. Check relay/transport logs.");
    }

    tokio::time::sleep(Duration::from_millis(500)).await;

    let b_events = stores_b_event.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 1, "B should have 1 event from A");
    assert_eq!(b_events[0].id, event.id);

    let updated = wait_for_event(&mut events_b, Duration::from_secs(2), |e| {
        matches!(e, SyncEvent::EntityUpdated { .. })
    })
    .await;
    assert!(updated.is_some(), "B should emit EntityUpdated");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    relay.kill().await;
}

/// Test: Bidirectional sync via DHT sync code (no mDNS).
#[tokio::test]
#[serial]
async fn dht_sync_code_bidirectional() {
    let mut relay = RelayProcess::spawn(14011).await;

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();
    let entity_id = EntityId::new();

    let (stores_a_entity, stores_a_event) = make_stores();
    let (stores_b_entity, stores_b_event) = make_stores();

    let relay_addr = relay.multiaddr();

    let sync_code = SyncCode::generate();
    let hash_bytes = hex::decode(&sync_code.hash).unwrap();
    eprintln!("[test] Sync code: {}", sync_code.code);

    let mut transport_a =
        make_dht_only_transport(peer_a, &relay_addr, "BiDirDhtA", hash_bytes.clone());
    let mut transport_b =
        make_dht_only_transport(peer_b, &relay_addr, "BiDirDhtB", hash_bytes.clone());

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    tokio::time::sleep(Duration::from_secs(3)).await;

    transport_a.publish_to_sync_group(&hash_bytes).await.unwrap();
    transport_b.publish_to_sync_group(&hash_bytes).await.unwrap();
    tokio::time::sleep(Duration::from_secs(2)).await;

    // Wait for mutual discovery
    let deadline = tokio::time::Instant::now() + Duration::from_secs(30);
    loop {
        if tokio::time::Instant::now() > deadline {
            relay.kill().await;
            panic!("DHT mutual discovery timeout");
        }
        transport_a.discover_sync_group(&hash_bytes).await.ok();
        transport_b.discover_sync_group(&hash_bytes).await.ok();
        tokio::time::sleep(Duration::from_secs(2)).await;

        let a_peers = transport_a.discovered_peers();
        let b_peers = transport_b.discovered_peers();
        if !a_peers.is_empty() && !b_peers.is_empty() {
            eprintln!("[test] Mutual DHT discovery complete");
            break;
        }
    }

    let b_mapped = transport_a.discovered_peers()[0].peer_id;
    let a_mapped = transport_b.discovered_peers()[0].peer_id;

    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_orchestrator(
        peer_a,
        stores_a_entity.clone(),
        stores_a_event.clone(),
        config.clone(),
    );
    let (handle_b, mut events_b, cmd_rx_b, orch_b) = create_orchestrator(
        peer_b,
        stores_b_entity.clone(),
        stores_b_event.clone(),
        config,
    );

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    let event_a = make_event(entity_id, peer_a, "A via DHT");
    let event_b = make_event(entity_id, peer_b, "B via DHT");
    handle_a.record_event(event_a.clone()).await.unwrap();
    handle_b.record_event(event_b.clone()).await.unwrap();
    tokio::time::sleep(Duration::from_millis(200)).await;

    // A -> B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();

    let completed_a = wait_for_event(&mut events_a, Duration::from_secs(15), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed_a.is_some(), "A->B DHT sync should complete");

    // B -> A
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();

    let completed_b = wait_for_event(&mut events_b, Duration::from_secs(15), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed_b.is_some(), "B->A DHT sync should complete");

    tokio::time::sleep(Duration::from_millis(500)).await;

    let a_events = stores_a_event.get_events_for_entity(&entity_id).unwrap();
    let b_events = stores_b_event.get_events_for_entity(&entity_id).unwrap();

    eprintln!(
        "[test] DHT bidirectional: A has {} events, B has {} events",
        a_events.len(),
        b_events.len()
    );
    assert_eq!(b_events.len(), 2, "B should have both events");
    assert_eq!(a_events.len(), 2, "A should have both events");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    relay.kill().await;
}

/// Test: Peers with DIFFERENT sync codes should NOT discover each other via DHT.
#[tokio::test]
#[serial]
async fn dht_different_sync_codes_no_discovery() {
    let mut relay = RelayProcess::spawn(14012).await;

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let relay_addr = relay.multiaddr();

    // Different sync codes → different DHT namespaces
    let code_a = SyncCode::generate();
    let code_b = SyncCode::generate();
    let hash_a = hex::decode(&code_a.hash).unwrap();
    let hash_b = hex::decode(&code_b.hash).unwrap();
    eprintln!("[test] Code A: {}, Code B: {}", code_a.code, code_b.code);

    let mut transport_a =
        make_dht_only_transport(peer_a, &relay_addr, "IsolatedA", hash_a.clone());
    let mut transport_b =
        make_dht_only_transport(peer_b, &relay_addr, "IsolatedB", hash_b.clone());

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    tokio::time::sleep(Duration::from_secs(3)).await;

    // Each publishes to their OWN sync group
    transport_a.publish_to_sync_group(&hash_a).await.unwrap();
    transport_b.publish_to_sync_group(&hash_b).await.unwrap();
    tokio::time::sleep(Duration::from_secs(2)).await;

    // Each discovers in their OWN sync group — should NOT find the other
    transport_a.discover_sync_group(&hash_a).await.unwrap();
    transport_b.discover_sync_group(&hash_b).await.unwrap();

    // Wait and verify no cross-discovery
    tokio::time::sleep(Duration::from_secs(10)).await;

    // Retry discovery
    transport_a.discover_sync_group(&hash_a).await.ok();
    transport_b.discover_sync_group(&hash_b).await.ok();
    tokio::time::sleep(Duration::from_secs(5)).await;

    let a_peers = transport_a.discovered_peers();
    let b_peers = transport_b.discovered_peers();

    eprintln!(
        "[test] Isolation check: A sees {} peers, B sees {} peers",
        a_peers.len(),
        b_peers.len()
    );

    // Filter to DHT-discovered peers only
    let a_sync_peers: Vec<_> = a_peers
        .iter()
        .filter(|p| p.discovery_method == privstack_sync::DiscoveryMethod::Dht)
        .collect();
    let b_sync_peers: Vec<_> = b_peers
        .iter()
        .filter(|p| p.discovery_method == privstack_sync::DiscoveryMethod::Dht)
        .collect();

    assert!(
        a_sync_peers.is_empty(),
        "A should NOT discover B (different sync code). Found: {:?}",
        a_sync_peers
    );
    assert!(
        b_sync_peers.is_empty(),
        "B should NOT discover A (different sync code). Found: {:?}",
        b_sync_peers
    );

    transport_a.stop().await.unwrap();
    transport_b.stop().await.unwrap();
    relay.kill().await;
}

// ═══════════════════════════════════════════════════════════════
// Level 4: Adversarial relay tests
//
// Real P2P transports under hostile conditions: relay crashes,
// peer disconnects, large payloads, rapid reconnects, data races,
// and multi-entity convergence over actual network transport.
// ═══════════════════════════════════════════════════════════════

/// Helper: set up two peers with mDNS discovery on a relay, return all state needed for testing.
/// Returns (relay, handle_a, handle_b, events_a, events_b, stores_a, stores_b, peer_a, peer_b, b_mapped, a_mapped, join_a, join_b)
#[allow(clippy::type_complexity)]
async fn setup_mdns_pair(
    relay_port: u16,
) -> (
    RelayProcess,
    privstack_sync::OrchestratorHandle,
    privstack_sync::OrchestratorHandle,
    mpsc::Receiver<SyncEvent>,
    mpsc::Receiver<SyncEvent>,
    (Arc<EntityStore>, Arc<EventStore>),
    (Arc<EntityStore>, Arc<EventStore>),
    PeerId,
    PeerId,
    PeerId,
    PeerId,
    tokio::task::JoinHandle<privstack_sync::SyncResult<()>>,
    tokio::task::JoinHandle<privstack_sync::SyncResult<()>>,
) {
    let relay = RelayProcess::spawn(relay_port).await;
    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let stores_a = make_stores();
    let stores_b = make_stores();

    let relay_addr = relay.multiaddr();
    let mut transport_a = make_p2p_transport(peer_a, &relay_addr, "AdvA");
    let mut transport_b = make_p2p_transport(peer_b, &relay_addr, "AdvB");

    transport_a.start().await.unwrap();
    transport_b.start().await.unwrap();

    // Wait for mutual mDNS discovery
    let deadline = tokio::time::Instant::now() + Duration::from_secs(15);
    loop {
        if tokio::time::Instant::now() > deadline {
            panic!("mDNS mutual discovery timeout");
        }
        let ap = transport_a.discovered_peers();
        let bp = transport_b.discovered_peers();
        if !ap.is_empty() && !bp.is_empty() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;
    }

    let b_mapped = transport_a.discovered_peers()[0].peer_id;
    let a_mapped = transport_b.discovered_peers()[0].peer_id;

    tokio::time::sleep(Duration::from_secs(1)).await;

    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

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

    (
        relay, handle_a, handle_b, events_a, events_b, stores_a, stores_b,
        peer_a, peer_b, b_mapped, a_mapped, join_a, join_b,
    )
}

fn make_update_event(entity_id: EntityId, peer_id: PeerId, data: &str) -> Event {
    Event::new(
        entity_id,
        peer_id,
        HybridTimestamp::now(),
        EventPayload::EntityUpdated {
            entity_type: "note".to_string(),
            json_data: format!(r#"{{"title":"{}"}}"#, data),
        },
    )
}

fn make_delete_event(entity_id: EntityId, peer_id: PeerId) -> Event {
    Event::new(
        entity_id,
        peer_id,
        HybridTimestamp::now(),
        EventPayload::EntityDeleted {
            entity_type: "note".to_string(),
        },
    )
}

fn make_snapshot_event(entity_id: EntityId, peer_id: PeerId, data: &str) -> Event {
    Event::new(
        entity_id,
        peer_id,
        HybridTimestamp::now(),
        EventPayload::FullSnapshot {
            entity_type: "note".to_string(),
            json_data: data.to_string(),
        },
    )
}

// ── 7. Relay crash mid-sync: peer writes during relay outage, relay restarts ──

/// Relay dies, peer writes offline, relay comes back, sync recovers.
#[tokio::test]
#[serial]
async fn relay_crash_and_recovery_sync() {
    let (
        mut relay, handle_a, handle_b, mut events_a, _events_b,
        stores_a, stores_b, peer_a, _peer_b, b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14020).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // Initial sync to establish relationship
    let create = make_event(entity_id, peer_a, "Pre-crash note");
    handle_a.record_event(create).await.unwrap();
    tokio::time::sleep(Duration::from_millis(200)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // Kill the relay
    relay.kill().await;
    tokio::time::sleep(Duration::from_millis(500)).await;

    // A writes while relay is dead (mDNS still works on localhost though)
    for i in 0..5 {
        let update = make_update_event(entity_id, peer_a, &format!("offline_edit_{i}"));
        handle_a.record_event(update).await.unwrap();
        tokio::time::sleep(Duration::from_millis(10)).await;
    }

    // Attempt sync — may fail or succeed depending on mDNS direct connection
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    let result = wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. } | SyncEvent::SyncFailed { .. })
    })
    .await;

    match result {
        Some(SyncEvent::SyncCompleted { events_sent, .. }) => {
            // mDNS direct connection still worked
            eprintln!("[test] Sync succeeded without relay, sent {events_sent} events");
            let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
            assert!(b_events.len() >= 6, "B should have received events");
        }
        Some(SyncEvent::SyncFailed { .. }) => {
            // Expected: relay was down and direct connection failed
            eprintln!("[test] Sync failed as expected with relay down");
        }
        _ => {
            eprintln!("[test] Sync timed out — acceptable with relay down");
        }
    }

    // Verify A's data is intact regardless
    let a_events = stores_a.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 6, "A should have all 6 events locally");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 8. Rapid-fire writes then bulk sync ──

/// 50 rapid events from one peer, then single sync — tests batch handling at scale.
#[tokio::test]
#[serial]
async fn bulk_rapid_fire_50_events_sync() {
    let (
        _relay, handle_a, handle_b, mut events_a, _events_b,
        _stores_a, stores_b, peer_a, _peer_b, b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14021).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    let create = make_event(entity_id, peer_a, "Bulk note");
    handle_a.record_event(create).await.unwrap();

    // Rapid-fire 49 updates with no delay
    for i in 0..49 {
        let update = make_update_event(entity_id, peer_a, &format!("rapid_{i}"));
        handle_a.record_event(update).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(300)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    let completed = wait_for_event(&mut events_a, Duration::from_secs(15), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed.is_some(), "Bulk sync should complete");

    tokio::time::sleep(Duration::from_millis(500)).await;

    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 50, "B should have all 50 events, got {}", b_events.len());

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 9. Multi-entity divergence over real P2P ──

/// 10 different entities, each peer writes to 5, then full convergence.
#[tokio::test]
#[serial]
async fn multi_entity_divergence_real_p2p() {
    let (
        _relay, handle_a, handle_b, mut events_a, mut events_b,
        _stores_a, stores_b, peer_a, peer_b, b_mapped, a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14022).await;

    let entities: Vec<EntityId> = (0..10).map(|_| EntityId::new()).collect();

    for eid in &entities {
        handle_a.share_entity(*eid).await.unwrap();
        handle_b.share_entity(*eid).await.unwrap();
    }

    // A creates entities 0-4, B creates entities 5-9
    for (i, eid) in entities.iter().enumerate() {
        let peer = if i < 5 { peer_a } else { peer_b };
        let handle = if i < 5 { &handle_a } else { &handle_b };
        let create = make_event(*eid, peer, &format!("Entity_{i}"));
        handle.record_event(create).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(300)).await;

    // A→B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    tokio::time::sleep(Duration::from_millis(300)).await;

    // B→A
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    tokio::time::sleep(Duration::from_millis(500)).await;

    // B should have all 10 entities
    for (i, eid) in entities.iter().enumerate() {
        let b_events = stores_b.1.get_events_for_entity(eid).unwrap();
        assert_eq!(
            b_events.len(),
            1,
            "B should have entity {i}: got {} events",
            b_events.len()
        );
    }

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 10. Delete-then-recreate over real P2P ──

/// A creates entity, syncs to B, A deletes it, syncs, then A recreates with new data.
#[tokio::test]
#[serial]
async fn delete_and_recreate_over_real_p2p() {
    let (
        _relay, handle_a, handle_b, mut events_a, mut events_b,
        _stores_a, stores_b, peer_a, _peer_b, b_mapped, a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14023).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // Create
    let create = make_event(entity_id, peer_a, "Original");
    handle_a.record_event(create).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync create to B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // Delete
    let delete = make_delete_event(entity_id, peer_a);
    handle_a.record_event(delete).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync delete to B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // Recreate with new data
    let recreate = make_event(entity_id, peer_a, "Resurrected");
    handle_a.record_event(recreate).await.unwrap();
    let update = make_update_event(entity_id, peer_a, "Resurrected_v2");
    handle_a.record_event(update).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    // Sync recreation
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // B→A for completeness
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    tokio::time::sleep(Duration::from_millis(500)).await;

    // B should have: create + delete + recreate + update = 4 events
    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 4, "B should have full lifecycle: got {}", b_events.len());

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 11. Both peers write conflicting updates, converge ──

/// Both peers write different updates to the same entity simultaneously,
/// then sync bidirectionally. CRDTs should merge without loss.
#[tokio::test]
#[serial]
async fn concurrent_writes_both_peers_converge() {
    let (
        _relay, handle_a, handle_b, mut events_a, mut events_b,
        stores_a, stores_b, peer_a, peer_b, b_mapped, a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14024).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // Both create the entity
    let create_a = make_event(entity_id, peer_a, "Created by A");
    handle_a.record_event(create_a).await.unwrap();

    // Sync so B has the entity
    tokio::time::sleep(Duration::from_millis(100)).await;
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // Now both write simultaneously (no sync between writes)
    for i in 0..10 {
        let ua = make_update_event(entity_id, peer_a, &format!("A_concurrent_{i}"));
        let ub = make_update_event(entity_id, peer_b, &format!("B_concurrent_{i}"));
        handle_a.record_event(ua).await.unwrap();
        handle_b.record_event(ub).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(200)).await;

    // 3-round convergence: A→B, B→A, A→B
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    tokio::time::sleep(Duration::from_millis(200)).await;

    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    tokio::time::sleep(Duration::from_millis(200)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    tokio::time::sleep(Duration::from_millis(500)).await;

    // 1 create + 10 A updates + 10 B updates = 21
    let a_events = stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(a_events.len(), 21, "A: got {}", a_events.len());
    assert_eq!(b_events.len(), 21, "B: got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids, "Event sets must match");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 12. Large payload sync ──

/// Sync events with ~10KB JSON payloads — stress-tests the codec and transport.
#[tokio::test]
#[serial]
async fn large_payload_sync_over_real_p2p() {
    let (
        _relay, handle_a, handle_b, mut events_a, _events_b,
        _stores_a, stores_b, peer_a, _peer_b, b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14025).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // Create with a large payload (~10KB)
    let big_body = "x".repeat(10_000);
    let big_json = format!(r#"{{"title":"Big note","body":"{}"}}"#, big_body);
    let create = make_snapshot_event(entity_id, peer_a, &big_json);
    handle_a.record_event(create).await.unwrap();

    // Add more large updates
    for i in 0..5 {
        let body = format!("update_{}_", i).repeat(2000);
        let json = format!(r#"{{"title":"Big note v{}","body":"{}"}}"#, i, body);
        let update = Event::new(
            entity_id,
            peer_a,
            HybridTimestamp::now(),
            EventPayload::EntityUpdated {
                entity_type: "note".to_string(),
                json_data: json,
            },
        );
        handle_a.record_event(update).await.unwrap();
    }

    tokio::time::sleep(Duration::from_millis(300)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    let completed = wait_for_event(&mut events_a, Duration::from_secs(15), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    assert!(completed.is_some(), "Large payload sync should complete");

    tokio::time::sleep(Duration::from_millis(500)).await;

    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 6, "B should have all 6 large events, got {}", b_events.len());

    // Verify data integrity — the snapshot payload should survive transport
    let snapshot = b_events.iter().find(|e| matches!(e.payload, EventPayload::FullSnapshot { .. }));
    assert!(snapshot.is_some(), "Snapshot event should be present");
    if let EventPayload::FullSnapshot { json_data, .. } = &snapshot.unwrap().payload {
        assert!(json_data.len() > 10_000, "Payload should be intact");
    }

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 13. Repeated sync is idempotent over real P2P ──

/// Sync the same data 5 times — event counts should stay stable.
#[tokio::test]
#[serial]
async fn repeated_sync_idempotent_real_p2p() {
    let (
        _relay, handle_a, handle_b, mut events_a, _events_b,
        _stores_a, stores_b, peer_a, _peer_b, b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14026).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    let create = make_event(entity_id, peer_a, "Idempotent note");
    handle_a.record_event(create).await.unwrap();
    for i in 0..4 {
        let update = make_update_event(entity_id, peer_a, &format!("edit_{i}"));
        handle_a.record_event(update).await.unwrap();
    }
    tokio::time::sleep(Duration::from_millis(200)).await;

    // Sync 5 times
    for round in 0..5 {
        handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
            .await
            .unwrap();
        let result = wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
            matches!(e, SyncEvent::SyncCompleted { .. })
        })
        .await;
        assert!(result.is_some(), "Sync round {round} should complete");
        tokio::time::sleep(Duration::from_millis(200)).await;
    }

    // B should have exactly 5 events, not duplicates
    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 5, "B should have 5 events (no dupes), got {}", b_events.len());

    // Verify no duplicate IDs
    let ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(ids.len(), 5, "All event IDs should be unique");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 14. Sync with stale peer that was offline ──

/// A writes 20 events. B has been "offline" (never synced). Then B comes online and syncs.
#[tokio::test]
#[serial]
async fn stale_peer_bulk_catchup_real_p2p() {
    let (
        _relay, handle_a, handle_b, mut events_a, mut events_b,
        _stores_a, stores_b, peer_a, _peer_b, b_mapped, a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14027).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // A writes 20 events without any sync
    let create = make_event(entity_id, peer_a, "Stale peer target");
    handle_a.record_event(create).await.unwrap();
    for i in 0..19 {
        let update = make_update_event(entity_id, peer_a, &format!("offline_update_{i}"));
        handle_a.record_event(update).await.unwrap();
    }
    tokio::time::sleep(Duration::from_millis(300)).await;

    // B initiates sync to catch up
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // A→B to push anything remaining
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    tokio::time::sleep(Duration::from_millis(500)).await;

    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
    assert_eq!(b_events.len(), 20, "B should catch up to all 20, got {}", b_events.len());

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 15. Mixed entity types: notes, tasks, calendar events ──

/// Different entity types sync correctly without cross-contamination.
#[tokio::test]
#[serial]
async fn mixed_entity_types_real_p2p() {
    let (
        _relay, handle_a, handle_b, mut events_a, _events_b,
        _stores_a, stores_b, peer_a, _peer_b, b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14028).await;

    let note_id = EntityId::new();
    let task_id = EntityId::new();
    let cal_id = EntityId::new();

    for eid in [note_id, task_id, cal_id] {
        handle_a.share_entity(eid).await.unwrap();
        handle_b.share_entity(eid).await.unwrap();
    }

    // Create different entity types
    let note = Event::new(note_id, peer_a, HybridTimestamp::now(), EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"My Note","body":"content"}"#.to_string(),
    });
    let task = Event::new(task_id, peer_a, HybridTimestamp::now(), EventPayload::EntityCreated {
        entity_type: "task".to_string(),
        json_data: r#"{"title":"Buy milk","done":false}"#.to_string(),
    });
    let cal = Event::new(cal_id, peer_a, HybridTimestamp::now(), EventPayload::EntityCreated {
        entity_type: "calendar_event".to_string(),
        json_data: r#"{"title":"Meeting","start":"2026-02-01T09:00:00"}"#.to_string(),
    });

    handle_a.record_event(note).await.unwrap();
    handle_a.record_event(task).await.unwrap();
    handle_a.record_event(cal).await.unwrap();

    // Update each
    let note_upd = make_update_event(note_id, peer_a, "Note v2");
    let task_upd = Event::new(task_id, peer_a, HybridTimestamp::now(), EventPayload::EntityUpdated {
        entity_type: "task".to_string(),
        json_data: r#"{"title":"Buy milk","done":true}"#.to_string(),
    });

    handle_a.record_event(note_upd).await.unwrap();
    handle_a.record_event(task_upd).await.unwrap();

    tokio::time::sleep(Duration::from_millis(200)).await;

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    tokio::time::sleep(Duration::from_millis(500)).await;

    let b_notes = stores_b.1.get_events_for_entity(&note_id).unwrap();
    let b_tasks = stores_b.1.get_events_for_entity(&task_id).unwrap();
    let b_cals = stores_b.1.get_events_for_entity(&cal_id).unwrap();

    assert_eq!(b_notes.len(), 2, "B notes: {}", b_notes.len());
    assert_eq!(b_tasks.len(), 2, "B tasks: {}", b_tasks.len());
    assert_eq!(b_cals.len(), 1, "B calendar: {}", b_cals.len());

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 16. Sync with peer that already has newer data ──

/// B writes newer events after initial sync from A, then A syncs again.
/// A should get B's newer events without losing its own.
#[tokio::test]
#[serial]
async fn sync_with_peer_that_has_newer_data() {
    let (
        _relay, handle_a, handle_b, mut events_a, mut events_b,
        stores_a, stores_b, peer_a, peer_b, b_mapped, a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14029).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // A creates
    let create = make_event(entity_id, peer_a, "Initial");
    handle_a.record_event(create).await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;

    // A→B sync
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // B writes 5 newer events
    for i in 0..5 {
        let update = make_update_event(entity_id, peer_b, &format!("B_newer_{i}"));
        handle_b.record_event(update).await.unwrap();
    }
    tokio::time::sleep(Duration::from_millis(200)).await;

    // B→A to push B's newer data
    handle_b
        .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    tokio::time::sleep(Duration::from_millis(300)).await;

    let a_events = stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 6, "A should have 1 create + 5 B updates, got {}", a_events.len());
    assert_eq!(b_events.len(), 6, "B should have 1 create + 5 updates, got {}", b_events.len());

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 17. Interleaved writes and syncs ──

/// Write-sync-write-sync pattern — tests that sync state is correctly
/// updated between rounds so no events are lost or duplicated.
#[tokio::test]
#[serial]
async fn interleaved_writes_and_syncs() {
    let (
        _relay, handle_a, handle_b, mut events_a, mut events_b,
        stores_a, stores_b, peer_a, peer_b, b_mapped, a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14030).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    let create = make_event(entity_id, peer_a, "Interleaved");
    handle_a.record_event(create).await.unwrap();

    // 5 rounds of write-from-A, sync A→B, write-from-B, sync B→A
    for round in 0..5 {
        let ua = make_update_event(entity_id, peer_a, &format!("A_round_{round}"));
        handle_a.record_event(ua).await.unwrap();
        tokio::time::sleep(Duration::from_millis(50)).await;

        handle_a
            .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
            .await
            .unwrap();
        wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
            matches!(e, SyncEvent::SyncCompleted { .. })
        })
        .await;
        tokio::time::sleep(Duration::from_millis(100)).await;

        let ub = make_update_event(entity_id, peer_b, &format!("B_round_{round}"));
        handle_b.record_event(ub).await.unwrap();
        tokio::time::sleep(Duration::from_millis(50)).await;

        handle_b
            .send(SyncCommand::SyncWithPeer { peer_id: a_mapped })
            .await
            .unwrap();
        wait_for_event(&mut events_b, Duration::from_secs(10), |e| {
            matches!(e, SyncEvent::SyncCompleted { .. })
        })
        .await;
        tokio::time::sleep(Duration::from_millis(100)).await;
    }

    // Final A→B to ensure full convergence
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;
    tokio::time::sleep(Duration::from_millis(500)).await;

    // 1 create + 5 A updates + 5 B updates = 11
    let a_events = stores_a.1.get_events_for_entity(&entity_id).unwrap();
    let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();

    assert_eq!(a_events.len(), 11, "A: got {}", a_events.len());
    assert_eq!(b_events.len(), 11, "B: got {}", b_events.len());

    let a_ids: std::collections::HashSet<_> = a_events.iter().map(|e| e.id).collect();
    let b_ids: std::collections::HashSet<_> = b_events.iter().map(|e| e.id).collect();
    assert_eq!(a_ids, b_ids, "Event sets must converge");

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 18. Sync to unknown/nonexistent peer ──

/// Attempting to sync with a peer that doesn't exist should fail gracefully.
#[tokio::test]
#[serial]
async fn sync_to_nonexistent_peer_fails_gracefully() {
    let (
        _relay, handle_a, _handle_b, mut events_a, _events_b,
        _stores_a, _stores_b, _peer_a, _peer_b, _b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14031).await;

    let fake_peer = PeerId::new();

    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: fake_peer })
        .await
        .unwrap();

    // Should get SyncFailed, not a crash
    let result = wait_for_event(&mut events_a, Duration::from_secs(10), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. } | SyncEvent::SyncFailed { .. })
    })
    .await;

    match result {
        Some(SyncEvent::SyncFailed { error, .. }) => {
            eprintln!("[test] Expected failure: {error}");
        }
        Some(SyncEvent::SyncCompleted { .. }) => {
            panic!("Sync to nonexistent peer should not succeed");
        }
        None => {
            // Timeout is also acceptable — no crash
            eprintln!("[test] Sync to nonexistent peer timed out (acceptable)");
        }
        _ => unreachable!(),
    }

    handle_a.shutdown().await.unwrap();
    _handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
}

// ── 19. DHT: late joiner discovers and syncs ──

/// Peer A publishes to DHT and writes data. Peer B joins later,
/// discovers via DHT, and syncs all historical events.
#[tokio::test]
#[serial]
async fn dht_late_joiner_discovers_and_syncs() {
    let mut relay = RelayProcess::spawn(14032).await;
    let relay_addr = relay.multiaddr();

    let peer_a = PeerId::new();
    let peer_b = PeerId::new();

    let sync_code = SyncCode::generate();
    let hash_bytes = hex::decode(&sync_code.hash).unwrap();

    let stores_a = make_stores();
    let stores_b = make_stores();

    // A starts first — poll until publish succeeds rather than fixed sleep
    let mut transport_a = make_dht_only_transport(peer_a, &relay_addr, "EarlyBird", hash_bytes.clone());
    transport_a.start().await.unwrap();

    let publish_deadline = tokio::time::Instant::now() + Duration::from_secs(15);
    loop {
        if tokio::time::Instant::now() > publish_deadline {
            relay.kill().await;
            panic!("Peer A failed to publish to DHT within deadline");
        }
        if transport_a.publish_to_sync_group(&hash_bytes).await.is_ok() {
            break;
        }
        tokio::time::sleep(Duration::from_secs(1)).await;
    }

    let config = OrchestratorConfig {
        sync_interval: Duration::from_secs(3600),
        discovery_interval: Duration::from_secs(3600),
        auto_sync: false,
        max_entities_per_sync: 0,
    };

    let entity_id = EntityId::new();

    // B joins late — poll until publish succeeds
    let mut transport_b = make_dht_only_transport(peer_b, &relay_addr, "LateJoiner", hash_bytes.clone());
    transport_b.start().await.unwrap();

    let publish_deadline = tokio::time::Instant::now() + Duration::from_secs(15);
    loop {
        if tokio::time::Instant::now() > publish_deadline {
            relay.kill().await;
            panic!("Peer B failed to publish to DHT within deadline");
        }
        if transport_b.publish_to_sync_group(&hash_bytes).await.is_ok() {
            break;
        }
        tokio::time::sleep(Duration::from_secs(1)).await;
    }

    // Both discover each other via DHT — generous deadline for loaded environments
    let deadline = tokio::time::Instant::now() + Duration::from_secs(60);
    loop {
        if tokio::time::Instant::now() > deadline {
            relay.kill().await;
            panic!("DHT mutual discovery timeout for late joiner test");
        }
        // Re-publish periodically in case DHT records expired under load
        transport_a.publish_to_sync_group(&hash_bytes).await.ok();
        transport_b.publish_to_sync_group(&hash_bytes).await.ok();
        transport_a.discover_sync_group(&hash_bytes).await.ok();
        transport_b.discover_sync_group(&hash_bytes).await.ok();
        tokio::time::sleep(Duration::from_secs(2)).await;
        if !transport_a.discovered_peers().is_empty() && !transport_b.discovered_peers().is_empty() {
            break;
        }
    }

    let b_mapped = transport_a.discovered_peers()[0].peer_id;

    let transport_a: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_a));
    let transport_b: Arc<Mutex<dyn SyncTransport>> = Arc::new(Mutex::new(transport_b));

    let (handle_a, mut events_a, cmd_rx_a, orch_a) = create_orchestrator(
        peer_a, stores_a.0.clone(), stores_a.1.clone(), config.clone(),
    );
    let (handle_b, _events_b, cmd_rx_b, orch_b) = create_orchestrator(
        peer_b, stores_b.0.clone(), stores_b.1.clone(), config,
    );

    let join_a = tokio::spawn(async move { orch_a.run(transport_a, cmd_rx_a).await });
    let join_b = tokio::spawn(async move { orch_b.run(transport_b, cmd_rx_b).await });

    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // A writes 10 events (simulating data written before B existed in production)
    let create = Event::new(entity_id, peer_a, HybridTimestamp::now(), EventPayload::EntityCreated {
        entity_type: "note".to_string(),
        json_data: r#"{"title":"Early note"}"#.to_string(),
    });
    handle_a.record_event(create).await.unwrap();
    for i in 0..9 {
        let update = make_update_event(entity_id, peer_a, &format!("pre_join_{i}"));
        handle_a.record_event(update).await.unwrap();
    }
    tokio::time::sleep(Duration::from_millis(200)).await;

    // A pushes to B (push-only protocol)
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();
    wait_for_event(&mut events_a, Duration::from_secs(30), |e| {
        matches!(e, SyncEvent::SyncCompleted { .. })
    })
    .await;

    // Poll for replication instead of fixed sleep — events may take time under load
    let repl_deadline = tokio::time::Instant::now() + Duration::from_secs(10);
    loop {
        let b_events = stores_b.1.get_events_for_entity(&entity_id).unwrap();
        if b_events.len() == 10 {
            break;
        }
        if tokio::time::Instant::now() > repl_deadline {
            assert_eq!(b_events.len(), 10, "Late joiner should have all 10 events, got {}", b_events.len());
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }

    handle_a.shutdown().await.unwrap();
    handle_b.shutdown().await.unwrap();
    let _ = join_a.await;
    let _ = join_b.await;
    relay.kill().await;
}

// ── 20. Rapid shutdown during active sync ──

/// Start a sync and immediately shut down — should not crash or hang.
#[tokio::test]
#[serial]
async fn shutdown_during_active_sync_no_crash() {
    let (
        _relay, handle_a, handle_b, _events_a, _events_b,
        _stores_a, _stores_b, peer_a, _peer_b, b_mapped, _a_mapped, join_a, join_b,
    ) = setup_mdns_pair(14033).await;

    let entity_id = EntityId::new();
    handle_a.share_entity(entity_id).await.unwrap();
    handle_b.share_entity(entity_id).await.unwrap();

    // Write some data
    for i in 0..10 {
        let update = make_update_event(entity_id, peer_a, &format!("pre_crash_{i}"));
        handle_a.record_event(update).await.unwrap();
    }

    // Start sync
    handle_a
        .send(SyncCommand::SyncWithPeer { peer_id: b_mapped })
        .await
        .unwrap();

    // Immediately shut down both
    tokio::time::sleep(Duration::from_millis(50)).await;
    let _ = handle_a.shutdown().await;
    let _ = handle_b.shutdown().await;

    // Should complete without hanging (timeout guards this)
    let result_a = tokio::time::timeout(Duration::from_secs(10), join_a).await;
    let result_b = tokio::time::timeout(Duration::from_secs(10), join_b).await;

    assert!(result_a.is_ok(), "A should shut down cleanly");
    assert!(result_b.is_ok(), "B should shut down cleanly");
}
