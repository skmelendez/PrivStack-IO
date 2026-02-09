# Sync Engine

The sync engine replicates entities between devices using event-based synchronization over pluggable transports.

## Protocol Overview

Sync is a request-response protocol built on top of vector clocks. Two peers exchange their state, determine what the other is missing, and transfer events in batches.

### Message Types

| Message | Direction | Purpose |
|---|---|---|
| `Hello` | Initiator -> Responder | Handshake with protocol version and entity list |
| `HelloAck` | Responder -> Initiator | Accept handshake, share own entity list |
| `SyncRequest` | Either | Request vector clocks for a set of entities |
| `SyncState` | Either | Respond with vector clocks per entity |
| `EventBatch` | Either | Send up to 100 events, with `is_final` flag |
| `EventAck` | Either | Acknowledge receipt, optionally send events back |
| `Subscribe` | Either | Request real-time push for specific entities |
| `EventNotify` | Either | Push a new event to a subscriber |
| `Ping` / `Pong` | Either | Keepalive |
| `Error` | Either | Error with code and message |

### Protocol Flow

```
Initiator                          Responder
    |                                  |
    |  Hello (version, entity_ids)     |
    |--------------------------------->|
    |                                  |
    |  HelloAck (version, entity_ids)  |
    |<---------------------------------|
    |                                  |
    |  SyncRequest (entity clocks)     |
    |--------------------------------->|
    |                                  |
    |  SyncState (entity clocks)       |
    |<---------------------------------|
    |                                  |
    |  [compare clocks, find missing]  |
    |                                  |
    |  EventBatch (events, is_final)   |
    |<-------------------------------->|  (bidirectional)
    |                                  |
    |  EventAck (+ return events)      |
    |<-------------------------------->|
    |                                  |
    |  Subscribe (entity_ids)          |
    |<-------------------------------->|
    |                                  |
    |  EventNotify (new events)        |
    |<-------------------------------->|  (ongoing)
```

## Event Application

When events arrive from a remote peer, the `EventApplicator` processes each one:

### EntityCreated
Insert the new entity into the store. If an entity with the same ID already exists (rare race condition), fall through to the update logic.

### EntityUpdated
Load the local version and merge according to the entity's schema:

- **LwwDocument** — if `remote.modified_at >= local.modified_at`, replace local with remote entirely
- **LwwPerField** — if remote is newer overall, merge field-by-field, keeping the newer version of each top-level field
- **Custom** — call the plugin's `PluginDomainHandler::merge()` with both versions

### EntityDeleted
Remove the entity from the store.

### FullSnapshot
Treated as an `EntityUpdated` — the snapshot is merged with the local state using the same strategy. This handles the case where a peer sends its complete view of an entity.

## Transports

The sync engine defines a `SyncTransport` trait:

```rust
pub trait SyncTransport: Send + Sync {
    async fn send_request(&self, peer: PeerId, msg: SyncMessage) -> Result<SyncMessage>;
    async fn incoming_requests(&self) -> impl Stream<Item = (PeerId, SyncMessage)>;
    async fn discover_peers(&self, filter: PeerFilter) -> Result<Vec<PeerInfo>>;
}
```

### P2P Transport (libp2p)

The primary transport uses libp2p with the following stack:

| Layer | Protocol |
|---|---|
| Transport | QUIC v1 (UDP) |
| Encryption | Noise |
| Multiplexing | Yamux |
| Discovery (LAN) | mDNS |
| Discovery (WAN) | Kademlia DHT |
| Messaging | request-response |
| Identity | identify |

The P2P transport composes these into a single `SyncBehaviour`:

```rust
struct SyncBehaviour {
    request_response: request_response::Behaviour,
    mdns: mdns::Behaviour,
    kademlia: kad::Behaviour<MemoryStore>,
    identify: identify::Behaviour,
}
```

Messages are serialized as JSON via `SyncCodec`.

### Cloud Transport

Cloud transports use the user's own cloud storage as a dumb file transport:

- **Google Drive** — OAuth-authenticated file operations
- **iCloud** — iCloud Drive file operations

Both implement a `CloudStorage` trait (`list_files`, `get_file`, `put_file`, `delete_file`). Encrypted event blobs are written and read; the sync protocol is the same, just serialized to files instead of network messages.

## Device Discovery and Pairing

### Sync Codes

Devices discover each other using a **4-word sync code** (e.g., `PEAR-MANGO-KIWI-GRAPE`). The SHA-256 hash of the code is used as a Kademlia DHT namespace, so only devices sharing the same code can find each other.

On the local network, mDNS is always active and doesn't require a sync code.

### Pairing Flow

Discovery alone doesn't grant sync access. Devices must go through a two-way approval:

```
Device A discovers Device B
  → A's status for B: PendingLocalApproval
  → User on A approves B

A sends approval to B
  → B's status for A: PendingLocalApproval
  → User on B approves A

Both devices now have status: Trusted
  → Sync begins automatically
```

| Status | Meaning |
|---|---|
| `PendingLocalApproval` | Discovered peer, waiting for local user to approve |
| `PendingRemoteApproval` | Local user approved, waiting for remote to approve back |
| `Trusted` | Both sides approved, sync active |
| `Rejected` | User explicitly rejected this peer |

The `PairingManager` tracks these relationships and enforces that no data is sent until both sides reach `Trusted`.

## Orchestrator

The `SyncOrchestrator` manages the overall sync lifecycle:

- Coordinates multiple transports (P2P + cloud) simultaneously
- Routes commands from the UI (start sync, stop sync, pair device, query status)
- Emits events for the UI (connection established, events synced, sync error)
- Supports different sync policies:
  - **Personal** — single-device trust model
  - **Enterprise** — multi-device with role-based access

### Commands and Events

```rust
pub enum SyncCommand {
    Start,
    Stop,
    QueryStatus,
    PairDevice(SyncCode),
    // ...
}

pub enum SyncEvent {
    SyncStarted { peer_id, device_name },
    EventsSynced { entity_id, events_sent, events_received },
    SyncError { error },
    // ...
}
```

## State Tracking

Per-entity sync state includes:
- Vector clock (what has been seen from each peer)
- Event count
- Set of known event IDs

Per-peer sync status includes:
- Remote vector clock
- Progress indicators (events sent/received)
- Connection state
