# PrivStack

A privacy-first, offline-capable personal information manager with encrypted local storage, peer-to-peer sync, and an extensible plugin architecture.

PrivStack keeps your data on your devices. There are no accounts, no cloud services, and no telemetry. Data is encrypted at rest with a master password, synced between your devices over direct peer-to-peer connections, and extended through a plugin system that lets third-party authors add new entity types and UI.

## Project Structure

```
core/           Rust workspace — encryption, storage, CRDTs, sync engine, plugin host, FFI
desktop/        Avalonia (.NET 9) desktop shell — window management, theming, plugin UI rendering
relay/          Standalone Rust relay & DHT bootstrap node for NAT traversal
```

## Core (Rust)

The core is a Cargo workspace (edition 2024, Rust 1.85+) split into 15 crates. It owns all data logic and exposes a C FFI consumed by the desktop shell and future mobile clients.

### Encryption

Two-tier key architecture:

- **Master key** — derived from the user's password via Argon2id (OWASP 2023 parameters: 19 MiB memory, 2 iterations). Never stored; re-derived on every unlock.
- **Per-entity keys** — random 256-bit keys, each encrypted with the master key. This allows password changes without re-encrypting every entity, and makes it possible to share individual items by sharing their wrapped key.

All data at rest is encrypted with ChaCha20-Poly1305 (AEAD). Nonces are 96-bit random per encryption. Key material is zeroized on drop.

### Storage

DuckDB is the storage backend, chosen for its embedded OLAP capabilities (useful for analytics and future AI features). Three stores:

| Store | Purpose |
|---|---|
| **Entity store** | Entities with JSON payloads, schema-driven indexed columns, full-text search |
| **Event store** | Append-only log of all mutation events for sync replay |
| **Blob store** | Namespace-scoped binary objects with content-hash dedup |

Indexed fields are extracted from JSON via pointers declared in each entity schema, enabling queries without decrypting the full payload.

### Entity Model

Entities are schema-less JSON documents with a declared type. Plugins register schemas that define:

- Indexed fields and their types (text, number, tag, datetime, bool, relation, vector embedding, geo point, counter, etc.)
- A merge strategy for conflict resolution (last-writer-wins per document, last-writer-wins per field, or custom)
- Optional domain handlers for validation, post-load enrichment, and custom merge logic

### CRDTs

The CRDT crate provides five conflict-free replicated data types used throughout the sync engine:

| Type | Use |
|---|---|
| **Vector Clock** | Causal ordering of events across peers |
| **LWW Register** | Single-value properties (titles, settings) — highest timestamp wins, peer ID breaks ties |
| **PN Counter** | Distributed increment/decrement (budgets, counters) |
| **OR-Set** | Add-wins set membership (tags, collections) |
| **RGA** | Ordered sequences (text content, task lists) |

All types satisfy commutativity, associativity, and idempotency, guaranteeing convergence regardless of message order.

### Sync Engine

Sync is event-based and transport-agnostic. The engine supports two transports:

**P2P (libp2p)** — QUIC connections with Noise encryption. Discovery via mDNS on the local network and Kademlia DHT for wide-area. Devices pair using a 4-word sync code whose SHA-256 hash scopes the DHT namespace. Two-way approval is required before any data flows.

