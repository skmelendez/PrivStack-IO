# Architecture Overview

PrivStack is structured as three independent components: a Rust core library, an Avalonia desktop shell, and a relay server. The core owns all data logic and is consumed by the shell through a C FFI boundary.

## High-Level Diagram

```
+-----------------------------------------------------------+
|  Desktop Shell (Avalonia / .NET 9)                        |
|                                                           |
|  +-------------+  +----------------+  +----------------+  |
|  | Plugin       |  | Adaptive View  |  | Shell UI       |  |
|  | Registry     |  | Renderer       |  | (Navigation,   |  |
|  |              |  | (JSON -> XAML) |  |  Settings,     |  |
|  +---------+---+  +-------+--------+  |  Theming)      |  |
|            |              |            +-------+--------+  |
|            v              v                    |           |
|  +---------+--------------+--------------------+--------+  |
|  |              SDK Host (P/Invoke FFI bridge)          |  |
|  +---------------------------+--------------------------+  |
+------------------------------|-----------------------------+
                               | C ABI
+------------------------------|-----------------------------+
|  Core (Rust)                 v                             |
|                                                            |
|  +------------------+  +------------------+                |
|  | Plugin Host      |  | Sync Engine      |                |
|  | (Wasmtime Wasm)  |  | (P2P + Cloud)    |                |
|  +--------+---------+  +--------+---------+                |
|           |                     |                          |
|  +--------v---------+  +-------v----------+                |
|  | Entity Model     |  | CRDT Layer       |                |
|  | (Schemas, Merge) |  | (VClock, LWW,    |                |
|  +--------+---------+  |  ORSet, RGA,     |                |
|           |             |  PNCounter)      |                |
|  +--------v---------+  +-------+----------+                |
|  | Storage Layer    |          |                           |
|  | (DuckDB)         <----------+                           |
|  +--------+---------+                                      |
|           |                                                |
|  +--------v---------+                                      |
|  | Crypto Layer     |                                      |
|  | (Argon2id,       |                                      |
|  |  ChaCha20-Poly)  |                                      |
|  +------------------+                                      |
+------------------------------------------------------------+

        P2P sync via libp2p (QUIC + Noise)
                    |
                    v
+------------------------------------------------------------+
|  Relay Server (Rust)                                       |
|  Kademlia DHT bootstrap + NAT traversal relay              |
+------------------------------------------------------------+
```

## Crate Map

The Rust core workspace contains 15 crates organized by layer:

| Crate | Layer | Purpose |
|---|---|---|
| `privstack-types` | Foundation | Core IDs (EntityId, PeerId, EventId), HybridTimestamp, EventPayload |
| `privstack-model` | Data Model | Entity, EntitySchema, MergeStrategy, PluginDomainHandler |
| `privstack-crdt` | CRDT | VectorClock, LWWRegister, ORSet, PNCounter, RGA |
| `privstack-crypto` | Encryption | Argon2id KDF, ChaCha20-Poly1305 cipher, key management |
| `privstack-storage` | Storage | DuckDB entity store, event store |
| `privstack-blobstore` | Storage | Namespace-scoped encrypted blob storage |
| `privstack-vault` | Vault | Password-protected multi-vault encrypted storage |
| `privstack-sync` | Sync | P2P and cloud sync transports, protocol, applicator |
| `privstack-plugin-sdk` | Plugin | Guest-side Wasm SDK (WIT bindings, Plugin trait) |
| `privstack-plugin-host` | Plugin | Wasmtime host, policy engine, resource limiting |
| `privstack-ffi` | FFI | C ABI exports, handle-based API |
| `privstack-license` | License | License validation |
| `privstack-ppk` | Crypto | Additional key management |

## Data Flow

### Write Path

1. Plugin calls SDK (create/update/delete entity)
2. SDK host serializes request to JSON
3. FFI call crosses into Rust (`privstack_execute`)
4. Core validates via entity schema and optional domain handler
5. Entity payload encrypted with per-entity key
6. Written to DuckDB entity store (encrypted payload + plaintext indexed fields)
7. Mutation event appended to event store
8. Sync engine notifies connected peers via `EventNotify`

### Read Path

1. Plugin requests entity by ID or query
2. FFI call into Rust
3. Entity loaded from DuckDB
4. Payload decrypted with per-entity key (derived from master key)
5. Optional `on_after_load` enrichment via domain handler
6. JSON response returned through FFI

### Sync Path

1. Peer discovered via mDNS (LAN) or Kademlia DHT (WAN)
2. Handshake: exchange protocol version and entity lists
3. Vector clocks exchanged per entity
4. Missing events transferred in batches of up to 100
5. Each event applied locally using the entity's merge strategy
6. Subscription established for real-time push of future events

## Design Principles

- **Offline-first** — all operations complete locally without network access
- **Plugin-agnostic core** — no domain logic in the core; all domain behavior is in plugins
- **Encryption by default** — data encrypted at rest and in transit
- **Transport-agnostic sync** — the sync protocol doesn't care whether events arrive via P2P, cloud, or a future transport
- **CRDT convergence** — concurrent edits from disconnected devices are guaranteed to converge without manual conflict resolution