**Cloud storage** — Google Drive and iCloud as dumb file transports (encrypted blobs written to the user's own cloud storage).

The sync protocol exchanges vector clocks per entity, then transfers missing events in batches of up to 100. Events are applied using the entity's declared merge strategy. A subscription model provides real-time push for already-connected peers.

### Plugin Host

Plugins are WebAssembly components loaded via Wasmtime 33. The host enforces:

- Entity-type scoping — a plugin can only read and write its own declared entity types
- Memory limits (64 MB first-party, 32 MB third-party)
- CPU fuel budgets (~1B instructions first-party, 500M third-party)
- Call timeouts (5s / 3s)
- Permission-gated host imports (e.g., HTTP access requires explicit grant)

A policy engine loads allowlist/blocklist configuration and checks permissions before every host function call.

> **Planned:** A future version will introduce a custom template engine for plugin UI declarations, replacing the current JSON component tree approach. This will provide stronger sandboxing guarantees for third-party plugin UI rendering.

### FFI

The `privstack-ffi` crate exports a C ABI via a handle-based API. A single `PrivStackHandle` owns the runtime (Tokio), stores, vault, sync engine, and plugin host. Core operations are exposed as `extern "C"` functions; domain-specific calls route through a generic `privstack_execute(json) -> json` endpoint.

Consumers:
- .NET (P/Invoke) — desktop shell
- Swift (C interop) — iOS (planned)
- JNI — Android (planned)

### Vault

A generic encrypted blob store layered on top of the main DuckDB database. Each vault has its own password and salt. Multiple vaults can coexist (e.g., personal, work). Unlock verifies the password by decrypting a stored verification token. The derived key is held in memory while unlocked and zeroized on lock.

## Desktop Shell (Avalonia)

The desktop app is an Avalonia 11 / .NET 9 application that acts as a thin shell around the Rust core. It handles window management, navigation, theming, and plugin UI rendering — all data logic lives in Rust.

### Architecture

- **MVVM** with CommunityToolkit.Mvvm and Microsoft.Extensions.DependencyInjection
- **Plugin registry** discovers, initializes, and manages the lifecycle of plugins (discovered → initializing → initialized → active → deactivated)
- **Capability broker** enables cross-plugin communication — plugins declare capabilities (timers, reminders, linkable items, deep links, search) and other plugins discover providers at runtime
- **SDK host** routes all data operations from plugins through FFI to Rust, with a reader-writer lock to prevent calls during workspace switches

### Plugin UI Rendering

Native (.NET) plugins use standard Avalonia XAML views. Wasm plugins return a JSON component tree that the **adaptive view renderer** translates into live Avalonia controls.

Supported components include layout containers (stack, grid, split pane, scroll), data display (text, badge, icon, image, list), input controls (button, text input, toggle, dropdown), and rich components (block editor, HTML content, graph view).

A built-in template engine evaluates `$for` loops, `$if` conditionals, and `{{ expression | filter }}` interpolation within the JSON tree before rendering.

### Theming and Responsive Layout

Seven themes ship by default (Dark, Light, Sage, Lavender, Azure, Slate, Ember). All visual properties use dynamic resources, so theme switches propagate instantly.

The responsive layout service adapts to three breakpoints based on content area width:

| Mode | Width | Behavior |
|---|---|---|
| Compact | < 700px | Sidebar collapsed, info panel hidden |
| Normal | 700–1100px | Balanced layout |
| Wide | > 1100px | Full sidebar, content, and info panel |

Font scaling is a separate accessibility multiplier applied on top of theme sizes.

### Shell Features

- **Command palette** (Cmd/Ctrl+K) — searches across plugin commands, navigation items, deep links, and entity content
- **Info panel** — right-side collapsible panel showing backlinks, local knowledge graph, and entity metadata
- **Setup wizard** — first-run flow for data directory, master password, and theme selection
- **Workspace switching** — multiple data directories with full plugin re-initialization on switch
- **Backup service** — scheduled automatic backups with configurable frequency and retention
- **Sensitive lock** — secondary timeout-based lock for high-value features (passwords, vault access)
- **Auto-update** — checks for and installs updates

### Platforms

| Platform | Transport | Native Library |
|---|---|---|
| macOS (arm64, x64) | libprivstack_ffi.dylib | QUIC P2P |
| Windows (x64) | privstack_ffi.dll | QUIC P2P + WebView |
| Linux (x64) | libprivstack_ffi.so | QUIC P2P |

WebView is currently disabled on macOS due to .NET 9 MacCatalyst compatibility issues.

## Relay

A lightweight, stateless Rust server that helps PrivStack clients find each other and connect through NATs.

- **Kademlia DHT** — clients publish their presence under a namespace derived from their sync code hash; the relay bootstraps the routing table
- **libp2p relay protocol** — forwards traffic between peers that cannot establish direct connections
- **HTTP identity API** (`GET /api/v1/identity`) — returns the relay's peer ID and addresses so clients don't need to hardcode them

The relay listens on UDP 4001 (QUIC) and TCP 4002 (HTTP). It stores no user data. Deployment is a single binary managed by systemd.

See [Relay README](relay/README.md) for deployment instructions.

## Building

### Core

```bash
cd core
cargo build --release
```

### Desktop

```bash
cd desktop/PrivStack.Desktop
dotnet build
```

### Relay

```bash
cd relay
cargo build --release
```

## Documentation

Detailed architecture documentation is available in the [wiki](wiki/):

- [Architecture Overview](wiki/architecture.md)
- [Entity Model](wiki/entity-model.md)
- [CRDTs](wiki/crdts.md)
- [Sync Engine](wiki/sync-engine.md)
- [Cryptography](wiki/cryptography.md)
- [Storage](wiki/storage.md)
- [FFI Layer](wiki/ffi.md)
- [Desktop SDKs](wiki/sdks.md)
- [Relay Server](wiki/relay.md)

## License

[PolyForm Internal Use License 1.0.0](LICENSE)
